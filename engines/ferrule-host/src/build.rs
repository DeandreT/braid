//! In-memory C# source emission. This operation never reads design-time resources or executes code.
use crate::protocol::{ENGINE_REVISION, MAX_NATIVE_BYTES, Revision};
use crate::{decode, framing, valid_context_parts};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::io::{self, Read, Write};

pub const PROTOCOL: &str = "weft.ferrule-build.v1";
pub const PROFILE: &str = "weft.ferrule-csharp-json.v1";
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
        profile: PROFILE,
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
        || request.profile != PROFILE
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
    use crate::preview::admission::plain_json;
    if project.root.output_path().is_some()
        || !plain_json(&project.source_options, project.source_path.as_deref())
        || !plain_json(&project.target_options, project.target_path.as_deref())
        || project.extra_sources.len() > 15
        || project.extra_targets.len() > 63
    {
        return Err("unsupported");
    }
    let mut boundaries = vec![Boundary {
        kind: "primaryInput",
        name: None,
        format: "json",
    }];
    let mut names = std::collections::BTreeSet::new();
    for source in &project.extra_sources {
        if source.dynamic_path.is_some()
            || !plain_json(&source.options, Some(&source.path))
            || !name(&source.name)
            || !names.insert(&source.name)
        {
            return Err("unsupported");
        }
        boundaries.push(Boundary {
            kind: "namedInput",
            name: Some(source.name.clone()),
            format: "json",
        });
    }
    boundaries.push(Boundary {
        kind: "primaryOutput",
        name: None,
        format: "json",
    });
    names.clear();
    for target in &project.extra_targets {
        if target.root.output_path().is_some()
            || !plain_json(&target.options, target.path.as_deref())
            || !name(&target.name)
            || !names.insert(&target.name)
        {
            return Err("unsupported");
        }
        boundaries.push(Boundary {
            kind: "namedOutput",
            name: Some(target.name.clone()),
            format: "json",
        });
    }
    let program = ferrule_codegen::lower(project).map_err(|_| "unsupported")?;
    let artifacts = ferrule_codegen_csharp::emit(&program).map_err(|_| "unsupported")?;
    if artifacts.is_empty() || artifacts.len() > 128 {
        return Err("sourceLimit");
    }
    let mut total = 0usize;
    for file in artifacts.files() {
        total = total
            .checked_add(file.contents.len())
            .ok_or("sourceLimit")?;
        if total > MAX_SOURCE_BYTES || file.contents.is_empty() || file.path.as_str().len() > 256 {
            return Err("sourceLimit");
        }
    }
    Ok((boundaries, artifacts.into_files()))
}
fn name(name: &str) -> bool {
    !name.is_empty() && name.len() <= 256 && !name.chars().any(char::is_control)
}
