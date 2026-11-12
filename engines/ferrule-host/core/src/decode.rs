use ferrule_mapping::{FileCodecError, Project};
use serde::Deserialize;
use serde::de::IgnoredAny;

use crate::protocol::MAX_NATIVE_BYTES;

pub struct DecodedProject {
    pub project: Project,
    pub document_prefix: String,
}

pub fn decode_native(bytes: &[u8]) -> Result<DecodedProject, FileCodecError> {
    if bytes.len() > MAX_NATIVE_BYTES {
        return Err(FileCodecError::TooLarge {
            bytes: bytes.len(),
            max: MAX_NATIVE_BYTES,
        });
    }
    let project = ferrule_mapping::project_file::decode_bytes(bytes)?;
    Ok(DecodedProject {
        project,
        document_prefix: document_prefix(bytes).into(),
    })
}

/// Probe only after authoritative codec admission or encoding.
pub(crate) fn document_prefix(bytes: &[u8]) -> &'static str {
    // Probe only the envelope marker after authoritative admission. IgnoredAny
    // preserves legacy acceptance of ignored overflow numbers and deep metadata.
    #[derive(Deserialize)]
    struct EnvelopeProbe {
        #[serde(rename = "__ferrule_file")]
        marker: Option<IgnoredAny>,
    }
    match serde_json::from_slice::<EnvelopeProbe>(bytes) {
        Ok(EnvelopeProbe { marker: Some(_) }) => "/document",
        _ => "",
    }
}
