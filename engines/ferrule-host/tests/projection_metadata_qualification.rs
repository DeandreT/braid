use braid_ferrule_host::protocol::Limits;
use serde_json::{Value, json};

fn base() -> Value {
    json!({
        "source":{"name":"Source","kind":{"kind":"group","children":[{"name":"code","kind":{"kind":"scalar","ty":"string"}}]}},
        "target":{"name":"Target","kind":{"kind":"group","children":[{"name":"result","kind":{"kind":"scalar","ty":"string"}}]}},
        "graph":{"nodes":{"1":{"kind":"source_field","path":["code"]}}},
        "root":{"target_field":"","bindings":[{"target_field":"result","node":1}],"children":[]}
    })
}

fn project(native: &Value) -> Value {
    let bytes = serde_json::to_vec(native).unwrap();
    let decoded = braid_ferrule_host::decode::decode_native(&bytes).unwrap();
    serde_json::to_value(
        braid_ferrule_host::projection::project(
            &decoded.project,
            "entry.native.json",
            &decoded.document_prefix,
            &Limits {
                maximum_diagnostics: 128,
                maximum_items: 8192,
                maximum_text_bytes: 2048,
            },
        )
        .unwrap(),
    )
    .unwrap()
}

fn input_field<'a>(projection: &'a Value, name: &str) -> &'a Value {
    let input = projection["boundaries"]
        .as_array()
        .unwrap()
        .iter()
        .find(|boundary| boundary["role"] == "primaryInput")
        .unwrap();
    projection["fields"]
        .as_array()
        .unwrap()
        .iter()
        .find(|field| field["boundaryId"] == input["id"] && field["name"] == name)
        .unwrap()
}

#[test]
fn new_pin_decimal_literals_keep_exact_decimal_identity_in_json_and_xml_metadata() {
    for options in [json!({"json_document":true}), json!({"xml_document":true})] {
        let mut native = base();
        native["source_options"] = options.clone();
        native["target_options"] = options;
        native["graph"]["nodes"]["1"] =
            json!({"kind":"const","value":{"$decimal96":"79228162514264337593543950335"}});
        let projected = project(&native).to_string();
        assert!(projected.contains("\"flavor\":\"decimal\""));
        assert!(projected.contains("79228162514264337593543950335"));
    }
}

#[test]
fn new_pin_invocation_counter_is_a_located_gap_not_inferred_execution() {
    let mut native = base();
    native["graph"]["nodes"]["2"] = json!({"kind":"const","value":1});
    native["graph"]["nodes"]["1"] =
        json!({"kind":"invocation_counter","key":"fixture-counter","start":2,"step":2});
    let projected = project(&native);
    assert_eq!(projected["coverage"]["status"], "partial");
    assert!(projected.to_string().contains("Invocation counter"));
}

#[test]
fn owned_iteration_explanation_keeps_braid_execution_unqualified_without_claiming_native_refusal() {
    let bytes = serde_json::to_vec(&base()).unwrap();
    let mut decoded = braid_ferrule_host::decode::decode_native(&bytes).unwrap();
    decoded.project.source.repeating = true;
    decoded.project.target.repeating = true;
    decoded.project.root.iteration = ferrule_mapping::ScopeIteration::OwnedSource {
        source: ferrule_mapping::OwnedSourceRef {
            owner: ferrule_mapping::SourceOwner::Primary,
            path: vec![],
        },
        output_path: None,
    };
    let projected = serde_json::to_value(
        braid_ferrule_host::projection::project(
            &decoded.project,
            "entry.native.json",
            &decoded.document_prefix,
            &Limits {
                maximum_diagnostics: 128,
                maximum_items: 8192,
                maximum_text_bytes: 2048,
            },
        )
        .unwrap(),
    )
    .unwrap();
    let controls = projected["controls"].to_string();
    assert!(controls.contains("Explicitly owned source iteration"));
    assert!(controls.contains("This read-only worker does not execute owned-source iteration"));
    assert!(
        !controls.contains("Native evaluation of this ownership form remains capability closed")
    );
    for operation in ["preview", "build"] {
        let capability = projected["capabilities"]
            .as_array()
            .unwrap()
            .iter()
            .find(|capability| capability["operation"] == operation)
            .unwrap();
        assert_eq!(capability["status"], "notQualified");
    }
}

#[test]
fn explicit_json_ignores_retained_xml_attribute_and_namespace_path_flags() {
    let mut native = base();
    native["source_options"] = json!({"json_document":true});
    native["source"]["kind"]["children"][0]["attribute"] = json!(true);
    native["source"]["kind"]["children"][0]["xml_namespace"] =
        json!({"kind":"qualified","uri":"urn:braid:fixture"});
    let projected = project(&native);
    assert_eq!(
        input_field(&projected, "code")["path"],
        json!([{"kind":"field","name":"code","namespaceUri":null}])
    );
    assert_eq!(projected["coverage"]["status"], "complete");
}

#[test]
fn qualified_xml_keeps_attribute_and_exact_namespace_identity() {
    let mut native = base();
    native["source_options"] = json!({"xml_document":true});
    native["source"]["kind"]["children"][0]["attribute"] = json!(true);
    native["source"]["kind"]["children"][0]["xml_namespace"] =
        json!({"kind":"qualified","uri":"urn:braid:fixture"});
    let mut unqualified = native["source"]["kind"]["children"][0].clone();
    unqualified["name"] = json!("local");
    unqualified["xml_namespace"] = json!({"kind":"unqualified"});
    let mut unspecified = unqualified.clone();
    unspecified["name"] = json!("legacy");
    unspecified.as_object_mut().unwrap().remove("xml_namespace");
    native["source"]["kind"]["children"]
        .as_array_mut()
        .unwrap()
        .extend([unqualified, unspecified]);
    let projected = project(&native);
    assert_eq!(
        input_field(&projected, "code")["path"],
        json!([{"kind":"attribute","name":"code","namespaceUri":"urn:braid:fixture"}])
    );
    assert_eq!(
        input_field(&projected, "local")["path"],
        json!([{"kind":"attribute","name":"local","namespaceUri":""}])
    );
    assert_eq!(
        input_field(&projected, "legacy")["path"],
        json!([{"kind":"attribute","name":"legacy","namespaceUri":null}])
    );
}

#[test]
fn unqualified_format_does_not_assert_a_physical_xml_path() {
    let mut native = base();
    native["source"]["kind"]["children"][0]["attribute"] = json!(true);
    native["source"]["kind"]["children"][0]["xml_namespace"] = json!({"kind":"unqualified"});
    let projected = project(&native);
    assert_eq!(
        input_field(&projected, "code")["path"],
        json!([{"kind":"field","name":"code","namespaceUri":null}])
    );
    assert_eq!(projected["coverage"]["status"], "partial");
    assert!(
        projected["coverage"]["gaps"]
            .as_array()
            .unwrap()
            .iter()
            .any(|gap| gap["code"] == "schema.formatDependentPath")
    );
}

#[test]
fn missing_lookup_collection_is_located_without_definite_field_lineage() {
    let mut native = base();
    native["graph"]["nodes"]["2"] = json!({"kind":"const","value":"needle"});
    native["graph"]["nodes"]["1"] = json!({"kind":"lookup","collection":["missing"],"key":["key"],"value":["value"],"matches":2});
    let projected = project(&native);
    assert_eq!(projected["coverage"]["status"], "partial");
    assert!(
        projected["coverage"]["gaps"]
            .as_array()
            .unwrap()
            .iter()
            .any(|gap| gap["code"] == "projection.ambiguousLookup"
                && gap["nativeLocator"]["nodeId"] == "1")
    );
    assert!(
        projected["rules"][0]["dependencies"]
            .as_array()
            .unwrap()
            .iter()
            .all(|dependency| dependency["kind"] != "field")
    );
}

#[test]
fn collided_lookup_and_count_collections_are_possible_references_not_definite_reads() {
    let catalog = json!({"name":"catalog","repeating":true,"kind":{"kind":"group","children":[
        {"name":"key","kind":{"kind":"scalar","ty":"string"}},
        {"name":"value","kind":{"kind":"scalar","ty":"string"}}
    ]}});
    for aggregate in [false, true] {
        let mut native = base();
        native["source"]["kind"]["children"]
            .as_array_mut()
            .unwrap()
            .push(catalog.clone());
        native["extra_sources"] =
            json!([{"name":"catalog","path":"do-not-open-catalog.xml","schema":catalog}]);
        native["graph"]["nodes"]["2"] = json!({"kind":"const","value":"needle"});
        native["graph"]["nodes"]["1"] = if aggregate {
            json!({"kind":"aggregate","function":"count","collection":["catalog"],"value":[]})
        } else {
            json!({"kind":"lookup","collection":["catalog"],"key":["key"],"value":["value"],"matches":2})
        };
        let projected = project(&native);
        assert_eq!(projected["coverage"]["status"], "partial");
        assert!(
            projected["coverage"]["gaps"]
                .as_array()
                .unwrap()
                .iter()
                .any(|gap| gap["code"]
                    == if aggregate {
                        "projection.ambiguousAggregate"
                    } else {
                        "projection.ambiguousLookup"
                    })
        );
        let rule = &projected["rules"][0];
        assert!(
            rule["dependencies"]
                .as_array()
                .unwrap()
                .iter()
                .all(|dependency| dependency["kind"] != "field")
        );
        assert!(
            serde_json::to_string(&rule["details"])
                .unwrap()
                .contains("fieldId"),
            "known alternatives remain readable"
        );
    }
}

#[test]
fn named_document_set_path_metadata_keeps_the_document_owner_without_resource_access() {
    let mut native = base();
    native["target"]["kind"]["children"][0]["repeating"] = json!(true);
    native["extra_sources"] = json!([{"name":"docs","path":"do-not-open-document-set","schema":native["source"],"options":{"xml_document":true,"local_xml_file_set":true}}]);
    native["graph"]["nodes"]["1"] = json!({"kind":"source_document_path"});
    native["root"] = json!({"target_field":"","bindings":[],"children":[{"target_field":"result","source":["docs"],"construction":{"scalar":{"value":1}},"bindings":[],"children":[]}]});
    // Use the authoritative encoder for the construction enum vocabulary.
    let mut decoded =
        ferrule_mapping::project_file::decode_bytes(&serde_json::to_vec(&base()).unwrap()).unwrap();
    decoded.extra_sources = serde_json::from_value(native["extra_sources"].clone()).unwrap();
    decoded.target = serde_json::from_value(native["target"].clone()).unwrap();
    decoded.graph = serde_json::from_value(native["graph"].clone()).unwrap();
    decoded.root.bindings.clear();
    decoded.root.children = vec![ferrule_mapping::Scope {
        target_field: "result".into(),
        iteration: ferrule_mapping::ScopeIteration::Source(vec!["docs".into()]),
        construction: ferrule_mapping::ScopeConstruction::Scalar { value: 1 },
        ..Default::default()
    }];
    let encoded = serde_json::to_value(decoded).unwrap();
    let projected = project(&encoded);
    let docs = projected["boundaries"]
        .as_array()
        .unwrap()
        .iter()
        .find(|boundary| boundary["role"] == "namedInput" && boundary["name"] == "docs")
        .unwrap();
    assert!(projected["rules"][0]["dependencies"].as_array().unwrap().iter().any(|dependency| dependency == &json!({"kind":"node","node":{"kind":"boundary","boundaryId":docs["id"]},"role":"value"})));
    assert!(
        !serde_json::to_string(&projected)
            .unwrap()
            .contains("do-not-open-document-set")
    );
}

#[test]
fn an_open_inner_context_does_not_certify_outer_fallback_as_definite_lineage() {
    let mut native = base();
    native["source"]["kind"]["children"].as_array_mut().unwrap().push(json!({"name":"open","kind":{"kind":"group","children":[],"dynamic":{"name":"property","kind":{"kind":"scalar","ty":"string"}}}}));
    native["root"] = json!({"target_field":"","bindings":[],"children":[{"target_field":"result","source":["open"],"bindings":[],"children":[]}]});
    let mut decoded =
        ferrule_mapping::project_file::decode_bytes(&serde_json::to_vec(&native).unwrap()).unwrap();
    decoded.root.children[0].construction = ferrule_mapping::ScopeConstruction::Scalar { value: 1 };
    let projected = project(&serde_json::to_value(decoded).unwrap());
    assert_eq!(projected["coverage"]["status"], "partial");
    assert!(
        projected["coverage"]["gaps"]
            .as_array()
            .unwrap()
            .iter()
            .any(|gap| gap["code"] == "projection.openSourceOwnership")
    );
    assert!(
        projected["rules"][0]["dependencies"]
            .as_array()
            .unwrap()
            .iter()
            .all(|dependency| dependency["kind"] != "field")
    );
}
