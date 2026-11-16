//! Compile and exercise the actual host route on Windows without importing the
//! Linux-only sandbox. This is route qualification, not whole-host qualification.
#[path = "../../src/authored_preview.rs"]
mod authored_preview;

use braid_ferrule_core::{authored_document::transport, authoring};
use serde_json::{Value, json};
use sha2::{Digest, Sha256};

fn frame(bytes: &[u8]) -> Vec<u8> {
    let mut result = (bytes.len() as u32).to_be_bytes().to_vec();
    result.extend(bytes);
    result
}

fn fixture() -> (Vec<u8>, Vec<u8>) {
    // The merged authoring compiler produces the exact unsaved native consumed
    // by this host route, proving both core modules survive the integration.
    let scalar = json!({"name":"value","shape":{"kind":"scalar","scalar":"string"}});
    let schema = json!({"name":"root","shape":{"kind":"group","children":[scalar]}});
    let plan = json!({
        "format":"braid.authoring.v1",
        "source":{"schema":schema,"options":{"kind":"json"}},
        "target":{"schema":schema,"options":{"kind":"csv","header":true,"quote":true,"bom":false,"preserve_empty_strings":true}},
        "edit":{"format":"ferrule.edit.v1","operations":[{
            "op":"bind","binding":{"scope":{"target":{"kind":"primary"},"children":[]},"field":"value"},
            "expression":{"kind":"source","id":"1","path":["value"]}
        }]}
    });
    let plan = serde_json::to_vec(&plan).unwrap();
    let native = authoring::compile(&plan).unwrap().native;
    let sample = br#"{"value":"merged, compiler"}"#;
    let header = json!({
        "protocolVersion":transport::PROTOCOL,"operation":"previewAuthored",
        "requestId":"00000000-0000-0000-0000-000000000001",
        "organizationId":"00000000-0000-0000-0000-000000000002",
        "projectId":"00000000-0000-0000-0000-000000000003",
        "authoringId":"00000000-0000-0000-0000-000000000004",
        "proposalDigest":format!("{:x}", Sha256::digest(&plan)),
        "nativeDigest":format!("{:x}", Sha256::digest(&native)),
        "sampleDigest":format!("{:x}", Sha256::digest(sample))
    });
    (
        serde_json::to_vec(&header).unwrap(),
        [frame(&native), frame(sample)].concat(),
    )
}

fn header(reply: &[u8]) -> (Value, usize) {
    let size = u32::from_be_bytes(reply[..4].try_into().unwrap()) as usize;
    (
        serde_json::from_slice(&reply[4..4 + size]).unwrap(),
        4 + size,
    )
}

#[test]
fn actual_host_route_executes_merged_authoring_output() {
    let (request, payload) = fixture();
    let mut reply = Vec::new();
    authored_preview::try_handle(&request, &mut payload.as_slice(), &mut reply)
        .unwrap()
        .unwrap();
    let (response, offset) = header(&reply);
    assert!(response["failure"].is_null());
    assert_eq!(
        response["request"],
        serde_json::from_slice::<Value>(&request).unwrap()
    );
    assert_eq!(response["result"]["format"], "csv");
    assert_eq!(response["result"]["mediaType"], "text/csv");
    let size = u32::from_be_bytes(reply[offset..offset + 4].try_into().unwrap()) as usize;
    assert_eq!(reply.len(), offset + 4 + size);
    assert_eq!(&reply[offset + 4..], b"value\n\"merged, compiler\"\n");
}

#[test]
fn other_protocols_and_ambiguous_probes_leave_streams_untouched() {
    for protocol in [
        "braid.ferrule-host.v1",
        authoring::transport::PROTOCOL,
        "braid.ferrule-preview.v1",
        "braid.ferrule-document-preview.v1",
        "braid.ferrule-csv-preview.v1",
        "braid.ferrule-xml-preview.v1",
        "unknown",
    ] {
        let request = serde_json::to_vec(&json!({"protocolVersion":protocol})).unwrap();
        let mut input = b"untouched".as_slice();
        let mut output = Vec::new();
        assert!(authored_preview::try_handle(&request, &mut input, &mut output).is_none());
        assert_eq!(input, b"untouched");
        assert!(output.is_empty());
    }
    let duplicate = format!(
        "{{\"protocolVersion\":\"{}\",\"protocolVersion\":\"{}\"}}",
        transport::PROTOCOL,
        transport::PROTOCOL
    );
    assert!(
        authored_preview::try_handle(duplicate.as_bytes(), &mut b"".as_slice(), &mut Vec::new())
            .is_none()
    );
}

#[test]
fn route_keeps_its_own_header_payload_and_eof_bounds() {
    assert_eq!(authored_preview::MAX_HEADER_BYTES, 16 * 1024);
    let (request, payload) = fixture();
    for input in [
        payload[..payload.len() - 1].to_vec(),
        [payload, vec![0]].concat(),
        ((transport::MAX_NATIVE_BYTES + 1) as u32)
            .to_be_bytes()
            .to_vec(),
    ] {
        let mut reply = Vec::new();
        authored_preview::try_handle(&request, &mut input.as_slice(), &mut reply)
            .unwrap()
            .unwrap();
        let (response, offset) = header(&reply);
        assert_eq!(response["failure"], json!({"code":"invalidFrame"}));
        assert_eq!(reply.len(), offset);
    }
    let mut oversized = request;
    oversized.resize(authored_preview::MAX_HEADER_BYTES + 1, b' ');
    let mut reply = Vec::new();
    authored_preview::try_handle(&oversized, &mut b"".as_slice(), &mut reply)
        .unwrap()
        .unwrap();
    assert_eq!(header(&reply).0["failure"], json!({"code":"invalidHeader"}));
}
