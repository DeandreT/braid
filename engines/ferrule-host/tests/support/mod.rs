#![allow(dead_code)]

use std::io::Cursor;

use serde_json::Value;
use sha2::{Digest, Sha256};
use weft_ferrule_host::protocol::{Limits, PROTOCOL_VERSION, RequestHeader, Revision};

pub const BASIC: &[u8] = include_bytes!("../fixtures/valid-basic.json");
pub const INVALID: &[u8] = include_bytes!("../fixtures/invalid-graph.json");
pub const INVALID_FUNCTION: &[u8] = include_bytes!("../fixtures/invalid-function.json");
pub const EXACT: &[u8] = include_bytes!("../fixtures/versioned-exact.json");
pub const RESOURCES: &[u8] = include_bytes!("../fixtures/resource-metadata.json");

pub fn request(operation: &str, native: &[u8]) -> RequestHeader {
    RequestHeader {
        protocol_version: PROTOCOL_VERSION.into(),
        request_id: "11111111-1111-4111-8111-111111111111".into(),
        operation: operation.into(),
        revision: Revision {
            organization_id: "22222222-2222-4222-8222-222222222222".into(),
            project_id: "33333333-3333-4333-8333-333333333333".into(),
            mapping_id: "44444444-4444-4444-8444-444444444444".into(),
            revision_id: "55555555-5555-4555-8555-555555555555".into(),
        },
        native_digest: digest(native),
        document_id: "entry.native.json".into(),
        limits: Limits {
            maximum_diagnostics: 128,
            maximum_items: 8192,
            maximum_text_bytes: 2048,
        },
    }
}

pub fn digest(bytes: &[u8]) -> String {
    format!("{:x}", Sha256::digest(bytes))
}

pub fn process(header: &RequestHeader, native: &[u8]) -> Value {
    serde_json::to_value(weft_ferrule_host::process_request(header, native)).unwrap()
}

pub fn framed_bytes(header: &[u8], native: &[u8]) -> Vec<u8> {
    let mut bytes = Vec::new();
    bytes.extend_from_slice(&(header.len() as u32).to_be_bytes());
    bytes.extend_from_slice(header);
    bytes.extend_from_slice(&(native.len() as u32).to_be_bytes());
    bytes.extend_from_slice(native);
    bytes
}

pub fn stream(bytes: &[u8]) -> Value {
    let mut output = Vec::new();
    weft_ferrule_host::handle_stream(&mut Cursor::new(bytes), &mut output).unwrap();
    response_frame(&output)
}

pub fn response_frame(output: &[u8]) -> Value {
    assert!(output.len() >= 4, "response must contain its length prefix");
    let length = u32::from_be_bytes(output[..4].try_into().unwrap()) as usize;
    assert!(length <= 8 * 1024 * 1024, "response exceeds hard cap");
    assert_eq!(output.len(), 4 + length, "exactly one response frame");
    serde_json::from_slice(&output[4..]).unwrap()
}

pub fn success(response: &Value, header: &RequestHeader, kind: &str) {
    assert!(response["failure"].is_null(), "{response:#}");
    assert_eq!(response["protocolVersion"], PROTOCOL_VERSION);
    assert_eq!(response["requestId"], header.request_id);
    assert_eq!(response["operation"], header.operation);
    assert_eq!(response["nativeDigest"], header.native_digest);
    assert_eq!(response["documentId"], header.document_id);
    assert_eq!(
        response["revision"],
        serde_json::to_value(&header.revision).unwrap()
    );
    assert_eq!(response["result"]["kind"], kind);
}

pub fn failure(response: &Value) -> &str {
    assert!(response["result"].is_null(), "{response:#}");
    let failure = &response["failure"];
    assert!(failure["message"].is_string(), "{response:#}");
    failure["code"].as_str().expect("structured failure code")
}
