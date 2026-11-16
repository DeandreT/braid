mod support;

use std::io::{Cursor, Write};
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};

use serde_json::{Value, json};
use support::{BASIC, INVALID, digest};
use braid_ferrule_host::preview::protocol::{
    MAX_PREVIEW_INPUT_BYTES, MAX_PREVIEW_OUTPUT_BYTES, PREVIEW_PROFILE, PREVIEW_PROTOCOL_VERSION,
    PreviewInput, PreviewLimits, PreviewParameter, PreviewRequestHeader, PreviewTarget,
    PreviewValue,
};
use braid_ferrule_host::protocol::{ENGINE_REVISION, Revision};

const ORDERS: &[u8] = include_bytes!("fixtures/preview-orders.json");
const CSV_SOURCE: &[u8] = include_bytes!("fixtures/preview-csv-source.json");
const ORDER: &[u8] =
    br#"{"id":"SO-1","lines":[{"sku":"A","qty":2},{"sku":"B","qty":9007199254740993}]}"#;
const RATES: &[u8] = br#"{"currency":"EUR"}"#;
const CLOCK: &str = "2026-10-20T01:02:03.5+02:00";
const X12_DOCUMENT: &[u8] = b"ISA*00*          *00*          *ZZ*BRAIDSENDER    *ZZ*BRAIDRECEIVER  *261024*1200*^*00501*000000330*0*T*:~GS*OW*BRAIDSENDER*BRAIDRECEIVER*20261024*1200*330*X*005010~ST*940*0330~BGM*220*BRAID-330~SE*3*0330~GE*1*330~IEA*1*000000330~";
const EDIFACT_DOCUMENT: &[u8] = b"UNB+UNOC:3+BRAIDSENDER+BRAIDRECEIVER+261024:1200+330'UNH+330+ORDERS:D:96A:UN'BGM+220+BRAID-330'UNT+3+330'UNZ+1+330'";

fn strict_envelope_mapping(kind: &str, document: &str) -> Vec<u8> {
    support::edi_identity_mapping(kind, document, false)
}

#[test]
fn strict_complete_envelopes_preserve_controls_and_refuse_broken_counts_without_lenient_segments() {
    use braid_ferrule_host::preview::protocol::DOCUMENT_PREVIEW_PROTOCOL_VERSION;
    for (kind, input, old, changed) in [
        ("x12", X12_DOCUMENT, "SE*3*0330", "SE*9*0330"),
        ("edifact", EDIFACT_DOCUMENT, "UNT+3+330", "UNT+9+330"),
    ] {
        let text = std::str::from_utf8(input).unwrap();
        let native = strict_envelope_mapping(kind, text);
        let mut request = header(&native, &[], input, PreviewTarget::All, vec![]);
        request.protocol_version = DOCUMENT_PREVIEW_PROTOCOL_VERSION.into();
        let (response, outputs) = preview(&request, &native, &[input]);
        assert!(response["failure"].is_null(), "{response:#}");
        let terminator = if kind == "x12" { '~' } else { '\'' };
        let expected = text
            .split(terminator)
            .filter(|value| !value.is_empty())
            .map(|segment| format!("{segment}{terminator}\n"))
            .collect::<String>();
        assert_eq!(std::str::from_utf8(&outputs[0]).unwrap(), expected);
        let broken = text.replace(old, changed).into_bytes();
        let mut request = header(&native, &[], &broken, PreviewTarget::All, vec![]);
        request.protocol_version = DOCUMENT_PREVIEW_PROTOCOL_VERSION.into();
        let (response, outputs) = preview(&request, &native, &[&broken]);
        assert_eq!(failure(&response).0, "invalidInput", "{response:#}");
        assert!(outputs.is_empty());
    }
}

#[test]
fn strict_acknowledgement_envelopes_keep_action_codes_and_refuse_control_mismatches() {
    use braid_ferrule_host::preview::protocol::DOCUMENT_PREVIEW_PROTOCOL_VERSION;
    let x12 = std::str::from_utf8(X12_DOCUMENT).unwrap().replace(
        "ST*940*0330~BGM*220*BRAID-330~SE*3*0330~",
        "ST*999*0330~AK1*PO*329~IK2*850*0329~AK9*A*1*1*1~SE*5*0330~",
    );
    let edifact = "UNB+UNOC:3+SENDER+RECEIVER+261024:1200+1'UNH+1+CONTRL:D:96A:UN'UCI+330+SENDER+RECEIVER+7'UNT+3+1'UNZ+1+1'";
    for (kind, text, from, to) in [
        ("x12", x12.as_str(), "SE*5*0330", "SE*5*9999"),
        ("edifact", edifact, "UNT+3+1", "UNT+3+999"),
    ] {
        let native = strict_envelope_mapping(kind, text);
        let mut request = header(&native, &[], text.as_bytes(), PreviewTarget::All, vec![]);
        request.protocol_version = DOCUMENT_PREVIEW_PROTOCOL_VERSION.into();
        let (response, outputs) = preview(&request, &native, &[text.as_bytes()]);
        assert!(response["failure"].is_null(), "{response:#}");
        let terminator = if kind == "x12" { '~' } else { '\'' };
        let expected = text
            .split(terminator)
            .filter(|value| !value.is_empty())
            .map(|segment| format!("{segment}{terminator}\n"))
            .collect::<String>();
        assert_eq!(std::str::from_utf8(&outputs[0]).unwrap(), expected);
        let broken = text.replace(from, to).into_bytes();
        let mut request = header(&native, &[], &broken, PreviewTarget::All, vec![]);
        request.protocol_version = DOCUMENT_PREVIEW_PROTOCOL_VERSION.into();
        let (response, outputs) = preview(&request, &native, &[&broken]);
        assert_eq!(failure(&response).0, "invalidInput");
        assert!(outputs.is_empty());
        assert!(
            response["failure"]["message"]
                .as_str()
                .unwrap()
                .contains("element 2")
        );
    }
}

#[test]
fn complete_x12_repeated_elements_reach_json_in_the_original_order() {
    use braid_ferrule_host::preview::protocol::DOCUMENT_PREVIEW_PROTOCOL_VERSION;
    let text = std::str::from_utf8(X12_DOCUMENT)
        .unwrap()
        .replace("BRAID-330", "FIRST^SECOND^THIRD");
    let mut mapping: Value =
        serde_json::from_slice(&strict_envelope_mapping("x12", &text)).unwrap();
    for side in ["source", "target"] {
        let bgm = mapping[side]["kind"]["children"]
            .as_array_mut()
            .unwrap()
            .iter_mut()
            .find(|value| value["name"] == "BGM")
            .unwrap();
        bgm["kind"]["children"][1]["repeating"] = json!(true);
    }
    let bgm = mapping["root"]["children"]
        .as_array_mut()
        .unwrap()
        .iter_mut()
        .find(|value| value["target_field"] == "BGM")
        .unwrap();
    let bindings = bgm["bindings"].as_array_mut().unwrap();
    let binding = bindings.remove(
        bindings
            .iter()
            .position(|value| value["target_field"] == "e2")
            .unwrap(),
    );
    let node = binding["node"].as_u64().unwrap().to_string();
    bgm["children"].as_array_mut().unwrap().push(json!({
        "target_field":"e2", "source":["BGM","e2"],
        "construction":{"scalar":{"value":binding["node"]}}, "bindings":[], "children":[]
    }));
    mapping["graph"]["nodes"][&node]["path"] = json!([]);
    mapping["graph"]["nodes"][&node]["frame"] = json!(["BGM", "e2"]);
    mapping["target_options"] = json!({"json_document":true});
    let native = serde_json::to_vec(&mapping).unwrap();
    let mut request = header(&native, &[], text.as_bytes(), PreviewTarget::All, vec![]);
    request.protocol_version = DOCUMENT_PREVIEW_PROTOCOL_VERSION.into();
    let (response, outputs) = preview(&request, &native, &[text.as_bytes()]);
    assert!(response["failure"].is_null(), "{response:#}");
    let output: Value = serde_json::from_slice(&outputs[0]).unwrap();
    assert_eq!(output["BGM"]["e2"], json!(["FIRST", "SECOND", "THIRD"]));
}

#[test]
fn complete_multiple_interchanges_reach_json_as_distinct_ordered_records() {
    use braid_ferrule_host::preview::protocol::DOCUMENT_PREVIEW_PROTOCOL_VERSION;
    for (kind, document) in [("x12", X12_DOCUMENT), ("edifact", EDIFACT_DOCUMENT)] {
        let first = std::str::from_utf8(document).unwrap();
        let second = first.replace("BRAID-330", "SECOND");
        let text = format!("{first}\n{second}");
        let mut mapping: Value =
            serde_json::from_slice(&strict_envelope_mapping(kind, first)).unwrap();
        for side in ["source", "target"] {
            let mut interchange = mapping[side].take();
            interchange["repeating"] = json!(true);
            mapping[side] =
                json!({"name":"Documents","kind":{"kind":"group","children":[interchange]}});
        }
        for node in mapping["graph"]["nodes"]
            .as_object_mut()
            .unwrap()
            .values_mut()
        {
            node["frame"] = json!(["Interchange"]);
        }
        let mut scope = mapping["root"].take();
        scope["target_field"] = json!("Interchange");
        scope["source"] = json!(["Interchange"]);
        mapping["root"] = json!({"target_field":"","bindings":[],"children":[scope]});
        mapping["target_options"] = json!({"json_document":true});
        let native = serde_json::to_vec(&mapping).unwrap();
        let mut request = header(&native, &[], text.as_bytes(), PreviewTarget::All, vec![]);
        request.protocol_version = DOCUMENT_PREVIEW_PROTOCOL_VERSION.into();
        let (response, outputs) = preview(&request, &native, &[text.as_bytes()]);
        assert!(response["failure"].is_null(), "{response:#}");
        let output: Value = serde_json::from_slice(&outputs[0]).unwrap();
        assert_eq!(output["Interchange"].as_array().unwrap().len(), 2);
        assert_eq!(output["Interchange"][0]["BGM"]["e2"], "BRAID-330");
        assert_eq!(output["Interchange"][1]["BGM"]["e2"], "SECOND");
    }
}

#[test]
fn lenient_x12_preserves_every_interchange_with_different_syntax() {
    use braid_ferrule_host::preview::protocol::DOCUMENT_PREVIEW_PROTOCOL_VERSION;
    let first = std::str::from_utf8(X12_DOCUMENT).unwrap();
    let mut mapping: Value =
        serde_json::from_slice(&strict_envelope_mapping("x12", first)).unwrap();
    for side in ["source", "target"] {
        let mut interchange = mapping[side].take();
        interchange["repeating"] = json!(true);
        mapping[side] =
            json!({"name":"Documents","kind":{"kind":"group","children":[interchange]}});
    }
    for node in mapping["graph"]["nodes"]
        .as_object_mut()
        .unwrap()
        .values_mut()
    {
        node["frame"] = json!(["Interchange"]);
    }
    let mut scope = mapping["root"].take();
    scope["target_field"] = json!("Interchange");
    scope["source"] = json!(["Interchange"]);
    mapping["root"] = json!({"target_field":"","bindings":[],"children":[scope]});
    mapping["source_options"]["lenient_segments"] = json!(true);
    mapping["target_options"] = json!({"json_document":true});
    let native = serde_json::to_vec(&mapping).unwrap();
    for second in [
        first.replace('*', "|"),
        first.replace('~', "!"),
        first.replace(':', ">"),
        first.replace('^', "!"),
    ] {
        for copies in [1, 128] {
            let text = format!("{first}\n{}", second.repeat(copies));
            let mut request = header(&native, &[], text.as_bytes(), PreviewTarget::All, vec![]);
            request.protocol_version = DOCUMENT_PREVIEW_PROTOCOL_VERSION.into();
            let (response, outputs) = preview(&request, &native, &[text.as_bytes()]);
            assert!(response["failure"].is_null(), "{response:#}");
            let value: Value = serde_json::from_slice(&outputs[0]).unwrap();
            assert_eq!(value["Interchange"].as_array().unwrap().len(), copies + 1);
        }
    }
}

#[test]
fn mixed_x12_cross_role_syntax_preserves_repeated_composites_and_explicit_mismatch_refuses() {
    use braid_ferrule_host::preview::protocol::DOCUMENT_PREVIEW_PROTOCOL_VERSION;
    let first = std::str::from_utf8(X12_DOCUMENT).unwrap();
    let native = support::x12_interchange_mapping(first);
    let first = first.replace("BGM*220*BRAID-330~", "BGM*220*BRAID-330*A:B^C:D~");
    let second: String = first
        .replace("BRAID-330", "SECOND")
        .chars()
        .map(|c| match c {
            '*' => '|',
            ':' => '~',
            '~' => ':',
            '^' => '!',
            _ => c,
        })
        .collect();
    let text = format!("{first}{second}");
    let mut request = header(&native, &[], text.as_bytes(), PreviewTarget::All, vec![]);
    request.protocol_version = DOCUMENT_PREVIEW_PROTOCOL_VERSION.into();
    let (response, outputs) = preview(&request, &native, &[text.as_bytes()]);
    assert!(response["failure"].is_null(), "{response:#}");
    let value: Value = serde_json::from_slice(&outputs[0]).unwrap();
    for (index, id) in ["BRAID-330", "SECOND"].iter().enumerate() {
        let body = &value["Interchanges"][index]["Groups"][0]["Messages"][0]["BGM"];
        assert_eq!(body["e2"], *id);
        assert_eq!(body["e3"], json!([{"c1":"A","c2":"B"},{"c1":"C","c2":"D"}]));
    }
    let mut selected: Value = serde_json::from_slice(&native).unwrap();
    selected["source_options"]["x12_separators"] =
        json!({"element":"*","component":":","segment":"~","repetition":"^","release":null});
    let selected = serde_json::to_vec(&selected).unwrap();
    let mut request = header(&selected, &[], text.as_bytes(), PreviewTarget::All, vec![]);
    request.protocol_version = DOCUMENT_PREVIEW_PROTOCOL_VERSION.into();
    let (response, outputs) = preview(&request, &selected, &[text.as_bytes()]);
    assert_eq!(failure(&response).0, "invalidInput");
    assert!(outputs.is_empty());
}

#[test]
fn complete_edi_groups_and_transactions_remain_distinct_ordered_records() {
    use braid_ferrule_host::preview::protocol::DOCUMENT_PREVIEW_PROTOCOL_VERSION;
    for kind in ["x12", "edifact"] {
        let edifact = std::str::from_utf8(EDIFACT_DOCUMENT)
            .unwrap()
            .replace(
                "UNH+",
                "UNG+ORDERS+SENDER+RECEIVER+261024:1200+330+UN+D:96A'UNH+",
            )
            .replace("UNZ+", "UNE+1+330'UNZ+");
        let first = if kind == "x12" {
            std::str::from_utf8(X12_DOCUMENT).unwrap()
        } else {
            edifact.as_str()
        };
        for groups in [false, true] {
            let (start, end, collection, tags) = if kind == "edifact" && groups {
                (
                    "UNG+",
                    "UNZ+",
                    "Groups",
                    vec!["UNG", "UNH", "BGM", "UNT", "UNE"],
                )
            } else if kind == "edifact" {
                ("UNH+", "UNE+", "Messages", vec!["UNH", "BGM", "UNT"])
            } else if groups {
                ("GS*", "IEA*", "Groups", vec!["GS", "ST", "BGM", "SE", "GE"])
            } else {
                ("ST*", "GE*", "Messages", vec!["ST", "BGM", "SE"])
            };
            let begin = first.find(start).unwrap();
            let finish = first.find(end).unwrap();
            let second = first[begin..finish]
                .replace("BRAID-330", "SECOND")
                .replace("0330", "0331")
                .replace("*330*X", "*331*X")
                .replace("GE*1*330", "GE*1*331")
                .replace("+330", "+331");
            let text = format!("{}{}{}", &first[..finish], second, &first[finish..]);
            let text = if groups {
                text.replace("IEA*1*", "IEA*2*").replace("UNZ+1+", "UNZ+2+")
            } else {
                text.replace("GE*1*", "GE*2*").replace("UNE+1+", "UNE+2+")
            };
            let mut mapping: Value =
                serde_json::from_slice(&strict_envelope_mapping(kind, first)).unwrap();
            for side in ["source", "target"] {
                let children = mapping[side]["kind"]["children"].as_array_mut().unwrap();
                let begin = children
                    .iter()
                    .position(|value| value["name"] == tags[0])
                    .unwrap();
                let grouped = children
                    .drain(begin..begin + tags.len())
                    .collect::<Vec<_>>();
                children.insert(begin, json!({"name":collection,"repeating":true,"kind":{"kind":"group","children":grouped}}));
            }
            let scopes = mapping["root"]["children"].as_array_mut().unwrap();
            let begin = scopes
                .iter()
                .position(|value| value["target_field"] == tags[0])
                .unwrap();
            let grouped = scopes.drain(begin..begin + tags.len()).collect::<Vec<_>>();
            scopes.insert(begin, json!({"target_field":collection,"source":[collection],"bindings":[],"children":grouped}));
            for node in mapping["graph"]["nodes"]
                .as_object_mut()
                .unwrap()
                .values_mut()
            {
                if tags.iter().any(|tag| node["path"][0] == *tag) {
                    node["frame"] = json!([collection]);
                }
            }
            mapping["target_options"] = json!({"json_document":true});
            let native = serde_json::to_vec(&mapping).unwrap();
            let mut request = header(&native, &[], text.as_bytes(), PreviewTarget::All, vec![]);
            request.protocol_version = DOCUMENT_PREVIEW_PROTOCOL_VERSION.into();
            let (response, outputs) = preview(&request, &native, &[text.as_bytes()]);
            assert!(response["failure"].is_null(), "{response:#}");
            let output: Value = serde_json::from_slice(&outputs[0]).unwrap();
            assert_eq!(output[collection].as_array().unwrap().len(), 2);
            assert_eq!(output[collection][0]["BGM"]["e2"], "BRAID-330");
            assert_eq!(output[collection][1]["BGM"]["e2"], "SECOND");
        }
    }
}

// Self-authored minimal business segment, not a reference-corpus fixture.
fn document_mapping(source_options: Value, target_options: Value) -> Vec<u8> {
    let schema = json!({"name":"Document", "kind":{"kind":"group","children":[
        {"name":"BGM", "kind":{"kind":"group","children":[
            {"name":"code","kind":{"kind":"scalar","ty":"string"}},
            {"name":"number","kind":{"kind":"scalar","ty":"string"}}
        ]}}
    ]}});
    serde_json::to_vec(&json!({
        "source": schema, "target": schema,
        "source_path":"/private/design/input.xml", "target_path":"/private/design/output.csv",
        "source_options":source_options,"target_options":target_options,
        "graph":{"nodes":{
            "1":{"kind":"source_field","path":["BGM","code"]},
            "2":{"kind":"source_field","path":["BGM","number"]}
        }},
        "root":{"target_field":"","bindings":[],"children":[
            {"target_field":"BGM","bindings":[
                {"target_field":"code","node":1},{"target_field":"number","node":2}
            ],"children":[]}
        ]}
    }))
    .unwrap()
}

#[test]
fn document_profile_runs_explicit_edi_inputs_and_outputs_without_path_inference() {
    use braid_ferrule_host::preview::protocol::{
        DOCUMENT_PREVIEW_PROFILE, DOCUMENT_PREVIEW_PROTOCOL_VERSION,
    };
    for (kind, input, expected, media) in [
        (
            "x12",
            X12_DOCUMENT,
            "BGM*220*BRAID-330~",
            "application/edi-x12",
        ),
        (
            "edifact",
            EDIFACT_DOCUMENT,
            "BGM+220+BRAID-330'",
            "application/edifact",
        ),
    ] {
        let native = document_mapping(
            json!({"edi_kind":kind,"lenient_segments":true}),
            json!({"edi_kind":kind}),
        );
        let mut request = header(&native, &[], input, PreviewTarget::Primary, vec![]);
        request.protocol_version = DOCUMENT_PREVIEW_PROTOCOL_VERSION.into();
        let (response, outputs) = preview(&request, &native, &[input]);
        assert!(response["failure"].is_null(), "{response:#}");
        assert_eq!(response["profile"], DOCUMENT_PREVIEW_PROFILE);
        assert_eq!(
            response["protocolVersion"],
            DOCUMENT_PREVIEW_PROTOCOL_VERSION
        );
        assert_eq!(response["result"]["artifacts"][0]["mediaType"], media);
        assert_eq!(
            std::str::from_utf8(&outputs[0]).unwrap().trim_end(),
            expected
        );
        request.protocol_version = PREVIEW_PROTOCOL_VERSION.into();
        let (legacy, outputs) = preview(&request, &native, &[input]);
        assert_eq!(failure(&legacy).0, "unsupportedCapability");
        assert!(outputs.is_empty());
    }
}

#[test]
fn document_profile_converts_edi_to_json_and_json_to_edi() {
    use braid_ferrule_host::preview::protocol::DOCUMENT_PREVIEW_PROTOCOL_VERSION;
    let json_input = br#"{"BGM":{"code":"220","number":"BRAID-330"}}"#;
    for (kind, edi, expected) in [
        ("x12", X12_DOCUMENT, "BGM*220*BRAID-330~"),
        ("edifact", EDIFACT_DOCUMENT, "BGM+220+BRAID-330'"),
    ] {
        for (source, target, input, expected_media) in [
            (
                json!({"edi_kind":kind,"lenient_segments":true}),
                json!({"json_document":true}),
                edi,
                "application/json",
            ),
            (
                json!({"json_document":true}),
                json!({"edi_kind":kind}),
                &json_input[..],
                if kind == "x12" {
                    "application/edi-x12"
                } else {
                    "application/edifact"
                },
            ),
        ] {
            let native = document_mapping(source, target);
            let mut request = header(&native, &[], input, PreviewTarget::All, vec![]);
            request.protocol_version = DOCUMENT_PREVIEW_PROTOCOL_VERSION.into();
            let (response, outputs) = preview(&request, &native, &[input]);
            assert!(response["failure"].is_null(), "{response:#}");
            assert_eq!(
                response["result"]["artifacts"][0]["mediaType"],
                expected_media
            );
            if expected_media == "application/json" {
                assert_eq!(
                    serde_json::from_slice::<Value>(&outputs[0]).unwrap(),
                    serde_json::from_slice::<Value>(json_input).unwrap()
                );
            } else {
                assert_eq!(
                    std::str::from_utf8(&outputs[0]).unwrap().trim_end(),
                    expected
                );
            }
        }
    }
}

#[test]
fn document_profile_refuses_unselected_and_conflicting_codecs_before_parsing() {
    use braid_ferrule_host::preview::protocol::DOCUMENT_PREVIEW_PROTOCOL_VERSION;
    for options in [
        json!({}),
        json!({"edi_kind":"x12","xml_document":true}),
        json!({"edi_kind":"hl7"}),
    ] {
        let native = document_mapping(options, json!({"json_document":true}));
        let input = b"not an input document";
        let mut request = header(&native, &[], input, PreviewTarget::All, vec![]);
        request.protocol_version = DOCUMENT_PREVIEW_PROTOCOL_VERSION.into();
        let (response, outputs) = preview(&request, &native, &[input]);
        assert_eq!(
            failure(&response),
            ("unsupportedCapability", Some("primary"))
        );
        assert!(outputs.is_empty());
    }
}

#[test]
fn document_profile_keeps_invalid_edi_and_output_limits_structured_without_partial_frames() {
    use braid_ferrule_host::preview::protocol::DOCUMENT_PREVIEW_PROTOCOL_VERSION;
    for kind in ["x12", "edifact"] {
        let native = document_mapping(json!({"edi_kind":kind}), json!({"json_document":true}));
        let mut request = header(&native, &[], b"invalid", PreviewTarget::All, vec![]);
        request.protocol_version = DOCUMENT_PREVIEW_PROTOCOL_VERSION.into();
        let (response, outputs) = preview(&request, &native, &[b"invalid"]);
        assert_eq!(failure(&response), ("invalidInput", Some("primary")));
        assert!(outputs.is_empty());

        let native = document_mapping(json!({"json_document":true}), json!({"edi_kind":kind}));
        let input = br#"{"BGM":{"code":"220","number":"BRAID-330"}}"#;
        let mut request = header(&native, &[], input, PreviewTarget::All, vec![]);
        request.protocol_version = DOCUMENT_PREVIEW_PROTOCOL_VERSION.into();
        request.limits.maximum_output_bytes = 1;
        let (response, outputs) = preview(&request, &native, &[input]);
        assert_eq!(failure(&response), ("resultLimitExceeded", None));
        assert!(outputs.is_empty());
    }
}

#[test]
fn document_profile_applies_format_limits_before_parsing_without_changing_legacy_json() {
    use braid_ferrule_host::preview::protocol::DOCUMENT_PREVIEW_PROTOCOL_VERSION;
    for (options, input) in [
        (
            json!({"edi_kind":"x12"}),
            vec![b'x'; braid_ferrule_host::document_limits::MAX_EDI_BYTES + 1],
        ),
        (
            json!({"edi_kind":"edifact"}),
            "A'".repeat(32_769).into_bytes(),
        ),
        (json!({"json_document":true}), "[".repeat(65).into_bytes()),
    ] {
        let native = document_mapping(options, json!({"json_document":true}));
        let mut request = header(&native, &[], &input, PreviewTarget::All, vec![]);
        request.protocol_version = DOCUMENT_PREVIEW_PROTOCOL_VERSION.into();
        let (response, outputs) = preview(&request, &native, &[&input]);
        assert_eq!(failure(&response), ("inputLimitExceeded", Some("primary")));
        assert!(outputs.is_empty());
    }
    for kind in ["x12", "edifact"] {
        let native = document_mapping(json!({"json_document":true}), json!({"edi_kind":kind}));
        let input =
            serde_json::to_vec(&json!({"BGM":{"code":"220","number":"x".repeat(16_384)}})).unwrap();
        let mut request = header(&native, &[], &input, PreviewTarget::All, vec![]);
        request.protocol_version = DOCUMENT_PREVIEW_PROTOCOL_VERSION.into();
        let (response, outputs) = preview(&request, &native, &[&input]);
        assert_eq!(
            failure(&response),
            ("resultLimitExceeded", Some("primary-output"))
        );
        assert!(outputs.is_empty());
    }
}

#[test]
fn document_profile_refuses_edi_without_an_embedded_executable_schema() {
    use braid_ferrule_host::preview::protocol::DOCUMENT_PREVIEW_PROTOCOL_VERSION;
    for kind in ["x12", "edifact"] {
        let native = serde_json::to_vec(&json!({
            "source":{"name":"Input","kind":{"kind":"group","children":[]}},
            "target":{"name":"Output","kind":{"kind":"group","children":[]}},
            "source_options":{"edi_kind":kind}, "target_options":{"json_document":true},
            "graph":{"nodes":{}},"root":{"target_field":"","bindings":[],"children":[]}
        }))
        .unwrap();
        let mut request = header(&native, &[], b"invalid", PreviewTarget::All, vec![]);
        request.protocol_version = DOCUMENT_PREVIEW_PROTOCOL_VERSION.into();
        let (response, outputs) = preview(&request, &native, &[b"invalid"]);
        assert_eq!(
            failure(&response),
            ("unsupportedCapability", Some("primary"))
        );
        assert!(outputs.is_empty());
    }
}

#[test]
fn document_profile_supports_mixed_named_boundaries_and_target_selection() {
    use braid_ferrule_host::preview::protocol::DOCUMENT_PREVIEW_PROTOCOL_VERSION;
    let mut mapping: Value = serde_json::from_slice(&document_mapping(
        json!({"json_document":true}),
        json!({"edi_kind":"x12"}),
    ))
    .unwrap();
    mapping["extra_sources"] = json!([{
        "name":"advice", "path":"/private/not-a-format.xlsx", "schema":mapping["source"],
        "options":{"edi_kind":"edifact","lenient_segments":true}
    }]);
    mapping["graph"]["nodes"]["1"]["path"] = json!(["advice", "BGM", "code"]);
    mapping["graph"]["nodes"]["2"]["path"] = json!(["advice", "BGM", "number"]);
    mapping["extra_targets"] = json!([{
        "name":"advice", "schema":mapping["target"], "root":mapping["root"],
        "options":{"edi_kind":"edifact"}
    }]);
    let native = serde_json::to_vec(&mapping).unwrap();
    for target in [
        PreviewTarget::All,
        PreviewTarget::Named {
            name: "advice".into(),
        },
    ] {
        let mut request = header(
            &native,
            &[("advice", EDIFACT_DOCUMENT)],
            b"{}",
            target.clone(),
            vec![],
        );
        request.protocol_version = DOCUMENT_PREVIEW_PROTOCOL_VERSION.into();
        let (response, outputs) = preview(&request, &native, &[b"{}", EDIFACT_DOCUMENT]);
        assert!(response["failure"].is_null(), "{response:#}");
        let artifacts = response["result"]["artifacts"].as_array().unwrap();
        assert_eq!(artifacts.last().unwrap()["boundaryId"], "named-output:0");
        assert_eq!(
            artifacts.last().unwrap()["mediaType"],
            "application/edifact"
        );
        assert_eq!(
            std::str::from_utf8(outputs.last().unwrap())
                .unwrap()
                .trim_end(),
            "BGM+220+BRAID-330'"
        );
        if matches!(target, PreviewTarget::All) {
            assert_eq!(artifacts.len(), 2);
            assert_eq!(artifacts[0]["mediaType"], "application/edi-x12");
        } else {
            assert_eq!(artifacts.len(), 1);
        }
    }
}

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
    braid_ferrule_host::handle_stream(&mut Cursor::new(bytes), &mut output).unwrap();
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
    let primary_path = PathBuf::from("/braid/input/primary.json");
    let paths = (0..named.len())
        .map(|index| PathBuf::from(format!("/braid/input/named-{}.json", index + 1)))
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
    .with_output_path(Path::new("/braid/output/primary.json"))
    .with_runtime_parameters(&parameters);
    if let Some(target) = target {
        options = options.with_target(target);
    }
    let options = options.with_current_datetime(CLOCK).unwrap();
    ferrule_cli::run_project_value_payloads(
        &project,
        Path::new("/braid/entry.native.json"),
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
    let (response, outputs) = executable(&bytes);
    assert!(response["failure"].is_null(), "{response:#}");
    assert_eq!(outputs.len(), 2);
}

#[test]
fn executable_document_preview_matches_in_process_frames() {
    use braid_ferrule_host::preview::protocol::DOCUMENT_PREVIEW_PROTOCOL_VERSION;
    for (kind, input) in [("x12", X12_DOCUMENT), ("edifact", EDIFACT_DOCUMENT)] {
        let native = document_mapping(
            json!({"edi_kind":kind,"lenient_segments":true}),
            json!({"json_document":true}),
        );
        let mut request = header(&native, &[], input, PreviewTarget::All, vec![]);
        request.protocol_version = DOCUMENT_PREVIEW_PROTOCOL_VERSION.into();
        let bytes = request_bytes(&request, &native, &[input]);
        let expected = stream(&bytes);
        assert!(expected.0["failure"].is_null(), "{:#}", expected.0);
        assert_eq!(executable(&bytes), expected);
    }
}

fn executable(bytes: &[u8]) -> (Value, Vec<Vec<u8>>) {
    let mut worker = Command::new(env!("CARGO_BIN_EXE_braid-ferrule-host"))
        .env_clear()
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .unwrap();
    worker.stdin.take().unwrap().write_all(bytes).unwrap();
    let output = worker.wait_with_output().unwrap();
    assert!(output.status.success());
    assert!(output.stderr.is_empty());
    reply(&output.stdout)
}
