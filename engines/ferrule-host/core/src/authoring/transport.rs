//! Portable authoring wire operation. The project/session identities are echoes,
//! not authorization, and deliberately do not invent a mapping or revision.
use std::io::{self, Read, Write};

use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};

use crate::{
    canonical_nonzero_guid, edit::EditFailure, framing_limit, projection::ProjectionPayload,
    protocol,
};

pub const PROTOCOL: &str = "weft.ferrule-authoring.v1";
pub const DOCUMENT_ID: &str = "entry.native.json";
pub const MAX_REQUEST_BYTES: usize = protocol::MAX_HEADER_BYTES + super::MAX_PLAN_BYTES + 8;
pub const MAX_RESPONSE_BYTES: usize =
    crate::edit::MAX_REPORT_BYTES + protocol::MAX_NATIVE_BYTES + 8;

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Request {
    pub protocol_version: String,
    pub request_id: String,
    pub operation: String,
    pub organization_id: String,
    pub project_id: String,
    pub authoring_id: String,
    /// Exact digest of the submitted plan bytes, including whitespace.
    pub plan_digest: String,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Response {
    pub protocol_version: &'static str,
    pub request: Option<Request>,
    pub engine_revision: &'static str,
    pub result: Option<ResultHeader>,
    pub failure: Option<EditFailure>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ResultHeader {
    pub native_digest: String,
    pub native_byte_count: usize,
    pub validation: protocol::ValidationResult,
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
                protocol_version: PROTOCOL,
                request: request.cloned(),
                engine_revision: protocol::ENGINE_REVISION,
                result: None,
                failure: Some(failure),
            },
            native: None,
        }
    }
}

pub fn process(request: &Request, plan: &[u8]) -> Outcome {
    let fail = |code, message| Outcome::failure(Some(request), EditFailure::new(code, message));
    if request.protocol_version != PROTOCOL || request.operation != "author" {
        return fail(
            "unsupportedOperation",
            "Only versioned native authoring is admitted.",
        );
    }
    if ![
        &request.request_id,
        &request.organization_id,
        &request.project_id,
        &request.authoring_id,
    ]
    .into_iter()
    .all(|id| canonical_nonzero_guid(id))
        || request.plan_digest.len() != 64
        || !request
            .plan_digest
            .bytes()
            .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
    {
        return fail(
            "invalidContext",
            "Invalid project/session identity or plan digest.",
        );
    }
    if plan.is_empty() || plan.len() > super::MAX_PLAN_BYTES {
        return fail(
            "authoringLimit",
            "Authoring plan exceeds its byte bound or is empty.",
        );
    }
    if format!("{:x}", Sha256::digest(plan)) != request.plan_digest {
        return fail(
            "digestMismatch",
            "Plan bytes do not match their declared digest.",
        );
    }
    let checkpoint = match super::compile(plan) {
        Ok(checkpoint) => checkpoint,
        Err(error) => return Outcome::failure(Some(request), error),
    };
    Outcome {
        response: Response {
            protocol_version: PROTOCOL,
            request: Some(request.clone()),
            engine_revision: protocol::ENGINE_REVISION,
            failure: None,
            result: Some(ResultHeader {
                native_digest: checkpoint.native_digest,
                native_byte_count: checkpoint.native.len(),
                validation: checkpoint.view.validation,
                projection: checkpoint.view.projection,
            }),
        },
        native: Some(checkpoint.native),
    }
}

/// Header was read under the host's ordinary header bound. Exactly one plan frame follows.
pub fn handle(header: &[u8], reader: &mut impl Read, writer: &mut impl Write) -> io::Result<()> {
    if header.len() > protocol::MAX_HEADER_BYTES {
        return write_outcome(
            writer,
            Outcome::failure(
                None,
                EditFailure::new("invalidHeader", "Header exceeds its byte bound."),
            ),
        );
    }
    let request = match serde_json::from_slice::<Request>(header) {
        Ok(request) => request,
        Err(_) => {
            return write_outcome(
                writer,
                Outcome::failure(
                    None,
                    EditFailure::new("invalidHeader", "Invalid authoring header."),
                ),
            );
        }
    };
    let outcome = match read_plan(reader) {
        Ok(plan) => process(&request, &plan),
        Err(()) => Outcome::failure(
            Some(&request),
            EditFailure::new("invalidFrame", "Expected one bounded plan frame and EOF."),
        ),
    };
    write_outcome(writer, outcome)
}

fn read_plan(reader: &mut impl Read) -> Result<Vec<u8>, ()> {
    let mut length = [0; 4];
    reader.read_exact(&mut length).map_err(|_| ())?;
    let length = u32::from_be_bytes(length) as usize;
    if length == 0 || length > super::MAX_PLAN_BYTES {
        return Err(());
    }
    let mut bytes = vec![0; length];
    reader.read_exact(&mut bytes).map_err(|_| ())?;
    if reader.read(&mut [0]).map_err(|_| ())? != 0 {
        return Err(());
    }
    Ok(bytes)
}

fn write_outcome(writer: &mut impl Write, outcome: Outcome) -> io::Result<()> {
    let header = match framing_limit::encode(&outcome.response, crate::edit::MAX_REPORT_BYTES) {
        Ok(header) => header,
        Err(_) => {
            return write_outcome(
                writer,
                Outcome::failure(
                    outcome.response.request.as_ref(),
                    EditFailure::new(
                        "resultLimitExceeded",
                        "Authoring reports exceed their byte bound.",
                    ),
                ),
            );
        }
    };
    writer.write_all(&(header.len() as u32).to_be_bytes())?;
    writer.write_all(&header)?;
    if let Some(native) = outcome.native {
        writer.write_all(&(native.len() as u32).to_be_bytes())?;
        writer.write_all(&native)?;
    }
    writer.flush()
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::Value;

    fn request(plan: &[u8]) -> Request {
        Request {
            protocol_version: PROTOCOL.into(),
            request_id: "00000000-0000-0000-0000-000000000001".into(),
            operation: "author".into(),
            organization_id: "00000000-0000-0000-0000-000000000002".into(),
            project_id: "00000000-0000-0000-0000-000000000003".into(),
            authoring_id: "00000000-0000-0000-0000-000000000004".into(),
            plan_digest: format!("{:x}", Sha256::digest(plan)),
        }
    }

    #[test]
    fn exact_project_request_and_two_frame_reply_without_fake_revision() {
        let plan = serde_json::to_vec(&super::super::tests::plan()).unwrap();
        let request = request(&plan);
        let header = serde_json::to_vec(&request).unwrap();
        let mut frame = (plan.len() as u32).to_be_bytes().to_vec();
        frame.extend(&plan);
        let mut reply = Vec::new();
        handle(&header, &mut frame.as_slice(), &mut reply).unwrap();
        let size = u32::from_be_bytes(reply[..4].try_into().unwrap()) as usize;
        let result: Value = serde_json::from_slice(&reply[4..4 + size]).unwrap();
        assert_eq!(result["request"], serde_json::to_value(request).unwrap());
        assert!(result["request"].get("revision").is_none());
        assert!(result["failure"].is_null());
        let count = u32::from_be_bytes(reply[4 + size..8 + size].try_into().unwrap()) as usize;
        assert_eq!(reply.len(), 8 + size + count);
        assert_eq!(
            result["result"]["nativeDigest"],
            format!("{:x}", Sha256::digest(&reply[8 + size..]))
        );
    }

    #[test]
    fn rejects_identity_digest_operation_and_trailing_frame_without_native() {
        let plan = b"{}";
        let mut r = request(plan);
        r.plan_digest = "0".repeat(64);
        let failed = process(&r, plan);
        assert!(failed.native.is_none());
        assert_eq!(failed.response.failure.unwrap().code, "digestMismatch");
        r = request(plan);
        r.authoring_id = "00000000-0000-0000-0000-000000000000".into();
        assert_eq!(
            process(&r, plan).response.failure.unwrap().code,
            "invalidContext"
        );
        r = request(plan);
        r.operation = "edit".into();
        assert_eq!(
            process(&r, plan).response.failure.unwrap().code,
            "unsupportedOperation"
        );
        let mut reply = Vec::new();
        handle(
            &serde_json::to_vec(&request(plan)).unwrap(),
            &mut &b"\0\0\0\x02{}x"[..],
            &mut reply,
        )
        .unwrap();
        let size = u32::from_be_bytes(reply[..4].try_into().unwrap()) as usize;
        assert_eq!(reply.len(), size + 4);
        let result: Value = serde_json::from_slice(&reply[4..]).unwrap();
        assert_eq!(result["failure"]["code"], "invalidFrame");
    }
}
