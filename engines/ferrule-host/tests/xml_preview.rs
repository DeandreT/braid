//! Authored protocol fixtures. These do not enable a host feature or qualify C14N.
mod support;

use serde_json::{Value, json};
use weft_ferrule_host::preview::protocol::{
    DOCUMENT_PREVIEW_PROTOCOL_VERSION, MAX_PREVIEW_INPUT_BYTES, MAX_PREVIEW_OUTPUT_BYTES,
    PreviewInput, PreviewLimits, PreviewRequestHeader, PreviewTarget, XML_PREVIEW_PROTOCOL_VERSION,
};
use weft_ferrule_host::preview::{PreviewOutcome, process_preview};

const NATIVE: &[u8] = include_bytes!("fixtures/xml-closed.native.json");
const INPUT: &[u8] = b"<Order><code>A</code><quantity>2</quantity></Order>";

fn header(native: &[u8], input: &[u8]) -> PreviewRequestHeader {
    let context = support::request("inspect", native);
    PreviewRequestHeader {
        protocol_version: XML_PREVIEW_PROTOCOL_VERSION.into(),
        request_id: context.request_id,
        revision: context.revision,
        native_digest: support::digest(native),
        document_id: "entry.native.json".into(),
        target: PreviewTarget::All,
        current_date_time: "2026-11-02T12:00:00Z".into(),
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

fn require_failure(outcome: PreviewOutcome, code: &str) {
    let failure = outcome.response.failure.expect("fixed refusal required");
    assert_eq!(failure.code, code);
    assert!(!failure.message.contains("private-sentinel"));
    assert!(outcome.response.result.is_none());
    assert!(outcome.outputs.is_empty());
}

#[test]
fn xml_is_explicit_and_carries_role_valid_metadata_not_design_file_extensions() {
    let outcome = preview(NATIVE, INPUT);
    assert!(
        outcome.response.failure.is_none(),
        "{:?}",
        outcome.response.failure
    );
    assert_eq!(
        outcome.response.protocol_version,
        XML_PREVIEW_PROTOCOL_VERSION
    );
    assert_eq!(outcome.response.profile, XML_PREVIEW_PROTOCOL_VERSION);
    let result = outcome.response.result.unwrap();
    assert_eq!(result.artifacts.len(), 1);
    let artifact = &result.artifacts[0];
    assert_eq!(artifact.media_type, "application/xml");
    assert_eq!(artifact.boundary_id, "primary-output");
    assert_eq!(artifact.sha256, support::digest(&outcome.outputs[0]));
    let metadata = artifact.xml_metadata.as_ref().unwrap();
    metadata
        .validate(weft_ferrule_host::xml_document::Role::Output)
        .unwrap();
    assert!(metadata.schema_hints.is_none());
    assert!(artifact.csv_dialect.is_none());
    let output = std::str::from_utf8(&outcome.outputs[0]).unwrap();
    assert!(output.contains("<code>A</code>"));
    assert!(output.contains("<quantity>2</quantity>"));
}

#[test]
fn unsafe_and_wrong_encoding_inputs_return_no_bytes_or_private_diagnostics() {
    for input in [
        b"<!DOCTYPE Order [<!ENTITY x SYSTEM 'file:///private-sentinel'>]><Order><code>&x;</code><quantity>2</quantity></Order>".as_slice(),
        b"<Order><code>&private-sentinel;</code><quantity>2</quantity></Order>".as_slice(),
        b"\xff<Order/>".as_slice(),
    ] {
        require_failure(preview(NATIVE, input), "invalidInput");
    }
    assert!(preview(NATIVE, INPUT).response.failure.is_none());
}

#[test]
fn harmless_doctype_text_is_not_a_dtd_and_is_escaped_in_output() {
    let outcome = preview(
        NATIVE,
        b"<Order><code><![CDATA[<!DOCTYPE harmless>]]></code><quantity>2</quantity></Order>",
    );
    assert!(
        outcome.response.failure.is_none(),
        "{:?}",
        outcome.response.failure
    );
    let output = std::str::from_utf8(&outcome.outputs[0]).unwrap();
    assert!(output.contains("&lt;!DOCTYPE harmless&gt;"));
}

#[test]
fn legacy_document_profile_does_not_gain_xml_admission() {
    let mut request = header(NATIVE, INPUT);
    request.protocol_version = DOCUMENT_PREVIEW_PROTOCOL_VERSION.into();
    require_failure(
        process_preview(&request, NATIVE, &[INPUT.to_vec()]),
        "unsupportedCapability",
    );
}

#[test]
fn all_declared_xml_metadata_is_charged_even_for_unselected_outputs() {
    let mut native: Value = serde_json::from_slice(NATIVE).unwrap();
    native["extra_targets"] = Value::Array(
        (0..8)
            .map(|index| {
                json!({
                    "name": format!("copy-{index}"),
                    "schema": native["target"], "root": native["root"],
                    "options": {"xml_document": true, "xml_schema_hints": {
                        "no_namespace_location": "x".repeat(4096), "locations": []
                    }}
                })
            })
            .collect(),
    );
    let native = serde_json::to_vec(&native).unwrap();
    let mut request = header(&native, INPUT);
    request.target = PreviewTarget::Primary;
    require_failure(
        process_preview(&request, &native, &[INPUT.to_vec()]),
        "inputLimitExceeded",
    );
}

#[test]
fn named_xml_targets_keep_declaration_identity_and_required_input_selection() {
    let mut native: Value = serde_json::from_slice(NATIVE).unwrap();
    native["extra_sources"] = json!([{
        "name": "lookup", "path": "never-open", "schema": native["source"],
        "options": {"xml_document": true}
    }]);
    native["extra_targets"] = json!([{
        "name": "copy", "schema": native["target"], "root": native["root"],
        "options": {"xml_document": true}
    }]);
    let native = serde_json::to_vec(&native).unwrap();
    let mut all = header(&native, INPUT);
    require_failure(
        process_preview(&all, &native, &[INPUT.to_vec()]),
        "missingInput",
    );
    all.inputs.push(PreviewInput::Named {
        name: "lookup".into(),
        byte_length: INPUT.len() as u32,
        sha256: support::digest(INPUT),
    });
    let outcome = process_preview(&all, &native, &[INPUT.to_vec(), INPUT.to_vec()]);
    assert!(
        outcome.response.failure.is_none(),
        "{:?}",
        outcome.response.failure
    );
    let artifacts = outcome.response.result.unwrap().artifacts;
    assert_eq!(
        artifacts
            .iter()
            .map(|artifact| artifact.boundary_id.as_str())
            .collect::<Vec<_>>(),
        ["primary-output", "named-output:0"]
    );
    let mut selected = header(&native, INPUT);
    selected.target = PreviewTarget::Named {
        name: "copy".into(),
    };
    let outcome = process_preview(&selected, &native, &[INPUT.to_vec()]);
    assert!(
        outcome.response.failure.is_none(),
        "{:?}",
        outcome.response.failure
    );
    assert_eq!(
        outcome.response.result.unwrap().artifacts[0].boundary_id,
        "named-output:0"
    );
}

#[test]
fn output_refusal_cannot_publish_a_partial_artifact_set() {
    let mut request = header(NATIVE, INPUT);
    request.limits.maximum_output_bytes = 8;
    require_failure(
        process_preview(&request, NATIVE, &[INPUT.to_vec()]),
        "resultLimitExceeded",
    );
}

#[test]
fn authored_mapping_exceptions_cannot_leak_values_or_impersonate_resource_refusals() {
    let mut native: Value = serde_json::from_slice(NATIVE).unwrap();
    native["graph"]["nodes"]["3"] = json!({"kind":"raise", "message":1});
    native["root"]["bindings"][0]["node"] = json!(3);
    let native = serde_json::to_vec(&native).unwrap();
    weft_ferrule_host::xml_document::admit_native(&native).unwrap();
    for message in [
        "private-sentinel",
        "output artifact `/weft/output/primary.xml` exceeds the 64 MiB per-document limit",
        "structured XML input exceeds projection work limit 1",
    ] {
        let input = format!("<Order><code>{message}</code><quantity>2</quantity></Order>");
        let outcome = preview(&native, input.as_bytes());
        let failure = outcome.response.failure.as_ref().unwrap();
        assert_eq!(
            failure.message,
            "The native XML mapping or bounded serialization failed; no output was published."
        );
        assert!(failure.subject.is_none());
        assert!(
            !serde_json::to_string(&outcome.response)
                .unwrap()
                .contains(message)
        );
        require_failure(outcome, "executionFailed");
    }
}

fn serializer_ceiling_mapping(named: bool) -> Vec<u8> {
    let mut native: Value = serde_json::from_slice(NATIVE).unwrap();
    let children = (0..1024)
        .map(|index| {
            json!({
                "name": format!("field{index}"), "kind": {"kind":"scalar", "ty":"string"}
            })
        })
        .collect::<Vec<_>>();
    let schema = json!({"name":"Output", "kind":{"kind":"group", "children":children}});
    let bindings = (0..1024)
        .map(|index| json!({"target_field":format!("field{index}"), "node":1}))
        .collect::<Vec<_>>();
    let root = json!({"target_field":"", "source":null, "bindings":bindings, "children":[]});
    if named {
        native["extra_targets"] = json!([{
            "name":"copy", "schema":schema, "root":root, "options":{"xml_document":true}
        }]);
    } else {
        native["target"] = schema;
        native["root"] = root;
    }
    serde_json::to_vec(&native).unwrap()
}

#[test]
fn real_cli_serializer_ceiling_on_primary_and_named_outputs_is_a_resource_refusal() {
    use ferrule_cli::{PayloadDocument, PayloadRunOptions};
    use std::path::{Path, PathBuf};
    use weft_ferrule_host::xml_document as xml;

    // 1,024 ordinary scalar bindings amplify a 64 KiB source string beyond the
    // real 64 MiB CLI ceiling. This does not lower or replace an upstream limit.
    let value = format!(
        "private-sentinel{}",
        "x".repeat(64 * 1024 - "private-sentinel".len())
    );
    let input = format!("<Order><code>{value}</code><quantity>2</quantity></Order>").into_bytes();
    for named in [false, true] {
        let native = serializer_ceiling_mapping(named);
        let (mut decoded, _) = xml::admit_native(&native).unwrap();
        assert!(ferrule_engine::validate(&decoded.project).is_empty());
        // These assertions distinguish actual serializer coverage from an
        // earlier physical, structured-input, schema or string-budget refusal.
        xml::Safety::new(xml::MAX_DOCUMENT_BYTES)
            .unwrap()
            .validate_bytes(&input)
            .unwrap();
        xml::validate_structured(&input, &decoded.project.source, xml::MAX_DOCUMENT_BYTES).unwrap();
        let primary_path = Path::new("/weft/output/primary.xml");
        let expected_path = if named {
            decoded.project.extra_targets[0].path = Some("/weft/output/named-0.xml".into());
            let path = PathBuf::from("/weft/output/named-0.xml");
            if path.is_absolute() {
                path
            } else {
                Path::new("/weft").join(path)
            }
        } else {
            primary_path.to_path_buf()
        };
        let document = PayloadDocument::new(Path::new("/weft/input/primary.xml"), &input).unwrap();
        let options = PayloadRunOptions::new(document)
            .with_output_path(primary_path)
            .with_bounded_documents()
            .with_current_datetime("2026-11-02T12:00:00Z")
            .unwrap();
        let Err(error) = ferrule_cli::run_project_value_payloads(
            &decoded.project,
            Path::new("/weft/entry.native.json"),
            &options,
        ) else {
            panic!("real CLI byte ceiling must be reached");
        };
        assert_eq!(error.chain().count(), 1);
        assert!(error.is::<String>());
        assert_eq!(
            error.to_string(),
            format!(
                "output artifact `{}` exceeds the {} MiB per-document limit",
                expected_path.display(),
                ferrule_cli::MAX_PAYLOAD_DOCUMENT_BYTES / (1024 * 1024)
            )
        );
        let outcome = preview(&native, &input);
        let failure = outcome.response.failure.as_ref().unwrap();
        assert_eq!(
            failure.message,
            "The XML operation exceeds a resource limit."
        );
        assert_eq!(
            failure.subject.as_deref(),
            Some(if named {
                "named-output:0"
            } else {
                "primary-output"
            })
        );
        require_failure(outcome, "resultLimitExceeded");
    }
}

#[test]
fn parser_resource_preflight_is_separate_from_execution_serializer_coverage() {
    use weft_ferrule_host::xml_document as xml;
    let input = format!(
        "<Order><code>{}</code><quantity>2</quantity></Order>",
        "=".repeat(1_000_001)
    )
    .into_bytes();
    let project = weft_ferrule_host::decode::decode_native(NATIVE)
        .unwrap()
        .project;
    xml::Safety::new(xml::MAX_DOCUMENT_BYTES)
        .unwrap()
        .validate_bytes(&input)
        .unwrap();
    assert_eq!(
        xml::validate_structured(&input, &project.source, xml::MAX_DOCUMENT_BYTES),
        Err(xml::Error::ResourceLimit(xml::Resource::StructuredParser))
    );
    require_failure(preview(NATIVE, &input), "inputLimitExceeded");
}
