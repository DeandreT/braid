mod support;

use std::io::{Read, Write};
use std::process::{Command, Stdio};

use braid_ferrule_host::protocol::{
    MAX_HEADER_BYTES, MAX_NATIVE_BYTES, MAX_RESPONSE_BYTES, PROTOCOL_VERSION, Response,
};
use serde_json::{Value, json};
use support::*;

#[test]
fn framing_roundtrip_emits_one_structured_response() {
    let header = request("inspect", BASIC);
    let bytes = framed_bytes(&serde_json::to_vec(&header).unwrap(), BASIC);
    let response = stream(&bytes);
    success(&response, &header, "inspect");
}

#[test]
fn executable_accepts_exact_native_frames_and_keeps_stdout_protocol_only() {
    let header = request("validate", EXACT);
    let bytes = framed_bytes(&serde_json::to_vec(&header).unwrap(), EXACT);
    let mut worker = Command::new(env!("CARGO_BIN_EXE_braid-ferrule-host"))
        .env_clear()
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .unwrap();
    worker.stdin.take().unwrap().write_all(&bytes).unwrap();
    let output = worker.wait_with_output().unwrap();
    assert!(output.status.success());
    assert!(output.stderr.len() <= 16 * 1024);
    let response = response_frame(&output.stdout);
    success(&response, &header, "validate");
}

#[test]
fn oversized_frames_are_rejected_before_body_allocation_or_read() {
    struct PrefixOnly {
        prefix: std::io::Cursor<Vec<u8>>,
    }
    impl Read for PrefixOnly {
        fn read(&mut self, bytes: &mut [u8]) -> std::io::Result<usize> {
            if self.prefix.position() == self.prefix.get_ref().len() as u64 {
                panic!("an oversized frame body must never be read");
            }
            self.prefix.read(bytes)
        }
    }
    let header = serde_json::to_vec(&request("inspect", BASIC)).unwrap();
    let mut native_prefix = Vec::new();
    native_prefix.extend_from_slice(&(header.len() as u32).to_be_bytes());
    native_prefix.extend_from_slice(&header);
    native_prefix.extend_from_slice(&((MAX_NATIVE_BYTES + 1) as u32).to_be_bytes());
    for prefix in [
        ((MAX_HEADER_BYTES + 1) as u32).to_be_bytes().to_vec(),
        native_prefix,
    ] {
        let mut input = PrefixOnly {
            prefix: std::io::Cursor::new(prefix),
        };
        let mut output = Vec::new();
        braid_ferrule_host::handle_stream(&mut input, &mut output).unwrap();
        assert_eq!(failure(&response_frame(&output)), "invalidFrame");
    }
}

#[test]
fn truncated_zero_and_extra_frames_are_structured_failures() {
    let header = serde_json::to_vec(&request("inspect", BASIC)).unwrap();
    let good = framed_bytes(&header, BASIC);
    let cases = [
        Vec::new(),
        vec![0, 0, 0],
        0_u32.to_be_bytes().to_vec(),
        good[..4 + header.len() - 1].to_vec(),
        good[..4 + header.len() + 2].to_vec(),
        good[..good.len() - 1].to_vec(),
        framed_bytes(&header, &[]),
        {
            let mut bytes = good.clone();
            bytes.push(0);
            bytes
        },
        {
            let mut bytes = good.clone();
            bytes.extend_from_slice(&good);
            bytes
        },
    ];
    for bytes in cases {
        assert_eq!(failure(&stream(&bytes)), "invalidFrame");
    }
}

#[test]
fn malformed_headers_and_unknown_properties_are_rejected() {
    let mut unknown = serde_json::to_value(request("inspect", BASIC)).unwrap();
    unknown["command"] = json!("arbitrary-command");
    let mut unknown_revision = serde_json::to_value(request("inspect", BASIC)).unwrap();
    unknown_revision["revision"]["tenantOverride"] = json!("other-tenant");
    let mut unknown_limits = serde_json::to_value(request("inspect", BASIC)).unwrap();
    unknown_limits["limits"]["unbounded"] = json!(true);
    for header in [
        b"{".to_vec(),
        b"[]".to_vec(),
        vec![0xff],
        serde_json::to_vec(&unknown).unwrap(),
        serde_json::to_vec(&unknown_revision).unwrap(),
        serde_json::to_vec(&unknown_limits).unwrap(),
    ] {
        assert_eq!(
            failure(&stream(&framed_bytes(&header, BASIC))),
            "invalidHeader"
        );
    }
}

#[test]
fn header_versions_operations_context_digests_and_limits_are_admitted_explicitly() {
    let baseline = serde_json::to_value(request("inspect", BASIC)).unwrap();
    let cases = [
        (
            "/protocolVersion",
            json!("braid.ferrule-host.v999"),
            "unsupportedProtocolVersion",
        ),
        ("/operation", json!("preview"), "unsupportedOperation"),
        (
            "/operation",
            json!("execute-command"),
            "unsupportedOperation",
        ),
        ("/requestId", json!("not-a-uuid"), "invalidContext"),
        (
            "/revision/organizationId",
            json!("00000000-0000-0000-0000-000000000000"),
            "invalidContext",
        ),
        (
            "/revision/projectId",
            json!("AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA"),
            "invalidContext",
        ),
        ("/revision/mappingId", json!(""), "invalidContext"),
        (
            "/revision/revisionId",
            json!("55555555-5555-4555-8555-55555555555"),
            "invalidContext",
        ),
        (
            "/documentId",
            json!("../entry.native.json"),
            "invalidContext",
        ),
        ("/nativeDigest", json!("0".repeat(64)), "digestMismatch"),
        ("/nativeDigest", json!("a".repeat(63)), "invalidContext"),
        ("/nativeDigest", json!("A".repeat(64)), "invalidContext"),
        ("/limits/maximumDiagnostics", json!(0), "invalidLimits"),
        ("/limits/maximumDiagnostics", json!(129), "invalidLimits"),
        ("/limits/maximumItems", json!(0), "invalidLimits"),
        ("/limits/maximumItems", json!(8193), "invalidLimits"),
        ("/limits/maximumTextBytes", json!(63), "invalidLimits"),
        ("/limits/maximumTextBytes", json!(2049), "invalidLimits"),
    ];
    for (pointer, changed, expected) in cases {
        let mut header = baseline.clone();
        *header.pointer_mut(pointer).unwrap() = changed;
        let response = stream(&framed_bytes(&serde_json::to_vec(&header).unwrap(), BASIC));
        assert_eq!(
            failure(&response),
            expected,
            "mutation at {pointer}: {response:#}"
        );
    }
    assert_eq!(baseline["protocolVersion"], PROTOCOL_VERSION);
}

#[test]
fn malformed_native_and_unsupported_native_envelopes_return_typed_failures() {
    for native in [b"{".as_slice(), &[0xff][..]] {
        let header = request("validate", native);
        assert_eq!(failure(&process(&header, native)), "nativeDecodeFailed");
    }
    let mut envelope: Value = serde_json::from_slice(EXACT).unwrap();
    envelope["__ferrule_file"]["version"] = json!(999);
    let native = serde_json::to_vec(&envelope).unwrap();
    let header = request("validate", &native);
    assert_eq!(
        failure(&process(&header, &native)),
        "unsupportedNativeVersion"
    );
}

#[test]
fn altered_or_missing_authoritative_float_metadata_is_not_silently_repaired() {
    let baseline: Value = serde_json::from_slice(EXACT).unwrap();
    let mut decimal_changed = baseline.clone();
    decimal_changed["document"]["graph"]["nodes"]["3"]["value"] = json!(0.0);
    let mut float_table_missing = baseline.clone();
    float_table_missing["__ferrule_file"]["float_bits"]
        .as_object_mut()
        .unwrap()
        .remove("/graph/nodes/2/value");
    let mut wrong_kind = baseline;
    wrong_kind["__ferrule_file"]["kind"] = json!("pipeline");
    for envelope in [decimal_changed, float_table_missing, wrong_kind] {
        let native = serde_json::to_vec(&envelope).unwrap();
        let header = request("inspect", &native);
        let response = process(&header, &native);
        assert_eq!(failure(&response), "nativeDecodeFailed");
        assert_eq!(response["failure"]["nativeLocator"]["kind"], "document");
        assert_eq!(
            response["failure"]["nativeLocator"]["documentId"],
            "entry.native.json"
        );
    }
}

#[test]
fn input_and_traversal_limits_fail_without_partial_inventory() {
    let oversized = vec![b' '; MAX_NATIVE_BYTES + 1];
    let header = request("inspect", &oversized);
    assert!(matches!(
        failure(&process(&header, &oversized)),
        "invalidFrame" | "inspectionLimitExceeded"
    ));
    let mut header = request("inspect", BASIC);
    header.limits.maximum_items = 1;
    assert_eq!(failure(&process(&header, BASIC)), "inspectionLimitExceeded");
}

#[test]
fn text_limits_preserve_utf8_and_report_truncation() {
    let mut native: Value = serde_json::from_slice(BASIC).unwrap();
    native["source"]["name"] = json!("é".repeat(70));
    let bytes = serde_json::to_vec(&native).unwrap();
    let mut header = request("inspect", &bytes);
    header.limits.maximum_text_bytes = 65;
    let response = process(&header, &bytes);
    success(&response, &header, "inspect");
    let boundary = &response["result"]["boundaries"][0];
    let name = boundary["rootName"].as_str().unwrap();
    assert_eq!(name.len(), 64);
    assert_eq!(name, "é".repeat(32));
    assert_eq!(boundary["rootNameTruncated"], true);
    assert_eq!(response["result"]["truncated"], true);
}

#[test]
fn bounded_native_locators_preserve_exact_pointers_or_use_document_fallback() {
    use braid_ferrule_host::protocol::{NativeLocator, bounded_locator};
    let document = NativeLocator::Document {
        document_id: "entry.native.json".into(),
    };
    for pointer in [
        format!("/{}", "x".repeat(63)),
        format!("/{}x", "é".repeat(31)),
    ] {
        assert_eq!(pointer.len(), 64);
        let locator = NativeLocator::JsonPointer {
            document_id: "entry.native.json".into(),
            pointer,
        };
        assert_eq!(bounded_locator(locator.clone(), 64), locator);
    }
    for pointer in [
        format!("/{}", "x".repeat(64)),
        format!("/{}", "é".repeat(32)),
    ] {
        assert_eq!(pointer.len(), 65);
        let locator = NativeLocator::JsonPointer {
            document_id: "entry.native.json".into(),
            pointer,
        };
        assert_eq!(bounded_locator(locator, 64), document);
    }
    let graph = NativeLocator::GraphNode {
        document_id: "entry.native.json".into(),
        node_id: "4294967295".into(),
        function_id: Some("18446744073709551615".into()),
    };
    assert_eq!(bounded_locator(graph.clone(), 64), graph);
    assert_eq!(bounded_locator(document.clone(), 64), document);
}

#[test]
fn inspect_uses_document_fallback_for_long_resource_pointers() {
    let mut document: Value = serde_json::from_slice(BASIC).unwrap();
    document["extra_sources"] = json!([{
        "name":"reference",
        "path":"do-not-read.json",
        "schema":document["source"],
        "options":{"json_schema_unresolved_reference":"https://braid-fixture.invalid/do-not-fetch-schema.json"}
    }]);
    let native = serde_json::to_vec(&json!({"__ferrule_file":{"kind":"project","version":2,"float_bits":{}},"document":document})).unwrap();
    let mut header = request("inspect", &native);
    header.limits.maximum_text_bytes = 64;
    let response = process(&header, &native);
    success(&response, &header, "inspect");
    let result = &response["result"];
    let resource = result["unresolvedResources"]
        .as_array()
        .unwrap()
        .iter()
        .find(|resource| resource["kind"] == "schemaReference")
        .unwrap();
    assert_eq!(
        resource["nativeLocator"],
        json!({"kind":"document","documentId":"entry.native.json"})
    );
    let retained: Value = serde_json::from_slice(&native).unwrap();
    for boundary in result["boundaries"].as_array().unwrap() {
        let pointer = boundary["nativeLocator"]["pointer"].as_str().unwrap();
        assert!(pointer.len() <= 64);
        assert!(retained.pointer(pointer).is_some());
    }
}

#[test]
fn direct_inspection_bounds_both_boundary_and_resource_native_locators() {
    let project = ferrule_mapping::project_file::decode_bytes(RESOURCES).unwrap();
    let mut header = request("inspect", RESOURCES);
    header.limits.maximum_text_bytes = 64;
    let prefix = format!("/{}", "x".repeat(64));
    let inventory = braid_ferrule_host::inspect::inspect(
        &project,
        "entry.native.json",
        &prefix,
        &header.limits,
    )
    .unwrap();
    let inventory = serde_json::to_value(inventory).unwrap();
    for locator in inventory["boundaries"]
        .as_array()
        .unwrap()
        .iter()
        .chain(inventory["unresolvedResources"].as_array().unwrap())
        .map(|entry| &entry["nativeLocator"])
    {
        assert_eq!(
            locator,
            &json!({"kind":"document","documentId":"entry.native.json"})
        );
    }
}

#[test]
fn bounded_response_serialization_checks_the_exact_byte_boundary() {
    let header = request("inspect", BASIC);
    let response = braid_ferrule_host::process_request(&header, BASIC);
    let expected = serde_json::to_vec(&response).unwrap();
    assert_eq!(
        braid_ferrule_host::framing::encode_response(&response, expected.len()).unwrap(),
        expected
    );
    assert!(braid_ferrule_host::framing::encode_response(&response, expected.len() - 1).is_err());
    assert!(braid_ferrule_host::framing::encode_response(&response, 0).is_err());
}

#[test]
fn oversized_worker_results_become_bounded_structured_failures() {
    let header = request("inspect", BASIC);
    let oversized = Response::failure(
        Some(&header),
        "testOversizedResult",
        &"x".repeat(MAX_RESPONSE_BYTES + 1),
    );
    assert!(
        braid_ferrule_host::framing::encode_response(&oversized, MAX_RESPONSE_BYTES * 2).is_err(),
        "the caller cannot increase the compiled hard cap"
    );
    let mut bytes = Vec::new();
    braid_ferrule_host::framing::write_response(&mut bytes, &oversized).unwrap();
    let response = response_frame(&bytes);
    assert_eq!(failure(&response), "resultLimitExceeded");
    assert_eq!(response["requestId"], header.request_id);
    assert_eq!(response["nativeDigest"], header.native_digest);
    assert!(bytes.len() < 2048, "fallback response remains small");
}
