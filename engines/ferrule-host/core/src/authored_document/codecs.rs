use super::{Failure, Format, MAX_DEPTH, MAX_NODES};
use ferrule_format_edi::{edifact, x12};
use ferrule_ir::{Instance, SchemaNode};
use ferrule_mapping::FormatOptions;

fn separators(o: &FormatOptions) -> Option<x12::Separators> {
    o.x12_separators.as_ref().map(|s| x12::Separators {
        element: s.element,
        component: s.component,
        segment: s.segment,
        repetition: s.repetition,
        release: s.release,
    })
}

pub(super) fn read(
    f: Format,
    bytes: &[u8],
    schema: &SchemaNode,
    o: &FormatOptions,
) -> Result<Instance, Failure> {
    let text = std::str::from_utf8(bytes).map_err(|_| Failure::InvalidInput)?;
    match f {
        Format::Json => {
            let text = text.strip_prefix('\u{feff}').unwrap_or(text);
            let raw: serde_json::Value =
                serde_json::from_str(text).map_err(|_| Failure::InvalidInput)?;
            let mut pending = vec![(&raw, 0)];
            let mut nodes = 0;
            while let Some((v, depth)) = pending.pop() {
                nodes += 1;
                if nodes > MAX_NODES || depth > MAX_DEPTH {
                    return Err(Failure::InputLimit);
                }
                match v {
                    serde_json::Value::Array(a) => pending.extend(a.iter().map(|v| (v, depth + 1))),
                    serde_json::Value::Object(a) => {
                        pending.extend(a.values().map(|v| (v, depth + 1)))
                    }
                    _ => (),
                }
            }
            json_projection_bound(&raw, schema).map_err(|_| Failure::InputLimit)?;
            ferrule_format_json::from_str(text, schema).map_err(|_| Failure::InvalidInput)
        }
        Format::Xml => ferrule_format_xml::from_str_structured(text, schema).map_err(|e| match e {
            ferrule_format_xml::XmlFormatError::StructuredInputLimit { .. }
            | ferrule_format_xml::XmlFormatError::StructuredParserResourceLimit { .. } => {
                Failure::InputLimit
            }
            _ => Failure::InvalidInput,
        }),
        Format::Csv | Format::Tsv => {
            ferrule_format_csv::from_bytes_with_options_bounded(bytes, schema, &o.into())
                .map(Instance::Repeated)
                .map_err(|e| match e {
                    ferrule_format_csv::CsvReadError::Limit { .. } => Failure::InputLimit,
                    _ => Failure::InvalidInput,
                })
        }
        Format::X12 => {
            x12_envelope(text, o).map_err(|_| Failure::InvalidInput)?;
            edi_projection_bound(
                schema,
                x12::tokenize(text)
                    .map_err(|_| Failure::InvalidInput)?
                    .len(),
            )
            .map_err(|_| Failure::InputLimit)?;
            x12::from_str_with_separators(text, schema, false, separators(o))
                .map_err(|_| Failure::InvalidInput)
        }
        Format::Edifact => {
            edifact::validate_envelope(text).map_err(|_| Failure::InvalidInput)?;
            edi_projection_bound(
                schema,
                edifact::tokenize(text)
                    .map_err(|_| Failure::InvalidInput)?
                    .len(),
            )
            .map_err(|_| Failure::InputLimit)?;
            edifact::from_str(text, schema, false).map_err(|_| Failure::InvalidInput)
        }
    }
}

fn edi_projection_bound(schema: &SchemaNode, segments: usize) -> Result<(), ()> {
    let mut schemas = vec![schema];
    let mut count = 0usize;
    while let Some(s) = schemas.pop() {
        count += 1;
        if let ferrule_ir::SchemaKind::Group { children, .. } = &s.kind {
            schemas.extend(children);
        }
    }
    if count.saturating_mul(segments) > MAX_NODES {
        Err(())
    } else {
        Ok(())
    }
}

fn json_projection_bound(raw: &serde_json::Value, schema: &SchemaNode) -> Result<(), ()> {
    let mut pending = vec![(schema, raw, 0usize)];
    let mut nodes = 0usize;
    while let Some((s, value, depth)) = pending.pop() {
        nodes += 1;
        if nodes > MAX_NODES || depth > MAX_DEPTH {
            return Err(());
        }
        if let serde_json::Value::Array(items) = value {
            pending.extend(items.iter().map(|value| (s, value, depth + 1)));
        } else if let ferrule_ir::SchemaKind::Group { children, .. } = &s.kind {
            for child in children {
                pending.push((
                    child,
                    value.get(&child.name).unwrap_or(&serde_json::Value::Null),
                    depth + 1,
                ));
            }
        }
    }
    Ok(())
}

pub(super) fn write(
    f: Format,
    instance: &Instance,
    schema: &SchemaNode,
    o: &FormatOptions,
    max: usize,
) -> Result<Vec<u8>, Failure> {
    render_bound(schema, instance)?;
    let result = match f {
        Format::Json => {
            ferrule_format_json::to_string(schema, instance).map_err(|_| Failure::InvalidOutput)
        }
        Format::Xml => {
            ferrule_format_xml::to_string(schema, instance).map_err(|_| Failure::InvalidOutput)
        }
        Format::Csv | Format::Tsv => {
            let rows = match instance {
                Instance::Repeated(rows) => rows.as_slice(),
                _ => std::slice::from_ref(instance),
            };
            return ferrule_format_csv::to_bytes_with_options_bounded(schema, rows, &o.into(), max)
                .map_err(|e| match e {
                    ferrule_format_csv::CsvBoundedError::OutputTooLarge { .. } => {
                        Failure::OutputLimit
                    }
                    _ => Failure::InvalidOutput,
                });
        }
        Format::X12 => x12::to_document_string_with_syntax(
            schema,
            instance,
            separators(o).unwrap_or_default(),
            o.x12_interchange_version.as_deref(),
            None,
        )
        .map_err(|_| Failure::InvalidOutput),
        Format::Edifact => edifact::to_string(schema, instance).map_err(|_| Failure::InvalidOutput),
    }?;
    if result.len() > max {
        return Err(Failure::OutputLimit);
    }
    Ok(result.into_bytes())
}

// These pinned String-returning codecs have no Write sink. Bound their complete
// schema-shaped expansion BEFORE rendering, then enforce the exact returned byte
// ceiling. This reservation deliberately overcounts absent fields and escaping.
// CSV additionally has its own bounded sink. This is not a process RSS bound.
fn render_bound(schema: &SchemaNode, instance: &Instance) -> Result<(), Failure> {
    use ferrule_ir::{SchemaKind, Value};
    let mut pending = vec![(schema, Some(instance), 0usize)];
    let mut nodes = 0usize;
    let mut bytes = 0usize;
    while let Some((schema, instance, depth)) = pending.pop() {
        nodes += 1;
        if nodes > MAX_NODES || depth > MAX_DEPTH {
            return Err(Failure::OutputLimit);
        }
        if let Some(Instance::Repeated(items)) = instance {
            pending.extend(items.iter().map(|v| (schema, Some(v), depth)));
            continue;
        }
        let namespace = schema
            .xml_namespace
            .as_ref()
            .and_then(|n| n.uri())
            .map_or(0, str::len);
        bytes = bytes.saturating_add((schema.name.len() + namespace).saturating_mul(12) + 512);
        match &schema.kind {
            SchemaKind::Group { children, .. } => {
                for child in children {
                    let value = instance.and_then(|v| v.field(&child.name));
                    pending.push((child, value, depth + 1));
                }
            }
            _ => {
                if let Some(Instance::Scalar(Value::String(s))) = instance {
                    bytes = bytes.saturating_add(s.len().saturating_mul(6));
                }
            }
        }
        if bytes > 64 * 1024 * 1024 {
            return Err(Failure::OutputLimit);
        }
    }
    Ok(())
}

// The pinned X12 tokenizer validates ISA fixed positions and version syntax, but
// its instance reader does not validate envelope ownership or trailer counts.
fn x12_envelope(text: &str, options: &FormatOptions) -> Result<(), ()> {
    let segments = x12::tokenize(text).map_err(|_| ())?;
    if segments.is_empty() || segments.len() > MAX_NODES {
        return Err(());
    }
    let mut interchange: Option<String> = None;
    let mut group: Option<String> = None;
    let mut message: Option<(String, usize)> = None;
    let mut groups = 0usize;
    let mut messages = 0usize;
    for (index, segment) in segments.iter().enumerate() {
        let field = |n: usize| -> Result<&str, ()> {
            let repeats = segment.elements.get(n - 1).ok_or(())?;
            if repeats.len() != 1 || repeats[0].len() != 1 || repeats[0][0].is_empty() {
                return Err(());
            }
            Ok(&repeats[0][0])
        };
        let count = |n: usize, expected: usize| -> Result<(), ()> {
            let value = field(n)?;
            if value.bytes().all(|b| b.is_ascii_digit()) && value.parse::<usize>() == Ok(expected) {
                Ok(())
            } else {
                Err(())
            }
        };
        match segment.id.as_str() {
            "ISA" if interchange.is_none() && group.is_none() && message.is_none() => {
                interchange = Some(field(13)?.into());
                groups = 0;
                if options
                    .x12_interchange_version
                    .as_ref()
                    .is_some_and(|v| field(12) != Ok(v.as_str()))
                {
                    return Err(());
                }
            }
            "GS" if interchange.is_some() && group.is_none() && message.is_none() => {
                group = Some(field(6)?.into());
                groups += 1;
                messages = 0;
            }
            "ST" if group.is_some() && message.is_none() => {
                message = Some((field(2)?.into(), index));
            }
            "SE" => {
                let (control, start) = message.take().ok_or(())?;
                count(1, index - start + 1)?;
                if field(2)? != control {
                    return Err(());
                }
                messages += 1;
            }
            "GE" if message.is_none() => {
                let control = group.take().ok_or(())?;
                count(1, messages)?;
                if messages == 0 || field(2)? != control {
                    return Err(());
                }
            }
            "IEA" if group.is_none() && message.is_none() => {
                let control = interchange.take().ok_or(())?;
                count(1, groups)?;
                if groups == 0 || field(2)? != control {
                    return Err(());
                }
            }
            "ISA" | "GS" | "ST" | "GE" | "IEA" => return Err(()),
            _ if message.is_some() => (),
            _ => return Err(()),
        }
    }
    if interchange.is_some() || group.is_some() || message.is_some() {
        Err(())
    } else {
        Ok(())
    }
}
