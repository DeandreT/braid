mod support;

use std::io::{Cursor, Write};
use std::process::{Command, Stdio};

use serde_json::{Value, json};
use support::{BASIC, digest, request};
use weft_ferrule_core::edit::{MAX_OPERATIONS, MAX_OPERATIONS_BYTES, Session, parse_script};
use weft_ferrule_host::edit::{PROTOCOL, Request, Response};

fn script() -> Vec<u8> {
    serde_json::to_vec(&json!({"format":"ferrule.edit.v1","operations":[{
        "op":"bind", "binding":{"scope":{"target":{"kind":"primary"},"children":[]},"field":"code"},
        "expression":{"kind":"constant","id":"4294967294","value":{"type":"string","text":"edited"}}
    }]}))
    .unwrap()
}

fn header(native: &[u8], operations: &[u8]) -> Request {
    let context = request("edit", native);
    Request {
        protocol_version: PROTOCOL.into(),
        request_id: context.request_id,
        operation: "edit".into(),
        revision: context.revision,
        native_digest: context.native_digest,
        document_id: context.document_id,
        operations_digest: digest(operations),
    }
}

fn frame(header: &[u8], native: &[u8], operations: &[u8]) -> Vec<u8> {
    let mut bytes = Vec::new();
    for body in [header, native, operations] {
        bytes.extend_from_slice(&(body.len() as u32).to_be_bytes());
        bytes.extend_from_slice(body);
    }
    bytes
}

fn response(bytes: &[u8]) -> (Response, &[u8]) {
    let length = u32::from_be_bytes(bytes[..4].try_into().unwrap()) as usize;
    let response: Response = serde_json::from_slice(&bytes[4..4 + length]).unwrap();
    if let Some(result) = &response.result {
        let native_length =
            u32::from_be_bytes(bytes[4 + length..8 + length].try_into().unwrap()) as usize;
        assert_eq!(native_length, result.native_byte_count);
        assert_eq!(bytes.len(), 8 + length + native_length);
        let native = &bytes[8 + length..];
        assert_eq!(digest(native), result.native_digest);
        (response, native)
    } else {
        assert_eq!(bytes.len(), 4 + length);
        (response, &[])
    }
}

#[test]
fn worker_replays_exact_base_and_returns_verified_native_validation_and_projection() {
    let operations = script();
    let header = header(BASIC, &operations);
    let mut worker = Command::new(env!("CARGO_BIN_EXE_weft-ferrule-host"))
        .env_clear()
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .unwrap();
    worker
        .stdin
        .take()
        .unwrap()
        .write_all(&frame(
            &serde_json::to_vec(&header).unwrap(),
            BASIC,
            &operations,
        ))
        .unwrap();
    let output = worker.wait_with_output().unwrap();
    assert!(output.status.success());
    assert!(output.stderr.is_empty());
    let (response, native) = response(&output.stdout);
    assert!(response.failure.is_none());
    assert_eq!(
        response.engine_revision,
        weft_ferrule_core::protocol::ENGINE_REVISION
    );
    let result = response.result.unwrap();
    assert_eq!(result.operation_count, 1);
    assert!(result.validation.is_valid);
    assert_eq!(result.operations_digest, header.operations_digest);
    let direct = Session::open(BASIC, &operations)
        .unwrap()
        .checkpoint("entry.native.json")
        .unwrap();
    assert_eq!(native, direct.native);
    assert_eq!(
        serde_json::to_value(result.validation).unwrap(),
        serde_json::to_value(direct.view.validation).unwrap()
    );
    assert_eq!(
        serde_json::to_value(result.projection).unwrap(),
        serde_json::to_value(direct.view.projection).unwrap()
    );
}

#[test]
fn edit_undo_and_redo_restore_the_exact_codec_output() {
    let empty = br#"{"format":"ferrule.edit.v1","operations":[]}"#;
    let mut session = Session::open(BASIC, empty).unwrap();
    let before = session.checkpoint("entry.native.json").unwrap().native;
    let op = parse_script(&script()).unwrap().operations.remove(0);
    assert!(session.apply(op).unwrap());
    let edited = session.checkpoint("entry.native.json").unwrap().native;
    assert_ne!(edited, before);
    assert!(session.undo().unwrap());
    assert_eq!(
        before,
        session.checkpoint("entry.native.json").unwrap().native
    );
    assert!(session.redo().unwrap());
    assert_eq!(
        edited,
        session.checkpoint("entry.native.json").unwrap().native
    );
}

#[test]
fn browser_handles_are_ferrule_resolved_and_allocate_an_unused_exact_node_identity() {
    let empty = br#"{"format":"ferrule.edit.v1","operations":[]}"#;
    let mut session = Session::open(BASIC, empty).unwrap();
    let view = session.view("entry.native.json").unwrap();
    let target = view
        .targets
        .into_iter()
        .find(|target| target.binding.field == "code")
        .unwrap();
    assert!(
        view.projection
            .fields
            .iter()
            .any(|field| field.id == target.field_id)
    );
    let id = view.next_node_id.unwrap();
    let operation = serde_json::from_value(json!({
        "op":"bind", "binding":target.binding,
        "expression":{"kind":"constant","id":id,"value":{"type":"int","text":"9007199254740993"}}
    }))
    .unwrap();
    assert!(session.apply(operation).unwrap());
    let changed = session.view("entry.native.json").unwrap();
    assert_eq!(
        changed
            .targets
            .iter()
            .find(|target| target.binding.field == "code")
            .unwrap()
            .node
            .as_deref(),
        Some(id.as_str())
    );
    assert_ne!(changed.next_node_id.as_deref(), Some(id.as_str()));
}

#[test]
fn failed_replay_reports_its_zero_based_operation_and_has_no_partial_checkpoint() {
    let mut script: Value = serde_json::from_slice(&script()).unwrap();
    let second = script["operations"][0].clone();
    script["operations"].as_array_mut().unwrap().push(second);
    let operations = serde_json::to_vec(&script).unwrap();
    let result = weft_ferrule_host::edit::process(&header(BASIC, &operations), BASIC, &operations);
    let failure = result.response.failure.unwrap();
    assert_eq!(failure.code, "operationRejected");
    assert_eq!(failure.operation_index.as_deref(), Some("1"));
    assert!(result.native.is_none());
    assert!(result.response.result.is_none());
}

#[test]
fn script_is_closed_and_bounded_and_refuses_numeric_identities() {
    for mutation in [
        json!({"op":"execute","path":"/etc/passwd"}),
        json!({"op":"edit_constant","node":1,"value":{"type":"string","text":"x"}}),
        json!({"op":"edit_constant","node":"1","value":{"type":"string","text":"x","native":true}}),
    ] {
        assert!(
            parse_script(
                &serde_json::to_vec(&json!({"format":"ferrule.edit.v1","operations":[mutation]}))
                    .unwrap()
            )
            .is_err()
        );
    }
    let mut unknown: Value = serde_json::from_slice(&script()).unwrap();
    unknown["actor"] = json!("another-user");
    assert_eq!(
        parse_script(&serde_json::to_vec(&unknown).unwrap())
            .unwrap_err()
            .code,
        "invalidOperations"
    );
    assert_eq!(
        parse_script(&vec![b' '; MAX_OPERATIONS_BYTES + 1])
            .unwrap_err()
            .code,
        "operationsLimitExceeded"
    );
    let op = unknown["operations"][0].clone();
    assert_eq!(
        parse_script(
            &serde_json::to_vec(
                &json!({"format":"ferrule.edit.v1","operations":vec![op; MAX_OPERATIONS+1]})
            )
            .unwrap()
        )
        .unwrap_err()
        .code,
        "operationsLimitExceeded"
    );
}

#[test]
fn framing_digest_unknown_header_and_trailing_bytes_fail_closed() {
    let operations = script();
    let header = header(BASIC, &operations);
    let mut unknown = serde_json::to_value(&header).unwrap();
    unknown["nativeOverride"] = json!("{}");
    let good = frame(&serde_json::to_vec(&header).unwrap(), BASIC, &operations);
    for (bytes, expected) in [
        (
            frame(&serde_json::to_vec(&unknown).unwrap(), BASIC, &operations),
            "invalidHeader",
        ),
        (
            frame(&serde_json::to_vec(&header).unwrap(), b"{}", &operations),
            "digestMismatch",
        ),
        ([good.clone(), vec![0]].concat(), "invalidFrame"),
        (good[..good.len() - 1].to_vec(), "invalidFrame"),
    ] {
        let mut output = Vec::new();
        weft_ferrule_host::handle_stream(&mut Cursor::new(bytes), &mut output).unwrap();
        assert_eq!(response(&output).0.failure.unwrap().code, expected);
    }
}

#[test]
fn every_stored_fixture_is_a_codec_fixed_point() {
    let directory = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/fixtures");
    for entry in std::fs::read_dir(directory).unwrap() {
        let path = entry.unwrap().path();
        if path.extension().is_none_or(|extension| extension != "json") {
            continue;
        }
        let native = std::fs::read(&path).unwrap();
        let project = ferrule_mapping::project_file::decode_bytes(&native).unwrap();
        let encoded = ferrule_mapping::project_file::encode_pretty(&project).unwrap();
        let decoded = ferrule_mapping::project_file::decode_str(&encoded).unwrap();
        assert_eq!(
            encoded,
            ferrule_mapping::project_file::encode_pretty(&decoded).unwrap(),
            "{}",
            path.display()
        );
    }
}
