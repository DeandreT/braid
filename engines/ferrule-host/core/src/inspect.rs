use ferrule_ir::{SchemaKind, SchemaNode};
use ferrule_mapping::{FormatOptions, Graph, Node, Project, Scope};

use crate::protocol::{
    BoundaryInventory, CapabilityCondition, InspectResult, Limits, MetadataCounts, NativeLocator,
    OperationCapability, ResourceReference, bounded_locator, bounded_text,
};

const MAX_STRUCTURAL_DEPTH: usize = 32;

pub fn inspect(
    project: &Project,
    document_id: &str,
    prefix: &str,
    limits: &Limits,
) -> Result<InspectResult, &'static str> {
    let mut budget = Budget {
        counts: MetadataCounts::default(),
        maximum: limits.maximum_items,
    };
    budget.graph(&project.graph)?;
    budget.scopes(&project.root)?;
    for target in &project.extra_targets {
        budget.scopes(&target.root)?;
    }
    budget.counts.failure_rules = project
        .failure_rules
        .len()
        .try_into()
        .map_err(|_| "Too many failure rules.")?;
    budget.charge(project.failure_rules.len())?;
    budget.counts.user_functions = project
        .user_functions
        .len()
        .try_into()
        .map_err(|_| "Too many user functions.")?;
    budget.charge(project.user_functions.len())?;
    for function in project.user_functions.values() {
        budget.charge(function.parameters.len())?;
        budget.graph(&function.body)?;
    }

    let mut boundaries = Vec::new();
    let mut unresolved_resources = Vec::new();
    let mut add_boundary = |id: String,
                            role: &str,
                            name: &str,
                            schema: &SchemaNode,
                            options: &FormatOptions,
                            pointer: String,
                            options_pointer: String,
                            path: Option<&str>,
                            path_pointer: String,
                            dynamic: bool|
     -> Result<(), &'static str> {
        budget.charge(1)?;
        budget.schema(schema)?;
        let (name, name_truncated) = bounded_text(name, limits.maximum_text_bytes as usize);
        let (root_name, root_name_truncated) =
            bounded_text(&schema.name, limits.maximum_text_bytes as usize);
        boundaries.push(BoundaryInventory {
            id: id.clone(),
            role: role.into(),
            name,
            name_truncated,
            root_name,
            root_name_truncated,
            shape: match &schema.kind {
                SchemaKind::Scalar { .. } => "scalar",
                SchemaKind::ScalarUnion { .. } => "scalarUnion",
                SchemaKind::Group { .. } => "object",
            }
            .into(),
            repeating: schema.repeating,
            native_locator: bounded_locator(
                NativeLocator::JsonPointer {
                    document_id: document_id.into(),
                    pointer,
                },
                limits.maximum_text_bytes as usize,
            ),
        });
        let input = matches!(role, "primaryInput" | "namedInput");
        let mut resource = |kind: &str, pointer: String| -> Result<(), &'static str> {
            budget.charge(1)?;
            unresolved_resources.push(ResourceReference {
                boundary_id: id.clone(),
                kind: kind.into(),
                resolution: "unresolved".into(),
                native_locator: bounded_locator(
                    NativeLocator::JsonPointer {
                        document_id: document_id.into(),
                        pointer,
                    },
                    limits.maximum_text_bytes as usize,
                ),
            });
            Ok(())
        };
        if path.is_some() {
            resource(
                if input {
                    "designTimeInput"
                } else {
                    "designTimeOutput"
                },
                path_pointer,
            )?;
        }
        if dynamic {
            resource(
                if input {
                    "dynamicInput"
                } else {
                    "dynamicOutput"
                },
                if input {
                    format!(
                        "{prefix}/extra_sources/{}/dynamic_path",
                        id.trim_start_matches("named-input:")
                    )
                } else if role == "primaryOutput" {
                    format!("{prefix}/root/output_path")
                } else {
                    format!(
                        "{prefix}/extra_targets/{}/root/output_path",
                        id.trim_start_matches("named-output:")
                    )
                },
            )?;
        }
        if options.edi_config_reference.is_some() {
            resource(
                "ediConfiguration",
                format!("{options_pointer}/edi_config_reference"),
            )?;
        }
        if options.external_source.is_some() {
            resource(
                "externalSource",
                format!("{options_pointer}/external_source"),
            )?;
        }
        if options.xml_schema_hints.is_some() {
            resource(
                "xmlSchemaHint",
                format!("{options_pointer}/xml_schema_hints"),
            )?;
        }
        // References remain metadata. No path, URL, header, or credential is returned.
        if options.json_schema_unresolved_reference.is_some() {
            resource(
                "schemaReference",
                format!("{options_pointer}/json_schema_unresolved_reference"),
            )?;
        }
        Ok(())
    };
    add_boundary(
        "primary-input".into(),
        "primaryInput",
        &project.source.name,
        &project.source,
        &project.source_options,
        format!("{prefix}/source"),
        format!("{prefix}/source_options"),
        project.source_path.as_deref(),
        format!("{prefix}/source_path"),
        false,
    )?;
    for (index, source) in project.extra_sources.iter().enumerate() {
        add_boundary(
            format!("named-input:{index}"),
            "namedInput",
            &source.name,
            &source.schema,
            &source.options,
            format!("{prefix}/extra_sources/{index}"),
            format!("{prefix}/extra_sources/{index}/options"),
            Some(&source.path),
            format!("{prefix}/extra_sources/{index}/path"),
            source.dynamic_path.is_some(),
        )?;
    }
    add_boundary(
        "primary-output".into(),
        "primaryOutput",
        &project.target.name,
        &project.target,
        &project.target_options,
        format!("{prefix}/target"),
        format!("{prefix}/target_options"),
        project.target_path.as_deref(),
        format!("{prefix}/target_path"),
        project.root.output_path().is_some(),
    )?;
    for (index, target) in project.extra_targets.iter().enumerate() {
        add_boundary(
            format!("named-output:{index}"),
            "namedOutput",
            &target.name,
            &target.schema,
            &target.options,
            format!("{prefix}/extra_targets/{index}"),
            format!("{prefix}/extra_targets/{index}/options"),
            target.path.as_deref(),
            format!("{prefix}/extra_targets/{index}/path"),
            target.root.output_path().is_some(),
        )?;
    }
    let truncated = boundaries
        .iter()
        .any(|boundary| boundary.name_truncated || boundary.root_name_truncated);
    let outputs = boundaries
        .iter()
        .filter(|boundary| matches!(boundary.role.as_str(), "primaryOutput" | "namedOutput"))
        .map(|boundary| boundary.id.clone())
        .collect::<Vec<_>>();
    Ok(InspectResult {
        boundaries,
        counts: budget.counts,
        unresolved_resources,
        capabilities: capabilities(outputs),
        truncated,
    })
}

pub(crate) fn capabilities(outputs: Vec<String>) -> Vec<OperationCapability> {
    [
        ("inspect", "supported", "ferrule.native-json.inspect.v1"),
        ("validate", "supported", "ferrule.native-json.validate.v1"),
        ("project", "supported", "ferrule.projection.v1"),
        ("preview", "notQualified", "ferrule.interpreter.v1"),
        ("build", "notQualified", "ferrule.generated.v1"),
    ]
    .into_iter()
    .map(|(operation, status, profile)| OperationCapability {
        operation: operation.into(),
        status: status.into(),
        profile: profile.into(),
        boundary_ids: if matches!(operation, "preview" | "build") {
            outputs.clone()
        } else {
            Vec::new()
        },
        conditions: vec![if operation == "project" {
            CapabilityCondition {
                code: "boundedReadOnlyProjection".into(),
                description: "Bounded read-only view; coverage may be partial or unavailable."
                    .into(),
            }
        } else if status == "supported" {
            CapabilityCondition {
                code: "readOnlyInspection".into(),
                description: "Metadata only; resource access and execution are unavailable.".into(),
            }
        } else {
            CapabilityCondition {
                code: "operationNotImplemented".into(),
                description: "This worker does not implement this operation.".into(),
            }
        }],
    })
    .collect()
}

struct Budget {
    counts: MetadataCounts,
    maximum: u32,
}

impl Budget {
    fn charge(&mut self, count: usize) -> Result<(), &'static str> {
        let count: u32 = count
            .try_into()
            .map_err(|_| "The native model exceeds the inspection item limit.")?;
        let total = self
            .counts
            .total_items
            .checked_add(count)
            .ok_or("Inspection item count overflowed.")?;
        if total > self.maximum {
            return Err("The native model exceeds the inspection item limit.");
        }
        self.counts.total_items = total;
        Ok(())
    }

    fn graph(&mut self, graph: &Graph) -> Result<(), &'static str> {
        self.charge(graph.nodes.len())?;
        self.counts.graph_nodes += graph.nodes.len() as u32;
        for node in graph.nodes.values() {
            self.charge(node.dependencies().len())?;
            match node {
                Node::ValueMap { table, .. } => self.charge(table.len().saturating_mul(2))?,
                Node::XmlSerialize { schema, .. } => self.schema(schema)?,
                Node::OwnedXmlSerialize(serialize) => self.schema(&serialize.schema)?,
                _ => {}
            }
        }
        Ok(())
    }

    fn schema(&mut self, root: &SchemaNode) -> Result<(), &'static str> {
        let mut pending = vec![(root, 0usize)];
        while let Some((schema, depth)) = pending.pop() {
            if depth > MAX_STRUCTURAL_DEPTH {
                return Err("The schema exceeds the inspection depth limit.");
            }
            self.charge(1)?;
            self.counts.schema_nodes += 1;
            if let SchemaKind::Group {
                children,
                alternatives,
                required,
                dynamic,
                xml_restricted_alternatives,
            } = &schema.kind
            {
                self.charge(
                    alternatives.len() + required.len() + xml_restricted_alternatives.len(),
                )?;
                for alternative in alternatives {
                    self.charge(
                        alternative.members.len()
                            + alternative.required.len()
                            + alternative.constraints.len(),
                    )?;
                }
                pending.extend(children.iter().map(|child| (child, depth + 1)));
                pending.extend(dynamic.iter().map(|child| (child.as_ref(), depth + 1)));
            }
            if let Some(contains) = &schema.json_contains {
                self.charge(contains.as_slice().len())?;
                pending.extend(
                    contains
                        .as_slice()
                        .iter()
                        .filter_map(|term| term.predicate().as_schema())
                        .map(|child| (child, depth + 1)),
                );
            }
            if let Some(dependent) = &schema.json_dependent_schemas {
                self.charge(dependent.as_slice().len())?;
                pending.extend(
                    dependent
                        .as_slice()
                        .iter()
                        .filter_map(|term| term.predicate().as_schema())
                        .map(|child| (child, depth + 1)),
                );
            }
        }
        Ok(())
    }

    fn scopes(&mut self, root: &Scope) -> Result<(), &'static str> {
        let mut pending = vec![(root, 0usize)];
        while let Some((scope, depth)) = pending.pop() {
            if depth > MAX_STRUCTURAL_DEPTH {
                return Err("The scope exceeds the inspection depth limit.");
            }
            self.charge(1)?;
            self.counts.scope_nodes += 1;
            self.charge(
                scope.bindings.len()
                    + scope.dynamic_bindings.len()
                    + scope.dynamic_children.len()
                    + scope.windows.len()
                    + scope.sort_then_by.len(),
            )?;
            pending.extend(scope.children.iter().map(|child| (child, depth + 1)));
            pending.extend(
                scope
                    .dynamic_children
                    .iter()
                    .map(|child| (&child.scope, depth + 1)),
            );
            if let Some(segments) = scope.concatenated() {
                pending.extend(segments.iter().map(|child| (child, depth + 1)));
            }
        }
        Ok(())
    }
}
