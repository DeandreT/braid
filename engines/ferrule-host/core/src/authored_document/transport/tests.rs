use super::*;
use crate::authored_document::{
    Format,
    tests::{native, project},
};
use serde_json::{Value, json};

fn request(native: &[u8], sample: &[u8]) -> Request {
    Request {
        protocol_version: PROTOCOL.into(),
        operation: OPERATION.into(),
        request_id: "00000000-0000-0000-0000-000000000001".into(),
        organization_id: "00000000-0000-0000-0000-000000000002".into(),
        project_id: "00000000-0000-0000-0000-000000000003".into(),
        authoring_id: "00000000-0000-0000-0000-000000000004".into(),
        proposal_digest: crate::authored_document::digest(
            b"proposal bytes remain with the project owner",
        ),
        native_digest: crate::authored_document::digest(native),
        sample_digest: crate::authored_document::digest(sample),
    }
}
fn frame(bytes: &[u8]) -> Vec<u8> {
    let mut frame = (bytes.len() as u32).to_be_bytes().to_vec();
    frame.extend(bytes);
    frame
}
fn message(request: &Request, native: &[u8], sample: &[u8]) -> Vec<u8> {
    [
        frame(&serde_json::to_vec(request).unwrap()),
        frame(native),
        frame(sample),
    ]
    .concat()
}
fn reply(input: &[u8]) -> (Value, Option<Vec<u8>>) {
    let mut output = Vec::new();
    serve(&mut &input[..], &mut output).unwrap();
    assert!(output.len() <= MAX_RESPONSE_BYTES);
    let mut reader = output.as_slice();
    let header = read_frame(&mut reader, MAX_HEADER_BYTES, false).unwrap();
    let header: Value = serde_json::from_slice(&header).unwrap();
    assert_ne!(header["result"].is_null(), header["failure"].is_null());
    assert_eq!(header["protocolVersion"], PROTOCOL);
    assert_eq!(header["engineRevision"], crate::protocol::ENGINE_REVISION);
    let bytes = if header["result"].is_null() {
        None
    } else {
        Some(read_frame(&mut reader, MAX_OUTPUT_BYTES, true).unwrap())
    };
    assert!(reader.is_empty());
    (header, bytes)
}
fn failure(input: &[u8], code: &str) {
    let (header, output) = reply(input);
    assert_eq!(header["failure"], json!({"code":code}));
    assert!(output.is_none());
}

#[test]
fn exact_project_owned_echo_and_two_frame_success() {
    let p = project();
    let native = native(&p);
    let sample = br#"{"value":"unsaved"}"#;
    let r = request(&native, sample);
    let (header, output) = reply(&message(&r, &native, sample));
    assert_eq!(header["request"], serde_json::to_value(r).unwrap());
    assert!(header["request"].get("revision").is_none());
    let output = output.unwrap();
    assert_eq!(
        header["result"],
        json!({"outputDigest":crate::authored_document::digest(&output),
        "outputByteCount":output.len(), "format":"json", "mediaType":"application/json"})
    );
    assert_eq!(
        serde_json::from_slice::<Value>(&output).unwrap(),
        json!({"value":"unsaved"})
    );
}

#[test]
fn every_truncation_extra_byte_and_oversized_prefix_refuses_without_output() {
    let native = native(&project());
    let sample = br#"{"value":"x"}"#;
    let r = request(&native, sample);
    let good = message(&r, &native, sample);
    for end in 0..good.len() {
        failure(&good[..end], "invalidFrame");
    }
    let mut extra = good.clone();
    extra.push(0);
    failure(&extra, "invalidFrame");
    failure(&[good, frame(b"extra")].concat(), "invalidFrame");
    failure(&u32::MAX.to_be_bytes(), "invalidFrame");
    let header = frame(&serde_json::to_vec(&r).unwrap());
    failure(
        &[
            header.clone(),
            ((MAX_NATIVE_BYTES + 1) as u32).to_be_bytes().to_vec(),
        ]
        .concat(),
        "invalidFrame",
    );
    failure(
        &[
            header,
            frame(&native),
            ((MAX_SAMPLE_BYTES + 1) as u32).to_be_bytes().to_vec(),
        ]
        .concat(),
        "invalidFrame",
    );
}

#[test]
fn strict_header_and_context_and_both_exact_byte_digests() {
    let native = native(&project());
    let sample = b"{}";
    let mut r = request(&native, sample);
    failure(&frame(b"not json"), "invalidHeader");
    let mut unknown = serde_json::to_value(&r).unwrap();
    unknown["revision"] = json!({});
    failure(
        &frame(&serde_json::to_vec(&unknown).unwrap()),
        "invalidHeader",
    );
    let mut duplicate = serde_json::to_string(&r).unwrap();
    duplicate.pop();
    duplicate.push_str(",\"operation\":\"previewAuthored\"}");
    failure(&frame(duplicate.as_bytes()), "invalidHeader");
    r.operation = "preview".into();
    failure(&message(&r, &native, sample), "unsupportedOperation");
    r = request(&native, sample);
    r.protocol_version = "other".into();
    failure(&message(&r, &native, sample), "unsupportedOperation");
    r = request(&native, sample);
    r.authoring_id = "00000000-0000-0000-0000-000000000000".into();
    failure(&message(&r, &native, sample), "invalidContext");
    r = request(&native, sample);
    r.proposal_digest = "A".repeat(64);
    failure(&message(&r, &native, sample), "invalidContext");
    r = request(&native, sample);
    r.native_digest = "0".repeat(64);
    failure(&message(&r, &native, sample), "digestMismatch");
    r = request(&native, sample);
    r.sample_digest = "0".repeat(64);
    failure(&message(&r, &native, sample), "digestMismatch");
}

#[test]
fn core_failures_have_closed_wire_categories() {
    let invalid = b"{}";
    let sample = b"{}";
    failure(
        &message(&request(invalid, sample), invalid, sample),
        "invalidMapping",
    );
    let mut p = project();
    p.source_path = Some("https://example.invalid/private".into());
    let bytes = native(&p);
    failure(
        &message(&request(&bytes, sample), &bytes, sample),
        "unsupportedSemantics",
    );
    p.source_path = None;
    let bytes = native(&p);
    let bad_sample = b"private invalid sample";
    failure(
        &message(&request(&bytes, bad_sample), &bytes, bad_sample),
        "invalidSample",
    );
    p.graph.nodes.insert(
        1,
        ferrule_mapping::Node::Const {
            value: ferrule_ir::Value::String("x".repeat(MAX_OUTPUT_BYTES)),
        },
    );
    let bytes = native(&p);
    failure(
        &message(&request(&bytes, sample), &bytes, sample),
        "resourceLimit",
    );
    assert_eq!(
        FailureCode::from(Failure::InvalidOutput),
        FailureCode::ExecutionFailed
    );
    assert_eq!(
        FailureCode::from(Failure::DeadlineExceeded),
        FailureCode::ResourceLimit
    );
    assert_eq!(
        FailureCode::from(Failure::NamedEndpoints),
        FailureCode::UnsupportedSemantics
    );
}

#[test]
fn empty_csv_sample_and_zero_byte_csv_output_are_successful_frames() {
    let mut p = project();
    let options = ferrule_mapping::FormatOptions {
        tabular_kind: Some(ferrule_mapping::TabularBoundaryKind::Csv),
        has_header_row: Some(false),
        ..Default::default()
    };
    p.source_options = options.clone();
    p.target_options = options;
    p.root.iteration = ferrule_mapping::ScopeIteration::Source(vec![]);
    let bytes = native(&p);
    let r = request(&bytes, b"");
    let (header, output) = reply(&message(&r, &bytes, b""));
    assert_eq!(header["result"]["outputByteCount"], 0);
    assert_eq!(output, Some(vec![]));
}

#[test]
fn invalid_echo_cannot_overflow_the_response_header() {
    let native = native(&project());
    let sample = b"{}";
    let mut r = request(&native, sample);
    let base = serde_json::to_vec(&r).unwrap().len();
    r.request_id.push_str(&"x".repeat(MAX_HEADER_BYTES - base));
    assert_eq!(serde_json::to_vec(&r).unwrap().len(), MAX_HEADER_BYTES);
    let (header, output) = reply(&message(&r, &native, sample));
    assert_eq!(header["failure"], json!({"code":"resourceLimit"}));
    assert!(header["request"].is_null());
    assert!(output.is_none());
}

#[test]
fn all_format_media_pairs_are_closed_and_match_the_contract() {
    for (f, id, media) in [
        (Format::Json, "json", "application/json"),
        (Format::Xml, "xml", "application/xml"),
        (Format::Csv, "csv", "text/csv"),
        (Format::Tsv, "tsv", "text/tab-separated-values"),
        (Format::X12, "x12", "application/edi-x12"),
        (Format::Edifact, "edifact", "application/edifact"),
    ] {
        assert_eq!(f.id(), id);
        assert_eq!(f.media_type(), media);
    }
}
