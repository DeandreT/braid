//! Page-cited text extraction only. Production activation requires a resource-limited worker host.
use ferrule_ir::{Instance, Value};
use ferrule_mapping::{PdfCapture, PdfCommand, PdfGroup, PdfLayout, PdfPageSelection, PdfRegion};
use ferrule_pdf::PdfError;
use serde::Serialize;
use sha2::{Digest, Sha256};

pub const MAX_INPUT_BYTES: usize = ferrule_pdf::MAX_INPUT_BYTES;
pub const MAX_RESULT_BYTES: usize = 8 * 1024 * 1024;

#[derive(Debug, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum Status {
    Partial,
    NeedsOcr,
    Encrypted,
    Malformed,
    LimitExceeded,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PageText {
    pub page: u32,
    pub text: String,
    pub missing_text: bool,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Extraction {
    pub format: &'static str,
    pub extractor: &'static str,
    pub source_sha256: String,
    pub source_byte_count: usize,
    pub status: Status,
    pub pages: Vec<PageText>,
}

impl Extraction {
    fn empty(bytes: &[u8], status: Status) -> Self {
        Self {
            format: "weft.spec-pdf-extraction.v1",
            extractor: "ferrule-pdf-dddc4dce-text.v1",
            source_sha256: format!("{:x}", Sha256::digest(bytes)),
            source_byte_count: bytes.len(),
            status,
            pages: Vec::new(),
        }
    }
}

/// Input is immutable captured bytes, never a path, URL, password or caller-authored layout.
pub fn extract(bytes: &[u8]) -> Extraction {
    if bytes.len() > MAX_INPUT_BYTES {
        return Extraction::empty(bytes, Status::LimitExceeded);
    }
    let layout = PdfLayout::new(
        "Document",
        PdfPageSelection::All,
        vec![PdfCommand::GroupPerPage(PdfGroup {
            name: "Pages".into(),
            region: PdfRegion::full(),
            children: vec![PdfCommand::Capture(PdfCapture {
                name: "Text".into(),
                region: PdfRegion::full(),
                algorithm: Default::default(),
            })],
        })],
    )
    .expect("fixed bounded PDF layout");
    let instance = match ferrule_pdf::from_bytes(bytes, &layout) {
        Ok(instance) => instance,
        Err(error) => {
            let status = match error {
                PdfError::InputTooLarge
                | PdfError::TooManyPages
                | PdfError::TooManyEvents
                | PdfError::DecodedTextTooLarge
                | PdfError::TooManyOutputNodes
                | PdfError::ValueTooLarge(_)
                | PdfError::InstanceTooDeep => Status::LimitExceeded,
                PdfError::InvalidPdf(message)
                    if message == "encrypted PDF documents are not supported" =>
                {
                    Status::Encrypted
                }
                _ => Status::Malformed,
            };
            return Extraction::empty(bytes, status);
        }
    };
    let mut result = Extraction::empty(bytes, Status::Partial);
    let Instance::Group(root) = instance else {
        return Extraction::empty(bytes, Status::Malformed);
    };
    if root.is_empty() {
        result.status = Status::NeedsOcr;
        return result;
    }
    if root.len() != 1 || root[0].0 != "Pages" {
        return Extraction::empty(bytes, Status::Malformed);
    }
    let Instance::Repeated(pages) = &root[0].1 else {
        return Extraction::empty(bytes, Status::Malformed);
    };
    for (index, page) in pages.iter().enumerate() {
        let Instance::Group(fields) = page else {
            return Extraction::empty(bytes, Status::Malformed);
        };
        if fields.len() != 1 || fields[0].0 != "Text" {
            return Extraction::empty(bytes, Status::Malformed);
        }
        let text = match &fields[0].1 {
            Instance::Scalar(Value::String(text)) => text.clone(),
            Instance::Scalar(Value::Null) => String::new(),
            _ => return Extraction::empty(bytes, Status::Malformed),
        };
        result.pages.push(PageText {
            page: (index + 1) as u32,
            missing_text: text.trim().is_empty(),
            text,
        });
    }
    if result.pages.iter().all(|page| page.missing_text) {
        result.status = Status::NeedsOcr;
    }
    // This deliberately never claims complete document understanding: images, tables,
    // handwriting and reading order have not been semantically qualified.
    result
}

pub fn encode(result: &Extraction) -> Result<Vec<u8>, serde_json::Error> {
    struct BoundedOutput {
        bytes: Vec<u8>,
        overflow: bool,
    }
    impl std::io::Write for BoundedOutput {
        fn write(&mut self, bytes: &[u8]) -> std::io::Result<usize> {
            if bytes.len() > MAX_RESULT_BYTES.saturating_sub(self.bytes.len()) {
                self.overflow = true;
                return Err(std::io::Error::other("result limit"));
            }
            self.bytes.extend_from_slice(bytes);
            Ok(bytes.len())
        }
        fn flush(&mut self) -> std::io::Result<()> {
            Ok(())
        }
    }
    let mut output = BoundedOutput {
        bytes: Vec::new(),
        overflow: false,
    };
    let encoded = serde_json::to_writer(&mut output, result);
    if !output.overflow {
        encoded?;
        return Ok(output.bytes);
    }
    serde_json::to_vec(&Extraction {
        format: result.format,
        extractor: result.extractor,
        source_sha256: result.source_sha256.clone(),
        source_byte_count: result.source_byte_count,
        status: Status::LimitExceeded,
        pages: Vec::new(),
    })
}
