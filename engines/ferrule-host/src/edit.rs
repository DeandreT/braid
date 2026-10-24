//! Authoritative bounded replay. Native bytes come from the server's immutable base,
//! never from an edit request made by a browser.

use std::io::{self, Read, Write};

use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use weft_ferrule_core::edit::{self, EditFailure, Session};

use crate::{
    framing,
    projection::ProjectionPayload,
    protocol::{ENGINE_REVISION, MAX_NATIVE_BYTES, Revision, ValidationResult},
    valid_context_parts,
};

pub const PROTOCOL: &str = "weft.ferrule-edit.v1";
pub const MAX_REPLY_BYTES: usize = edit::MAX_REPORT_BYTES + MAX_NATIVE_BYTES + 8;

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Request {
    pub protocol_version: String,
    pub request_id: String,
    pub operation: String,
    pub revision: Revision,
    pub native_digest: String,
    pub document_id: String,
    pub operations_digest: String,
}

#[derive(Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Response {
    pub protocol_version: String,
    pub request: Option<Request>,
    pub engine_revision: String,
    pub result: Option<ResultHeader>,
    pub failure: Option<EditFailure>,
}

#[derive(Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ResultHeader {
    pub native_digest: String,
    pub native_byte_count: usize,
    pub operations_digest: String,
    pub operation_count: usize,
    pub validation: ValidationResult,
    pub projection: ProjectionPayload,
}

pub struct Outcome {
    pub response: Response,
    pub native: Option<Vec<u8>>,
}

impl Outcome {
    fn failure(request: Option<&Request>, failure: EditFailure) -> Self {
        Self {
            response: Response {
                protocol_version: PROTOCOL.into(),
                request: request.cloned(),
                engine_revision: ENGINE_REVISION.into(),
                result: None,
                failure: Some(failure),
            },
            native: None,
        }
    }
}

pub fn handle(header: &[u8], reader: &mut impl Read, writer: &mut impl Write) -> io::Result<()> {
    let request = serde_json::from_slice::<Request>(header).ok();
    let outcome = match read_inputs(reader) {
        Ok((native, operations)) => match &request {
            Some(request) => process(request, &native, &operations),
            None => Outcome::failure(
                None,
                EditFailure::new(
                    "invalidHeader",
                    "The edit header is not an admitted JSON object.",
                ),
            ),
        },
        Err(message) => {
            Outcome::failure(request.as_ref(), EditFailure::new("invalidFrame", message))
        }
    };
    write_outcome(writer, outcome)
}

fn read_inputs(reader: &mut impl Read) -> Result<(Vec<u8>, Vec<u8>), &'static str> {
    let native = framing::read_frame(reader, MAX_NATIVE_BYTES)?;
    let operations = framing::read_frame(reader, edit::MAX_OPERATIONS_BYTES)?;
    framing::require_eof(reader)?;
    Ok((native, operations))
}

pub fn process(request: &Request, native: &[u8], operations: &[u8]) -> Outcome {
    let failure = |code, message| Outcome::failure(Some(request), EditFailure::new(code, message));
    if request.protocol_version != PROTOCOL || request.operation != "edit" {
        return failure(
            "unsupportedOperation",
            "Only the versioned edit operation is admitted.",
        );
    }
    if !valid_context_parts(
        &request.request_id,
        &request.revision,
        &request.native_digest,
        &request.document_id,
    ) || request.operations_digest.len() != 64
        || !request
            .operations_digest
            .bytes()
            .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
    {
        return failure("invalidContext", "The edit context or digest is invalid.");
    }
    if format!("{:x}", Sha256::digest(native)) != request.native_digest
        || format!("{:x}", Sha256::digest(operations)) != request.operations_digest
    {
        return failure(
            "digestMismatch",
            "The base or operation bytes do not match their declared SHA-256 digest.",
        );
    }
    let checkpoint = match Session::open(native, operations)
        .and_then(|session| session.checkpoint(&request.document_id))
    {
        Ok(checkpoint) => checkpoint,
        Err(error) => return Outcome::failure(Some(request), error),
    };
    // The echo refers to the submitted log, including intent that was a no-op.
    // History may omit no-op commands; provenance must never omit submitted intent.
    let script = match edit::parse_script(operations) {
        Ok(script) => script,
        Err(error) => return Outcome::failure(Some(request), error),
    };
    Outcome {
        response: Response {
            protocol_version: PROTOCOL.into(),
            request: Some(request.clone()),
            engine_revision: ENGINE_REVISION.into(),
            failure: None,
            result: Some(ResultHeader {
                native_digest: checkpoint.native_digest,
                native_byte_count: checkpoint.native.len(),
                operations_digest: request.operations_digest.clone(),
                operation_count: script.operations.len(),
                validation: checkpoint.view.validation,
                projection: checkpoint.view.projection,
            }),
        },
        native: Some(checkpoint.native),
    }
}

fn write_outcome(writer: &mut impl Write, outcome: Outcome) -> io::Result<()> {
    let bytes = match framing::encode_bounded(&outcome.response, edit::MAX_REPORT_BYTES) {
        Ok(bytes) => bytes,
        Err(_) => {
            return write_outcome(
                writer,
                Outcome::failure(
                    outcome.response.request.as_ref(),
                    EditFailure::new(
                        "resultLimitExceeded",
                        "The checkpoint reports exceed 16 MiB.",
                    ),
                ),
            );
        }
    };
    writer.write_all(&(bytes.len() as u32).to_be_bytes())?;
    writer.write_all(&bytes)?;
    if let Some(native) = outcome.native {
        writer.write_all(&(native.len() as u32).to_be_bytes())?;
        writer.write_all(&native)?;
    }
    writer.flush()
}
