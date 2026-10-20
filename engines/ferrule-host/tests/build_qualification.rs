mod support;
use serde_json::{Value, json};
use std::io::Cursor;
use support::{BASIC, INVALID, digest};
use weft_ferrule_host::build::{PROFILE, PROTOCOL, Request};

fn request(native: &[u8]) -> Request {
    let reference = support::request("inspect", native);
    Request {
        protocol_version: PROTOCOL.into(),
        request_id: reference.request_id,
        revision: reference.revision,
        native_digest: digest(native),
        intent_digest: "a".repeat(64),
        operation: "compile".into(),
        profile: PROFILE.into(),
    }
}
fn header(native: &[u8]) -> Value {
    let r = request(native);
    json!({"protocolVersion":r.protocol_version,"requestId":r.request_id,"revision":r.revision,
        "nativeDigest":r.native_digest,"intentDigest":r.intent_digest,"operation":r.operation,"profile":r.profile})
}

#[test]
fn native_codegen_emits_complete_bounded_deterministic_sources() {
    let a = weft_ferrule_host::build::generate(&request(BASIC), BASIC).unwrap();
    let b = weft_ferrule_host::build::generate(&request(BASIC), BASIC).unwrap();
    assert_eq!(a.1, b.1);
    assert!(a.1.iter().any(|f| f.path.as_str() == "GeneratedMapping.cs"));
    assert!(
        a.1.iter()
            .any(|f| f.path.as_str() == "Ferrule.Generated.csproj")
    );
    assert!(a.1.len() > 3 && a.1.len() <= 128);
    assert!(
        a.1.iter().map(|f| f.contents.len()).sum::<usize>()
            <= weft_ferrule_host::build::MAX_SOURCE_BYTES
    );
    assert_eq!(a.0.len(), 2);
    assert_eq!(a.0[0].kind, "primaryInput");
    assert_eq!(a.0[1].kind, "primaryOutput");
}
#[test]
fn unsupported_physical_input_does_not_fall_back_to_json() {
    let native = include_bytes!("fixtures/preview-csv-source.json");
    assert_eq!(
        weft_ferrule_host::build::generate(&request(native), native).err(),
        Some("unsupported")
    );
}
#[test]
fn malformed_mapping_is_not_build_support() {
    assert!(weft_ferrule_host::build::generate(&request(INVALID), INVALID).is_err());
}
#[test]
fn digest_and_profile_substitution_refuse_before_generation() {
    let mut r = request(BASIC);
    r.native_digest = "b".repeat(64);
    assert_eq!(
        weft_ferrule_host::build::generate(&r, BASIC).err(),
        Some("invalidRequest")
    );
    r = request(BASIC);
    r.profile = "unknown".into();
    assert_eq!(
        weft_ferrule_host::build::generate(&r, BASIC).err(),
        Some("invalidRequest")
    );
}
#[test]
fn framed_reply_hashes_every_source_and_ends_at_declared_bytes() {
    let mut input = support::framed_bytes(&serde_json::to_vec(&header(BASIC)).unwrap(), BASIC);
    let mut output = Vec::new();
    weft_ferrule_host::handle_stream(&mut Cursor::new(&input), &mut output).unwrap();
    let mut cursor = Cursor::new(&output);
    let raw = weft_ferrule_host::framing::read_frame(&mut cursor, 128 * 1024).unwrap();
    let reply: Value = serde_json::from_slice(&raw).unwrap();
    assert_eq!(reply["status"], "supported");
    assert_eq!(reply["intentDigest"], "a".repeat(64));
    for f in reply["files"].as_array().unwrap() {
        let content =
            weft_ferrule_host::framing::read_frame(&mut cursor, 32 * 1024 * 1024).unwrap();
        assert_eq!(f["byteCount"], content.len());
        assert_eq!(f["sha256"], digest(&content));
    }
    assert_eq!(cursor.position() as usize, output.len());
    input.push(1);
    output.clear();
    weft_ferrule_host::handle_stream(&mut Cursor::new(input), &mut output).unwrap();
    let reply = support::response_frame(&output);
    assert_eq!(reply["status"], "invalidRequest");
    assert!(reply["files"].as_array().unwrap().is_empty());
}
