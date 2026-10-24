mod support;

use std::collections::{BTreeMap, BTreeSet};

use ferrule_mapping::project_file;
use serde_json::{Value, json};
use support::{digest, process, request, success};
use weft_ferrule_host::protocol::Limits;

const BASIC: &[u8] = include_bytes!("fixtures/projection-basic.json");
const CONTROLS: &[u8] = include_bytes!("fixtures/projection-controls.json");
const FUNCTIONS: &[u8] = include_bytes!("fixtures/projection-functions.json");
const PARTIAL: &[u8] = include_bytes!("fixtures/projection-partial.json");
const OWNED: &[u8] = include_bytes!("fixtures/projection-owned-context.json");

#[test]
fn self_authored_projection_examples_decode_and_validate_without_evaluation() {
    for native in [BASIC, CONTROLS, FUNCTIONS, PARTIAL, OWNED] {
        let project = project_file::decode_bytes(native).unwrap();
        let issues = ferrule_engine::validate(&project);
        assert!(
            issues.is_empty(),
            "fixture is structurally valid: {issues:?}"
        );
        let projection = projected(native);
        assert_projection_integrity(&projection, native);
    }
}

#[test]
fn direct_dotted_and_nested_paths_have_independent_field_lineage() {
    let projection = projected(BASIC);
    assert_eq!(projection["coverage"]["status"], "complete");
    let input = boundary(&projection, "primaryInput", None);
    let output = boundary(&projection, "primaryOutput", None);
    let code = field(&projection, input, &[f("code")]);
    let dotted = field(&projection, input, &[f("a.b")]);
    let nested = field(&projection, input, &[f("nested"), f("b")]);
    assert_ne!(dotted["id"], nested["id"]);
    for (target_path, source) in [
        (vec![f("direct")], code),
        (vec![f("dotted")], dotted),
        (vec![f("nestedOutput"), f("b")], nested),
    ] {
        let output_field = field(&projection, output, &target_path);
        let rule = rule(&projection, output_field);
        assert_eq!(
            field_dependencies(rule),
            BTreeSet::from([source["id"].as_str().unwrap().to_string()])
        );
        assert_field_dependency(rule, source, "value");
        assert_relationship(
            &projection,
            "readsFrom",
            "rule",
            rule["id"].as_str().unwrap(),
            "field",
            source["id"].as_str().unwrap(),
        );
        assert_relationship(
            &projection,
            "writesTo",
            "rule",
            rule["id"].as_str().unwrap(),
            "field",
            output_field["id"].as_str().unwrap(),
        );
    }
    assert_eq!(code["dataType"]["kind"], "scalar");
    assert_eq!(code["dataType"]["scalarTypes"], json!(["string"]));
}

#[test]
fn integer_float_and_null_literals_preserve_the_authoritative_native_tags() {
    let projection = projected(BASIC);
    let output = boundary(&projection, "primaryOutput", None);
    let expected = [
        (
            "exactInteger",
            json!({"kind":"exactNumber","flavor":"integer","value":"9007199254740993"}),
        ),
        (
            "integralFloat",
            json!({"kind":"exactNumber","flavor":"binaryFloat","value":"3ff0000000000000"}),
        ),
        (
            "negativeZero",
            json!({"kind":"exactNumber","flavor":"binaryFloat","value":"8000000000000000"}),
        ),
        ("absent", json!({"kind":"null","nullKind":"absent"})),
        (
            "explicitNull",
            json!({"kind":"null","nullKind":"explicitNull"}),
        ),
        ("xmlNil", json!({"kind":"null","nullKind":"xmlNil"})),
    ];
    for (name, literal) in expected {
        let output_field = field(&projection, output, &[f(name)]);
        let rule = rule(&projection, output_field);
        assert!(
            rule["dependencies"]
                .as_array()
                .unwrap()
                .iter()
                .any(|dependency| dependency
                    == &json!({"kind":"constant","value":literal,"role":"value"})),
            "literal dependency for {name}: {rule:#}"
        );
        assert!(
            details(rule)
                .iter()
                .any(|detail| detail["kind"] == "literal" && detail["value"] == literal)
        );
    }
    assert_eq!(
        field(&projection, output, &[f("exactInteger")])["dataType"]["scalarTypes"],
        json!(["integer"])
    );
    assert_eq!(
        field(&projection, output, &[f("integralFloat")])["dataType"]["scalarTypes"],
        json!(["binaryFloat"])
    );
}

#[test]
fn conditional_and_default_dependencies_keep_distinct_roles() {
    let projection = projected(BASIC);
    let input = boundary(&projection, "primaryInput", None);
    let output = boundary(&projection, "primaryOutput", None);
    let conditional = rule(&projection, field(&projection, output, &[f("conditional")]));
    assert_field_dependency(
        conditional,
        field(&projection, input, &[f("enabled")]),
        "condition",
    );
    assert_field_dependency(
        conditional,
        field(&projection, input, &[f("code")]),
        "value",
    );
    assert!(
        details(conditional)
            .iter()
            .any(|detail| detail["kind"] == "expression" && detail["operation"] == "If")
    );
    let parameter_rule = rule(&projection, field(&projection, output, &[f("parameter")]));
    let parameter = projection["parameters"]
        .as_array()
        .unwrap()
        .iter()
        .find(|parameter| parameter["name"] == "fallbackCode")
        .unwrap();
    assert_eq!(
        parameter["defaultValue"],
        json!({"kind":"string","value":"fallback"})
    );
    assert!(
        parameter_rule["dependencies"]
            .as_array()
            .unwrap()
            .iter()
            .any(|dependency| dependency
                == &json!({"kind":"parameter","parameterId":parameter["id"],"role":"value"}))
    );
    assert_constant_dependency(
        parameter_rule,
        json!({"kind":"string","value":"fallback"}),
        "default",
    );
    assert!(
        details(parameter_rule)
            .iter()
            .any(|detail| detail["kind"] == "expression"
                && detail["operation"] == "Parameter with default")
    );
    let value_map = rule(&projection, field(&projection, output, &[f("valueMap")]));
    assert_field_dependency(
        value_map,
        field(&projection, input, &[f("code")]),
        "condition",
    );
    assert_constant_dependency(
        value_map,
        json!({"kind":"string","value":"missing"}),
        "default",
    );
}

#[test]
fn named_boundaries_repetition_and_output_declaration_order_are_preserved() {
    let projection = projected(CONTROLS);
    assert_eq!(projection["coverage"]["status"], "complete");
    let boundaries = projection["boundaries"].as_array().unwrap();
    assert_eq!(
        boundaries
            .iter()
            .map(|boundary| boundary["role"].as_str().unwrap())
            .collect::<Vec<_>>(),
        [
            "primaryInput",
            "namedInput",
            "primaryOutput",
            "namedOutput",
            "namedOutput"
        ]
    );
    assert_eq!(boundaries[1]["name"], "catalog");
    assert_eq!(boundaries[3]["name"], "audit");
    assert_eq!(boundaries[4]["name"], "summary");
    let input = boundary(&projection, "primaryInput", None);
    let catalog = boundary(&projection, "namedInput", Some("catalog"));
    let output = boundary(&projection, "primaryOutput", None);
    let orders = field(&projection, input, &[f("orders")]);
    assert_eq!(orders["dataType"]["kind"], "array");
    assert!(matches!(
        orders["cardinality"]["kind"].as_str(),
        Some("zeroOrMore" | "oneOrMore" | "bounded")
    ));
    field(&projection, input, &[f("orders"), any(), f("orderId")]);
    field(&projection, catalog, &[any(), f("label")]);
    field(&projection, output, &[f("rows"), any(), f("amount")]);
    field(&projection, output, &[f("joined"), any(), f("label")]);
    let rule_output_boundaries = projection["rules"]
        .as_array()
        .unwrap()
        .iter()
        .map(|rule| {
            let field = projection["fields"]
                .as_array()
                .unwrap()
                .iter()
                .find(|field| field["id"] == rule["outputFieldId"])
                .unwrap();
            field["boundaryId"].as_str().unwrap()
        })
        .collect::<Vec<_>>();
    let output_ids = boundaries
        .iter()
        .filter(|boundary| {
            matches!(
                boundary["role"].as_str(),
                Some("primaryOutput" | "namedOutput")
            )
        })
        .map(|boundary| boundary["id"].as_str().unwrap())
        .collect::<Vec<_>>();
    let mut observed = Vec::new();
    for id in rule_output_boundaries {
        if observed.last().copied() != Some(id) {
            observed.push(id);
        }
    }
    assert_eq!(observed, output_ids);
}

#[test]
fn composite_join_keys_and_named_source_reads_are_independent_dependencies() {
    let projection = projected(CONTROLS);
    let input = boundary(&projection, "primaryInput", None);
    let catalog = boundary(&projection, "namedInput", Some("catalog"));
    let join = control(&projection, "join");
    for name in ["orderId", "region"] {
        assert_field_dependency(
            join,
            field(&projection, input, &[f("orders"), any(), f(name)]),
            "value",
        );
        assert_field_dependency(
            join,
            field(&projection, catalog, &[any(), f(name)]),
            "value",
        );
    }
    let output = boundary(&projection, "primaryOutput", None);
    let joined_label = rule(
        &projection,
        field(&projection, output, &[f("joined"), any(), f("label")]),
    );
    assert_field_dependency(
        joined_label,
        field(&projection, catalog, &[any(), f("label")]),
        "value",
    );
    assert!(
        projection["relationships"]
            .as_array()
            .unwrap()
            .iter()
            .any(|relationship| relationship["kind"] == "governedBy"
                && relationship["to"] == json!({"kind":"control","controlId":join["id"]}))
    );
    let total = rule(&projection, field(&projection, output, &[f("total")]));
    assert_field_dependency(
        total,
        field(&projection, input, &[f("orders"), any(), f("amount")]),
        "value",
    );
}

#[test]
fn a_scalar_read_outside_iteration_explains_native_first_item_behavior() {
    let mut native: Value = serde_json::from_slice(CONTROLS).unwrap();
    native["graph"]["nodes"]["18"] = json!({"kind":"source_field","path":["orders","orderId"]});
    native["root"]["bindings"][0]["node"] = json!(18);
    native["target"]["kind"]["children"][2]["kind"]["ty"] = json!("string");
    let bytes = serde_json::to_vec(&native).unwrap();
    assert!(ferrule_engine::validate(&project_file::decode_bytes(&bytes).unwrap()).is_empty());
    let projection = projected(&bytes);
    let output = boundary(&projection, "primaryOutput", None);
    let input = boundary(&projection, "primaryInput", None);
    let total = rule(&projection, field(&projection, output, &[f("total")]));
    assert_field_dependency(
        total,
        field(&projection, input, &[f("orders"), any(), f("orderId")]),
        "value",
    );
    // Pinned resolve_scalar traverses an uniterated repetition through its
    // first member. A wildcard schema path must not imply all-member reads.
    assert!(details(total).iter().any(|detail| {
        detail["kind"] == "literal"
            && detail["value"]["value"]
                .as_str()
                .is_some_and(|text| text.contains("uniterated") && text.contains("first item"))
    }));
    assert_eq!(projection["coverage"]["status"], "complete");
    assert_projection_integrity(&projection, &bytes);
}

#[test]
fn controls_explain_filter_group_sort_window_and_failure_order() {
    let projection = projected(CONTROLS);
    let input = boundary(&projection, "primaryInput", None);
    assert_field_dependency(
        control(&projection, "filter"),
        field(&projection, input, &[f("orders"), any(), f("enabled")]),
        "condition",
    );
    assert_field_dependency(
        control(&projection, "grouping"),
        field(&projection, input, &[f("orders"), any(), f("region")]),
        "value",
    );
    let sorting = control(&projection, "sorting");
    for name in ["amount", "orderId"] {
        assert_field_dependency(
            sorting,
            field(&projection, input, &[f("orders"), any(), f(name)]),
            "value",
        );
    }
    let windows = projection["controls"]
        .as_array()
        .unwrap()
        .iter()
        .filter(|control| control["kind"] == "window")
        .flat_map(details)
        .filter_map(|detail| {
            detail["label"]
                .as_str()
                .or_else(|| detail["operation"].as_str())
        })
        .filter(|label| ["Skip first", "First"].contains(label))
        .collect::<Vec<_>>();
    assert_eq!(windows, ["Skip first", "First"]);
    let ordering = projection["controls"]
        .as_array()
        .unwrap()
        .iter()
        .filter(|control| control["kind"] == "ordering")
        .map(|control| serde_json::to_string(control).unwrap().to_lowercase())
        .collect::<Vec<_>>();
    assert!(
        ordering
            .iter()
            .any(|text| text.contains("filter then sort")),
        "relative filter/sort order must be readable: {ordering:?}"
    );
    let failures = projection["controls"]
        .as_array()
        .unwrap()
        .iter()
        .filter(|control| control["kind"] == "failure")
        .collect::<Vec<_>>();
    assert_eq!(failures.len(), 2);
    assert_eq!(failures[0]["nativeLocator"]["pointer"], "/failure_rules/0");
    assert_eq!(failures[1]["nativeLocator"]["pointer"], "/failure_rules/1");
    assert_field_dependency(
        failures[0],
        field(&projection, input, &[f("orders"), any(), f("enabled")]),
        "condition",
    );
    for (failure, text) in failures
        .into_iter()
        .zip(["Disabled order", "All orders checked"])
    {
        assert!(
            details(failure)
                .iter()
                .any(|detail| detail["kind"] == "literal"
                    && detail["value"] == json!({"kind":"string","value":text}))
        );
    }
}

#[test]
fn every_grouping_mode_and_window_variant_is_readable_and_ordered() {
    for (mode, label, node) in [
        ("group_by", "by", 4),
        ("group_adjacent_by", "adjacent", 4),
        ("group_starting_with", "starting", 3),
        ("group_ending_with", "ending", 3),
        ("group_into_blocks", "blocks", 5),
    ] {
        let mut native: Value = serde_json::from_slice(CONTROLS).unwrap();
        let scope = &mut native["root"]["children"][0];
        scope.as_object_mut().unwrap().remove("group_by");
        scope[mode] = json!(node);
        scope["windows"] = json!([
            {"kind":"skip_first","count":6},
            {"kind":"first","count":5},
            {"kind":"from","position":6},
            {"kind":"from_to","first":6,"last":5},
            {"kind":"last","count":5}
        ]);
        let bytes = serde_json::to_vec(&native).unwrap();
        let projection = projected(&bytes);
        assert_eq!(projection["coverage"]["status"], "complete");
        let grouping = control(&projection, "grouping");
        assert!(
            serde_json::to_string(grouping)
                .unwrap()
                .to_lowercase()
                .contains(label)
        );
        let labels = projection["controls"]
            .as_array()
            .unwrap()
            .iter()
            .filter(|control| control["kind"] == "window")
            .flat_map(details)
            .filter_map(|detail| {
                detail["label"]
                    .as_str()
                    .or_else(|| detail["operation"].as_str())
            })
            .filter(|label| {
                ["Skip first", "First", "From", "Inclusive range", "Last"].contains(label)
            })
            .collect::<Vec<_>>();
        assert_eq!(
            labels,
            ["Skip first", "First", "From", "Inclusive range", "Last"]
        );
        assert_projection_integrity(&projection, &bytes);
    }
}

#[test]
fn reusable_function_definitions_have_private_details_and_shared_caller_references() {
    let projection = projected(FUNCTIONS);
    assert_eq!(projection["coverage"]["status"], "complete");
    assert_eq!(projection["boundaries"].as_array().unwrap().len(), 2);
    assert!(
        projection["parameters"].as_array().unwrap().is_empty(),
        "private function parameters are not runtime parameters"
    );
    assert_eq!(
        projection["fields"].as_array().unwrap().len(),
        5,
        "function internals must not create schema fields"
    );
    let definition = projection["controls"]
        .as_array()
        .unwrap()
        .iter()
        .find(|control| control["summary"] == "Reusable function prefixCode")
        .unwrap();
    assert_eq!(
        definition["nativeLocator"]["pointer"],
        "/user_functions/18446744073709551615"
    );
    for label in ["Private parameters", "Body"] {
        assert!(
            details(definition)
                .iter()
                .any(|detail| detail["kind"] == "group" && detail["label"] == label)
        );
    }
    let body = details(definition);
    assert!(body.iter().any(|detail| {
        detail["kind"] == "expression"
            && detail["operation"]
                .as_str()
                .is_some_and(|operation| operation.to_lowercase().contains("concat"))
    }));
    let input = boundary(&projection, "primaryInput", None);
    let output = boundary(&projection, "primaryOutput", None);
    for name in ["formatted", "other"] {
        let caller = rule(&projection, field(&projection, output, &[f(name)]));
        assert_field_dependency(caller, field(&projection, input, &[f("code")]), "value");
        assert!(
            details(caller)
                .iter()
                .any(|detail| detail["kind"] == "expression"
                    && detail["operation"] == "Reusable function call")
        );
        assert!(details(caller).iter().any(|detail| *detail == &json!({"kind":"reference","reference":{"kind":"control","controlId":definition["id"]}})));
    }
    assert_projection_integrity(&projection, FUNCTIONS);
}

#[test]
fn main_and_two_private_graphs_may_share_node_ids_without_sharing_definition_identity() {
    let mut native: Value = serde_json::from_slice(FUNCTIONS).unwrap();
    let mut second = native["user_functions"]["18446744073709551615"].clone();
    second["name"] = json!("otherPrefixCode");
    second["parameters"][0]["name"] = json!("otherPrefix");
    native["user_functions"]["9007199254740993"] = second;
    native["graph"]["nodes"]["5"]["function"] = json!(9007199254740993_u64);
    let bytes = serde_json::to_vec(&native).unwrap();
    assert!(ferrule_engine::validate(&project_file::decode_bytes(&bytes).unwrap()).is_empty());
    let projection = projected(&bytes);
    let first = projection["controls"]
        .as_array()
        .unwrap()
        .iter()
        .find(|control| control["summary"] == "Reusable function prefixCode")
        .unwrap();
    let second = projection["controls"]
        .as_array()
        .unwrap()
        .iter()
        .find(|control| control["summary"] == "Reusable function otherPrefixCode")
        .unwrap();
    assert_ne!(first["id"], second["id"]);
    assert_eq!(
        first["nativeLocator"]["pointer"],
        "/user_functions/18446744073709551615"
    );
    assert_eq!(
        second["nativeLocator"]["pointer"],
        "/user_functions/9007199254740993"
    );
    let output = boundary(&projection, "primaryOutput", None);
    for (name, definition) in [("formatted", first), ("other", second)] {
        let caller = rule(&projection, field(&projection, output, &[f(name)]));
        assert!(caller["dependencies"].as_array().unwrap().iter().any(|dependency| dependency == &json!({"kind":"node","node":{"kind":"control","controlId":definition["id"]},"role":"value"})));
    }
    assert!(projection["parameters"].as_array().unwrap().is_empty());
    assert_projection_integrity(&projection, &bytes);
}

#[test]
fn unsupported_and_ambiguous_explanations_are_partial_with_all_possible_lineage() {
    let projection = projected(PARTIAL);
    assert_eq!(projection["coverage"]["status"], "partial");
    assert!(
        !projection["coverage"]["gaps"]
            .as_array()
            .unwrap()
            .is_empty()
    );
    let input = boundary(&projection, "primaryInput", None);
    let output = boundary(&projection, "primaryOutput", None);
    let dynamic = rule(
        &projection,
        field(&projection, output, &[f("dynamicValue")]),
    );
    assert!(details(dynamic).iter().any(|detail| {
        detail["kind"] == "unsupported"
            && detail["description"]
                .as_str()
                .is_some_and(|description| !description.is_empty())
    }));
    let nested = rule(
        &projection,
        field(&projection, output, &[f("rows"), any(), f("code")]),
    );
    let outer = rule(&projection, field(&projection, output, &[f("outerCode")]));
    assert_eq!(
        field_dependencies(outer),
        BTreeSet::from([field(&projection, input, &[f("code")])["id"]
            .as_str()
            .unwrap()
            .to_string()])
    );
    assert_field_dependency(nested, field(&projection, input, &[f("code")]), "value");
    assert_field_dependency(
        nested,
        field(&projection, input, &[f("items"), any(), f("code")]),
        "value",
    );
    assert!(
        projection["coverage"]["gaps"]
            .as_array()
            .unwrap()
            .iter()
            .any(|gap| format!("{} {}", gap["code"], gap["message"])
                .to_lowercase()
                .contains("ambig"))
    );
    assert_projection_integrity(&projection, PARTIAL);
}

#[test]
fn duplicate_bindings_to_a_repeating_scalar_preserve_order_and_distinct_rule_identity() {
    let mut native: Value = serde_json::from_slice(FUNCTIONS).unwrap();
    native["target"]["kind"]["children"] =
        json!([{"name":"values","kind":{"kind":"scalar","ty":"string"},"repeating":true}]);
    native["graph"]["nodes"] =
        json!({"1":{"kind":"const","value":"first"},"2":{"kind":"const","value":"second"}});
    native["root"] = json!({"target_field":"","bindings":[{"target_field":"values","node":1},{"target_field":"values","node":2}],"children":[]});
    native.as_object_mut().unwrap().remove("user_functions");
    let bytes = serde_json::to_vec(&native).unwrap();
    let decoded = project_file::decode_bytes(&bytes).unwrap();
    assert!(
        ferrule_engine::validate(&decoded).is_empty(),
        "native repeating scalar bindings are valid ordered append operations"
    );
    let projection = projected(&bytes);
    assert_eq!(projection["coverage"]["status"], "complete");
    let output = boundary(&projection, "primaryOutput", None);
    let values = field(&projection, output, &[f("values")]);
    let rules = projection["rules"]
        .as_array()
        .unwrap()
        .iter()
        .filter(|rule| rule["outputFieldId"] == values["id"])
        .collect::<Vec<_>>();
    assert_eq!(rules.len(), 2);
    assert_ne!(rules[0]["id"], rules[1]["id"]);
    for (index, (rule, text)) in rules.iter().zip(["first", "second"]).enumerate() {
        assert_constant_dependency(rule, json!({"kind":"string","value":text}), "value");
        assert_eq!(
            rule["nativeLocator"]["pointer"],
            format!("/root/bindings/{index}")
        );
    }
    assert_projection_integrity(&projection, &bytes);
}

#[test]
fn explicit_missing_frame_never_falls_back_to_a_root_field() {
    let mut native: Value = serde_json::from_slice(PARTIAL).unwrap();
    native["graph"]["nodes"]["1"]["frame"] = json!(["missing"]);
    let bytes = serde_json::to_vec(&native).unwrap();
    let projection = projected(&bytes);
    let output = boundary(&projection, "primaryOutput", None);
    let outer = rule(&projection, field(&projection, output, &[f("outerCode")]));
    assert!(field_dependencies(outer).is_empty());
    assert_eq!(projection["coverage"]["status"], "partial");
    assert_projection_integrity(&projection, &bytes);
}

#[test]
fn missing_graph_nodes_use_an_honest_document_locator() {
    let mut native: Value = serde_json::from_slice(FUNCTIONS).unwrap();
    native["root"]["bindings"][0]["node"] = json!(4294967295_u32);
    let bytes = serde_json::to_vec(&native).unwrap();
    let projection = projected(&bytes);
    assert_eq!(projection["coverage"]["status"], "partial");
    assert!(
        projection["coverage"]["gaps"]
            .as_array()
            .unwrap()
            .iter()
            .any(|gap| gap["nativeLocator"]["kind"] == "document"
                && gap["message"].as_str().unwrap().contains("missing"))
    );
    assert_projection_integrity(&projection, &bytes);
}

#[test]
fn a_named_source_colliding_with_a_primary_prefix_is_not_guessed() {
    let mut native: Value = serde_json::from_slice(PARTIAL).unwrap();
    native["extra_sources"] = json!([{"name":"items","path":"do-not-read.json","schema":{"name":"NamedItems","kind":{"kind":"group","children":[{"name":"code","kind":{"kind":"scalar","ty":"string"}}]}}}]);
    native["graph"]["nodes"]["4"] = json!({"kind":"source_field","path":["items","code"]});
    native["root"]["bindings"][0]["node"] = json!(4);
    let bytes = serde_json::to_vec(&native).unwrap();
    let projection = projected(&bytes);
    assert_eq!(projection["coverage"]["status"], "partial");
    let output = boundary(&projection, "primaryOutput", None);
    let rule = rule(&projection, field(&projection, output, &[f("outerCode")]));
    let input = boundary(&projection, "primaryInput", None);
    let named = boundary(&projection, "namedInput", Some("items"));
    assert_field_dependency(
        rule,
        field(&projection, input, &[f("items"), any(), f("code")]),
        "value",
    );
    assert_field_dependency(rule, field(&projection, named, &[f("code")]), "value");
    assert_projection_integrity(&projection, &bytes);
}

#[test]
fn invalid_cycles_produce_bounded_located_gaps_and_no_dangling_references() {
    let mut native: Value = serde_json::from_slice(FUNCTIONS).unwrap();
    native["graph"]["nodes"]["30"] = json!({"kind":"call","function":"concat","args":[31,2]});
    native["graph"]["nodes"]["31"] = json!({"kind":"call","function":"concat","args":[30,2]});
    native["root"]["bindings"][0]["node"] = json!(30);
    let bytes = serde_json::to_vec(&native).unwrap();
    assert!(!ferrule_engine::validate(&project_file::decode_bytes(&bytes).unwrap()).is_empty());
    let projection = projected(&bytes);
    assert_eq!(projection["coverage"]["status"], "partial");
    assert!(
        projection["coverage"]["gaps"]
            .as_array()
            .unwrap()
            .iter()
            .any(|gap| format!("{} {}", gap["code"], gap["message"])
                .to_lowercase()
                .contains("cycl"))
    );
    assert!(serde_json::to_vec(&projection).unwrap().len() < 64 * 1024);
    assert_projection_integrity(&projection, &bytes);
}

#[test]
fn structural_and_text_budget_exhaustion_returns_no_projection_payload() {
    let decoded = weft_ferrule_host::decode::decode_native(BASIC).unwrap();
    let mut limits = request("project", BASIC).limits;
    limits.maximum_items = 1;
    assert!(
        weft_ferrule_host::projection::project(
            &decoded.project,
            "entry.native.json",
            &decoded.document_prefix,
            &limits
        )
        .is_err()
    );
    for change in ["constant", "path", "boundaryName"] {
        let mut native: Value = serde_json::from_slice(FUNCTIONS).unwrap();
        let oversized = "é".repeat(1025);
        match change {
            "constant" => native["graph"]["nodes"]["2"]["value"] = json!(oversized),
            "path" => {
                native["source"]["kind"]["children"][0]["name"] = json!(oversized);
                native["graph"]["nodes"]["1"]["path"][0] =
                    native["source"]["kind"]["children"][0]["name"].clone();
            }
            "boundaryName" => native["source"]["name"] = json!(oversized),
            _ => unreachable!(),
        }
        let bytes = serde_json::to_vec(&native).unwrap();
        let decoded = weft_ferrule_host::decode::decode_native(&bytes).unwrap();
        let limits = request("project", &bytes).limits;
        assert!(
            weft_ferrule_host::projection::project(
                &decoded.project,
                "entry.native.json",
                &decoded.document_prefix,
                &limits
            )
            .is_err(),
            "oversized {change} must not be silently truncated"
        );
    }
}

#[test]
fn real_worker_project_response_is_deterministic_and_retains_native_identity() {
    for native in [BASIC, CONTROLS, FUNCTIONS, PARTIAL] {
        let bytes = native.to_vec();
        let header = request("project", native);
        let first = process(&header, native);
        let second = process(&header, native);
        success(&first, &header, "project");
        assert_eq!(first, second);
        assert_eq!(native, bytes);
        assert_eq!(digest(native), header.native_digest);
        assert_projection_integrity(&first["result"], native);
        for operation in ["preview", "build"] {
            let capability = first["result"]["capabilities"]
                .as_array()
                .unwrap()
                .iter()
                .find(|capability| capability["operation"] == operation)
                .unwrap();
            assert_eq!(capability["status"], "notQualified");
        }
    }
}

fn projected(native: &[u8]) -> Value {
    let decoded = weft_ferrule_host::decode::decode_native(native).unwrap();
    let limits: Limits = request("project", native).limits;
    serde_json::to_value(
        weft_ferrule_host::projection::project(
            &decoded.project,
            "entry.native.json",
            &decoded.document_prefix,
            &limits,
        )
        .unwrap(),
    )
    .unwrap()
}

fn f(name: &str) -> Value {
    json!({"kind":"field","name":name,"namespaceUri":null})
}
fn any() -> Value {
    json!({"kind":"anyItem"})
}

fn boundary<'a>(projection: &'a Value, role: &str, name: Option<&str>) -> &'a Value {
    projection["boundaries"]
        .as_array()
        .unwrap()
        .iter()
        .find(|boundary| {
            boundary["role"] == role && name.is_none_or(|name| boundary["name"] == name)
        })
        .unwrap_or_else(|| panic!("missing boundary {role}/{name:?}"))
}

fn field<'a>(projection: &'a Value, boundary: &Value, path: &[Value]) -> &'a Value {
    projection["fields"]
        .as_array()
        .unwrap()
        .iter()
        .find(|field| field["boundaryId"] == boundary["id"] && field["path"] == json!(path))
        .unwrap_or_else(|| panic!("missing field in boundary {} at {path:?}", boundary["id"]))
}

fn rule<'a>(projection: &'a Value, field: &Value) -> &'a Value {
    projection["rules"]
        .as_array()
        .unwrap()
        .iter()
        .find(|rule| rule["outputFieldId"] == field["id"])
        .unwrap_or_else(|| panic!("missing output rule for field {field:#}"))
}

fn control<'a>(projection: &'a Value, kind: &str) -> &'a Value {
    projection["controls"]
        .as_array()
        .unwrap()
        .iter()
        .find(|control| control["kind"] == kind)
        .unwrap_or_else(|| panic!("missing control kind {kind}"))
}

fn field_dependencies(owner: &Value) -> BTreeSet<String> {
    owner["dependencies"]
        .as_array()
        .unwrap()
        .iter()
        .filter(|dependency| dependency["kind"] == "field")
        .map(|dependency| dependency["fieldId"].as_str().unwrap().to_string())
        .collect()
}

fn assert_field_dependency(owner: &Value, field: &Value, role: &str) {
    assert!(
        owner["dependencies"]
            .as_array()
            .unwrap()
            .iter()
            .any(|dependency| dependency
                == &json!({"kind":"field","fieldId":field["id"],"role":role})),
        "missing {role} field dependency {} in {owner:#}",
        field["path"]
    );
}

fn assert_constant_dependency(owner: &Value, literal: Value, role: &str) {
    assert!(owner["dependencies"].as_array().unwrap().iter().any(|dependency| dependency == &json!({"kind":"constant","value":literal,"role":role})), "missing {role} constant dependency in {owner:#}");
}

fn details(owner: &Value) -> Vec<&Value> {
    fn walk<'a>(detail: &'a Value, output: &mut Vec<&'a Value>) {
        output.push(detail);
        for property in ["children", "arguments"] {
            if let Some(children) = detail[property].as_array() {
                for child in children {
                    walk(child, output);
                }
            }
        }
    }
    let mut output = Vec::new();
    for detail in owner["details"].as_array().unwrap() {
        walk(detail, &mut output);
    }
    output
}

fn assert_relationship(
    projection: &Value,
    kind: &str,
    from_kind: &str,
    from_id: &str,
    to_kind: &str,
    to_id: &str,
) {
    let reference = |kind: &str, id: &str| {
        let mut value = json!({"kind":kind});
        value[format!("{kind}Id")] = json!(id);
        value
    };
    assert!(
        projection["relationships"].as_array().unwrap().iter().any(
            |relationship| relationship["kind"] == kind
                && relationship["from"] == reference(from_kind, from_id)
                && relationship["to"] == reference(to_kind, to_id)
        ),
        "missing {kind} relationship {from_kind}:{from_id} → {to_kind}:{to_id}"
    );
}

fn assert_projection_integrity(projection: &Value, native: &[u8]) {
    let collections = [
        ("boundary", "boundaries"),
        ("field", "fields"),
        ("parameter", "parameters"),
        ("rule", "rules"),
        ("control", "controls"),
    ];
    let mut ids = BTreeMap::new();
    for (kind, collection) in collections {
        let records = projection[collection].as_array().unwrap();
        let unique: BTreeSet<_> = records
            .iter()
            .map(|record| record["id"].as_str().unwrap())
            .collect();
        assert_eq!(records.len(), unique.len(), "duplicate {kind} identity");
        ids.insert(kind, unique);
    }
    let relationships = projection["relationships"].as_array().unwrap();
    let relationship_ids: BTreeSet<_> = relationships
        .iter()
        .map(|relationship| relationship["id"].as_str().unwrap())
        .collect();
    assert_eq!(
        relationships.len(),
        relationship_ids.len(),
        "duplicate relationship identity"
    );
    let retained: Value = serde_json::from_slice(native).unwrap();
    let prefix = if retained.get("__ferrule_file").is_some() {
        "/document"
    } else {
        ""
    };
    fn walk(value: &Value, check: &mut impl FnMut(&Value)) {
        if let Some(object) = value.as_object() {
            check(value);
            for child in object.values() {
                walk(child, check);
            }
        } else if let Some(array) = value.as_array() {
            for child in array {
                walk(child, check);
            }
        }
    }
    walk(projection, &mut |value| {
        if let Some(kind) = value["kind"].as_str()
            && let Some(collection) = ids.get(kind)
        {
            let property = format!("{kind}Id");
            if let Some(id) = value[property].as_str() {
                assert!(collection.contains(id), "dangling {kind} reference {id}");
            }
        }
        if value["documentId"].as_str() == Some("entry.native.json") {
            match value["kind"].as_str().unwrap() {
                "document" => {}
                "jsonPointer" => assert!(
                    retained
                        .pointer(value["pointer"].as_str().unwrap())
                        .is_some(),
                    "native pointer must resolve: {value:#}"
                ),
                "graphNode" => {
                    let node = value["nodeId"].as_str().unwrap();
                    let pointer = if let Some(function) = value["functionId"].as_str() {
                        format!("{prefix}/user_functions/{function}/body/nodes/{node}")
                    } else {
                        format!("{prefix}/graph/nodes/{node}")
                    };
                    assert!(
                        retained.pointer(&pointer).is_some(),
                        "native graph identity must resolve: {value:#}"
                    );
                }
                other => panic!("unqualified native locator type {other}"),
            }
        }
    });
    for field in projection["fields"].as_array().unwrap() {
        assert!(ids["boundary"].contains(field["boundaryId"].as_str().unwrap()));
    }
    for rule in projection["rules"].as_array().unwrap() {
        assert!(ids["field"].contains(rule["outputFieldId"].as_str().unwrap()));
    }
    for boundary in projection["boundaries"].as_array().unwrap() {
        if let Some(root) = boundary["rootFieldId"].as_str() {
            assert!(ids["field"].contains(root));
        }
    }
    for capability in projection["capabilities"].as_array().unwrap() {
        for id in capability["boundaryIds"].as_array().unwrap() {
            assert!(ids["boundary"].contains(id.as_str().unwrap()));
        }
    }
}
