use super::{Failure, Format, MAX_DEPTH};
use ferrule_ir::{SchemaKind, SchemaNode};
use ferrule_mapping::{
    EdiBoundaryKind, FormatOptions, Node, Project, Scope, ScopeConstruction, ScopeIteration,
    TabularBoundaryKind,
};

pub(super) fn admit(native: &[u8], p: &Project) -> Result<(Format, Format), Failure> {
    if !p.extra_sources.is_empty() || !p.extra_targets.is_empty() {
        return Err(Failure::NamedEndpoints);
    }
    if p.source_path.is_some() || p.target_path.is_some() {
        return Err(Failure::ExternalDependency);
    }
    if !p.user_functions.is_empty() || !p.failure_rules.is_empty() {
        return Err(Failure::UnsupportedOperator);
    }
    let input = boundary(&p.source_options)?;
    let output = boundary(&p.target_options)?;
    // serde's native decoder accepts unknown option keys for forward-compatible
    // editing. Execution must not silently discard a future boundary capability.
    let raw: serde_json::Value =
        serde_json::from_slice(native).map_err(|_| Failure::InvalidNative)?;
    let raw = if raw.get("__ferrule_file").is_some() {
        &raw["document"]
    } else {
        &raw
    };
    let object = raw.as_object().ok_or(Failure::InvalidNative)?;
    for name in object.keys() {
        if !matches!(
            name.as_str(),
            "source"
                | "target"
                | "source_path"
                | "target_path"
                | "source_options"
                | "target_options"
                | "extra_sources"
                | "extra_targets"
                | "failure_rules"
                | "user_functions"
                | "graph"
                | "root"
        ) {
            return Err(Failure::ExternalDependency);
        }
    }
    for key in ["source_options", "target_options"] {
        if let Some(options) = raw.get(key).and_then(|v| v.as_object()) {
            for name in options.keys() {
                if !matches!(
                    name.as_str(),
                    "json_document"
                        | "xml_document"
                        | "tabular_kind"
                        | "edi_kind"
                        | "delimiter"
                        | "csv_quote"
                        | "csv_quote_disabled"
                        | "csv_utf8_bom"
                        | "csv_preserve_empty_strings"
                        | "has_header_row"
                        | "lenient_segments"
                        | "x12_separators"
                        | "x12_interchange_version"
                ) {
                    return Err(Failure::UnsupportedBoundary);
                }
            }
        }
    }
    let mut count = 0;
    schema(&p.source, 0, &mut count)?;
    schema(&p.target, 0, &mut count)?;
    for (format, schema) in [(input, &p.source), (output, &p.target)] {
        if format == Format::Xml && !ferrule_ir::xml_structured_document_input_is_supported(schema)
        {
            return Err(Failure::UnsupportedSchema);
        }
        if matches!(format, Format::Csv | Format::Tsv) {
            let SchemaKind::Group { children, .. } = &schema.kind else {
                return Err(Failure::UnsupportedSchema);
            };
            if schema.repeating
                || children.is_empty()
                || children
                    .iter()
                    .any(|c| c.repeating || !matches!(c.kind, SchemaKind::Scalar { .. }))
            {
                return Err(Failure::UnsupportedSchema);
            }
        }
    }
    if p.graph.nodes.len() > 8192 {
        return Err(Failure::InvalidMapping);
    }
    for node in p.graph.nodes.values() {
        if !matches!(
            node,
            Node::Const { .. } | Node::SourceField { .. } | Node::Position { .. }
        ) {
            return Err(Failure::UnsupportedOperator);
        }
    }
    scope(&p.root, 0, false)?;
    if !ferrule_engine::validate(p).is_empty() {
        return Err(Failure::InvalidMapping);
    }
    Ok((input, output))
}

fn boundary(o: &FormatOptions) -> Result<Format, Failure> {
    if o.edi_config_reference.is_some()
        || o.json_schema_unresolved_reference.is_some()
        || o.http_get.is_some()
        || o.external_source.is_some()
    {
        return Err(Failure::ExternalDependency);
    }
    let mut admitted = FormatOptions::default();
    let f = if o.json_document {
        admitted.json_document = true;
        Format::Json
    } else if o.xml_document {
        admitted.xml_document = true;
        Format::Xml
    } else if o.tabular_kind == Some(TabularBoundaryKind::Csv) {
        admitted.tabular_kind = o.tabular_kind;
        admitted.delimiter = o.delimiter;
        admitted.csv_quote = o.csv_quote;
        admitted.csv_quote_disabled = o.csv_quote_disabled;
        admitted.csv_utf8_bom = o.csv_utf8_bom;
        admitted.csv_preserve_empty_strings = o.csv_preserve_empty_strings;
        admitted.has_header_row = o.has_header_row;
        let delimiter = o.delimiter.unwrap_or(',');
        let quote = o.csv_quote.unwrap_or('"');
        if !delimiter.is_ascii()
            || matches!(delimiter, '\0' | '\r' | '\n')
            || !quote.is_ascii()
            || matches!(quote, '\0' | '\r' | '\n')
            || delimiter == quote
            || o.csv_quote_disabled && o.csv_quote.is_some()
        {
            return Err(Failure::UnsupportedBoundary);
        }
        if delimiter == '\t' {
            Format::Tsv
        } else {
            Format::Csv
        }
    } else {
        match o.edi_kind {
            Some(EdiBoundaryKind::X12) => {
                admitted.edi_kind = o.edi_kind;
                admitted.x12_separators = o.x12_separators;
                admitted.x12_interchange_version = o.x12_interchange_version.clone();
                // The complete-envelope tokenizer has no configured-release API.
                if o.x12_separators.is_some_and(|s| s.release.is_some()) {
                    return Err(Failure::UnsupportedBoundary);
                }
                if o.x12_interchange_version
                    .as_ref()
                    .is_some_and(|v| v.len() != 5 || !v.bytes().all(|b| b.is_ascii_digit()))
                {
                    return Err(Failure::UnsupportedBoundary);
                }
                Format::X12
            }
            Some(EdiBoundaryKind::Edifact) => {
                admitted.edi_kind = o.edi_kind;
                Format::Edifact
            }
            _ => return Err(Failure::UnsupportedBoundary),
        }
    };
    if *o != admitted {
        return Err(Failure::UnsupportedBoundary);
    }
    Ok(f)
}

fn schema(s: &SchemaNode, depth: usize, count: &mut usize) -> Result<(), Failure> {
    *count += 1;
    if depth > MAX_DEPTH || *count > 8192 || s.name.is_empty() || s.name.len() > 256 {
        return Err(Failure::UnsupportedSchema);
    }
    if s.xml_namespace
        .as_ref()
        .and_then(|n| n.uri())
        .is_some_and(|n| n.len() > 1024)
    {
        return Err(Failure::UnsupportedSchema);
    }
    let mut accepted = match &s.kind {
        SchemaKind::Scalar { ty } => SchemaNode::scalar(&s.name, *ty),
        SchemaKind::Group {
            children,
            required,
            alternatives,
            dynamic,
            xml_restricted_alternatives,
        } => {
            if !alternatives.is_empty()
                || dynamic.is_some()
                || !xml_restricted_alternatives.is_empty()
            {
                return Err(Failure::UnsupportedSchema);
            }
            let mut names = std::collections::BTreeSet::new();
            for child in children {
                if !names.insert(&child.name) {
                    return Err(Failure::UnsupportedSchema);
                }
                schema(child, depth + 1, count)?;
            }
            let mut group = SchemaNode::group(&s.name, children.clone());
            if let SchemaKind::Group { required: r, .. } = &mut group.kind {
                *r = required.clone();
            }
            group
        }
        _ => return Err(Failure::UnsupportedSchema),
    };
    accepted.repeating = s.repeating;
    accepted.xml_namespace = s.xml_namespace.clone();
    accepted.attribute = s.attribute;
    accepted.text = s.text;
    accepted.xml_optional = s.xml_optional;
    accepted.xml_attribute_required = s.xml_attribute_required;
    accepted.nullable = s.nullable;
    accepted.container_nullable = s.container_nullable;
    if accepted != *s {
        return Err(Failure::UnsupportedSchema);
    }
    Ok(())
}

fn scope(s: &Scope, depth: usize, iterating: bool) -> Result<(), Failure> {
    if depth > MAX_DEPTH {
        return Err(Failure::UnsupportedScope);
    }
    let active = match &s.iteration {
        ScopeIteration::None => iterating,
        ScopeIteration::Source(_) if !iterating => true,
        _ => return Err(Failure::UnsupportedScope),
    };
    let accepted = Scope {
        target_field: s.target_field.clone(),
        iteration: s.iteration.clone(),
        construction: ScopeConstruction::Constructed,
        bindings: s.bindings.clone(),
        children: s.children.clone(),
        ..Default::default()
    };
    if accepted != *s {
        return Err(Failure::UnsupportedScope);
    }
    for child in &s.children {
        scope(child, depth + 1, active)?;
    }
    Ok(())
}
