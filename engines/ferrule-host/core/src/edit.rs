//! The browser and authoritative worker replay exactly this implementation.

use ferrule_edit_ops::{EditError, EditOperation, EditScript, EditSession, Format};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};

use crate::{decode, framing_limit, inspect, projection, protocol, validation};

pub const MAX_OPERATIONS: usize = 2_000;
pub const MAX_OPERATIONS_BYTES: usize = 1024 * 1024;
pub const MAX_REPORT_BYTES: usize = 16 * 1024 * 1024;

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct EditFailure {
    pub code: String,
    pub message: String,
    pub message_truncated: bool,
    /// Zero-based decimal string, never an imprecise JavaScript number.
    pub operation_index: Option<String>,
}

impl EditFailure {
    pub fn new(code: &str, message: &str) -> Self {
        let (message, message_truncated) =
            protocol::bounded_text(message, protocol::MAX_TEXT_BYTES as usize);
        Self {
            code: code.into(),
            message,
            message_truncated,
            operation_index: None,
        }
    }

    fn operation(index: usize, error: EditError) -> Self {
        Self {
            operation_index: Some(index.to_string()),
            ..Self::new("operationRejected", &error.to_string())
        }
    }
}

pub struct Session {
    inner: EditSession,
}

/// Encoding is used only by the worker and cross-target qualification, not by the page client.
pub struct Checkpoint {
    pub native: Vec<u8>,
    pub native_digest: String,
    pub view: View,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct View {
    pub operations: EditScript,
    pub validation: protocol::ValidationResult,
    pub projection: projection::ProjectionPayload,
    pub can_undo: bool,
    pub can_redo: bool,
    pub undo_label: Option<String>,
    pub redo_label: Option<String>,
    pub targets: Vec<EditTarget>,
    pub next_node_id: Option<String>,
}

/// Ferrule resolves these handles; the page never interprets native JSON or native pointers.
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct EditTarget {
    pub field_id: String,
    pub binding: ferrule_edit_ops::BindingRef,
    pub node: Option<String>,
}

fn edit_targets(
    project: &ferrule_mapping::Project,
    projection: &projection::ProjectionPayload,
) -> Vec<EditTarget> {
    use crate::projection::types::PathSegment;
    let outputs = projection
        .boundaries
        .iter()
        .filter(|b| matches!(b.role.as_str(), "primaryOutput" | "namedOutput"))
        .collect::<Vec<_>>();
    let mut result = Vec::new();
    for field in &projection.fields {
        let Some(output) = outputs.iter().position(|b| b.id == field.boundary_id) else {
            continue;
        };
        let (root, target) = if output == 0 {
            (&project.root, ferrule_edit_ops::Target::Primary {})
        } else if let Some(named) = project.extra_targets.get(output - 1) {
            (
                &named.root,
                ferrule_edit_ops::Target::Named {
                    name: named.name.clone(),
                },
            )
        } else {
            continue;
        };
        let mut names = field
            .path
            .iter()
            .filter_map(|p| match p {
                PathSegment::Field { name, .. } | PathSegment::Attribute { name, .. } => Some(name),
                PathSegment::AnyItem => None,
            })
            .collect::<Vec<_>>();
        let Some(name) = names.pop() else { continue };
        let mut scope = root;
        let mut children = Vec::new();
        let mut found = true;
        for name in names {
            let matches = scope
                .children
                .iter()
                .enumerate()
                .filter(|(_, c)| &c.target_field == name)
                .collect::<Vec<_>>();
            if matches.len() != 1 {
                found = false;
                break;
            }
            let (index, child) = matches[0];
            let Ok(index) = u32::try_from(index) else {
                found = false;
                break;
            };
            children.push(ferrule_edit_ops::Index(index));
            scope = child;
        }
        if found && field.data_type.kind == "scalar" {
            result.push(EditTarget {
                field_id: field.id.clone(),
                node: scope
                    .bindings
                    .iter()
                    .find(|b| &b.target_field == name)
                    .map(|b| b.node.to_string()),
                binding: ferrule_edit_ops::BindingRef {
                    scope: ferrule_edit_ops::ScopeRef { target, children },
                    field: name.clone(),
                },
            });
        }
    }
    result
}

pub fn parse_script(bytes: &[u8]) -> Result<EditScript, EditFailure> {
    if bytes.is_empty() || bytes.len() > MAX_OPERATIONS_BYTES {
        return Err(EditFailure::new(
            "operationsLimitExceeded",
            "The operation log exceeds its byte bound.",
        ));
    }
    let script: EditScript = serde_json::from_slice(bytes)
        .map_err(|error| EditFailure::new("invalidOperations", &error.to_string()))?;
    if script.operations.len() > MAX_OPERATIONS {
        return Err(EditFailure::new(
            "operationsLimitExceeded",
            "The operation log exceeds 2,000 operations.",
        ));
    }
    canonical_script(&script)?;
    Ok(script)
}

pub fn canonical_script(script: &EditScript) -> Result<Vec<u8>, EditFailure> {
    if script.operations.len() > MAX_OPERATIONS {
        return Err(EditFailure::new(
            "operationsLimitExceeded",
            "The operation log exceeds 2,000 operations.",
        ));
    }
    framing_limit::encode(script, MAX_OPERATIONS_BYTES).map_err(|_| {
        EditFailure::new(
            "operationsLimitExceeded",
            "The canonical operation log exceeds 1 MiB.",
        )
    })
}

impl Session {
    pub fn open(native: &[u8], operations: &[u8]) -> Result<Self, EditFailure> {
        if native.is_empty() || native.len() > protocol::MAX_NATIVE_BYTES {
            return Err(EditFailure::new(
                "nativeLimitExceeded",
                "The native document exceeds 4 MiB or is empty.",
            ));
        }
        let script = parse_script(operations)?;
        let decoded = decode::decode_native(native)
            .map_err(|error| EditFailure::new("nativeDecodeFailed", &error.to_string()))?;
        // The same structural bound applies before and after replay. This avoids
        // accepting a base the authoritative projection could not explain.
        inspect::inspect(
            &decoded.project,
            "entry.native.json",
            &decoded.document_prefix,
            &limits(),
        )
        .map_err(|message| EditFailure::new("resultLimitExceeded", message))?;
        let mut session = Self {
            inner: EditSession::new(decoded.project),
        };
        for (index, operation) in script.operations.into_iter().enumerate() {
            session
                .inner
                .apply(operation)
                .map_err(|error| EditFailure::operation(index, error))?;
        }
        Ok(session)
    }

    pub fn apply(&mut self, operation: EditOperation) -> Result<bool, EditFailure> {
        let mut operations = self.inner.operations().to_vec();
        operations.push(operation.clone());
        canonical_script(&EditScript {
            format: Format::V1,
            operations,
        })?;
        self.inner
            .apply(operation)
            .map_err(|error| EditFailure::new("operationRejected", &error.to_string()))
    }

    pub fn undo(&mut self) -> Result<bool, EditFailure> {
        self.inner
            .undo()
            .map_err(|error| EditFailure::new("historyRejected", &error.to_string()))
    }

    pub fn redo(&mut self) -> Result<bool, EditFailure> {
        self.inner
            .redo()
            .map_err(|error| EditFailure::new("historyRejected", &error.to_string()))
    }

    pub fn view(&self, document_id: &str) -> Result<View, EditFailure> {
        let project = self.inner.project();
        // encode_pretty selects Ferrule's envelope, independently of the input.
        // Locators must refer to that result, not the legacy base representation.
        let native = ferrule_mapping::project_file::encode_pretty(project)
            .map_err(|error| EditFailure::new("nativeEncodeFailed", &error.to_string()))?;
        self.view_at(document_id, decode::document_prefix(native.as_bytes()))
    }

    fn view_at(&self, document_id: &str, prefix: &str) -> Result<View, EditFailure> {
        let project = self.inner.project();
        inspect::inspect(project, document_id, prefix, &limits())
            .map_err(|message| EditFailure::new("resultLimitExceeded", message))?;
        let validation = validation::validate(project, document_id, prefix, &limits())
            .map_err(|message| EditFailure::new("resultLimitExceeded", message))?;
        let projection = projection::project(project, document_id, prefix, &limits())
            .map_err(|message| EditFailure::new("resultLimitExceeded", message))?;
        let view = View {
            targets: edit_targets(project, &projection),
            next_node_id: project
                .graph
                .nodes
                .keys()
                .copied()
                .max()
                .map_or(Some(0), |id| id.checked_add(1))
                .map(|id| id.to_string()),
            operations: EditScript {
                format: Format::V1,
                operations: self.inner.operations().to_vec(),
            },
            validation,
            projection,
            can_undo: self.inner.can_undo(),
            can_redo: self.inner.can_redo(),
            undo_label: self.inner.undo_label().map(str::to_owned),
            redo_label: self.inner.redo_label().map(str::to_owned),
        };
        framing_limit::encode(&view, MAX_REPORT_BYTES).map_err(|_| {
            EditFailure::new(
                "resultLimitExceeded",
                "The edit view exceeds its report byte bound.",
            )
        })?;
        Ok(view)
    }

    pub fn checkpoint(&self, document_id: &str) -> Result<Checkpoint, EditFailure> {
        let native = ferrule_mapping::project_file::encode_pretty(self.inner.project())
            .map_err(|error| EditFailure::new("nativeEncodeFailed", &error.to_string()))?
            .into_bytes();
        if native.len() > protocol::MAX_NATIVE_BYTES {
            return Err(EditFailure::new(
                "nativeLimitExceeded",
                "The encoded checkpoint exceeds 4 MiB.",
            ));
        }
        let native_digest = format!("{:x}", Sha256::digest(&native));
        Ok(Checkpoint {
            view: self.view_at(document_id, decode::document_prefix(&native))?,
            native,
            native_digest,
        })
    }
}

pub fn limits() -> protocol::Limits {
    protocol::Limits {
        maximum_diagnostics: protocol::MAX_DIAGNOSTICS,
        maximum_items: protocol::MAX_ITEMS,
        maximum_text_bytes: protocol::MAX_TEXT_BYTES,
    }
}
