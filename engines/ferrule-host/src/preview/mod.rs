//! Bounded sample preview through Ferrule's public in-memory payload API.

use std::io::{self, Read, Write};
use std::path::{Path, PathBuf};

use ferrule_cli::{NamedPayloadInput, PayloadDocument, PayloadRunOptions};
use ferrule_engine::{EngineError, RuntimeParameters};
use ferrule_ir::Value;
use ferrule_mapping::FileCodecError;
use sha2::{Digest, Sha256};

pub mod admission;
pub mod protocol;

use admission::Refusal;

use crate::protocol::{MAX_NATIVE_BYTES, MAX_TEXT_BYTES, bounded_text};
use crate::{decode, framing, valid_context_parts};
use protocol::{
    DOCUMENT_PREVIEW_PROTOCOL_VERSION, MAX_PREVIEW_ARTIFACTS, MAX_PREVIEW_DISCARD_BYTES,
    MAX_PREVIEW_INPUT_BYTES, MAX_PREVIEW_INPUTS, MAX_PREVIEW_OUTPUT_BYTES,
    MAX_PREVIEW_PARAMETER_NAME_BYTES, MAX_PREVIEW_PARAMETER_TEXT_BYTES, MAX_PREVIEW_PARAMETERS,
    MAX_PREVIEW_RESPONSE_BYTES, MAX_PREVIEW_TARGET_NAME_BYTES, PREVIEW_PROTOCOL_VERSION,
    PreviewArtifact, PreviewFailure, PreviewInput, PreviewRequestHeader, PreviewResponse,
    PreviewResult, PreviewTarget, PreviewValue,
};

const PROJECT_ROOT: &str = "/weft";
const MINIMUM_TEXT_BYTES: u32 = 64;

/// A complete preview reply: the response header and its declared output bytes.
pub struct PreviewOutcome {
    pub response: PreviewResponse,
    pub outputs: Vec<Vec<u8>>,
}

impl PreviewOutcome {
    fn failure(
        header: Option<&PreviewRequestHeader>,
        code: &str,
        message: &str,
        subject: Option<&str>,
    ) -> Self {
        let maximum = header
            .map(|header| header.limits.maximum_text_bytes as usize)
            .filter(|maximum| {
                (MINIMUM_TEXT_BYTES as usize..=MAX_TEXT_BYTES as usize).contains(maximum)
            })
            .unwrap_or(MAX_TEXT_BYTES as usize);
        let (message, message_truncated) = bounded_text(message, maximum);
        let (subject, subject_truncated) =
            match subject.map(|subject| bounded_text(subject, maximum)) {
                Some((subject, truncated)) => (Some(subject), truncated),
                None => (None, false),
            };
        Self {
            response: PreviewResponse::failure(
                header,
                PreviewFailure {
                    code: code.into(),
                    message,
                    message_truncated,
                    subject,
                    subject_truncated,
                },
            ),
            outputs: Vec::new(),
        }
    }
}

/// Reads the remainder of a preview request whose header frame is already admitted.
pub fn handle_preview(
    header_bytes: &[u8],
    reader: &mut impl Read,
    writer: &mut impl Write,
) -> io::Result<()> {
    let Ok(header) = serde_json::from_slice::<PreviewRequestHeader>(header_bytes) else {
        discard(reader);
        return write_outcome(
            writer,
            &PreviewOutcome::failure(
                None,
                "invalidHeader",
                "The preview header is not an admitted UTF-8 JSON object.",
                None,
            ),
        );
    };
    if let Err(refusal) = check_header(&header) {
        discard(reader);
        return write_outcome(writer, &refused(&header, &refusal));
    }
    let native = match framing::read_frame(reader, MAX_NATIVE_BYTES) {
        Ok(native) => native,
        Err(message) => {
            discard(reader);
            return write_outcome(
                writer,
                &PreviewOutcome::failure(Some(&header), "invalidFrame", message, None),
            );
        }
    };
    let mut inputs = Vec::with_capacity(header.inputs.len());
    for declared in &header.inputs {
        match read_declared_frame(reader, declared.byte_length() as usize) {
            Ok(bytes) => inputs.push(bytes),
            Err(message) => {
                discard(reader);
                return write_outcome(
                    writer,
                    &PreviewOutcome::failure(Some(&header), "invalidFrame", message, None),
                );
            }
        }
    }
    if let Err(message) = framing::require_eof(reader) {
        return write_outcome(
            writer,
            &PreviewOutcome::failure(Some(&header), "invalidFrame", message, None),
        );
    }
    write_outcome(writer, &process_preview(&header, &native, &inputs))
}

/// Executes one admitted request against exact native and input bytes.
pub fn process_preview(
    header: &PreviewRequestHeader,
    native: &[u8],
    inputs: &[Vec<u8>],
) -> PreviewOutcome {
    if let Err(refusal) = check_header(header) {
        return refused(header, &refusal);
    }
    let failure = |code: &str, message: &str, subject: Option<&str>| {
        PreviewOutcome::failure(Some(header), code, message, subject)
    };
    if inputs.len() != header.inputs.len() {
        return failure(
            "invalidFrame",
            "The input frames do not match their declarations.",
            None,
        );
    }
    if native.is_empty() || native.len() > MAX_NATIVE_BYTES {
        return failure(
            "invalidFrame",
            "The native document exceeds its admitted byte range.",
            None,
        );
    }
    if hex_digest(native) != header.native_digest {
        return failure(
            "digestMismatch",
            "The native bytes do not match the requested SHA-256 digest.",
            None,
        );
    }
    for (declared, bytes) in header.inputs.iter().zip(inputs) {
        if bytes.len() != declared.byte_length() as usize || hex_digest(bytes) != declared.sha256()
        {
            return failure(
                "digestMismatch",
                "An input document does not match its declared length and SHA-256 digest.",
                Some(input_subject(declared)),
            );
        }
    }
    let decoded = match decode::decode_native(native) {
        Ok(decoded) => decoded,
        Err(error) => {
            let code = if matches!(&error, FileCodecError::UnsupportedVersion { .. }) {
                "unsupportedNativeVersion"
            } else {
                "nativeDecodeFailed"
            };
            return failure(code, &error.to_string(), None);
        }
    };
    let mut project = decoded.project;
    let issues = ferrule_engine::validate(&project).len();
    if issues > 0 {
        return failure(
            "invalidMapping",
            &format!(
                "The mapping has {issues} validation issue(s); preview requires a valid mapping."
            ),
            None,
        );
    }
    let admission = match admission::admit(&project, header) {
        Ok(admission) => admission,
        Err(refusal) => return refused(header, &refusal),
    };
    if header.is_document_preview() {
        for ((input, bytes), format) in header
            .inputs
            .iter()
            .zip(inputs)
            .zip(&admission.input_formats)
        {
            let options = match input {
                PreviewInput::Primary { .. } => &project.source_options,
                PreviewInput::Named { name, .. } => project
                    .extra_sources
                    .iter()
                    .find(|source| source.name == *name)
                    .map(|source| &source.options)
                    .unwrap_or(&project.source_options),
            };
            let envelope = crate::edi_envelope::validate(*format, bytes);
            if !crate::document_limits::within_limits(
                *format,
                bytes,
                options.x12_separators.map(|settings| settings.segment),
            ) {
                return failure(
                    "inputLimitExceeded",
                    "The document exceeds its format-specific byte, nesting or segment limits.",
                    Some(input_subject(input)),
                );
            }
            if let Err(location) = envelope {
                return failure(
                    "invalidInput",
                    &format!(
                        "Invalid EDI envelope at interchange {} segment {} {} element {}.",
                        location.interchange, location.segment, location.tag, location.element
                    ),
                    Some(input_subject(input)),
                );
            }
        }
    }

    let mut parameters = RuntimeParameters::new();
    for parameter in &header.parameters {
        let value = match parameter_value(&parameter.value) {
            Some(value) => value,
            None => {
                return failure(
                    "invalidParameter",
                    "A runtime parameter value is not admitted for its declared kind.",
                    Some(&parameter.name),
                );
            }
        };
        if parameters.insert(parameter.name.clone(), value).is_err() {
            return failure(
                "invalidParameter",
                "A runtime parameter is outside Ferrule's host parameter bounds.",
                Some(&parameter.name),
            );
        }
    }

    let paths = header
        .inputs
        .iter()
        .zip(&admission.input_formats)
        .enumerate()
        .map(|(index, (input, format))| match input {
            PreviewInput::Primary { .. } => {
                PathBuf::from(format!("/weft/input/primary.{}", format.extension()))
            }
            PreviewInput::Named { .. } => {
                PathBuf::from(format!("/weft/input/named-{index}.{}", format.extension()))
            }
        })
        .collect::<Vec<_>>();
    let mut primary = None;
    let mut documents = Vec::new();
    for ((input, bytes), path) in header.inputs.iter().zip(inputs).zip(&paths) {
        let Ok(document) = PayloadDocument::new(path, bytes) else {
            return failure(
                "invalidInput",
                "An input document exceeds Ferrule's payload bounds.",
                Some(input_subject(input)),
            );
        };
        match input {
            PreviewInput::Primary { .. } => primary = Some(document),
            PreviewInput::Named { name, .. } => documents.push((name.as_str(), document)),
        }
    }
    let Some(primary) = primary else {
        return failure("invalidRequest", "The primary input is missing.", None);
    };
    let mut named = Vec::with_capacity(documents.len());
    for (name, document) in documents {
        let Ok(input) = NamedPayloadInput::new(name, document) else {
            return failure(
                "invalidRequest",
                "A named input identity exceeds Ferrule's payload bounds.",
                Some(name),
            );
        };
        named.push(input);
    }

    // Only synthetic identities reach the document renderer; design-time paths
    // neither select a codec nor escape into output artifact identities.
    if header.is_document_preview() {
        for target in &admission.targets {
            if let Some(index) = target
                .boundary_id
                .strip_prefix("named-output:")
                .and_then(|index| index.parse::<usize>().ok())
            {
                project.extra_targets[index].path = Some(format!(
                    "/weft/output/named-{index}.{}",
                    target.format.extension()
                ));
            }
        }
    }
    let output_path = PathBuf::from(format!(
        "/weft/output/primary.{}",
        admission.targets[0].format.extension()
    ));
    let mut options = PayloadRunOptions::new(primary)
        .with_extra_sources(&named)
        .with_output_path(&output_path)
        .with_runtime_parameters(&parameters);
    if let Some(selection) = admission.selection {
        options = options.with_target(selection);
    }
    let options = match options.with_current_datetime(&header.current_date_time) {
        Ok(options) => options,
        Err(_) => {
            return failure(
                "invalidClock",
                "The supplied execution clock is not an admitted dateTime with an explicit offset.",
                None,
            );
        }
    };
    let project_path = Path::new(PROJECT_ROOT).join(&header.document_id);
    let outcome = match ferrule_cli::run_project_value_payloads(&project, &project_path, &options) {
        Ok(outcome) => outcome,
        Err(error) => {
            let (code, subject) = classify(&error, header);
            return failure(code, &format!("{error:#}"), subject.as_deref());
        }
    };

    if outcome.artifacts.len() != admission.targets.len()
        || outcome
            .artifacts
            .iter()
            .zip(&admission.targets)
            .any(|(artifact, target)| artifact.target != target.name)
    {
        return failure(
            "executionFailed",
            "The engine produced outputs that do not match the admitted static targets.",
            None,
        );
    }
    let total = outcome
        .artifacts
        .iter()
        .try_fold(0usize, |total, artifact| {
            total.checked_add(artifact.bytes.len())
        });
    if outcome.artifacts.len() > header.limits.maximum_artifacts as usize
        || total.is_none_or(|total| total > header.limits.maximum_output_bytes as usize)
    {
        return failure(
            "resultLimitExceeded",
            "The preview outputs exceed the requested output limits.",
            None,
        );
    }
    let maximum = header.limits.maximum_text_bytes as usize;
    let mut artifacts = Vec::with_capacity(outcome.artifacts.len());
    let mut outputs = Vec::with_capacity(outcome.artifacts.len());
    for (artifact, target) in outcome.artifacts.into_iter().zip(admission.targets) {
        if header.is_document_preview()
            && !crate::document_limits::within_limits(
                target.format,
                &artifact.bytes,
                target.segment,
            )
        {
            return failure(
                "resultLimitExceeded",
                "The output exceeds its format-specific byte, nesting or segment limits.",
                Some(&target.boundary_id),
            );
        }
        let (target_name, target_name_truncated) = bounded_text(&target.name, maximum);
        artifacts.push(PreviewArtifact {
            boundary_id: target.boundary_id,
            target_name,
            target_name_truncated,
            media_type: target.format.media_type().into(),
            records_written: artifact.records_written as u64,
            byte_length: artifact.bytes.len() as u32,
            sha256: hex_digest(&artifact.bytes),
        });
        outputs.push(artifact.bytes);
    }
    PreviewOutcome {
        response: PreviewResponse::success(
            header,
            PreviewResult {
                records_written: outcome.records_written as u64,
                artifacts,
            },
        ),
        outputs,
    }
}

/// Writes the bounded response header followed by exactly its declared output frames.
pub fn write_outcome(writer: &mut impl Write, outcome: &PreviewOutcome) -> io::Result<()> {
    let (bytes, outputs) =
        match framing::encode_bounded(&outcome.response, MAX_PREVIEW_RESPONSE_BYTES) {
            Ok(bytes) => (bytes, outcome.outputs.as_slice()),
            Err(_) => {
                let mut response = outcome.response.clone();
                response.result = None;
                response.failure = Some(PreviewFailure {
                    code: "resultLimitExceeded".into(),
                    message: "The preview response exceeds its compiled byte limit.".into(),
                    message_truncated: false,
                    subject: None,
                    subject_truncated: false,
                });
                (
                    framing::encode_bounded(&response, MAX_PREVIEW_RESPONSE_BYTES)
                        .map_err(io::Error::other)?,
                    &[][..],
                )
            }
        };
    write_frame(writer, &bytes)?;
    for output in outputs {
        write_frame(writer, output)?;
    }
    writer.flush()
}

fn write_frame(writer: &mut impl Write, bytes: &[u8]) -> io::Result<()> {
    let length = u32::try_from(bytes.len()).map_err(io::Error::other)?;
    writer.write_all(&length.to_be_bytes())?;
    writer.write_all(bytes)
}

fn refused(header: &PreviewRequestHeader, refusal: &Refusal) -> PreviewOutcome {
    PreviewOutcome::failure(
        Some(header),
        refusal.code,
        refusal.message,
        refusal.subject.as_deref(),
    )
}

fn check_header(header: &PreviewRequestHeader) -> Result<(), Refusal> {
    let failure = |code: &'static str, message: &'static str, subject: Option<&str>| {
        Err(Refusal {
            code,
            message,
            subject: subject.map(Into::into),
        })
    };
    if !matches!(
        header.protocol_version.as_str(),
        PREVIEW_PROTOCOL_VERSION | DOCUMENT_PREVIEW_PROTOCOL_VERSION
    ) {
        return failure(
            "unsupportedProtocolVersion",
            "The requested preview protocol version is not supported.",
            None,
        );
    }
    if !valid_context_parts(
        &header.request_id,
        &header.revision,
        &header.native_digest,
        &header.document_id,
    ) {
        return failure(
            "invalidContext",
            "The revision, request, digest, or logical document identity is invalid.",
            None,
        );
    }
    let limits = &header.limits;
    if !(1..=MAX_PREVIEW_INPUT_BYTES as u32).contains(&limits.maximum_input_bytes)
        || !(1..=MAX_PREVIEW_OUTPUT_BYTES as u32).contains(&limits.maximum_output_bytes)
        || !(1..=MAX_PREVIEW_ARTIFACTS as u32).contains(&limits.maximum_artifacts)
        || !(MINIMUM_TEXT_BYTES..=MAX_TEXT_BYTES).contains(&limits.maximum_text_bytes)
    {
        return failure(
            "invalidLimits",
            "The requested limits are outside the compiled preview admission range.",
            None,
        );
    }
    if header.inputs.is_empty()
        || header.inputs.len() > MAX_PREVIEW_INPUTS
        || !matches!(header.inputs[0], PreviewInput::Primary { .. })
        || header.inputs[1..]
            .iter()
            .any(|input| matches!(input, PreviewInput::Primary { .. }))
    {
        return failure(
            "invalidRequest",
            "Inputs must declare exactly one leading primary document and bounded named documents.",
            None,
        );
    }
    let mut names = std::collections::BTreeSet::new();
    let mut total = 0u64;
    for input in &header.inputs {
        if input.byte_length() == 0
            || input.sha256().len() != 64
            || !input
                .sha256()
                .bytes()
                .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
        {
            return failure(
                "invalidRequest",
                "An input declaration has an invalid length or digest.",
                Some(input_subject(input)),
            );
        }
        if let PreviewInput::Named { name, .. } = input
            && (name.is_empty()
                || name.len() > MAX_PREVIEW_TARGET_NAME_BYTES
                || name.contains('\0')
                || !names.insert(name.as_str()))
        {
            return failure(
                "invalidRequest",
                "Named inputs must have distinct bounded names.",
                Some(name),
            );
        }
        total += u64::from(input.byte_length());
    }
    if total > u64::from(limits.maximum_input_bytes) {
        return failure(
            "inputLimitExceeded",
            "The declared input documents exceed the requested input byte limit.",
            None,
        );
    }
    if let PreviewTarget::Named { name } = &header.target
        && (name.is_empty() || name.len() > MAX_PREVIEW_TARGET_NAME_BYTES)
    {
        return failure(
            "invalidRequest",
            "The selected target name is outside its admitted bounds.",
            None,
        );
    }
    if header.parameters.len() > MAX_PREVIEW_PARAMETERS {
        return failure(
            "invalidRequest",
            "The request exceeds the runtime parameter count limit.",
            None,
        );
    }
    let mut parameter_names = std::collections::BTreeSet::new();
    for parameter in &header.parameters {
        if parameter.name.is_empty()
            || parameter.name.len() > MAX_PREVIEW_PARAMETER_NAME_BYTES
            || parameter.name.contains('\0')
            || !parameter_names.insert(parameter.name.as_str())
        {
            return failure(
                "invalidRequest",
                "Runtime parameters must have distinct bounded names.",
                Some(&parameter.name),
            );
        }
        let text = match &parameter.value {
            PreviewValue::String(text)
            | PreviewValue::Integer(text)
            | PreviewValue::Float(text) => text.len(),
            PreviewValue::Boolean(_) => 0,
        };
        if text > MAX_PREVIEW_PARAMETER_TEXT_BYTES || parameter_value(&parameter.value).is_none() {
            return failure(
                "invalidParameter",
                "A runtime parameter value is not admitted for its declared kind.",
                Some(&parameter.name),
            );
        }
    }
    Ok(())
}

/// Converts exact decimal text without accepting alternate spellings or non-finite values.
fn parameter_value(value: &PreviewValue) -> Option<Value> {
    match value {
        PreviewValue::String(text) => Some(Value::String(text.clone())),
        PreviewValue::Boolean(value) => Some(Value::Bool(*value)),
        PreviewValue::Integer(text) => {
            let parsed = text.parse::<i64>().ok()?;
            (parsed.to_string() == *text).then_some(Value::Int(parsed))
        }
        PreviewValue::Float(text) => {
            if !json_number(text) {
                return None;
            }
            let parsed = text.parse::<f64>().ok()?;
            parsed.is_finite().then_some(Value::Float(parsed))
        }
    }
}

fn json_number(text: &str) -> bool {
    let bytes = text.as_bytes();
    let mut index = usize::from(bytes.first() == Some(&b'-'));
    let digits = |index: &mut usize| {
        let start = *index;
        while bytes.get(*index).is_some_and(u8::is_ascii_digit) {
            *index += 1;
        }
        *index - start
    };
    match bytes.get(index) {
        Some(b'0') => index += 1,
        Some(b'1'..=b'9') => {
            digits(&mut index);
        }
        _ => return false,
    }
    if bytes.get(index) == Some(&b'.') {
        index += 1;
        if digits(&mut index) == 0 {
            return false;
        }
    }
    if matches!(bytes.get(index), Some(b'e' | b'E')) {
        index += 1;
        if matches!(bytes.get(index), Some(b'+' | b'-')) {
            index += 1;
        }
        if digits(&mut index) == 0 {
            return false;
        }
    }
    index == bytes.len()
}

fn classify(
    error: &anyhow::Error,
    header: &PreviewRequestHeader,
) -> (&'static str, Option<String>) {
    for cause in error.chain() {
        if let Some(engine) = cause.downcast_ref::<EngineError>() {
            return match engine {
                EngineError::MissingRuntimeParameter { name, .. } => {
                    ("missingParameter", Some(name.clone()))
                }
                EngineError::RuntimeParameterType { name, .. } => {
                    ("invalidParameter", Some(name.clone()))
                }
                EngineError::UnsupportedSourceOwnership { .. }
                | EngineError::UnsupportedOwnedContext { .. }
                | EngineError::UnsupportedSourceIteration { .. } => ("unsupportedCapability", None),
                EngineError::UnknownTarget { name } | EngineError::AmbiguousTarget { name } => {
                    ("unknownTarget", Some(name.clone()))
                }
                _ => ("executionFailed", None),
            };
        }
    }
    let context = error.to_string();
    if context == "reading primary input payload" {
        return ("invalidInput", Some("primary".into()));
    }
    for input in &header.inputs {
        if let PreviewInput::Named { name, .. } = input
            && context == format!("reading payload source `{name}`")
        {
            return ("invalidInput", Some(name.clone()));
        }
    }
    ("executionFailed", None)
}

fn input_subject(input: &PreviewInput) -> &str {
    match input {
        PreviewInput::Primary { .. } => "primary",
        PreviewInput::Named { name, .. } => name,
    }
}

fn hex_digest(bytes: &[u8]) -> String {
    format!("{:x}", Sha256::digest(bytes))
}

fn read_declared_frame(reader: &mut impl Read, expected: usize) -> Result<Vec<u8>, &'static str> {
    let mut prefix = [0u8; 4];
    reader
        .read_exact(&mut prefix)
        .map_err(|_| "An input frame length is incomplete.")?;
    if u32::from_be_bytes(prefix) as usize != expected {
        return Err("An input frame length does not match its declaration.");
    }
    let mut bytes = vec![0; expected];
    reader
        .read_exact(&mut bytes)
        .map_err(|_| "An input frame body is incomplete.")?;
    Ok(bytes)
}

/// Consumes a bounded remainder so the host can finish writing before reading the refusal.
fn discard(reader: &mut impl Read) {
    let _ = io::copy(
        &mut reader.take(MAX_PREVIEW_DISCARD_BYTES as u64),
        &mut io::sink(),
    );
}
