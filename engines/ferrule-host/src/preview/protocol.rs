use serde::{Deserialize, Serialize};

use crate::protocol::{ENGINE_REVISION, Revision};

pub const PREVIEW_PROTOCOL_VERSION: &str = "braid.ferrule-preview.v1";
pub const PREVIEW_PROFILE: &str = "braid.ferrule-json-preview.v1";
pub const DOCUMENT_PREVIEW_PROTOCOL_VERSION: &str = "braid.ferrule-document-preview.v1";
pub const DOCUMENT_PREVIEW_PROFILE: &str = "braid.ferrule-document-preview.v1";
pub const CSV_PREVIEW_PROTOCOL_VERSION: &str = "braid.ferrule-csv-preview.v1";
pub const CSV_PREVIEW_PROFILE: &str = "braid.ferrule-csv-preview.v1";
pub const XML_PREVIEW_PROTOCOL_VERSION: &str = "braid.ferrule-xml-preview.v1";
pub const FLEXTEXT_PREVIEW_PROTOCOL_VERSION: &str = crate::flextext_document::PROFILE;
pub const XLSX_PREVIEW_PROTOCOL_VERSION: &str = crate::xlsx_document::PROFILE;
pub const MAX_PREVIEW_INPUTS: usize = 16;
pub const MAX_PREVIEW_INPUT_BYTES: usize = 8 * 1024 * 1024;
pub const MAX_PREVIEW_OUTPUT_BYTES: usize = 8 * 1024 * 1024;
pub const MAX_PREVIEW_ARTIFACTS: usize = 64;
pub const MAX_PREVIEW_PARAMETERS: usize = 32;
pub const MAX_PREVIEW_PARAMETER_NAME_BYTES: usize = 256;
pub const MAX_PREVIEW_PARAMETER_TEXT_BYTES: usize = 4 * 1024;
pub const MAX_PREVIEW_TARGET_NAME_BYTES: usize = 256;
pub const MAX_PREVIEW_RESPONSE_BYTES: usize = 1024 * 1024;
/// Upper bound of request bytes that may be discarded after an unusable header.
pub const MAX_PREVIEW_DISCARD_BYTES: usize = 16 * 1024 * 1024;

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct PreviewRequestHeader {
    pub protocol_version: String,
    pub request_id: String,
    pub revision: Revision,
    pub native_digest: String,
    pub document_id: String,
    pub target: PreviewTarget,
    pub current_date_time: String,
    pub parameters: Vec<PreviewParameter>,
    pub inputs: Vec<PreviewInput>,
    pub limits: PreviewLimits,
}

impl PreviewRequestHeader {
    pub fn is_document_preview(&self) -> bool {
        matches!(
            self.protocol_version.as_str(),
            DOCUMENT_PREVIEW_PROTOCOL_VERSION
                | CSV_PREVIEW_PROTOCOL_VERSION
                | XML_PREVIEW_PROTOCOL_VERSION
                | crate::flextext_document::PROFILE
                | crate::xlsx_document::PROFILE
                | crate::native_document::PROTOBUF_PROFILE
                | crate::native_document::XBRL_PROFILE
        )
    }

    pub fn is_csv_preview(&self) -> bool {
        self.protocol_version == CSV_PREVIEW_PROTOCOL_VERSION
    }
    pub fn native_format(&self) -> Option<crate::native_document::NativeFormat> {
        crate::native_document::NativeFormat::from_profile(&self.protocol_version)
    }

    pub fn is_flextext_preview(&self) -> bool {
        self.protocol_version == FLEXTEXT_PREVIEW_PROTOCOL_VERSION
    }

    pub fn is_xlsx_preview(&self) -> bool {
        self.protocol_version == XLSX_PREVIEW_PROTOCOL_VERSION
    }
    pub fn is_xml_preview(&self) -> bool {
        self.protocol_version == XML_PREVIEW_PROTOCOL_VERSION
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(
    tag = "kind",
    rename_all = "camelCase",
    rename_all_fields = "camelCase",
    deny_unknown_fields
)]
pub enum PreviewTarget {
    All,
    Primary,
    Named { name: String },
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct PreviewParameter {
    pub name: String,
    pub value: PreviewValue,
}

/// Exact host values. Numbers travel as decimal text so no JSON number is reinterpreted.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "kind", content = "value", rename_all = "camelCase")]
pub enum PreviewValue {
    String(String),
    Integer(String),
    Float(String),
    Boolean(bool),
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(
    tag = "role",
    rename_all = "camelCase",
    rename_all_fields = "camelCase",
    deny_unknown_fields
)]
pub enum PreviewInput {
    Primary {
        byte_length: u32,
        sha256: String,
    },
    Named {
        name: String,
        byte_length: u32,
        sha256: String,
    },
}

impl PreviewInput {
    pub fn byte_length(&self) -> u32 {
        match self {
            Self::Primary { byte_length, .. } | Self::Named { byte_length, .. } => *byte_length,
        }
    }

    pub fn sha256(&self) -> &str {
        match self {
            Self::Primary { sha256, .. } | Self::Named { sha256, .. } => sha256,
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct PreviewLimits {
    pub maximum_input_bytes: u32,
    pub maximum_output_bytes: u32,
    pub maximum_artifacts: u32,
    pub maximum_text_bytes: u32,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PreviewResponse {
    pub protocol_version: String,
    pub request_id: Option<String>,
    pub revision: Option<Revision>,
    pub native_digest: Option<String>,
    pub document_id: Option<String>,
    pub engine_revision: String,
    pub profile: String,
    pub result: Option<PreviewResult>,
    pub failure: Option<PreviewFailure>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PreviewResult {
    pub records_written: u64,
    pub artifacts: Vec<PreviewArtifact>,
}

/// Metadata for one output frame that follows the response header in this order.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PreviewArtifact {
    pub boundary_id: String,
    pub target_name: String,
    pub target_name_truncated: bool,
    pub media_type: String,
    pub records_written: u64,
    pub byte_length: u32,
    pub sha256: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub csv_dialect: Option<crate::csv_document::CsvDialect>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub flex_text_settings: Option<crate::flextext_document::Settings>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub xlsx_settings: Option<crate::xlsx_document::Settings>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub document_settings: Option<crate::native_document::Settings>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub xml_metadata: Option<crate::xml_document::Metadata>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PreviewFailure {
    pub code: String,
    pub message: String,
    pub message_truncated: bool,
    pub subject: Option<String>,
    pub subject_truncated: bool,
}

impl PreviewResponse {
    pub fn failure(header: Option<&PreviewRequestHeader>, failure: PreviewFailure) -> Self {
        let mut response = Self::context(header);
        response.failure = Some(failure);
        response
    }

    pub fn success(header: &PreviewRequestHeader, result: PreviewResult) -> Self {
        let mut response = Self::context(Some(header));
        response.result = Some(result);
        response
    }

    fn context(header: Option<&PreviewRequestHeader>) -> Self {
        Self {
            protocol_version: header
                .map_or(PREVIEW_PROTOCOL_VERSION, |header| {
                    if header.is_flextext_preview()
                        || header.is_xlsx_preview()
                        || header.native_format().is_some()
                    {
                        &header.protocol_version
                    } else if header.is_xml_preview() {
                        XML_PREVIEW_PROTOCOL_VERSION
                    } else if header.is_csv_preview() {
                        CSV_PREVIEW_PROTOCOL_VERSION
                    } else if header.is_document_preview() {
                        DOCUMENT_PREVIEW_PROTOCOL_VERSION
                    } else {
                        PREVIEW_PROTOCOL_VERSION
                    }
                })
                .into(),
            request_id: header.map(|header| header.request_id.clone()),
            revision: header.map(|header| header.revision.clone()),
            native_digest: header.map(|header| header.native_digest.clone()),
            document_id: header.map(|header| header.document_id.clone()),
            engine_revision: ENGINE_REVISION.into(),
            profile: header
                .map_or(PREVIEW_PROFILE, |header| {
                    if header.is_flextext_preview()
                        || header.is_xlsx_preview()
                        || header.native_format().is_some()
                    {
                        &header.protocol_version
                    } else if header.is_xml_preview() {
                        XML_PREVIEW_PROTOCOL_VERSION
                    } else if header.is_csv_preview() {
                        CSV_PREVIEW_PROFILE
                    } else if header.is_document_preview() {
                        DOCUMENT_PREVIEW_PROFILE
                    } else {
                        PREVIEW_PROFILE
                    }
                })
                .into(),
            result: None,
            failure: None,
        }
    }
}
