//! In-memory C# source emission. This operation never reads design-time resources or executes code.
use crate::protocol::{ENGINE_REVISION, MAX_NATIVE_BYTES, Revision};
use crate::{decode, framing, valid_context_parts};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::io::{self, Read, Write};

pub const PROTOCOL: &str = "braid.ferrule-build.v1";
pub const PROFILE: &str = "braid.ferrule-csharp-json.v1";
pub const DOCUMENT_PROFILE: &str = "braid.ferrule-csharp-document.v1";
pub const X12_INTERCHANGES_PROFILE: &str = "braid.ferrule-csharp-x12-interchanges.v1";
pub const CSV_PROFILE: &str = "braid.ferrule-csharp-csv.v1";
pub const XML_PROFILE: &str = "braid.ferrule-csharp-xml.v1";
pub const FLEX_PROFILE: &str = "braid.ferrule-csharp-flextext.v1";
pub const PB_PROFILE: &str = "braid.ferrule-csharp-protobuf.v1";
pub const XBRL_PROFILE: &str = "braid.ferrule-csharp-xbrl.v1";

fn native_profile(id: &str) -> Option<&'static str> {
    [FLEX_PROFILE, PB_PROFILE, XBRL_PROFILE]
        .into_iter()
        .find(|profile| *profile == id)
}

fn document_profile(profile: &str) -> bool {
    matches!(
        profile,
        DOCUMENT_PROFILE | X12_INTERCHANGES_PROFILE | CSV_PROFILE
    )
}
pub const MAX_SOURCE_BYTES: usize = 32 * 1024 * 1024;

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Request {
    pub protocol_version: String,
    pub request_id: String,
    pub revision: Revision,
    pub native_digest: String,
    pub intent_digest: String,
    pub operation: String,
    pub profile: String,
}
#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Boundary {
    pub kind: &'static str,
    pub name: Option<String>,
    pub format: &'static str,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub csv_dialect: Option<crate::csv_document::CsvDialect>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub xml_metadata: Option<crate::xml_document::Metadata>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub document_settings: Option<crate::native_document::Settings>,
}
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SourceFile {
    pub path: String,
    pub byte_count: usize,
    pub sha256: String,
}
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Response<'a> {
    pub protocol_version: &'static str,
    pub request_id: Option<&'a str>,
    pub revision: Option<&'a Revision>,
    pub native_digest: Option<&'a str>,
    pub intent_digest: Option<&'a str>,
    pub engine_revision: &'static str,
    pub profile: &'static str,
    pub status: &'static str,
    pub boundaries: Vec<Boundary>,
    pub files: Vec<SourceFile>,
}

pub fn handle(header: &[u8], reader: &mut impl Read, writer: &mut impl Write) -> io::Result<()> {
    let request = serde_json::from_slice::<Request>(header).ok();
    let native = framing::read_frame(reader, MAX_NATIVE_BYTES);
    let complete = framing::require_eof(reader);
    let generated = match (&request, native, complete) {
        (Some(request), Ok(native), Ok(())) => generate(request, &native),
        _ => Err("invalidRequest"),
    };
    let (status, boundaries, files) = match generated {
        Ok((boundaries, files)) => ("supported", boundaries, files),
        Err(status) => (status, Vec::new(), Vec::new()),
    };
    let response = Response {
        protocol_version: PROTOCOL,
        request_id: request.as_ref().map(|r| r.request_id.as_str()),
        revision: request.as_ref().map(|r| &r.revision),
        native_digest: request.as_ref().map(|r| r.native_digest.as_str()),
        intent_digest: request.as_ref().map(|r| r.intent_digest.as_str()),
        engine_revision: ENGINE_REVISION,
        profile: if let Some(profile) = request.as_ref().and_then(|r| native_profile(&r.profile)) {
            profile
        } else if request.as_ref().is_some_and(|r| r.profile == XML_PROFILE) {
            XML_PROFILE
        } else if request.as_ref().is_some_and(|r| r.profile == CSV_PROFILE) {
            CSV_PROFILE
        } else if request
            .as_ref()
            .is_some_and(|r| r.profile == X12_INTERCHANGES_PROFILE)
        {
            X12_INTERCHANGES_PROFILE
        } else if request
            .as_ref()
            .is_some_and(|r| r.profile == DOCUMENT_PROFILE)
        {
            DOCUMENT_PROFILE
        } else {
            PROFILE
        },
        status,
        boundaries,
        files: files
            .iter()
            .map(|f| SourceFile {
                path: f.path.as_str().into(),
                byte_count: f.contents.len(),
                sha256: format!("{:x}", Sha256::digest(&f.contents)),
            })
            .collect(),
    };
    let header = framing::encode_bounded(&response, 128 * 1024).map_err(io::Error::other)?;
    frame(writer, &header)?;
    for file in files {
        frame(writer, &file.contents)?;
    }
    writer.flush()
}

fn frame(writer: &mut impl Write, bytes: &[u8]) -> io::Result<()> {
    writer.write_all(&(bytes.len() as u32).to_be_bytes())?;
    writer.write_all(bytes)
}

pub fn generate(
    request: &Request,
    native: &[u8],
) -> Result<(Vec<Boundary>, Vec<ferrule_codegen::GeneratedFile>), &'static str> {
    if request.protocol_version != PROTOCOL
        || !matches!(
            request.profile.as_str(),
            PROFILE
                | DOCUMENT_PROFILE
                | X12_INTERCHANGES_PROFILE
                | CSV_PROFILE
                | XML_PROFILE
                | FLEX_PROFILE
                | PB_PROFILE
                | XBRL_PROFILE
        )
        || !matches!(request.operation.as_str(), "inspect" | "compile")
        || !valid_context_parts(
            &request.request_id,
            &request.revision,
            &request.native_digest,
            "entry.native.json",
        )
        || request.intent_digest.len() != 64
        || !request
            .intent_digest
            .bytes()
            .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
        || native.is_empty()
        || native.len() > MAX_NATIVE_BYTES
        || format!("{:x}", Sha256::digest(native)) != request.native_digest
    {
        return Err("invalidRequest");
    }
    if request.profile == XML_PROFILE {
        let (decoded, xml) =
            crate::xml_document::admit_native(native).map_err(|_| "unsupportedXmlBuild")?;
        let project = decoded.project;
        if project.execution_contract.is_some() {
            return Err("unsupportedXmlBuild");
        }
        let mut boundaries = Vec::new();
        for (input, names, metadata) in [
            (
                true,
                project
                    .extra_sources
                    .iter()
                    .map(|s| s.name.clone())
                    .collect::<Vec<_>>(),
                xml.inputs,
            ),
            (
                false,
                project
                    .extra_targets
                    .iter()
                    .map(|s| s.name.clone())
                    .collect(),
                xml.outputs,
            ),
        ] {
            for (name, metadata) in std::iter::once(None)
                .chain(names.into_iter().map(Some))
                .zip(metadata)
            {
                let kind = match (input, name.is_some()) {
                    (true, false) => "primaryInput",
                    (true, true) => "namedInput",
                    (false, false) => "primaryOutput",
                    (false, true) => "namedOutput",
                };
                boundaries.push(Boundary {
                    kind,
                    name,
                    format: "xml",
                    csv_dialect: None,
                    xml_metadata: Some(metadata),
                    document_settings: None,
                });
            }
        }
        let mut scaffold = Vec::new();
        for boundary in &boundaries {
            scaffold.push((boundary.kind, &boundary.name, boundary.format));
        }
        framing::encode_bounded(&scaffold, 32 * 1024 - 2048).map_err(|_| "sourceLimit")?;
        let files = ferrule_codegen_csharp::emit(&xml.program)
            .map_err(|_| "unsupportedXmlBuild")?
            .into_files();
        check_files(&files)?;
        let manifest = files
            .iter()
            .map(|f| SourceFile {
                path: f.path.as_str().into(),
                byte_count: f.contents.len(),
                sha256: format!("{:x}", Sha256::digest(&f.contents)),
            })
            .collect::<Vec<_>>();
        framing::encode_bounded(&manifest, 64 * 1024).map_err(|_| "sourceLimit")?;
        return Ok((boundaries, files));
    }
    let decoded = decode::decode_native(native).map_err(|_| "invalidMapping")?;
    let project = &decoded.project;
    if let Some(profile) = native_profile(&request.profile) {
        use crate::native_document::{self, NativeFormat};
        let (format, refusal) = match profile {
            FLEX_PROFILE => ("flexText", "unsupportedFlexTextBuild"),
            PB_PROFILE => ("protobuf", "unsupportedProtobufBuild"),
            _ => ("xbrl", "unsupportedXbrlBuild"),
        };
        if project.execution_contract.is_some() {
            return Err(refusal);
        }
        let mut files = if profile == FLEX_PROFILE {
            crate::flextext_document::admit(project).map_err(|_| refusal)?;
            crate::flextext_document::admit_saved_metadata(native, project).map_err(|_| refusal)?;
            let program = ferrule_codegen::lower(project).map_err(|_| refusal)?;
            ferrule_codegen_csharp::emit_with_flextext_bytes(
                &program,
                &project.source_options,
                &project.target_options,
            )
            .map_err(|_| refusal)?
        } else {
            let family = if profile == PB_PROFILE {
                NativeFormat::Protobuf
            } else {
                NativeFormat::Xbrl
            };
            native_document::admit(family, project).map_err(|_| refusal)?;
            native_document::admit_saved_metadata(family, native, project).map_err(|_| refusal)?;
            if profile == PB_PROFILE {
                ferrule_codegen_csharp::emit_with_protobuf_bytes(project).map_err(|_| refusal)?
            } else {
                use ferrule_codegen_csharp::xbrl_documents::{Policy, emit};
                let program = ferrule_codegen::lower(project).map_err(|_| refusal)?;
                emit(
                    &program,
                    &Policy {
                        source: project.source_options.xbrl.as_ref().ok_or(refusal)?,
                        target: project.target_options.xbrl.as_ref().ok_or(refusal)?,
                        extra_sources: &[],
                        extra_targets: &[],
                    },
                )
                .map_err(|_| refusal)?
            }
        }
        .into_files();
        // Fixed host ABI for the three primary-only profiles. No mapping text
        // or caller-supplied type or method name is interpolated here.
        let body = match profile {
            FLEX_PROFILE => {
                "return all ? M.ExecuteFlexTextBytesOutputsWithHost(input, host) : M.ExecuteFlexTextBytesSelectedTargetWithHost(input, FerruleTargetSelection.Primary(), host);"
            }
            PB_PROFILE => {
                "return all ? M.ExecuteProtobufOutputs(input, [], host).Single().Bytes : M.ExecuteSelectedProtobufOutput(input, [], FerruleTargetSelection.Primary(), host).Bytes;"
            }
            _ => {
                "var extras = new System.Collections.Generic.Dictionary<string, byte[]>(); return all ? M.ExecuteXbrlBytesOutputs(input, extras, host).Primary : M.ExecuteXbrlBytesSelectedTarget(input, extras, FerruleTargetSelection.Primary(), host);"
            }
        };
        let bridge = format!(
            "using System.Linq; using Ferrule.Runtime; using M = Ferrule.Generated.GeneratedMapping; namespace Braid.Generated; public static class NativeDocument {{ public static byte[] Execute(byte[] input, bool all, FerruleExecutionContext host) {{ {body} }} }}"
        );
        files.push(ferrule_codegen::GeneratedFile::new(
            ferrule_codegen::ArtifactPath::new("Runtime/BraidNativeDocument.cs")
                .map_err(|_| refusal)?,
            bridge.into_bytes(),
        ));
        check_files(&files)?;
        let mut boundaries = Vec::new();
        for (kind, options) in [
            ("primaryInput", &project.source_options),
            ("primaryOutput", &project.target_options),
        ] {
            let settings = if profile == FLEX_PROFILE {
                let settings = crate::flextext_document::settings(options).map_err(|_| refusal)?;
                native_document::Settings {
                    schema: settings.schema,
                    options_json: settings.layout_json,
                }
            } else {
                native_document::settings(
                    if profile == PB_PROFILE {
                        NativeFormat::Protobuf
                    } else {
                        NativeFormat::Xbrl
                    },
                    options,
                )
                .map_err(|_| refusal)?
            };
            boundaries.push(Boundary {
                kind,
                name: None,
                format,
                csv_dialect: None,
                xml_metadata: None,
                document_settings: Some(settings),
            });
        }
        return Ok((boundaries, files));
    }
    for options in [&project.source_options, &project.target_options]
        .into_iter()
        .chain(project.extra_sources.iter().map(|source| &source.options))
        .chain(project.extra_targets.iter().map(|target| &target.options))
    {
        if options.pdf.is_some() {
            return Err("unsupportedPdfBuild");
        }
        if options.sqlite_read.is_some() {
            return Err("statefulDatabaseBuild");
        }
        if options.flextext.is_some() {
            return Err("unsupportedFlexTextBuild");
        }
        if options.tabular_kind == Some(ferrule_mapping::TabularBoundaryKind::Xlsx) {
            return Err("unsupportedXlsxBuild");
        }
        if options.protobuf.is_some() {
            return Err("unsupportedProtobufBuild");
        }
        if options.xbrl.is_some() {
            return Err("unsupportedXbrlBuild");
        }
    }
    let csv_policy = if request.profile == CSV_PROFILE {
        Some(crate::csv_document::admit(project).map_err(|_| "unsupportedCsvPlan")?)
    } else {
        None
    };
    use crate::preview::admission::plain_json;
    let format = |options: &ferrule_mapping::FormatOptions, path: Option<&str>| {
        if document_profile(&request.profile) {
            crate::document_format::DocumentFormat::from_options(options)
                .and_then(|value| {
                    let csv = matches!(
                        value,
                        crate::document_format::DocumentFormat::Csv
                            | crate::document_format::DocumentFormat::Tsv
                    );
                    if csv == (request.profile == CSV_PROFILE) {
                        Ok(value)
                    } else {
                        Err(crate::document_format::FormatRefusal::UnsupportedFamily)
                    }
                })
                .map(|value| value.id())
                .map_err(|_| "unsupported")
        } else if plain_json(options, path) {
            Ok("json")
        } else {
            Err("unsupported")
        }
    };
    if project.root.output_path().is_some()
        || project.extra_sources.len() > 15
        || project.extra_targets.len() > 63
    {
        return Err("unsupported");
    }
    let mut boundaries = vec![Boundary {
        document_settings: None,
        xml_metadata: None,
        kind: "primaryInput",
        name: None,
        format: format(&project.source_options, project.source_path.as_deref())?,
        csv_dialect: if request.profile == CSV_PROFILE {
            Some(
                crate::csv_document::CsvDialect::from_options(&project.source_options)
                    .map_err(|_| "unsupportedCsvPlan")?,
            )
        } else {
            None
        },
    }];
    let mut names = std::collections::BTreeSet::new();
    for source in &project.extra_sources {
        if source.dynamic_path.is_some() || !name(&source.name) || !names.insert(&source.name) {
            return Err("unsupported");
        }
        boundaries.push(Boundary {
            kind: "namedInput",
            document_settings: None,
            xml_metadata: None,
            name: Some(source.name.clone()),
            format: format(&source.options, Some(&source.path))?,
            csv_dialect: None,
        });
    }
    boundaries.push(Boundary {
        kind: "primaryOutput",
        document_settings: None,
        xml_metadata: None,
        name: None,
        format: format(&project.target_options, project.target_path.as_deref())?,
        csv_dialect: if request.profile == CSV_PROFILE {
            Some(
                crate::csv_document::CsvDialect::from_options(&project.target_options)
                    .map_err(|_| "unsupportedCsvPlan")?,
            )
        } else {
            None
        },
    });
    names.clear();
    for target in &project.extra_targets {
        if target.root.output_path().is_some() || !name(&target.name) || !names.insert(&target.name)
        {
            return Err("unsupported");
        }
        boundaries.push(Boundary {
            kind: "namedOutput",
            document_settings: None,
            xml_metadata: None,
            name: Some(target.name.clone()),
            format: format(&target.options, target.path.as_deref())?,
            csv_dialect: None,
        });
    }
    let program = ferrule_codegen::lower(project).map_err(|_| "unsupported")?;
    if request.profile == X12_INTERCHANGES_PROFILE
        && !boundaries.iter().any(|boundary| boundary.format == "x12")
    {
        return Err("unsupported");
    }
    let artifacts = if let Some(policy) = csv_policy {
        ferrule_codegen_csharp::emit_with_csv_document_adapters(&program, &policy)
            .map_err(|_| "unsupportedCsvPlan")?
    } else if document_profile(&request.profile)
        && boundaries.iter().any(|boundary| boundary.format != "json")
    {
        let options = |options: &ferrule_mapping::FormatOptions| {
            use crate::document_format::DocumentFormat;
            use ferrule_codegen::{
                DocumentBoundaryOptions, EdifactBoundaryOptions, X12BoundaryOptions,
            };
            match DocumentFormat::from_options(options).map_err(|_| "unsupported")? {
                DocumentFormat::Json => Ok(DocumentBoundaryOptions::Json),
                DocumentFormat::X12 => {
                    let mut boundary = X12BoundaryOptions::from_format_options(options)
                        .map_err(|_| "unsupported")?;
                    if request.profile == X12_INTERCHANGES_PROFILE {
                        boundary.envelope_profile =
                            ferrule_codegen::X12EnvelopeProfile::MapForceInterchanges;
                    }
                    Ok(DocumentBoundaryOptions::X12(boundary))
                }
                DocumentFormat::Edifact => EdifactBoundaryOptions::from_format_options(options)
                    .map(DocumentBoundaryOptions::Edifact)
                    .map_err(|_| "unsupported"),
                DocumentFormat::Csv | DocumentFormat::Tsv => Err("unsupportedCsvPlan"),
                DocumentFormat::FlexText => Err("unsupportedFlexTextBuild"),
                DocumentFormat::Xlsx => Err("unsupportedXlsxBuild"),
                DocumentFormat::Protobuf => Err("unsupportedProtobufBuild"),
                DocumentFormat::Xbrl => Err("unsupportedXbrlBuild"),
                DocumentFormat::Xml => Err("unsupportedXmlBuild"),
            }
        };
        let policy = ferrule_codegen::StaticDocumentBoundaryPolicy {
            source: options(&project.source_options)?,
            target: options(&project.target_options)?,
            extra_sources: project
                .extra_sources
                .iter()
                .map(|source| {
                    Ok(ferrule_codegen::NamedDocumentBoundaryOptions {
                        name: source.name.clone(),
                        options: options(&source.options)?,
                    })
                })
                .collect::<Result<Vec<_>, &'static str>>()?,
            extra_targets: project
                .extra_targets
                .iter()
                .map(|target| {
                    Ok(ferrule_codegen::NamedDocumentBoundaryOptions {
                        name: target.name.clone(),
                        options: options(&target.options)?,
                    })
                })
                .collect::<Result<Vec<_>, &'static str>>()?,
        };
        ferrule_codegen_csharp::emit_with_static_document_adapters(&program, &policy)
            .map_err(|_| "unsupported")?
    } else {
        ferrule_codegen_csharp::emit(&program).map_err(|_| "unsupported")?
    };
    let mut files = artifacts.into_files();
    if document_profile(&request.profile) && request.profile != CSV_PROFILE {
        // This host-owned metadata carries only admitted syntax, not mapping logic.
        // It also covers headerless output from an optional-envelope X12 schema.
        let segment = |options: &ferrule_mapping::FormatOptions| {
            u32::from(options.x12_separators.map_or('~', |value| value.segment))
        };
        let mut syntax = String::from(
            "#nullable enable\nnamespace Braid.Generated { public static class DocumentSyntax { public static int OutputSegment(string? name) {\n",
        );
        syntax.push_str(&format!(
            "if (name == null) return {};\n",
            segment(&project.target_options)
        ));
        for target in &project.extra_targets {
            let name = format!(
                "\"{}\"",
                target
                    .name
                    .encode_utf16()
                    .map(|unit| format!("\\u{unit:04x}"))
                    .collect::<String>()
            );
            syntax.push_str(&format!(
                "if (name == {name}) return {};\n",
                segment(&target.options)
            ));
        }
        syntax.push_str("throw new System.InvalidOperationException(); } } }\n");
        files.push(ferrule_codegen::GeneratedFile::new(
            ferrule_codegen::ArtifactPath::new("Runtime/BraidDocumentSyntax.cs")
                .map_err(|_| "unsupported")?,
            syntax.into_bytes(),
        ));
    }
    check_files(&files)?;
    Ok((boundaries, files))
}
fn check_files(files: &[ferrule_codegen::GeneratedFile]) -> Result<(), &'static str> {
    if files.is_empty() || files.len() > 128 {
        return Err("sourceLimit");
    }
    let mut total = 0usize;
    for file in files {
        total = total
            .checked_add(file.contents.len())
            .ok_or("sourceLimit")?;
        if total > MAX_SOURCE_BYTES || file.contents.is_empty() || file.path.as_str().len() > 256 {
            return Err("sourceLimit");
        }
    }
    Ok(())
}
fn name(name: &str) -> bool {
    !name.is_empty() && name.len() <= 256 && !name.chars().any(char::is_control)
}
