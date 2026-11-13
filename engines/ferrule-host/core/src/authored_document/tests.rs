use super::*;
use ferrule_ir::{ScalarType, SchemaNode};
use ferrule_mapping::{
    Binding, EdiBoundaryKind, FormatOptions, Graph, Node, Project, Scope, ScopeIteration,
    TabularBoundaryKind,
};

fn options(format: Format) -> FormatOptions {
    match format {
        Format::Json => FormatOptions {
            json_document: true,
            ..Default::default()
        },
        Format::Xml => FormatOptions {
            xml_document: true,
            ..Default::default()
        },
        Format::Csv | Format::Tsv => FormatOptions {
            tabular_kind: Some(TabularBoundaryKind::Csv),
            delimiter: Some(if format == Format::Tsv { '\t' } else { ',' }),
            csv_preserve_empty_strings: true,
            ..Default::default()
        },
        Format::X12 | Format::Edifact => FormatOptions {
            edi_kind: Some(if format == Format::X12 {
                EdiBoundaryKind::X12
            } else {
                EdiBoundaryKind::Edifact
            }),
            ..Default::default()
        },
    }
}
fn scalar(name: &str) -> SchemaNode {
    SchemaNode::scalar(name, ScalarType::String)
}
pub(super) fn project() -> Project {
    Project {
        source: SchemaNode::group("root", vec![scalar("value")]),
        target: SchemaNode::group("root", vec![scalar("value")]),
        source_path: None,
        target_path: None,
        source_options: options(Format::Json),
        target_options: options(Format::Json),
        extra_sources: vec![],
        extra_targets: vec![],
        failure_rules: vec![],
        user_functions: Default::default(),
        graph: Graph {
            nodes: [(
                1,
                Node::SourceField {
                    path: vec!["value".into()],
                    frame: None,
                },
            )]
            .into(),
        },
        root: Scope {
            bindings: vec![Binding {
                target_field: "value".into(),
                node: 1,
            }],
            ..Default::default()
        },
    }
}
pub(super) fn native(p: &Project) -> Vec<u8> {
    ferrule_mapping::project_file::encode_pretty(p)
        .unwrap()
        .into_bytes()
}
fn run(p: &Project, input: &[u8]) -> Result<Output, Failure> {
    execute(&native(p), input, Limits::default())
}

#[test]
fn json_to_csv_quotes_and_digest_binding() {
    let mut p = project();
    p.target_options = options(Format::Csv);
    let sample = r#"{"value":"a,\"b\"\n<&é"}"#.as_bytes();
    let output = run(&p, sample).unwrap();
    assert_eq!(
        String::from_utf8(output.bytes.clone()).unwrap(),
        "value\n\"a,\"\"b\"\"\n<&é\"\n"
    );
    assert_eq!(output.native_sha256, digest(&native(&p)));
    assert_eq!(output.sample_sha256, digest(sample));
    assert_eq!(output.output_sha256, digest(&output.bytes));
    assert_eq!(output.output_format, Format::Csv);
}

#[test]
fn json_to_xml_escapes_and_round_trips() {
    let mut p = project();
    p.target_options = options(Format::Xml);
    let result = run(&p, br#"{"value":"<&\"'"}"#).unwrap();
    let text = String::from_utf8(result.bytes.clone()).unwrap();
    assert!(text.contains("&lt;"));
    assert!(text.contains("&amp;"));
    let instance = codecs::read(Format::Xml, &result.bytes, &p.target, &p.target_options).unwrap();
    assert_eq!(
        instance.field("value"),
        Some(&Instance::Scalar(Value::String("<&\"'".into())))
    );
}

#[test]
fn empty_rows_and_empty_string_are_distinct() {
    let mut p = project();
    p.target_options = options(Format::Csv);
    p.root.iteration = ScopeIteration::Source(vec![]);
    assert_eq!(run(&p, b"[]").unwrap().bytes, b"value\n");
    assert!(run(&p, b"").is_err());
    p.root.iteration = ScopeIteration::None;
    let result = run(&p, br#"{"value":""}"#).unwrap();
    let rows = codecs::read(Format::Csv, &result.bytes, &p.target, &p.target_options).unwrap();
    assert_eq!(
        rows,
        Instance::Repeated(vec![Instance::Group(
            vec![(
                "value".into(),
                Instance::Scalar(Value::String(String::new()))
            )]
            .into()
        )])
    );
}

const X12_SAMPLE: &str = "ISA*00*          *00*          *ZZ*SENDER         *ZZ*RECEIVER       *261112*1200*U*00401*000000001*0*P*:~GS*PO*SENDER*RECEIVER*20261112*1200*1*X*004010~ST*850*0001~BEG*00*SA*sample~SE*3*0001~GE*1*1~IEA*1*000000001~";
const EDIFACT_SAMPLE: &str = "UNB+UNOC:3+SENDER+RECEIVER+261112:1200+1'UNH+1+ORDERS:D:96A:UN'BGM+220+sample+9'UNT+3+1'UNZ+1+1'";

fn fixture(f: Format) -> (SchemaNode, Vec<u8>, Vec<String>) {
    let (wire, element, terminator) = match f {
        Format::X12 => (X12_SAMPLE, '*', '~'),
        Format::Edifact => (EDIFACT_SAMPLE, '+', '\''),
        _ => {
            return (
                project().source,
                match f {
                    Format::Json => br#"{"value":"sample"}"#.to_vec(),
                    Format::Xml => b"<root><value>sample</value></root>".to_vec(),
                    _ => b"value\nsample\n".to_vec(),
                },
                vec!["value".into()],
            );
        }
    };
    let children = wire
        .trim_end_matches(terminator)
        .split(terminator)
        .map(|segment| {
            let mut elements = segment.split(element);
            let name = elements.next().unwrap();
            SchemaNode::group(
                name,
                elements
                    .enumerate()
                    .map(|(i, value)| {
                        let name = format!("friendly_{}", i + 1);
                        if f == Format::Edifact && value.contains(':') {
                            SchemaNode::group(
                                name,
                                value
                                    .split(':')
                                    .enumerate()
                                    .map(|(i, _)| scalar(&format!("part_{}", i + 1)))
                                    .collect(),
                            )
                        } else {
                            scalar(&name)
                        }
                    })
                    .collect(),
            )
        })
        .collect();
    let path = if f == Format::X12 {
        vec!["BEG".into(), "friendly_3".into()]
    } else {
        vec!["BGM".into(), "friendly_2".into()]
    };
    (
        SchemaNode::group("Envelope", children),
        wire.as_bytes().to_vec(),
        path,
    )
}

fn target_scope(
    instance: &Instance,
    graph: &mut Graph,
    path: &mut Vec<String>,
    replace: &[String],
    source: &[String],
) -> Scope {
    let Instance::Group(fields) = instance else {
        panic!("group fixture");
    };
    let mut scope = Scope::default();
    for (name, value) in fields {
        path.push(name.clone());
        match value {
            Instance::Scalar(value) => {
                let id = graph.nodes.len() as u32 + 1;
                let node = if path == replace {
                    Node::SourceField {
                        path: source.to_vec(),
                        frame: None,
                    }
                } else {
                    Node::Const {
                        value: value.clone(),
                    }
                };
                graph.nodes.insert(id, node);
                scope.bindings.push(Binding {
                    target_field: name.clone(),
                    node: id,
                });
            }
            Instance::Group(_) => {
                let mut child = target_scope(value, graph, path, replace, source);
                child.target_field = name.clone();
                scope.children.push(child);
            }
            _ => panic!("fixture shape"),
        }
        path.pop();
    }
    scope
}

#[test]
fn all_36_boundary_pairs_execute_real_documents() {
    let formats = [
        Format::Json,
        Format::Xml,
        Format::Csv,
        Format::Tsv,
        Format::X12,
        Format::Edifact,
    ];
    for source_format in formats {
        for target_format in formats {
            let mut p = project();
            let (source, input, source_path) = fixture(source_format);
            let (target, sample_output, target_path) = fixture(target_format);
            p.source = source;
            p.target = target;
            p.source_options = options(source_format);
            p.target_options = options(target_format);
            let target_instance =
                codecs::read(target_format, &sample_output, &p.target, &p.target_options).unwrap();
            let target_instance = match &target_instance {
                Instance::Repeated(items) => &items[0],
                v => v,
            };
            p.graph.nodes.clear();
            p.root = target_scope(
                target_instance,
                &mut p.graph,
                &mut vec![],
                &target_path,
                &source_path,
            );
            let input = String::from_utf8(input)
                .unwrap()
                .replace("sample", "mapped");
            let output = run(&p, input.as_bytes())
                .unwrap_or_else(|e| panic!("{source_format:?}->{target_format:?}: {e:?}"));
            assert_eq!(output.input_format, source_format);
            assert_eq!(output.output_format, target_format);
            let parsed =
                codecs::read(target_format, &output.bytes, &p.target, &p.target_options).unwrap();
            let mut value = match &parsed {
                Instance::Repeated(items) => &items[0],
                v => v,
            };
            for name in &target_path {
                value = value.field(name).unwrap();
            }
            assert_eq!(
                value,
                &Instance::Scalar(Value::String("mapped".into())),
                "{source_format:?}->{target_format:?}"
            );
        }
    }
}

#[test]
fn xml_namespace_identity_and_dtd_refusal() {
    let mut p = project();
    p.source_options = options(Format::Xml);
    p.target_options = options(Format::Xml);
    p.source.xml_namespace = ferrule_ir::XmlNamespace::qualified("urn:authored");
    p.target = p.source.clone();
    let result = run(
        &p,
        b"<root xmlns=\"urn:authored\"><value>sample</value></root>",
    )
    .unwrap();
    assert!(
        String::from_utf8(result.bytes)
            .unwrap()
            .contains("urn:authored")
    );
    assert_eq!(
        run(
            &p,
            b"<root xmlns=\"urn:wrong\"><value>sample</value></root>"
        )
        .unwrap_err(),
        Failure::InvalidInput
    );
    assert_eq!(run(&p, b"<!DOCTYPE root SYSTEM 'https://example.invalid/schema'><root xmlns=\"urn:authored\"/>").unwrap_err(), Failure::InvalidInput);
}

#[test]
fn budgets_are_atomic_and_diagnostics_are_categorical() {
    let p = project();
    let bytes = native(&p);
    let input = br#"{"value":"private-value"}"#;
    assert_eq!(
        execute(
            &bytes,
            input,
            Limits {
                input_bytes: 1,
                ..Default::default()
            }
        )
        .unwrap_err(),
        Failure::InputLimit
    );
    assert_eq!(
        execute(
            &bytes,
            input,
            Limits {
                output_bytes: 1,
                ..Default::default()
            }
        )
        .unwrap_err(),
        Failure::OutputLimit
    );
    assert_eq!(
        execute(
            &bytes,
            input,
            Limits {
                elapsed: Duration::from_nanos(1),
                ..Default::default()
            }
        )
        .unwrap_err(),
        Failure::DeadlineExceeded
    );
    let mut p = project();
    p.target = SchemaNode::group("root", vec![scalar("value"), scalar("second")]);
    p.root.bindings.push(Binding {
        target_field: "second".into(),
        node: 1,
    });
    assert_eq!(
        execute(
            &native(&p),
            input,
            Limits {
                work: 1,
                ..Default::default()
            }
        )
        .unwrap_err(),
        Failure::ExecutionLimit
    );
    p.target_options = options(Format::Csv);
    assert_eq!(
        execute(
            &native(&p),
            input,
            Limits {
                output_bytes: 1,
                ..Default::default()
            }
        )
        .unwrap_err(),
        Failure::OutputLimit
    );
}

#[test]
fn paths_imports_named_endpoints_and_operators_are_refused() {
    let mut p = project();
    p.source_path = Some("https://example.invalid/private".into());
    assert_eq!(run(&p, b"{}").unwrap_err(), Failure::ExternalDependency);
    p.source_path = None;
    p.graph.nodes.insert(
        2,
        Node::RuntimeValue {
            value: ferrule_mapping::RuntimeValue::MappingFilePath,
        },
    );
    assert_eq!(run(&p, b"{}").unwrap_err(), Failure::UnsupportedOperator);
    p.graph.nodes.remove(&2);
    p.graph.nodes.insert(
        2,
        Node::Call {
            function: "untrusted.external".into(),
            args: vec![],
        },
    );
    assert_eq!(run(&p, b"{}").unwrap_err(), Failure::UnsupportedOperator);
    p.graph.nodes.remove(&2);
    p.extra_sources.push(ferrule_mapping::NamedSource {
        name: "lookup".into(),
        path: "C:/private".into(),
        schema: p.source.clone(),
        options: p.source_options.clone(),
        dynamic_path: None,
    });
    assert_eq!(run(&p, b"{}").unwrap_err(), Failure::NamedEndpoints);
    p.extra_sources.clear();
    p.target_options.xml_document = true;
    assert_eq!(run(&p, b"{}").unwrap_err(), Failure::UnsupportedBoundary);
    p.target_options = options(Format::Json);
    let mut raw = serde_json::to_value(&p).unwrap();
    raw["imports"] = serde_json::json!(["https://example.invalid/private"]);
    assert_eq!(
        execute(&serde_json::to_vec(&raw).unwrap(), b"{}", Limits::default()).unwrap_err(),
        Failure::ExternalDependency
    );
    raw.as_object_mut().unwrap().remove("imports");
    raw["source_options"]["future_transport"] = true.into();
    assert_eq!(
        execute(&serde_json::to_vec(&raw).unwrap(), b"{}", Limits::default()).unwrap_err(),
        Failure::UnsupportedBoundary
    );
}

#[test]
fn edi_envelope_reference_count_version_and_reserved_characters() {
    for f in [Format::X12, Format::Edifact] {
        let (schema, wire, path) = fixture(f);
        let mut p = project();
        p.source = schema;
        p.source_options = options(f);
        p.graph
            .nodes
            .insert(1, Node::SourceField { path, frame: None });
        assert!(run(&p, &wire).is_ok());
        let wire = String::from_utf8(wire).unwrap();
        let bad_count = wire.replace(
            if f == Format::X12 { "SE*3*" } else { "UNT+3+" },
            if f == Format::X12 { "SE*4*" } else { "UNT+4+" },
        );
        assert_eq!(
            run(&p, bad_count.as_bytes()).unwrap_err(),
            Failure::InvalidInput
        );
        let bad_reference = wire.replace(
            if f == Format::X12 {
                "GE*1*1"
            } else {
                "UNZ+1+1"
            },
            if f == Format::X12 {
                "GE*1*9"
            } else {
                "UNZ+1+9"
            },
        );
        assert_eq!(
            run(&p, bad_reference.as_bytes()).unwrap_err(),
            Failure::InvalidInput
        );
        assert_eq!(run(&p, b"").unwrap_err(), Failure::InvalidInput);
        if f == Format::X12 {
            p.source_options.x12_interchange_version = Some("00501".into());
            assert_eq!(run(&p, wire.as_bytes()).unwrap_err(), Failure::InvalidInput);
            let version5010 = wire.replace("*U*00401*", "*^*00501*");
            assert!(run(&p, version5010.as_bytes()).is_ok());
        } else {
            let escaped = wire.replace("sample", "a?+b?:c?'d??e");
            let output = run(&p, escaped.as_bytes()).unwrap();
            assert_eq!(
                serde_json::from_slice::<serde_json::Value>(&output.bytes).unwrap()["value"],
                "a+b:c'd?e"
            );
        }
    }
}

fn to_edi(format: Format) -> Project {
    let mut p = project();
    let (schema, sample, path) = fixture(format);
    p.target = schema;
    p.target_options = options(format);
    let value = codecs::read(format, &sample, &p.target, &p.target_options).unwrap();
    p.graph.nodes.clear();
    p.root = target_scope(&value, &mut p.graph, &mut vec![], &path, &["value".into()]);
    p
}

#[test]
fn edi_output_escaping_and_observed_dialect_metadata() {
    let p = to_edi(Format::Edifact);
    let output = run(&p, br#"{"value":"a+b:c'd?e"}"#).unwrap();
    assert!(
        String::from_utf8(output.bytes)
            .unwrap()
            .contains("a?+b?:c?'d??e")
    );
    match output.dialect {
        Dialect::Edifact {
            versions,
            repetition,
            ..
        } => {
            assert_eq!(versions, ["3"]);
            assert_eq!(repetition, None);
        }
        _ => panic!("EDIFACT metadata"),
    }
    let mut p = to_edi(Format::X12);
    assert_eq!(
        run(&p, br#"{"value":"a*b"}"#).unwrap_err(),
        Failure::InvalidOutput
    );
    let result = run(&p, br#"{"value":"mapped"}"#).unwrap();
    match result.dialect {
        Dialect::X12 {
            versions,
            repetition,
            ..
        } => {
            assert_eq!(versions, ["00401"]);
            assert_eq!(repetition, None);
        }
        _ => panic!("X12 metadata"),
    }
    // The mapped ISA version is authoritative; a conflicting native option fails.
    p.target_options.x12_interchange_version = Some("00501".into());
    assert!(matches!(
        run(&p, br#"{"value":"mapped"}"#),
        Err(Failure::InvalidOutput)
    ));
}

#[test]
fn csv_tsv_multiple_rows_positions_bom_and_no_quote_refusal() {
    let mut p = project();
    p.source_options = options(Format::Csv);
    p.target_options = options(Format::Tsv);
    p.target_options.csv_utf8_bom = true;
    p.target = SchemaNode::group(
        "root",
        vec![scalar("value"), SchemaNode::scalar("row", ScalarType::Int)],
    );
    p.graph
        .nodes
        .insert(2, Node::Position { collection: vec![] });
    p.root.bindings.push(Binding {
        target_field: "row".into(),
        node: 2,
    });
    p.root.iteration = ScopeIteration::Source(vec![]);
    let output = run(&p, b"value\n\"a,b\"\n\"two\tcolumns\"\n").unwrap();
    assert_eq!(
        output.bytes,
        "\u{feff}value\trow\na,b\t1\n\"two\tcolumns\"\t2\n".as_bytes()
    );
    match output.dialect {
        Dialect::Csv(d) => assert_eq!(
            d,
            CsvDialect {
                delimiter: '\t',
                quote: Some('"'),
                header_row: true,
                utf8_bom: true,
                preserve_empty_strings: true
            }
        ),
        _ => panic!("CSV dialect"),
    }
    p.target_options.csv_quote_disabled = true;
    assert_eq!(
        run(&p, b"value\n\"two\tcolumns\"\n").unwrap_err(),
        Failure::InvalidOutput
    );
}

#[test]
fn all_formats_refuse_empty_or_malformed_documents_as_appropriate() {
    for f in [
        Format::Json,
        Format::Xml,
        Format::Csv,
        Format::Tsv,
        Format::X12,
        Format::Edifact,
    ] {
        let mut p = project();
        let (schema, _, path) = fixture(f);
        p.source = schema;
        p.source_options = options(f);
        p.graph
            .nodes
            .insert(1, Node::SourceField { path, frame: None });
        if matches!(f, Format::Csv | Format::Tsv) {
            p.root.iteration = ScopeIteration::Source(vec![]);
            assert_eq!(
                serde_json::from_slice::<serde_json::Value>(&run(&p, b"").unwrap().bytes).unwrap(),
                serde_json::json!([])
            );
        } else {
            assert_eq!(run(&p, b"").unwrap_err(), Failure::InvalidInput);
        }
        assert_eq!(run(&p, &[0xff]).unwrap_err(), Failure::InvalidInput);
    }
}

#[test]
fn expansion_and_unsupported_scope_fail_before_returning_output() {
    let mut p = project();
    p.root.iteration = ScopeIteration::Source(vec![]);
    let sample =
        serde_json::to_vec(&vec![serde_json::json!({"value":"x".repeat(4096)}); 100]).unwrap();
    p.target = SchemaNode::group("root", (0..30).map(|i| scalar(&format!("v{i}"))).collect());
    p.root.bindings = (0..30)
        .map(|i| Binding {
            target_field: format!("v{i}"),
            node: 1,
        })
        .collect();
    assert_eq!(run(&p, &sample).unwrap_err(), Failure::ExecutionLimit);
    p.root.children.push(Scope {
        target_field: "nested".into(),
        iteration: ScopeIteration::Source(vec![]),
        ..Default::default()
    });
    assert_eq!(run(&p, &sample).unwrap_err(), Failure::UnsupportedScope);
}

#[test]
fn exact_native_bytes_bind_different_whitespace_without_saved_identity() {
    let p = project();
    let compact = serde_json::to_vec(&p).unwrap();
    let pretty = native(&p);
    let sample = br#"{"value":"new unsaved proposal"}"#;
    let a = execute(&compact, sample, Limits::default()).unwrap();
    let b = execute(&pretty, sample, Limits::default()).unwrap();
    assert_ne!(a.native_sha256, b.native_sha256);
    assert_eq!(a.sample_sha256, b.sample_sha256);
    assert_eq!(a.bytes, b.bytes);
    assert_eq!(a.engine_revision, crate::protocol::ENGINE_REVISION);
}
