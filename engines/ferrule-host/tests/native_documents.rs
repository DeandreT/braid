mod support;
use braid_ferrule_host::native_document::{PROTOBUF_PROFILE, XBRL_PROFILE};
use braid_ferrule_host::preview::{PreviewOutcome, process_preview, protocol::*};
use ferrule_ir::{ScalarType, SchemaNode, XML_TEXT_FIELD};
use ferrule_mapping::{XbrlBoundaryOptions, XbrlNamespaceBinding};

const PROTOBUF: &[u8] = include_bytes!("fixtures/protobuf.native.json");
const XBRLI: &str = "http://www.xbrl.org/2003/instance";
const HELLO: &[u8] = &[0x0a, 0x05, b'h', b'e', b'l', b'l', b'o'];

fn header(profile: &str, native: &[u8], bytes: &[u8]) -> PreviewRequestHeader {
    let context = support::request("inspect", native);
    PreviewRequestHeader {
        protocol_version: profile.into(),
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

fn run(profile: &str, native: &[u8], bytes: &[u8]) -> PreviewOutcome {
    process_preview(&header(profile, native, bytes), native, &[bytes.to_vec()])
}

fn edited(native: &[u8], edit: impl FnOnce(&mut serde_json::Value)) -> Vec<u8> {
    let mut value: serde_json::Value = serde_json::from_slice(native).unwrap();
    edit(&mut value);
    serde_json::to_vec(&value).unwrap()
}

#[test]
fn protobuf_binary_round_trips_exact_bytes_and_failures_stay_independent() {
    for input in [HELLO, &[][..], "\u{e9}\u{1f680}".as_bytes()] {
        let bytes = if input.is_empty() || input == HELLO {
            input.to_vec()
        } else {
            [&[0x0a, input.len() as u8][..], input].concat()
        };
        let result = run(PROTOBUF_PROFILE, PROTOBUF, &bytes);
        assert!(
            result.response.failure.is_none(),
            "{:?}",
            result.response.failure
        );
        assert_eq!(result.outputs, [bytes]);
        assert_eq!(result.response.profile, PROTOBUF_PROFILE);
        let artifact = &result.response.result.unwrap().artifacts[0];
        assert_eq!(artifact.media_type, "application/x-protobuf");
        assert!(artifact.flex_text_settings.is_none() && artifact.csv_dialect.is_none());
        let settings = artifact.document_settings.as_ref().unwrap();
        assert_eq!(settings.schema, "braid.protobuf-settings.v1");
        assert!(settings.options_json.contains("\"root_message\":\"Doc\""));
        // Truncated length-delimited field: native decode failure, no output.
        let bad = run(PROTOBUF_PROFILE, PROTOBUF, &[0x0a, 0x05, b'h']);
        let failure = bad.response.failure.unwrap();
        assert_eq!(failure.code, "invalidInput");
        assert_eq!(failure.subject.as_deref(), Some("primary"));
        assert!(bad.outputs.is_empty());
    }
}

#[test]
fn protobuf_requires_its_own_profile_and_canonical_saved_options() {
    // The document profile never selects a native-only codec.
    let document = run(DOCUMENT_PREVIEW_PROTOCOL_VERSION, PROTOBUF, HELLO);
    assert!(document.outputs.is_empty());
    assert!(run(XBRL_PROFILE, PROTOBUF, HELLO).outputs.is_empty());
    for native in [
        edited(PROTOBUF, |v| {
            v["source_options"]["json_document"] = true.into()
        }),
        // Omitted defaults spelled out are noncanonical metadata, not silently normalized.
        edited(PROTOBUF, |v| {
            v["target_options"]["protobuf"]["imports"] = serde_json::json!([])
        }),
        edited(PROTOBUF, |v| {
            v["target_options"]["protobuf"]["root_message"] = "Missing".into()
        }),
        edited(PROTOBUF, |v| {
            v["target_options"]["protobuf"]["schema"] = "message".into()
        }),
    ] {
        let result = run(PROTOBUF_PROFILE, &native, HELLO);
        assert_eq!(
            result.response.failure.unwrap().code,
            "unsupportedCapability"
        );
        assert!(result.outputs.is_empty());
    }
    let mut named = header(PROTOBUF_PROFILE, PROTOBUF, HELLO);
    named.target = PreviewTarget::Named { name: "x".into() };
    assert!(
        process_preview(&named, PROTOBUF, &[HELLO.to_vec()])
            .outputs
            .is_empty()
    );
    let mut limited = header(PROTOBUF_PROFILE, PROTOBUF, HELLO);
    limited.limits.maximum_output_bytes = 1;
    assert!(
        process_preview(&limited, PROTOBUF, &[HELLO.to_vec()])
            .outputs
            .is_empty()
    );
    assert_eq!(run(PROTOBUF_PROFILE, PROTOBUF, HELLO).outputs, [HELLO]);
}

fn xbrl_schema() -> SchemaNode {
    SchemaNode::group(
        "xbrl",
        vec![
            SchemaNode::group(
                "rows",
                vec![
                    SchemaNode::group(
                        "identifier",
                        vec![
                            SchemaNode::scalar("scheme", ScalarType::String).attribute(),
                            SchemaNode::scalar(XML_TEXT_FIELD, ScalarType::String).text(),
                        ],
                    ),
                    SchemaNode::group(
                        "period",
                        vec![SchemaNode::scalar("instant", ScalarType::String)],
                    ),
                    SchemaNode::scalar("Amount", ScalarType::Int),
                ],
            )
            .repeating(),
        ],
    )
}

fn xbrl_options(source: bool) -> XbrlBoundaryOptions {
    let path = |parts: &[&str]| parts.iter().map(|part| part.to_string()).collect();
    let options = if source {
        XbrlBoundaryOptions::external_source("taxonomy.xsd")
    } else {
        XbrlBoundaryOptions::external_target("taxonomy.xsd", None)
    };
    options
        .unwrap()
        .with_namespace_bindings(vec![
            XbrlNamespaceBinding::new(path(&["rows", "identifier"]), XBRLI).unwrap(),
            XbrlNamespaceBinding::new(path(&["rows", "period"]), XBRLI).unwrap(),
            XbrlNamespaceBinding::new(path(&["rows", "period", "instant"]), XBRLI).unwrap(),
            XbrlNamespaceBinding::new(path(&["rows", "Amount"]), "urn:a").unwrap(),
        ])
        .unwrap()
}

fn xbrl_native(source_mode: bool) -> Vec<u8> {
    let mut project = braid_ferrule_host::decode::decode_native(
        br##"{"source":{"name":"x","kind":{"kind":"group","children":[]}},
        "target":{"name":"x","kind":{"kind":"group","children":[]}},
        "graph":{"nodes":{
          "1":{"kind":"source_field","path":["identifier","scheme"],"frame":["rows"]},
          "2":{"kind":"source_field","path":["identifier","#text"],"frame":["rows"]},
          "3":{"kind":"source_field","path":["period","instant"],"frame":["rows"]},
          "4":{"kind":"source_field","path":["Amount"],"frame":["rows"]}}},
        "root":{"target_field":"","source":null,"bindings":[],"children":[
          {"target_field":"rows","source":["rows"],"bindings":[{"target_field":"Amount","node":4}],"children":[
            {"target_field":"identifier","bindings":[{"target_field":"scheme","node":1},{"target_field":"#text","node":2}],"children":[]},
            {"target_field":"period","bindings":[{"target_field":"instant","node":3}],"children":[]}]}]}}"##,
    )
    .unwrap()
    .project;
    project.source = xbrl_schema();
    project.target = xbrl_schema();
    project.source_options.xbrl = Some(xbrl_options(true));
    project.target_options.xbrl = Some(xbrl_options(source_mode));
    serde_json::to_vec(&project).unwrap()
}

fn instance(amount: &str) -> Vec<u8> {
    format!(
        r#"<xbrli:xbrl xmlns:xbrli="{XBRLI}" xmlns:a="urn:a"><xbrli:context id="c"><xbrli:entity><xbrli:identifier scheme="urn:id">Entity</xbrli:identifier></xbrli:entity><xbrli:period><xbrli:instant>2026-06-30</xbrli:instant></xbrli:period></xbrli:context><xbrli:unit id="u"><xbrli:measure>xbrli:pure</xbrli:measure></xbrli:unit><a:Amount contextRef="c" unitRef="u">{amount}</a:Amount></xbrli:xbrl>"#
    )
    .into_bytes()
}

#[test]
fn xbrl_renders_deterministic_native_instances_and_refuses_bad_input() {
    let native = xbrl_native(false);
    let first = run(XBRL_PROFILE, &native, &instance("5"));
    assert!(
        first.response.failure.is_none(),
        "{:?}",
        first.response.failure
    );
    let artifact = &first.response.result.as_ref().unwrap().artifacts[0];
    assert_eq!(artifact.media_type, "application/xbrl+xml");
    assert_eq!(
        artifact.document_settings.as_ref().unwrap().schema,
        "braid.xbrl-settings.v1"
    );
    let output = &first.outputs[0];
    let reread = ferrule_format_xbrl::from_bytes_bounded(
        output,
        &xbrl_schema(),
        &xbrl_options(true),
        &Default::default(),
    )
    .unwrap();
    assert!(format!("{reread:?}").contains("Int(5)"), "{reread:?}");
    assert_eq!(
        run(XBRL_PROFILE, &native, &instance("5")).outputs,
        first.outputs
    );

    for (bytes, code) in [
        (vec![0xff], "invalidInput"),
        (instance("not-a-number"), "invalidInput"),
        (
            b"<!DOCTYPE x [<!ENTITY e \"y\">]><x/>".to_vec(),
            "invalidInput",
        ),
        (
            format!(
                "<xbrli:xbrl xmlns:xbrli=\"{XBRLI}\">{}{}</xbrli:xbrl>",
                "<a>".repeat(80),
                "</a>".repeat(80)
            )
            .into_bytes(),
            "inputLimitExceeded",
        ),
    ] {
        let result = run(XBRL_PROFILE, &native, &bytes);
        let failure = result.response.failure.unwrap();
        assert_eq!(failure.code, code);
        assert_eq!(failure.subject.as_deref(), Some("primary"));
        assert!(result.outputs.is_empty());
    }
    assert_eq!(
        run(XBRL_PROFILE, &native, &instance("5")).outputs,
        first.outputs
    );

    // A target declared as an XBRL source is not a writable boundary; the pinned
    // engine's own validation refuses it before Braid admission is reached.
    let wrong = run(XBRL_PROFILE, &xbrl_native(true), &instance("5"));
    assert!(matches!(
        wrong.response.failure.unwrap().code.as_str(),
        "invalidMapping" | "unsupportedCapability"
    ));
    assert!(wrong.outputs.is_empty());
}

#[test]
fn generated_builds_refuse_both_formats_explicitly() {
    for (native, expected) in [
        (PROTOBUF.to_vec(), "unsupportedProtobufBuild"),
        (xbrl_native(false), "unsupportedXbrlBuild"),
    ] {
        let context = support::request("inspect", &native);
        for profile in [
            braid_ferrule_host::build::PROFILE,
            braid_ferrule_host::build::DOCUMENT_PROFILE,
        ] {
            let request = braid_ferrule_host::build::Request {
                protocol_version: braid_ferrule_host::build::PROTOCOL.into(),
                request_id: context.request_id.clone(),
                revision: context.revision.clone(),
                native_digest: support::digest(&native),
                intent_digest: support::digest(&native),
                operation: "inspect".into(),
                profile: profile.into(),
            };
            assert!(matches!(
                braid_ferrule_host::build::generate(&request, &native),
                Err(status) if status == expected
            ));
        }
    }
}

#[test]
fn opted_in_generation_binds_primary_settings_for_each_adapter() {
    use braid_ferrule_host::build::{self, FLEX_PROFILE, PB_PROFILE};
    for (profile, native, format) in [
        (
            FLEX_PROFILE,
            include_bytes!("fixtures/flextext.native.json").to_vec(),
            "flexText",
        ),
        (PB_PROFILE, PROTOBUF.to_vec(), "protobuf"),
        (build::XBRL_PROFILE, xbrl_native(false), "xbrl"),
    ] {
        let context = support::request("inspect", &native);
        let request = build::Request {
            protocol_version: build::PROTOCOL.into(),
            request_id: context.request_id,
            revision: context.revision,
            native_digest: support::digest(&native),
            intent_digest: support::digest(&native),
            operation: "compile".into(),
            profile: profile.into(),
        };
        let (boundaries, files) = build::generate(&request, &native).expect(format);
        assert!(!files.is_empty());
        assert_eq!(boundaries.len(), 2);
        for (boundary, kind) in boundaries.iter().zip(["primaryInput", "primaryOutput"]) {
            assert_eq!(boundary.kind, kind);
            assert_eq!(boundary.format, format);
            assert!(boundary.name.is_none());
            assert!(boundary.document_settings.is_some());
        }
    }
}
