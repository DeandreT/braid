use std::collections::BTreeMap;

use ferrule_mapping::{
    FailureIteration, FailureSelection, IterationOutput, NodeId, OwnedSourceRef, Scope,
    ScopeConstruction, ScopeIteration, SequenceExpr, SequenceWindow, SortFilterOrder, SourceOwner,
    SourceSelector,
};

use super::budget::MAX_DEPTH;
use super::expressions::{extend_dependencies, group, scalar_name, text_detail};
use super::types::*;
use super::{Builder, Context, Frame};

impl Builder<'_> {
    pub fn functions(&mut self) -> Result<(), &'static str> {
        // Reserve every definition identity before walking a body: calls stay
        // references even when two reusable definitions refer to one another.
        for id in self.project.user_functions.keys() {
            self.function_controls.insert(
                *id,
                self.budget
                    .id("control", &["function", &id.get().to_string()])?,
            );
        }
        for (id, function) in self.project.user_functions.clone() {
            let control = self.function_controls[&id].clone();
            let mut context = Context {
                function: Some(id),
                use_id: control.clone(),
                projection_ref: Some(NodeReference::Control {
                    control_id: control.clone(),
                }),
                ..Context::default()
            };
            let mut parameters = Vec::new();
            for parameter in &function.parameters {
                self.budget.charge(1)?;
                let name = self.budget.text(&parameter.name)?;
                context
                    .private_parameters
                    .insert(parameter.id.get(), name.clone());
                parameters.push(Detail::Group {
                    label: name,
                    children: vec![
                        group(
                            "Native identity",
                            text_detail(&parameter.id.get().to_string()),
                        ),
                        group("Type", text_detail(scalar_name(parameter.ty))),
                    ],
                });
            }
            let body = self.expression(function.output, &context, "value")?;
            let mut details = vec![
                group(
                    "Library",
                    text_detail(&self.budget.text(&function.library)?),
                ),
                Detail::Group {
                    label: "Private parameters".into(),
                    children: parameters,
                },
                group("Body", body.detail),
                group(
                    "Output",
                    text_detail(&self.budget.text(&function.output_name)?),
                ),
                group(
                    "Output coercion",
                    text_detail(scalar_name(function.output_type)),
                ),
            ];
            if let Some(description) = &function.description {
                details.push(group(
                    "Description",
                    text_detail(&self.budget.text(description)?),
                ));
            }
            self.add_control(Control {
                id: control,
                kind: "construction".into(),
                summary: self
                    .budget
                    .text(&format!("Reusable function {}", function.name))?,
                applies_to: Vec::new(),
                dependencies: body.dependencies,
                details,
                native_locator: self.pointer(&format!("/user_functions/{}", id.get()))?,
            })?;
        }
        Ok(())
    }

    pub fn targets(&mut self) -> Result<(), &'static str> {
        let outputs = self
            .payload
            .boundaries
            .iter()
            .filter(|boundary| matches!(boundary.role.as_str(), "primaryOutput" | "namedOutput"))
            .map(|boundary| boundary.id.clone())
            .collect::<Vec<_>>();
        self.scope(
            &self.project.root.clone(),
            &outputs[0],
            &[],
            "/root",
            &Context::default(),
            0,
        )?;
        for (index, target) in self.project.extra_targets.clone().iter().enumerate() {
            self.scope(
                &target.root,
                &outputs[index + 1],
                &[],
                &format!("/extra_targets/{index}/root"),
                &Context::default(),
                0,
            )?;
        }
        // Paths are explanatory graph expressions only. No design resource is
        // opened and no saved resource path is included in the projection.
        for (index, source) in self.project.extra_sources.clone().iter().enumerate() {
            if let Some(dynamic) = &source.dynamic_path {
                let boundary = self
                    .payload
                    .boundaries
                    .iter()
                    .filter(|boundary| boundary.role == "namedInput")
                    .nth(index)
                    .unwrap()
                    .id
                    .clone();
                let suffix = format!("/extra_sources/{index}/dynamic_path");
                let id = self.budget.id("control", &[&suffix, "dynamicInput"])?;
                let mut context = Context {
                    use_id: id.clone(),
                    projection_ref: Some(NodeReference::Control {
                        control_id: id.clone(),
                    }),
                    ..Context::default()
                };
                let dependencies = match &dynamic.iteration {
                    SourceSelector::Legacy(path) => {
                        self.source_frames(path, &mut context, &id, &suffix)?
                    }
                    SourceSelector::Owned(driver) => {
                        let candidates = self.owned_collection(driver)?;
                        self.frames_for_candidates(
                            candidates,
                            &driver.path,
                            &mut context,
                            &id,
                            &suffix,
                        )?
                    }
                };
                let path = self.expression(dynamic.node, &context, "value")?;
                let mut all_dependencies = dependencies;
                extend_dependencies(&mut all_dependencies, path.dependencies);
                self.add_control(Control {
                    id,
                    kind: "dynamicBoundary".into(),
                    summary: "Dynamic named input location".into(),
                    applies_to: vec![NodeReference::Boundary {
                        boundary_id: boundary,
                    }],
                    dependencies: all_dependencies,
                    details: vec![group(
                        "Symbolic location expression (never opened)",
                        path.detail,
                    )],
                    native_locator: self.pointer(&suffix)?,
                })?;
            }
        }
        Ok(())
    }

    #[allow(clippy::too_many_arguments)]
    fn scope(
        &mut self,
        scope: &Scope,
        boundary: &str,
        output_path: &[String],
        suffix: &str,
        parent: &Context,
        depth: usize,
    ) -> Result<(), &'static str> {
        if depth > MAX_DEPTH {
            return Err("The projection exceeds its scope-depth limit.");
        }
        self.budget.charge(1)?;
        self.budget.text(&scope.target_field)?;
        let target = self.field_id(boundary, output_path);
        let target_ref = target.as_ref().map(|id| NodeReference::Field {
            field_id: id.clone(),
        });
        // Ferrule refuses these scopes before evaluation, so their reads have no lineage.
        let closed = !parent.owned_join_closed
            && if parent.owned_join_tuple {
                !owned_tuple_descendant_is_open(scope)
            } else {
                matches!(scope.iteration, ScopeIteration::OwnedInnerJoin { .. })
                    && !owned_join_is_open(scope)
            };
        let closed_parent;
        let parent = if closed {
            self.gap(
                "projection.closedOwnedJoinScope",
                "Ferrule refuses this owned join context before evaluation; its expressions have no executable explanation.",
                self.pointer(suffix)?,
                target_ref.clone(),
            )?;
            closed_parent = Context {
                owned_join_closed: true,
                ..parent.clone()
            };
            &closed_parent
        } else {
            parent
        };
        let mut context = parent.clone();
        let iteration_id = self
            .budget
            .id("control", &[boundary, suffix, "iteration"])?;
        context.use_id = iteration_id.clone();
        context.projection_ref = target_ref.clone();
        let first_rule = self.payload.rules.len();
        let first_control = self.payload.controls.len();
        match &scope.iteration {
            ScopeIteration::None => {}
            ScopeIteration::Source(path)
            | ScopeIteration::DynamicDocuments { source: path, .. } => {
                let dependencies = self.source_frames(path, &mut context, &iteration_id, suffix)?;
                self.add_control(Control { id: iteration_id.clone(), kind: "iteration".into(), summary: "Source item iteration".into(),
                    applies_to: target_ref.clone().into_iter().collect(), dependencies,
                    details: vec![text_detail("Items are resolved relative to the current source frame, with outward fallback."), output_detail(scope.iteration_output)],
                    native_locator: self.pointer(suffix)? })?;
            }
            ScopeIteration::OwnedSource { source, .. } => {
                let collections = self.owned_collection(source)?;
                let dependencies = self.frames_for_candidates(
                    collections,
                    &source.path,
                    &mut context,
                    &iteration_id,
                    suffix,
                )?;
                self.add_control(Control {
                    id: iteration_id.clone(),
                    kind: "iteration".into(),
                    summary: "Explicitly owned source iteration".into(),
                    applies_to: target_ref.clone().into_iter().collect(),
                    dependencies,
                    details: vec![
                        output_detail(scope.iteration_output),
                        text_detail(
                            "This read-only worker does not execute owned-source iteration; preview and build remain unqualified.",
                        ),
                    ],
                    native_locator: self.pointer(suffix)?,
                })?;
            }
            ScopeIteration::Sequence(sequence) => {
                let (detail, dependencies) = self.sequence(sequence, parent)?;
                context.generated = Some(iteration_id.clone());
                context.frames.push(Frame {
                    boundary: String::new(),
                    path: Vec::new(),
                    collection: Vec::new(),
                    position: true,
                    value: false,
                    control: iteration_id.clone(),
                });
                self.add_control(Control {
                    id: iteration_id.clone(),
                    kind: "iteration".into(),
                    summary: "Generated scalar sequence".into(),
                    applies_to: target_ref.clone().into_iter().collect(),
                    dependencies,
                    details: vec![detail, output_detail(scope.iteration_output)],
                    native_locator: self.pointer(suffix)?,
                })?;
            }
            ScopeIteration::InnerJoin { id, plan } => {
                let mut dependencies = Vec::new();
                let mut details = Vec::new();
                let mut collections = BTreeMap::new();
                context.in_join = true;
                for source in plan.sources() {
                    let candidates = self.collection_candidates(source.collection(), parent)?;
                    if candidates.len() != 1 {
                        context.join_ownership_unknown = true;
                        self.gap(
                            "projection.ambiguousJoinSource",
                            "A join source has no unique admitted collection.",
                            self.pointer(suffix)?,
                            target_ref.clone(),
                        )?;
                    }
                    let mut source_context = parent.clone();
                    self.frames_for_candidates(
                        candidates.clone(),
                        source.collection(),
                        &mut source_context,
                        &iteration_id,
                        suffix,
                    )?;
                    let new_frames = &source_context.frames[parent.frames.len()..];
                    context.frames.extend_from_slice(new_frames);
                    context
                        .documents
                        .extend_from_slice(&source_context.documents[parent.documents.len()..]);
                    if !new_frames.iter().any(|frame| frame.position) && candidates.len() == 1 {
                        let (boundary, path) = &candidates[0];
                        context.frames.push(Frame {
                            boundary: boundary.clone(),
                            path: path.clone(),
                            collection: source.collection().to_vec(),
                            position: true,
                            value: true,
                            control: iteration_id.clone(),
                        });
                        self.budget.charge(1 + path.len())?;
                    }
                    for (boundary, path) in &candidates {
                        if let Some(field_id) = self.field_id(boundary, path) {
                            dependencies.push(Dependency::Field {
                                field_id: field_id.clone(),
                                role: "value".into(),
                            });
                            details.push(group(
                                &format!("{:?} join source", source.cardinality()),
                                Detail::Reference {
                                    reference: NodeReference::Field { field_id },
                                },
                            ));
                        }
                    }
                    collections.insert(source.collection().to_vec(), candidates);
                }
                for (stage, (right, conditions)) in plan.stages().enumerate() {
                    let mut keys = Vec::new();
                    for condition in conditions.iter() {
                        let mut pair = Vec::new();
                        for (collection, leaf) in [
                            (condition.left_collection(), condition.left_path()),
                            (right.collection(), condition.right_path()),
                        ] {
                            for segment in leaf {
                                self.budget.text(segment)?;
                            }
                            for (boundary, path) in
                                self.collection_candidates(collection, parent)?
                            {
                                let full = path.iter().chain(leaf).cloned().collect::<Vec<_>>();
                                if let Some(field_id) = self.field_id(&boundary, &full) {
                                    extend_dependencies(
                                        &mut dependencies,
                                        vec![Dependency::Field {
                                            field_id: field_id.clone(),
                                            role: "value".into(),
                                        }],
                                    );
                                    pair.push(Detail::Reference {
                                        reference: NodeReference::Field { field_id },
                                    });
                                } else {
                                    self.gap(
                                        "projection.unresolvedJoinKey",
                                        "A join equality path has no unique admitted field.",
                                        self.pointer(suffix)?,
                                        target_ref.clone(),
                                    )?;
                                }
                            }
                        }
                        keys.push(Detail::Expression {
                            operation: "Equal".into(),
                            arguments: pair,
                        });
                    }
                    details.push(Detail::Group {
                        label: format!("Join stage {} (all keys must match)", stage + 1),
                        children: keys,
                    });
                }
                context.joins.insert(id.get(), collections);
                context.join_controls.insert(id.get(), iteration_id.clone());
                details.push(output_detail(scope.iteration_output));
                details.push(text_detail("Joined source frames retain raw source positions. Ordinary reads search from the last joined source outward; JoinPosition is the separate flattened tuple position."));
                self.add_control(Control {
                    id: iteration_id.clone(),
                    kind: "join".into(),
                    summary: "Ordered inner join".into(),
                    applies_to: target_ref.clone().into_iter().collect(),
                    dependencies,
                    details,
                    native_locator: self.pointer(&format!("{suffix}/join/plan"))?,
                })?;
            }
            ScopeIteration::OwnedInnerJoin { id, plan } => {
                let mut dependencies = Vec::new();
                let mut details = Vec::new();
                let mut members = Vec::new();
                for source in plan.sources() {
                    let candidates = self.owned_collection(source.collection())?;
                    if candidates.len() != 1 {
                        self.gap(
                            "projection.ambiguousJoinSource",
                            "A join source has no unique admitted collection.",
                            self.pointer(suffix)?,
                            target_ref.clone(),
                        )?;
                    }
                    for (boundary, path) in &candidates {
                        if let Some(field_id) = self.field_id(boundary, path) {
                            dependencies.push(Dependency::Field {
                                field_id: field_id.clone(),
                                role: "value".into(),
                            });
                            details.push(group(
                                &format!("{:?} owned join source", source.cardinality()),
                                Detail::Reference {
                                    reference: NodeReference::Field { field_id },
                                },
                            ));
                        }
                    }
                    members.push((source.collection().clone(), candidates));
                }
                for (stage, (right, conditions)) in plan.stages().enumerate() {
                    let mut keys = Vec::new();
                    for condition in conditions.iter() {
                        let mut pair = Vec::new();
                        for (collection, leaf) in [
                            (condition.left_collection(), condition.left_path()),
                            (right.collection(), condition.right_path()),
                        ] {
                            for segment in leaf {
                                self.budget.text(segment)?;
                            }
                            // Key paths are row-relative to their exact member; no other
                            // endpoint or same-path collection supplies them.
                            let candidates = members
                                .iter()
                                .find(|(member, _)| member == collection)
                                .map(|(_, candidates)| candidates.clone())
                                .unwrap_or_default();
                            let mut resolved = false;
                            for (boundary, path) in candidates {
                                let full = path.iter().chain(leaf).cloned().collect::<Vec<_>>();
                                if let Some(field_id) = self.field_id(&boundary, &full) {
                                    resolved = true;
                                    extend_dependencies(
                                        &mut dependencies,
                                        vec![Dependency::Field {
                                            field_id: field_id.clone(),
                                            role: "value".into(),
                                        }],
                                    );
                                    pair.push(Detail::Reference {
                                        reference: NodeReference::Field { field_id },
                                    });
                                }
                            }
                            if !resolved {
                                self.gap(
                                    "projection.unresolvedJoinKey",
                                    "A join equality path has no unique admitted field.",
                                    self.pointer(suffix)?,
                                    target_ref.clone(),
                                )?;
                            }
                        }
                        keys.push(Detail::Expression {
                            operation: "Equal".into(),
                            arguments: pair,
                        });
                    }
                    details.push(Detail::Group {
                        label: format!("Join stage {} (all keys must match)", stage + 1),
                        children: keys,
                    });
                }
                context.owned_joins.insert(id.get(), members);
                context.owned_join_tuple = true;
                context.join_controls.insert(id.get(), iteration_id.clone());
                details.push(output_detail(scope.iteration_output));
                details.push(text_detail("Each source and key belongs to exactly one endpoint. Tuple expressions read members only through owned join reads and the flattened join position."));
                self.add_control(Control {
                    id: iteration_id.clone(),
                    kind: "join".into(),
                    summary: "Ordered owned inner join".into(),
                    applies_to: target_ref.clone().into_iter().collect(),
                    dependencies,
                    details,
                    native_locator: self.pointer(&format!("{suffix}/join/plan"))?,
                })?;
            }
            ScopeIteration::OptionalPresence { condition } => {
                let mut use_context = parent.clone();
                use_context.use_id = iteration_id.clone();
                use_context.projection_ref = Some(NodeReference::Control {
                    control_id: iteration_id.clone(),
                });
                let expression = self.expression(*condition, &use_context, "condition")?;
                self.add_control(Control {
                    id: iteration_id.clone(),
                    kind: "iteration".into(),
                    summary: "Optional presence".into(),
                    applies_to: target_ref.clone().into_iter().collect(),
                    dependencies: expression.dependencies,
                    details: vec![
                        group("Present when true", expression.detail),
                        text_detail("Constructs at most one group in the unchanged parent context; no iteration frame is added."),
                    ],
                    native_locator: self.pointer(suffix)?,
                })?;
            }
            ScopeIteration::Concatenate(sequence) => {
                self.add_control(Control {
                    id: iteration_id.clone(),
                    kind: "ordering".into(),
                    summary: "Ordered scope concatenation".into(),
                    applies_to: target_ref.clone().into_iter().collect(),
                    dependencies: Vec::new(),
                    details: vec![text_detail(
                        "Segments append in declared order; each owns its own source context.",
                    )],
                    native_locator: self.pointer(suffix)?,
                })?;
                for (index, segment) in sequence.iter().enumerate() {
                    self.scope(
                        segment,
                        boundary,
                        output_path,
                        &format!("{suffix}/segments/{index}"),
                        parent,
                        depth + 1,
                    )?;
                }
            }
        }
        // Optional presence keeps the parent item, so it also keeps a grouped parent's members.
        context.grouped = scope.has_grouping()
            || ((!scope.iterates() || scope.presence().is_some()) && parent.grouped);
        self.scope_controls(scope, suffix, &context, parent, target_ref.clone())?;
        if let Some(path) = scope.output_path() {
            let id = self
                .budget
                .id("control", &[boundary, suffix, "outputPath"])?;
            let mut use_context = context.clone();
            use_context.use_id = id.clone();
            use_context.projection_ref = Some(NodeReference::Control {
                control_id: id.clone(),
            });
            let expression = self.expression(path, &use_context, "value")?;
            self.add_control(Control {
                id,
                kind: "dynamicBoundary".into(),
                summary: "Dynamic output document location".into(),
                applies_to: vec![NodeReference::Boundary {
                    boundary_id: boundary.into(),
                }],
                dependencies: expression.dependencies,
                details: vec![group(
                    "Symbolic location expression (never written)",
                    expression.detail,
                )],
                native_locator: self.pointer(suffix)?,
            })?;
        }
        match &scope.construction {
            ScopeConstruction::Constructed => {}
            ScopeConstruction::Scalar { value } => {
                if let Some(field_id) = target.as_ref() {
                    self.binding(
                        *value,
                        field_id,
                        &format!("{suffix}/construction"),
                        &context,
                        "Scalar scope construction",
                    )?;
                } else {
                    self.gap(
                        "projection.unresolvedTarget",
                        "The scalar scope has no unique target schema field.",
                        self.pointer(suffix)?,
                        None,
                    )?;
                }
            }
            construction => {
                let (name, message) = match construction {
                    ScopeConstruction::CopyCurrentSource => (
                        "Copy current source",
                        "The complete current source group is copied without field-by-field bindings.",
                    ),
                    ScopeConstruction::XmlMixedContent { .. } => (
                        "XML mixed content construction",
                        "Interleaved text and child occurrence order remain native construction metadata.",
                    ),
                    ScopeConstruction::RecursiveFilter { .. } => (
                        "Recursive filtering construction",
                        "The recursive construction plan is retained without expanding a recursive graph.",
                    ),
                    ScopeConstruction::PathHierarchy { .. } => (
                        "Path hierarchy construction",
                        "The bounded recursive path-tree plan remains at its native location.",
                    ),
                    ScopeConstruction::AdjacencyTree { .. } => (
                        "Adjacency tree construction",
                        "The bounded recursive adjacency-tree plan remains at its native location.",
                    ),
                    ScopeConstruction::OwnedPathHierarchy { .. } => (
                        "Owned path hierarchy construction",
                        "The bounded recursive path-tree plan over one explicitly owned collection remains at its native location.",
                    ),
                    ScopeConstruction::OwnedAdjacencyTree { .. } => (
                        "Owned adjacency tree construction",
                        "The bounded recursive adjacency-tree plan over one explicitly owned collection remains at its native location.",
                    ),
                    ScopeConstruction::Constructed | ScopeConstruction::Scalar { .. } => {
                        unreachable!()
                    }
                };
                let id = self
                    .budget
                    .id("control", &[boundary, suffix, "construction"])?;
                let dependencies = context
                    .frames
                    .iter()
                    .filter_map(|frame| self.field_id(&frame.boundary, &frame.path))
                    .map(|field_id| Dependency::Field {
                        field_id,
                        role: "value".into(),
                    })
                    .collect();
                self.gap(
                    "projection.nativeConstruction",
                    message,
                    self.pointer(suffix)?,
                    target_ref.clone(),
                )?;
                self.add_control(Control {
                    id,
                    kind: "construction".into(),
                    summary: name.into(),
                    applies_to: target_ref.clone().into_iter().collect(),
                    dependencies,
                    details: vec![Detail::Unsupported {
                        construct: name.into(),
                        description: message.into(),
                        native_locator: self.pointer(suffix)?,
                    }],
                    native_locator: self.pointer(suffix)?,
                })?;
            }
        }
        for (index, binding) in scope.bindings.iter().enumerate() {
            self.budget.text(&binding.target_field)?;
            let path = output_path
                .iter()
                .cloned()
                .chain([binding.target_field.clone()])
                .collect::<Vec<_>>();
            let pointer = format!("{suffix}/bindings/{index}");
            if let Some(field_id) = self.field_id(boundary, &path) {
                self.binding(binding.node, &field_id, &pointer, &context, "Field binding")?;
            } else {
                self.gap(
                    "projection.unresolvedTarget",
                    "The binding has no unique admitted target field.",
                    self.pointer(&pointer)?,
                    target_ref.clone(),
                )?;
            }
        }
        for (index, child) in scope.children.iter().enumerate() {
            let path = output_path
                .iter()
                .cloned()
                .chain([self.budget.text(&child.target_field)?])
                .collect::<Vec<_>>();
            self.scope(
                child,
                boundary,
                &path,
                &format!("{suffix}/children/{index}"),
                &context,
                depth + 1,
            )?;
        }
        for (index, binding) in scope.dynamic_bindings.iter().enumerate() {
            let pointer = format!("{suffix}/dynamic_bindings/{index}");
            self.dynamic_field(
                binding.key,
                Some(binding.value),
                &pointer,
                &context,
                target_ref.clone(),
            )?;
        }
        for (index, child) in scope.dynamic_children.iter().enumerate() {
            let pointer = format!("{suffix}/dynamic_children/{index}");
            self.dynamic_field(child.key, None, &pointer, &context, target_ref.clone())?;
            // A computed name has no honest fixed target field. Keep its body
            // located instead of assigning it to an unrelated schema field.
            self.gap("projection.dynamicChild", "The runtime-named child scope remains at its native location; its output has no fixed field identity.", self.pointer(&format!("{pointer}/scope"))?, target_ref.clone())?;
        }
        if scope.merge_dynamic_fields {
            let id = self
                .budget
                .id("control", &[boundary, suffix, "mergeDynamic"])?;
            self.add_control(Control { id, kind: "construction".into(), summary: "Merge dynamic object fragments".into(), applies_to: target_ref.clone().into_iter().collect(), dependencies: Vec::new(),
                details: vec![text_detail("Iteration fragments merge into one object; duplicate computed names are rejected.")], native_locator: self.pointer(suffix)? })?;
        }
        let governed_rules = self.payload.rules[first_rule..]
            .iter()
            .map(|rule| NodeReference::Rule {
                rule_id: rule.id.clone(),
            })
            .collect::<Vec<_>>();
        let scope_locator = self.pointer(suffix)?;
        for control in &mut self.payload.controls[first_control..] {
            // This scope's direct controls govern every nested rule. Nested
            // controls already carry their own narrower applies-to set.
            if control.native_locator == scope_locator || control.id == iteration_id {
                self.budget.charge(governed_rules.len())?;
                control.applies_to.extend(governed_rules.iter().cloned());
            }
        }
        Ok(())
    }

    fn binding(
        &mut self,
        node: NodeId,
        field_id: &str,
        suffix: &str,
        context: &Context,
        label: &str,
    ) -> Result<(), &'static str> {
        let id = self.budget.id("rule", &[field_id, suffix])?;
        let mut context = context.clone();
        context.use_id = id.clone();
        context.projection_ref = Some(NodeReference::Rule {
            rule_id: id.clone(),
        });
        let expression = self.expression(node, &context, "value")?;
        self.add_rule(Rule {
            id,
            output_field_id: field_id.into(),
            summary: format!("{label}: {}", expression.summary),
            dependencies: expression.dependencies,
            details: vec![expression.detail],
            native_locator: self.pointer(suffix)?,
        })
    }

    /// The collection of exactly one endpoint at its endpoint-relative path. No
    /// other endpoint, alias or same-path collection is a candidate.
    pub(crate) fn owned_collection(
        &mut self,
        source: &OwnedSourceRef,
    ) -> Result<Vec<(String, Vec<String>)>, &'static str> {
        let owner = match &source.owner {
            SourceOwner::Primary => None,
            SourceOwner::Named { name } => Some(name.as_str()),
        };
        if let Some(name) = owner {
            self.budget.text(name)?;
        }
        for segment in &source.path {
            self.budget.text(segment)?;
        }
        Ok(self
            .catalog
            .iter()
            .filter(|field| {
                field.input && field.source_name.as_deref() == owner && field.path == source.path
            })
            .map(|field| (field.boundary_id.clone(), field.path.clone()))
            .collect())
    }

    fn source_frames(
        &mut self,
        path: &[String],
        context: &mut Context,
        control: &str,
        suffix: &str,
    ) -> Result<Vec<Dependency>, &'static str> {
        let candidates = self.collection_candidates(path, context)?;
        self.frames_for_candidates(candidates, path, context, control, suffix)
    }

    fn frames_for_candidates(
        &mut self,
        candidates: Vec<(String, Vec<String>)>,
        selector: &[String],
        context: &mut Context,
        control: &str,
        suffix: &str,
    ) -> Result<Vec<Dependency>, &'static str> {
        if candidates.len() != 1 {
            self.gap("projection.ambiguousIteration", "The iteration selector has no unique source collection; all admitted candidates remain visible.", self.pointer(suffix)?, context.projection_ref.clone())?;
        }
        let mut dependencies = Vec::new();
        let parent_frames = context.frames.clone();
        for (boundary, path) in candidates {
            if let Some(field_id) = self.field_id(&boundary, &path) {
                dependencies.push(Dependency::Field {
                    field_id,
                    role: "value".into(),
                });
            }
            let parent_base = parent_frames.iter().rev().find(|frame| {
                frame.value
                    && frame.boundary == boundary
                    && frame
                        .path
                        .iter()
                        .chain(selector)
                        .cloned()
                        .collect::<Vec<_>>()
                        == path
            });
            let base = parent_base
                .map(|frame| frame.path.as_slice())
                .unwrap_or(&[]);
            let consumed = path.len().saturating_sub(base.len());
            let alias = selector.len().saturating_sub(consumed);
            if selector.is_empty()
                && context.grouped
                && let Some(parent) = parent_base
            {
                context.frames.push(Frame {
                    boundary: boundary.clone(),
                    path: path.clone(),
                    collection: parent.collection.clone(),
                    position: true,
                    value: true,
                    control: control.into(),
                });
                self.budget.charge(1 + path.len())?;
            }
            let document_set = self
                .payload
                .boundaries
                .iter()
                .any(|item| item.id == boundary && item.shape == "documentSet");
            if document_set && parent_base.is_none() {
                context.documents.push(boundary.clone());
                context.frames.push(Frame {
                    boundary: boundary.clone(),
                    path: Vec::new(),
                    collection: selector[..alias].to_vec(),
                    position: true,
                    value: true,
                    control: control.into(),
                });
                self.budget.charge(1)?;
            }
            let frames = self
                .catalog
                .iter()
                .filter(|field| {
                    field.input
                        && field.boundary_id == boundary
                        && field.schema.repeating
                        && path.starts_with(&field.path)
                        && field.path.starts_with(base)
                        && (parent_base.is_none() || field.path.len() > base.len())
                })
                .map(|field| field.path.clone())
                .collect::<Vec<_>>();
            for frame in frames {
                let selector_length = alias + frame.len().saturating_sub(base.len());
                let collection = selector[..selector_length.min(selector.len())].to_vec();
                self.budget.charge(1 + frame.len())?;
                context.frames.push(Frame {
                    boundary: boundary.clone(),
                    path: frame,
                    collection,
                    position: true,
                    value: true,
                    control: control.into(),
                });
            }
            let terminal_singular = self.catalog.iter().any(|field| {
                field.input
                    && field.boundary_id == boundary
                    && field.path == path
                    && !field.schema.repeating
            });
            if terminal_singular && !selector.is_empty() {
                self.budget.charge(1 + path.len())?;
                context.frames.push(Frame {
                    boundary: boundary.clone(),
                    path,
                    collection: selector.to_vec(),
                    position: false,
                    value: true,
                    control: control.into(),
                });
            }
        }
        Ok(dependencies)
    }

    fn scope_controls(
        &mut self,
        scope: &Scope,
        suffix: &str,
        context: &Context,
        parent: &Context,
        target: Option<NodeReference>,
    ) -> Result<(), &'static str> {
        if let Some(node) = scope.filter {
            self.expression_control(
                "filter",
                "Per-item filter",
                node,
                "condition",
                suffix,
                "filter",
                context,
                target.clone(),
            )?;
        }
        if let Some(node) = scope.post_group_filter {
            self.expression_control(
                "filter",
                "Post-group filter (any matching member retains the group)",
                node,
                "condition",
                suffix,
                "postGroupFilter",
                context,
                target.clone(),
            )?;
        }
        for (name, node) in [
            ("Group by equal keys", scope.group_by),
            ("Group adjacent equal keys", scope.group_adjacent_by),
            (
                "Group starting with matching items",
                scope.group_starting_with,
            ),
            ("Group ending with matching items", scope.group_ending_with),
            ("Group into blocks", scope.group_into_blocks),
        ] {
            if let Some(node) = node {
                self.expression_control(
                    "grouping",
                    name,
                    node,
                    if matches!(
                        name,
                        "Group starting with matching items" | "Group ending with matching items"
                    ) {
                        "condition"
                    } else {
                        "value"
                    },
                    suffix,
                    name,
                    if name == "Group into blocks" {
                        parent
                    } else {
                        context
                    },
                    target.clone(),
                )?;
            }
        }
        if scope.has_sort() {
            let id = self.budget.id("control", &[suffix, "sorting"])?;
            let mut details = Vec::new();
            let mut dependencies = Vec::new();
            let mut use_context = context.clone();
            use_context.use_id = id.clone();
            use_context.projection_ref = Some(NodeReference::Control {
                control_id: id.clone(),
            });
            for (index, key) in scope.sort_keys().enumerate() {
                let expression = self.expression(key.node, &use_context, "value")?;
                details.push(group(
                    &format!(
                        "Key {} ({})",
                        index + 1,
                        if key.descending {
                            "descending"
                        } else {
                            "ascending"
                        }
                    ),
                    expression.detail,
                ));
                extend_dependencies(&mut dependencies, expression.dependencies);
            }
            details.push(text_detail(
                "Stable ordering retains source order when keys are equal or incomparable.",
            ));
            self.add_control(Control {
                id,
                kind: "sorting".into(),
                summary: "Ordered sort keys".into(),
                applies_to: target.clone().into_iter().collect(),
                dependencies,
                details,
                native_locator: self.pointer(suffix)?,
            })?;
        }
        if scope.has_sort() || scope.filter.is_some() {
            let id = self.budget.id("control", &[suffix, "sortFilterOrder"])?;
            self.add_control(Control {
                id,
                kind: "ordering".into(),
                summary: match scope.sort_filter_order {
                    SortFilterOrder::SortThenFilter => "Sort then filter",
                    SortFilterOrder::FilterThenSort => "Filter then sort",
                }
                .into(),
                applies_to: target.clone().into_iter().collect(),
                dependencies: Vec::new(),
                details: vec![text_detail(
                    "Grouping and ordered windows follow these ordinary controls.",
                )],
                native_locator: self.pointer(suffix)?,
            })?;
        }
        for (index, window) in scope.windows.iter().enumerate() {
            let id = self
                .budget
                .id("control", &[suffix, "window", &index.to_string()])?;
            let label = match window {
                SequenceWindow::SkipFirst { .. } => "Skip first",
                SequenceWindow::First { .. } => "First",
                SequenceWindow::From { .. } => "From",
                SequenceWindow::FromTo { .. } => "Inclusive range",
                SequenceWindow::Last { .. } => "Last",
            };
            let mut use_context = parent.clone();
            use_context.use_id = id.clone();
            use_context.projection_ref = Some(NodeReference::Control {
                control_id: id.clone(),
            });
            let mut details = Vec::new();
            let mut dependencies = Vec::new();
            for node in window.nodes() {
                let expression = self.expression(node, &use_context, "value")?;
                details.push(expression.detail);
                extend_dependencies(&mut dependencies, expression.dependencies);
            }
            self.add_control(Control {
                id,
                kind: "window".into(),
                summary: format!("Window {}: {label}", index + 1),
                applies_to: target.clone().into_iter().collect(),
                dependencies,
                details: vec![Detail::Group {
                    label: label.into(),
                    children: details,
                }],
                native_locator: self.pointer(&format!("{suffix}/windows/{index}"))?,
            })?;
        }
        Ok(())
    }

    #[allow(clippy::too_many_arguments)]
    fn expression_control(
        &mut self,
        kind: &str,
        summary: &str,
        node: NodeId,
        role: &str,
        suffix: &str,
        key: &str,
        context: &Context,
        target: Option<NodeReference>,
    ) -> Result<(), &'static str> {
        let id = self.budget.id("control", &[suffix, key])?;
        let mut use_context = context.clone();
        use_context.use_id = id.clone();
        use_context.projection_ref = Some(NodeReference::Control {
            control_id: id.clone(),
        });
        let expression = self.expression(node, &use_context, role)?;
        let mut details = vec![expression.detail];
        if kind == "grouping" {
            details.push(text_detail("Groups retain source encounter order. Ordinary bindings read the first member; aggregates reduce members and nested source scopes can iterate members."));
            details.push(text_detail(match summary {
                "Group by equal keys" => "Equal keys share one group in first-seen key order.",
                "Group adjacent equal keys" => "Only contiguous equal-key runs share a group; a later repeated key starts a separate group.",
                "Group starting with matching items" => "A matching item begins a group; items before the first match form an initial group.",
                "Group ending with matching items" => "A matching item ends a group; items after the final match form a trailing group.",
                _ => "Each contiguous block contains the requested member count; the final block can be shorter.",
            }));
        }
        self.add_control(Control {
            id,
            kind: kind.into(),
            summary: summary.into(),
            applies_to: target.into_iter().collect(),
            dependencies: expression.dependencies,
            details,
            native_locator: self.pointer(suffix)?,
        })
    }

    fn sequence(
        &mut self,
        sequence: &SequenceExpr,
        context: &Context,
    ) -> Result<(Detail, Vec<Dependency>), &'static str> {
        let mut details = Vec::new();
        let mut dependencies = Vec::new();
        for node in sequence.inputs() {
            let expression = self.expression(node, context, "value")?;
            details.push(expression.detail);
            extend_dependencies(&mut dependencies, expression.dependencies);
        }
        if matches!(
            sequence,
            SequenceExpr::RecursiveCollect { .. }
                | SequenceExpr::OwnedRecursiveCollect(_)
                | SequenceExpr::FilterMapV1(_)
        ) {
            self.gap("projection.nativeSequence", "The advanced sequence plan remains native metadata; only its enclosing inputs are explained.", crate::protocol::NativeLocator::Document { document_id: self.document_id.into() }, context.projection_ref.clone())?;
            details.push(Detail::Unsupported {
                construct: sequence.kind_name().into(),
                description: "The advanced sequence plan is not completely explained.".into(),
                native_locator: crate::protocol::NativeLocator::Document {
                    document_id: self.document_id.into(),
                },
            });
        }
        if matches!(sequence, SequenceExpr::Generate { from: None, .. }) {
            details.insert(0, text_detail("The omitted lower bound is one."));
        }
        Ok((
            Detail::Expression {
                operation: sequence.kind_name().into(),
                arguments: details,
            },
            dependencies,
        ))
    }

    fn dynamic_field(
        &mut self,
        key: NodeId,
        value: Option<NodeId>,
        suffix: &str,
        context: &Context,
        target: Option<NodeReference>,
    ) -> Result<(), &'static str> {
        let id = self.budget.id("control", &[suffix, "dynamicField"])?;
        let mut use_context = context.clone();
        use_context.use_id = id.clone();
        use_context.projection_ref = Some(NodeReference::Control {
            control_id: id.clone(),
        });
        let key = self.expression(key, &use_context, "condition")?;
        let mut dependencies = key.dependencies;
        let mut details = vec![group("Computed field name", key.detail)];
        if let Some(value) = value {
            let value = self.expression(value, &use_context, "value")?;
            details.push(group("Value", value.detail));
            extend_dependencies(&mut dependencies, value.dependencies);
        }
        self.gap(
            "projection.dynamicTarget",
            "A runtime-computed target name has no fixed output field identity.",
            self.pointer(suffix)?,
            target.clone(),
        )?;
        self.add_control(Control {
            id,
            kind: "construction".into(),
            summary: "Runtime-named target field".into(),
            applies_to: target.into_iter().collect(),
            dependencies,
            details,
            native_locator: self.pointer(suffix)?,
        })
    }

    pub fn failures(&mut self) -> Result<(), &'static str> {
        let outputs = self
            .payload
            .boundaries
            .iter()
            .filter(|boundary| matches!(boundary.role.as_str(), "primaryOutput" | "namedOutput"))
            .map(|boundary| NodeReference::Boundary {
                boundary_id: boundary.id.clone(),
            })
            .collect::<Vec<_>>();
        for (index, rule) in self.project.failure_rules.clone().iter().enumerate() {
            let suffix = format!("/failure_rules/{index}");
            let id = self.budget.id("control", &[&suffix, "failure"])?;
            let mut context = Context {
                use_id: id.clone(),
                projection_ref: Some(NodeReference::Control {
                    control_id: id.clone(),
                }),
                ..Context::default()
            };
            let mut details = Vec::new();
            let mut dependencies = match &rule.iteration {
                FailureIteration::Source { collection } => {
                    self.source_frames(collection, &mut context, &id, &suffix)?
                }
                FailureIteration::OwnedSource { source } => {
                    let candidates = self.owned_collection(source)?;
                    self.frames_for_candidates(
                        candidates,
                        &source.path,
                        &mut context,
                        &id,
                        &suffix,
                    )?
                }
                FailureIteration::Sequence { sequence } => {
                    let (detail, dependencies) = self.sequence(sequence, &context)?;
                    details.push(detail);
                    context.generated = Some(id.clone());
                    context.frames.push(Frame {
                        boundary: String::new(),
                        path: Vec::new(),
                        collection: Vec::new(),
                        position: true,
                        value: false,
                        control: id.clone(),
                    });
                    dependencies
                }
            };
            if let Some(node) = rule.selection.predicate() {
                let predicate = self.expression(node, &context, "condition")?;
                details.push(group(
                    match rule.selection {
                        FailureSelection::WhenTrue { .. } => "Select when true",
                        _ => "Select when false",
                    },
                    predicate.detail,
                ));
                extend_dependencies(&mut dependencies, predicate.dependencies);
            } else {
                details.push(text_detail("Select every item."));
            }
            if let Some(node) = rule.message {
                let message = self.expression(node, &context, "value")?;
                details.push(group(
                    "Lazy message from the first selected item",
                    message.detail,
                ));
                extend_dependencies(&mut dependencies, message.dependencies);
            } else {
                details.push(text_detail("No message expression is specified."));
            }
            self.add_control(Control {
                id,
                kind: "failure".into(),
                summary: format!("Failure rule {} (before any output)", index + 1),
                applies_to: outputs.clone(),
                dependencies,
                details,
                native_locator: self.pointer(&suffix)?,
            })?;
        }
        Ok(())
    }

    pub fn ordering(&mut self, outputs: &[String]) -> Result<(), &'static str> {
        if outputs.len() > 1 {
            let id = self.budget.id("control", &["outputOrder"])?;
            self.add_control(Control {
                id,
                kind: "ordering".into(),
                summary: "Primary output, then named outputs in declared order".into(),
                applies_to: outputs
                    .iter()
                    .map(|id| NodeReference::Boundary {
                        boundary_id: id.clone(),
                    })
                    .collect(),
                dependencies: Vec::new(),
                details: outputs
                    .iter()
                    .enumerate()
                    .map(|(index, id)| {
                        group(
                            &format!("Output {}", index + 1),
                            Detail::Reference {
                                reference: NodeReference::Boundary {
                                    boundary_id: id.clone(),
                                },
                            },
                        )
                    })
                    .collect(),
                native_locator: crate::protocol::NativeLocator::Document {
                    document_id: self.document_id.into(),
                },
            })?;
        }
        Ok(())
    }
}

/// Ferrule admits an owned join only with plain construction and a context it can
/// own: no grouping, mapped-sequence output or runtime-named targets.
fn owned_join_is_open(scope: &Scope) -> bool {
    matches!(
        scope.construction,
        ScopeConstruction::Constructed | ScopeConstruction::Scalar { .. }
    ) && owned_capability_is_basic(scope)
}

/// Only static, constructed descendant scopes share an owned join tuple.
fn owned_tuple_descendant_is_open(scope: &Scope) -> bool {
    matches!(scope.iteration, ScopeIteration::None)
        && matches!(scope.construction, ScopeConstruction::Constructed)
        && owned_capability_is_basic(scope)
}

fn owned_capability_is_basic(scope: &Scope) -> bool {
    scope.iteration_output != IterationOutput::MappedSequence
        && !scope.has_grouping()
        && scope.post_group_filter.is_none()
        && scope.dynamic_bindings.is_empty()
        && scope.dynamic_children.is_empty()
        && !scope.merge_dynamic_fields
}

fn output_detail(output: IterationOutput) -> Detail {
    text_detail(match output {
        IterationOutput::Repeated => {
            "Retain all surviving items; declared schema cardinality stays independent."
        }
        IterationOutput::First => {
            "Retain the first surviving item after controls, or an empty group when no item survives; declared schema cardinality stays independent."
        }
        IterationOutput::MappedSequence => {
            "Retain mapping-produced XML occurrences; declared schema cardinality stays independent."
        }
    })
}
