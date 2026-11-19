use std::collections::BTreeSet;

use ferrule_ir::{ScalarType, Value};
use ferrule_mapping::{FunctionId, Node, NodeId, SourceOwner};

use super::budget::MAX_DEPTH;
use super::types::*;
use super::{Builder, Context, Frame};
use crate::protocol::NativeLocator;

#[derive(Clone)]
pub(crate) struct Expression {
    pub detail: Detail,
    pub dependencies: Vec<Dependency>,
    pub summary: String,
}

impl Builder<'_> {
    pub fn expression(
        &mut self,
        node: NodeId,
        context: &Context,
        role: &str,
    ) -> Result<Expression, &'static str> {
        self.expression_inner(node, context, role, 0, &mut BTreeSet::new())
    }

    fn expression_inner(
        &mut self,
        id: NodeId,
        context: &Context,
        role: &str,
        depth: usize,
        active: &mut BTreeSet<(Option<FunctionId>, NodeId)>,
    ) -> Result<Expression, &'static str> {
        if depth > MAX_DEPTH {
            return Err("The projection exceeds its expression-depth limit.");
        }
        self.budget.charge(1)?;
        let locator = NativeLocator::GraphNode {
            document_id: self.document_id.into(),
            node_id: id.to_string(),
            function_id: context.function.map(|id| id.get().to_string()),
        };
        if !active.insert((context.function, id)) {
            return self.unsupported(
                "Cyclic expression",
                "A cyclic graph has no finite expression explanation.",
                locator,
                context,
            );
        }
        let node = match context.function {
            Some(function) => self
                .project
                .user_functions
                .get(&function)
                .and_then(|function| function.body.nodes.get(&id)),
            None => self.project.graph.nodes.get(&id),
        }
        .cloned();
        let result = match node {
            None => self.unsupported(
                "Missing expression",
                "The expression refers to a missing graph node.",
                NativeLocator::Document {
                    document_id: self.document_id.into(),
                },
                context,
            )?,
            Some(_) if context.owned_join_closed => self.unsupported(
                "Closed owned join scope expression",
                "Ferrule refuses this owned join context before evaluation; the expression has no executable explanation.",
                locator.clone(),
                context,
            )?,
            Some(node) if context.owned_join_tuple && !owned_join_tuple_node(&node) => self
                .unsupported(
                    "Closed owned join tuple expression",
                    "An owned join tuple reads members only through owned join reads; this form is closed here.",
                    locator.clone(),
                    context,
                )?,
            Some(Node::Const { value }) => {
                let literal = self.literal(&value)?;
                Expression {
                    detail: Detail::Literal {
                        value: literal.clone(),
                    },
                    dependencies: vec![Dependency::Constant {
                        value: literal,
                        role: role.into(),
                    }],
                    summary: "Constant".into(),
                }
            }
            Some(Node::Unconnected) => Expression {
                detail: Detail::Literal {
                    value: Literal::Null {
                        null_kind: "absent".into(),
                    },
                },
                dependencies: vec![Dependency::Constant {
                    value: Literal::Null {
                        null_kind: "absent".into(),
                    },
                    role: role.into(),
                }],
                summary: "Unconnected input".into(),
            },
            Some(Node::SourceField { path, frame }) => {
                self.source_expression(&path, frame.as_deref(), context, role, locator.clone())?
            }
            Some(Node::SourceRootField { path, required }) => {
                for segment in &path {
                    self.budget.text(segment)?;
                }
                let candidates = self
                    .catalog
                    .iter()
                    .filter(|field| {
                        field.input && field.source_name.is_none() && field.path == path
                    })
                    .map(|field| field.id.clone())
                    .collect();
                let mut expression = self.fields_expression(
                    candidates,
                    role,
                    locator.clone(),
                    context,
                    "Immutable primary-root field",
                )?;
                expression.detail = Detail::Group {
                    label: if required {
                        "Required primary-root field"
                    } else {
                        "Primary-root field"
                    }
                    .into(),
                    children: vec![expression.detail],
                };
                expression.summary = "Primary-root field".into();
                expression
            }
            Some(Node::OwnedSourceField(native)) => {
                let owner = match &native.source.owner {
                    SourceOwner::Primary => None,
                    SourceOwner::Named { name } => Some(name.as_str()),
                };
                for segment in &native.source.path {
                    self.budget.text(segment)?;
                }
                if let Some(owner) = owner {
                    self.budget.text(owner)?;
                }
                let candidates = self
                    .catalog
                    .iter()
                    .filter(|field| {
                        field.input
                            && field.source_name.as_deref() == owner
                            && field.path == native.source.path
                    })
                    .map(|field| field.id.clone())
                    .collect::<Vec<_>>();
                self.fields_expression(
                    candidates,
                    role,
                    locator.clone(),
                    context,
                    "Explicitly owned source field",
                )?
            }
            Some(Node::If {
                condition,
                then,
                else_,
            }) => {
                let condition =
                    self.expression_inner(condition, context, "condition", depth + 1, active)?;
                let then = self.expression_inner(then, context, role, depth + 1, active)?;
                let otherwise = self.expression_inner(else_, context, role, depth + 1, active)?;
                let dependencies = merge_dependencies([
                    condition.dependencies,
                    then.dependencies,
                    otherwise.dependencies,
                ]);
                Expression {
                    detail: Detail::Expression {
                        operation: "If".into(),
                        arguments: vec![
                            group("Condition", condition.detail),
                            group("When true", then.detail),
                            group("When false", otherwise.detail),
                        ],
                    },
                    dependencies,
                    summary: "Conditional value".into(),
                }
            }
            Some(Node::Call { function, args }) => {
                self.budget.text(&function)?;
                let mut details = Vec::new();
                let mut dependencies = Vec::new();
                for arg in args {
                    let expression =
                        self.expression_inner(arg, context, role, depth + 1, active)?;
                    details.push(expression.detail);
                    extend_dependencies(&mut dependencies, expression.dependencies);
                }
                Expression {
                    detail: Detail::Expression {
                        operation: function.clone(),
                        arguments: details,
                    },
                    dependencies,
                    summary: self.budget.text(&format!("Call {function}"))?,
                }
            }
            Some(Node::RuntimeParameter { name, ty, preview }) => self.parameter_expression(
                id,
                &name,
                ty,
                None,
                preview.as_deref(),
                context,
                role,
                locator.clone(),
            )?,
            Some(Node::RuntimeParameterDefault {
                name,
                ty,
                default,
                preview,
            }) => {
                let fallback =
                    self.expression_inner(default, context, "default", depth + 1, active)?;
                let literal_default = match &fallback.detail {
                    Detail::Literal { value } => Some(value.clone()),
                    _ => None,
                };
                let mut parameter = self.parameter_expression(
                    id,
                    &name,
                    ty,
                    literal_default,
                    preview.as_deref(),
                    context,
                    role,
                    locator.clone(),
                )?;
                parameter.detail = Detail::Expression {
                    operation: "Parameter with default".into(),
                    arguments: vec![
                        parameter.detail,
                        group("When the host omits the name", fallback.detail),
                        text_detail(
                            "Supplied null remains supplied; it does not select this default.",
                        ),
                    ],
                };
                extend_dependencies(&mut parameter.dependencies, fallback.dependencies);
                parameter.summary = "Runtime parameter with default".into();
                parameter
            }
            Some(Node::RuntimeValue { value }) => Expression {
                detail: Detail::Expression {
                    operation: "Host runtime value".into(),
                    arguments: vec![text_detail(match value {
                        ferrule_mapping::RuntimeValue::MappingFilePath => "Active mapping path",
                        ferrule_mapping::RuntimeValue::MainMappingFilePath => "Main mapping path",
                        ferrule_mapping::RuntimeValue::CurrentDateTime => {
                            "Stable current date and time"
                        }
                    })],
                },
                dependencies: Vec::new(),
                summary: "Host runtime value".into(),
            },
            Some(Node::FunctionParameter { parameter }) => {
                if let Some(name) = context.private_parameters.get(&parameter.get()) {
                    Expression {
                        detail: Detail::Group {
                            label: self.budget.text(&format!("Private parameter {name}"))?,
                            children: vec![text_detail(&parameter.get().to_string())],
                        },
                        dependencies: Vec::new(),
                        summary: "Private function parameter".into(),
                    }
                } else {
                    self.unsupported(
                        "Unknown private parameter",
                        "This function parameter has no declared identity.",
                        locator.clone(),
                        context,
                    )?
                }
            }
            Some(Node::UserFunctionCall { function, args }) => {
                let control = self.function_controls.get(&function).cloned();
                if let Some(control) = control {
                    let parameters = self
                        .project
                        .user_functions
                        .get(&function)
                        .map(|function| function.parameters.clone())
                        .unwrap_or_default();
                    let mut details = vec![Detail::Reference {
                        reference: NodeReference::Control {
                            control_id: control.clone(),
                        },
                    }];
                    let mut dependencies = vec![Dependency::Node {
                        node: NodeReference::Control {
                            control_id: control,
                        },
                        role: role.into(),
                    }];
                    for (index, arg) in args.into_iter().enumerate() {
                        let expression =
                            self.expression_inner(arg, context, role, depth + 1, active)?;
                        let label = parameters
                            .get(index)
                            .map(|parameter| format!("Argument {}", parameter.name))
                            .unwrap_or_else(|| format!("Argument {}", index + 1));
                        self.budget.text(&label)?;
                        details.push(group(&label, expression.detail));
                        extend_dependencies(&mut dependencies, expression.dependencies);
                    }
                    Expression {
                        detail: Detail::Expression {
                            operation: "Reusable function call".into(),
                            arguments: details,
                        },
                        dependencies,
                        summary: "Call reusable function".into(),
                    }
                } else {
                    self.unsupported(
                        "Missing reusable function",
                        "The call refers to an undeclared reusable function.",
                        locator.clone(),
                        context,
                    )?
                }
            }
            Some(Node::ValueMap {
                input,
                input_type,
                table,
                default,
            }) => {
                let input =
                    self.expression_inner(input, context, "condition", depth + 1, active)?;
                let mut details = vec![group("Input", input.detail)];
                let mut dependencies = input.dependencies;
                if let Some(ty) = input_type {
                    details.push(group(
                        "Declared matching type",
                        text_detail(scalar_name(ty)),
                    ));
                }
                for (index, (key, value)) in table.iter().enumerate() {
                    self.budget.charge(2)?;
                    let key = self.literal(key)?;
                    let value = self.literal(value)?;
                    details.push(Detail::Group {
                        label: format!("Entry {} (first match wins)", index + 1),
                        children: vec![
                            group("When", Detail::Literal { value: key.clone() }),
                            group(
                                "Return",
                                Detail::Literal {
                                    value: value.clone(),
                                },
                            ),
                        ],
                    });
                    dependencies.push(Dependency::Constant {
                        value: key,
                        role: "condition".into(),
                    });
                    dependencies.push(Dependency::Constant {
                        value,
                        role: role.into(),
                    });
                }
                if let Some(default) = default {
                    let value = self.literal(&default)?;
                    details.push(group(
                        "Enabled fallback",
                        Detail::Literal {
                            value: value.clone(),
                        },
                    ));
                    dependencies.push(Dependency::Constant {
                        value,
                        role: "default".into(),
                    });
                } else {
                    details.push(text_detail("Fallback is disabled."));
                }
                Expression {
                    detail: Detail::Expression {
                        operation: "Value map".into(),
                        arguments: details,
                    },
                    dependencies,
                    summary: "Ordered value map".into(),
                }
            }
            Some(Node::Position { collection }) => {
                for segment in &collection {
                    self.budget.text(segment)?;
                }
                let frame = context.frames.iter().rev().find(|frame| {
                    frame.position
                        && (collection.is_empty() || frame.collection.ends_with(&collection))
                });
                let control = frame.map(|frame| frame.control.clone());
                if let Some(control_id) = control {
                    Expression {
                        detail: Detail::Expression {
                            operation: "One-based item position".into(),
                            arguments: vec![
                                Detail::Group {
                                    label: "Native source collection selector".into(),
                                    children: collection
                                        .iter()
                                        .map(|segment| text_detail(segment))
                                        .collect(),
                                },
                                Detail::Reference {
                                    reference: NodeReference::Control {
                                        control_id: control_id.clone(),
                                    },
                                },
                            ],
                        },
                        dependencies: vec![Dependency::Node {
                            node: NodeReference::Control { control_id },
                            role: role.into(),
                        }],
                        summary: "Item position".into(),
                    }
                } else {
                    Expression {
                        detail: text_detail(
                            "No native position frame matches this selector; native position defaults to one.",
                        ),
                        dependencies: Vec::new(),
                        summary: "Default item position".into(),
                    }
                }
            }
            Some(Node::JoinField {
                join,
                collection,
                path,
            }) => {
                if context.joins.contains_key(&join.get()) {
                    let mut candidates = Vec::new();
                    let collections = context
                        .joins
                        .get(&join.get())
                        .and_then(|sources| sources.get(&collection))
                        .cloned()
                        .unwrap_or_default();
                    for (boundary, prefix) in collections {
                        let full = prefix
                            .into_iter()
                            .chain(path.iter().cloned())
                            .collect::<Vec<_>>();
                        if let Some(id) = self.field_id(&boundary, &full) {
                            candidates.push(id);
                        }
                    }
                    self.fields_expression(
                        candidates,
                        role,
                        locator.clone(),
                        context,
                        "Joined field",
                    )?
                } else {
                    self.unsupported(
                        "Unresolved joined field",
                        "The owning join is not active at this expression.",
                        locator.clone(),
                        context,
                    )?
                }
            }
            Some(Node::OwnedJoinField(native)) => {
                for segment in &native.path {
                    self.budget.text(segment)?;
                }
                let member = context
                    .owned_joins
                    .get(&native.join.get())
                    .and_then(|members| {
                        members
                            .iter()
                            .find(|(member, _)| *member == native.collection)
                    })
                    .map(|(_, candidates)| candidates.clone());
                if let Some(collections) = member {
                    let mut candidates = Vec::new();
                    for (boundary, prefix) in collections {
                        let full = prefix
                            .into_iter()
                            .chain(native.path.iter().cloned())
                            .collect::<Vec<_>>();
                        if let Some(id) = self.field_id(&boundary, &full) {
                            candidates.push(id);
                        }
                    }
                    self.fields_expression(
                        candidates,
                        role,
                        locator.clone(),
                        context,
                        "Owned joined field",
                    )?
                } else {
                    self.unsupported(
                        "Unresolved owned joined field",
                        "The owning join or its member is not active at this expression.",
                        locator.clone(),
                        context,
                    )?
                }
            }
            Some(Node::OwnedJoinSourcePosition(native)) => {
                let active = context
                    .owned_joins
                    .get(&native.join.get())
                    .is_some_and(|members| {
                        members
                            .iter()
                            .any(|(member, _)| *member == native.collection)
                    });
                let control = context.join_controls.get(&native.join.get()).cloned();
                if let (true, Some(control_id)) = (active, control) {
                    Expression {
                        detail: text_detail(
                            "One-based raw source position of one owned join member",
                        ),
                        dependencies: vec![Dependency::Node {
                            node: NodeReference::Control { control_id },
                            role: role.into(),
                        }],
                        summary: "Owned join source position".into(),
                    }
                } else {
                    self.unsupported(
                        "Unresolved owned join source position",
                        "The owning join or its member is not active at this expression.",
                        locator.clone(),
                        context,
                    )?
                }
            }
            Some(Node::JoinPosition { join }) => {
                let control = context.join_controls.get(&join.get()).cloned();
                if let Some(control_id) = control {
                    Expression {
                        detail: text_detail("One-based flattened join position"),
                        dependencies: vec![Dependency::Node {
                            node: NodeReference::Control { control_id },
                            role: role.into(),
                        }],
                        summary: "Join position".into(),
                    }
                } else {
                    self.unsupported(
                        "Unresolved join position",
                        "The owning join is not active at this expression.",
                        locator.clone(),
                        context,
                    )?
                }
            }
            Some(Node::Aggregate {
                function,
                collection,
                value,
                expression,
                arg,
            }) => {
                let candidates = self.collection_candidates(&collection, context)?;
                let ambiguous = candidates.len() != 1;
                if ambiguous {
                    self.gap("projection.ambiguousAggregate", "The aggregate collection has no unique admitted source; references below are possible locations, not definite dependencies.", locator.clone(), context.projection_ref.clone())?;
                }
                let mut dependencies = Vec::new();
                let mut details = Vec::new();
                let mut item_context = context.clone();
                let control = self
                    .budget
                    .id("control", &[&context.use_id, "aggregate", &id.to_string()])?;
                let mut collection_dependencies = Vec::new();
                for (boundary, path) in &candidates {
                    if let Some(field_id) = self.field_id(boundary, path) {
                        if !ambiguous {
                            dependencies.push(Dependency::Field {
                                field_id: field_id.clone(),
                                role: role.into(),
                            });
                        }
                        if !ambiguous {
                            collection_dependencies.push(Dependency::Field {
                                field_id: field_id.clone(),
                                role: "value".into(),
                            });
                        }
                        details.push(group(
                            "Collection",
                            Detail::Reference {
                                reference: NodeReference::Field { field_id },
                            },
                        ));
                    }
                    item_context.frames.push(Frame {
                        boundary: boundary.clone(),
                        path: path.clone(),
                        collection: collection.clone(),
                        position: true,
                        value: true,
                        control: control.clone(),
                    });
                    if expression.is_none() && !value.is_empty() {
                        let full = path.iter().chain(&value).cloned().collect::<Vec<_>>();
                        if let Some(field_id) = self.field_id(boundary, &full) {
                            if !ambiguous {
                                dependencies.push(Dependency::Field {
                                    field_id: field_id.clone(),
                                    role: role.into(),
                                });
                            }
                            details.push(group(
                                "Item value",
                                Detail::Reference {
                                    reference: NodeReference::Field { field_id },
                                },
                            ));
                        } else {
                            self.gap(
                                "projection.unresolvedAggregate",
                                "The aggregate value path has no unique schema field.",
                                locator.clone(),
                                context.projection_ref.clone(),
                            )?;
                        }
                    }
                }
                self.add_control(Control {
                    id: control,
                    kind: "iteration".into(),
                    summary: "Aggregate item context".into(),
                    applies_to: context.projection_ref.clone().into_iter().collect(),
                    dependencies: collection_dependencies,
                    details: vec![text_detail(
                        "Each item is evaluated in an aggregate-owned source frame.",
                    )],
                    native_locator: locator.clone(),
                })?;
                if let Some(node) = expression {
                    let expression =
                        self.expression_inner(node, &item_context, role, depth + 1, active)?;
                    details.push(group("Per-item expression", expression.detail));
                    if !ambiguous {
                        extend_dependencies(&mut dependencies, expression.dependencies);
                    }
                }
                if let Some(node) = arg {
                    let arg = self.expression_inner(node, context, role, depth + 1, active)?;
                    details.push(group("Parent-context argument", arg.detail));
                    extend_dependencies(&mut dependencies, arg.dependencies);
                }
                Expression {
                    detail: Detail::Expression {
                        operation: format!("{function:?}"),
                        arguments: details,
                    },
                    dependencies,
                    summary: format!("{function:?} collection"),
                }
            }
            Some(Node::Lookup {
                collection,
                key,
                matches,
                value,
            }) => {
                let matches =
                    self.expression_inner(matches, context, "condition", depth + 1, active)?;
                let mut dependencies = matches.dependencies;
                let mut details = vec![group("Match against", matches.detail)];
                let candidates = self.collection_candidates(&collection, context)?;
                let ambiguous = candidates.len() != 1;
                if ambiguous {
                    self.gap("projection.ambiguousLookup", "The lookup collection has no unique admitted source; references below are possible locations, not definite dependencies.", locator.clone(), context.projection_ref.clone())?;
                }
                for (boundary, path) in candidates {
                    for (label, leaf, dependency_role) in [
                        ("Key", key.as_slice(), "condition"),
                        ("Return", value.as_slice(), role),
                    ] {
                        let full = path
                            .iter()
                            .cloned()
                            .chain(leaf.iter().cloned())
                            .collect::<Vec<_>>();
                        if let Some(field_id) = self.field_id(&boundary, &full) {
                            details.push(group(
                                label,
                                Detail::Reference {
                                    reference: NodeReference::Field {
                                        field_id: field_id.clone(),
                                    },
                                },
                            ));
                            if !ambiguous {
                                dependencies.push(Dependency::Field {
                                    field_id,
                                    role: dependency_role.into(),
                                });
                            }
                        } else {
                            self.gap(
                                "projection.unresolvedLookup",
                                "The lookup path has no unique schema field.",
                                locator.clone(),
                                context.projection_ref.clone(),
                            )?;
                        }
                    }
                }
                Expression {
                    detail: Detail::Expression {
                        operation: "First matching lookup".into(),
                        arguments: details,
                    },
                    dependencies,
                    summary: "First matching lookup".into(),
                }
            }
            Some(Node::Raise { message }) => {
                let mut details = Vec::new();
                let mut dependencies = Vec::new();
                if let Some(node) = message {
                    let message = self.expression_inner(node, context, role, depth + 1, active)?;
                    details.push(group("Lazy message", message.detail));
                    dependencies = message.dependencies;
                }
                Expression {
                    detail: Detail::Expression {
                        operation: "Raise mapping exception".into(),
                        arguments: details,
                    },
                    dependencies,
                    summary: "Mapping exception".into(),
                }
            }
            Some(Node::SourceDocumentPath) => {
                let boundary = context.documents.last().cloned().or_else(|| {
                    self.payload
                        .boundaries
                        .iter()
                        .find(|boundary| {
                            boundary.role == "primaryInput" && boundary.shape == "documentSet"
                        })
                        .map(|boundary| boundary.id.clone())
                });
                if boundary.is_none() {
                    self.gap("projection.documentContext", "A source document location remains symbolic because no static document context is established.", locator.clone(), context.projection_ref.clone())?;
                }
                let mut details = vec![text_detail(
                    "Symbolic resolved location of the nearest active source document (never read or substituted here).",
                )];
                let dependencies = boundary
                    .map(|boundary_id| {
                        let node = NodeReference::Boundary { boundary_id };
                        details.push(Detail::Reference {
                            reference: node.clone(),
                        });
                        vec![Dependency::Node {
                            node,
                            role: role.into(),
                        }]
                    })
                    .unwrap_or_default();
                Expression {
                    detail: Detail::Group {
                        label: "Source document metadata".into(),
                        children: details,
                    },
                    dependencies,
                    summary: "Source document metadata".into(),
                }
            }
            Some(
                node @ (Node::DynamicSourceField { .. } | Node::OwnedDynamicSourceField(_)),
            ) => {
                let (key, construct) = match &node {
                    Node::OwnedDynamicSourceField(native) => {
                        (native.key, "Explicitly owned runtime-named source field")
                    }
                    Node::DynamicSourceField { key, .. } => (*key, "Runtime-named source field"),
                    _ => unreachable!(),
                };
                let key = self.expression_inner(key, context, role, depth + 1, active)?;
                let mut unsupported = self.unsupported(
                    construct,
                    "The computed field name has no fixed field lineage.",
                    locator.clone(),
                    context,
                )?;
                unsupported.detail = Detail::Group {
                    label: construct.into(),
                    children: vec![unsupported.detail, group("Computed key", key.detail)],
                };
                unsupported.dependencies = key.dependencies;
                unsupported
            }
            Some(node) => self.unsupported(
                unsupported_kind(&node),
                "This native expression is retained without a complete explanation.",
                locator.clone(),
                context,
            )?,
        };
        active.remove(&(context.function, id));
        Ok(result)
    }

    pub fn literal(&self, value: &Value) -> Result<Literal, &'static str> {
        Ok(match value {
            Value::String(value) => Literal::String {
                value: self.budget.text(value)?,
            },
            Value::Int(value) => Literal::ExactNumber {
                flavor: "integer".into(),
                value: value.to_string(),
            },
            Value::Float(value) => Literal::ExactNumber {
                flavor: "binaryFloat".into(),
                value: format!("{:016x}", value.to_bits()),
            },
            Value::Decimal(value) => Literal::ExactNumber {
                flavor: "decimal".into(),
                value: value.to_string(),
            },
            Value::Bool(value) => Literal::Boolean { value: *value },
            Value::Null => Literal::Null {
                null_kind: "absent".into(),
            },
            Value::JsonNull(_) => Literal::Null {
                null_kind: "explicitNull".into(),
            },
            Value::XmlNil(_) => Literal::Null {
                null_kind: "xmlNil".into(),
            },
        })
    }

    #[allow(clippy::too_many_arguments)]
    fn parameter_expression(
        &mut self,
        node: NodeId,
        name: &str,
        ty: ScalarType,
        default_value: Option<Literal>,
        preview: Option<&str>,
        context: &Context,
        role: &str,
        locator: NativeLocator,
    ) -> Result<Expression, &'static str> {
        let key = format!(
            "{}:{node}",
            context
                .function
                .map(|id| id.get().to_string())
                .unwrap_or_default()
        );
        let id = if let Some(id) = self.parameter_ids.get(&key) {
            id.clone()
        } else {
            let id = self.budget.id("parameter", &[&key])?;
            self.budget.charge(4)?;
            if let Some(value) = &default_value {
                self.budget.literal(value)?;
            }
            self.payload.parameters.push(Parameter {
                id: id.clone(),
                name: self.budget.text(name)?,
                data_type: DataType {
                    kind: "scalar".into(),
                    scalar_types: vec![scalar_name(ty).into()],
                    native_name: None,
                },
                requirement: "unknown".into(),
                default_value,
                native_locator: locator,
            });
            self.parameter_ids.insert(key, id.clone());
            id
        };
        let mut detail = Detail::Reference {
            reference: NodeReference::Parameter {
                parameter_id: id.clone(),
            },
        };
        if let Some(preview) = preview {
            detail = Detail::Group {
                label: "Host parameter".into(),
                children: vec![
                    detail,
                    group(
                        "Saved design-time preview (separate from default)",
                        text_detail(&self.budget.text(preview)?),
                    ),
                ],
            };
        }
        Ok(Expression {
            detail,
            dependencies: vec![Dependency::Parameter {
                parameter_id: id,
                role: role.into(),
            }],
            summary: "Runtime parameter".into(),
        })
    }

    fn source_expression(
        &mut self,
        path: &[String],
        frame: Option<&[String]>,
        context: &Context,
        role: &str,
        locator: NativeLocator,
    ) -> Result<Expression, &'static str> {
        for segment in path {
            self.budget.text(segment)?;
        }
        if context.function.is_some() {
            return self.unsupported(
                "Function source access",
                "Private functions cannot read caller source frames.",
                locator,
                context,
            );
        }
        if context.join_ownership_unknown {
            return self.unsupported("Unqualified joined source ownership", "An active joined source has no unique admitted schema context; scalar field lineage is unavailable.", locator, context);
        }
        if path.is_empty() && frame.is_none() {
            for candidate in context.frames.iter().rev() {
                if !candidate.value && context.generated.as_ref() == Some(&candidate.control) {
                    break;
                }
                if candidate.value
                    && let Some(field) = self.catalog.iter().find(|field| {
                        field.boundary_id == candidate.boundary
                            && field.path == candidate.path
                            && matches!(
                                &field.schema.kind,
                                ferrule_ir::SchemaKind::Scalar { .. }
                                    | ferrule_ir::SchemaKind::ScalarUnion { .. }
                            )
                    })
                {
                    let field_id = field.id.clone();
                    return self.fields_expression(
                        vec![field_id],
                        role,
                        locator,
                        context,
                        "Current source scalar item",
                    );
                }
            }
        }
        if path.is_empty()
            && frame.is_none()
            && let Some(control_id) = &context.generated
        {
            return Ok(Expression {
                detail: Detail::Reference {
                    reference: NodeReference::Control {
                        control_id: control_id.clone(),
                    },
                },
                dependencies: vec![Dependency::Node {
                    node: NodeReference::Control {
                        control_id: control_id.clone(),
                    },
                    role: role.into(),
                }],
                summary: "Generated item".into(),
            });
        }
        let mut candidates = Vec::new();
        let exact_frames = context
            .frames
            .iter()
            .rev()
            .filter(|candidate| {
                candidate.position
                    && candidate.value
                    && frame.is_some_and(|frame| {
                        candidate.collection == frame
                            || !candidate.collection.is_empty()
                                && frame.ends_with(&candidate.collection)
                    })
            })
            .take(1)
            .collect::<Vec<_>>();
        if let Some(frame) = frame {
            for segment in frame {
                self.budget.text(segment)?;
            }
            for candidate in exact_frames {
                let full = candidate
                    .path
                    .iter()
                    .chain(path)
                    .cloned()
                    .collect::<Vec<_>>();
                if let Some(id) = self.field_id(&candidate.boundary, &full) {
                    candidates.push(id);
                }
            }
        } else {
            let absolute_owner = context.frames.iter().rev().find(|candidate| {
                candidate.position
                    && candidate.value
                    && !candidate.collection.is_empty()
                    && path.starts_with(&candidate.collection)
            });
            if let Some(owner) = absolute_owner {
                let full = owner
                    .path
                    .iter()
                    .chain(&path[owner.collection.len()..])
                    .cloned()
                    .collect::<Vec<_>>();
                if let Some(id) = self.field_id(&owner.boundary, &full) {
                    candidates.push(id);
                }
            } else {
                for candidate in context.frames.iter().rev() {
                    if !candidate.value {
                        continue;
                    }
                    let full = candidate
                        .path
                        .iter()
                        .chain(path)
                        .cloned()
                        .collect::<Vec<_>>();
                    if let Some(id) = self.field_id(&candidate.boundary, &full) {
                        candidates.push(id);
                    }
                }
                candidates.extend(
                    self.catalog
                        .iter()
                        .filter(|field| {
                            field.input && field.source_name.is_none() && field.path == path
                        })
                        .map(|field| field.id.clone()),
                );
                if let Some((name, tail)) = path.split_first() {
                    candidates.extend(
                        self.catalog
                            .iter()
                            .filter(|field| {
                                field.input
                                    && field.source_name.as_ref() == Some(name)
                                    && field.path == tail
                            })
                            .map(|field| field.id.clone()),
                    );
                }
            }
        }
        let first_item_read = candidates.iter().any(|id| {
            self.catalog
                .iter()
                .find(|field| &field.id == id)
                .is_some_and(|field| {
                    self.catalog.iter().any(|collection| {
                        collection.input
                            && collection.boundary_id == field.boundary_id
                            && collection.schema.repeating
                            && field.path.starts_with(&collection.path)
                            && !context.frames.iter().any(|frame| {
                                frame.boundary == collection.boundary_id
                                    && frame.path == collection.path
                            })
                    })
                })
        });
        let open_shadow = frame.is_none()
            && context.frames.iter().any(|frame| {
                if !frame.value {
                    return false;
                }
                let full = frame.path.iter().chain(path).cloned().collect::<Vec<_>>();
                self.field_id(&frame.boundary, &full).is_none()
                    && self.catalog.iter().any(|field| {
                        field.id
                            == self
                                .field_id(&frame.boundary, &frame.path)
                                .unwrap_or_default()
                            && (field.schema.recursive_ref.is_some()
                                || matches!(
                                    &field.schema.kind,
                                    ferrule_ir::SchemaKind::Group {
                                        dynamic: Some(_),
                                        ..
                                    }
                                ))
                    })
            });
        let mut expression =
            self.fields_expression(candidates, role, locator.clone(), context, "Source field")?;
        if context.in_join && expression.dependencies.len() > 1 {
            expression.dependencies.clear();
            expression.detail = group(
                "Possible joined-source fallback locations (not definite dependencies)",
                expression.detail,
            );
        }
        if open_shadow {
            let message = "An active open or recursive source context can own this path; known outer fields are fallback possibilities, not definite dependencies.";
            self.gap(
                "projection.openSourceOwnership",
                message,
                locator.clone(),
                context.projection_ref.clone(),
            )?;
            expression.dependencies.clear();
            expression.detail = Detail::Group {
                label: "Unqualified source ownership".into(),
                children: vec![
                    Detail::Unsupported {
                        construct: "Runtime source ownership".into(),
                        description: message.into(),
                        native_locator: locator,
                    },
                    group("Known fallback possibilities", expression.detail),
                ],
            };
        }
        if first_item_read {
            expression.detail = Detail::Group {
                label: "Scalar source read".into(),
                children: vec![
                    expression.detail,
                    text_detail(
                        "At each uniterated repeated collection, the native scalar read uses its first item.",
                    ),
                ],
            };
        }
        Ok(expression)
    }

    fn fields_expression(
        &mut self,
        candidates: Vec<String>,
        role: &str,
        locator: NativeLocator,
        context: &Context,
        label: &str,
    ) -> Result<Expression, &'static str> {
        let candidates = candidates
            .into_iter()
            .filter(|id| {
                self.catalog.iter().any(|field| {
                    field.id == *id
                        && matches!(
                            &field.schema.kind,
                            ferrule_ir::SchemaKind::Scalar { .. }
                                | ferrule_ir::SchemaKind::ScalarUnion { .. }
                        )
                })
            })
            .collect::<Vec<_>>();
        let mut unique = Vec::new();
        for candidate in candidates {
            if !unique.contains(&candidate) {
                unique.push(candidate);
            }
        }
        if unique.len() != 1 {
            self.gap(
                if unique.is_empty() {
                    "projection.unresolvedField"
                } else {
                    "projection.ambiguousField"
                },
                if unique.is_empty() {
                    "The source path has no unique admitted field."
                } else {
                    "Outward fallback can refer to several schema fields."
                },
                locator.clone(),
                context.projection_ref.clone(),
            )?;
        }
        let dependencies = unique
            .iter()
            .map(|field_id| Dependency::Field {
                field_id: field_id.clone(),
                role: role.into(),
            })
            .collect();
        let children = unique
            .into_iter()
            .map(|field_id| Detail::Reference {
                reference: NodeReference::Field { field_id },
            })
            .collect::<Vec<_>>();
        Ok(Expression {
            detail: if children.is_empty() {
                Detail::Unsupported {
                    construct: label.into(),
                    description: "No fixed field explanation is available.".into(),
                    native_locator: locator,
                }
            } else {
                Detail::Group {
                    label: label.into(),
                    children,
                }
            },
            dependencies,
            summary: label.into(),
        })
    }

    pub fn collection_candidates(
        &self,
        path: &[String],
        context: &Context,
    ) -> Result<Vec<(String, Vec<String>)>, &'static str> {
        for segment in path {
            self.budget.text(segment)?;
        }
        let mut result = Vec::new();
        for frame in context.frames.iter().rev() {
            if !frame.value {
                continue;
            }
            let full = frame.path.iter().chain(path).cloned().collect::<Vec<_>>();
            if self.field_id(&frame.boundary, &full).is_some() {
                let candidate = (frame.boundary.clone(), full);
                if !result.contains(&candidate) {
                    result.push(candidate);
                }
            }
        }
        for field in &self.catalog {
            if field.input
                && (field.source_name.is_none() && field.path == path
                    || path.split_first().is_some_and(|(name, tail)| {
                        field.source_name.as_ref() == Some(name) && field.path == tail
                    }))
            {
                let candidate = (field.boundary_id.clone(), field.path.clone());
                if !result.contains(&candidate) {
                    result.push(candidate);
                }
            }
        }
        Ok(result)
    }

    fn unsupported(
        &mut self,
        construct: &str,
        message: &str,
        locator: NativeLocator,
        context: &Context,
    ) -> Result<Expression, &'static str> {
        self.gap(
            "projection.unsupportedExpression",
            message,
            locator.clone(),
            context.projection_ref.clone(),
        )?;
        Ok(Expression {
            detail: Detail::Unsupported {
                construct: self.budget.text(construct)?,
                description: self.budget.text(message)?,
                native_locator: locator,
            },
            dependencies: Vec::new(),
            summary: "Partial explanation".into(),
        })
    }
}

pub(crate) fn scalar_name(ty: ScalarType) -> &'static str {
    match ty {
        ScalarType::String => "string",
        ScalarType::Int => "integer",
        ScalarType::Float => "binaryFloat",
        ScalarType::Bool => "boolean",
    }
}

pub(crate) fn group(label: &str, detail: Detail) -> Detail {
    Detail::Group {
        label: label.into(),
        children: vec![detail],
    }
}
pub(crate) fn text_detail(value: &str) -> Detail {
    Detail::Literal {
        value: Literal::String {
            value: value.into(),
        },
    }
}
pub(crate) fn extend_dependencies(target: &mut Vec<Dependency>, values: Vec<Dependency>) {
    for value in values {
        if !target.contains(&value) {
            target.push(value);
        }
    }
}
fn merge_dependencies<const N: usize>(values: [Vec<Dependency>; N]) -> Vec<Dependency> {
    let mut result = Vec::new();
    for value in values {
        extend_dependencies(&mut result, value);
    }
    result
}

/// The forms an owned join tuple can evaluate; every other read stays closed there.
fn owned_join_tuple_node(node: &Node) -> bool {
    matches!(
        node,
        Node::OwnedJoinField(_)
            | Node::OwnedJoinSourcePosition(_)
            | Node::JoinPosition { .. }
            | Node::Unconnected
            | Node::Const { .. }
            | Node::Call { .. }
            | Node::If { .. }
            | Node::ValueMap { .. }
            | Node::RuntimeValue { .. }
            | Node::RuntimeParameter { .. }
            | Node::RuntimeParameterDefault { .. }
            | Node::Raise { .. }
    )
}

// Exhaustive so that a native form added upstream is named deliberately.
fn unsupported_kind(node: &Node) -> &'static str {
    match node {
        Node::OwnedPosition(_) => "Explicitly owned position",
        Node::GeneratedPosition { .. } => "Generated position",
        Node::OwnedRowDocumentPath(_) => "Explicitly owned row document location",
        Node::OwnedSourceDocumentPath(_) => "Explicitly owned source document location",
        Node::OwnedLookup(_) => "Explicitly owned lookup",
        Node::OwnedCollectionFind(_) => "Explicitly owned collection search",
        Node::OwnedXmlSerialize(_) => "Explicitly owned XML serialization",
        Node::OwnedAggregate(_) => "Explicitly owned aggregate",
        Node::OwnedXmlMixedContent(_) => "Explicitly owned XML mixed content",
        Node::OwnedJoinAggregate(_) => "Owned join aggregate",
        Node::SourceRootXmlTypeEquals { .. } => "Observed XML root type",
        Node::XmlMixedContent { .. } => "XML mixed content",
        Node::XmlSerialize { .. } => "XML serialization",
        Node::CollectionFind { .. } => "Collection search",
        Node::SequenceExists { .. } => "Generated sequence predicate",
        Node::SequenceItemAt { .. } => "Generated sequence item selection",
        Node::SequenceAggregate { .. } => "Generated sequence aggregate",
        Node::JoinAggregate { .. } => "Join aggregate",
        Node::InvocationCounter { .. } => "Invocation counter",
        Node::OwnedSourceField(_)
        | Node::OwnedJoinField(_)
        | Node::OwnedJoinSourcePosition(_)
        | Node::SourceField { .. }
        | Node::SourceRootField { .. }
        | Node::SourceDocumentPath
        | Node::Position { .. }
        | Node::JoinField { .. }
        | Node::JoinPosition { .. }
        | Node::Unconnected
        | Node::Const { .. }
        | Node::FunctionParameter { .. }
        | Node::RuntimeValue { .. }
        | Node::RuntimeParameter { .. }
        | Node::RuntimeParameterDefault { .. }
        | Node::Call { .. }
        | Node::UserFunctionCall { .. }
        | Node::If { .. }
        | Node::Raise { .. }
        | Node::ValueMap { .. }
        | Node::Lookup { .. }
        | Node::DynamicSourceField { .. }
        | Node::OwnedDynamicSourceField(_)
        | Node::Aggregate { .. } => "Unexplained native expression",
    }
}
