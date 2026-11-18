//! Primary-only native FlexText. No independent parser, evaluator or serializer.
use ferrule_mapping::{FlexCommand, FormatOptions, OnceSplitter, Project};
use serde::{Deserialize, Serialize};

pub const PROFILE: &str = "braid.ferrule-flextext-preview.v1";
pub const BUILD_REFUSAL: &str =
    "The pinned Ferrule engine has no generated C# FlexText document-byte adapter.";
const UNSAFE_PADDING: &str = "The pinned FlexText writer cannot bound fixed-column padding before allocation; this output mode is unavailable.";
const UNSAFE_AGGREGATE: &str = "The pinned FlexText writer cannot preflight aggregate record or arm output allocation; this output mode is unavailable.";

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Settings {
    pub schema: String,
    pub layout_json: String,
}

pub fn settings(options: &FormatOptions) -> Result<Settings, &'static str> {
    let layout = options
        .flextext
        .as_ref()
        .ok_or("FlexText requires an explicit saved layout.")?;
    let allowed = FormatOptions {
        flextext: Some(layout.clone()),
        ..Default::default()
    };
    if *options != allowed {
        return Err("FlexText refuses conflicting or unsupported saved options.");
    }
    let layout_json = serde_json::to_string(layout).map_err(|_| "Invalid FlexText layout.")?;
    if layout_json.len() > 16 * 1024 {
        return Err("FlexText layout exceeds the metadata limit.");
    }
    Ok(Settings {
        schema: "braid.flextext-settings.v1".into(),
        layout_json,
    })
}

pub fn admit(project: &Project) -> Result<(), &'static str> {
    if !project.extra_sources.is_empty()
        || !project.extra_targets.is_empty()
        || project.root.output_path().is_some()
    {
        return Err("FlexText supports only primary static input and output documents.");
    }
    settings(&project.source_options)?;
    settings(&project.target_options)?;
    safe_output(project.target_options.flextext.as_ref().unwrap().command())?;
    if project.source != project.source_options.flextext.as_ref().unwrap().schema()
        || project.target != project.target_options.flextext.as_ref().unwrap().schema()
    {
        return Err("FlexText schema must equal its saved layout schema.");
    }
    Ok(())
}

/// The pinned writer does not expose a pre-render aggregate budget for every
/// mode. Do not enter allocation-before-budget paths, even with small widths:
/// FixedWidthRecords and DelimitedRecords collect all rows before bounded_join;
/// Switch collects all present arms before checking the selected shape.
fn safe_output(command: &FlexCommand) -> Result<(), &'static str> {
    match command {
        FlexCommand::Ignore | FlexCommand::Store { .. } => Ok(()),
        FlexCommand::SplitOnce {
            splitter: OnceSplitter::FixedColumns(_),
            ..
        } => Err(UNSAFE_PADDING),
        FlexCommand::SplitOnce { first, second, .. } => {
            safe_output(first)?;
            safe_output(second)
        }
        FlexCommand::SplitMany { child, .. } => safe_output(child),
        FlexCommand::FixedWidthRecords { .. }
        | FlexCommand::DelimitedRecords { .. }
        | FlexCommand::Switch { .. } => Err(UNSAFE_AGGREGATE),
    }
}

/// The retained metadata must describe precisely the native layout that executes.
/// Require the pin's own saved form rather than silently dropping unknown fields or
/// displaying omitted defaults as though they were an independently supplied setting.
pub fn admit_saved_metadata(native: &[u8], project: &Project) -> Result<(), &'static str> {
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
        let saved_options = root[key].as_object().ok_or("Invalid saved options.")?;
        let effective_options =
            serde_json::to_value(options).map_err(|_| "Invalid saved options.")?;
        if saved_options
            .iter()
            .any(|(name, value)| effective_options.get(name) != Some(value))
        {
            return Err("FlexText refuses unknown or noncanonical saved options.");
        }
        let saved = &root[key]["flextext"];
        let effective = serde_json::to_value(options.flextext.as_ref().ok_or("Missing layout.")?)
            .map_err(|_| "Invalid layout metadata.")?;
        if saved != &effective {
            return Err(
                "FlexText requires the pinned engine's canonical saved layout, without omitted defaults or unknown layout settings.",
            );
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use ferrule_mapping::{FixedWidthRecordField, ManySplitter};
    use std::num::NonZeroU32;

    fn store() -> FlexCommand {
        FlexCommand::store("text", ferrule_ir::ScalarType::String, None)
    }

    #[test]
    fn supplementary_characters_use_the_same_utf8_layout_budget_as_dotnet() {
        let options = |name| FormatOptions {
            flextext: Some(
                ferrule_mapping::FlexTextLayout::new(
                    name,
                    store(),
                    ferrule_mapping::FlexLineEnding::Lf,
                    false,
                )
                .unwrap(),
            ),
            ..Default::default()
        };
        let overhead = settings(&options("D".to_string()))
            .unwrap()
            .layout_json
            .len()
            - 1;
        let count = (16 * 1024 - overhead) / 4;
        let accepted = settings(&options("\u{1f680}".repeat(count))).unwrap();
        assert_eq!(accepted.layout_json.len(), overhead + count * 4);
        assert!(accepted.layout_json.contains('\u{1f680}'));
        assert!(settings(&options("\u{1f680}".repeat(count + 1))).is_err());
    }

    #[test]
    fn fixed_record_width_and_total_record_allocation_are_both_refused() {
        for width in [1, u32::MAX] {
            let command = FlexCommand::FixedWidthRecords {
                name: "rows".into(),
                fields: vec![
                    FixedWidthRecordField::new(
                        "text",
                        ferrule_ir::ScalarType::String,
                        NonZeroU32::new(width).unwrap(),
                    )
                    .unwrap(),
                ],
                fill_char: ' ',
                record_delimiters: true,
                treat_empty_as_absent: true,
            };
            assert_eq!(safe_output(&command), Err(UNSAFE_AGGREGATE));
            let mut project = crate::decode::decode_native(include_bytes!(
                "../tests/fixtures/flextext.native.json"
            ))
            .unwrap()
            .project;
            let target = ferrule_mapping::FlexTextLayout::new(
                "Document",
                command.clone(),
                ferrule_mapping::FlexLineEnding::Lf,
                false,
            )
            .unwrap();
            project.target = target.schema();
            project.target_options.flextext = Some(target);
            assert_eq!(admit(&project), Err(UNSAFE_AGGREGATE));
            // Nesting inside a bounded split does not make its child serializer safe.
            assert!(
                safe_output(&FlexCommand::SplitMany {
                    name: "records".into(),
                    splitter: ManySplitter::FixedLines(NonZeroU32::new(1).unwrap()),
                    child: Box::new(command),
                })
                .is_err()
            );
        }
    }

    #[test]
    fn fixed_column_padding_is_refused_before_native_rendering() {
        let command = FlexCommand::SplitOnce {
            name: "record".into(),
            splitter: OnceSplitter::FixedColumns(NonZeroU32::new(u32::MAX).unwrap()),
            first: Box::new(store()),
            second: Box::new(FlexCommand::store(
                "rest",
                ferrule_ir::ScalarType::String,
                None,
            )),
        };
        assert_eq!(safe_output(&command), Err(UNSAFE_PADDING));
    }

    #[test]
    fn sequential_bounded_split_keeps_only_the_safe_child_profiles() {
        assert!(
            safe_output(&FlexCommand::SplitMany {
                name: "records".into(),
                splitter: ManySplitter::Delimiter("|".into()),
                child: Box::new(store())
            })
            .is_ok()
        );
        let switch = FlexCommand::Switch {
            name: "choice".into(),
            mode: ferrule_mapping::SwitchMode::FirstMatch,
            arms: vec![],
            default: Some(Box::new(store())),
        };
        assert!(safe_output(&switch).is_err());
        let delimited = FlexCommand::DelimitedRecords {
            name: "rows".into(),
            dialect: ferrule_mapping::DelimitedDialect::new(',', "\n", '"', '\\').unwrap(),
            fields: vec![
                ferrule_mapping::DelimitedRecordField::new("text", ferrule_ir::ScalarType::String)
                    .unwrap(),
            ],
        };
        assert!(safe_output(&delimited).is_err());
    }
}
