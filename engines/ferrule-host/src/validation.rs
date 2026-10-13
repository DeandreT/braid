use ferrule_engine::{ValidationEndpoint as Endpoint, ValidationOwner as Owner};
use ferrule_engine::{ValidationSchemaStep, ValidationScopeStep};
use ferrule_mapping::Project;

use crate::protocol::{
    Limits, NativeLocator, SchemaStep, ScopeStep, ValidationEndpoint, ValidationIssue,
    ValidationOwner, ValidationResult, bounded_locator, bounded_text,
};

pub const MAX_VALIDATION_ISSUES: usize = 32768;

pub fn validate(
    project: &Project,
    document_id: &str,
    document_prefix: &str,
    limits: &Limits,
) -> Result<ValidationResult, &'static str> {
    let issues = ferrule_engine::validate(project);
    if issues.len() > MAX_VALIDATION_ISSUES {
        return Err("The validation result exceeds the compiled issue-count limit.");
    }
    // Endpoint names are identities; truncation would change their meaning.
    // Admit every owner before converting the bounded diagnostic subset.
    if issues
        .iter()
        .filter_map(|issue| issue.owner.as_ref())
        .any(|owner| !owner_name_is_admitted(owner, limits.maximum_text_bytes as usize))
    {
        return Err("A native owner identity exceeds its admitted text limit.");
    }
    let total_issue_count = issues.len() as u32;
    let emitted_issue_count = issues.len().min(limits.maximum_diagnostics as usize) as u32;
    let mut truncated = emitted_issue_count < total_issue_count;
    let issues = issues
        .into_iter()
        .take(emitted_issue_count as usize)
        .map(|issue| {
            let (location, location_truncated) =
                bounded_text(&issue.location, limits.maximum_text_bytes as usize);
            let (message, message_truncated) =
                bounded_text(&issue.message, limits.maximum_text_bytes as usize);
            truncated |= location_truncated || message_truncated;
            let native_locator = issue.owner.as_ref().map_or_else(
                || NativeLocator::Document {
                    document_id: document_id.into(),
                },
                |owner| owner_locator(owner, document_id, document_prefix),
            );
            ValidationIssue {
                code: "ferrule.validation".into(),
                location,
                location_truncated,
                message,
                message_truncated,
                owner: issue.owner.as_ref().map(owner_to_wire),
                native_locator: bounded_locator(native_locator, limits.maximum_text_bytes as usize),
            }
        })
        .collect();
    Ok(ValidationResult {
        is_valid: total_issue_count == 0,
        total_issue_count,
        emitted_issue_count,
        truncated,
        issues,
    })
}

fn owner_name_is_admitted(owner: &Owner, maximum_bytes: usize) -> bool {
    let endpoint = match owner {
        Owner::Endpoint(endpoint) => Some(endpoint),
        Owner::SchemaNode(location) => Some(&location.endpoint),
        Owner::Scope(location) => Some(&location.target),
        _ => None,
    };
    match endpoint {
        Some(Endpoint::NamedSource { name, .. } | Endpoint::NamedTarget { name, .. }) => {
            name.len() <= maximum_bytes
        }
        _ => true,
    }
}

pub fn owner_to_wire(owner: &Owner) -> ValidationOwner {
    match owner {
        Owner::GraphNode { function, node } => ValidationOwner::GraphNode {
            function_id: function.map(|function| function.get().to_string()),
            node_id: node.to_string(),
        },
        Owner::UserFunction(function) => ValidationOwner::UserFunction {
            function_id: function.get().to_string(),
        },
        Owner::Endpoint(endpoint) => ValidationOwner::Endpoint {
            endpoint: endpoint_to_wire(endpoint),
        },
        Owner::SchemaNode(location) => ValidationOwner::SchemaNode {
            endpoint: endpoint_to_wire(&location.endpoint),
            path: location
                .path
                .iter()
                .map(|step| match step {
                    ValidationSchemaStep::Child(index) => SchemaStep::Child {
                        index: index.to_string(),
                    },
                    ValidationSchemaStep::DynamicField => SchemaStep::DynamicField,
                    ValidationSchemaStep::Contains(index) => SchemaStep::Contains {
                        index: index.to_string(),
                    },
                    ValidationSchemaStep::DependentSchema(index) => SchemaStep::DependentSchema {
                        index: index.to_string(),
                    },
                })
                .collect(),
        },
        Owner::Scope(location) => ValidationOwner::Scope {
            target: endpoint_to_wire(&location.target),
            path: location
                .path
                .iter()
                .map(|step| match step {
                    ValidationScopeStep::Child(index) => ScopeStep::Child {
                        index: index.to_string(),
                    },
                    ValidationScopeStep::DynamicChild(index) => ScopeStep::DynamicChild {
                        index: index.to_string(),
                    },
                    ValidationScopeStep::Segment(index) => ScopeStep::Segment {
                        index: index.to_string(),
                    },
                })
                .collect(),
        },
        Owner::FailureRule { index } => ValidationOwner::FailureRule {
            index: index.to_string(),
        },
    }
}

fn endpoint_to_wire(endpoint: &Endpoint) -> ValidationEndpoint {
    match endpoint {
        Endpoint::Source => ValidationEndpoint::Source,
        Endpoint::Target => ValidationEndpoint::Target,
        Endpoint::NamedSource { index, name } => ValidationEndpoint::NamedSource {
            index: index.to_string(),
            name: name.clone(),
        },
        Endpoint::NamedTarget { index, name } => ValidationEndpoint::NamedTarget {
            index: index.to_string(),
            name: name.clone(),
        },
    }
}

pub fn owner_locator(owner: &Owner, document_id: &str, prefix: &str) -> NativeLocator {
    let pointer = match owner {
        Owner::GraphNode { function, node } => {
            return NativeLocator::GraphNode {
                document_id: document_id.into(),
                node_id: node.to_string(),
                function_id: function.map(|function| function.get().to_string()),
            };
        }
        Owner::UserFunction(function) => format!("{prefix}/user_functions/{}", function.get()),
        Owner::Endpoint(endpoint) => format!("{prefix}{}", endpoint_pointer(endpoint)),
        Owner::FailureRule { index } => format!("{prefix}/failure_rules/{index}"),
        Owner::Scope(location) => {
            let mut pointer = match &location.target {
                Endpoint::Target => format!("{prefix}/root"),
                Endpoint::NamedTarget { index, .. } => {
                    format!("{prefix}/extra_targets/{index}/root")
                }
                _ => {
                    return NativeLocator::Document {
                        document_id: document_id.into(),
                    };
                }
            };
            for step in &location.path {
                pointer.push_str(&match step {
                    ValidationScopeStep::Child(index) => format!("/children/{index}"),
                    ValidationScopeStep::DynamicChild(index) => {
                        format!("/dynamic_children/{index}/scope")
                    }
                    ValidationScopeStep::Segment(index) => format!("/segments/{index}"),
                });
            }
            pointer
        }
        Owner::SchemaNode(location) => {
            let mut pointer = format!("{prefix}{}", endpoint_pointer(&location.endpoint));
            if matches!(
                &location.endpoint,
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
                    // Predicate clauses have an independent canonical wire model.
                    // Retain their exact typed owner until that pointer is qualified.
                    ValidationSchemaStep::Contains(_)
                    | ValidationSchemaStep::DependentSchema(_) => {
                        return NativeLocator::Document {
                            document_id: document_id.into(),
                        };
                    }
                }
            }
            pointer
        }
    };
    NativeLocator::JsonPointer {
        document_id: document_id.into(),
        pointer,
    }
}

fn endpoint_pointer(endpoint: &Endpoint) -> String {
    match endpoint {
        Endpoint::Source => "/source".into(),
        Endpoint::Target => "/target".into(),
        Endpoint::NamedSource { index, .. } => format!("/extra_sources/{index}"),
        Endpoint::NamedTarget { index, .. } => format!("/extra_targets/{index}"),
    }
}
