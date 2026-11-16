use super::*;
use serde_json::{Value, json};

fn scalar(name: &str) -> Value {
    json!({"name":name,"shape":{"kind":"scalar","scalar":"string"}})
}

fn group(name: &str, children: Vec<Value>) -> Value {
    json!({"name":name,"shape":{"kind":"group","children":children}})
}

pub(super) fn plan() -> Value {
    json!({
        "format":"braid.authoring.v1",
        "source":{"schema":group("Source",vec![scalar("code")]),"options":{"kind":"json"}},
        "target":{"schema":group("Target",vec![scalar("code")]),"options":{"kind":"json"}},
        "edit":{"format":"ferrule.edit.v1","operations":[{
            "op":"bind","binding":{"scope":{"target":{"kind":"primary"},"children":[]},"field":"code"},
            "expression":{"kind":"source","id":"1","path":["code"]}
        }]}
    })
}

fn run(value: &Value) -> Result<Checkpoint, EditFailure> {
    compile(&serde_json::to_vec(value).unwrap())
}

fn rejected(value: &Value, code: &str) {
    assert_eq!(run(value).err().expect("must refuse").code, code);
}

#[test]
fn creates_fresh_deterministic_project_and_applies_operations() {
    let a = run(&plan()).unwrap();
    let b = run(&plan()).unwrap();
    assert_eq!(a.native, b.native);
    assert_eq!(a.native_digest, b.native_digest);
    assert!(a.view.validation.is_valid);
    assert!(!a.view.projection.fields.is_empty());
    let project = ferrule_mapping::project_file::decode_bytes(&a.native).unwrap();
    assert!(project.source_path.is_none());
    assert!(project.extra_sources.is_empty());
    assert_eq!(project.graph.nodes.len(), 1);
    assert_eq!(project.root.bindings[0].target_field, "code");
    assert_eq!(
        ferrule_mapping::project_file::encode_pretty(&project)
            .unwrap()
            .as_bytes(),
        a.native
    );
}

#[test]
fn six_explicit_format_families_round_trip_without_claiming_execution() {
    for kind in ["json", "xml", "x12", "edifact", "csv", "tsv"] {
        let mut p = plan();
        let options = match kind {
            "x12" => json!({"kind":kind,"interchange_version":"00501"}),
            "csv" | "tsv" => {
                json!({"kind":kind,"header":true,"quote":true,"bom":false,"preserve_empty_strings":true})
            }
            _ => json!({"kind":kind}),
        };
        // EDI's schema must express ordered segment groups, not friendly JSON fields.
        if matches!(kind, "x12" | "edifact") {
            p["target"]["schema"] = group(
                "Envelope",
                vec![group(
                    if kind == "x12" { "ISA" } else { "UNB" },
                    vec![scalar("element1")],
                )],
            );
            p["edit"]["operations"] = json!([]);
        }
        p["target"]["options"] = options;
        let checkpoint = run(&p).unwrap();
        let native = ferrule_mapping::project_file::decode_bytes(&checkpoint.native).unwrap();
        match kind {
            "json" => assert!(native.target_options.json_document),
            "xml" => assert!(native.target_options.xml_document),
            "x12" => assert_eq!(native.target_options.edi_kind, Some(EdiBoundaryKind::X12)),
            "edifact" => assert_eq!(
                native.target_options.edi_kind,
                Some(EdiBoundaryKind::Edifact)
            ),
            _ => {
                assert_eq!(
                    native.target_options.tabular_kind,
                    Some(TabularBoundaryKind::Csv)
                );
                assert_eq!(
                    native.target_options.delimiter,
                    Some(if kind == "tsv" { '\t' } else { ',' })
                );
                assert!(native.target_options.csv_preserve_empty_strings);
            }
        }
    }
}

#[test]
fn unknown_and_duplicate_fields_are_refused_including_nested_options_and_edits() {
    for pointer in [
        "/extra",
        "/source/schema/extra",
        "/target/options/extra",
        "/edit/operations/0/extra",
    ] {
        let mut p = plan();
        let (parent, key) = pointer.rsplit_once('/').unwrap();
        p.pointer_mut(parent)
            .unwrap()
            .as_object_mut()
            .unwrap()
            .insert(key.into(), json!(true));
        rejected(&p, "invalidAuthoring");
    }
    let bytes = serde_json::to_string(&plan()).unwrap().replacen(
        "\"format\":\"braid.authoring.v1\"",
        "\"format\":\"braid.authoring.v1\",\"format\":\"braid.authoring.v1\"",
        1,
    );
    assert_eq!(
        compile(bytes.as_bytes()).err().unwrap().code,
        "invalidAuthoring"
    );
}

#[test]
fn rejects_schema_collisions_missing_required_and_foreign_semantics() {
    let mut p = plan();
    p["source"]["schema"]["shape"]["children"] = json!([scalar("code"), scalar("code")]);
    rejected(&p, "invalidSchema");
    let mut p = plan();
    p["source"]["schema"]["required"] = json!(["absent"]);
    rejected(&p, "invalidSchema");
    let mut p = plan();
    p["target"]["options"] = json!({"kind":"xml"});
    p["target"]["schema"]["nullable"] = json!(true);
    rejected(&p, "unsupportedSemantics");
    let mut p = plan();
    p["target"]["options"] = json!({"kind":"x12","interchange_version":"501"});
    rejected(&p, "invalidBoundary");
}

#[test]
fn preserves_json_required_and_xml_namespace_attribute_metadata() {
    let mut p = plan();
    p["source"]["schema"]["required"] = json!(["code"]);
    p["target"]["options"] = json!({"kind":"xml"});
    p["target"]["schema"]["xml"] = json!({"namespace":"urn:test"});
    p["target"]["schema"]["shape"]["children"][0]["xml"] = json!({"namespace":"","attribute":true});
    let result = run(&p).unwrap();
    let native = ferrule_mapping::project_file::decode_bytes(&result.native).unwrap();
    assert_eq!(
        native.target.xml_namespace,
        XmlNamespace::qualified("urn:test")
    );
    let SchemaKind::Group { children, .. } = native.target.kind else {
        panic!()
    };
    assert!(children[0].attribute);
    assert_eq!(children[0].xml_namespace, Some(XmlNamespace::Unqualified));
    let SchemaKind::Group { required, .. } = native.source.kind else {
        panic!()
    };
    assert_eq!(required, ["code"]);
}

#[test]
fn refuses_limits_before_native_assembly_and_reports_failed_operation() {
    assert_eq!(
        compile(&vec![b' '; MAX_PLAN_BYTES + 1]).err().unwrap().code,
        "authoringLimit"
    );
    let mut p = plan();
    p["source"]["schema"]["shape"]["children"] = Value::Array(
        (0..MAX_SCHEMA_NODES)
            .map(|n| scalar(&format!("n{n}")))
            .collect(),
    );
    rejected(&p, "authoringLimit");
    let mut p = plan();
    let mut nested = scalar("leaf");
    for _ in 0..MAX_SCHEMA_DEPTH + 2 {
        nested = group("nested", vec![nested]);
    }
    p["source"]["schema"] = nested;
    rejected(&p, "authoringLimit");
    let mut p = plan();
    p["edit"]["operations"][0]["expression"] = json!({"kind":"existing","node":"99"});
    let error = run(&p).err().unwrap();
    assert_eq!(error.code, "operationRejected");
    assert_eq!(error.operation_index.as_deref(), Some("0"));
}

fn xml_text_plan(namespace: Option<Value>) -> Value {
    let mut p = plan();
    p["target"]["options"] = json!({"kind":"xml"});
    p["target"]["schema"]["xml"] = json!({"namespace":"urn:document"});
    let mut text = scalar("#text");
    text["xml"] = json!({"text":true});
    if let Some(namespace) = namespace {
        text["xml"]["namespace"] = namespace;
    }
    p["target"]["schema"]["shape"]["children"] = json!([text]);
    p["edit"]["operations"][0]["binding"]["field"] = json!("#text");
    p
}

#[test]
fn compiles_and_executes_xml_text_with_absent_or_null_namespace() {
    for namespace in [None, Some(Value::Null)] {
        let p = xml_text_plan(namespace);
        let checkpoint = run(&p).unwrap();
        let native = ferrule_mapping::project_file::decode_bytes(&checkpoint.native).unwrap();
        let SchemaKind::Group { children, .. } = &native.target.kind else {
            panic!()
        };
        assert_eq!(children[0].name, ferrule_ir::XML_TEXT_FIELD);
        assert!(children[0].text);
        assert_eq!(children[0].xml_namespace, None);
        assert!(ferrule_ir::xml_structured_document_input_is_supported(
            &native.target
        ));
        let result = crate::authored_document::execute(
            &checkpoint.native,
            br#"{"code":"A<&\"B"}"#,
            Default::default(),
        )
        .unwrap();
        assert_eq!(result.output_format, crate::authored_document::Format::Xml);
        let parsed = ferrule_format_xml::from_str_structured(
            std::str::from_utf8(&result.bytes).unwrap(),
            &native.target,
        )
        .unwrap();
        assert_eq!(
            parsed.field("#text"),
            Some(&ferrule_ir::Instance::Scalar(ferrule_ir::Value::String(
                "A<&\"B".into()
            )))
        );

        // The same authored simple-content schema is executable on the input side.
        let mut reverse = plan();
        reverse["source"] = p["target"].clone();
        reverse["edit"]["operations"][0]["expression"]["path"] = json!(["#text"]);
        let checkpoint = run(&reverse).unwrap();
        let back = crate::authored_document::execute(
            &checkpoint.native,
            &result.bytes,
            Default::default(),
        )
        .unwrap();
        assert_eq!(
            serde_json::from_slice::<Value>(&back.bytes).unwrap(),
            json!({"code":"A<&\"B"})
        );
    }
}

#[test]
fn compiler_preview_preserves_namespaced_attributes_and_elements_with_text() {
    // Simple content can coexist with attributes, but not sibling child elements.
    // Exercise both simple-content and ordinary-element documents through execution.
    for simple_content in [true, false] {
        let mut p = xml_text_plan(None);
        if !simple_content {
            p["target"]["schema"]["shape"]["children"][0] = scalar("body");
            p["target"]["schema"]["shape"]["children"][0]["xml"] = json!({"namespace":"urn:body"});
            p["edit"]["operations"][0]["binding"]["field"] = json!("body");
        }
        let mut attribute = scalar("label");
        attribute["xml"] = json!({"namespace":"urn:attribute","attribute":true});
        p["target"]["schema"]["shape"]["children"]
            .as_array_mut()
            .unwrap()
            .push(attribute);
        let mut binding = p["edit"]["operations"][0].clone();
        binding["binding"]["field"] = json!("label");
        binding["expression"]["id"] = json!("2");
        p["edit"]["operations"]
            .as_array_mut()
            .unwrap()
            .push(binding);
        let checkpoint = run(&p).unwrap();
        let native = ferrule_mapping::project_file::decode_bytes(&checkpoint.native).unwrap();
        let output = crate::authored_document::execute(
            &checkpoint.native,
            br#"{"code":"value & \"quoted\""}"#,
            Default::default(),
        )
        .unwrap();
        let xml = std::str::from_utf8(&output.bytes).unwrap();
        assert!(xml.contains("urn:document"));
        assert!(xml.contains("urn:attribute"));
        if !simple_content {
            assert!(xml.contains("urn:body"));
        }
        let parsed = ferrule_format_xml::from_str_structured(xml, &native.target).unwrap();
        let expected =
            ferrule_ir::Instance::Scalar(ferrule_ir::Value::String("value & \"quoted\"".into()));
        assert_eq!(parsed.field("label"), Some(&expected));
        assert_eq!(
            parsed.field(if simple_content { "#text" } else { "body" }),
            Some(&expected)
        );
    }
}

#[test]
fn xml_text_role_and_namespace_grammar_remains_closed() {
    for namespace in [json!(""), json!("urn:forbidden")] {
        rejected(&xml_text_plan(Some(namespace)), "unsupportedSemantics");
    }
    for xml in [
        json!({"namespace":""}),
        json!({"namespace":"","attribute":true}),
        json!({"text":false}),
    ] {
        let mut p = xml_text_plan(None);
        p["target"]["schema"]["shape"]["children"][0]["xml"] = xml;
        rejected(&p, "unsupportedSemantics");
    }
    let mut p = xml_text_plan(None);
    p["target"]["schema"]["shape"]["children"][0]
        .as_object_mut()
        .unwrap()
        .remove("xml");
    rejected(&p, "unsupportedSemantics");
    for flag in ["attribute", "optional", "nillable"] {
        let mut p = xml_text_plan(None);
        p["target"]["schema"]["shape"]["children"][0]["xml"][flag] = json!(true);
        rejected(&p, "unsupportedSemantics");
    }
    let mut p = xml_text_plan(None);
    p["target"]["schema"]["shape"]["children"][0]["repeating"] = json!(true);
    rejected(&p, "unsupportedSemantics");
    let mut p = xml_text_plan(None);
    p["target"]["schema"]["shape"]["children"][0]["name"] = json!("ordinaryName");
    rejected(&p, "unsupportedSemantics");
    for attribute in [false, true] {
        for namespace in [None, Some(Value::Null)] {
            let mut p = xml_text_plan(None);
            let child = &mut p["target"]["schema"]["shape"]["children"][0];
            child["name"] = json!("element");
            child["xml"] = json!({"attribute":attribute});
            if let Some(namespace) = namespace {
                child["xml"]["namespace"] = namespace;
            }
            rejected(&p, "unsupportedSemantics");
        }
    }
    let mut p = xml_text_plan(None);
    p["target"]["schema"]["shape"]["children"][0]["xml"]["unknown"] = json!(true);
    rejected(&p, "invalidAuthoring");
    rejected(&xml_text_plan(Some(json!(42))), "invalidAuthoring");
}
