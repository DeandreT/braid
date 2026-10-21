use std::collections::BTreeSet;
use std::path::Path;

use ferrule_engine::TargetSelection;
use ferrule_mapping::{FormatOptions, Graph, Node, Project};

use super::protocol::{PreviewInput, PreviewRequestHeader, PreviewTarget};

/// One admitted output boundary, in the order the payload API renders it.
pub struct AdmittedTarget {
    pub boundary_id: String,
    pub name: String,
}

pub struct Admission<'a> {
    pub selection: Option<TargetSelection<'a>>,
    pub targets: Vec<AdmittedTarget>,
}

pub struct Refusal {
    pub code: &'static str,
    pub message: &'static str,
    pub subject: Option<String>,
}

fn refuse(code: &'static str, message: &'static str, subject: Option<&str>) -> Refusal {
    Refusal {
        code,
        message,
        subject: subject.map(Into::into),
    }
}

/// Admits only static plain-JSON boundaries reachable by the selected target(s),
/// before any input document is parsed or any graph node is evaluated.
pub fn admit<'a>(
    project: &Project,
    header: &'a PreviewRequestHeader,
) -> Result<Admission<'a>, Refusal> {
    let selection = match &header.target {
        PreviewTarget::All => None,
        PreviewTarget::Primary => Some(TargetSelection::Primary),
        PreviewTarget::Named { name } => {
            let matches = project
                .extra_targets
                .iter()
                .filter(|target| target.name == *name)
                .count();
            if matches != 1 {
                return Err(refuse(
                    "unknownTarget",
                    "The selected target is not exactly one named target of this mapping.",
                    Some(name),
                ));
            }
            Some(TargetSelection::Named(name))
        }
    };

    let mut targets = Vec::new();
    if matches!(header.target, PreviewTarget::All | PreviewTarget::Primary) {
        if project.root.output_path().is_some() {
            return Err(refuse(
                "unsupportedCapability",
                "Dynamically named output documents are outside the JSON preview profile.",
                Some("primary-output"),
            ));
        }
        if !plain_json(&project.target_options, project.target_path.as_deref()) {
            return Err(refuse(
                "unsupportedCapability",
                "The primary output is not a plain JSON document.",
                Some("primary-output"),
            ));
        }
        targets.push(AdmittedTarget {
            boundary_id: "primary-output".into(),
            name: project.target.name.clone(),
        });
    }
    for (index, target) in project.extra_targets.iter().enumerate() {
        let selected = match &header.target {
            PreviewTarget::All => true,
            PreviewTarget::Primary => false,
            PreviewTarget::Named { name } => target.name == *name,
        };
        if !selected {
            continue;
        }
        let boundary_id = format!("named-output:{index}");
        if target.root.output_path().is_some() {
            return Err(refuse(
                "unsupportedCapability",
                "Dynamically named output documents are outside the JSON preview profile.",
                Some(&boundary_id),
            ));
        }
        // The payload API resolves an unselected named target only from its stored path.
        let explicit = matches!(header.target, PreviewTarget::Named { .. });
        let stored = target
            .path
            .as_deref()
            .filter(|path| !path.trim().is_empty());
        if (!explicit && stored.is_none()) || !plain_json(&target.options, stored) {
            return Err(refuse(
                "unsupportedCapability",
                "A selected named output is not a plain JSON document.",
                Some(&boundary_id),
            ));
        }
        targets.push(AdmittedTarget {
            boundary_id,
            name: target.name.clone(),
        });
    }

    if !plain_json(&project.source_options, project.source_path.as_deref()) {
        return Err(refuse(
            "unsupportedCapability",
            "The primary input is not a plain JSON document.",
            Some("primary"),
        ));
    }
    let required = match selection {
        None => project
            .extra_sources
            .iter()
            .map(|source| (source.name.as_str(), source.dynamic_path.is_some()))
            .collect::<Vec<_>>(),
        Some(selection) => {
            let sources =
                ferrule_engine::required_sources_for_target(project, selection).map_err(|_| {
                    refuse(
                        "unknownTarget",
                        "The selected target cannot be resolved.",
                        None,
                    )
                })?;
            sources
                .static_sources
                .iter()
                .map(|source| (source.name.as_str(), false))
                .chain(
                    sources
                        .dynamic_sources
                        .iter()
                        .map(|source| (source.name.as_str(), true)),
                )
                .collect()
        }
    };
    let mut required_names = BTreeSet::new();
    for (name, dynamic) in required {
        if dynamic {
            return Err(refuse(
                "unsupportedCapability",
                "Dynamically loaded named inputs are outside the JSON preview profile.",
                Some(name),
            ));
        }
        let mut matching = project
            .extra_sources
            .iter()
            .filter(|source| source.name == name);
        let (Some(source), None) = (matching.next(), matching.next()) else {
            return Err(refuse(
                "unsupportedCapability",
                "A required named input is not declared exactly once.",
                Some(name),
            ));
        };
        if !plain_json(&source.options, Some(&source.path)) {
            return Err(refuse(
                "unsupportedCapability",
                "A required named input is not a plain JSON document.",
                Some(name),
            ));
        }
        required_names.insert(name);
    }

    let supplied = header
        .inputs
        .iter()
        .filter_map(|input| match input {
            PreviewInput::Named { name, .. } => Some(name.as_str()),
            PreviewInput::Primary { .. } => None,
        })
        .collect::<BTreeSet<_>>();
    if let Some(missing) = required_names.difference(&supplied).next() {
        return Err(refuse(
            "missingInput",
            "A named input required by the selected target was not supplied.",
            Some(missing),
        ));
    }
    if let Some(unexpected) = supplied.difference(&required_names).next() {
        return Err(refuse(
            "unexpectedInput",
            "A supplied named input is not required by the selected target.",
            Some(unexpected),
        ));
    }

    let declared = declared_parameters(project);
    for parameter in &header.parameters {
        if !declared.contains(parameter.name.as_str()) {
            return Err(refuse(
                "unknownParameter",
                "A supplied runtime parameter is not declared by this mapping.",
                Some(&parameter.name),
            ));
        }
    }
    Ok(Admission { selection, targets })
}

/// A boundary is plain JSON when every non-JSON option is absent and any stored
/// design-time path names a `.json` document unless JSON is explicitly selected.
pub(crate) fn plain_json(options: &FormatOptions, stored_path: Option<&str>) -> bool {
    let comparable = FormatOptions {
        json_document: options.json_document,
        mfd_decimal_input_names: options.mfd_decimal_input_names.clone(),
        ..Default::default()
    };
    if *options != comparable {
        return false;
    }
    match stored_path.filter(|path| !path.trim().is_empty()) {
        Some(path) => {
            options.json_document
                || Path::new(path)
                    .extension()
                    .and_then(|extension| extension.to_str())
                    .is_some_and(|extension| extension.eq_ignore_ascii_case("json"))
        }
        None => true,
    }
}

fn declared_parameters(project: &Project) -> BTreeSet<&str> {
    fn collect<'a>(graph: &'a Graph, names: &mut BTreeSet<&'a str>) {
        for node in graph.nodes.values() {
            if let Node::RuntimeParameter { name, .. }
            | Node::RuntimeParameterDefault { name, .. } = node
            {
                names.insert(name.as_str());
            }
        }
    }
    let mut names = BTreeSet::new();
    collect(&project.graph, &mut names);
    for function in project.user_functions.values() {
        collect(&function.body, &mut names);
    }
    names
}
