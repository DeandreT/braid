use serde::{Deserialize, Serialize};

use crate::protocol::{NativeLocator, OperationCapability};

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ProjectionPayload {
    pub coverage: Coverage,
    pub boundaries: Vec<Boundary>,
    pub fields: Vec<Field>,
    pub parameters: Vec<Parameter>,
    pub rules: Vec<Rule>,
    pub controls: Vec<Control>,
    pub relationships: Vec<Relationship>,
    pub diagnostics: Vec<Diagnostic>,
    pub capabilities: Vec<OperationCapability>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Coverage {
    pub status: String,
    pub gaps: Vec<Gap>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Gap {
    pub code: String,
    pub message: String,
    pub native_locator: NativeLocator,
    pub projection_locator: Option<NodeReference>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Boundary {
    pub id: String,
    pub name: String,
    pub role: String,
    pub format: String,
    pub shape: String,
    pub root_field_id: Option<String>,
    pub native_locator: NativeLocator,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Field {
    pub id: String,
    pub boundary_id: String,
    pub path: Vec<PathSegment>,
    pub name: String,
    pub data_type: DataType,
    pub cardinality: Cardinality,
    pub requirement: String,
    pub nullability: String,
    pub native_locator: NativeLocator,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DataType {
    pub kind: String,
    pub scalar_types: Vec<String>,
    pub native_name: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Cardinality {
    pub kind: String,
    pub minimum: Option<String>,
    pub maximum: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(
    tag = "kind",
    rename_all = "camelCase",
    rename_all_fields = "camelCase"
)]
pub enum PathSegment {
    Field {
        name: String,
        namespace_uri: Option<String>,
    },
    Attribute {
        name: String,
        namespace_uri: Option<String>,
    },
    AnyItem,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(
    tag = "kind",
    rename_all = "camelCase",
    rename_all_fields = "camelCase"
)]
pub enum Literal {
    String { value: String },
    Boolean { value: bool },
    ExactNumber { flavor: String, value: String },
    Null { null_kind: String },
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(
    tag = "kind",
    rename_all = "camelCase",
    rename_all_fields = "camelCase"
)]
pub enum NodeReference {
    Boundary { boundary_id: String },
    Field { field_id: String },
    Parameter { parameter_id: String },
    Rule { rule_id: String },
    Control { control_id: String },
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(
    tag = "kind",
    rename_all = "camelCase",
    rename_all_fields = "camelCase"
)]
pub enum Dependency {
    Field { field_id: String, role: String },
    Parameter { parameter_id: String, role: String },
    Constant { value: Literal, role: String },
    Node { node: NodeReference, role: String },
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(
    tag = "kind",
    rename_all = "camelCase",
    rename_all_fields = "camelCase"
)]
pub enum Detail {
    Group {
        label: String,
        children: Vec<Detail>,
    },
    Expression {
        operation: String,
        arguments: Vec<Detail>,
    },
    Literal {
        value: Literal,
    },
    Reference {
        reference: NodeReference,
    },
    Unsupported {
        construct: String,
        description: String,
        native_locator: NativeLocator,
    },
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Parameter {
    pub id: String,
    pub name: String,
    pub data_type: DataType,
    pub requirement: String,
    pub default_value: Option<Literal>,
    pub native_locator: NativeLocator,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Rule {
    pub id: String,
    pub output_field_id: String,
    pub summary: String,
    pub dependencies: Vec<Dependency>,
    pub details: Vec<Detail>,
    pub native_locator: NativeLocator,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Control {
    pub id: String,
    pub kind: String,
    pub summary: String,
    pub applies_to: Vec<NodeReference>,
    pub dependencies: Vec<Dependency>,
    pub details: Vec<Detail>,
    pub native_locator: NativeLocator,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Relationship {
    pub id: String,
    pub kind: String,
    pub from: NodeReference,
    pub to: NodeReference,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Diagnostic {
    pub code: String,
    pub severity: String,
    pub message: String,
    pub native_locator: Option<NativeLocator>,
    pub projection_locator: Option<NodeReference>,
}
