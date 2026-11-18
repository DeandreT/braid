mod support;
use braid_ferrule_host::preview::{process_preview, protocol::*};
const NATIVE: &[u8] = include_bytes!("fixtures/flextext.native.json");

#[test]
fn framed_flextext_conserves_empty_output_and_rejects_missing_frame() {
    let header = serde_json::to_vec(&header(NATIVE, b"")).unwrap();
    for present in [true, false, true] {
        let mut request = support::framed_bytes(&header, NATIVE);
        if present {
            request.extend_from_slice(&0u32.to_be_bytes());
        }
        let mut reply = vec![];
        braid_ferrule_host::handle_stream(&mut &request[..], &mut reply).unwrap();
        let length = u32::from_be_bytes(reply[..4].try_into().unwrap()) as usize;
        let response: PreviewResponse = serde_json::from_slice(&reply[4..4 + length]).unwrap();
        assert_eq!(response.profile, FLEXTEXT_PREVIEW_PROTOCOL_VERSION);
        assert_eq!(
            response.native_digest.as_deref(),
            Some(support::digest(NATIVE).as_str())
        );
        if present {
            assert_eq!(
                response.result.unwrap().artifacts[0].sha256,
                support::digest(b"")
            );
            assert_eq!(&reply[4 + length..], &0u32.to_be_bytes());
        } else {
            assert_eq!(response.failure.unwrap().code, "invalidFrame");
            assert_eq!(reply.len(), 4 + length);
        }
    }
}

#[test]
fn single_document_fixture_does_not_iterate_and_repeated_output_stays_sanitized() {
    let mut project = braid_ferrule_host::decode::decode_native(NATIVE)
        .unwrap()
        .project;
    assert!(matches!(
        &project.root.iteration,
        ferrule_mapping::ScopeIteration::None
    ));
    let source = ferrule_format_flextext::from_str(
        "hello",
        &project.source,
        project.source_options.flextext.as_ref().unwrap(),
    )
    .unwrap();
    let output = ferrule_engine::run(&project, &source).unwrap();
    assert!(matches!(&output, ferrule_ir::Instance::Group(_)));
    assert_eq!(
        ferrule_format_flextext::to_string(
            &project.target,
            &output,
            project.target_options.flextext.as_ref().unwrap(),
        )
        .unwrap(),
        "hello"
    );

    // An empty source path means iteration, not a single document. Do not repair
    // that different mapping or expose its codec error through the preview wire.
    project.root.iteration = ferrule_mapping::ScopeIteration::Source(Vec::new());
    let native = serde_json::to_vec(&project).unwrap();
    let input = b"synthetic-private-text".to_vec();
    let result = process_preview(
        &header(&native, &input),
        &native,
        std::slice::from_ref(&input),
    );
    let failure = result.response.failure.unwrap();
    assert_eq!(failure.code, "executionFailed");
    assert_eq!(
        failure.message,
        "The native FlexText mapping or bounded serialization failed; no output was published."
    );
    assert!(result.outputs.is_empty());
    let retry = b"after rejected mapping".to_vec();
    assert_eq!(
        process_preview(
            &header(NATIVE, &retry),
            NATIVE,
            std::slice::from_ref(&retry)
        )
        .outputs,
        [retry]
    );
}

#[test]
fn process_preview_refuses_unsafe_target_modes_before_reading_input_or_rendering() {
    use ferrule_ir::ScalarType;
    use ferrule_mapping::{
        DelimitedDialect, DelimitedRecordField, FixedWidthRecordField, FlexCommand, FlexLineEnding,
        FlexTextLayout, OnceSplitter, SwitchArm, SwitchMode,
    };
    use std::num::NonZeroU32;
    let store = || FlexCommand::store("text", ScalarType::String, None);
    let commands = [
        FlexCommand::SplitOnce {
            name: "record".into(),
            splitter: OnceSplitter::FixedColumns(NonZeroU32::new(u32::MAX).unwrap()),
            first: Box::new(store()),
            second: Box::new(FlexCommand::store("rest", ScalarType::String, None)),
        },
        FlexCommand::FixedWidthRecords {
            name: "records".into(),
            fields: vec![
                FixedWidthRecordField::new(
                    "text",
                    ScalarType::String,
                    NonZeroU32::new(u32::MAX).unwrap(),
                )
                .unwrap(),
            ],
            fill_char: ' ',
            record_delimiters: true,
            treat_empty_as_absent: true,
        },
        FlexCommand::DelimitedRecords {
            name: "records".into(),
            dialect: DelimitedDialect::new(',', "\n", '"', '\\').unwrap(),
            fields: vec![DelimitedRecordField::new("text", ScalarType::String).unwrap()],
        },
        FlexCommand::Switch {
            name: "choice".into(),
            mode: SwitchMode::FirstMatch,
            arms: vec![SwitchArm::new("prefix", store()).unwrap()],
            default: None,
        },
    ];
    for command in commands {
        let mut project = braid_ferrule_host::decode::decode_native(NATIVE)
            .unwrap()
            .project;
        // Keep the fixture schema/mapping valid. The unsafe mode is an unused static
        // branch after a safe field; admission must still refuse its renderer.
        let target = FlexTextLayout::new(
            "Document",
            FlexCommand::SplitOnce {
                name: "output".into(),
                splitter: OnceSplitter::Delimiter("|".into()),
                first: Box::new(store()),
                second: Box::new(command),
            },
            FlexLineEnding::Lf,
            false,
        )
        .unwrap();
        project.target = target.schema();
        project.target_options.flextext = Some(target);
        project.root.bindings.clear();
        project.root.children.clear();
        project.graph.nodes.clear();
        assert!(ferrule_engine::validate(&project).is_empty());
        let native = serde_json::to_vec(&project).unwrap();
        let response = process_preview(&header(&native, b"\xff"), &native, &[vec![255]]);
        assert_eq!(
            response.response.failure.unwrap().code,
            "unsupportedCapability"
        );
        assert!(response.outputs.is_empty());
    }
}

fn header(native: &[u8], bytes: &[u8]) -> PreviewRequestHeader {
    let context = support::request("inspect", native);
    PreviewRequestHeader {
        protocol_version: FLEXTEXT_PREVIEW_PROTOCOL_VERSION.into(),
        request_id: context.request_id,
        revision: context.revision,
        native_digest: support::digest(native),
        document_id: "entry.native.json".into(),
        target: PreviewTarget::Primary,
        current_date_time: "2026-11-01T12:00:00Z".into(),
        parameters: vec![],
        inputs: vec![PreviewInput::Primary {
            byte_length: bytes.len() as u32,
            sha256: support::digest(bytes),
        }],
        limits: PreviewLimits {
            maximum_input_bytes: 8 * 1024 * 1024,
            maximum_output_bytes: 8 * 1024 * 1024,
            maximum_artifacts: 1,
            maximum_text_bytes: 2048,
        },
    }
}

#[test]
fn explicit_layout_empty_unicode_and_independent_failures() {
    for input in [
        b"hello".as_slice(),
        "\u{e9}<not-markup>".as_bytes(),
        b"".as_slice(),
        b"after failure".as_slice(),
    ] {
        let result = process_preview(&header(NATIVE, input), NATIVE, &[input.to_vec()]);
        assert!(
            result.response.failure.is_none(),
            "{:?}",
            result.response.failure
        );
        assert_eq!(result.outputs, [input.to_vec()]);
        let artifact = &result.response.result.unwrap().artifacts[0];
        assert_eq!(artifact.media_type, "text/plain");
        assert!(artifact.flex_text_settings.is_some());
        let bad = process_preview(&header(NATIVE, b"\xff"), NATIVE, &[vec![255]]);
        let failure = bad.response.failure.unwrap();
        assert_eq!(failure.code, "invalidInput");
        assert_eq!(failure.subject.as_deref(), Some("primary"));
        assert!(bad.outputs.is_empty());
    }
    assert!(
        process_preview(&header(NATIVE, b""), NATIVE, &[])
            .response
            .failure
            .is_some()
    );
}

#[test]
fn legacy_conflicts_and_bounds_never_publish() {
    let mut legacy = header(NATIVE, b"x");
    legacy.protocol_version = DOCUMENT_PREVIEW_PROTOCOL_VERSION.into();
    assert!(
        process_preview(&legacy, NATIVE, &[b"x".to_vec()])
            .response
            .failure
            .is_some()
    );
    let mut native: serde_json::Value = serde_json::from_slice(NATIVE).unwrap();
    native["source_options"]["json_document"] = serde_json::json!(true);
    let bytes = serde_json::to_vec(&native).unwrap();
    assert!(
        process_preview(&header(&bytes, b"x"), &bytes, &[b"x".to_vec()])
            .outputs
            .is_empty()
    );
    let mut limited = header(NATIVE, b"long");
    limited.limits.maximum_output_bytes = 1;
    assert!(
        process_preview(&limited, NATIVE, &[b"long".to_vec()])
            .outputs
            .is_empty()
    );
}

#[test]
fn native_value_ceiling_and_build_refusal_are_explicit() {
    let boundary = vec![b'x'; 1024 * 1024];
    let accepted = process_preview(
        &header(NATIVE, &boundary),
        NATIVE,
        std::slice::from_ref(&boundary),
    );
    assert!(
        accepted.response.failure.is_none(),
        "{:?}",
        accepted.response.failure
    );
    assert_eq!(accepted.outputs, [boundary]);
    let too_large = vec![b'x'; 1024 * 1024 + 1];
    let failed = process_preview(
        &header(NATIVE, &too_large),
        NATIVE,
        std::slice::from_ref(&too_large),
    );
    let failure = failed.response.failure.unwrap();
    assert_eq!(failure.code, "inputLimitExceeded");
    assert_eq!(failure.subject.as_deref(), Some("primary"));
    assert!(failed.outputs.is_empty());
    let retry = b"after oversized value".to_vec();
    let accepted = process_preview(
        &header(NATIVE, &retry),
        NATIVE,
        std::slice::from_ref(&retry),
    );
    assert!(accepted.response.failure.is_none());
    assert_eq!(accepted.outputs, [retry]);
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
        Err("unsupportedFlexTextBuild")
    ));
}
