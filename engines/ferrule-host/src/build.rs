//! In-memory C# source emission. This operation never reads design-time resources or executes code.
use crate::protocol::{ENGINE_REVISION, MAX_NATIVE_BYTES, Revision};
use crate::{decode, framing, valid_context_parts};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::io::{self, Read, Write};

pub const PROTOCOL: &str = "weft.ferrule-build.v1";
pub const PROFILE: &str = "weft.ferrule-csharp-json.v1";
pub const DOCUMENT_PROFILE: &str = "weft.ferrule-csharp-document.v1";
pub const X12_INTERCHANGES_PROFILE: &str = "weft.ferrule-csharp-x12-interchanges.v1";
pub const CSV_PROFILE: &str = "weft.ferrule-csharp-csv.v1";

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
        profile: if request.as_ref().is_some_and(|r| r.profile == CSV_PROFILE) {
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
            PROFILE | DOCUMENT_PROFILE | X12_INTERCHANGES_PROFILE | CSV_PROFILE
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
    let decoded = decode::decode_native(native).map_err(|_| "invalidMapping")?;
    let project = &decoded.project;
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
            name: Some(source.name.clone()),
            format: format(&source.options, Some(&source.path))?,
            csv_dialect: None,
        });
    }
    boundaries.push(Boundary {
        kind: "primaryOutput",
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
            "#nullable enable\nnamespace Weft.Generated { public static class DocumentSyntax { public static int OutputSegment(string? name) {\n",
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
            ferrule_codegen::ArtifactPath::new("Runtime/WeftDocumentSyntax.cs")
                .map_err(|_| "unsupported")?,
            syntax.into_bytes(),
        ));
    }
    if files.is_empty() || files.len() > 128 {
        return Err("sourceLimit");
    }
    let mut total = 0usize;
    for file in &files {
        total = total
            .checked_add(file.contents.len())
            .ok_or("sourceLimit")?;
        if total > MAX_SOURCE_BYTES || file.contents.is_empty() || file.path.as_str().len() > 256 {
            return Err("sourceLimit");
        }
    }
    Ok((boundaries, files))
}
fn name(name: &str) -> bool {
    !name.is_empty() && name.len() <= 256 && !name.chars().any(char::is_control)
}
