#![forbid(unsafe_code)]

use std::io::{self, Read, Write};

use ferrule_mapping::FileCodecError;
use sha2::{Digest, Sha256};

mod authored_preview;
pub mod build;
pub mod csv_document;
pub mod document_format;
pub mod document_limits;
pub mod edi_envelope;
pub mod edit;
pub mod framing;
pub mod interchange;
pub mod preview;
pub mod sandbox;
mod x12_syntax;
pub mod xml_document;
pub use braid_ferrule_core::{
    canonical_nonzero_guid, decode, inspect, projection, protocol, valid_context_parts, validation,
};

use preview::protocol::{
    CSV_PREVIEW_PROTOCOL_VERSION, DOCUMENT_PREVIEW_PROTOCOL_VERSION, PREVIEW_PROTOCOL_VERSION,
    XML_PREVIEW_PROTOCOL_VERSION,
};
use protocol::{
    Failure, MAX_DIAGNOSTICS, MAX_HEADER_BYTES, MAX_ITEMS, MAX_NATIVE_BYTES, MAX_TEXT_BYTES,
    NativeLocator, PROTOCOL_VERSION, RequestHeader, Response, WorkerResult, bounded_text,
};
use serde::Deserialize;

pub fn handle_stream(reader: &mut impl Read, writer: &mut impl Write) -> io::Result<()> {
    let header_bytes = match framing::read_frame(
        reader,
        MAX_HEADER_BYTES.max(authored_preview::MAX_HEADER_BYTES),
    ) {
        Ok(bytes) => bytes,
        Err(message) => {
            return framing::write_response(
                writer,
                &Response::failure(None, "invalidFrame", message),
            );
        }
    };
    if let Some(result) = authored_preview::try_handle(&header_bytes, reader, writer) {
        return result;
    }
    // Keep legacy header admission unchanged if the independent route's cap
    // ever grows. Its native/sample frames are read only by its own handler.
    if header_bytes.len() > MAX_HEADER_BYTES {
        return framing::write_response(
            writer,
            &Response::failure(
                None,
                "invalidFrame",
                "A frame length exceeds its admitted range.",
            ),
        );
    }
    // Select independent bounded request shapes before native inspection.
    #[derive(Deserialize)]
    struct ProtocolProbe {
        #[serde(rename = "protocolVersion")]
        protocol_version: Option<String>,
    }
    if serde_json::from_slice::<ProtocolProbe>(&header_bytes).is_ok_and(|probe| {
        probe.protocol_version.as_deref()
            == Some(braid_ferrule_core::authoring::transport::PROTOCOL)
    }) {
        return braid_ferrule_core::authoring::transport::handle(&header_bytes, reader, writer);
    }
    if serde_json::from_slice::<ProtocolProbe>(&header_bytes)
        .is_ok_and(|probe| probe.protocol_version.as_deref() == Some(edit::PROTOCOL))
    {
        return edit::handle(&header_bytes, reader, writer);
    }
    if serde_json::from_slice::<ProtocolProbe>(&header_bytes)
        .is_ok_and(|probe| probe.protocol_version.as_deref() == Some(interchange::PROTOCOL))
    {
        return interchange::handle(&header_bytes, reader, writer);
    }
    if serde_json::from_slice::<ProtocolProbe>(&header_bytes)
        .is_ok_and(|probe| probe.protocol_version.as_deref() == Some(build::PROTOCOL))
    {
        return build::handle(&header_bytes, reader, writer);
    }
    if serde_json::from_slice::<ProtocolProbe>(&header_bytes).is_ok_and(|probe| {
        matches!(
            probe.protocol_version.as_deref(),
            Some(
                PREVIEW_PROTOCOL_VERSION
                    | DOCUMENT_PREVIEW_PROTOCOL_VERSION
                    | CSV_PREVIEW_PROTOCOL_VERSION
                    | XML_PREVIEW_PROTOCOL_VERSION
            )
        )
    }) {
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
