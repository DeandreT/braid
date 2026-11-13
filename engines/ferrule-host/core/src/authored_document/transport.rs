//! Portable authored-preview wire boundary. Context is a correlation echo, not
//! authorization. Proposal bytes are absent: only native/sample hashes can be verified.
use super::{Failure, Limits};
use serde::{Deserialize, Serialize};
use std::io::{self, Read, Write};

pub const PROTOCOL: &str = "weft.ferrule-authored-preview.v1";
pub const OPERATION: &str = "previewAuthored";
pub const MAX_HEADER_BYTES: usize = 16 * 1024;
pub const MAX_NATIVE_BYTES: usize = crate::protocol::MAX_NATIVE_BYTES;
pub const MAX_SAMPLE_BYTES: usize = 1024 * 1024;
pub const MAX_OUTPUT_BYTES: usize = 1024 * 1024;
pub const MAX_REQUEST_BYTES: usize = MAX_HEADER_BYTES + MAX_NATIVE_BYTES + MAX_SAMPLE_BYTES + 12;
pub const MAX_RESPONSE_BYTES: usize = MAX_HEADER_BYTES + MAX_OUTPUT_BYTES + 8;

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Request {
    pub protocol_version: String,
    pub request_id: String,
    pub operation: String,
    pub organization_id: String,
    pub project_id: String,
    pub authoring_id: String,
    pub proposal_digest: String,
    pub native_digest: String,
    pub sample_digest: String,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum FailureCode {
    InvalidFrame,
    InvalidHeader,
    UnsupportedOperation,
    InvalidContext,
    DigestMismatch,
    InvalidMapping,
    UnsupportedSemantics,
    InvalidSample,
    ExecutionFailed,
    ResourceLimit,
}

impl From<Failure> for FailureCode {
    fn from(failure: Failure) -> Self {
        match failure {
            Failure::InvalidNative | Failure::InvalidMapping => Self::InvalidMapping,
            Failure::UnsupportedBoundary
            | Failure::UnsupportedSchema
            | Failure::UnsupportedOperator
            | Failure::UnsupportedScope
            | Failure::NamedEndpoints
            | Failure::ExternalDependency => Self::UnsupportedSemantics,
            Failure::InvalidInput => Self::InvalidSample,
            Failure::ExecutionFailed | Failure::InvalidOutput => Self::ExecutionFailed,
            Failure::InvalidLimits
            | Failure::NativeLimit
            | Failure::InputLimit
            | Failure::ExecutionLimit
            | Failure::OutputLimit
            | Failure::DeadlineExceeded => Self::ResourceLimit,
        }
    }
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Response {
    protocol_version: &'static str,
    request: Option<Request>,
    engine_revision: &'static str,
    result: Option<ResultHeader>,
    failure: Option<FailureHeader>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct ResultHeader {
    output_digest: String,
    output_byte_count: usize,
    format: &'static str,
    media_type: &'static str,
}

#[derive(Serialize)]
struct FailureHeader {
    code: FailureCode,
}

struct Outcome {
    response: Response,
    output: Option<Vec<u8>>,
}

impl Outcome {
    fn failure(request: Option<Request>, code: FailureCode) -> Self {
        Self {
            response: Response {
                protocol_version: PROTOCOL,
                request,
                engine_revision: crate::protocol::ENGINE_REVISION,
                result: None,
                failure: Some(FailureHeader { code }),
            },
            output: None,
        }
    }
}

fn process(request: Request, native: &[u8], sample: &[u8]) -> Outcome {
    if request.protocol_version != PROTOCOL || request.operation != OPERATION {
        return Outcome::failure(Some(request), FailureCode::UnsupportedOperation);
    }
    if ![
        &request.request_id,
        &request.organization_id,
        &request.project_id,
        &request.authoring_id,
    ]
    .into_iter()
    .all(|id| crate::canonical_nonzero_guid(id))
        || ![
            &request.proposal_digest,
            &request.native_digest,
            &request.sample_digest,
        ]
        .into_iter()
        .all(|s| valid_digest(s))
    {
        return Outcome::failure(Some(request), FailureCode::InvalidContext);
    }
    if super::digest(native) != request.native_digest
        || super::digest(sample) != request.sample_digest
    {
        return Outcome::failure(Some(request), FailureCode::DigestMismatch);
    }
    match super::execute(
        native,
        sample,
        Limits {
            input_bytes: MAX_SAMPLE_BYTES,
            output_bytes: MAX_OUTPUT_BYTES,
            ..Default::default()
        },
    ) {
        Ok(result) => Outcome {
            response: Response {
                protocol_version: PROTOCOL,
                request: Some(request),
                engine_revision: crate::protocol::ENGINE_REVISION,
                failure: None,
                result: Some(ResultHeader {
                    output_digest: result.output_sha256,
                    output_byte_count: result.bytes.len(),
                    format: result.output_format.id(),
                    media_type: result.output_format.media_type(),
                }),
            },
            output: Some(result.bytes),
        },
        Err(error) => Outcome::failure(Some(request), error.into()),
    }
}

fn valid_digest(value: &str) -> bool {
    value.len() == 64
        && value
            .bytes()
            .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
}

/// Reads exactly three BE-u32 frames followed by EOF. A later process host owns
/// the deadline for blocked I/O. Allocation is checked before each frame read.
pub fn serve(reader: &mut impl Read, writer: &mut impl Write) -> io::Result<()> {
    let header = match read_frame(reader, MAX_HEADER_BYTES, false) {
        Ok(header) => header,
        Err(()) => return write_outcome(writer, Outcome::failure(None, FailureCode::InvalidFrame)),
    };
    handle(&header, reader, writer)
}

/// Entry for a future host that has already read the bounded header frame.
pub fn handle(header: &[u8], reader: &mut impl Read, writer: &mut impl Write) -> io::Result<()> {
    if header.is_empty() || header.len() > MAX_HEADER_BYTES {
        return write_outcome(writer, Outcome::failure(None, FailureCode::InvalidHeader));
    }
    let request = match serde_json::from_slice::<Request>(header) {
        Ok(request) => request,
        Err(_) => return write_outcome(writer, Outcome::failure(None, FailureCode::InvalidHeader)),
    };
    let payloads = (|| {
        let native = read_frame(reader, MAX_NATIVE_BYTES, false)?;
        let sample = read_frame(reader, MAX_SAMPLE_BYTES, true)?;
        if reader.read(&mut [0]).map_err(|_| ())? != 0 {
            return Err(());
        }
        Ok((native, sample))
    })();
    let outcome = match payloads {
        Ok((native, sample)) => process(request, &native, &sample),
        Err(()) => Outcome::failure(Some(request), FailureCode::InvalidFrame),
    };
    write_outcome(writer, outcome)
}

fn read_frame(reader: &mut impl Read, maximum: usize, empty: bool) -> Result<Vec<u8>, ()> {
    let mut length = [0; 4];
    reader.read_exact(&mut length).map_err(|_| ())?;
    let length = u32::from_be_bytes(length) as usize;
    if length > maximum || !empty && length == 0 {
        return Err(());
    }
    let mut bytes = vec![0; length];
    reader.read_exact(&mut bytes).map_err(|_| ())?;
    Ok(bytes)
}

fn write_outcome(writer: &mut impl Write, outcome: Outcome) -> io::Result<()> {
    let header = match crate::framing_limit::encode(&outcome.response, MAX_HEADER_BYTES) {
        Ok(header) => header,
        // An invalid but parseable request can consume the whole header budget.
        // Drop its echo instead of recursively trying to serialize it again.
        Err(_) => return write_outcome(writer, Outcome::failure(None, FailureCode::ResourceLimit)),
    };
    writer.write_all(&(header.len() as u32).to_be_bytes())?;
    writer.write_all(&header)?;
    if let Some(output) = outcome.output {
        writer.write_all(&(output.len() as u32).to_be_bytes())?;
        writer.write_all(&output)?;
    }
    writer.flush()
}

#[cfg(test)]
mod tests;
