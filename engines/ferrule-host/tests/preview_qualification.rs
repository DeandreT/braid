mod support;

use std::io::{Cursor, Write};
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};

use serde_json::{Value, json};
use support::{BASIC, INVALID, digest};
use weft_ferrule_host::preview::protocol::{
    MAX_PREVIEW_INPUT_BYTES, MAX_PREVIEW_OUTPUT_BYTES, PREVIEW_PROFILE, PREVIEW_PROTOCOL_VERSION,
    PreviewInput, PreviewLimits, PreviewParameter, PreviewRequestHeader, PreviewTarget,
    PreviewValue,
};
use weft_ferrule_host::protocol::{ENGINE_REVISION, Revision};

const ORDERS: &[u8] = include_bytes!("fixtures/preview-orders.json");
const CSV_SOURCE: &[u8] = include_bytes!("fixtures/preview-csv-source.json");
const ORDER: &[u8] =
    br#"{"id":"SO-1","lines":[{"sku":"A","qty":2},{"sku":"B","qty":9007199254740993}]}"#;
const RATES: &[u8] = br#"{"currency":"EUR"}"#;
const CLOCK: &str = "2026-10-20T01:02:03.5+02:00";

fn parameters() -> Vec<PreviewParameter> {
    vec![
        PreviewParameter {
            name: "region".into(),
            value: PreviewValue::String("west".into()),
        },
        PreviewParameter {
            name: "express".into(),
            value: PreviewValue::Boolean(true),
        },
    ]
}

fn header(
    native: &[u8],
    named: &[(&str, &[u8])],
    primary: &[u8],
    target: PreviewTarget,
    parameters: Vec<PreviewParameter>,
) -> PreviewRequestHeader {
    let mut inputs = vec![PreviewInput::Primary {
        byte_length: primary.len() as u32,
        sha256: digest(primary),
    }];
    inputs.extend(named.iter().map(|(name, bytes)| PreviewInput::Named {
        name: (*name).into(),
        byte_length: bytes.len() as u32,
        sha256: digest(bytes),
    }));
    PreviewRequestHeader {
        protocol_version: PREVIEW_PROTOCOL_VERSION.into(),
        request_id: "11111111-1111-4111-8111-111111111111".into(),
        revision: Revision {
            organization_id: "22222222-2222-4222-8222-222222222222".into(),
            project_id: "33333333-3333-4333-8333-333333333333".into(),
            mapping_id: "44444444-4444-4444-8444-444444444444".into(),
            revision_id: "55555555-5555-4555-8555-555555555555".into(),
        },
        native_digest: digest(native),
        document_id: "entry.native.json".into(),
        target,
        current_date_time: CLOCK.into(),
        parameters,
        inputs,
        limits: PreviewLimits {
            maximum_input_bytes: MAX_PREVIEW_INPUT_BYTES as u32,
            maximum_output_bytes: MAX_PREVIEW_OUTPUT_BYTES as u32,
            maximum_artifacts: 64,
            maximum_text_bytes: 2048,
        },
    }
}

fn frame(bytes: &mut Vec<u8>, body: &[u8]) {
    bytes.extend_from_slice(&(body.len() as u32).to_be_bytes());
    bytes.extend_from_slice(body);
}

fn request_bytes(header: &PreviewRequestHeader, native: &[u8], inputs: &[&[u8]]) -> Vec<u8> {
    let mut bytes = Vec::new();
    frame(&mut bytes, &serde_json::to_vec(header).unwrap());
    frame(&mut bytes, native);
    for input in inputs {
        frame(&mut bytes, input);
    }
    bytes
}

/// Parses the response header frame and exactly its declared output frames.
fn reply(output: &[u8]) -> (Value, Vec<Vec<u8>>) {
    let mut offset = 0;
    let mut next = || {
        let length = u32::from_be_bytes(output[offset..offset + 4].try_into().unwrap()) as usize;
        let body = output[offset + 4..offset + 4 + length].to_vec();
        offset += 4 + length;
        body
    };
    let response: Value = serde_json::from_slice(&next()).unwrap();
    let count = response["result"]["artifacts"]
        .as_array()
        .map_or(0, Vec::len);
    let outputs = (0..count).map(|_| next()).collect();
    assert_eq!(offset, output.len(), "no bytes beyond the declared frames");
    (response, outputs)
}

fn stream(bytes: &[u8]) -> (Value, Vec<Vec<u8>>) {
    let mut output = Vec::new();
    weft_ferrule_host::handle_stream(&mut Cursor::new(bytes), &mut output).unwrap();
    reply(&output)
}

fn preview(
    header: &PreviewRequestHeader,
    native: &[u8],
    inputs: &[&[u8]],
) -> (Value, Vec<Vec<u8>>) {
    stream(&request_bytes(header, native, inputs))
}

fn failure(response: &Value) -> (&str, Option<&str>) {
    assert!(response["result"].is_null(), "{response:#}");
    (
        response["failure"]["code"].as_str().unwrap(),
        response["failure"]["subject"].as_str(),
    )
}

/// The upstream host API with the same logical identities, parameters and clock.
fn direct(
    native: &[u8],
    primary: &[u8],
    named: &[(&str, &[u8])],
    target: Option<ferrule_engine::TargetSelection<'_>>,
) -> ferrule_cli::PayloadRunOutcome {
    let project = ferrule_mapping::project_file::decode_bytes(native).unwrap();
    let primary_path = PathBuf::from("/weft/input/primary.json");
    let paths = (0..named.len())
        .map(|index| PathBuf::from(format!("/weft/input/named-{}.json", index + 1)))
        .collect::<Vec<_>>();
    let named = named
        .iter()
        .zip(&paths)
        .map(|((name, bytes), path)| {
            ferrule_cli::NamedPayloadInput::new(
                name,
                ferrule_cli::PayloadDocument::new(path, bytes).unwrap(),
            )
            .unwrap()
        })
        .collect::<Vec<_>>();
    let mut parameters = ferrule_engine::RuntimeParameters::new();
    parameters
        .insert("region", ferrule_ir::Value::String("west".into()))
        .unwrap();
    parameters
        .insert("express", ferrule_ir::Value::Bool(true))
        .unwrap();
    let mut options = ferrule_cli::PayloadRunOptions::new(
        ferrule_cli::PayloadDocument::new(&primary_path, primary).unwrap(),
    )
    .with_extra_sources(&named)
    .with_output_path(Path::new("/weft/output/primary.json"))
    .with_runtime_parameters(&parameters);
    if let Some(target) = target {
        options = options.with_target(target);
    }
    let options = options.with_current_datetime(CLOCK).unwrap();
    ferrule_cli::run_project_value_payloads(
        &project,
        Path::new("/weft/entry.native.json"),
        &options,
    )
    .unwrap()
}

#[test]
fn preview_outputs_match_the_direct_payload_api_and_independent_expectations() {
    let named = [("rates", RATES)];
    let request = header(ORDERS, &named, ORDER, PreviewTarget::All, parameters());
    let (response, outputs) = preview(&request, ORDERS, &[ORDER, RATES]);
    assert!(response["failure"].is_null(), "{response:#}");
    assert_eq!(response["protocolVersion"], PREVIEW_PROTOCOL_VERSION);
    assert_eq!(response["profile"], PREVIEW_PROFILE);
    assert_eq!(response["engineRevision"], ENGINE_REVISION);
    assert_eq!(response["requestId"], request.request_id);
    assert_eq!(response["nativeDigest"], request.native_digest);
    assert_eq!(response["documentId"], "entry.native.json");

    let artifacts = response["result"]["artifacts"].as_array().unwrap();
    assert_eq!(
        artifacts
            .iter()
            .map(|artifact| (
                artifact["boundaryId"].as_str().unwrap(),
                artifact["targetName"].as_str().unwrap()
            ))
            .collect::<Vec<_>>(),
        [
            ("primary-output", "Shipment"),
            ("named-output:0", "summary")
        ]
    );
    let expected = [
        json!({
            "orderId": "SO-1",
            "region": "west",
            "generatedAt": CLOCK,
            "currency": "EUR",
            "express": true,
            "items": [{"sku": "A", "quantity": 2}, {"sku": "B", "quantity": 9007199254740993_i64}]
        }),
        json!({"lineCount": 2, "region": "west"}),
    ];
    let upstream = direct(ORDERS, ORDER, &named, None);
    assert_eq!(upstream.artifacts.len(), 2);
    for (((artifact, output), expected), upstream) in artifacts
        .iter()
        .zip(&outputs)
        .zip(&expected)
        .zip(&upstream.artifacts)
    {
        assert_eq!(&serde_json::from_slice::<Value>(output).unwrap(), expected);
        assert_eq!(
            output, &upstream.bytes,
            "worker framing preserves upstream bytes"
        );
        assert_eq!(artifact["byteLength"], output.len());
        assert_eq!(artifact["sha256"], digest(output));
        assert_eq!(artifact["mediaType"], "application/json");
        assert_eq!(artifact["recordsWritten"], upstream.records_written);
    }
    assert!(
        std::str::from_utf8(&outputs[0])
            .unwrap()
            .contains("9007199254740993")
    );
    assert_eq!(
        response["result"]["recordsWritten"],
        upstream.records_written
    );

    let (again, repeated) = preview(&request, ORDERS, &[ORDER, RATES]);
    assert_eq!(again, response);
    assert_eq!(repeated, outputs);
}

#[test]
fn target_selection_returns_only_the_selected_static_target_and_its_required_inputs() {
    let named = header(
        ORDERS,
        &[],
        ORDER,
        PreviewTarget::Named {
            name: "summary".into(),
        },
        parameters(),
    );
    let (response, outputs) = preview(&named, ORDERS, &[ORDER]);
    let artifacts = response["result"]["artifacts"].as_array().unwrap();
    assert_eq!(artifacts.len(), 1);
    assert_eq!(artifacts[0]["boundaryId"], "named-output:0");
    assert_eq!(
        serde_json::from_slice::<Value>(&outputs[0]).unwrap(),
        json!({"lineCount": 2, "region": "west"})
    );
    let upstream = direct(
        ORDERS,
        ORDER,
        &[],
        Some(ferrule_engine::TargetSelection::Named("summary")),
    );
    assert_eq!(outputs[0], upstream.artifacts[0].bytes);

    let extra = header(
        ORDERS,
        &[("rates", RATES)],
        ORDER,
        PreviewTarget::Named {
            name: "summary".into(),
        },
        parameters(),
    );
    assert_eq!(
        failure(&preview(&extra, ORDERS, &[ORDER, RATES]).0),
        ("unexpectedInput", Some("rates"))
    );

    let primary = header(
        ORDERS,
        &[("rates", RATES)],
        ORDER,
        PreviewTarget::Primary,
        parameters(),
    );
    let (response, outputs) = preview(&primary, ORDERS, &[ORDER, RATES]);
    assert_eq!(
        response["result"]["artifacts"][0]["boundaryId"],
        "primary-output"
    );
    assert_eq!(outputs.len(), 1);
    assert_eq!(
        outputs[0],
        direct(
            ORDERS,
            ORDER,
            &[("rates", RATES)],
            Some(ferrule_engine::TargetSelection::Primary)
        )
        .artifacts[0]
            .bytes
    );

    let unknown = header(
        ORDERS,
        &[],
        ORDER,
        PreviewTarget::Named {
            name: "missing".into(),
        },
        parameters(),
    );
    assert_eq!(
        failure(&preview(&unknown, ORDERS, &[ORDER]).0),
        ("unknownTarget", Some("missing"))
    );
}

#[test]
fn missing_unknown_and_invalid_inputs_parameters_and_clocks_are_structured() {
    let missing = header(ORDERS, &[], ORDER, PreviewTarget::All, parameters());
    assert_eq!(
        failure(&preview(&missing, ORDERS, &[ORDER]).0),
        ("missingInput", Some("rates"))
    );

    let mut unknown = parameters();
    unknown.push(PreviewParameter {
        name: "undeclared".into(),
        value: PreviewValue::Integer("1".into()),
    });
    let request = header(
        ORDERS,
        &[("rates", RATES)],
        ORDER,
        PreviewTarget::All,
        unknown,
    );
    assert_eq!(
        failure(&preview(&request, ORDERS, &[ORDER, RATES]).0),
        ("unknownParameter", Some("undeclared"))
    );

    let request = header(
        ORDERS,
        &[("rates", RATES)],
        ORDER,
        PreviewTarget::All,
        parameters()[1..].to_vec(),
    );
    assert_eq!(
        failure(&preview(&request, ORDERS, &[ORDER, RATES]).0),
        ("missingParameter", Some("region"))
    );

    for value in [
        PreviewValue::Integer("01".into()),
        PreviewValue::Integer("9223372036854775808".into()),
        PreviewValue::Float("NaN".into()),
        PreviewValue::Float("1e999".into()),
        PreviewValue::Float(".5".into()),
    ] {
        let mut invalid = parameters();
        invalid[0].value = value;
        let request = header(
            ORDERS,
            &[("rates", RATES)],
            ORDER,
            PreviewTarget::All,
            invalid,
        );
        assert_eq!(
            failure(&preview(&request, ORDERS, &[ORDER, RATES]).0),
            ("invalidParameter", Some("region"))
        );
    }

    let malformed: &[u8] = b"{\"id\":";
    let request = header(
        ORDERS,
        &[("rates", RATES)],
        malformed,
        PreviewTarget::All,
        parameters(),
    );
    assert_eq!(
        failure(&preview(&request, ORDERS, &[malformed, RATES]).0),
        ("invalidInput", Some("primary"))
    );
    let request = header(
        ORDERS,
        &[("rates", malformed)],
        ORDER,
        PreviewTarget::All,
        parameters(),
    );
    assert_eq!(
        failure(&preview(&request, ORDERS, &[ORDER, malformed]).0),
        ("invalidInput", Some("rates"))
    );

    for clock in [
        "2026-10-20 01:02:03Z",
        "2026-10-20T01:02:03",
        "2026-02-30T00:00:00Z",
        "2026-10-20T01:02:60Z",
    ] {
        let mut request = header(
            ORDERS,
            &[("rates", RATES)],
            ORDER,
            PreviewTarget::All,
            parameters(),
        );
        request.current_date_time = clock.into();
        assert_eq!(
            failure(&preview(&request, ORDERS, &[ORDER, RATES]).0),
            ("invalidClock", None)
        );
    }
}

#[test]
fn non_json_resource_bearing_and_invalid_mappings_are_refused_before_parsing_inputs() {
    let not_json: &[u8] = b"code,name\nA,B\n";
    let request = header(CSV_SOURCE, &[], not_json, PreviewTarget::All, Vec::new());
    assert_eq!(
        failure(&preview(&request, CSV_SOURCE, &[not_json]).0),
        ("unsupportedCapability", Some("primary"))
    );

    for (field, options, subject) in [
        (
            "target_options",
            json!({"xml_document": true}),
            "primary-output",
        ),
        (
            "source_options",
            json!({"json_document": true, "json_schema_unresolved_reference": "https://example.invalid/schema.json"}),
            "primary",
        ),
    ] {
        let mut project: Value = serde_json::from_slice(BASIC).unwrap();
        project[field] = options;
        let native = serde_json::to_vec(&project).unwrap();
        let request = header(&native, &[], not_json, PreviewTarget::All, Vec::new());
        assert_eq!(
            failure(&preview(&request, &native, &[not_json]).0),
            ("unsupportedCapability", Some(subject))
        );
    }

    let request = header(INVALID, &[], not_json, PreviewTarget::All, Vec::new());
    assert_eq!(
        failure(&preview(&request, INVALID, &[not_json]).0),
        ("invalidMapping", None)
    );

    let input: &[u8] = br#"{"code":"A1","a.b":"dot","nested":{"b":"x"}}"#;
    let request = header(BASIC, &[], input, PreviewTarget::All, Vec::new());
    let (response, outputs) = preview(&request, BASIC, &[input]);
    assert!(response["failure"].is_null(), "{response:#}");
    assert_eq!(
        serde_json::from_slice::<Value>(&outputs[0]).unwrap(),
        json!({"code": "A1", "dotted": "dot", "nestedOutput": {"b": "x"}})
    );
}

#[test]
fn limits_digests_and_declarations_are_enforced_without_partial_outputs() {
    let named = [("rates", RATES)];
    let mut request = header(ORDERS, &named, ORDER, PreviewTarget::All, parameters());
    request.limits.maximum_output_bytes = 32;
    assert_eq!(
        failure(&preview(&request, ORDERS, &[ORDER, RATES]).0),
        ("resultLimitExceeded", None)
    );
    let mut request = header(ORDERS, &named, ORDER, PreviewTarget::All, parameters());
    request.limits.maximum_artifacts = 1;
    assert_eq!(
        failure(&preview(&request, ORDERS, &[ORDER, RATES]).0),
        ("resultLimitExceeded", None)
    );
    let mut request = header(ORDERS, &named, ORDER, PreviewTarget::All, parameters());
    request.limits.maximum_input_bytes = (ORDER.len() + RATES.len() - 1) as u32;
    assert_eq!(
        failure(&preview(&request, ORDERS, &[ORDER, RATES]).0),
        ("inputLimitExceeded", None)
    );
    for limits in [
        (0, 1, 1, 64),
        (MAX_PREVIEW_INPUT_BYTES as u32 + 1, 1, 1, 64),
        (1, MAX_PREVIEW_OUTPUT_BYTES as u32 + 1, 1, 64),
        (1, 1, 65, 64),
        (1, 1, 1, 63),
    ] {
        let mut request = header(ORDERS, &named, ORDER, PreviewTarget::All, parameters());
        request.limits = PreviewLimits {
            maximum_input_bytes: limits.0,
            maximum_output_bytes: limits.1,
            maximum_artifacts: limits.2,
            maximum_text_bytes: limits.3,
        };
        assert_eq!(
            failure(&preview(&request, ORDERS, &[ORDER, RATES]).0),
            ("invalidLimits", None)
        );
    }

    let changed: &[u8] = br#"{"currency":"USD"}"#;
    let request = header(ORDERS, &named, ORDER, PreviewTarget::All, parameters());
    assert_eq!(
        failure(&preview(&request, ORDERS, &[ORDER, changed]).0),
        ("digestMismatch", Some("rates"))
    );
    let mut request = header(ORDERS, &named, ORDER, PreviewTarget::All, parameters());
    request.native_digest = digest(BASIC);
    assert_eq!(
        failure(&preview(&request, ORDERS, &[ORDER, RATES]).0),
        ("digestMismatch", None)
    );

    let mut duplicate = header(ORDERS, &named, ORDER, PreviewTarget::All, parameters());
    duplicate.inputs.push(duplicate.inputs[1].clone());
    assert_eq!(
        failure(&preview(&duplicate, ORDERS, &[ORDER, RATES, RATES]).0),
        ("invalidRequest", Some("rates"))
    );
    let mut reordered = header(ORDERS, &named, ORDER, PreviewTarget::All, parameters());
    reordered.inputs.swap(0, 1);
    assert_eq!(
        failure(&preview(&reordered, ORDERS, &[RATES, ORDER]).0),
        ("invalidRequest", None)
    );
}

#[test]
fn preview_frames_are_exact_and_unusable_headers_never_execute() {
    let named = [("rates", RATES)];
    let request = header(ORDERS, &named, ORDER, PreviewTarget::All, parameters());
    let good = request_bytes(&request, ORDERS, &[ORDER, RATES]);

    let mut trailing = good.clone();
    trailing.push(0);
    assert_eq!(failure(&stream(&trailing).0), ("invalidFrame", None));
    assert_eq!(
        failure(&stream(&good[..good.len() - 1]).0),
        ("invalidFrame", None)
    );
    let short = request_bytes(&request, ORDERS, &[ORDER, &RATES[..RATES.len() - 1]]);
    assert_eq!(failure(&stream(&short).0), ("invalidFrame", None));

    let mut value = serde_json::to_value(&request).unwrap();
    value["unexpected"] = json!(true);
    let mut bytes = Vec::new();
    frame(&mut bytes, &serde_json::to_vec(&value).unwrap());
    frame(&mut bytes, ORDERS);
    let (response, outputs) = stream(&bytes);
    assert_eq!(failure(&response), ("invalidHeader", None));
    assert!(response["requestId"].is_null());
    assert!(outputs.is_empty());

    let mut context = request.clone();
    context.document_id = "../entry.json".into();
    assert_eq!(
        failure(&preview(&context, ORDERS, &[ORDER, RATES]).0),
        ("invalidContext", None)
    );
}

#[test]
fn executable_streams_preview_frames_on_stdout_only() {
    let named = [("rates", RATES)];
    let request = header(ORDERS, &named, ORDER, PreviewTarget::All, parameters());
    let bytes = request_bytes(&request, ORDERS, &[ORDER, RATES]);
    let mut worker = Command::new(env!("CARGO_BIN_EXE_weft-ferrule-host"))
        .env_clear()
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .unwrap();
    worker.stdin.take().unwrap().write_all(&bytes).unwrap();
    let output = worker.wait_with_output().unwrap();
    assert!(output.status.success());
    assert!(output.stderr.is_empty());
    let (response, outputs) = reply(&output.stdout);
    assert!(response["failure"].is_null(), "{response:#}");
    assert_eq!(outputs.len(), 2);
}
