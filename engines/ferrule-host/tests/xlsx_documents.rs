//! Self-authored workbooks, intentionally independent of Ferrule's corpus.
mod support;

use std::io::{Cursor, Read, Write};

use braid_ferrule_host::preview::protocol::{
    MAX_PREVIEW_INPUT_BYTES, MAX_PREVIEW_OUTPUT_BYTES, PreviewInput, PreviewLimits,
    PreviewRequestHeader, PreviewTarget, XLSX_PREVIEW_PROTOCOL_VERSION,
};
use braid_ferrule_host::preview::{PreviewOutcome, process_preview};
use braid_ferrule_host::xlsx_document;
use ferrule_ir::{Instance, Value};
use zip::{CompressionMethod, ZipArchive, ZipWriter, write::SimpleFileOptions};

const NATIVE: &[u8] = include_bytes!("fixtures/xlsx-flat.native.json");

#[test]
fn restricted_child_reads_a_workbook_to_json_but_refuses_the_workbook_writer() {
    use std::process::{Command, Stdio};
    let input = workbook("Orders", &[row("Order-7", 7)]);
    let mut project: serde_json::Value = serde_json::from_slice(NATIVE).unwrap();
    project["target_options"] = serde_json::json!({"json_document":true});
    let to_json = serde_json::to_vec(&project).unwrap();
    for (profile, mapping, bytes, expected_error) in [
        (
            xlsx_document::PROFILE,
            NATIVE,
            input.as_slice(),
            Some("unsupportedCapability"),
        ),
        (
            xlsx_document::READ_JSON_PROFILE,
            NATIVE,
            input.as_slice(),
            Some("unsupportedCapability"),
        ),
        (
            xlsx_document::READ_JSON_PROFILE,
            to_json.as_slice(),
            &b"not a workbook"[..],
            Some("invalidInput"),
        ),
        (
            xlsx_document::READ_JSON_PROFILE,
            to_json.as_slice(),
            input.as_slice(),
            None,
        ),
    ] {
        let mut request = header(mapping, bytes);
        request.protocol_version = profile.into();
        let mut frame = support::framed_bytes(&serde_json::to_vec(&request).unwrap(), mapping);
        frame.extend((bytes.len() as u32).to_be_bytes());
        frame.extend_from_slice(bytes);
        let mut child = Command::new(env!("CARGO_BIN_EXE_braid-ferrule-host"))
            .args([
                "--service-sandbox",
                "--cpu-seconds=30",
                "--memory-bytes=536870912",
                "--open-files=64",
            ])
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .spawn()
            .unwrap();
        child.stdin.take().unwrap().write_all(&frame).unwrap();
        let output = child.wait_with_output().unwrap();
        assert!(output.status.success());
        let reply = output.stdout.strip_prefix(b"BFS1").unwrap();
        let len = u32::from_be_bytes(reply[..4].try_into().unwrap()) as usize;
        let body: serde_json::Value = serde_json::from_slice(&reply[4..4 + len]).unwrap();
        let payload = &reply[4 + len..];
        assert_eq!(body["profile"], profile);
        if let Some(reason) = expected_error {
            assert_eq!(body["failure"]["code"], reason, "{body}");
            assert!(payload.is_empty());
        } else {
            assert!(body["failure"].is_null(), "{body}");
            assert_eq!(
                body["result"]["artifacts"][0]["mediaType"],
                "application/json"
            );
            assert_eq!(
                u32::from_be_bytes(payload[..4].try_into().unwrap()) as usize,
                payload.len() - 4
            );
            let rows: serde_json::Value = serde_json::from_slice(&payload[4..]).unwrap();
            assert_eq!(rows, serde_json::json!([{"code":"Order-7","quantity":7}]));
        }
    }
}

fn header(native: &[u8], input: &[u8]) -> PreviewRequestHeader {
    let context = support::request("inspect", native);
    PreviewRequestHeader {
        protocol_version: XLSX_PREVIEW_PROTOCOL_VERSION.into(),
        request_id: context.request_id,
        revision: context.revision,
        native_digest: support::digest(native),
        document_id: "entry.native.json".into(),
        target: PreviewTarget::Primary,
        current_date_time: "2026-11-07T12:00:00Z".into(),
        parameters: vec![],
        inputs: vec![PreviewInput::Primary {
            byte_length: input.len() as u32,
            sha256: support::digest(input),
        }],
        limits: PreviewLimits {
            maximum_input_bytes: MAX_PREVIEW_INPUT_BYTES as u32,
            maximum_output_bytes: MAX_PREVIEW_OUTPUT_BYTES as u32,
            maximum_artifacts: 1,
            maximum_text_bytes: 2048,
        },
    }
}

fn preview(native: &[u8], input: &[u8]) -> PreviewOutcome {
    process_preview(&header(native, input), native, &[input.to_vec()])
}

fn project() -> ferrule_mapping::Project {
    braid_ferrule_host::decode::decode_native(NATIVE)
        .unwrap()
        .project
}

fn row(code: &str, quantity: i64) -> Instance {
    Instance::Group(
        vec![
            (
                "code".to_string(),
                Instance::Scalar(Value::String(code.into())),
            ),
            (
                "quantity".to_string(),
                Instance::Scalar(Value::Int(quantity)),
            ),
        ]
        .into(),
    )
}

/// A source workbook with a title row above the saved header row.
fn workbook(sheet: &str, rows: &[Instance]) -> Vec<u8> {
    ferrule_format_xlsx::to_bytes_bounded(
        &project().source,
        rows,
        Some(sheet),
        2,
        &[],
        true,
        &ferrule_format_xlsx::XlsxLimits::default(),
    )
    .unwrap()
}

fn entries(bytes: &[u8]) -> Vec<(String, Vec<u8>)> {
    let mut archive = ZipArchive::new(Cursor::new(bytes)).unwrap();
    (0..archive.len())
        .map(|index| {
            let mut file = archive.by_index(index).unwrap();
            let mut content = Vec::new();
            file.read_to_end(&mut content).unwrap();
            (file.name().to_string(), content)
        })
        .collect()
}

fn rezip(entries: &[(String, Vec<u8>)]) -> Vec<u8> {
    let mut writer = ZipWriter::new(Cursor::new(Vec::new()));
    let options = SimpleFileOptions::default().compression_method(CompressionMethod::Deflated);
    for (name, content) in entries {
        writer.start_file(name.as_str(), options).unwrap();
        writer.write_all(content).unwrap();
    }
    writer.finish().unwrap().into_inner()
}

fn edit(bytes: &[u8], part: &str, change: impl Fn(String) -> String) -> Vec<u8> {
    let mut parts = entries(bytes);
    let entry = parts.iter_mut().find(|(name, _)| name == part).unwrap();
    entry.1 = change(String::from_utf8(entry.1.clone()).unwrap()).into_bytes();
    rezip(&parts)
}

fn failure_code(outcome: &PreviewOutcome) -> &str {
    assert!(outcome.outputs.is_empty());
    &outcome.response.failure.as_ref().unwrap().code
}

#[test]
fn saved_sheet_settings_produce_a_deterministic_new_workbook() {
    let input = workbook("Orders", &[row("A-1", 3), row("B-2", 40)]);
    let first = preview(NATIVE, &input);
    assert!(
        first.response.failure.is_none(),
        "{:?}",
        first.response.failure
    );
    let artifact = &first.response.result.as_ref().unwrap().artifacts[0];
    assert_eq!(artifact.media_type, xlsx_document::MEDIA_TYPE);
    assert_eq!(artifact.records_written, 2);
    let settings = artifact.xlsx_settings.as_ref().unwrap();
    assert_eq!(settings.sheet.as_deref(), Some("Result"));
    assert_eq!((settings.start_row, settings.has_header_row), (1, true));
    assert_eq!(settings.headers, ["Code", "Quantity"]);
    let project = project();
    let rows = ferrule_format_xlsx::from_bytes_bounded(
        &first.outputs[0],
        &project.target,
        Some("Result"),
        1,
        &[],
        true,
        &xlsx_document::limits(first.outputs[0].len()),
    )
    .unwrap();
    assert_eq!(rows, [row("A-1", 3), row("B-2", 40)]);
    // Fixed package metadata makes exact-byte regression comparison meaningful.
    assert_eq!(preview(NATIVE, &input).outputs, first.outputs);
}

#[test]
fn sheet_selection_comes_from_the_mapping_and_failures_are_independent() {
    let wrong = workbook("Other", &[row("A-1", 3)]);
    assert_eq!(failure_code(&preview(NATIVE, &wrong)), "invalidInput");
    let typed = edit(
        &workbook("Orders", &[row("A-1", 3)]),
        "xl/worksheets/sheet1.xml",
        |xml| xml.replacen("<v>3</v>", "<v>3.5</v>", 1),
    );
    assert_eq!(failure_code(&preview(NATIVE, &typed)), "invalidInput");
    let retry = preview(NATIVE, &workbook("Orders", &[row("C-3", 1)]));
    assert!(retry.response.failure.is_none());
    assert_eq!(retry.outputs.len(), 1);
}

#[test]
fn macros_formulas_and_external_links_are_refused() {
    let valid = workbook("Orders", &[row("A-1", 3)]);
    let macro_enabled = edit(&valid, "[Content_Types].xml", |xml| {
        xml.replace(
            "spreadsheetml.sheet.main+xml",
            "vnd.ms-excel.sheet.macroEnabled.main+xml",
        )
    });
    let formula = edit(&valid, "xl/worksheets/sheet1.xml", |xml| {
        xml.replacen("<v>3</v>", "<f>1+2</f><v>3</v>", 1)
    });
    let external = edit(&valid, "xl/_rels/workbook.xml.rels", |xml| {
        xml.replacen("Target=", "TargetMode=\"External\" Target=", 1)
    });
    for input in [macro_enabled, formula, external] {
        assert_eq!(failure_code(&preview(NATIVE, &input)), "invalidInput");
    }
}

#[test]
fn zip_entry_count_and_expansion_ratio_are_bounded_before_cells_are_read() {
    let valid = workbook("Orders", &[row("A-1", 3)]);
    let mut crowded = entries(&valid);
    for index in 0..64 {
        crowded.push((format!("docProps/extra{index}.xml"), b"<x/>".to_vec()));
    }
    assert_eq!(
        failure_code(&preview(NATIVE, &rezip(&crowded))),
        "inputLimitExceeded"
    );
    let comment = "a".repeat(4 * 1024 * 1024);
    let bomb = edit(&valid, "xl/worksheets/sheet1.xml", |xml| {
        xml.replacen("<sheetData>", &format!("<!--{comment}--><sheetData>"), 1)
    });
    assert!(bomb.len() * xlsx_document::MAX_EXPANSION_RATIO < comment.len());
    assert_eq!(failure_code(&preview(NATIVE, &bomb)), "inputLimitExceeded");
    assert!(preview(NATIVE, &valid).response.failure.is_none());
}

#[test]
fn stateful_unknown_and_non_primary_plans_are_refused_before_reading_input() {
    let input = workbook("Orders", &[row("A-1", 3)]);
    for (key, value) in [
        ("xlsx_update_existing", serde_json::json!(true)),
        ("xlsx_rows", serde_json::json!([1])),
        ("future_option", serde_json::json!(true)),
    ] {
        let mut native: serde_json::Value = serde_json::from_slice(NATIVE).unwrap();
        native["target_options"][key] = value;
        let bytes = serde_json::to_vec(&native).unwrap();
        let outcome = preview(&bytes, &input);
        assert!(
            matches!(
                failure_code(&outcome),
                "unsupportedCapability" | "invalidMapping"
            ),
            "{key}"
        );
    }
    let mut native: serde_json::Value = serde_json::from_slice(NATIVE).unwrap();
    native["source_options"]["xlsx_headers"] = serde_json::json!(["Code", "Quantity"]);
    let bytes = serde_json::to_vec(&native).unwrap();
    assert_eq!(
        failure_code(&preview(&bytes, &input)),
        "unsupportedCapability"
    );
    assert!(
        process_preview(&header(NATIVE, b""), NATIVE, &[Vec::new()])
            .response
            .failure
            .is_some()
    );
}

#[test]
fn generated_csharp_builds_refuse_xlsx_explicitly() {
    let context = support::request("inspect", NATIVE);
    let request = braid_ferrule_host::build::Request {
        protocol_version: braid_ferrule_host::build::PROTOCOL.into(),
        request_id: context.request_id,
        revision: context.revision,
        native_digest: support::digest(NATIVE),
        intent_digest: support::digest(NATIVE),
        operation: "inspect".into(),
        profile: braid_ferrule_host::build::PROFILE.into(),
    };
    assert!(matches!(
        braid_ferrule_host::build::generate(&request, NATIVE),
        Err("unsupportedXlsxBuild")
    ));
}

#[test]
fn portable_profile_only_admits_canonical_rows_and_returns_exact_new_package() {
    let mut mapping: serde_json::Value = serde_json::from_slice(NATIVE).unwrap();
    mapping["source_options"]
        .as_object_mut()
        .unwrap()
        .remove("xlsx_start_row");
    mapping["target_options"]
        .as_object_mut()
        .unwrap()
        .remove("xlsx_headers");
    mapping["target"]["kind"]["children"][0]["name"] = "label".into();
    mapping["root"]["bindings"][0]["target_field"] = "label".into();
    mapping["target_options"]["xlsx_sheet"] = "Order".into();
    let native = serde_json::to_vec(&mapping).unwrap();
    let rows = [row("<A>&", -1), row("東京", 17)];
    let src = ferrule_format_xlsx::to_flat_package(&project().source, &rows, "Orders").unwrap();

    let run = |bytes: &[u8]| {
        let mut h = header(&native, bytes);
        h.protocol_version = xlsx_document::PORTABLE_PROFILE.into();
        process_preview(&h, &native, &[bytes.to_vec()])
    };
    assert!(run(&workbook("Orders", &rows)).outputs.is_empty());
    let result = run(&src);
    let [output] = result.outputs.as_slice() else {
        panic!("{:?}", result.response.failure)
    };
    // Complete package digest of the generated .NET consumer for these mapped rows.
    assert_eq!(
        support::digest(output),
        "6fd5a294eb4037d09781b5d2cfe5bd881a3f7bb31ba481335f3dbde68577062f"
    );
    let mut h = header(&native, &src);
    h.protocol_version = xlsx_document::PORTABLE_PROFILE.into();
    let mut frame = support::framed_bytes(&serde_json::to_vec(&h).unwrap(), &native);
    frame.extend((src.len() as u32).to_be_bytes());
    frame.extend(&src);
    let mut child = std::process::Command::new(env!("CARGO_BIN_EXE_braid-ferrule-host"))
        .args([
            "--service-sandbox",
            "--cpu-seconds=30",
            "--memory-bytes=536870912",
            "--open-files=64",
        ])
        .stdin(std::process::Stdio::piped())
        .stdout(std::process::Stdio::piped())
        .spawn()
        .unwrap();
    child.stdin.take().unwrap().write_all(&frame).unwrap();
    let reply = child.wait_with_output().unwrap();
    assert!(reply.status.success());
    let bytes = reply.stdout.strip_prefix(b"BFS1").unwrap();
    let n = u32::from_be_bytes(bytes[..4].try_into().unwrap()) as usize;
    let metadata: serde_json::Value = serde_json::from_slice(&bytes[4..4 + n]).unwrap();
    assert!(metadata["failure"].is_null(), "{metadata}");
    assert_eq!(&bytes[8 + n..], output);
}
