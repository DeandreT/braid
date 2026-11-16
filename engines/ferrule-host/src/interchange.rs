//! Separate, byte-only package conversion profile. No host paths enter the codec.
use std::io::{self, Read, Write};

use ferrule_mfd::{ExportProfile, MfdPackage};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};

use crate::{decode, framing, protocol::ENGINE_REVISION};

pub const PROTOCOL: &str = "braid.ferrule-mfd.v1";
pub const MAX_PACKAGE: usize = 16 * 1024 * 1024 + 34 * 1024;
pub const MAX_HEADER: usize = 1024 * 1024;
pub const MAX_RESPONSE: usize = 18 * 1024 * 1024;
const MAGIC: &str = "braid.interchange-package.v1";

#[derive(Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Request {
    pub protocol_version: String,
    pub request_id: String,
    pub operation: String,
    pub input_digest: String,
    pub design_path: String,
    pub engine_revision: String,
}

#[derive(Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Warning {
    pub code: String,
    pub message: String,
}

#[derive(Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Response {
    pub protocol_version: String,
    pub request_id: Option<String>,
    pub operation: Option<String>,
    pub input_digest: Option<String>,
    pub design_path: Option<String>,
    pub engine_revision: String,
    pub status: String,
    pub payload_digest: Option<String>,
    pub payload_bytes: usize,
    pub summary: String,
    pub warnings: Vec<Warning>,
}

pub fn handle(header: &[u8], reader: &mut impl Read, writer: &mut impl Write) -> io::Result<()> {
    let request = serde_json::from_slice::<Request>(header).ok();
    let result = (|| {
        let request = request.as_ref().ok_or("invalidRequest")?;
        if request.protocol_version != PROTOCOL
            || request.engine_revision != ENGINE_REVISION
            || !crate::canonical_nonzero_guid(&request.request_id)
            || !matches!(request.operation.as_str(), "import" | "export")
            || !request.design_path.to_ascii_lowercase().ends_with(".mfd")
        {
            return Err("invalidRequest");
        }
        let maximum = if request.operation == "import" {
            MAX_PACKAGE
        } else {
            crate::protocol::MAX_NATIVE_BYTES
        };
        let payload = framing::read_frame(reader, maximum).map_err(|_| "invalidFrame")?;
        framing::require_eof(reader).map_err(|_| "invalidFrame")?;
        if hex(&payload) != request.input_digest {
            return Err("digestMismatch");
        }
        convert(request, &payload)
    })();
    let (status, payload, warnings, summary) = match result {
        Ok((payload, warnings, summary)) => ("succeeded", payload, warnings, summary),
        Err(status) => (status, Vec::new(), Vec::new(), "Conversion refused.".into()),
    };
    let response = Response {
        protocol_version: PROTOCOL.into(),
        request_id: request.as_ref().map(|r| r.request_id.clone()),
        operation: request.as_ref().map(|r| r.operation.clone()),
        input_digest: request.as_ref().map(|r| r.input_digest.clone()),
        design_path: request.as_ref().map(|r| r.design_path.clone()),
        engine_revision: ENGINE_REVISION.into(),
        status: status.into(),
        payload_digest: (!payload.is_empty()).then(|| hex(&payload)),
        payload_bytes: payload.len(),
        summary,
        warnings,
    };
    let header = framing::encode_bounded(&response, MAX_HEADER).map_err(io::Error::other)?;
    if header.len() + payload.len() + 8 > MAX_RESPONSE {
        return Err(io::Error::other("Interchange response exceeds bound"));
    }
    frame(writer, &header)?;
    if !payload.is_empty() {
        frame(writer, &payload)?;
    }
    writer.flush()
}

type Converted = (Vec<u8>, Vec<Warning>, String);
fn convert(request: &Request, payload: &[u8]) -> Result<Converted, &'static str> {
    if request.operation == "import" {
        let package = decode_package(payload)?;
        if package.design_path() != request.design_path {
            return Err("invalidPackage");
        }
        let imported = ferrule_mfd::import_package(&package).map_err(|_| "conversionRefused")?;
        let warnings = imported
            .warnings
            .into_iter()
            .map(|message| Warning {
                code: "importWarning".into(),
                message,
            })
            .collect::<Vec<_>>();
        check_warnings(&warnings)?;
        let summary = format!(
            "Imported mapping from {} to {}: {} graph nodes, {} extra sources, {} extra targets. Review every conversion warning.",
            imported.project.source.name,
            imported.project.target.name,
            imported.project.graph.nodes.len(),
            imported.project.extra_sources.len(),
            imported.project.extra_targets.len()
        );
        if summary.len() > 8192 || summary.contains('\0') {
            return Err("resourceLimit");
        }
        let native = ferrule_mapping::project_file::encode_pretty(&imported.project)
            .map_err(|_| "conversionRefused")?
            .into_bytes();
        if native.is_empty() || native.len() > crate::protocol::MAX_NATIVE_BYTES {
            return Err("resourceLimit");
        }
        Ok((native, warnings, summary))
    } else {
        let decoded = decode::decode_native(payload).map_err(|_| "invalidNative")?;
        let exported = ferrule_mfd::export_package(
            &decoded.project,
            &request.design_path,
            ExportProfile::FerruleExtensions,
        )
        .map_err(|_| "conversionRefused")?;
        let mut warnings = exported
            .report
            .warnings
            .into_iter()
            .map(|message| Warning {
                code: "exportWarning".into(),
                message,
            })
            .collect::<Vec<_>>();
        warnings.extend(exported.report.issues.into_iter().map(|issue| Warning {
            code: "compatibility".into(),
            message: format!(
                "{:?}: {} (component {}, uid {:?})",
                issue.feature, issue.message, issue.component, issue.component_uid
            ),
        }));
        check_warnings(&warnings)?;
        Ok((
            encode_package(&exported.package),
            warnings,
            format!(
                "MFD export compatibility: {:?}. Review every conversion warning.",
                exported.report.compatibility
            ),
        ))
    }
}

fn check_warnings(warnings: &[Warning]) -> Result<(), &'static str> {
    if warnings.len() > 256
        || warnings.iter().any(|w| {
            w.message.is_empty()
                || w.message.len() > 2048
                || w.message.contains('\0')
                || w.code.len() > 128
        })
    {
        return Err("resourceLimit");
    }
    Ok(())
}

pub fn decode_package(bytes: &[u8]) -> Result<MfdPackage, &'static str> {
    if bytes.len() > MAX_PACKAGE {
        return Err("resourceLimit");
    }
    let mut remaining = bytes;
    if text(&mut remaining)? != MAGIC {
        return Err("invalidPackage");
    }
    let design = text(&mut remaining)?.to_owned();
    let count = number(&mut remaining)?;
    if !(1..=128).contains(&count) {
        return Err("resourceLimit");
    }
    let mut entries = Vec::with_capacity(count);
    let mut previous = String::new();
    for _ in 0..count {
        let path = text(&mut remaining)?.to_owned();
        if path.len() > 256 || path <= previous {
            return Err("invalidPackage");
        }
        previous.clone_from(&path);
        let contents = field(&mut remaining)?;
        entries.push((path, contents));
    }
    if !remaining.is_empty() {
        return Err("invalidPackage");
    }
    MfdPackage::new(&design, entries).map_err(|_| "invalidPackage")
}

pub fn encode_package(package: &MfdPackage) -> Vec<u8> {
    let mut bytes = Vec::new();
    append(&mut bytes, MAGIC.as_bytes());
    append(&mut bytes, package.design_path().as_bytes());
    bytes.extend_from_slice(&(package.entries().len() as u32).to_be_bytes());
    for (path, contents) in package.entries() {
        append(&mut bytes, path.as_bytes());
        append(&mut bytes, contents);
    }
    bytes
}
fn number(bytes: &mut &[u8]) -> Result<usize, &'static str> {
    let prefix = bytes.get(..4).ok_or("invalidPackage")?;
    let value = u32::from_be_bytes(prefix.try_into().map_err(|_| "invalidPackage")?) as usize;
    *bytes = &bytes[4..];
    Ok(value)
}
fn field<'a>(bytes: &mut &'a [u8]) -> Result<&'a [u8], &'static str> {
    let size = number(bytes)?;
    let value = bytes.get(..size).ok_or("invalidPackage")?;
    *bytes = &bytes[size..];
    Ok(value)
}
fn text<'a>(bytes: &mut &'a [u8]) -> Result<&'a str, &'static str> {
    std::str::from_utf8(field(bytes)?).map_err(|_| "invalidPackage")
}
fn append(bytes: &mut Vec<u8>, value: &[u8]) {
    bytes.extend_from_slice(&(value.len() as u32).to_be_bytes());
    bytes.extend_from_slice(value);
}
fn hex(bytes: &[u8]) -> String {
    format!("{:x}", Sha256::digest(bytes))
}
fn frame(writer: &mut impl Write, bytes: &[u8]) -> io::Result<()> {
    writer.write_all(&(bytes.len() as u32).to_be_bytes())?;
    writer.write_all(bytes)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn complete_warning_limits_refuse_instead_of_truncating() {
        let warnings = (0..257)
            .map(|_| Warning {
                code: "warning".into(),
                message: "message".into(),
            })
            .collect::<Vec<_>>();
        assert_eq!(check_warnings(&warnings), Err("resourceLimit"));
        assert_eq!(
            check_warnings(&[Warning {
                code: "warning".into(),
                message: "x".repeat(2049)
            }]),
            Err("resourceLimit")
        );
        assert!(check_warnings(&warnings[..256]).is_ok());
    }
}
