use std::collections::{BTreeMap, BTreeSet};

use ferrule_ir::{Instance, ScalarType, SchemaNode, Value as ScalarValue};
use ferrule_mapping::{
    Binding, Graph, IterationOutput, Node, Project, Scope, ScopeIteration, SequenceExpr,
    project_file,
};
use serde_json::{Value, json};
use weft_ferrule_host::protocol::Limits;

#[test]
fn an_explicit_empty_frame_at_a_noniterating_root_has_no_root_field_lineage() {
    let project = project(
        SchemaNode::group("Source", vec![text_schema("code")]),
        SchemaNode::group("Target", vec![text_schema("mapped")]),
        vec![(1, read(&["code"], Some(&[])))],
        scope("", vec![("mapped", 1)], vec![]),
    );
    let source = g([("code", s("ROOT-CODE"))]);
    let output = ferrule_engine::run(&project, &source).unwrap();
    assert_eq!(scalar_at(&output, &["mapped"]), &ScalarValue::Null);

    let (projection, bytes) = projected(&project);
    let input_code = field(&projection, "primaryInput", &["code"]);
    let mapping = rule(&projection, &["mapped"]);
    assert!(
        !dependencies(mapping).contains(input_code["id"].as_str().unwrap()),
        "the native explicit frame has no matching position frame"
    );
    assert!(!detail_field_references(mapping).contains(input_code["id"].as_str().unwrap()));
    assert_exact_or_partial(&projection, mapping, &[], 1, &bytes);
}

#[test]
fn a_path_in_an_explicit_rows_frame_remains_relative_to_that_item() {
    let source_schema = SchemaNode::group(
        "Source",
        vec![repeating(
            "rows",
            vec![
                text_schema("code"),
                SchemaNode::group("rows", vec![text_schema("code")]),
            ],
        )],
    );
    let mut output_scope = scope("out", vec![("mapped", 1)], vec![]);
    output_scope.iteration = ScopeIteration::Source(strings(&["rows"]));
    let project = project(
        source_schema,
        SchemaNode::group(
            "Target",
            vec![repeating("out", vec![text_schema("mapped")])],
        ),
        vec![(1, read(&["rows", "code"], Some(&["rows"])))],
        scope("", vec![], vec![output_scope]),
    );
    let source = g([(
        "rows",
        Instance::Repeated(vec![g([
            ("code", s("OUTER-CODE")),
            ("rows", g([("code", s("NESTED-CODE"))])),
        ])]),
    )]);
    let output = ferrule_engine::run(&project, &source).unwrap();
    assert_eq!(
        scalar_at(&repeated_at(&output, &["out"])[0], &["mapped"]),
        &ScalarValue::String("NESTED-CODE".into())
    );

    let (projection, bytes) = projected(&project);
    let expected = field(&projection, "primaryInput", &["rows", "rows", "code"]);
    let wrong = field(&projection, "primaryInput", &["rows", "code"]);
    let mapping = rule(&projection, &["out", "mapped"]);
    assert!(
        !dependencies(mapping).contains(wrong["id"].as_str().unwrap()),
        "an explicit frame does not remove a matching path prefix"
    );
    assert!(!detail_field_references(mapping).contains(wrong["id"].as_str().unwrap()));
    assert_exact_or_partial(&projection, mapping, &[expected], 1, &bytes);
}

#[test]
fn a_singular_terminal_source_scope_keeps_the_inner_value_context() {
    let mut detail_scope = scope("mapped", vec![("code", 1)], vec![]);
    detail_scope.iteration = ScopeIteration::Source(strings(&["detail"]));
    detail_scope.iteration_output = IterationOutput::First;
    let project = project(
        SchemaNode::group(
            "Source",
            vec![
                text_schema("code"),
                SchemaNode::group("detail", vec![text_schema("code")]),
            ],
        ),
        SchemaNode::group(
            "Target",
            vec![SchemaNode::group("mapped", vec![text_schema("code")])],
        ),
        vec![(1, read(&["code"], None))],
        scope("", vec![], vec![detail_scope]),
    );
    let source = g([
        ("code", s("ROOT-CODE")),
        ("detail", g([("code", s("DETAIL-CODE"))])),
    ]);
    let output = ferrule_engine::run(&project, &source).unwrap();
    assert_eq!(
        scalar_at(&output, &["mapped", "code"]),
        &ScalarValue::String("DETAIL-CODE".into())
    );

    let (projection, bytes) = projected(&project);
    let expected = field(&projection, "primaryInput", &["detail", "code"]);
    let mapping = rule(&projection, &["mapped", "code"]);
    assert_exact_or_partial(&projection, mapping, &[expected], 1, &bytes);
    if projection["coverage"]["status"] == "complete" {
        let wrong = field(&projection, "primaryInput", &["code"]);
        assert!(!dependencies(mapping).contains(wrong["id"].as_str().unwrap()));
    }
}

#[test]
fn nested_native_collection_names_and_explicit_selector_suffixes_keep_distinct_owners() {
    let mut lines_scope = scope(
        "lines",
        vec![
            ("outer", 1),
            ("inner", 2),
            ("innerSuffix", 3),
            ("outerSuffix", 4),
        ],
        vec![],
    );
    lines_scope.iteration = ScopeIteration::Source(strings(&["lines"]));
    let mut rows_scope = scope("out", vec![], vec![lines_scope]);
    rows_scope.iteration = ScopeIteration::Source(strings(&["rows"]));
    let project = project(
        SchemaNode::group(
            "Source",
            vec![repeating(
                "rows",
                vec![
                    text_schema("code"),
                    repeating("lines", vec![text_schema("code")]),
                ],
            )],
        ),
        SchemaNode::group(
            "Target",
            vec![repeating(
                "out",
                vec![repeating(
                    "lines",
                    vec![
                        text_schema("outer"),
                        text_schema("inner"),
                        text_schema("innerSuffix"),
                        text_schema("outerSuffix"),
                    ],
                )],
            )],
        ),
        vec![
            (1, read(&["code"], Some(&["rows"]))),
            (2, read(&["code"], Some(&["lines"]))),
            (3, read(&["code"], Some(&["rows", "lines"]))),
            (4, read(&["code"], Some(&["prefix", "rows"]))),
        ],
        scope("", vec![], vec![rows_scope]),
    );
    let source = g([(
        "rows",
        Instance::Repeated(vec![
            g([
                ("code", s("OUTER-1")),
                (
                    "lines",
                    Instance::Repeated(vec![g([("code", s("INNER-1"))])]),
                ),
            ]),
            g([
                ("code", s("OUTER-2")),
                (
                    "lines",
                    Instance::Repeated(vec![g([("code", s("INNER-2"))])]),
                ),
            ]),
        ]),
    )]);
    let output = ferrule_engine::run(&project, &source).unwrap();
    for (index, row) in repeated_at(&output, &["out"]).iter().enumerate() {
        let line = &repeated_at(row, &["lines"])[0];
        let outer = ScalarValue::String(format!("OUTER-{}", index + 1));
        let inner = ScalarValue::String(format!("INNER-{}", index + 1));
        assert_eq!(scalar_at(line, &["outer"]), &outer);
        assert_eq!(scalar_at(line, &["outerSuffix"]), &outer);
        assert_eq!(scalar_at(line, &["inner"]), &inner);
        assert_eq!(scalar_at(line, &["innerSuffix"]), &inner);
    }

    let (projection, bytes) = projected(&project);
    let outer = field(&projection, "primaryInput", &["rows", "code"]);
    let inner = field(&projection, "primaryInput", &["rows", "lines", "code"]);
    for (name, expected, forbidden, node) in [
        ("outer", outer, inner, 1),
        ("inner", inner, outer, 2),
        ("innerSuffix", inner, outer, 3),
        ("outerSuffix", outer, inner, 4),
    ] {
        let mapping = rule(&projection, &["out", "lines", name]);
        assert!(
            !dependencies(mapping).contains(forbidden["id"].as_str().unwrap()),
            "native explicit collection identity for {name} must not become the schema absolute path"
        );
        assert!(!detail_field_references(mapping).contains(forbidden["id"].as_str().unwrap()));
        assert_exact_or_partial(&projection, mapping, &[expected], node, &bytes);
    }
}

#[test]
fn a_named_outer_position_inside_generated_items_uses_the_outer_iteration_control() {
    let mut generated = scope(
        "generated",
        vec![("outerIndex", 3), ("generatedIndex", 4), ("value", 2)],
        vec![],
    );
    generated.iteration = ScopeIteration::Sequence(SequenceExpr::Generate {
        from: None,
        to: 1,
        item: 2,
    });
    let mut rows = scope("out", vec![], vec![generated]);
    rows.iteration = ScopeIteration::Source(strings(&["rows"]));
    let project = project(
        SchemaNode::group("Source", vec![repeating("rows", vec![])]),
        SchemaNode::group(
            "Target",
            vec![repeating(
                "out",
                vec![repeating(
                    "generated",
                    vec![
                        int_schema("outerIndex"),
                        int_schema("generatedIndex"),
                        int_schema("value"),
                    ],
                )],
            )],
        ),
        vec![
            (
                1,
                Node::Const {
                    value: ScalarValue::Int(3),
                },
            ),
            (2, read(&[], None)),
            (
                3,
                Node::Position {
                    collection: strings(&["rows"]),
                },
            ),
            (4, Node::Position { collection: vec![] }),
        ],
        scope("", vec![], vec![rows]),
    );
    let source = g([("rows", Instance::Repeated(vec![g([]), g([])]))]);
    let output = ferrule_engine::run(&project, &source).unwrap();
    for (outer, row) in repeated_at(&output, &["out"]).iter().enumerate() {
        for (inner, generated) in repeated_at(row, &["generated"]).iter().enumerate() {
            assert_eq!(
                scalar_at(generated, &["outerIndex"]),
                &ScalarValue::Int((outer + 1) as i64)
            );
            assert_eq!(
                scalar_at(generated, &["generatedIndex"]),
                &ScalarValue::Int((inner + 1) as i64)
            );
            assert_eq!(
                scalar_at(generated, &["value"]),
                &ScalarValue::Int((inner + 1) as i64)
            );
        }
    }
    let second_outer_first_generated =
        &repeated_at(&repeated_at(&output, &["out"])[1], &["generated"])[0];
    assert_ne!(
        scalar_at(second_outer_first_generated, &["outerIndex"]),
        scalar_at(second_outer_first_generated, &["generatedIndex"])
    );

    let (projection, bytes) = projected(&project);
    let outer_control = iteration(&projection, "/root/children/0");
    let generated_control = iteration(&projection, "/root/children/0/children/0");
    let outer_rule = rule(&projection, &["out", "generated", "outerIndex"]);
    let generated_rule = rule(&projection, &["out", "generated", "generatedIndex"]);
    assert!(!control_dependencies(outer_rule).contains(generated_control["id"].as_str().unwrap()));
    assert!(!detail_references(outer_rule).contains(generated_control["id"].as_str().unwrap()));
    if projection["coverage"]["status"] == "complete" {
        assert_eq!(
            control_dependencies(outer_rule),
            BTreeSet::from([outer_control["id"].as_str().unwrap().to_string()])
        );
        assert_eq!(
            control_dependencies(generated_rule),
            BTreeSet::from([generated_control["id"].as_str().unwrap().to_string()])
        );
        assert!(detail_references(outer_rule).contains(outer_control["id"].as_str().unwrap()));
    } else {
        assert_partial_gap(&projection, 3, &bytes);
        assert!(
            control_dependencies(outer_rule).is_empty()
                || control_dependencies(outer_rule).contains(outer_control["id"].as_str().unwrap())
        );
    }
}

#[test]
fn grouping_explains_first_member_scalar_selection_and_first_seen_key_order() {
    let mut groups = scope("groups", vec![("key", 1), ("code", 2)], vec![]);
    groups.iteration = ScopeIteration::Source(strings(&["rows"]));
    groups.group_by = Some(1);
    let project = project(
        SchemaNode::group(
            "Source",
            vec![repeating(
                "rows",
                vec![text_schema("key"), text_schema("code")],
            )],
        ),
        SchemaNode::group(
            "Target",
            vec![repeating(
                "groups",
                vec![text_schema("key"), text_schema("code")],
            )],
        ),
        vec![
            (1, read(&["key"], Some(&["rows"]))),
            (2, read(&["code"], Some(&["rows"]))),
        ],
        scope("", vec![], vec![groups]),
    );
    let source = g([(
        "rows",
        Instance::Repeated(vec![
            g([("key", s("B")), ("code", s("B-FIRST"))]),
            g([("key", s("A")), ("code", s("A-FIRST"))]),
            g([("key", s("B")), ("code", s("B-SECOND"))]),
        ]),
    )]);
    let output = ferrule_engine::run(&project, &source).unwrap();
    let groups = repeated_at(&output, &["groups"]);
    assert_eq!(groups.len(), 2);
    assert_eq!(
        scalar_at(&groups[0], &["key"]),
        &ScalarValue::String("B".into())
    );
    assert_eq!(
        scalar_at(&groups[0], &["code"]),
        &ScalarValue::String("B-FIRST".into())
    );
    assert_eq!(
        scalar_at(&groups[1], &["key"]),
        &ScalarValue::String("A".into())
    );
    assert_eq!(
        scalar_at(&groups[1], &["code"]),
        &ScalarValue::String("A-FIRST".into())
    );

    let (projection, bytes) = projected(&project);
    let mapping = rule(&projection, &["groups", "code"]);
    let expected = field(&projection, "primaryInput", &["rows", "code"]);
    assert_exact_or_partial(&projection, mapping, &[expected], 2, &bytes);
    let grouping = projection["controls"]
        .as_array()
        .unwrap()
        .iter()
        .find(|control| control["kind"] == "grouping")
        .unwrap();
    let readable = serde_json::to_string(&grouping["details"])
        .unwrap()
        .to_lowercase();
    assert!(
        readable.contains("first member"),
        "ordinary scalar bindings must explain their selected group member"
    );
    assert!(
        readable.contains("first-seen") || readable.contains("first seen"),
        "group order follows first key encounters"
    );
    assert!(projection["relationships"].as_array().unwrap().iter().any(
        |relationship| relationship["kind"] == "governedBy"
            && relationship["from"] == json!({"kind":"rule","ruleId":mapping["id"]})
            && relationship["to"] == json!({"kind":"control","controlId":grouping["id"]})
    ));
}

#[test]
fn joined_raw_positions_and_source_contexts_use_the_actual_join_owner() {
    let native = include_bytes!("fixtures/projection-join-context.json");
    let project = project_file::decode_bytes(native).unwrap();
    let source = g([
        ("code", s("ROOT-CODE")),
        (
            "left",
            Instance::Repeated(vec![g([("key", s("MATCH")), ("code", s("LEFT-CODE"))])]),
        ),
        (
            "right",
            Instance::Repeated(vec![
                g([("key", s("MISS")), ("code", s("RIGHT-FIRST"))]),
                g([("key", s("MATCH")), ("code", s("RIGHT-SECOND"))]),
            ]),
        ),
    ]);
    let output = ferrule_engine::run(&project, &source).unwrap();
    let rows = repeated_at(&output, &["rows"]);
    assert_eq!(rows.len(), 1);
    assert_eq!(scalar_at(&rows[0], &["rawRight"]), &ScalarValue::Int(2));
    assert_eq!(scalar_at(&rows[0], &["flattened"]), &ScalarValue::Int(1));
    for name in ["code", "framedCode"] {
        assert_eq!(
            scalar_at(&rows[0], &[name]),
            &ScalarValue::String("RIGHT-SECOND".into())
        );
    }

    let (projection, bytes) = projected(&project);
    let join = projection["controls"]
        .as_array()
        .unwrap()
        .iter()
        .find(|control| {
            control["kind"] == "join"
                && control["nativeLocator"]["pointer"] == "/root/children/0/join/plan"
        })
        .expect("the actual joined scope must publish its join control");
    let join_id = join["id"].as_str().unwrap();
    for name in ["rawRight", "flattened"] {
        let mapping = rule(&projection, &["rows", name]);
        assert_eq!(
            control_dependencies(mapping),
            BTreeSet::from([join_id.to_string()]),
            "both raw source and flattened positions belong to this join"
        );
        if name == "rawRight" {
            assert!(detail_references(mapping).contains(join_id));
        } else {
            assert!(
                serde_json::to_string(&mapping["details"])
                    .unwrap()
                    .to_lowercase()
                    .contains("flattened")
            );
        }
    }

    let right = field(&projection, "primaryInput", &["right", "code"]);
    let right_id = right["id"].as_str().unwrap();
    let framed = rule(&projection, &["rows", "framedCode"]);
    assert_eq!(
        dependencies(framed),
        BTreeSet::from([right_id.to_string()]),
        "an explicit right frame has definite right/code lineage even when another rule is partial"
    );
    assert!(detail_field_references(framed).contains(right_id));

    let ordinary = rule(&projection, &["rows", "code"]);
    for wrong in [
        field(&projection, "primaryInput", &["code"]),
        field(&projection, "primaryInput", &["left", "code"]),
    ] {
        assert!(
            !dependencies(ordinary).contains(wrong["id"].as_str().unwrap()),
            "possible joined-source fallback locations cannot become definite root/left reads"
        );
    }
    assert_exact_or_partial(&projection, ordinary, &[right], 3, &bytes);
}

#[test]
fn a_generated_item_identity_does_not_pin_a_nested_scalar_source_frame() {
    let native = include_bytes!("fixtures/projection-generated-source-context.json");
    let project = project_file::decode_bytes(native).unwrap();
    let source = g([(
        "numbers",
        Instance::Repeated(vec![
            Instance::Scalar(ScalarValue::Int(42)),
            Instance::Scalar(ScalarValue::Int(99)),
        ]),
    )]);
    let output = ferrule_engine::run(&project, &source).unwrap();
    let outer = repeated_at(&output, &["outer"]);
    assert_eq!(outer.len(), 1);
    let inner = repeated_at(&outer[0], &["inner"]);
    assert_eq!(inner.len(), 2);
    assert_eq!(inner[0].as_scalar(), Some(&ScalarValue::Int(42)));
    assert_eq!(inner[1].as_scalar(), Some(&ScalarValue::Int(99)));

    let (projection, bytes) = projected(&project);
    let generated = iteration(&projection, "/root/children/0");
    let generated_id = generated["id"].as_str().unwrap();
    let mapping = rule(&projection, &["outer", "inner"]);
    assert!(
        !control_dependencies(mapping).contains(generated_id),
        "the declared item node grants lexical use and does not bind this later read to the generated frame"
    );
    assert!(!detail_references(mapping).contains(generated_id));
    let numbers = field(&projection, "primaryInput", &["numbers"]);
    assert_exact_or_partial(&projection, mapping, &[numbers], 1, &bytes);
}

fn project(
    source: SchemaNode,
    target: SchemaNode,
    nodes: Vec<(u32, Node)>,
    root: Scope,
) -> Project {
    Project {
        source,
        target,
        source_path: None,
        target_path: None,
        source_options: Default::default(),
        target_options: Default::default(),
        extra_sources: vec![],
        extra_targets: vec![],
        failure_rules: vec![],
        user_functions: BTreeMap::new(),
        graph: Graph {
            nodes: nodes.into_iter().collect(),
        },
        root,
    }
}

fn scope(name: &str, bindings: Vec<(&str, u32)>, children: Vec<Scope>) -> Scope {
    Scope {
        target_field: name.into(),
        bindings: bindings
            .into_iter()
            .map(|(name, node)| Binding {
                target_field: name.into(),
                node,
            })
            .collect(),
        children,
        ..Default::default()
    }
}

fn text_schema(name: &str) -> SchemaNode {
    SchemaNode::scalar(name, ScalarType::String)
}
fn int_schema(name: &str) -> SchemaNode {
    SchemaNode::scalar(name, ScalarType::Int)
}
fn repeating(name: &str, children: Vec<SchemaNode>) -> SchemaNode {
    let mut schema = SchemaNode::group(name, children);
    schema.repeating = true;
    schema
}
fn strings(path: &[&str]) -> Vec<String> {
    path.iter().map(|part| (*part).into()).collect()
}
fn read(path: &[&str], frame: Option<&[&str]>) -> Node {
    Node::SourceField {
        path: strings(path),
        frame: frame.map(strings),
    }
}
fn g<const N: usize>(fields: [(&str, Instance); N]) -> Instance {
    Instance::Group(
        fields
            .into_iter()
            .map(|(name, value)| (name.into(), value))
            .collect::<Vec<_>>()
            .into(),
    )
}
fn s(value: &str) -> Instance {
    Instance::Scalar(ScalarValue::String(value.into()))
}

fn instance_at<'a>(instance: &'a Instance, path: &[&str]) -> &'a Instance {
    path.iter().fold(instance, |instance, name| {
        instance
            .field(name)
            .unwrap_or_else(|| panic!("missing authored output field {name}"))
    })
}
fn scalar_at<'a>(instance: &'a Instance, path: &[&str]) -> &'a ScalarValue {
    instance_at(instance, path)
        .as_scalar()
        .expect("authored scalar output")
}
fn repeated_at<'a>(instance: &'a Instance, path: &[&str]) -> &'a [Instance] {
    instance_at(instance, path)
        .as_repeated()
        .expect("authored repeated output")
}

fn projected(project: &Project) -> (Value, Vec<u8>) {
    let bytes = project_file::encode_pretty(project).unwrap().into_bytes();
    let decoded = weft_ferrule_host::decode::decode_native(&bytes).unwrap();
    let limits = Limits {
        maximum_diagnostics: 128,
        maximum_items: 8192,
        maximum_text_bytes: 2048,
    };
    let projection = weft_ferrule_host::projection::project(
        &decoded.project,
        "entry.native.json",
        &decoded.document_prefix,
        &limits,
    )
    .unwrap();
    (serde_json::to_value(projection).unwrap(), bytes)
}

fn field<'a>(projection: &'a Value, role: &str, names: &[&str]) -> &'a Value {
    let boundary = projection["boundaries"]
        .as_array()
        .unwrap()
        .iter()
        .find(|boundary| boundary["role"] == role)
        .unwrap();
    projection["fields"]
        .as_array()
        .unwrap()
        .iter()
        .find(|field| {
            let path = field["path"]
                .as_array()
                .unwrap()
                .iter()
                .filter(|segment| segment["kind"] == "field")
                .map(|segment| segment["name"].as_str().unwrap())
                .collect::<Vec<_>>();
            field["boundaryId"] == boundary["id"] && path == names
        })
        .unwrap_or_else(|| panic!("missing independent schema path {role}: {names:?}"))
}

fn rule<'a>(projection: &'a Value, output_path: &[&str]) -> &'a Value {
    let output = field(projection, "primaryOutput", output_path);
    projection["rules"]
        .as_array()
        .unwrap()
        .iter()
        .find(|rule| rule["outputFieldId"] == output["id"])
        .unwrap()
}

fn dependencies(rule: &Value) -> BTreeSet<String> {
    rule["dependencies"]
        .as_array()
        .unwrap()
        .iter()
        .filter(|dependency| dependency["kind"] == "field")
        .map(|dependency| dependency["fieldId"].as_str().unwrap().into())
        .collect()
}

fn control_dependencies(rule: &Value) -> BTreeSet<String> {
    rule["dependencies"]
        .as_array()
        .unwrap()
        .iter()
        .filter(|dependency| {
            dependency["kind"] == "node" && dependency["node"]["kind"] == "control"
        })
        .map(|dependency| dependency["node"]["controlId"].as_str().unwrap().into())
        .collect()
}

fn iteration<'a>(projection: &'a Value, native_pointer: &str) -> &'a Value {
    projection["controls"]
        .as_array()
        .unwrap()
        .iter()
        .find(|control| {
            control["kind"] == "iteration" && control["nativeLocator"]["pointer"] == native_pointer
        })
        .unwrap()
}

fn detail_references(rule: &Value) -> BTreeSet<String> {
    fn walk(detail: &Value, output: &mut BTreeSet<String>) {
        if detail["kind"] == "reference" && detail["reference"]["kind"] == "control" {
            output.insert(detail["reference"]["controlId"].as_str().unwrap().into());
        }
        for property in ["children", "arguments"] {
            if let Some(children) = detail[property].as_array() {
                for child in children {
                    walk(child, output);
                }
            }
        }
    }
    let mut output = BTreeSet::new();
    for detail in rule["details"].as_array().unwrap() {
        walk(detail, &mut output);
    }
    output
}

fn detail_field_references(rule: &Value) -> BTreeSet<String> {
    fn walk(detail: &Value, output: &mut BTreeSet<String>) {
        if detail["kind"] == "reference" && detail["reference"]["kind"] == "field" {
            output.insert(detail["reference"]["fieldId"].as_str().unwrap().into());
        }
        for property in ["children", "arguments"] {
            if let Some(children) = detail[property].as_array() {
                for child in children {
                    walk(child, output);
                }
            }
        }
    }
    let mut output = BTreeSet::new();
    for detail in rule["details"].as_array().unwrap() {
        walk(detail, &mut output);
    }
    output
}

fn assert_exact_or_partial(
    projection: &Value,
    rule: &Value,
    expected_fields: &[&Value],
    node: u32,
    bytes: &[u8],
) {
    let expected = expected_fields
        .iter()
        .map(|field| field["id"].as_str().unwrap().to_string())
        .collect::<BTreeSet<_>>();
    let actual = dependencies(rule);
    if expected.is_empty() {
        assert!(
            actual.is_empty(),
            "a native absent owner cannot invent field dependencies"
        );
    }
    match projection["coverage"]["status"].as_str().unwrap() {
        "complete" => assert_eq!(
            actual, expected,
            "complete field lineage must match the engine-backed owner"
        ),
        "partial" => {
            assert_partial_gap(projection, node, bytes);
            assert!(
                actual.is_empty() || expected.is_subset(&actual),
                "partial lineage must retain the native owner or remain explicitly unqualified"
            );
        }
        other => panic!(
            "small authored semantic oracle must be complete or conservatively partial, got {other}"
        ),
    }
}

fn assert_partial_gap(projection: &Value, node: u32, bytes: &[u8]) {
    assert_eq!(projection["coverage"]["status"], "partial");
    let node_id = node.to_string();
    let retained: Value = serde_json::from_slice(bytes).unwrap();
    let gaps = projection["coverage"]["gaps"].as_array().unwrap();
    assert!(!gaps.is_empty());
    assert!(
        gaps.iter().any(|gap| {
            let locator = &gap["nativeLocator"];
            locator["kind"] == "graphNode" && locator["nodeId"].as_str() == Some(node_id.as_str())
                || locator["kind"] == "jsonPointer"
                    && locator["pointer"]
                        .as_str()
                        .is_some_and(|pointer| retained.pointer(pointer).is_some())
                || locator["kind"] == "document" && locator["documentId"] == "entry.native.json"
        }),
        "a conservative semantic gap must retain a valid native location for node {node}"
    );
}
