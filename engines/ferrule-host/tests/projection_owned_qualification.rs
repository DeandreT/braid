//! Projection of the native forms admitted at the pinned Ferrule revision: owned
//! drivers, owned failure iteration, owned joins, optional presence, and the forms
//! that remain explicit gaps.
mod support;

use std::collections::{BTreeMap, BTreeSet};

use braid_ferrule_host::protocol::Limits;
use ferrule_ir::{Instance, ScalarType, SchemaNode, Value as ScalarValue};
use ferrule_mapping::{
    AggregateOp, Binding, DynamicSourcePath, FailureIteration, FailureRule, FailureSelection,
    Graph, JoinId, NamedSource, Node, OwnedAdjacencyTreePlan, OwnedAggregate, OwnedAggregateValue,
    OwnedJoinConditions, OwnedJoinField, OwnedJoinKey, OwnedJoinPlan, OwnedJoinSource,
    OwnedJoinSourcePosition, OwnedLookup, OwnedPathHierarchyPlan, OwnedRecursiveCollect,
    OwnedSourceRef, OwnedXmlSerialize, Project, Scope, ScopeConstruction, ScopeIteration,
    SequenceExpr, SourceOwner, project_file,
};
use serde_json::Value;
use support::*;

const OWNED: &[u8] = include_bytes!("fixtures/projection-owned-context.json");

#[test]
fn the_retained_owned_fixture_projects_each_form_by_its_exact_owner() {
    let project = project_file::decode_bytes(OWNED).unwrap();
    assert!(ferrule_engine::validate(&project).is_empty());
    let (projection, _) = projected(&project);
    let name = rule(&projection, &["rows", "name"]);
    assert_eq!(
        field_dependencies(name),
        BTreeSet::from([id(field(&projection, "namedInput:0", &["orders", "name"])).to_string()])
    );
    assert!(gap_at_node(
        &projection,
        "projection.unsupportedExpression",
        3
    ));
    assert_eq!(
        control(&projection, "/root/children/1")["summary"],
        "Optional presence"
    );
    let failure = field_dependencies(control(&projection, "/failure_rules/0"));
    assert!(failure.contains(id(field(&projection, "namedInput:0", &["orders", "name"]))));
    assert!(!failure.contains(id(field(&projection, "primaryInput", &["orders", "name"]))));
    let driver = field_dependencies(control(&projection, "/extra_sources/1/dynamic_path"));
    assert!(driver.contains(id(field(&projection, "primaryInput", &["rows", "file"]))));
    for code in ["schema.objectItemNullability", "schema.undeclaredProperty"] {
        assert!(
            projection["coverage"]["gaps"]
                .as_array()
                .unwrap()
                .iter()
                .any(|gap| gap["code"] == code),
            "{code}"
        );
    }
}

#[test]
fn an_owned_dynamic_driver_frames_only_its_exact_endpoint() {
    let mut project = project(
        group("Source", vec![repeating("rows", vec![text("file")])]),
        group("Target", vec![text("mapped")]),
        vec![(1, read(&["file"]))],
        scope("", vec![], vec![]),
    );
    project.extra_sources = vec![
        named(
            "other",
            group("Other", vec![repeating("rows", vec![text("file")])]),
        ),
        NamedSource {
            dynamic_path: Some(DynamicSourcePath::owned(1, owned(None, &["rows"]))),
            ..named("documents", group("Documents", vec![text("code")]))
        },
    ];
    let (projection, _) = projected(&project);
    let control = control(&projection, "/extra_sources/1/dynamic_path");
    assert_eq!(control["kind"], "dynamicBoundary");
    let expected = field(&projection, "primaryInput", &["rows", "file"]);
    let shadow = field(&projection, "namedInput:0", &["rows", "file"]);
    let reads = field_dependencies(control);
    assert!(reads.contains(id(expected)), "{control:#}");
    assert!(
        !reads.contains(id(shadow)),
        "a same-path endpoint is not the owner"
    );
    assert!(reads.contains(id(field(&projection, "primaryInput", &["rows"]))));
    assert!(!reads.contains(id(field(&projection, "namedInput:0", &["rows"]))));
}

#[test]
fn an_owned_failure_iteration_reads_only_its_exact_endpoint() {
    let mut project = project(
        group("Source", vec![repeating("rows", vec![text("code")])]),
        group("Target", vec![text("mapped")]),
        vec![(1, read(&["code"]))],
        scope("", vec![], vec![]),
    );
    project.extra_sources = vec![named(
        "lookup",
        group("Lookup", vec![repeating("rows", vec![text("code")])]),
    )];
    project.failure_rules = vec![FailureRule {
        iteration: FailureIteration::OwnedSource {
            source: owned(Some("lookup"), &["rows"]),
        },
        selection: FailureSelection::WhenTrue { predicate: 1 },
        message: None,
    }];
    let (projection, _) = projected(&project);
    let control = control(&projection, "/failure_rules/0");
    assert_eq!(control["kind"], "failure");
    let reads = field_dependencies(control);
    assert!(reads.contains(id(field(&projection, "namedInput:0", &["rows", "code"]))));
    assert!(!reads.contains(id(field(&projection, "primaryInput", &["rows", "code"]))));
    assert_eq!(
        projection["coverage"]["status"], "complete",
        "{projection:#}"
    );
}

#[test]
fn optional_presence_explains_its_condition_without_adding_a_frame() {
    let mut child = scope("maybe", vec![("mapped", 2)], vec![]);
    child.iteration = ScopeIteration::OptionalPresence { condition: 1 };
    let project = project(
        group(
            "Source",
            vec![SchemaNode::scalar("flag", ScalarType::Bool), text("code")],
        ),
        group("Target", vec![repeating("maybe", vec![text("mapped")])]),
        vec![(1, read(&["flag"])), (2, read(&["code"]))],
        scope("", vec![], vec![child]),
    );
    assert!(ferrule_engine::validate(&project).is_empty());
    for (flag, groups) in [(true, 1), (false, 0)] {
        let source = Instance::Group(
            vec![
                ("flag".to_string(), scalar(ScalarValue::Bool(flag))),
                ("code".to_string(), scalar(ScalarValue::String("C".into()))),
            ]
            .into(),
        );
        let output = ferrule_engine::run(&project, &source).unwrap();
        let maybe = output.field("maybe").and_then(Instance::as_repeated);
        assert_eq!(maybe.map(<[Instance]>::len), Some(groups), "{output:?}");
    }

    let (projection, _) = projected(&project);
    let control = control(&projection, "/root/children/0");
    assert_eq!(control["kind"], "iteration");
    assert_eq!(control["summary"], "Optional presence");
    assert_eq!(
        field_dependencies(control),
        BTreeSet::from([id(field(&projection, "primaryInput", &["flag"])).to_string()])
    );
    let mapped = rule(&projection, &["maybe", "mapped"]);
    assert_eq!(
        field_dependencies(mapped),
        BTreeSet::from([id(field(&projection, "primaryInput", &["code"])).to_string()]),
        "the presence scope keeps the parent item"
    );
    assert_eq!(
        projection["coverage"]["status"], "complete",
        "{projection:#}"
    );
}

#[test]
fn optional_presence_keeps_a_grouped_parents_member_iteration() {
    let mut members = scope("members", vec![("position", 2)], vec![]);
    members.iteration = ScopeIteration::Source(Vec::new());
    let mut maybe = scope("maybe", vec![], vec![members]);
    maybe.iteration = ScopeIteration::OptionalPresence { condition: 3 };
    let mut groups = scope("groups", vec![], vec![maybe]);
    groups.iteration = ScopeIteration::Source(strings(&["rows"]));
    groups.group_by = Some(1);
    let project = project(
        group("Source", vec![repeating("rows", vec![text("key")])]),
        group(
            "Target",
            vec![repeating(
                "groups",
                vec![repeating(
                    "maybe",
                    vec![repeating(
                        "members",
                        vec![SchemaNode::scalar("position", ScalarType::Int)],
                    )],
                )],
            )],
        ),
        vec![
            (1, read(&["key"])),
            (
                2,
                Node::Position {
                    collection: Vec::new(),
                },
            ),
            (
                3,
                Node::Const {
                    value: ScalarValue::Bool(true),
                },
            ),
        ],
        scope("", vec![], vec![groups]),
    );
    assert!(ferrule_engine::validate(&project).is_empty());
    let source = Instance::Group(
        vec![(
            "rows".to_string(),
            Instance::Repeated(
                ["B", "A", "B"]
                    .into_iter()
                    .map(|key| {
                        Instance::Group(
                            vec![("key".to_string(), scalar(ScalarValue::String(key.into())))]
                                .into(),
                        )
                    })
                    .collect(),
            ),
        )]
        .into(),
    );
    let output = ferrule_engine::run(&project, &source).unwrap();
    let first = &output.field("groups").unwrap().as_repeated().unwrap()[0];
    let members = first.field("maybe").unwrap().as_repeated().unwrap()[0]
        .field("members")
        .unwrap()
        .as_repeated()
        .unwrap();
    assert_eq!(
        members.len(),
        2,
        "the presence scope keeps the group's members"
    );

    let (projection, _) = projected(&project);
    let member_iteration = control(&projection, "/root/children/0/children/0/children/0");
    let position = rule(&projection, &["groups", "maybe", "members", "position"]);
    assert_eq!(
        position["dependencies"][0]["node"]["controlId"], member_iteration["id"],
        "{position:#}"
    );
}

#[test]
fn scopes_ferrule_refuses_inside_an_owned_join_are_closed_partial_coverage() {
    let orders = owned(None, &["orders"]);
    let customers = owned(Some("lookup"), &["orders"]);
    let plan = || {
        OwnedJoinPlan::new(
            OwnedJoinSource::new(orders.clone()),
            OwnedJoinSource::new(customers.clone()),
            OwnedJoinConditions::new(OwnedJoinKey::new(
                orders.clone(),
                strings(&["customer"]),
                strings(&["id"]),
            )),
        )
        .unwrap()
    };
    let join = JoinId::new(7);
    let mut presence = scope("presence", vec![("name", 1)], vec![]);
    presence.iteration = ScopeIteration::OptionalPresence { condition: 2 };
    let mut nested = scope("nested", vec![("name", 1)], vec![]);
    nested.iteration = ScopeIteration::Source(strings(&["orders"]));
    let fixed = scope("fixed", vec![("name", 1)], vec![]);
    let mut joined = scope("rows", vec![], vec![presence, nested, fixed]);
    joined.iteration = ScopeIteration::OwnedInnerJoin {
        id: join,
        plan: plan(),
    };
    let mut grouped = scope("grouped", vec![("name", 1)], vec![]);
    grouped.iteration = ScopeIteration::OwnedInnerJoin {
        id: join,
        plan: plan(),
    };
    grouped.group_by = Some(2);
    let member = |name: &str| repeating(name, vec![text("name")]);
    let mut project = project(
        group(
            "Source",
            vec![repeating("orders", vec![text("customer"), text("name")])],
        ),
        group(
            "Target",
            vec![
                repeating(
                    "rows",
                    vec![
                        member("presence"),
                        member("nested"),
                        group("fixed", vec![text("name")]),
                    ],
                ),
                member("grouped"),
            ],
        ),
        vec![
            (
                1,
                Node::OwnedJoinField(OwnedJoinField {
                    join,
                    collection: customers.clone(),
                    path: strings(&["name"]),
                }),
            ),
            (
                2,
                Node::Const {
                    value: ScalarValue::Bool(true),
                },
            ),
        ],
        scope("", vec![], vec![joined, grouped]),
    );
    project.extra_sources = vec![named(
        "lookup",
        group(
            "Lookup",
            vec![repeating("orders", vec![text("id"), text("name")])],
        ),
    )];
    let (projection, _) = projected(&project);
    let name = id(field(&projection, "namedInput:0", &["orders", "name"])).to_string();
    for pointer in [
        "/root/children/0/children/0",
        "/root/children/0/children/1",
        "/root/children/1",
    ] {
        assert!(
            gap_at_pointer(&projection, "projection.closedOwnedJoinScope", pointer),
            "{pointer}: {projection:#}"
        );
    }
    for path in [
        &["rows", "presence", "name"][..],
        &["rows", "nested", "name"],
        &["grouped", "name"],
    ] {
        assert!(
            field_dependencies(rule(&projection, path)).is_empty(),
            "{path:?}"
        );
    }
    assert_eq!(
        field_dependencies(rule(&projection, &["rows", "fixed", "name"])),
        BTreeSet::from([name]),
        "a static descendant shares the tuple"
    );
    assert!(!gap_at_pointer(
        &projection,
        "projection.closedOwnedJoinScope",
        "/root/children/0/children/2"
    ));
    assert_eq!(projection["coverage"]["status"], "partial");
}

#[test]
fn an_owned_inner_join_reads_members_by_their_exact_identity() {
    let orders = owned(None, &["orders"]);
    let customers = owned(Some("lookup"), &["orders"]);
    let plan = OwnedJoinPlan::new(
        OwnedJoinSource::new(orders.clone()),
        OwnedJoinSource::new(customers.clone()),
        OwnedJoinConditions::new(OwnedJoinKey::new(
            orders.clone(),
            strings(&["customer"]),
            strings(&["id"]),
        )),
    )
    .unwrap();
    let join = JoinId::new(7);
    let mut joined = scope(
        "rows",
        vec![("name", 1), ("position", 2), ("plain", 3)],
        vec![],
    );
    joined.iteration = ScopeIteration::OwnedInnerJoin { id: join, plan };
    let mut project = project(
        group(
            "Source",
            vec![repeating("orders", vec![text("customer"), text("name")])],
        ),
        group(
            "Target",
            vec![repeating(
                "rows",
                vec![
                    text("name"),
                    SchemaNode::scalar("position", ScalarType::Int),
                    text("plain"),
                ],
            )],
        ),
        vec![
            (
                1,
                Node::OwnedJoinField(OwnedJoinField {
                    join,
                    collection: customers.clone(),
                    path: strings(&["name"]),
                }),
            ),
            (
                2,
                Node::OwnedJoinSourcePosition(OwnedJoinSourcePosition {
                    join,
                    collection: customers.clone(),
                }),
            ),
            (3, read(&["name"])),
        ],
        scope("", vec![], vec![joined]),
    );
    project.extra_sources = vec![named(
        "lookup",
        group(
            "Lookup",
            vec![repeating("orders", vec![text("id"), text("name")])],
        ),
    )];
    let (projection, bytes) = projected(&project);
    let control = projection["controls"]
        .as_array()
        .unwrap()
        .iter()
        .find(|control| control["kind"] == "join")
        .unwrap();
    assert_eq!(control["summary"], "Ordered owned inner join");
    let reads = field_dependencies(control);
    for (boundary, path) in [
        ("primaryInput", &["orders"][..]),
        ("primaryInput", &["orders", "customer"]),
        ("namedInput:0", &["orders"]),
        ("namedInput:0", &["orders", "id"]),
    ] {
        assert!(
            reads.contains(id(field(&projection, boundary, path))),
            "{path:?}"
        );
    }
    assert!(!reads.contains(id(field(&projection, "namedInput:0", &["orders", "name"]))));

    let name = rule(&projection, &["rows", "name"]);
    assert_eq!(
        field_dependencies(name),
        BTreeSet::from([id(field(&projection, "namedInput:0", &["orders", "name"])).to_string()]),
        "the member read selects only its exact owner, never the same-path primary field"
    );
    let position = rule(&projection, &["rows", "position"]);
    assert_eq!(
        position["dependencies"][0]["node"]["controlId"], control["id"],
        "{position:#}"
    );
    let plain = rule(&projection, &["rows", "plain"]);
    assert!(
        field_dependencies(plain).is_empty(),
        "ordinary reads are closed in the tuple"
    );
    assert!(gap_at_node(
        &projection,
        "projection.unsupportedExpression",
        3
    ));
    assert_eq!(projection["coverage"]["status"], "partial");
    assert!(
        serde_json::from_slice::<Value>(&bytes)
            .unwrap()
            .pointer("/root/children/0/join/plan")
            .is_some()
    );
}

#[test]
fn new_native_constructions_sequences_and_owned_expressions_remain_explicit_gaps() {
    let orders = owned(None, &["orders"]);
    let mut paths = scope("paths", vec![], vec![]);
    paths.construction = ScopeConstruction::OwnedPathHierarchy {
        plan: OwnedPathHierarchyPlan::new(
            owned(None, &["files"]),
            "/".into(),
            "dirs".into(),
            "items".into(),
            "name".into(),
        )
        .unwrap(),
    };
    let mut tree = scope("tree", vec![], vec![]);
    tree.construction = ScopeConstruction::OwnedAdjacencyTree {
        plan: OwnedAdjacencyTreePlan::new(
            orders.clone(),
            strings(&["customer"]),
            strings(&["name"]),
            "key".into(),
            "children".into(),
            None,
        )
        .unwrap(),
    };
    let mut generated = scope("generated", vec![], vec![]);
    generated.iteration =
        ScopeIteration::Sequence(SequenceExpr::OwnedRecursiveCollect(OwnedRecursiveCollect {
            collection: orders.clone(),
            children: strings(&["orders"]),
            descent_value: strings(&["name"]),
            values: strings(&["orders"]),
            value: strings(&["name"]),
            prefix: 4,
            separator: 4,
            item: 5,
        }));
    let mut deep = SchemaNode::group("leaf", Vec::new());
    for level in 0..4 {
        deep = SchemaNode::group(format!("level{level}"), vec![deep]);
    }
    let project = project(
        group(
            "Source",
            vec![
                repeating("orders", vec![text("customer"), text("name")]),
                repeating_scalar("files"),
            ],
        ),
        group(
            "Target",
            vec![
                text("lookup"),
                SchemaNode::scalar("total", ScalarType::Int),
                text("xml"),
                group("paths", vec![]),
                group("tree", vec![]),
                repeating_scalar("generated"),
            ],
        ),
        vec![
            (
                1,
                Node::OwnedLookup(OwnedLookup {
                    collection: orders.clone(),
                    key: strings(&["customer"]),
                    matches: 4,
                    value: strings(&["name"]),
                }),
            ),
            (
                2,
                Node::OwnedAggregate(OwnedAggregate {
                    function: AggregateOp::Count,
                    collection: orders.clone(),
                    value: OwnedAggregateValue::Path(strings(&["name"])),
                    arg: None,
                }),
            ),
            (
                3,
                Node::OwnedXmlSerialize(OwnedXmlSerialize {
                    source: orders.clone(),
                    frame: None,
                    schema: Box::new(deep),
                    declaration: false,
                    indent: false,
                    namespace: None,
                }),
            ),
            (
                4,
                Node::Const {
                    value: ScalarValue::String("/".into()),
                },
            ),
            (5, Node::Unconnected),
        ],
        scope(
            "",
            vec![("lookup", 1), ("total", 2), ("xml", 3)],
            vec![paths, tree, generated],
        ),
    );
    let (projection, _) = projected(&project);
    assert_eq!(projection["coverage"]["status"], "partial");
    for node in [1, 2, 3] {
        assert!(gap_at_node(
            &projection,
            "projection.unsupportedExpression",
            node
        ));
    }
    let constructs = unsupported_constructs(&projection);
    for construct in [
        "Explicitly owned lookup",
        "Explicitly owned aggregate",
        "Explicitly owned XML serialization",
        "Owned path hierarchy construction",
        "Owned adjacency tree construction",
        "owned_recursive_collect",
    ] {
        assert!(
            constructs.contains(construct),
            "{construct}: {constructs:?}"
        );
    }
    for pointer in ["/root/children/0", "/root/children/1"] {
        assert!(gap_at_pointer(
            &projection,
            "projection.nativeConstruction",
            pointer
        ));
    }
    assert!(
        projection["coverage"]["gaps"]
            .as_array()
            .unwrap()
            .iter()
            .any(|gap| gap["code"] == "projection.nativeSequence")
    );
}

#[test]
fn an_owned_xml_serializer_schema_is_charged_to_the_inspection_depth_limit() {
    let mut deep = SchemaNode::group("leaf", Vec::new());
    for level in 0..40 {
        deep = SchemaNode::group(format!("level{level}"), vec![deep]);
    }
    let project = project(
        group("Source", vec![repeating("orders", vec![text("name")])]),
        group("Target", vec![text("xml")]),
        vec![(
            1,
            Node::OwnedXmlSerialize(OwnedXmlSerialize {
                source: owned(None, &["orders"]),
                frame: None,
                schema: Box::new(deep),
                declaration: false,
                indent: false,
                namespace: None,
            }),
        )],
        scope("", vec![("xml", 1)], vec![]),
    );
    let native = project_file::encode_pretty(&project).unwrap().into_bytes();
    let response = process(&request("inspect", &native), &native);
    assert_eq!(
        failure(&response),
        "inspectionLimitExceeded",
        "{response:#}"
    );
    assert_eq!(
        response["failure"]["message"],
        "The schema exceeds the inspection depth limit."
    );
}

#[test]
fn object_item_nullability_and_undeclared_properties_are_located_schema_gaps() {
    let mut items = repeating("items", vec![text("code")]);
    items.json_object_item_nullable = Some(true);
    let mut open = group("open", vec![]);
    if let ferrule_ir::SchemaKind::Group { dynamic, .. } = &mut open.kind {
        *dynamic = Some(Box::new(text("value")));
    }
    open.add_json_undeclared_property("authored").unwrap();
    let mut project = project(
        group("Source", vec![items, open]),
        group("Target", vec![text("mapped")]),
        vec![(1, read(&["open", "authored"]))],
        scope("", vec![("mapped", 1)], vec![]),
    );
    project.source_options.json_document = true;
    let (projection, bytes) = projected(&project);
    let retained: Value = serde_json::from_slice(&bytes).unwrap();
    for (code, path) in [
        ("schema.objectItemNullability", &["items"][..]),
        ("schema.undeclaredProperty", &["open", "authored"]),
    ] {
        let field = field(&projection, "primaryInput", path);
        let gap = projection["coverage"]["gaps"]
            .as_array()
            .unwrap()
            .iter()
            .find(|gap| gap["code"] == code)
            .unwrap_or_else(|| panic!("missing {code}: {projection:#}"));
        assert_eq!(gap["projectionLocator"]["fieldId"], field["id"]);
        let pointer = gap["nativeLocator"]["pointer"].as_str().unwrap();
        assert!(retained.pointer(pointer).is_some(), "{pointer}");
    }
    assert_eq!(
        field_dependencies(rule(&projection, &["mapped"])),
        BTreeSet::from([id(field(&projection, "primaryInput", &["open", "authored"])).to_string()])
    );
}

fn project(
    source: SchemaNode,
    target: SchemaNode,
    nodes: Vec<(u32, Node)>,
    root: Scope,
) -> Project {
    Project {
        execution_contract: None,
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

fn named(name: &str, schema: SchemaNode) -> NamedSource {
    NamedSource {
        name: name.into(),
        path: format!("{name}.json"),
        schema,
        options: Default::default(),
        dynamic_path: None,
    }
}

fn owned(owner: Option<&str>, path: &[&str]) -> OwnedSourceRef {
    OwnedSourceRef {
        owner: owner.map_or(SourceOwner::Primary, |name| SourceOwner::Named {
            name: name.into(),
        }),
        path: strings(path),
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

fn group(name: &str, children: Vec<SchemaNode>) -> SchemaNode {
    SchemaNode::group(name, children)
}
fn text(name: &str) -> SchemaNode {
    SchemaNode::scalar(name, ScalarType::String)
}
fn repeating(name: &str, children: Vec<SchemaNode>) -> SchemaNode {
    let mut schema = SchemaNode::group(name, children);
    schema.repeating = true;
    schema
}
fn repeating_scalar(name: &str) -> SchemaNode {
    let mut schema = text(name);
    schema.repeating = true;
    schema
}
fn strings(path: &[&str]) -> Vec<String> {
    path.iter().map(|part| (*part).into()).collect()
}
fn read(path: &[&str]) -> Node {
    Node::SourceField {
        path: strings(path),
        frame: None,
    }
}
fn scalar(value: ScalarValue) -> Instance {
    Instance::Scalar(value)
}

fn projected(project: &Project) -> (Value, Vec<u8>) {
    let bytes = project_file::encode_pretty(project).unwrap().into_bytes();
    let decoded = braid_ferrule_host::decode::decode_native(&bytes).unwrap();
    let limits = Limits {
        maximum_diagnostics: 128,
        maximum_items: 8192,
        maximum_text_bytes: 2048,
    };
    let projection = braid_ferrule_host::projection::project(
        &decoded.project,
        "entry.native.json",
        &decoded.document_prefix,
        &limits,
    )
    .unwrap();
    (serde_json::to_value(projection).unwrap(), bytes)
}

fn id(field: &Value) -> &str {
    field["id"].as_str().unwrap()
}

/// A field of the boundary named by role, with an optional declaration index.
fn field<'a>(projection: &'a Value, boundary: &str, names: &[&str]) -> &'a Value {
    let (role, index) = boundary
        .split_once(':')
        .map_or((boundary, 0), |(role, index)| {
            (role, index.parse().unwrap())
        });
    let boundary = &projection["boundaries"]
        .as_array()
        .unwrap()
        .iter()
        .filter(|candidate| candidate["role"] == role)
        .nth(index)
        .unwrap()["id"];
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
            field["boundaryId"] == *boundary && path == names
        })
        .unwrap_or_else(|| panic!("missing schema path {role}:{index}: {names:?}"))
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

fn control<'a>(projection: &'a Value, pointer: &str) -> &'a Value {
    projection["controls"]
        .as_array()
        .unwrap()
        .iter()
        .find(|control| control["nativeLocator"]["pointer"] == pointer)
        .unwrap_or_else(|| panic!("missing control at {pointer}: {projection:#}"))
}

fn field_dependencies(item: &Value) -> BTreeSet<String> {
    item["dependencies"]
        .as_array()
        .unwrap()
        .iter()
        .filter(|dependency| dependency["kind"] == "field")
        .map(|dependency| dependency["fieldId"].as_str().unwrap().into())
        .collect()
}

fn gap_at_node(projection: &Value, code: &str, node: u32) -> bool {
    projection["coverage"]["gaps"]
        .as_array()
        .unwrap()
        .iter()
        .any(|gap| {
            gap["code"] == code
                && gap["nativeLocator"]["kind"] == "graphNode"
                && gap["nativeLocator"]["nodeId"]
                    .as_str()
                    .and_then(|id| id.parse().ok())
                    == Some(node)
        })
}

fn gap_at_pointer(projection: &Value, code: &str, pointer: &str) -> bool {
    projection["coverage"]["gaps"]
        .as_array()
        .unwrap()
        .iter()
        .any(|gap| gap["code"] == code && gap["nativeLocator"]["pointer"] == pointer)
}

fn unsupported_constructs(projection: &Value) -> BTreeSet<String> {
    fn walk(detail: &Value, output: &mut BTreeSet<String>) {
        if detail["kind"] == "unsupported" {
            output.insert(detail["construct"].as_str().unwrap().into());
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
    for item in projection["rules"]
        .as_array()
        .unwrap()
        .iter()
        .chain(projection["controls"].as_array().unwrap())
    {
        for detail in item["details"].as_array().unwrap() {
            walk(detail, &mut output);
        }
    }
    output
}
