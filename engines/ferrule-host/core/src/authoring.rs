//! Fresh native construction. This is an authoring contract, not runtime admission.
pub mod transport;

use std::collections::{BTreeMap, BTreeSet};

use ferrule_edit_ops::EditScript;
use ferrule_ir::{ScalarType, SchemaKind, SchemaNode, XmlNamespace};
use ferrule_mapping::{EdiBoundaryKind, FormatOptions, Graph, Project, Scope, TabularBoundaryKind};
use serde::Deserialize;

use crate::edit::{self, Checkpoint, EditFailure};

pub const MAX_PLAN_BYTES: usize = 1024 * 1024;
pub const MAX_SCHEMA_NODES: usize = 8192;
pub const MAX_SCHEMA_DEPTH: usize = 24;
const MAX_TEXT_BYTES: usize = 4096;

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Plan {
    format: Version,
    source: Boundary,
    target: Boundary,
    edit: EditScript,
}

#[derive(Deserialize)]
enum Version {
    #[serde(rename = "weft.authoring.v1")]
    V1,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Boundary {
    schema: Schema,
    options: Options,
}

#[derive(Deserialize)]
#[serde(tag = "kind", rename_all = "camelCase", deny_unknown_fields)]
enum Options {
    Json {},
    Xml {},
    X12 {
        interchange_version: String,
    },
    Edifact {},
    Csv {
        header: bool,
        quote: bool,
        bom: bool,
        preserve_empty_strings: bool,
    },
    Tsv {
        header: bool,
        quote: bool,
        bom: bool,
        preserve_empty_strings: bool,
    },
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Schema {
    name: String,
    shape: Shape,
    #[serde(default)]
    repeating: bool,
    #[serde(default)]
    nullable: bool,
    #[serde(default)]
    required: Vec<String>,
    xml: Option<Xml>,
}

#[derive(Deserialize)]
#[serde(tag = "kind", rename_all = "camelCase", deny_unknown_fields)]
enum Shape {
    Scalar { scalar: Scalar },
    Group { children: Vec<Schema> },
}

#[derive(Clone, Copy, Deserialize)]
#[serde(rename_all = "camelCase")]
enum Scalar {
    String,
    Int,
    Float,
    Bool,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Xml {
    /// Empty means explicitly unqualified; omission of xml means inherited namespace.
    namespace: String,
    #[serde(default)]
    attribute: bool,
    #[serde(default)]
    text: bool,
    #[serde(default)]
    optional: bool,
    #[serde(default)]
    nillable: bool,
}

fn refusal(code: &str, message: &str) -> EditFailure {
    EditFailure::new(code, message)
}

/// Parses and checks structural budgets before allocating any Ferrule schema or project.
/// No native bytes or external resources supplied by a model are accepted.
pub fn compile(bytes: &[u8]) -> Result<Checkpoint, EditFailure> {
    if bytes.is_empty() || bytes.len() > MAX_PLAN_BYTES {
        return Err(refusal(
            "authoringLimit",
            "Authoring input must be 1..1048576 bytes.",
        ));
    }
    // serde_json's recursion guard remains enabled. This bounded preliminary tree is
    // discarded before native assembly; the typed parse below rejects unknown/duplicate fields.
    let value: serde_json::Value = serde_json::from_slice(bytes).map_err(|_| {
        refusal(
            "invalidAuthoring",
            "Invalid or excessively nested authoring JSON.",
        )
    })?;
    check_json(&value, 0, &mut 0)?;
    drop(value);
    let plan: Plan = serde_json::from_slice(bytes).map_err(|_| {
        refusal(
            "invalidAuthoring",
            "Authoring must match the closed weft.authoring.v1 contract.",
        )
    })?;
    let _ = plan.format;
    let mut count = 0;
    check_schema(&plan.source.schema, &plan.source.options, 0, &mut count)?;
    check_schema(&plan.target.schema, &plan.target.options, 0, &mut count)?;
    check_boundary(&plan.source)?;
    check_boundary(&plan.target)?;
    let operations = edit::canonical_script(&plan.edit)?;
    let project = Project {
        source: assemble_schema(plan.source.schema)?,
        target: assemble_schema(plan.target.schema)?,
        source_options: assemble_options(plan.source.options)?,
        target_options: assemble_options(plan.target.options)?,
        source_path: None,
        target_path: None,
        extra_sources: vec![],
        extra_targets: vec![],
        failure_rules: vec![],
        user_functions: BTreeMap::new(),
        graph: Graph::default(),
        root: Scope::default(),
    };
    let native = ferrule_mapping::project_file::encode_pretty(&project).map_err(|_| {
        refusal(
            "nativeEncodeFailed",
            "Could not encode the authored project.",
        )
    })?;
    // Session enforces native bounds, applies the closed edit vocabulary, inspects,
    // validates and projects; checkpoint encodes through the authoritative codec.
    edit::Session::open(native.as_bytes(), &operations)?.checkpoint("entry.native.json")
}

fn check_json(
    value: &serde_json::Value,
    depth: usize,
    count: &mut usize,
) -> Result<(), EditFailure> {
    *count += 1;
    if depth > 80 || *count > 100_000 {
        return Err(refusal(
            "authoringLimit",
            "Authoring JSON exceeds depth or value-count limits.",
        ));
    }
    match value {
        serde_json::Value::String(s) if s.len() > MAX_TEXT_BYTES => {
            return Err(refusal(
                "authoringLimit",
                "Authoring text exceeds 4096 UTF-8 bytes.",
            ));
        }
        serde_json::Value::Array(items) => {
            for item in items {
                check_json(item, depth + 1, count)?;
            }
        }
        serde_json::Value::Object(items) => {
            for (key, item) in items {
                if key.len() > MAX_TEXT_BYTES {
                    return Err(refusal(
                        "authoringLimit",
                        "Authoring key exceeds its byte limit.",
                    ));
                }
                check_json(item, depth + 1, count)?;
            }
        }
        _ => (),
    }
    Ok(())
}

fn check_schema(
    s: &Schema,
    options: &Options,
    depth: usize,
    count: &mut usize,
) -> Result<(), EditFailure> {
    *count += 1;
    if depth > MAX_SCHEMA_DEPTH || *count > MAX_SCHEMA_NODES {
        return Err(refusal(
            "authoringLimit",
            "Combined schemas exceed 8192 nodes or depth 24.",
        ));
    }
    if s.name.is_empty() || s.name.chars().any(char::is_control) {
        return Err(refusal(
            "invalidSchema",
            "Schema names must be nonempty and contain no control characters.",
        ));
    }
    if matches!(options, Options::Xml {}) && !xml_name(&s.name) {
        return Err(refusal(
            "unsupportedSemantics",
            "V1 XML local names use ASCII letters, digits, underscore, dot and hyphen; namespace prefixes are not names.",
        ));
    }
    if s.repeating && s.nullable {
        return Err(refusal(
            "unsupportedSemantics",
            "Nullable repeated schemas require separate item/container nullability, outside v1.",
        ));
    }
    if !matches!(options, Options::Json {}) && (s.nullable || !s.required.is_empty()) {
        return Err(refusal(
            "unsupportedSemantics",
            "JSON nullability and required members require a JSON boundary.",
        ));
    }
    if let Some(xml) = &s.xml {
        if !matches!(options, Options::Xml {})
            || (xml.attribute && xml.text)
            || ((xml.attribute || xml.text)
                && (!matches!(s.shape, Shape::Scalar { .. })
                    || s.repeating
                    || xml.nillable
                    || xml.optional))
            || (depth == 0 && (xml.attribute || xml.text))
        {
            return Err(refusal(
                "unsupportedSemantics",
                "Invalid XML attribute, text, repetition or namespace metadata.",
            ));
        }
    }
    match &s.shape {
        Shape::Scalar { .. } if !s.required.is_empty() => {
            return Err(refusal(
                "invalidSchema",
                "Only groups have required members.",
            ));
        }
        Shape::Group { children } => {
            let mut names = BTreeSet::new();
            for child in children {
                if !names.insert(&child.name) {
                    return Err(refusal(
                        "invalidSchema",
                        "Sibling schema names must be unique.",
                    ));
                }
                check_schema(child, options, depth + 1, count)?;
            }
            let mut required = BTreeSet::new();
            for name in &s.required {
                if !names.contains(name) || !required.insert(name) {
                    return Err(refusal(
                        "invalidSchema",
                        "Required members must be unique declared children.",
                    ));
                }
            }
        }
        _ => (),
    }
    Ok(())
}

fn check_boundary(b: &Boundary) -> Result<(), EditFailure> {
    let Shape::Group { children } = &b.schema.shape else {
        return Err(refusal(
            "unsupportedSemantics",
            "V1 requires group document roots.",
        ));
    };
    match &b.options {
        Options::Csv { .. } | Options::Tsv { .. } => {
            if children.is_empty()
                || children
                    .iter()
                    .any(|c| c.repeating || !matches!(c.shape, Shape::Scalar { .. }))
            {
                return Err(refusal(
                    "unsupportedSemantics",
                    "CSV/TSV requires a nonempty flat ordered scalar-column schema.",
                ));
            }
        }
        Options::X12 {
            interchange_version,
        } => {
            if interchange_version.len() != 5
                || !interchange_version.bytes().all(|b| b.is_ascii_digit())
            {
                return Err(refusal(
                    "invalidBoundary",
                    "X12 interchange_version must be exactly five ASCII digits.",
                ));
            }
            check_edi_root(children, &["ISA"])?;
        }
        Options::Edifact {} => check_edi_root(children, &["UNB", "UNH"])?,
        _ => (),
    }
    Ok(())
}

fn check_edi_root(children: &[Schema], triggers: &[&str]) -> Result<(), EditFailure> {
    let Some(first) = children.first() else {
        return Err(refusal(
            "invalidBoundary",
            "EDI requires an ordered envelope schema.",
        ));
    };
    if !triggers.contains(&first.name.as_str()) || !matches!(first.shape, Shape::Group { .. }) {
        return Err(refusal(
            "unsupportedSemantics",
            "EDI requires an explicit leading ISA or UNB/UNH segment group.",
        ));
    }
    Ok(())
}

fn xml_name(name: &str) -> bool {
    name.bytes()
        .next()
        .is_some_and(|b| b.is_ascii_alphabetic() || b == b'_')
        && name
            .bytes()
            .all(|b| b.is_ascii_alphanumeric() || matches!(b, b'_' | b'.' | b'-'))
}

fn assemble_schema(s: Schema) -> Result<SchemaNode, EditFailure> {
    let mut node = match s.shape {
        Shape::Scalar { scalar } => SchemaNode::scalar(
            s.name,
            match scalar {
                Scalar::String => ScalarType::String,
                Scalar::Int => ScalarType::Int,
                Scalar::Float => ScalarType::Float,
                Scalar::Bool => ScalarType::Bool,
            },
        ),
        Shape::Group { children } => SchemaNode::group(
            s.name,
            children
                .into_iter()
                .map(assemble_schema)
                .collect::<Result<_, _>>()?,
        ),
    };
    node.repeating = s.repeating;
    if matches!(node.kind, SchemaKind::Scalar { .. }) {
        node.nullable = s.nullable;
    } else {
        node.container_nullable = s.nullable;
    }
    if let SchemaKind::Group { required, .. } = &mut node.kind {
        *required = s.required;
    }
    if let Some(xml) = s.xml {
        node.xml_namespace = Some(if xml.namespace.is_empty() {
            XmlNamespace::Unqualified
        } else {
            XmlNamespace::qualified(xml.namespace)
                .ok_or_else(|| refusal("invalidSchema", "Invalid XML namespace."))?
        });
        node.attribute = xml.attribute;
        node.text = xml.text;
        node.xml_optional = xml.optional;
        node.nillable = xml.nillable;
    }
    Ok(node)
}

fn assemble_options(options: Options) -> Result<FormatOptions, EditFailure> {
    let mut result = FormatOptions::default();
    match options {
        Options::Json {} => result.json_document = true,
        Options::Xml {} => result.xml_document = true,
        Options::X12 {
            interchange_version,
        } => {
            result.edi_kind = Some(EdiBoundaryKind::X12);
            result.x12_interchange_version = Some(interchange_version);
        }
        Options::Edifact {} => result.edi_kind = Some(EdiBoundaryKind::Edifact),
        Options::Csv {
            header,
            quote,
            bom,
            preserve_empty_strings,
        } => {
            result = tabular(',', header, quote, bom, preserve_empty_strings);
        }
        Options::Tsv {
            header,
            quote,
            bom,
            preserve_empty_strings,
        } => {
            result = tabular('\t', header, quote, bom, preserve_empty_strings);
        }
    }
    Ok(result)
}

fn tabular(delimiter: char, header: bool, quote: bool, bom: bool, empty: bool) -> FormatOptions {
    FormatOptions {
        tabular_kind: Some(TabularBoundaryKind::Csv),
        delimiter: Some(delimiter),
        has_header_row: Some(header),
        csv_quote_disabled: !quote,
        csv_utf8_bom: bom,
        csv_preserve_empty_strings: empty,
        ..Default::default()
    }
}

#[cfg(test)]
mod tests;
