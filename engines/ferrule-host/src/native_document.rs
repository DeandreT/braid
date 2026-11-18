//! Primary-only native Protocol Buffers and XBRL documents. The pinned Ferrule
//! codecs own parsing, evaluation and serialization; this module only admits
//! the saved boundary options and classifies native failures.
use ferrule_mapping::{FormatOptions, Project, XbrlBoundaryMode};
use serde::{Deserialize, Serialize};

pub const PROTOBUF_PROFILE: &str = "braid.ferrule-protobuf-preview.v1";
pub const XBRL_PROFILE: &str = "braid.ferrule-xbrl-preview.v1";
/// Canonical saved-options JSON per boundary. Input settings travel inside the
/// 64 KiB preview request document and both sides inside a regression case header.
pub const MAX_SETTINGS_BYTES: usize = 16 * 1024;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NativeFormat {
    Protobuf,
    Xbrl,
}

impl NativeFormat {
    pub fn from_profile(profile: &str) -> Option<Self> {
        match profile {
            PROTOBUF_PROFILE => Some(Self::Protobuf),
            XBRL_PROFILE => Some(Self::Xbrl),
            _ => None,
        }
    }

    /// The native format a boundary declares, if it declares one of these.
    pub fn of(options: &FormatOptions) -> Option<Self> {
        if options.protobuf.is_some() {
            Some(Self::Protobuf)
        } else if options.xbrl.is_some() {
            Some(Self::Xbrl)
        } else {
            None
        }
    }

    pub const fn profile(self) -> &'static str {
        match self {
            Self::Protobuf => PROTOBUF_PROFILE,
            Self::Xbrl => XBRL_PROFILE,
        }
    }

    pub const fn settings_schema(self) -> &'static str {
        match self {
            Self::Protobuf => "braid.protobuf-settings.v1",
            Self::Xbrl => "braid.xbrl-settings.v1",
        }
    }

    pub const fn build_refusal(self) -> &'static str {
        match self {
            Self::Protobuf => "unsupportedProtobufBuild",
            Self::Xbrl => "unsupportedXbrlBuild",
        }
    }

    const fn key(self) -> &'static str {
        match self {
            Self::Protobuf => "protobuf",
            Self::Xbrl => "xbrl",
        }
    }

    pub const fn execution_failure(self) -> &'static str {
        match self {
            Self::Protobuf => {
                "The native Protocol Buffers mapping or bounded serialization failed; no output was published."
            }
            Self::Xbrl => {
                "The native XBRL mapping or bounded serialization failed; no output was published."
            }
        }
    }
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Settings {
    pub schema: String,
    pub options_json: String,
}

pub fn settings(format: NativeFormat, options: &FormatOptions) -> Result<Settings, &'static str> {
    let (allowed, options_json) = match format {
        NativeFormat::Protobuf => {
            let value = options
                .protobuf
                .as_ref()
                .ok_or("Protocol Buffers requires an explicit saved schema and root message.")?;
            (
                FormatOptions {
                    protobuf: Some(value.clone()),
                    ..Default::default()
                },
                serde_json::to_string(value),
            )
        }
        NativeFormat::Xbrl => {
            let value = options
                .xbrl
                .as_ref()
                .ok_or("XBRL requires explicit saved boundary options.")?;
            (
                FormatOptions {
                    xbrl: Some(value.clone()),
                    ..Default::default()
                },
                serde_json::to_string(value),
            )
        }
    };
    if *options != allowed {
        return Err("The native document refuses conflicting or unsupported saved options.");
    }
    let options_json = options_json.map_err(|_| "Invalid native document options.")?;
    if options_json.len() > MAX_SETTINGS_BYTES {
        return Err("The saved native document options exceed the metadata limit.");
    }
    Ok(Settings {
        schema: format.settings_schema().into(),
        options_json,
    })
}

pub fn admit(format: NativeFormat, project: &Project) -> Result<(), &'static str> {
    if !project.extra_sources.is_empty()
        || !project.extra_targets.is_empty()
        || project.root.output_path().is_some()
    {
        return Err("Native documents support only primary static input and output documents.");
    }
    settings(format, &project.source_options)?;
    settings(format, &project.target_options)?;
    match format {
        NativeFormat::Protobuf => {
            for options in [&project.source_options, &project.target_options] {
                let protobuf = options.protobuf.as_ref().unwrap();
                layout(protobuf)
                    .and_then(|layout| layout.resolve_message(&protobuf.root_message))
                    .map_err(|_| {
                        "The saved Protocol Buffers schema or root message is not admitted by the pinned codec."
                    })?;
            }
        }
        NativeFormat::Xbrl => {
            if project.source_options.xbrl.as_ref().unwrap().mode()
                != XbrlBoundaryMode::ExternalSource
                || project.target_options.xbrl.as_ref().unwrap().mode()
                    != XbrlBoundaryMode::ExternalTarget
            {
                return Err("XBRL requires a saved source instance and a saved target instance.");
            }
        }
    }
    Ok(())
}

fn layout(
    options: &ferrule_mapping::ProtobufOptions,
) -> Result<ferrule_format_protobuf::Layout, ferrule_format_protobuf::ProtobufError> {
    // The same embedded-file resolution the pinned payload route uses; no file is opened.
    ferrule_format_protobuf::Layout::parse_files(
        options.schema_path.as_deref().unwrap_or("root.proto"),
        &options.schema,
        options
            .imports
            .iter()
            .map(|file| (file.path.as_str(), file.source.as_str())),
    )
}

/// The retained metadata must describe precisely the native options that execute:
/// unknown fields and omitted defaults are refused rather than silently normalized.
pub fn admit_saved_metadata(
    format: NativeFormat,
    native: &[u8],
    project: &Project,
) -> Result<(), &'static str> {
    let value: serde_json::Value =
        serde_json::from_slice(native).map_err(|_| "Invalid native metadata.")?;
    let root = if value.get("__ferrule_file").is_some() {
        &value["document"]
    } else {
        &value
    };
    for (key, options) in [
        ("source_options", &project.source_options),
        ("target_options", &project.target_options),
    ] {
        let saved = root[key].as_object().ok_or("Invalid saved options.")?;
        let effective = serde_json::to_value(options).map_err(|_| "Invalid saved options.")?;
        if saved
            .iter()
            .any(|(name, value)| effective.get(name) != Some(value))
            || saved.get(format.key()) != effective.get(format.key())
        {
            return Err(
                "Native documents require the pinned engine's canonical saved options, without omitted defaults or unknown settings.",
            );
        }
    }
    Ok(())
}

/// Native bounded read of the primary input. `Err(true)` is a codec limit.
pub fn read_input(format: NativeFormat, project: &Project, bytes: &[u8]) -> Result<(), bool> {
    match format {
        NativeFormat::Protobuf => {
            let options = project.source_options.protobuf.as_ref().unwrap();
            let layout = layout(options).map_err(|_| false)?;
            ferrule_format_protobuf::from_slice(&layout, &options.root_message, bytes)
                .map(drop)
                .map_err(|_| false)
        }
        NativeFormat::Xbrl => ferrule_format_xbrl::from_bytes_bounded(
            bytes,
            &project.source,
            project.source_options.xbrl.as_ref().unwrap(),
            &ferrule_format_xbrl::XbrlLimits::default(),
        )
        .map(drop)
        .map_err(|error| {
            matches!(
                error,
                ferrule_format_xbrl::XbrlFormatError::LimitExceeded { .. }
            )
        }),
    }
}

/// Protobuf output must decode with the saved target schema. The pinned XBRL
/// writer already re-checks its closed profile; UTF-8 keeps the text contract.
pub fn valid_output(format: NativeFormat, project: &Project, bytes: &[u8]) -> bool {
    match format {
        NativeFormat::Protobuf => {
            let options = project.target_options.protobuf.as_ref().unwrap();
            layout(options).is_ok_and(|layout| {
                ferrule_format_protobuf::from_slice(&layout, &options.root_message, bytes).is_ok()
            })
        }
        NativeFormat::Xbrl => std::str::from_utf8(bytes).is_ok(),
    }
}

pub fn limited(error: &(dyn std::error::Error + 'static)) -> bool {
    matches!(
        error.downcast_ref::<ferrule_format_xbrl::XbrlFormatError>(),
        Some(ferrule_format_xbrl::XbrlFormatError::LimitExceeded { .. })
    ) || matches!(
        error.downcast_ref::<ferrule_format_protobuf::BoundedEncodeError>(),
        Some(ferrule_format_protobuf::BoundedEncodeError::LimitExceeded { .. })
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn exact_option_bags_and_metadata_limit() {
        let protobuf = ferrule_mapping::ProtobufOptions {
            schema: "syntax = \"proto3\"; message Doc { string text = 1; }".into(),
            root_message: "Doc".into(),
            schema_path: None,
            imports: vec![],
        };
        let options = FormatOptions {
            protobuf: Some(protobuf.clone()),
            ..Default::default()
        };
        let saved = settings(NativeFormat::Protobuf, &options).unwrap();
        assert_eq!(saved.schema, "braid.protobuf-settings.v1");
        assert!(settings(NativeFormat::Xbrl, &options).is_err());
        assert!(
            settings(
                NativeFormat::Protobuf,
                &FormatOptions {
                    json_document: true,
                    ..options.clone()
                }
            )
            .is_err()
        );
        let overhead = saved.options_json.len() - protobuf.schema.len();
        let fill = |extra: usize| FormatOptions {
            protobuf: Some(ferrule_mapping::ProtobufOptions {
                schema: format!("{}{}", protobuf.schema, " ".repeat(extra)),
                ..protobuf.clone()
            }),
            ..Default::default()
        };
        let room = MAX_SETTINGS_BYTES - overhead - protobuf.schema.len();
        assert!(settings(NativeFormat::Protobuf, &fill(room)).is_ok());
        assert!(settings(NativeFormat::Protobuf, &fill(room + 1)).is_err());
        assert_eq!(NativeFormat::of(&options), Some(NativeFormat::Protobuf));
        assert_eq!(NativeFormat::of(&FormatOptions::default()), None);
    }
}
