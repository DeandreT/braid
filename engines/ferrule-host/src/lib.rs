#![forbid(unsafe_code)]

use std::io::{self, Read, Write};

use ferrule_mapping::FileCodecError;
use sha2::{Digest, Sha256};

pub mod decode;
pub mod build;
pub mod framing;
pub mod inspect;
pub mod preview;
pub mod projection;
pub mod protocol;
pub mod validation;

use preview::protocol::PREVIEW_PROTOCOL_VERSION;
use protocol::{
    Failure, MAX_DIAGNOSTICS, MAX_HEADER_BYTES, MAX_ITEMS, MAX_NATIVE_BYTES, MAX_TEXT_BYTES,
    NativeLocator, PROTOCOL_VERSION, RequestHeader, Response, Revision, WorkerResult, bounded_text,
};
use serde::Deserialize;

pub fn handle_stream(reader: &mut impl Read, writer: &mut impl Write) -> io::Result<()> {
    let header_bytes = match framing::read_frame(reader, MAX_HEADER_BYTES) {
        Ok(bytes) => bytes,
        Err(message) => {
            return framing::write_response(
                writer,
                &Response::failure(None, "invalidFrame", message),
            );
        }
    };
    // Only the preview protocol selects the separate multi-document request shape.
    #[derive(Deserialize)]
    struct ProtocolProbe {
        #[serde(rename = "protocolVersion")]
        protocol_version: Option<String>,
    }
    if serde_json::from_slice::<ProtocolProbe>(&header_bytes)
        .is_ok_and(|probe| probe.protocol_version.as_deref() == Some(build::PROTOCOL))
    {
        return build::handle(&header_bytes, reader, writer);
    }
    if serde_json::from_slice::<ProtocolProbe>(&header_bytes)
        .is_ok_and(|probe| probe.protocol_version.as_deref() == Some(PREVIEW_PROTOCOL_VERSION))
    {
        return preview::handle_preview(&header_bytes, reader, writer);
    }
    let header = serde_json::from_slice::<RequestHeader>(&header_bytes).ok();
    let native = match framing::read_frame(reader, MAX_NATIVE_BYTES) {
        Ok(bytes) => bytes,
        Err(message) => {
            return framing::write_response(
                writer,
                &Response::failure(header.as_ref(), "invalidFrame", message),
            );
        }
    };
    if let Err(message) = framing::require_eof(reader) {
        return framing::write_response(
            writer,
            &Response::failure(header.as_ref(), "invalidFrame", message),
        );
    }
    let response = match header {
        Some(header) => process_request(&header, &native),
        None => Response::failure(
            None,
            "invalidHeader",
            "The request header is not an admitted UTF-8 JSON object.",
        ),
    };
    framing::write_response(writer, &response)
}

pub fn process_request(header: &RequestHeader, native: &[u8]) -> Response {
    if header.protocol_version != PROTOCOL_VERSION {
        return Response::failure(
            Some(header),
            "unsupportedProtocolVersion",
            "The requested host protocol version is not supported.",
        );
    }
    if !matches!(
        header.operation.as_str(),
        "inspect" | "validate" | "project"
    ) {
        return Response::failure(
            Some(header),
            "unsupportedOperation",
            "This host admits inspection, validation, and projection only.",
        );
    }
    if !valid_context(header) {
        return Response::failure(
            Some(header),
            "invalidContext",
            "The revision, request, digest, or logical document identity is invalid.",
        );
    }
    if !(1..=MAX_DIAGNOSTICS).contains(&header.limits.maximum_diagnostics)
        || !(1..=MAX_ITEMS).contains(&header.limits.maximum_items)
        || !(64..=MAX_TEXT_BYTES).contains(&header.limits.maximum_text_bytes)
    {
        return Response::failure(
            Some(header),
            "invalidLimits",
            "The requested limits are outside the compiled admission range.",
        );
    }
    if native.is_empty() || native.len() > MAX_NATIVE_BYTES {
        return Response::failure(
            Some(header),
            "invalidFrame",
            "The native document exceeds its admitted byte range.",
        );
    }
    let actual_digest = format!("{:x}", Sha256::digest(native));
    if actual_digest != header.native_digest {
        return Response::failure(
            Some(header),
            "digestMismatch",
            "The native bytes do not match the requested SHA-256 digest.",
        );
    }
    let decoded = match decode::decode_native(native) {
        Ok(decoded) => decoded,
        Err(error) => {
            let code = if matches!(&error, FileCodecError::UnsupportedVersion { .. }) {
                "unsupportedNativeVersion"
            } else {
                "nativeDecodeFailed"
            };
            let (message, message_truncated) = bounded_text(
                &error.to_string(),
                header.limits.maximum_text_bytes as usize,
            );
            return Response::with_failure(
                Some(header),
                Failure {
                    code: code.into(),
                    message,
                    message_truncated,
                    native_locator: Some(NativeLocator::Document {
                        document_id: header.document_id.clone(),
                    }),
                },
            );
        }
    };
    let inspection = match inspect::inspect(
        &decoded.project,
        &header.document_id,
        &decoded.document_prefix,
        &header.limits,
    ) {
        Ok(inspection) => inspection,
        Err(message) => return Response::failure(Some(header), "inspectionLimitExceeded", message),
    };
    if header.operation == "inspect" {
        return Response::success(header, WorkerResult::Inspect(inspection));
    }
    if header.operation == "project" {
        return match projection::project(
            &decoded.project,
            &header.document_id,
            &decoded.document_prefix,
            &header.limits,
        ) {
            Ok(projection) => Response::success(header, WorkerResult::Project(projection)),
            Err(message) => Response::failure(Some(header), "inspectionLimitExceeded", message),
        };
    }
    match validation::validate(
        &decoded.project,
        &header.document_id,
        &decoded.document_prefix,
        &header.limits,
    ) {
        Ok(validation) => Response::success(header, WorkerResult::Validate(validation)),
        Err(message) => Response::failure(Some(header), "inspectionLimitExceeded", message),
    }
}

fn valid_context(header: &RequestHeader) -> bool {
    valid_context_parts(
        &header.request_id,
        &header.revision,
        &header.native_digest,
        &header.document_id,
    )
}

pub(crate) fn valid_context_parts(
    request_id: &str,
    revision: &Revision,
    native_digest: &str,
    document_id: &str,
) -> bool {
    [
        request_id,
        &revision.organization_id,
        &revision.project_id,
        &revision.mapping_id,
        &revision.revision_id,
    ]
    .into_iter()
    .all(canonical_nonzero_guid)
        && native_digest.len() == 64
        && native_digest
            .bytes()
            .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
        && valid_document_id(document_id)
}

fn canonical_nonzero_guid(value: &str) -> bool {
    value.len() == 36
        && value != "00000000-0000-0000-0000-000000000000"
        && value.bytes().enumerate().all(|(index, byte)| {
            if matches!(index, 8 | 13 | 18 | 23) {
                byte == b'-'
            } else {
                byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte)
            }
        })
}

fn valid_document_id(value: &str) -> bool {
    !value.is_empty()
        && value.len() <= 128
        && !value.contains("..")
        && value.as_bytes()[0].is_ascii_alphanumeric()
        && value
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'_' | b'-'))
}
