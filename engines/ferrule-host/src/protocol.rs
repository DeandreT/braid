use serde::{Deserialize, Serialize};

pub const PROTOCOL_VERSION: &str = "weft.ferrule-host.v1";
pub const ENGINE_REVISION: &str = "577359a991a7dd44172a3c4af91fdcab7592457c";
pub const MAX_HEADER_BYTES: usize = 16 * 1024;
pub const MAX_NATIVE_BYTES: usize = 4 * 1024 * 1024;
pub const MAX_RESPONSE_BYTES: usize = 8 * 1024 * 1024;
pub const MAX_DIAGNOSTICS: u32 = 128;
pub const MAX_ITEMS: u32 = 8192;
pub const MAX_TEXT_BYTES: u32 = 2048;

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Revision {
    pub organization_id: String,
    pub project_id: String,
    pub mapping_id: String,
    pub revision_id: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Limits {
    pub maximum_diagnostics: u32,
    pub maximum_items: u32,
    pub maximum_text_bytes: u32,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct RequestHeader {
    pub protocol_version: String,
    pub request_id: String,
    pub operation: String,
    pub revision: Revision,
    pub native_digest: String,
    pub document_id: String,
    pub limits: Limits,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Response {
    pub protocol_version: String,
    pub request_id: Option<String>,
    pub operation: Option<String>,
    pub revision: Option<Revision>,
    pub native_digest: Option<String>,
    pub document_id: Option<String>,
    pub engine_revision: String,
    pub result: Option<WorkerResult>,
    pub failure: Option<Failure>,
}

impl Response {
    pub fn failure(header: Option<&RequestHeader>, code: &str, message: &str) -> Self {
        Self::with_failure(
            header,
            Failure {
                code: code.into(),
                message: message.into(),
                message_truncated: false,
                native_locator: None,
            },
        )
    }

    pub fn with_failure(header: Option<&RequestHeader>, failure: Failure) -> Self {
        let mut response = Self::context(header);
        response.failure = Some(failure);
        response
    }

    pub fn success(header: &RequestHeader, result: WorkerResult) -> Self {
        let mut response = Self::context(Some(header));
        response.result = Some(result);
        response
    }

    fn context(header: Option<&RequestHeader>) -> Self {
        Self {
            protocol_version: PROTOCOL_VERSION.into(),
            request_id: header.map(|header| header.request_id.clone()),
            operation: header.map(|header| header.operation.clone()),
            revision: header.map(|header| header.revision.clone()),
            native_digest: header.map(|header| header.native_digest.clone()),
            document_id: header.map(|header| header.document_id.clone()),
            engine_revision: ENGINE_REVISION.into(),
            result: None,
            failure: None,
        }
    }

    pub fn replace_result_with_failure(&self, code: &str, message: &str) -> Self {
        Self {
            protocol_version: self.protocol_version.clone(),
            request_id: self.request_id.clone(),
            operation: self.operation.clone(),
            revision: self.revision.clone(),
            native_digest: self.native_digest.clone(),
            document_id: self.document_id.clone(),
            engine_revision: self.engine_revision.clone(),
            result: None,
            failure: Some(Failure {
                code: code.into(),
                message: message.into(),
                message_truncated: false,
                native_locator: None,
            }),
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "kind", rename_all = "camelCase")]
pub enum WorkerResult {
    Inspect(InspectResult),
    Validate(ValidationResult),
    Project(crate::projection::ProjectionPayload),
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Failure {
    pub code: String,
    pub message: String,
    pub message_truncated: bool,
    pub native_locator: Option<NativeLocator>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(
    tag = "kind",
    rename_all = "camelCase",
    rename_all_fields = "camelCase"
)]
pub enum NativeLocator {
    Document {
        document_id: String,
    },
    JsonPointer {
        document_id: String,
        pointer: String,
    },
    GraphNode {
        document_id: String,
        node_id: String,
        function_id: Option<String>,
    },
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct InspectResult {
    pub boundaries: Vec<BoundaryInventory>,
    pub counts: MetadataCounts,
    pub unresolved_resources: Vec<ResourceReference>,
    pub capabilities: Vec<OperationCapability>,
    pub truncated: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct BoundaryInventory {
    pub id: String,
    pub role: String,
    pub name: String,
    pub name_truncated: bool,
    pub root_name: String,
    pub root_name_truncated: bool,
    pub shape: String,
    pub repeating: bool,
    pub native_locator: NativeLocator,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MetadataCounts {
    pub schema_nodes: u32,
    pub graph_nodes: u32,
    pub scope_nodes: u32,
    pub user_functions: u32,
    pub failure_rules: u32,
    pub total_items: u32,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ResourceReference {
    pub boundary_id: String,
    pub kind: String,
    pub resolution: String,
    pub native_locator: NativeLocator,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct OperationCapability {
    pub operation: String,
    pub status: String,
    pub profile: String,
    pub boundary_ids: Vec<String>,
    pub conditions: Vec<CapabilityCondition>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CapabilityCondition {
    pub code: String,
    pub description: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ValidationResult {
    pub is_valid: bool,
    pub total_issue_count: u32,
    pub emitted_issue_count: u32,
    pub truncated: bool,
    pub issues: Vec<ValidationIssue>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ValidationIssue {
    pub code: String,
    pub location: String,
    pub location_truncated: bool,
    pub message: String,
    pub message_truncated: bool,
    pub owner: Option<ValidationOwner>,
    pub native_locator: NativeLocator,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(
    tag = "kind",
    rename_all = "camelCase",
    rename_all_fields = "camelCase"
)]
pub enum ValidationOwner {
    GraphNode {
        function_id: Option<String>,
        node_id: String,
    },
    UserFunction {
        function_id: String,
    },
    Endpoint {
        endpoint: ValidationEndpoint,
    },
    SchemaNode {
        endpoint: ValidationEndpoint,
        path: Vec<SchemaStep>,
    },
    Scope {
        target: ValidationEndpoint,
        path: Vec<ScopeStep>,
    },
    FailureRule {
        index: String,
    },
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(
    tag = "kind",
    rename_all = "camelCase",
    rename_all_fields = "camelCase"
)]
pub enum ValidationEndpoint {
    Source,
    Target,
    NamedSource { index: String, name: String },
    NamedTarget { index: String, name: String },
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(
    tag = "kind",
    rename_all = "camelCase",
    rename_all_fields = "camelCase"
)]
pub enum SchemaStep {
    Child { index: String },
    DynamicField,
    Contains { index: String },
    DependentSchema { index: String },
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(
    tag = "kind",
    rename_all = "camelCase",
    rename_all_fields = "camelCase"
)]
pub enum ScopeStep {
    Child { index: String },
    DynamicChild { index: String },
    Segment { index: String },
}

pub fn bounded_text(text: &str, maximum_bytes: usize) -> (String, bool) {
    if text.len() <= maximum_bytes {
        return (text.into(), false);
    }
    let mut end = maximum_bytes;
    while !text.is_char_boundary(end) {
        end -= 1;
    }
    (text[..end].into(), true)
}

pub fn bounded_locator(locator: NativeLocator, maximum_bytes: usize) -> NativeLocator {
    match locator {
        NativeLocator::JsonPointer {
            document_id,
            pointer,
        } if pointer.len() > maximum_bytes => NativeLocator::Document { document_id },
        locator => locator,
    }
}
