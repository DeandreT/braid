mod support;

use ferrule_ir::Value as ScalarValue;
use ferrule_mapping::{Node, project_file};
use serde_json::{Value, json};
use support::*;

#[test]
fn every_retained_native_fixture_round_trips_through_the_pinned_codec() {
    let directory = concat!(env!("CARGO_MANIFEST_DIR"), "/tests/fixtures");
    let mut fixtures = std::fs::read_dir(directory)
        .unwrap()
        .map(|entry| entry.unwrap().path())
        .filter(|path| {
            path.extension()
                .is_some_and(|extension| extension == "json")
        })
        .map(|path| std::fs::read(path).unwrap())
        .collect::<Vec<_>>();
    // The invalid-cycle fixture is retained outside application packaging.
    fixtures.push(
        include_bytes!("../../../dotnet/tests/Braid.Tests/Fixtures/validation-cycle.json").to_vec(),
    );
    assert!(fixtures.len() >= 15);
    let mut legacy = 0;
    for native in &fixtures {
        let decoded = project_file::decode_bytes(native).unwrap();
        let encoded = project_file::encode_pretty(&decoded).unwrap();
        let again = project_file::decode_bytes(encoded.as_bytes()).unwrap();
        assert_eq!(
            serde_json::to_value(&decoded).unwrap(),
            serde_json::to_value(&again).unwrap()
        );
        assert_eq!(project_file::encode_pretty(&again).unwrap(), encoded);
        let marked = |bytes: &[u8]| {
            serde_json::from_slice::<Value>(bytes).unwrap()["__ferrule_file"].is_object()
        };
        legacy += usize::from(!marked(native));
        // The encoder writes the legacy form whenever it represents the model exactly,
        // so native pointers can lose the envelope prefix and nothing else. Projection
        // identities hash those pointers, so projections compare within one form.
        let prefix = |bytes: &[u8]| if marked(bytes) { "/document" } else { "" };
        let same_form = marked(native) == marked(encoded.as_bytes());
        let operations: &[&str] = if same_form {
            &["inspect", "validate", "project"]
        } else {
            &["inspect", "validate"]
        };
        for &operation in operations {
            let mut original = process(&request(operation, native), native);
            let mut reencoded =
                process(&request(operation, encoded.as_bytes()), encoded.as_bytes());
            strip_pointer_prefix(&mut original, prefix(native));
            strip_pointer_prefix(&mut reencoded, prefix(encoded.as_bytes()));
            assert_eq!(original["result"], reencoded["result"], "{operation}");
            assert_eq!(original["failure"], reencoded["failure"], "{operation}");
        }
    }
    assert!(
        legacy > 0,
        "legacy unmarked files are part of the retained set"
    );
}

fn strip_pointer_prefix(value: &mut Value, prefix: &str) {
    match value {
        Value::Object(object) => {
            for (key, item) in object.iter_mut() {
                match item {
                    Value::String(text) if key == "pointer" => {
                        *text = text.strip_prefix(prefix).unwrap().to_string();
                    }
                    _ => strip_pointer_prefix(item, prefix),
                }
            }
        }
        Value::Array(items) => items
            .iter_mut()
            .for_each(|item| strip_pointer_prefix(item, prefix)),
        _ => {}
    }
}

#[test]
fn self_authored_documents_use_the_authoritative_decoder() {
    for native in [BASIC, INVALID, INVALID_FUNCTION, EXACT, RESOURCES] {
        let expected = project_file::decode_bytes(native).unwrap();
        let actual = braid_ferrule_host::decode::decode_native(native).unwrap();
        assert_eq!(
            serde_json::to_value(actual.project).unwrap(),
            serde_json::to_value(expected).unwrap()
        );
    }
}

#[test]
fn native_codec_preserves_exact_integer_float_tag_and_negative_zero() {
    let decoded = braid_ferrule_host::decode::decode_native(EXACT).unwrap();
    assert_eq!(decoded.document_prefix, "/document");
    let Node::Const {
        value: ScalarValue::Int(integer),
    } = decoded.project.graph.nodes[&1]
    else {
        panic!("the exact integer must remain Int");
    };
    assert_eq!(integer, 9_007_199_254_740_993);
    for (id, bits) in [(2, 0x3ff0000000000000_u64), (3, 0x8000000000000000_u64)] {
        let Node::Const {
            value: ScalarValue::Float(float),
        } = decoded.project.graph.nodes[&id]
        else {
            panic!("node {id} must remain Float");
        };
        assert_eq!(float.to_bits(), bits);
    }
    let original = EXACT.to_vec();
    for operation in ["inspect", "validate"] {
        let header = request(operation, EXACT);
        success(&process(&header, EXACT), &header, operation);
    }
    assert_eq!(EXACT, original);
    assert_eq!(digest(EXACT), digest(&original));
}

#[test]
fn ordinary_native_negative_zero_uses_the_same_codec_semantics() {
    let envelope: Value = serde_json::from_slice(EXACT).unwrap();
    let native = serde_json::to_vec(&envelope["document"]).unwrap();
    let decoded = braid_ferrule_host::decode::decode_native(&native).unwrap();
    let direct = project_file::decode_bytes(&native).unwrap();
    assert_eq!(decoded.document_prefix, "");
    let Node::Const {
        value: ScalarValue::Float(actual),
    } = decoded.project.graph.nodes[&3]
    else {
        panic!("ordinary negative zero must remain Float");
    };
    let Node::Const {
        value: ScalarValue::Float(expected),
    } = direct.graph.nodes[&3]
    else {
        panic!("the direct decoder must retain negative zero");
    };
    assert_eq!(actual.to_bits(), expected.to_bits());
    assert_eq!(actual.to_bits(), 0x8000000000000000);
}

#[test]
fn legacy_ignored_metadata_does_not_trigger_a_second_json_tree_decode() {
    let text = std::str::from_utf8(BASIC).unwrap();
    let prefix = text.trim_end().strip_suffix('}').unwrap();
    let native = format!("{prefix},\"ignoredLegacyMetadata\":{{\"overflow\":1e10000,\"string\":\"9007199254740993\"}}}}").into_bytes();
    assert!(
        serde_json::from_slice::<Value>(&native).is_err(),
        "a generic tree parse rejects this ignored number"
    );
    let direct = project_file::decode_bytes(&native).unwrap();
    let decoded = braid_ferrule_host::decode::decode_native(&native).unwrap();
    assert_eq!(decoded.document_prefix, "");
    assert_eq!(
        serde_json::to_value(decoded.project).unwrap(),
        serde_json::to_value(direct).unwrap()
    );
    let header = request("inspect", &native);
    success(&process(&header, &native), &header, "inspect");
}

#[test]
fn authoritative_decoder_rejects_duplicate_root_markers_and_bom_prefixes() {
    let envelope: Value = serde_json::from_slice(EXACT).unwrap();
    let marker = serde_json::to_string(&envelope["__ferrule_file"]).unwrap();
    let tail = std::str::from_utf8(EXACT)
        .unwrap()
        .trim_start()
        .strip_prefix('{')
        .unwrap();
    let duplicate_marker = format!("{{\"__ferrule_file\":{marker},{tail}").into_bytes();
    let mut cases = vec![duplicate_marker];
    for native in [BASIC, EXACT] {
        let mut prefixed = vec![0xef, 0xbb, 0xbf];
        prefixed.extend_from_slice(native);
        cases.push(prefixed);
    }
    for native in cases {
        assert!(project_file::decode_bytes(&native).is_err());
        assert!(braid_ferrule_host::decode::decode_native(&native).is_err());
        let header = request("inspect", &native);
        assert_eq!(failure(&process(&header, &native)), "nativeDecodeFailed");
    }
}

#[test]
fn oversized_named_validation_owner_identity_is_refused_without_truncation() {
    for target in [false, true] {
        for (limit, name) in [(64, "x".repeat(65)), (2048, "é".repeat(1025))] {
            let native = named_owner_document(target, &name, false);
            let direct = ferrule_engine::validate(&project_file::decode_bytes(&native).unwrap());
            assert!(direct.iter().any(|issue| {
                issue
                    .owner
                    .as_ref()
                    .is_some_and(|owner| owner_endpoint_name(owner) == Some(name.as_str()))
            }));
            let mut header = request("validate", &native);
            header.limits.maximum_text_bytes = limit;
            let response = process(&header, &native);
            assert_eq!(failure(&response), "inspectionLimitExceeded");
            assert!(
                !serde_json::to_string(&response)
                    .unwrap()
                    .contains(name.as_str())
            );
        }
    }
}

#[test]
fn named_owner_identity_at_the_byte_limit_preserves_its_declaration_index() {
    for target in [false, true] {
        for (limit, name) in [(64, "x".repeat(64)), (2048, "é".repeat(1024))] {
            let native = named_owner_document(target, &name, false);
            let mut header = request("validate", &native);
            header.limits.maximum_text_bytes = limit;
            let response = process(&header, &native);
            success(&response, &header, "validate");
            let issues = response["result"]["issues"].as_array().unwrap();
            let issue = issues
                .iter()
                .find(|issue| {
                    let endpoint = if target {
                        &issue["owner"]["target"]
                    } else {
                        &issue["owner"]["endpoint"]
                    };
                    endpoint["name"] == name
                })
                .expect("the invalid second named boundary must retain an owner");
            let endpoint = if target {
                &issue["owner"]["target"]
            } else {
                &issue["owner"]["endpoint"]
            };
            assert_eq!(
                endpoint["kind"],
                if target { "namedTarget" } else { "namedSource" }
            );
            assert_eq!(endpoint["index"], "1");
            assert_eq!(endpoint["name"], name);
        }
    }
}

#[test]
fn omitted_diagnostics_do_not_bypass_named_owner_identity_admission() {
    use ferrule_engine::{ValidationEndpoint, ValidationOwner};
    for target in [false, true] {
        let name = "x".repeat(65);
        let native = named_owner_document(target, &name, true);
        let direct = ferrule_engine::validate(&project_file::decode_bytes(&native).unwrap());
        let oversized_position = direct
            .iter()
            .position(|issue| {
                issue
                    .owner
                    .as_ref()
                    .is_some_and(|owner| owner_endpoint_name(owner) == Some(name.as_str()))
            })
            .unwrap();
        assert!(
            oversized_position > 0,
            "oversized identity must occur after the one emitted issue"
        );
        let first_endpoint = match direct[0].owner.as_ref().unwrap() {
            ValidationOwner::Endpoint(endpoint) => endpoint,
            ValidationOwner::Scope(location) => &location.target,
            owner => panic!("unexpected first owner: {owner:?}"),
        };
        assert!(matches!(
            first_endpoint,
            ValidationEndpoint::NamedSource { index: 0, .. }
                | ValidationEndpoint::NamedTarget { index: 0, .. }
        ));
        let mut header = request("validate", &native);
        header.limits.maximum_text_bytes = 64;
        header.limits.maximum_diagnostics = 1;
        assert_eq!(
            failure(&process(&header, &native)),
            "inspectionLimitExceeded"
        );
    }
}

#[test]
fn long_scope_native_pointer_uses_document_fallback_and_retains_typed_path() {
    let mut native: Value = serde_json::from_slice(BASIC).unwrap();
    let mut schema = json!({"name":"leaf","kind":{"kind":"group","children":[{"name":"value","kind":{"kind":"scalar","ty":"string"}}]}});
    let mut scope = json!({"target_field":"leaf","bindings":[{"target_field":"value","node":999}],"children":[]});
    for level in 0..8 {
        let name = format!("level{level}");
        schema = json!({"name":name,"kind":{"kind":"group","children":[schema]}});
        scope = json!({"target_field":name,"bindings":[],"children":[scope]});
    }
    scope["target_field"] = json!("");
    native["target"] = schema;
    native["root"] = scope;
    let bytes = serde_json::to_vec(&native).unwrap();
    let direct = ferrule_engine::validate(&project_file::decode_bytes(&bytes).unwrap());
    let expected = direct
        .iter()
        .find_map(|issue| match &issue.owner {
            Some(ferrule_engine::ValidationOwner::Scope(location)) if location.path.len() == 8 => {
                Some(issue.owner.as_ref().unwrap())
            }
            _ => None,
        })
        .expect("the leaf binding must produce a nested scope issue");
    assert!(expected_owner_pointer(expected).len() > 64);
    let mut header = request("validate", &bytes);
    header.limits.maximum_text_bytes = 64;
    let response = process(&header, &bytes);
    success(&response, &header, "validate");
    let issue = response["result"]["issues"]
        .as_array()
        .unwrap()
        .iter()
        .find(|issue| issue["owner"] == expected_owner(expected))
        .unwrap();
    assert_eq!(
        issue["nativeLocator"],
        json!({"kind":"document","documentId":"entry.native.json"})
    );
    assert_eq!(issue["owner"]["path"].as_array().unwrap().len(), 8);
}

#[test]
fn long_schema_native_pointer_fallback_preserves_the_in_memory_engine_owner() {
    use ferrule_ir::SchemaNode;
    let mut project = project_file::decode_bytes(BASIC).unwrap();
    let mut schema = SchemaNode::group("leaf", Vec::new());
    schema.nullable = true;
    for level in 0..8 {
        schema = SchemaNode::group(format!("level{level}"), vec![schema]);
    }
    project.source = schema;
    let expected = ferrule_engine::validate(&project)
        .into_iter()
        .find_map(|issue| match issue.owner {
            Some(owner @ ferrule_engine::ValidationOwner::SchemaNode(_)) => Some(owner),
            _ => None,
        })
        .expect("the deliberately invalid in-memory leaf must have a schema owner");
    assert!(expected_owner_pointer(&expected).len() > 64);
    let mut header = request("validate", BASIC);
    header.limits.maximum_text_bytes = 64;
    let actual =
        braid_ferrule_host::validation::validate(&project, "entry.native.json", "", &header.limits)
            .unwrap();
    let actual = serde_json::to_value(actual).unwrap();
    let issue = actual["issues"]
        .as_array()
        .unwrap()
        .iter()
        .find(|issue| issue["owner"] == expected_owner(&expected))
        .unwrap();
    assert_eq!(
        issue["nativeLocator"],
        json!({"kind":"document","documentId":"entry.native.json"})
    );
    assert_eq!(issue["owner"]["path"].as_array().unwrap().len(), 8);
}

fn named_owner_document(target: bool, name: &str, first_invalid: bool) -> Vec<u8> {
    let mut native: Value = serde_json::from_slice(BASIC).unwrap();
    let invalid_root = json!({"bindings":[{"target_field":"missing","node":999}],"children":[]});
    if target {
        native["extra_targets"] = json!([
            {"name":"first","schema":native["target"],"root":if first_invalid { &invalid_root } else { &native["root"] }},
            {"name":name,"schema":native["target"],"root":invalid_root}
        ]);
    } else {
        let invalid_options = json!({"json5":true,"json_lines":true});
        native["extra_sources"] = json!([
            {"name":"first","path":"do-not-read.json","schema":native["source"],"options":if first_invalid { invalid_options.clone() } else { json!({}) }},
            {"name":name,"path":"do-not-read.json","schema":native["source"],"options":invalid_options}
        ]);
    }
    serde_json::to_vec(&native).unwrap()
}

fn owner_endpoint_name(owner: &ferrule_engine::ValidationOwner) -> Option<&str> {
    use ferrule_engine::{ValidationEndpoint, ValidationOwner};
    let endpoint = match owner {
        ValidationOwner::Endpoint(endpoint) => endpoint,
        ValidationOwner::SchemaNode(location) => &location.endpoint,
        ValidationOwner::Scope(location) => &location.target,
        _ => return None,
    };
    match endpoint {
        ValidationEndpoint::NamedSource { name, .. }
        | ValidationEndpoint::NamedTarget { name, .. } => Some(name),
        _ => None,
    }
}

#[test]
fn independently_specified_basic_metadata_is_stable() {
    let header = request("inspect", BASIC);
    let first = process(&header, BASIC);
    let second = process(&header, BASIC);
    success(&first, &header, "inspect");
    assert_eq!(first, second);
    let result = &first["result"];
    assert_eq!(result["counts"]["schemaNodes"], 10);
    assert_eq!(result["counts"]["graphNodes"], 3);
    assert_eq!(result["counts"]["scopeNodes"], 2);
    assert_eq!(result["counts"]["userFunctions"], 0);
    assert_eq!(result["counts"]["failureRules"], 0);
    assert_eq!(result["truncated"], false);
    let boundaries = result["boundaries"].as_array().unwrap();
    assert_eq!(boundaries.len(), 2);
    assert_eq!(boundaries[0]["role"], "primaryInput");
    assert_eq!(boundaries[0]["rootName"], "Source");
    assert_eq!(boundaries[1]["role"], "primaryOutput");
    assert_eq!(boundaries[1]["rootName"], "Target");
    assert_eq!(boundaries[0]["nativeLocator"]["pointer"], "/source");
    assert_eq!(boundaries[1]["nativeLocator"]["pointer"], "/target");
    for operation in ["preview", "build"] {
        let capability = result["capabilities"]
            .as_array()
            .unwrap()
            .iter()
            .find(|c| c["operation"] == operation)
            .unwrap();
        assert_eq!(capability["status"], "notQualified");
    }
}

#[test]
fn versioned_boundary_locators_resolve_against_retained_bytes() {
    let header = request("inspect", EXACT);
    let response = process(&header, EXACT);
    success(&response, &header, "inspect");
    let native: Value = serde_json::from_slice(EXACT).unwrap();
    for boundary in response["result"]["boundaries"].as_array().unwrap() {
        let locator = &boundary["nativeLocator"];
        assert_eq!(locator["kind"], "jsonPointer");
        assert_eq!(locator["documentId"], "entry.native.json");
        let pointer = locator["pointer"].as_str().unwrap();
        assert!(pointer.starts_with("/document/"));
        let owner = native.pointer(pointer).expect("locator must resolve");
        assert_eq!(owner["name"], boundary["rootName"]);
    }
}

#[test]
fn validation_agrees_with_direct_engine_issues_and_typed_owners() {
    for native in [BASIC, INVALID, INVALID_FUNCTION, EXACT, RESOURCES] {
        let project = project_file::decode_bytes(native).unwrap();
        let expected = ferrule_engine::validate(&project);
        let header = request("validate", native);
        let response = process(&header, native);
        success(&response, &header, "validate");
        let result = &response["result"];
        assert_eq!(result["isValid"], expected.is_empty());
        assert_eq!(result["totalIssueCount"], expected.len());
        assert_eq!(result["emittedIssueCount"], expected.len());
        assert_eq!(result["truncated"], false);
        let actual = result["issues"].as_array().unwrap();
        assert_eq!(actual.len(), expected.len());
        let retained: Value = serde_json::from_slice(native).unwrap();
        let document_prefix = if retained.get("__ferrule_file").is_some() {
            "/document"
        } else {
            ""
        };
        for (actual, expected) in actual.iter().zip(expected) {
            assert_eq!(actual["location"], expected.location);
            assert_eq!(actual["message"], expected.message);
            assert_eq!(actual["locationTruncated"], false);
            assert_eq!(actual["messageTruncated"], false);
            assert_eq!(
                actual["owner"],
                expected
                    .owner
                    .as_ref()
                    .map(expected_owner)
                    .unwrap_or(Value::Null)
            );
            assert_locator_resolves(&actual["nativeLocator"], &retained, document_prefix);
        }
    }
    assert!(!ferrule_engine::validate(&project_file::decode_bytes(INVALID).unwrap()).is_empty());
}

#[test]
fn function_identity_exceeding_json_precision_remains_exact_in_worker_diagnostics() {
    let header = request("validate", INVALID_FUNCTION);
    let response = process(&header, INVALID_FUNCTION);
    success(&response, &header, "validate");
    let issues = response["result"]["issues"].as_array().unwrap();
    assert!(
        issues
            .iter()
            .any(|issue| issue["owner"]["kind"] == "userFunction")
    );
    assert!(
        issues
            .iter()
            .any(|issue| issue["owner"]["kind"] == "graphNode")
    );
    for issue in issues {
        if let Some(function_id) = issue["owner"]["functionId"].as_str() {
            assert_eq!(function_id, "18446744073709551615");
            match issue["nativeLocator"]["kind"].as_str().unwrap() {
                "graphNode" => assert_eq!(issue["nativeLocator"]["functionId"], function_id),
                "jsonPointer" => assert_eq!(
                    issue["nativeLocator"]["pointer"],
                    "/user_functions/18446744073709551615"
                ),
                other => panic!("unexpected function locator {other}"),
            }
        }
    }
}

#[test]
fn every_native_owner_variant_and_structural_step_preserves_typed_identity() {
    use braid_ferrule_host::validation::{owner_locator, owner_to_wire};
    use ferrule_engine::{
        ValidationEndpoint as Endpoint, ValidationOwner as Owner, ValidationSchemaLocation,
        ValidationSchemaStep as SchemaStep, ValidationScopeLocation,
        ValidationScopeStep as ScopeStep,
    };
    use ferrule_mapping::FunctionId;

    let endpoints = [
        Endpoint::Source,
        Endpoint::Target,
        Endpoint::NamedSource {
            index: 2,
            name: "a.b/~".into(),
        },
        Endpoint::NamedTarget {
            index: 3,
            name: "a.b/~".into(),
        },
    ];
    let mut owners = vec![
        Owner::GraphNode {
            function: None,
            node: u32::MAX,
        },
        Owner::GraphNode {
            function: Some(FunctionId::new(u64::MAX)),
            node: 7,
        },
        Owner::UserFunction(FunctionId::new(u64::MAX)),
        Owner::FailureRule { index: 3 },
    ];
    for endpoint in endpoints {
        owners.push(Owner::Endpoint(endpoint.clone()));
        owners.push(Owner::SchemaNode(ValidationSchemaLocation {
            endpoint: endpoint.clone(),
            path: vec![SchemaStep::Child(1), SchemaStep::DynamicField],
        }));
        owners.push(Owner::SchemaNode(ValidationSchemaLocation {
            endpoint: endpoint.clone(),
            path: vec![SchemaStep::Contains(2)],
        }));
        owners.push(Owner::SchemaNode(ValidationSchemaLocation {
            endpoint,
            path: vec![SchemaStep::DependentSchema(3)],
        }));
    }
    for target in [
        Endpoint::Target,
        Endpoint::NamedTarget {
            index: 1,
            name: "target/~".into(),
        },
    ] {
        owners.push(Owner::Scope(ValidationScopeLocation {
            target,
            path: vec![
                ScopeStep::Child(1),
                ScopeStep::DynamicChild(2),
                ScopeStep::Segment(3),
            ],
        }));
    }
    for owner in owners {
        assert_eq!(
            serde_json::to_value(owner_to_wire(&owner)).unwrap(),
            expected_owner(&owner)
        );
        for prefix in ["", "/document"] {
            let locator =
                serde_json::to_value(owner_locator(&owner, "entry.native.json", prefix)).unwrap();
            assert_eq!(locator["documentId"], "entry.native.json");
            match &owner {
                Owner::GraphNode { function, node } => {
                    assert_eq!(locator["kind"], "graphNode");
                    assert_eq!(locator["nodeId"], node.to_string());
                    assert_eq!(
                        locator["functionId"],
                        function
                            .map(|id| json!(id.get().to_string()))
                            .unwrap_or(Value::Null)
                    );
                }
                Owner::SchemaNode(location)
                    if location.path.iter().any(|step| {
                        matches!(
                            step,
                            SchemaStep::Contains(_) | SchemaStep::DependentSchema(_)
                        )
                    }) =>
                {
                    assert_eq!(
                        locator["kind"], "document",
                        "predicate owner remains exact while navigation uses the document fallback"
                    );
                }
                _ => {
                    assert_eq!(locator["kind"], "jsonPointer");
                    assert_eq!(
                        locator["pointer"],
                        format!("{prefix}{}", expected_owner_pointer(&owner))
                    );
                }
            }
        }
    }
}

fn expected_owner_pointer(owner: &ferrule_engine::ValidationOwner) -> String {
    use ferrule_engine::{
        ValidationEndpoint as Endpoint, ValidationOwner as Owner, ValidationSchemaStep,
        ValidationScopeStep,
    };
    let endpoint_pointer = |endpoint: &Endpoint| match endpoint {
        Endpoint::Source => "/source".to_string(),
        Endpoint::Target => "/target".to_string(),
        Endpoint::NamedSource { index, .. } => format!("/extra_sources/{index}"),
        Endpoint::NamedTarget { index, .. } => format!("/extra_targets/{index}"),
    };
    match owner {
        Owner::UserFunction(id) => format!("/user_functions/{}", id.get()),
        Owner::Endpoint(endpoint) => endpoint_pointer(endpoint),
        Owner::FailureRule { index } => format!("/failure_rules/{index}"),
        Owner::SchemaNode(location) => {
            let mut pointer = endpoint_pointer(&location.endpoint);
            if matches!(
                location.endpoint,
                Endpoint::NamedSource { .. } | Endpoint::NamedTarget { .. }
            ) {
                pointer.push_str("/schema");
            }
            for step in &location.path {
                match step {
                    ValidationSchemaStep::Child(index) => {
                        pointer.push_str(&format!("/kind/children/{index}"))
                    }
                    ValidationSchemaStep::DynamicField => pointer.push_str("/kind/dynamic"),
                    _ => panic!("predicate owners use a document locator"),
                }
            }
            pointer
        }
        Owner::Scope(location) => {
            let mut pointer = match location.target {
                Endpoint::Target => "/root".to_string(),
                Endpoint::NamedTarget { index, .. } => format!("/extra_targets/{index}/root"),
                _ => panic!("only target boundaries own scopes"),
            };
            for step in &location.path {
                match step {
                    ValidationScopeStep::Child(index) => {
                        pointer.push_str(&format!("/children/{index}"))
                    }
                    ValidationScopeStep::DynamicChild(index) => {
                        pointer.push_str(&format!("/dynamic_children/{index}/scope"))
                    }
                    ValidationScopeStep::Segment(index) => {
                        pointer.push_str(&format!("/segments/{index}"))
                    }
                }
            }
            pointer
        }
        _ => panic!("graph nodes use a typed graph locator"),
    }
}

#[test]
fn validation_truncation_reports_full_validity_and_total_count() {
    let expected = ferrule_engine::validate(&project_file::decode_bytes(INVALID).unwrap());
    assert!(expected.len() > 1);
    let mut header = request("validate", INVALID);
    header.limits.maximum_diagnostics = 1;
    let response = process(&header, INVALID);
    success(&response, &header, "validate");
    let result = &response["result"];
    assert_eq!(result["isValid"], false);
    assert_eq!(result["totalIssueCount"], expected.len());
    assert_eq!(result["emittedIssueCount"], 1);
    assert_eq!(result["issues"].as_array().unwrap().len(), 1);
    assert_eq!(result["truncated"], true);
}

#[test]
fn metadata_resources_remain_unresolved_without_file_or_network_access() {
    for operation in ["inspect", "validate"] {
        let header = request(operation, RESOURCES);
        let response = process(&header, RESOURCES);
        success(&response, &header, operation);
        if operation == "inspect" {
            let resources = response["result"]["unresolvedResources"]
                .as_array()
                .unwrap();
            assert!(resources.len() >= 6);
            let retained: Value = serde_json::from_slice(RESOURCES).unwrap();
            for resource in resources {
                assert_eq!(resource["resolution"], "unresolved");
                assert_eq!(resource["nativeLocator"]["documentId"], "entry.native.json");
                assert_locator_resolves(&resource["nativeLocator"], &retained, "");
            }
            let encoded = serde_json::to_string(&response).unwrap();
            assert!(!encoded.contains("braid-fixture-do-not"));
            assert!(!encoded.contains("127.0.0.1"));
            assert!(!encoded.contains("braid-fixture.invalid"));
        }
    }
}

fn assert_locator_resolves(locator: &Value, retained: &Value, prefix: &str) {
    assert_eq!(locator["documentId"], "entry.native.json");
    match locator["kind"].as_str().unwrap() {
        "document" => assert!(retained.is_object()),
        "jsonPointer" => assert!(
            retained
                .pointer(locator["pointer"].as_str().unwrap())
                .is_some(),
            "unresolved locator: {locator:#}"
        ),
        "graphNode" => {
            let node = locator["nodeId"].as_str().unwrap();
            let pointer = if let Some(function) = locator["functionId"].as_str() {
                format!("{prefix}/user_functions/{function}/body/nodes/{node}")
            } else {
                format!("{prefix}/graph/nodes/{node}")
            };
            assert!(
                retained.pointer(&pointer).is_some(),
                "unresolved graph locator: {locator:#}"
            );
        }
        other => panic!("unknown native locator kind {other}"),
    }
}

fn expected_endpoint(endpoint: &ferrule_engine::ValidationEndpoint) -> Value {
    use ferrule_engine::ValidationEndpoint::*;
    match endpoint {
        Source => json!({"kind":"source"}),
        Target => json!({"kind":"target"}),
        NamedSource { index, name } => {
            json!({"kind":"namedSource","index":index.to_string(),"name":name})
        }
        NamedTarget { index, name } => {
            json!({"kind":"namedTarget","index":index.to_string(),"name":name})
        }
    }
}

pub fn expected_owner(owner: &ferrule_engine::ValidationOwner) -> Value {
    use ferrule_engine::{ValidationOwner::*, ValidationSchemaStep, ValidationScopeStep};
    match owner {
        GraphNode { function, node } => {
            json!({"kind":"graphNode","functionId":function.map(|id| id.get().to_string()),"nodeId":node.to_string()})
        }
        UserFunction(id) => json!({"kind":"userFunction","functionId":id.get().to_string()}),
        Endpoint(endpoint) => json!({"kind":"endpoint","endpoint":expected_endpoint(endpoint)}),
        SchemaNode(location) => {
            let path: Vec<_> = location
                .path
                .iter()
                .map(|step| match step {
                    ValidationSchemaStep::Child(index) => {
                        json!({"kind":"child","index":index.to_string()})
                    }
                    ValidationSchemaStep::DynamicField => json!({"kind":"dynamicField"}),
                    ValidationSchemaStep::Contains(index) => {
                        json!({"kind":"contains","index":index.to_string()})
                    }
                    ValidationSchemaStep::DependentSchema(index) => {
                        json!({"kind":"dependentSchema","index":index.to_string()})
                    }
                })
                .collect();
            json!({"kind":"schemaNode","endpoint":expected_endpoint(&location.endpoint),"path":path})
        }
        Scope(location) => {
            let path: Vec<_> = location
                .path
                .iter()
                .map(|step| match step {
                    ValidationScopeStep::Child(index) => {
                        json!({"kind":"child","index":index.to_string()})
                    }
                    ValidationScopeStep::DynamicChild(index) => {
                        json!({"kind":"dynamicChild","index":index.to_string()})
                    }
                    ValidationScopeStep::Segment(index) => {
                        json!({"kind":"segment","index":index.to_string()})
                    }
                })
                .collect();
            json!({"kind":"scope","target":expected_endpoint(&location.target),"path":path})
        }
        FailureRule { index } => json!({"kind":"failureRule","index":index.to_string()}),
    }
}
