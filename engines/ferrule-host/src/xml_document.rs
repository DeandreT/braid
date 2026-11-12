//! Dormant XML core. This module does not register a protocol, dispatch a mapping,
//! publish output, or advertise an XML capability. Admission is not qualification.
use std::collections::BTreeSet;
use std::fmt;
use std::io::{self, Write};

use ferrule_ir::{SchemaKind, SchemaNode, XmlSchemaHints, XmlSchemaLocation};
use ferrule_mapping::{FormatOptions, Project};
use serde::de::{IgnoredAny, MapAccess, Visitor};
use serde::{Deserialize, Deserializer, Serialize};

#[path = "xml_safety.rs"]
mod safety;
pub use safety::{DocumentStats, Safety};

pub const CAPABILITY: &str = "weft.xml.structured-document.v1";
pub const METADATA_SCHEMA: &str = "weft.xml-boundary.v1";
pub const SAFETY_PROFILE: &str = "weft.xml-safe.v1";
pub const MAX_DOCUMENT_BYTES: usize = 8 * 1024 * 1024;
pub const MAX_METADATA_BYTES: usize = 32 * 1024;
pub const MAX_DEPTH: usize = 64;
pub const MAX_NODES: usize = 1_000_000;
pub const MAX_ATTRIBUTES: usize = 256;
pub const MAX_SCHEMA_NODES: usize = 8192;
pub const MAX_INPUTS: usize = 16;
pub const MAX_OUTPUTS: usize = 64;

/// Fixed public categories only. Never retain upstream messages, names, URIs,
/// XML values, or parser errors in a failure returned from this module.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Resource {
    DocumentBytes,
    AggregateBytes,
    DocumentCount,
    PhysicalDepth,
    PhysicalNodes,
    Attributes,
    SchemaDepth,
    SchemaNodes,
    MetadataBytes,
    NativeBytes,
    StructuredParser,
    StructuredProjection,
    StructuredBudget,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Error {
    InvalidInput,
    InvalidEncoding,
    InvalidDeclaration,
    DtdProhibited,
    UnsupportedName,
    InvalidLimits,
    InvalidNative,
    InvalidOptions,
    InvalidMetadata,
    UnsupportedBoundary,
    Cancelled,
    ResourceLimit(Resource),
}

impl fmt::Display for Error {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(match self {
            Self::InvalidInput => "The XML document is not valid for this boundary.",
            Self::InvalidEncoding => "XML requires strict UTF-8 encoding.",
            Self::InvalidDeclaration => "The XML declaration is outside the XML 1.0 UTF-8 profile.",
            Self::DtdProhibited => "XML document type declarations are prohibited.",
            Self::UnsupportedName => "XML names are outside the supported profile.",
            Self::InvalidLimits => "The XML limits are outside the admitted range.",
            Self::InvalidNative => "The native XML mapping is invalid.",
            Self::InvalidOptions => "The saved options are outside the XML profile.",
            Self::InvalidMetadata => "The XML metadata is invalid for this boundary.",
            Self::UnsupportedBoundary => "The mapping is outside the closed static XML profile.",
            Self::Cancelled => "XML validation was cancelled.",
            Self::ResourceLimit(_) => "The XML operation exceeds a resource limit.",
        })
    }
}
impl std::error::Error for Error {}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Role {
    Input,
    Output,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct SchemaHints {
    pub no_namespace_location: Option<String>,
    pub locations: Vec<SchemaLocation>,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct SchemaLocation {
    pub namespace: String,
    pub location: String,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Metadata {
    pub schema: String,
    pub safety_profile: String,
    // Deliberately explicit null. Legacy descriptors must not use this type.
    pub schema_hints: Option<SchemaHints>,
}

// Derived struct visitors also accept positional sequences. Constrain the
// entry point to a map, then let the private derived visitor enforce required,
// unknown and duplicate fields without a Value round-trip that loses keys.
macro_rules! deserialize_object {
    ($name:ident { $($(#[$attribute:meta])* $field:ident: $ty:ty),* $(,)? }) => {
        impl<'de> Deserialize<'de> for $name {
            fn deserialize<D: Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
                #[derive(Deserialize)]
                #[serde(rename_all = "camelCase", deny_unknown_fields)]
                struct Object { $($(#[$attribute])* $field: $ty),* }

                struct ObjectVisitor;
                impl<'de> Visitor<'de> for ObjectVisitor {
                    type Value = $name;
                    fn expecting(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
                        f.write_str("an XML metadata object")
                    }
                    fn visit_map<M: MapAccess<'de>>(self, map: M) -> Result<Self::Value, M::Error> {
                        let value = Object::deserialize(serde::de::value::MapAccessDeserializer::new(map))?;
                        Ok($name { $($field: value.$field),* })
                    }
                }
                deserializer.deserialize_map(ObjectVisitor)
            }
        }
    };
}

deserialize_object!(Metadata {
    schema: String,
    safety_profile: String,
    #[serde(deserialize_with = "required_nullable")]
    schema_hints: Option<SchemaHints>,
});
deserialize_object!(SchemaHints {
    #[serde(deserialize_with = "required_nullable")]
    no_namespace_location: Option<String>,
    locations: Vec<SchemaLocation>,
});
deserialize_object!(SchemaLocation {
    namespace: String,
    location: String,
});

fn required_nullable<'de, D, T>(deserializer: D) -> Result<Option<T>, D::Error>
where
    D: Deserializer<'de>,
    T: Deserialize<'de>,
{
    Option::<T>::deserialize(deserializer)
}

impl Metadata {
    pub fn from_options(options: &FormatOptions, role: Role) -> Result<Self, Error> {
        let hints = options.xml_schema_hints.as_ref();
        // Validate before cloning the bounded hint strings.
        options
            .validate_xml_schema_hint_options(role == Role::Output)
            .map_err(|_| Error::InvalidOptions)?;
        if let Some(hints) = hints {
            hints.validate().map_err(|_| Error::InvalidOptions)?;
        }
        let expected = FormatOptions {
            xml_document: true,
            xml_schema_hints: if role == Role::Output {
                hints.cloned()
            } else {
                None
            },
            ..Default::default()
        };
        if *options != expected {
            return Err(Error::InvalidOptions);
        }
        Ok(Self {
            schema: METADATA_SCHEMA.into(),
            safety_profile: SAFETY_PROFILE.into(),
            schema_hints: hints.map(|hints| SchemaHints {
                no_namespace_location: hints.no_namespace_location.clone(),
                locations: hints
                    .locations
                    .iter()
                    .map(|pair| SchemaLocation {
                        namespace: pair.namespace.clone(),
                        location: pair.location.clone(),
                    })
                    .collect(),
            }),
        })
    }

    pub fn validate(&self, role: Role) -> Result<(), Error> {
        if self.schema != METADATA_SCHEMA
            || self.safety_profile != SAFETY_PROFILE
            || (role == Role::Input && self.schema_hints.is_some())
        {
            return Err(Error::InvalidMetadata);
        }
        if let Some(hints) = &self.schema_hints {
            // Refuse oversized untrusted metadata before making a second copy.
            if hints.locations.len() > 32
                || hints
                    .no_namespace_location
                    .as_ref()
                    .is_some_and(|v| v.len() > 4096)
                || hints
                    .locations
                    .iter()
                    .any(|p| p.namespace.len() > 4096 || p.location.len() > 4096)
            {
                return Err(Error::InvalidMetadata);
            }
            XmlSchemaHints {
                no_namespace_location: hints.no_namespace_location.clone(),
                locations: hints
                    .locations
                    .iter()
                    .map(|p| XmlSchemaLocation {
                        namespace: p.namespace.clone(),
                        location: p.location.clone(),
                    })
                    .collect(),
            }
            .validate()
            .map_err(|_| Error::InvalidMetadata)?;
        }
        Ok(())
    }

    /// Shared reservation: max(actual serde_json wire bytes, pinned .NET
    /// JavaScriptEncoder.Default canonical bytes). Both include the complete
    /// property name, colon and reserved comma (15 bytes), with no deduplication.
    /// The .NET form is never shorter for these string/null/array/object records.
    /// Count both capped encodings nevertheless, so the native writer cannot
    /// silently outgrow the reservation if its encoding changes.
    pub fn encoded_property_bytes(&self) -> Result<usize, Error> {
        let mut native = CappedWriter::new(io::sink(), MAX_METADATA_BYTES);
        self.write_property(&mut native)?;
        let mut canonical = CappedWriter::new(io::sink(), MAX_METADATA_BYTES);
        self.write_property_with_formatter(&mut canonical, DotNetDefaultFormatter)?;
        Ok(native.written.max(canonical.written))
    }

    fn write_property<W: Write>(&self, writer: &mut CappedWriter<W>) -> Result<(), Error> {
        self.write_property_with_formatter(writer, serde_json::ser::CompactFormatter)
    }

    fn write_property_with_formatter<W: Write, F: serde_json::ser::Formatter>(
        &self,
        writer: &mut CappedWriter<W>,
        formatter: F,
    ) -> Result<(), Error> {
        writer
            .write_all(b"\"xmlMetadata\":")
            .map_err(metadata_write_error)?;
        self.serialize(&mut serde_json::Serializer::with_formatter(
            &mut *writer,
            formatter,
        ))
        .map_err(|_| Error::ResourceLimit(Resource::MetadataBytes))?;
        writer.write_all(b",").map_err(metadata_write_error)
    }

    /// Writes unchanged native serde_json bytes after checking the larger shared
    /// reservation. Returns actual bytes written, which can be less than
    /// encoded_property_bytes(). Count/validate the entire table before publishing.
    pub fn write_encoded_property(
        &self,
        destination: &mut impl Write,
        maximum: usize,
    ) -> Result<usize, Error> {
        if maximum > MAX_METADATA_BYTES {
            return Err(Error::InvalidLimits);
        }
        // Refuse before a byte reaches the destination if it cannot fit.
        let count = self.encoded_property_bytes()?;
        if count > maximum {
            return Err(Error::ResourceLimit(Resource::MetadataBytes));
        }
        let mut writer = CappedWriter::new(destination, maximum);
        self.write_property(&mut writer)?;
        Ok(writer.written)
    }
}

// Exact string-escaping policy of the pinned .NET metadata codec, also used by
// weft.build_json_text for its admitted noncontrol strings. This is a counting
// formatter only, not a replacement wire encoding or a general .NET serializer.
// Default permits printable ASCII except JSON/HTML-sensitive characters,
// plus signs and backticks; backslashes use their short JSON escape. It
// escapes every other BMP scalar as one UTF-16 \uXXXX, and every supplementary
// scalar as a surrogate pair. Short JSON control escapes and \\ remain 2 bytes.
// A .NET encoder/runtime change requires new cross-language golden qualification;
// do not broaden this allowlist or substitute the relaxed encoder unilaterally.
struct DotNetDefaultFormatter;

impl serde_json::ser::Formatter for DotNetDefaultFormatter {
    fn write_string_fragment<W: ?Sized + Write>(
        &mut self,
        writer: &mut W,
        fragment: &str,
    ) -> io::Result<()> {
        let mut unescaped = 0;
        for (offset, ch) in fragment.char_indices() {
            if matches!(ch, ' '..='~')
                && !matches!(ch, '"' | '\\' | '&' | '\'' | '+' | '<' | '>' | '`')
            {
                continue;
            }
            writer.write_all(&fragment.as_bytes()[unescaped..offset])?;
            self.write_scalar(writer, ch)?;
            unescaped = offset + ch.len_utf8();
        }
        writer.write_all(&fragment.as_bytes()[unescaped..])
    }

    fn write_char_escape<W: ?Sized + Write>(
        &mut self,
        writer: &mut W,
        escape: serde_json::ser::CharEscape,
    ) -> io::Result<()> {
        use serde_json::ser::CharEscape;
        let ch = match escape {
            CharEscape::Quote => '"',
            CharEscape::ReverseSolidus => '\\',
            CharEscape::Solidus => '/',
            CharEscape::Backspace => '\u{8}',
            CharEscape::FormFeed => '\u{c}',
            CharEscape::LineFeed => '\n',
            CharEscape::CarriageReturn => '\r',
            CharEscape::Tab => '\t',
            CharEscape::AsciiControl(byte) => char::from(byte),
        };
        self.write_scalar(writer, ch)
    }
}

impl DotNetDefaultFormatter {
    fn write_scalar<W: ?Sized + Write>(&self, writer: &mut W, ch: char) -> io::Result<()> {
        let short = match ch {
            '\\' => Some(b'\\'),
            '\u{8}' => Some(b'b'),
            '\u{c}' => Some(b'f'),
            '\n' => Some(b'n'),
            '\r' => Some(b'r'),
            '\t' => Some(b't'),
            _ => None,
        };
        if let Some(short) = short {
            return writer.write_all(&[b'\\', short]);
        }
        if matches!(ch, ' '..='~') && !matches!(ch, '"' | '&' | '\'' | '+' | '<' | '>' | '`') {
            return writer.write_all(&[ch as u8]);
        }
        let mut units = [0u16; 2];
        for &unit in ch.encode_utf16(&mut units).iter() {
            const HEX: &[u8; 16] = b"0123456789ABCDEF";
            writer.write_all(&[
                b'\\',
                b'u',
                HEX[usize::from(unit >> 12)],
                HEX[usize::from((unit >> 8) & 15)],
                HEX[usize::from((unit >> 4) & 15)],
                HEX[usize::from(unit & 15)],
            ])?;
        }
        Ok(())
    }
}

fn metadata_write_error(_: io::Error) -> Error {
    Error::ResourceLimit(Resource::MetadataBytes)
}

struct CappedWriter<W> {
    inner: W,
    written: usize,
    maximum: usize,
}
impl<W> CappedWriter<W> {
    fn new(inner: W, maximum: usize) -> Self {
        Self {
            inner,
            written: 0,
            maximum,
        }
    }
}
impl<W: Write> Write for CappedWriter<W> {
    fn write(&mut self, bytes: &[u8]) -> io::Result<usize> {
        let next = self
            .written
            .checked_add(bytes.len())
            .filter(|next| *next <= self.maximum)
            .ok_or_else(|| io::Error::other("XML metadata limit"))?;
        self.inner.write_all(bytes)?;
        self.written = next;
        Ok(bytes.len())
    }
    fn flush(&mut self) -> io::Result<()> {
        self.inner.flush()
    }
}

/// One ledger across all declared inputs AND outputs, never selected subsets.
/// There is no deduplication discount for repeated metadata.
pub fn metadata_ledger(inputs: &[Metadata], outputs: &[Metadata]) -> Result<usize, Error> {
    if inputs.is_empty()
        || outputs.is_empty()
        || inputs.len() > MAX_INPUTS
        || outputs.len() > MAX_OUTPUTS
    {
        return Err(Error::ResourceLimit(Resource::DocumentCount));
    }
    let mut total = 0usize;
    for (table, role) in [(inputs, Role::Input), (outputs, Role::Output)] {
        for metadata in table {
            metadata.validate(role)?;
            total = total
                .checked_add(metadata.encoded_property_bytes()?)
                .filter(|total| *total <= MAX_METADATA_BYTES)
                .ok_or(Error::ResourceLimit(Resource::MetadataBytes))?;
        }
    }
    Ok(total)
}

pub struct Admission {
    /// Lowered only once, after all declaration checks. Reuse for emission.
    pub program: ferrule_codegen::Program,
    pub inputs: Vec<Metadata>,
    pub outputs: Vec<Metadata>,
    pub metadata_bytes: usize,
}

/// New-profile-only decoding. The legacy decoder is deliberately unchanged.
/// The caller still owns native digest/revision/authorization and envelope caps.
/// C# XmlNativeMetadata is metadata projection, not a replacement for this
/// complete schema and emission-plan admission.
pub fn admit_native(native: &[u8]) -> Result<(crate::decode::DecodedProject, Admission), Error> {
    validate_raw_options(native)?;
    let decoded = crate::decode::decode_native(native).map_err(|_| Error::InvalidNative)?;
    let admission = admit_project(&decoded.project)?;
    Ok((decoded, admission))
}

fn admit_project(project: &Project) -> Result<Admission, Error> {
    if project.extra_sources.len() >= MAX_INPUTS || project.extra_targets.len() >= MAX_OUTPUTS {
        return Err(Error::ResourceLimit(Resource::DocumentCount));
    }
    let mut input_names = BTreeSet::new();
    let mut output_names = BTreeSet::new();
    if project.root.iteration.iterates() || project.root.output_path().is_some() {
        return Err(Error::UnsupportedBoundary);
    }
    let mut inputs = vec![Metadata::from_options(
        &project.source_options,
        Role::Input,
    )?];
    let mut outputs = vec![Metadata::from_options(
        &project.target_options,
        Role::Output,
    )?];
    bounded_closed_schema(&project.source)?;
    bounded_closed_schema(&project.target)?;
    for source in &project.extra_sources {
        if source.dynamic_path.is_some()
            || !valid_name(&source.name)
            || !input_names.insert(&source.name)
        {
            return Err(Error::UnsupportedBoundary);
        }
        bounded_closed_schema(&source.schema)?;
        inputs.push(Metadata::from_options(&source.options, Role::Input)?);
    }
    for target in &project.extra_targets {
        if target.root.iteration.iterates()
            || target.root.output_path().is_some()
            || !valid_name(&target.name)
            || !output_names.insert(&target.name)
        {
            return Err(Error::UnsupportedBoundary);
        }
        bounded_closed_schema(&target.schema)?;
        outputs.push(Metadata::from_options(&target.options, Role::Output)?);
    }
    let metadata_bytes = metadata_ledger(&inputs, &outputs)?;
    let program = ferrule_codegen::lower(project).map_err(|_| Error::UnsupportedBoundary)?;
    let plan = ferrule_codegen::prepare_boundary_emission(
        &program,
        ferrule_codegen::CompanionSelection::Complete,
    )
    .map_err(|_| Error::UnsupportedBoundary)?;
    let xml = plan.xml().ok_or(Error::UnsupportedBoundary)?;
    if xml.inputs() != ferrule_codegen::XmlDocumentInputs::Structured
        || xml.mode() != ferrule_codegen::XmlOutputMode::SingleDocument
        || xml.primary_output() != ferrule_codegen::XmlDocumentCardinality::Single
        || xml.named_outputs().len() != project.extra_targets.len()
        || xml
            .named_outputs()
            .iter()
            .any(|mode| *mode != ferrule_codegen::XmlDocumentCardinality::Single)
        || xml.selected_inputs().is_none()
    {
        return Err(Error::UnsupportedBoundary);
    }
    Ok(Admission {
        program,
        inputs,
        outputs,
        metadata_bytes,
    })
}

fn valid_name(name: &str) -> bool {
    !name.is_empty() && name.len() <= 256 && !name.chars().any(char::is_control)
}

/// Iterative bound before the upstream recursive support proof or lowerer.
pub fn bounded_closed_schema(root: &SchemaNode) -> Result<(), Error> {
    let mut pending = vec![(root, 1usize)];
    let mut nodes = 0usize;
    while let Some((node, depth)) = pending.pop() {
        if depth > MAX_DEPTH {
            return Err(Error::ResourceLimit(Resource::SchemaDepth));
        }
        nodes = nodes
            .checked_add(1)
            .ok_or(Error::ResourceLimit(Resource::SchemaNodes))?;
        if nodes > MAX_SCHEMA_NODES {
            return Err(Error::ResourceLimit(Resource::SchemaNodes));
        }
        // These can contain nested schemas. Refuse without copying/traversing
        // them; the closed XML proof refuses these metadata forms as well.
        if node.json_contains.is_some() || node.json_dependent_schemas.is_some() {
            return Err(Error::UnsupportedBoundary);
        }
        if let SchemaKind::Group {
            children,
            dynamic,
            alternatives,
            xml_restricted_alternatives,
            ..
        } = &node.kind
        {
            if dynamic.is_some()
                || !alternatives.is_empty()
                || !xml_restricted_alternatives.is_empty()
            {
                return Err(Error::UnsupportedBoundary);
            }
            if nodes
                .checked_add(pending.len())
                .and_then(|n| n.checked_add(children.len()))
                .is_none_or(|n| n > MAX_SCHEMA_NODES)
            {
                return Err(Error::ResourceLimit(Resource::SchemaNodes));
            }
            pending.extend(children.iter().map(|child| (child, depth + 1)));
        }
    }
    if !ferrule_ir::xml_structured_document_input_is_supported(root) {
        return Err(Error::UnsupportedBoundary);
    }
    // At the exact pin, the Structured proof establishes that every non-text
    // SchemaNode.name is a physical local NCName (including attributes), and
    // the only text role is the scalar #text pseudo-field. The generated
    // Structured descriptor preserves these same roles/names; lower clones
    // these schemas without translating their XML identity. Apply the shared
    // physical-name profile only after that proof, never to arbitrary field
    // strings, URI values, or unproved generic/mixed-content pseudo-fields.
    // Namespace metadata stores URIs, not authored prefixes. The pinned writers
    // synthesize xml/fnsN attribute prefixes; colon-bearing schema names are
    // already refused by the Structured proof, not split or silently repaired.
    pending.push((root, 1));
    while let Some((node, depth)) = pending.pop() {
        if !node.text && !safety::ncname_is_supported(&node.name) {
            return Err(Error::UnsupportedBoundary);
        }
        if let SchemaKind::Group { children, .. } = &node.kind {
            pending.extend(children.iter().map(|child| (child, depth + 1)));
        }
    }
    Ok(())
}

/// Safety preflight followed by the pinned structured reader. The temporary
/// instance is dropped; Ferrule remains the only mapping engine.
pub fn validate_structured(
    bytes: &[u8],
    schema: &SchemaNode,
    maximum_bytes: usize,
) -> Result<DocumentStats, Error> {
    let stats = Safety::new(maximum_bytes)?.validate_bytes(bytes)?;
    bounded_closed_schema(schema)?;
    let text = std::str::from_utf8(bytes).map_err(|_| Error::InvalidEncoding)?;
    ferrule_format_xml::from_str_structured(text.strip_prefix('\u{feff}').unwrap_or(text), schema)
        .map_err(|error| match error {
            ferrule_format_xml::XmlFormatError::StructuredInputLimit { budget, .. } => {
                // These are the typed variant's pinned static budget IDs, not
                // matches against an exception's rendered text or XML content.
                Error::ResourceLimit(match budget {
                    "document bytes" => Resource::DocumentBytes,
                    "physical depth" => Resource::PhysicalDepth,
                    "materialized nodes"
                    | "materialized field, string and text bytes"
                    | "projection work" => Resource::StructuredProjection,
                    _ => Resource::StructuredBudget,
                })
            }
            ferrule_format_xml::XmlFormatError::StructuredParserResourceLimit { .. } => {
                Error::ResourceLimit(Resource::StructuredParser)
            }
            ferrule_format_xml::XmlFormatError::UnsupportedStructuredInputSchema => {
                Error::UnsupportedBoundary
            }
            _ => Error::InvalidInput,
        })?;
    Ok(stats)
}

// Exact inventory at the pinned FormatOptions revision. Do not derive this from
// serialization: default-valued optional fields are deliberately omitted there.
const OPTION_KEYS: &[&str] = &[
    "lenient_segments",
    "edi_kind",
    "edi_config_reference",
    "edi_implied_decimals",
    "edi_lexical_formats",
    "edi_value_constraints",
    "edi_autocomplete",
    "x12_separators",
    "x12_interchange_version",
    "idoc",
    "idoc_native_config",
    "idoc_native_text_settings",
    "swift_mt",
    "xml_document",
    "xml_schema_hints",
    "xml_allow_inactive_root_type_members",
    "xml_root_view_read_policy",
    "wsdl",
    "local_xml_file_set",
    "json_document",
    "json_schema_unresolved_reference",
    "mfd_decimal_input_names",
    "mfd_optional_parser_input_names",
    "json5",
    "tabular_kind",
    "delimiter",
    "csv_quote",
    "csv_quote_disabled",
    "csv_utf8_bom",
    "csv_preserve_empty_strings",
    "csv_text_repair_dependency",
    "has_header_row",
    "fixed_width",
    "flextext",
    "pdf",
    "http_get",
    "external_source",
    "json_lines",
    "protobuf",
    "xbrl",
    "xlsx_sheet",
    "xlsx_start_row",
    "xlsx_columns",
    "xlsx_headers",
    "xlsx_update_existing",
    "xlsx_rows",
    "xlsx_composite",
    "xlsx_worksheet_set",
    "xlsx_grid",
    "xlsx_hierarchical",
    "sqlite_read",
];

struct StrictOptions;
impl<'de> Deserialize<'de> for StrictOptions {
    fn deserialize<D: Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        struct OptionsVisitor;
        impl<'de> Visitor<'de> for OptionsVisitor {
            type Value = StrictOptions;
            fn expecting(&self, f: &mut fmt::Formatter) -> fmt::Result {
                f.write_str("a closed XML option object")
            }
            fn visit_map<M: MapAccess<'de>>(self, mut map: M) -> Result<Self::Value, M::Error> {
                let mut seen = BTreeSet::new();
                while let Some(key) = map.next_key::<String>()? {
                    if !OPTION_KEYS.contains(&key.as_str()) || !seen.insert(key) {
                        return Err(serde::de::Error::custom("invalid XML option key"));
                    }
                    map.next_value::<IgnoredAny>()?;
                }
                Ok(StrictOptions)
            }
        }
        deserializer.deserialize_map(OptionsVisitor)
    }
}

#[derive(Deserialize)]
struct RawEndpoint {
    #[serde(default, rename = "options")]
    _options: Option<StrictOptions>,
}

#[derive(Deserialize)]
struct RawProject {
    #[serde(default)]
    source_options: Option<StrictOptions>,
    #[serde(default)]
    target_options: Option<StrictOptions>,
    #[serde(default)]
    extra_sources: Vec<RawEndpoint>,
    #[serde(default)]
    extra_targets: Vec<RawEndpoint>,
}

#[derive(Deserialize)]
struct RawRoot {
    #[serde(default, rename = "__ferrule_file")]
    marker: Option<IgnoredAny>,
    #[serde(default)]
    document: Option<RawProject>,
    #[serde(default)]
    source_options: Option<StrictOptions>,
    #[serde(default)]
    target_options: Option<StrictOptions>,
    #[serde(default)]
    extra_sources: Vec<RawEndpoint>,
    #[serde(default)]
    extra_targets: Vec<RawEndpoint>,
}

/// This must see original bytes, not a Value that already lost duplicate keys.
pub fn validate_raw_options(native: &[u8]) -> Result<(), Error> {
    if native.len() > crate::protocol::MAX_NATIVE_BYTES {
        return Err(Error::ResourceLimit(Resource::NativeBytes));
    }
    let root: RawRoot = serde_json::from_slice(native).map_err(|_| Error::InvalidOptions)?;
    let project = if root.marker.is_some() {
        root.document.ok_or(Error::InvalidNative)?
    } else {
        RawProject {
            source_options: root.source_options,
            target_options: root.target_options,
            extra_sources: root.extra_sources,
            extra_targets: root.extra_targets,
        }
    };
    if project.source_options.is_none() || project.target_options.is_none() {
        return Err(Error::InvalidOptions);
    }
    if project.extra_sources.len() >= MAX_INPUTS || project.extra_targets.len() >= MAX_OUTPUTS {
        return Err(Error::ResourceLimit(Resource::DocumentCount));
    }
    Ok(())
}
