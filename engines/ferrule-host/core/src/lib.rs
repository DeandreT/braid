#![forbid(unsafe_code)]

//! Ferrule's native codec, deterministic edits and Weft's bounded explanation.
//! This crate has no host, filesystem, transport or browser API.

pub mod authored_document;
pub mod decode;
pub mod edit;
pub mod framing_limit;
pub mod inspect;
pub mod projection;
pub mod protocol;
pub mod validation;

pub use ferrule_edit_ops::{EditOperation, EditScript};

pub fn valid_context_parts(
    request_id: &str,
    revision: &protocol::Revision,
    native_digest: &str,
    document_id: &str,
) -> bool {
    [
        request_id,
        &revision.organization_id,
        &revision.project_id,
        &revision.mapping_id,
        &revision.revision_id,
    ]
    .into_iter()
    .all(canonical_nonzero_guid)
        && native_digest.len() == 64
        && native_digest
            .bytes()
            .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
        && !document_id.is_empty()
        && document_id.len() <= 128
        && !document_id.contains("..")
        && document_id.as_bytes()[0].is_ascii_alphanumeric()
        && document_id
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'_' | b'-'))
}

pub fn canonical_nonzero_guid(value: &str) -> bool {
    value.len() == 36
        && value != "00000000-0000-0000-0000-000000000000"
        && value.bytes().enumerate().all(|(index, byte)| {
            if matches!(index, 8 | 13 | 18 | 23) {
                byte == b'-'
            } else {
                byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte)
            }
        })
}
