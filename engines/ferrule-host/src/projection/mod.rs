mod budget;
mod expressions;
mod schemas;
mod scopes;
pub mod types;

use std::collections::{BTreeMap, BTreeSet};

use ferrule_mapping::{FunctionId, Project};

use crate::protocol::{Limits, NativeLocator};
use budget::Budget;
use schemas::{FieldInfo, Inventory};
use types::*;

pub use types::ProjectionPayload;

pub fn project(
    project: &Project,
    document_id: &str,
    document_prefix: &str,
    limits: &Limits,
) -> Result<ProjectionPayload, &'static str> {
    let mut budget = Budget::new(limits);
    let inventory = schemas::inventory(project, document_id, document_prefix, &mut budget)?;
    let outputs = inventory
        .boundaries
        .iter()
        .filter(|boundary| matches!(boundary.role.as_str(), "primaryOutput" | "namedOutput"))
        .map(|boundary| boundary.id.clone())
        .collect::<Vec<_>>();
    let mut builder = Builder::new(project, document_id, document_prefix, budget, inventory);
    builder.functions()?;
    builder.targets()?;
    builder.failures()?;
    builder.ordering(&outputs)?;
    builder.normalize_gap_links();
    builder.relationships()?;
    let capabilities = crate::inspect::capabilities(outputs);
    for capability in &capabilities {
        builder
            .budget
            .charge(1 + capability.boundary_ids.len() + capability.conditions.len())?;
    }
    builder.payload.capabilities = capabilities;
    if !builder.payload.coverage.gaps.is_empty() {
        builder.payload.coverage.status = "partial".into();
    }
    Ok(builder.payload)
}

#[derive(Clone, Default)]
pub(crate) struct Context {
    pub frames: Vec<Frame>,
    pub generated: Option<String>,
    pub joins: BTreeMap<u64, JoinSources>,
    pub join_ownership_unknown: bool,
    pub in_join: bool,
    pub join_controls: BTreeMap<u64, String>,
    pub function: Option<FunctionId>,
    pub private_parameters: BTreeMap<u64, String>,
    pub documents: Vec<String>,
    pub grouped: bool,
    pub use_id: String,
    pub projection_ref: Option<NodeReference>,
}

pub(crate) type JoinSources = BTreeMap<Vec<String>, Vec<(String, Vec<String>)>>;

#[derive(Clone)]
pub(crate) struct Frame {
    pub boundary: String,
    /// Absolute schema value-context path; this is not a native selector.
    pub path: Vec<String>,
    pub collection: Vec<String>,
    pub position: bool,
    pub value: bool,
    pub control: String,
}

pub(crate) struct Builder<'a> {
    pub project: &'a Project,
    pub document_id: &'a str,
    pub prefix: &'a str,
    pub budget: Budget,
    pub payload: ProjectionPayload,
    pub catalog: Vec<FieldInfo<'a>>,
    pub function_controls: BTreeMap<FunctionId, String>,
    pub parameter_ids: BTreeMap<String, String>,
}

impl<'a> Builder<'a> {
    fn new(
        project: &'a Project,
        document_id: &'a str,
        prefix: &'a str,
        budget: Budget,
        inventory: Inventory<'a>,
    ) -> Self {
        let diagnostics = inventory
            .gaps
            .iter()
            .map(|gap| Diagnostic {
                code: gap.code.clone(),
                severity: "warning".into(),
                message: gap.message.clone(),
                native_locator: Some(gap.native_locator.clone()),
                projection_locator: gap.projection_locator.clone(),
            })
            .collect();
        Self {
            project,
            document_id,
            prefix,
            budget,
            catalog: inventory.catalog,
            function_controls: BTreeMap::new(),
            parameter_ids: BTreeMap::new(),
            payload: ProjectionPayload {
                coverage: Coverage {
                    status: "complete".into(),
                    gaps: inventory.gaps,
                },
                boundaries: inventory.boundaries,
                fields: inventory.fields,
                parameters: Vec::new(),
                rules: Vec::new(),
                controls: Vec::new(),
                relationships: Vec::new(),
                diagnostics,
                capabilities: Vec::new(),
            },
        }
    }

    pub fn pointer(&self, suffix: &str) -> Result<NativeLocator, &'static str> {
        self.budget
            .pointer(self.document_id, format!("{}{suffix}", self.prefix))
    }

    pub fn gap(
        &mut self,
        code: &str,
        message: &str,
        locator: NativeLocator,
        reference: Option<NodeReference>,
    ) -> Result<(), &'static str> {
        let gap = self.budget.gap(code, message, locator, reference)?;
        self.payload.diagnostics.push(Diagnostic {
            code: gap.code.clone(),
            severity: "warning".into(),
            message: gap.message.clone(),
            native_locator: Some(gap.native_locator.clone()),
            projection_locator: gap.projection_locator.clone(),
        });
        self.payload.coverage.gaps.push(gap);
        Ok(())
    }

    pub fn add_control(&mut self, control: Control) -> Result<(), &'static str> {
        if self
            .payload
            .controls
            .iter()
            .any(|existing| existing.id == control.id)
        {
            return Ok(());
        }
        self.budget.charge(2 + control.applies_to.len())?;
        self.dependencies_budget(&control.dependencies)?;
        for detail in &control.details {
            self.budget.detail(detail, 0)?;
        }
        self.budget.text(&control.summary)?;
        self.payload.controls.push(control);
        Ok(())
    }

    pub fn add_rule(&mut self, rule: Rule) -> Result<(), &'static str> {
        self.budget.charge(2)?;
        self.dependencies_budget(&rule.dependencies)?;
        for detail in &rule.details {
            self.budget.detail(detail, 0)?;
        }
        self.budget.text(&rule.summary)?;
        self.payload.rules.push(rule);
        Ok(())
    }

    fn dependencies_budget(&mut self, dependencies: &[Dependency]) -> Result<(), &'static str> {
        for dependency in dependencies {
            self.budget.charge(1)?;
            match dependency {
                Dependency::Constant { value, .. } => self.budget.literal(value)?,
                Dependency::Node { .. } => self.budget.charge(1)?,
                _ => {}
            }
        }
        Ok(())
    }

    pub fn field_id(&self, boundary: &str, path: &[String]) -> Option<String> {
        let mut fields = self
            .catalog
            .iter()
            .filter(|field| field.boundary_id == boundary && field.path == path);
        let first = fields.next()?;
        if fields.next().is_some() {
            None
        } else {
            Some(first.id.clone())
        }
    }

    fn relationships(&mut self) -> Result<(), &'static str> {
        let mut edges = Vec::new();
        for field in &self.payload.fields {
            edges.push((
                "boundaryMember",
                NodeReference::Field {
                    field_id: field.id.clone(),
                },
                NodeReference::Boundary {
                    boundary_id: field.boundary_id.clone(),
                },
            ));
        }
        for rule in &self.payload.rules {
            let reference = NodeReference::Rule {
                rule_id: rule.id.clone(),
            };
            edges.push((
                "writesTo",
                reference.clone(),
                NodeReference::Field {
                    field_id: rule.output_field_id.clone(),
                },
            ));
            dependency_edges(&reference, &rule.dependencies, &mut edges);
        }
        for control in &self.payload.controls {
            let reference = NodeReference::Control {
                control_id: control.id.clone(),
            };
            for target in &control.applies_to {
                edges.push(("governedBy", target.clone(), reference.clone()));
            }
            dependency_edges(&reference, &control.dependencies, &mut edges);
        }
        let mut seen = BTreeSet::new();
        for (kind, from, to) in edges {
            let from_key = reference_key(&from);
            let to_key = reference_key(&to);
            let id = self
                .budget
                .id("relationship", &[kind, &from_key, &to_key])?;
            if seen.insert(id.clone()) {
                self.budget.charge(3)?;
                self.payload.relationships.push(Relationship {
                    id,
                    kind: kind.into(),
                    from,
                    to,
                });
            }
        }
        Ok(())
    }

    fn normalize_gap_links(&mut self) {
        let mut origins = BTreeMap::<String, BTreeSet<String>>::new();
        for parameter in &self.payload.parameters {
            if matches!(&parameter.native_locator, NativeLocator::GraphNode { .. }) {
                origins
                    .entry(graph_locator_key(&parameter.native_locator))
                    .or_default()
                    .insert(reference_key(&NodeReference::Parameter {
                        parameter_id: parameter.id.clone(),
                    }));
            }
        }
        for rule in &self.payload.rules {
            let owner = reference_key(&NodeReference::Rule {
                rule_id: rule.id.clone(),
            });
            if matches!(&rule.native_locator, NativeLocator::GraphNode { .. }) {
                origins
                    .entry(graph_locator_key(&rule.native_locator))
                    .or_default()
                    .insert(owner.clone());
            }
            graph_detail_origins(&rule.details, &owner, &mut origins);
        }
        for control in &self.payload.controls {
            let owner = reference_key(&NodeReference::Control {
                control_id: control.id.clone(),
            });
            if matches!(&control.native_locator, NativeLocator::GraphNode { .. }) {
                origins
                    .entry(graph_locator_key(&control.native_locator))
                    .or_default()
                    .insert(owner.clone());
            }
            graph_detail_origins(&control.details, &owner, &mut origins);
        }
        let admitted = |locator: &NativeLocator, owner: &Option<NodeReference>| {
            if let NativeLocator::GraphNode { .. } = locator {
                owner.as_ref().is_some_and(|owner| {
                    origins
                        .get(&graph_locator_key(locator))
                        .is_some_and(|owners| {
                            owners.len() == 1 && owners.contains(&reference_key(owner))
                        })
                })
            } else {
                true
            }
        };
        for gap in &mut self.payload.coverage.gaps {
            if !admitted(&gap.native_locator, &gap.projection_locator) {
                gap.projection_locator = None;
            }
        }
        for diagnostic in &mut self.payload.diagnostics {
            if diagnostic
                .native_locator
                .as_ref()
                .is_some_and(|locator| !admitted(locator, &diagnostic.projection_locator))
            {
                diagnostic.projection_locator = None;
            }
        }
    }
}

fn graph_detail_origins(
    details: &[Detail],
    owner: &str,
    origins: &mut BTreeMap<String, BTreeSet<String>>,
) {
    for detail in details {
        match detail {
            Detail::Group { children, .. } => graph_detail_origins(children, owner, origins),
            Detail::Expression { arguments, .. } => graph_detail_origins(arguments, owner, origins),
            Detail::Unsupported {
                native_locator: locator @ NativeLocator::GraphNode { .. },
                ..
            } => {
                origins
                    .entry(graph_locator_key(locator))
                    .or_default()
                    .insert(owner.into());
            }
            _ => {}
        }
    }
}

fn graph_locator_key(locator: &NativeLocator) -> String {
    match locator {
        NativeLocator::GraphNode {
            document_id,
            node_id,
            function_id,
        } => format!(
            "{}:{document_id}:{}:{}:{node_id}",
            document_id.len(),
            function_id.as_deref().unwrap_or_default().len(),
            function_id.as_deref().unwrap_or_default()
        ),
        _ => unreachable!(),
    }
}

fn dependency_edges(
    from: &NodeReference,
    dependencies: &[Dependency],
    edges: &mut Vec<(&'static str, NodeReference, NodeReference)>,
) {
    for dependency in dependencies {
        match dependency {
            Dependency::Field { field_id, .. } => edges.push((
                "readsFrom",
                from.clone(),
                NodeReference::Field {
                    field_id: field_id.clone(),
                },
            )),
            Dependency::Parameter { parameter_id, .. } => edges.push((
                "dependsOn",
                from.clone(),
                NodeReference::Parameter {
                    parameter_id: parameter_id.clone(),
                },
            )),
            Dependency::Node { node, .. } => edges.push(("dependsOn", from.clone(), node.clone())),
            Dependency::Constant { .. } => {}
        }
    }
}

pub(crate) fn reference_key(reference: &NodeReference) -> String {
    match reference {
        NodeReference::Boundary { boundary_id } => format!("boundary:{boundary_id}"),
        NodeReference::Field { field_id } => format!("field:{field_id}"),
        NodeReference::Parameter { parameter_id } => format!("parameter:{parameter_id}"),
        NodeReference::Rule { rule_id } => format!("rule:{rule_id}"),
        NodeReference::Control { control_id } => format!("control:{control_id}"),
    }
}
