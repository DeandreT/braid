use ferrule_ir::{ScalarType, SchemaKind, SchemaNode, XmlNamespace, XmlWildcardProcessContents};
use ferrule_mapping::{EdiBoundaryKind, FormatOptions, Project, TabularBoundaryKind};

use super::budget::Budget;
use super::types::{Boundary, Cardinality, DataType, Field, Gap, NodeReference, PathSegment};

const MAX_SCHEMA_DEPTH: usize = 32;

pub(crate) struct Inventory<'a> {
    pub boundaries: Vec<Boundary>,
    pub fields: Vec<Field>,
    pub catalog: Vec<FieldInfo<'a>>,
    pub gaps: Vec<Gap>,
}

pub(crate) struct FieldInfo<'a> {
    pub id: String,
    pub boundary_id: String,
    pub source_name: Option<String>,
    pub input: bool,
    /// Native instance-field names, with the boundary root excluded.
    /// Repetition is represented only in the public typed path.
    pub path: Vec<String>,
    pub schema: &'a SchemaNode,
}

pub(crate) fn inventory<'a>(
    project: &'a Project,
    document_id: &str,
    prefix: &str,
    budget: &mut Budget,
) -> Result<Inventory<'a>, &'static str> {
    let mut result = Inventory {
        boundaries: Vec::new(),
        fields: Vec::new(),
        catalog: Vec::new(),
        gaps: Vec::new(),
    };
    add_boundary(
        &mut result,
        &project.source,
        &project.source_options,
        &project.source.name,
        "primaryInput",
        "0",
        true,
        None,
        document_id,
        format!("{prefix}/source"),
        format!("{prefix}/source"),
        budget,
    )?;
    for (index, source) in project.extra_sources.iter().enumerate() {
        add_boundary(
            &mut result,
            &source.schema,
            &source.options,
            &source.name,
            "namedInput",
            &index.to_string(),
            true,
            Some(&source.name),
            document_id,
            format!("{prefix}/extra_sources/{index}"),
            format!("{prefix}/extra_sources/{index}/schema"),
            budget,
        )?;
    }
    add_boundary(
        &mut result,
        &project.target,
        &project.target_options,
        &project.target.name,
        "primaryOutput",
        "0",
        false,
        None,
        document_id,
        format!("{prefix}/target"),
        format!("{prefix}/target"),
        budget,
    )?;
    for (index, target) in project.extra_targets.iter().enumerate() {
        add_boundary(
            &mut result,
            &target.schema,
            &target.options,
            &target.name,
            "namedOutput",
            &index.to_string(),
            false,
            None,
            document_id,
            format!("{prefix}/extra_targets/{index}"),
            format!("{prefix}/extra_targets/{index}/schema"),
            budget,
        )?;
    }
    Ok(result)
}

#[allow(clippy::too_many_arguments)]
fn add_boundary<'a>(
    inventory: &mut Inventory<'a>,
    root: &'a SchemaNode,
    options: &FormatOptions,
    name: &str,
    role: &str,
    ordinal: &str,
    input: bool,
    source_name: Option<&str>,
    document_id: &str,
    boundary_pointer: String,
    schema_pointer: String,
    budget: &mut Budget,
) -> Result<(), &'static str> {
    // Boundary, locator, and root-field reference are all counted.
    budget.charge(3)?;
    let boundary_id = budget.id("boundary", &[role, ordinal, &boundary_pointer])?;
    let root_id = budget.id("field", &[&boundary_id, &schema_pointer])?;
    let format = boundary_format(options);
    inventory.boundaries.push(Boundary {
        id: boundary_id.clone(),
        name: budget.text(name)?,
        role: role.into(),
        format: format.into(),
        shape: if options.local_xml_file_set {
            "documentSet"
        } else if root.repeating {
            "rows"
        } else {
            match &root.kind {
                SchemaKind::Scalar { .. } | SchemaKind::ScalarUnion { .. } => "scalar",
                SchemaKind::Group { .. } => "object",
            }
        }
        .into(),
        root_field_id: Some(root_id),
        native_locator: budget.pointer(document_id, boundary_pointer)?,
    });
    let source_name = source_name.map(|name| budget.text(name)).transpose()?;
    let mut pending = vec![Pending {
        schema: root,
        pointer: schema_pointer,
        path: Vec::new(),
        public_path: Vec::new(),
        requirement: root_requirement(root, options),
        depth: 0,
    }];
    while let Some(current) = pending.pop() {
        if current.depth > MAX_SCHEMA_DEPTH {
            return Err("The schema exceeds the projection depth limit.");
        }
        let schema = current.schema;
        let data_type = data_type(schema);
        // Field, data type, cardinality, locator, paths, and scalar members.
        budget.charge(4 + current.public_path.len() + data_type.scalar_types.len())?;
        let id = budget.id("field", &[&boundary_id, &current.pointer])?;
        let locator = budget.pointer(document_id, current.pointer.clone())?;
        inventory.fields.push(Field {
            id: id.clone(),
            boundary_id: boundary_id.clone(),
            path: current.public_path.clone(),
            name: budget.text(&schema.name)?,
            data_type,
            cardinality: cardinality(schema, current.requirement, budget)?,
            requirement: current.requirement.into(),
            nullability: nullability(schema, options).into(),
            native_locator: locator,
        });
        inventory.catalog.push(FieldInfo {
            id: id.clone(),
            boundary_id: boundary_id.clone(),
            source_name: source_name.clone(),
            input,
            path: current.path.clone(),
            schema,
        });
        schema_gaps(
            schema,
            options,
            current.depth == 0,
            &id,
            &current.pointer,
            document_id,
            budget,
            &mut inventory.gaps,
        )?;
        if let SchemaKind::Group {
            children, required, ..
        } = &schema.kind
        {
            // A nested-array wrapper's child name is an implementation role,
            // not a physical object property. The located gap retains it
            // without manufacturing an ordinary field named "item".
            if schema.json_array_wrapper {
                continue;
            }
            budget.charge(children.len())?;
            for (index, child) in children.iter().enumerate().rev() {
                let name = budget.text(&child.name)?;
                let mut path = current.path.clone();
                path.push(name.clone());
                let mut public_path = current.public_path.clone();
                if schema.repeating {
                    public_path.push(PathSegment::AnyItem);
                }
                let namespace_uri = match if format == "xml" {
                    child.xml_namespace.as_ref()
                } else {
                    None
                } {
                    Some(XmlNamespace::Qualified(uri)) => Some(budget.text(uri.as_str())?),
                    // Empty is exact unqualified; None is unspecified legacy identity.
                    Some(XmlNamespace::Unqualified) => Some(String::new()),
                    None => None,
                };
                public_path.push(if format == "xml" && child.attribute {
                    PathSegment::Attribute {
                        name,
                        namespace_uri,
                    }
                } else {
                    PathSegment::Field {
                        name,
                        namespace_uri,
                    }
                });
                pending.push(Pending {
                    schema: child,
                    pointer: format!("{}/kind/children/{index}", current.pointer),
                    path,
                    public_path,
                    requirement: child_requirement(child, required, options),
                    depth: current.depth + 1,
                });
            }
        }
    }
    Ok(())
}

struct Pending<'a> {
    schema: &'a SchemaNode,
    pointer: String,
    path: Vec<String>,
    public_path: Vec<PathSegment>,
    requirement: &'static str,
    depth: usize,
}

fn root_requirement(schema: &SchemaNode, options: &FormatOptions) -> &'static str {
    if options.xml_document && !options.json_document {
        xml_requirement(schema)
    } else {
        "unknown"
    }
}

fn child_requirement(
    schema: &SchemaNode,
    parent_required: &[String],
    options: &FormatOptions,
) -> &'static str {
    if options.json_document && !options.xml_document {
        if parent_required.iter().any(|name| name == &schema.name) {
            "required"
        } else {
            "optional"
        }
    } else if options.xml_document && !options.json_document {
        xml_requirement(schema)
    } else {
        "unknown"
    }
}

fn xml_requirement(schema: &SchemaNode) -> &'static str {
    if schema.attribute {
        if schema.xml_attribute_required {
            "required"
        } else {
            "optional"
        }
    } else if schema.repeating || schema.text {
        "unknown"
    } else if schema.xml_optional {
        "optional"
    } else {
        "required"
    }
}

fn cardinality(
    schema: &SchemaNode,
    requirement: &str,
    budget: &Budget,
) -> Result<Cardinality, &'static str> {
    if let Some(range) = schema.item_count_range {
        Ok(Cardinality {
            kind: "bounded".into(),
            minimum: Some(budget.text(&range.minimum().to_string())?),
            maximum: range
                .maximum()
                .map(|maximum| budget.text(&maximum.to_string()))
                .transpose()?,
        })
    } else {
        Ok(Cardinality {
            kind: if schema.repeating {
                "zeroOrMore"
            } else if requirement == "optional" {
                "zeroOrOne"
            } else {
                "exactlyOne"
            }
            .into(),
            minimum: None,
            maximum: None,
        })
    }
}

fn data_type(schema: &SchemaNode) -> DataType {
    let (kind, scalar_types) = match &schema.kind {
        SchemaKind::Scalar { ty } => ("scalar", vec![scalar_type(*ty).into()]),
        SchemaKind::ScalarUnion { types } => (
            "scalarUnion",
            types.iter().map(|ty| scalar_type(ty).into()).collect(),
        ),
        SchemaKind::Group { .. } => ("object", Vec::new()),
    };
    DataType {
        kind: if schema.repeating { "array" } else { kind }.into(),
        scalar_types,
        native_name: None,
    }
}

fn scalar_type(ty: ScalarType) -> &'static str {
    match ty {
        ScalarType::String => "string",
        ScalarType::Int => "integer",
        ScalarType::Float => "binaryFloat",
        ScalarType::Bool => "boolean",
    }
}

fn nullability(schema: &SchemaNode, options: &FormatOptions) -> &'static str {
    let nullable = if options.json_document && !options.xml_document {
        if schema.repeating || matches!(&schema.kind, SchemaKind::Group { .. }) {
            schema.container_nullable
        } else {
            schema.nullable
        }
    } else if options.xml_document && !options.json_document {
        if schema.repeating {
            // XML nil belongs to an occurrence, not an enclosing array.
            return "unknown";
        }
        schema.nillable
    } else {
        return "unknown";
    };
    if nullable { "nullable" } else { "nonNullable" }
}

fn boundary_format(options: &FormatOptions) -> &'static str {
    // Use explicit format identity only. Design-time paths are not opened,
    // parsed as URLs, or used to infer format-dependent schema assertions.
    if options.idoc.is_some() || options.idoc_native_config.is_some() {
        "idoc"
    } else if options.swift_mt.is_some() {
        "swiftMt"
    } else if options.fixed_width.is_some() {
        "fixedWidth"
    } else if options.flextext.is_some() {
        "flexText"
    } else if options.pdf.is_some() {
        "pdf"
    } else if options.protobuf.is_some() {
        "protobuf"
    } else if options.xbrl.is_some() {
        "xbrl"
    } else if options.json_document && options.xml_document {
        "unknown"
    } else if options.xml_document {
        "xml"
    } else if options.json5 {
        "json5"
    } else if options.json_lines {
        "jsonLines"
    } else if options.json_document {
        "json"
    } else if let Some(kind) = options.edi_kind {
        match kind {
            EdiBoundaryKind::X12 => "x12",
            EdiBoundaryKind::Edifact => "edifact",
            EdiBoundaryKind::Hl7 => "hl7",
            EdiBoundaryKind::Tradacoms => "tradacoms",
            EdiBoundaryKind::Idoc => "idoc",
            EdiBoundaryKind::SwiftMt => "swiftMt",
        }
    } else if let Some(kind) = options.tabular_kind {
        match kind {
            TabularBoundaryKind::Csv => "csv",
            TabularBoundaryKind::Xlsx => "xlsx",
        }
    } else {
        "unknown"
    }
}

#[allow(clippy::too_many_arguments)]
fn schema_gaps(
    schema: &SchemaNode,
    options: &FormatOptions,
    root: bool,
    field_id: &str,
    pointer: &str,
    document_id: &str,
    budget: &mut Budget,
    gaps: &mut Vec<Gap>,
) -> Result<(), &'static str> {
    let mut add =
        |present: bool, code: &str, message: &str, suffix: &str| -> Result<(), &'static str> {
            if present {
                let locator = budget.pointer(document_id, format!("{pointer}{suffix}"))?;
                gaps.push(budget.gap(
                    code,
                    message,
                    locator,
                    Some(NodeReference::Field {
                        field_id: field_id.into(),
                    }),
                )?);
            }
            Ok(())
        };
    add(
        schema.recursive_ref.is_some(),
        "schema.recursiveReference",
        "The recursive schema reference is retained at its native location; its recursive child shape is not expanded.",
        "/recursive_ref",
    )?;
    add(
        schema.json_array_wrapper,
        "schema.nestedArray",
        "The outer array is visible; its nested array item schema is retained at the native location and is not shown as an object property.",
        "/json_array_wrapper",
    )?;
    add(
        schema.json_any,
        "schema.arbitraryJson",
        "This field contains arbitrary JSON encoded in the native string domain; its physical value shape is not described by the scalar type.",
        "/json_any",
    )?;
    add(
        boundary_format(options) == "xml" && root && schema.xml_namespace.is_some(),
        "schema.rootNamespace",
        "The root namespace identity is retained at its native location; the empty root field path cannot carry a namespace segment.",
        "/xml_namespace",
    )?;
    add(
        boundary_format(options) == "unknown"
            && (schema.attribute || schema.xml_namespace.is_some()),
        "schema.formatDependentPath",
        "XML path annotations remain at the native location; an unqualified boundary format cannot assert an attribute or namespace path.",
        "",
    )?;
    add(
        !schema.xml_name_alternatives.is_empty(),
        "schema.namespaceAlternatives",
        "The primary namespace identity is visible; additional accepted namespace identities remain at the native location.",
        "/xml_name_alternatives",
    )?;
    add(
        schema.xml_wildcard_namespace.is_some(),
        "schema.xmlWildcard",
        "XML wildcard namespace and process rules remain at the native location.",
        "/xml_wildcard_namespace",
    )?;
    add(
        schema.xml_wildcard_namespace.is_none()
            && schema.xml_wildcard_process_contents != XmlWildcardProcessContents::Skip,
        "schema.xmlWildcardProcess",
        "The XML wildcard process policy remains at the native location.",
        "/xml_wildcard_process_contents",
    )?;
    add(
        schema.text,
        "schema.xmlText",
        "This field is XML simple text content; the v1 path retains its native field identity without a dedicated text-content segment.",
        "/text",
    )?;
    add(
        options.json_document && schema.repeating && schema.nullable,
        "schema.arrayItemNullability",
        "Array-container nullability is visible; nullable scalar items remain a distinct native fact that v1 cannot express.",
        "/nullable",
    )?;
    add(
        options.json_document && schema.json_object_item_nullable.is_some(),
        "schema.objectItemNullability",
        "Array-container nullability is visible; the explicit nullability of each object item remains a distinct native fact that v1 cannot express.",
        "/json_object_item_nullable",
    )?;
    add(
        schema.json_undeclared_property,
        "schema.undeclaredProperty",
        "This property is admitted by its parent's dynamic value schema rather than declared; that provenance remains at the native location.",
        "/json_undeclared_property",
    )?;
    add(
        options.xml_document && schema.repeating && schema.nillable,
        "schema.xmlOccurrenceNullability",
        "XML nil applies to each repeated element occurrence; it does not establish nullability of an array container.",
        "/nillable",
    )?;
    add(
        schema.fixed.is_some(),
        "schema.fixedValue",
        "The schema requires a fixed lexical value retained at the native location.",
        "/fixed",
    )?;
    add(
        schema.default.is_some(),
        "schema.defaultValue",
        "The schema has a format-owned lexical default retained at the native location.",
        "/default",
    )?;
    add(
        schema.value_generation.is_some(),
        "schema.generatedValue",
        "The schema has format-owned value generation retained at the native location.",
        "/value_generation",
    )?;
    add(
        schema.json_allowed_values.is_some(),
        "schema.allowedValues",
        "The exact set of allowed scalar values remains at the native location.",
        "/json_allowed_values",
    )?;
    add(
        schema.numeric_range.is_some(),
        "schema.numericRange",
        "The exact numeric interval remains at the native location.",
        "/numeric_range",
    )?;
    add(
        schema.json_multiple_of.is_some(),
        "schema.numericMultiples",
        "The exact numeric divisibility constraints remain at the native location.",
        "/json_multiple_of",
    )?;
    add(
        schema.json_contains.is_some(),
        "schema.arrayContains",
        "The array contains predicates and their independent cardinalities remain at the native location.",
        "/json_contains",
    )?;
    add(
        schema.json_unique_items,
        "schema.uniqueItems",
        "The array requires structurally distinct items, as retained at the native location.",
        "/json_unique_items",
    )?;
    add(
        schema.json_dependent_schemas.is_some(),
        "schema.dependentSchemas",
        "The conditional whole-object assertions remain at the native location.",
        "/json_dependent_schemas",
    )?;
    add(
        schema.property_count_range.is_some(),
        "schema.propertyCount",
        "The exact object property-count interval remains at the native location.",
        "/property_count_range",
    )?;
    add(
        schema.json_property_dependencies.is_some(),
        "schema.propertyDependencies",
        "The object property-presence implications remain at the native location.",
        "/json_property_dependencies",
    )?;
    add(
        schema.json_pattern_property_names.is_some(),
        "schema.patternProperties",
        "The runtime property-name selectors remain at the native location.",
        "/json_pattern_property_names",
    )?;
    add(
        schema.json_property_names.is_some(),
        "schema.propertyNames",
        "The constraints on runtime property names remain at the native location.",
        "/json_property_names",
    )?;
    add(
        schema.string_length_range.is_some(),
        "schema.stringLength",
        "The exact string-length interval remains at the native location.",
        "/string_length_range",
    )?;
    add(
        schema.json_patterns.is_some(),
        "schema.stringPatterns",
        "The string pattern constraints remain at the native location.",
        "/json_patterns",
    )?;
    add(
        !schema.json_formats.is_empty(),
        "schema.stringFormats",
        "The ordered string format annotations remain at the native location.",
        "/json_formats",
    )?;
    add(
        schema.xml_type_alternatives || schema.xml_default_type.is_some(),
        "schema.xmlTypeIdentity",
        "XML type-selection metadata remains at the native location.",
        if schema.xml_default_type.is_some() {
            "/xml_default_type"
        } else {
            "/xml_type_alternatives"
        },
    )?;
    add(
        !schema.xml_repeating_sequences.is_empty(),
        "schema.xmlSequences",
        "The flattened XML repeating sequence and document-order rules remain at the native location.",
        "/xml_repeating_sequences",
    )?;
    add(
        !schema.xml_repeating_choices.is_empty(),
        "schema.xmlChoices",
        "The flattened XML choice and cross-member occurrence order remain at the native location.",
        "/xml_repeating_choices",
    )?;
    add(
        schema.database_relation.is_some(),
        "schema.databaseRelation",
        "The database relation endpoints remain at the native location.",
        "/database_relation",
    )?;
    if let SchemaKind::Group {
        alternatives,
        dynamic,
        xml_restricted_alternatives,
        ..
    } = &schema.kind
    {
        add(
            !alternatives.is_empty(),
            "schema.groupAlternatives",
            "The visible fields merge compatible alternatives; alternative membership, requirements, constraints, and selection semantics remain at the native location.",
            "/kind/alternatives",
        )?;
        add(
            !xml_restricted_alternatives.is_empty(),
            "schema.restrictedAlternatives",
            "The XML restriction-derived alternative identities remain at the native location.",
            "/kind/xml_restricted_alternatives",
        )?;
        add(
            dynamic.is_some(),
            "schema.dynamicProperties",
            "Runtime-named property values use the native dynamic schema; no fixed property name is synthesized in this inventory.",
            "/kind/dynamic",
        )?;
    }
    Ok(())
}
