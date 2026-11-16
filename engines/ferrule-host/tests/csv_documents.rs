//! Self-authored boundary fixtures, intentionally independent of Ferrule's corpus.
mod support;

use serde_json::{Value, json};
use braid_ferrule_host::preview::protocol::{
    CSV_PREVIEW_PROTOCOL_VERSION, DOCUMENT_PREVIEW_PROTOCOL_VERSION, MAX_PREVIEW_INPUT_BYTES,
    MAX_PREVIEW_OUTPUT_BYTES, PreviewInput, PreviewLimits, PreviewRequestHeader, PreviewTarget,
};
use braid_ferrule_host::preview::{PreviewOutcome, process_preview};

const NATIVE: &[u8] = include_bytes!("fixtures/csv-flat.native.json");

fn header(native: &[u8], input: &[u8]) -> PreviewRequestHeader {
    let context = support::request("inspect", native);
    PreviewRequestHeader {
        protocol_version: CSV_PREVIEW_PROTOCOL_VERSION.into(),
        request_id: context.request_id,
        revision: context.revision,
        native_digest: support::digest(native),
        document_id: "entry.native.json".into(),
        target: PreviewTarget::All,
        current_date_time: "2026-10-30T12:00:00Z".into(),
        parameters: vec![],
        inputs: vec![PreviewInput::Primary {
            byte_length: input.len() as u32,
            sha256: support::digest(input),
        }],
        limits: PreviewLimits {
            maximum_input_bytes: MAX_PREVIEW_INPUT_BYTES as u32,
            maximum_output_bytes: MAX_PREVIEW_OUTPUT_BYTES as u32,
            maximum_artifacts: 64,
            maximum_text_bytes: 2048,
        },
    }
}

fn preview(native: &[u8], input: &[u8]) -> PreviewOutcome {
    process_preview(&header(native, input), native, &[input.to_vec()])
}

#[test]
fn csv_bytes_bom_multiline_and_positional_headers_use_explicit_saved_options() {
    let input = b"\xef\xbb\xbfunused,labels\r\nA,\"line 1\r\nline 2,\"\"quoted\"\"\"\r\n";
    let result = preview(NATIVE, input);
    assert!(
        result.response.failure.is_none(),
        "{:?}",
        result.response.failure
    );
    assert_eq!(
        result.outputs,
        [b"code,description\nA,\"line 1\r\nline 2,\"\"quoted\"\"\"\n".to_vec()]
    );
    let artifact = &result.response.result.unwrap().artifacts[0];
    assert_eq!(artifact.media_type, "text/csv");
    assert_eq!(artifact.csv_dialect.as_ref().unwrap().delimiter, ',');
}

#[test]
fn tsv_identity_and_custom_quotes_are_settings_not_extensions() {
    let mut native: Value = serde_json::from_slice(NATIVE).unwrap();
    for side in ["source_options", "target_options"] {
        native[side]["delimiter"] = json!("\t");
        native[side]["csv_quote"] = json!("'");
        native[side]["csv_utf8_bom"] = json!(true);
    }
    let native = serde_json::to_vec(&native).unwrap();
    let result = preview(&native, b"ignored\theader\nA\t'one\ttwo'\n");
    assert!(
        result.response.failure.is_none(),
        "{:?}",
        result.response.failure
    );
    assert_eq!(
        result.outputs,
        [b"\xef\xbb\xbfcode\tdescription\nA\t'one\ttwo'\n".to_vec()]
    );
    let artifact = &result.response.result.unwrap().artifacts[0];
    assert_eq!(artifact.media_type, "text/tab-separated-values");
    assert_eq!(artifact.csv_dialect.as_ref().unwrap().quote, Some('\''));
}

#[test]
fn empty_document_is_present_but_missing_frame_is_not() {
    let mut native: Value = serde_json::from_slice(NATIVE).unwrap();
    for side in ["source_options", "target_options"] {
        native[side]["has_header_row"] = json!(false);
    }
    let native = serde_json::to_vec(&native).unwrap();
    let result = preview(&native, b"");
    assert!(
        result.response.failure.is_none(),
        "{:?}",
        result.response.failure
    );
    assert_eq!(result.outputs, [Vec::<u8>::new()]);
    let missing = process_preview(&header(&native, b""), &native, &[]);
    assert_eq!(missing.response.failure.unwrap().code, "invalidFrame");
    let mut legacy = header(&native, b"");
    legacy.protocol_version = DOCUMENT_PREVIEW_PROTOCOL_VERSION.into();
    assert_eq!(
        process_preview(&legacy, &native, &[vec![]])
            .response
            .failure
            .unwrap()
            .code,
        "invalidRequest"
    );
}

#[test]
fn csv_does_not_expand_legacy_document_admission() {
    let mut request = header(NATIVE, b"code,description\nA,B\n");
    request.protocol_version = DOCUMENT_PREVIEW_PROTOCOL_VERSION.into();
    let result = process_preview(&request, NATIVE, &[b"code,description\nA,B\n".to_vec()]);
    assert_eq!(
        result.response.failure.unwrap().code,
        "unsupportedCapability"
    );
}

#[test]
fn absent_trailing_cell_and_present_empty_cell_keep_distinct_native_values() {
    use ferrule_ir::{Instance, Value as NativeValue};
    let project = braid_ferrule_host::decode::decode_native(NATIVE)
        .unwrap()
        .project;
    let options = ferrule_format_csv::CsvReadOptions {
        has_headers: false,
        preserve_empty_strings: true,
        ..Default::default()
    };
    let rows =
        ferrule_format_csv::from_bytes_with_options_bounded(b"A,\nA\n", &project.source, &options)
            .unwrap();
    let row = |description| {
        Instance::Group(
            vec![
                (
                    "code".into(),
                    Instance::Scalar(NativeValue::String("A".into())),
                ),
                ("description".into(), Instance::Scalar(description)),
            ]
            .into(),
        )
    };
    assert_eq!(
        rows,
        vec![row(NativeValue::String("".into())), row(NativeValue::Null)]
    );
    let collapsed = ferrule_format_csv::from_bytes_with_options_bounded(
        b"A,\nA\n",
        &project.source,
        &ferrule_format_csv::CsvReadOptions {
            preserve_empty_strings: false,
            ..options
        },
    )
    .unwrap();
    assert_eq!(
        collapsed,
        vec![row(NativeValue::Null), row(NativeValue::Null)]
    );
}

#[test]
fn header_only_and_zero_byte_input_are_valid_zero_row_documents() {
    for input in [b"".as_slice(), b"ignored,header\r\n".as_slice()] {
        let result = preview(NATIVE, input);
        assert!(result.response.failure.is_none());
        assert_eq!(result.outputs, [b"code,description\n".to_vec()]);
    }
}

#[test]
fn malformed_utf8_and_input_or_output_budget_failure_never_publish_partial_rows() {
    let result = preview(NATIVE, b"code,description\nA,\xff");
    assert_eq!(result.response.failure.unwrap().code, "invalidInput");
    assert!(result.outputs.is_empty());
    let input = b"code,description\nA,B\n";
    let mut request = header(NATIVE, input);
    request.limits.maximum_output_bytes = 1;
    let result = process_preview(&request, NATIVE, &[input.to_vec()]);
    assert_eq!(result.response.failure.unwrap().code, "resultLimitExceeded");
    assert!(result.outputs.is_empty());
    let huge = format!("code,description\nA,{}", "X".repeat(1024 * 1024 + 1));
    assert_eq!(
        preview(NATIVE, huge.as_bytes())
            .response
            .failure
            .unwrap()
            .code,
        "inputLimitExceeded"
    );
}

#[test]
fn named_mixed_repeating_and_conflicting_plans_refuse_before_input_parsing() {
    for change in [
        "named",
        "mixed",
        "repeating",
        "conflicting",
        "missingIdentity",
    ] {
        let mut value: Value = serde_json::from_slice(NATIVE).unwrap();
        match change {
            "named" => {
                value["extra_targets"] = json!([{"name":"extra","schema":value["target"],"root":value["root"],"options":value["target_options"]}])
            }
            "mixed" => value["target_options"] = json!({"json_document":true}),
            "repeating" => value["source"]["repeating"] = json!(true),
            "conflicting" => value["source_options"]["json_document"] = json!(true),
            _ => value["source_options"].as_object_mut().unwrap().clear(),
        }
        let native = serde_json::to_vec(&value).unwrap();
        let result = preview(&native, b"\xff");
        assert!(matches!(
            result.response.failure.unwrap().code.as_str(),
            "unsupportedCapability" | "invalidMapping"
        ));
        assert!(result.outputs.is_empty());
    }
}

#[test]
fn generated_csv_uses_real_pinned_companions_and_host_context_overload() {
    let context = support::request("inspect", NATIVE);
    let request = braid_ferrule_host::build::Request {
        protocol_version: braid_ferrule_host::build::PROTOCOL.into(),
        request_id: context.request_id,
        revision: context.revision,
        native_digest: support::digest(NATIVE),
        intent_digest: "a".repeat(64),
        operation: "compile".into(),
        profile: braid_ferrule_host::build::CSV_PROFILE.into(),
    };
    let (boundaries, files) = braid_ferrule_host::build::generate(&request, NATIVE).unwrap();
    assert!(boundaries.iter().all(|boundary| boundary.format == "csv"));
    for name in [
        "GeneratedMapping.Csv.cs",
        "GeneratedMapping.CsvDocument.cs",
        "Runtime/FerruleCsvInput.cs",
        "Runtime/FerruleCsvSource.cs",
    ] {
        assert!(
            files.iter().any(|file| file.path.as_str() == name),
            "{name}"
        );
    }
    let companion = files
        .iter()
        .find(|file| file.path.as_str() == "GeneratedMapping.CsvDocument.cs")
        .unwrap();
    let text = std::str::from_utf8(&companion.contents).unwrap();
    assert!(text.contains("ExecuteCsvDocumentBytes(byte[] source, global::Ferrule.Runtime.FerruleExecutionContext executionContext)"));
    assert!(!text.contains("ExecuteDocumentBytesOutputsWithHost"));
}
