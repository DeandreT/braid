//! Primary-only flat XLSX through the pinned bounded reader and new-workbook writer.
//! No independent ZIP, XML or cell codec; Braid only narrows the native limits.
use ferrule_format_xlsx::{XlsxFormatError, XlsxLimitOptions, XlsxLimits};
use ferrule_mapping::{FormatOptions, Project, TabularBoundaryKind};
use serde::{Deserialize, Serialize};

pub const PROFILE: &str = "braid.ferrule-xlsx-preview.v1";
pub const READ_JSON_PROFILE: &str = "braid.ferrule-xlsx-to-json-preview.v1";
pub const SERVICE_OUTPUT_REFUSAL: &str =
    "XLSX output requires a worksheet thread unavailable in the restricted service runner.";
pub const SETTINGS_SCHEMA: &str = "braid.xlsx-settings.v1";
pub const MEDIA_TYPE: &str = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
pub const MAX_DOCUMENT_BYTES: usize = 8 * 1024 * 1024;
/// Package-level expansion ratio: all decompressed parts against the compressed workbook.
pub const MAX_EXPANSION_RATIO: usize = 200;
const MAX_SETTINGS_BYTES: usize = 16 * 1024;

/// The saved flat-table settings with the pinned engine's defaults resolved.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Settings {
    pub schema: String,
    pub sheet: Option<String>,
    pub start_row: u32,
    pub columns: Vec<u32>,
    pub headers: Vec<String>,
    pub has_header_row: bool,
}

/// Closed allowlist: flat sheet selection only. Update-existing, transposed,
/// composite, grid, worksheet-set and hierarchical layouts are refused. Header
/// overrides are output-only because the pinned reader does not consume them.
pub fn settings(options: &FormatOptions, output: bool) -> Result<Settings, &'static str> {
    if options.tabular_kind != Some(TabularBoundaryKind::Xlsx) {
        return Err("XLSX requires explicit saved tabular_kind=Xlsx.");
    }
    let allowed = FormatOptions {
        tabular_kind: options.tabular_kind,
        has_header_row: options.has_header_row,
        xlsx_sheet: options.xlsx_sheet.clone(),
        xlsx_start_row: options.xlsx_start_row,
        xlsx_columns: options.xlsx_columns.clone(),
        xlsx_headers: if output {
            options.xlsx_headers.clone()
        } else {
            Vec::new()
        },
        ..Default::default()
    };
    if *options != allowed {
        return Err(
            "XLSX supports only flat new-workbook sheet settings; update-existing and other layouts are refused.",
        );
    }
    let settings = Settings {
        schema: SETTINGS_SCHEMA.into(),
        sheet: options.xlsx_sheet.clone(),
        start_row: options.xlsx_start_row.unwrap_or(1),
        columns: options.xlsx_columns.clone(),
        headers: options.xlsx_headers.clone(),
        has_header_row: options.has_header_row.unwrap_or(true),
    };
    if settings.sheet.as_ref().is_some_and(|sheet| {
        sheet.is_empty() || sheet.chars().count() > 31 || sheet.chars().any(char::is_control)
    }) || settings.start_row == 0
        || settings.start_row > 1_048_576
        || settings.columns.len() > 256
        || settings
            .columns
            .iter()
            .any(|column| !(1..=16_384).contains(column))
        || settings.headers.len() > 256
        || !serde_json::to_vec(&settings).is_ok_and(|json| json.len() <= MAX_SETTINGS_BYTES)
    {
        return Err("XLSX sheet settings are outside their metadata limits.");
    }
    Ok(settings)
}

pub fn admit(project: &Project) -> Result<(), &'static str> {
    if !project.extra_sources.is_empty()
        || !project.extra_targets.is_empty()
        || project.root.output_path().is_some()
    {
        return Err("XLSX supports only primary static input and new output workbooks.");
    }
    settings(&project.source_options, false)?;
    settings(&project.target_options, true)?;
    Ok(())
}

/// The pinned options type ignores unknown saved fields; refuse them instead so
/// the retained settings describe exactly what executes.
pub fn admit_saved_metadata(native: &[u8], project: &Project) -> Result<(), &'static str> {
    let value: serde_json::Value =
        serde_json::from_slice(native).map_err(|_| "Invalid native metadata.")?;
    let root = value
        .get("document")
        .filter(|_| value.get("__ferrule_file").is_some());
    let root = root.unwrap_or(&value);
    for (key, options) in [
        ("source_options", &project.source_options),
        ("target_options", &project.target_options),
    ] {
        let saved = root[key].as_object().ok_or("Invalid saved options.")?;
        let effective = serde_json::to_value(options).map_err(|_| "Invalid saved options.")?;
        if saved
            .iter()
            .any(|(name, value)| effective.get(name) != Some(value))
        {
            return Err("XLSX refuses unknown or noncanonical saved options.");
        }
    }
    Ok(())
}

/// Braid's per-document budget inside the native bounded profile. The expansion
/// budget is the smaller of a fixed cap and the package ratio, so a small
/// archive cannot expand to the full cap.
pub fn limits(compressed: usize) -> XlsxLimits {
    XlsxLimits::new(XlsxLimitOptions {
        max_input_bytes: MAX_DOCUMENT_BYTES,
        max_entries: 64,
        max_entry_bytes: 16 * 1024 * 1024,
        max_expanded_bytes: (32 * 1024 * 1024).min(compressed.max(1) * MAX_EXPANSION_RATIO),
        max_xml_depth: 32,
        max_xml_nodes: 1_000_000,
        max_xml_text_bytes: 16 * 1024 * 1024,
        max_shared_strings: 100_000,
        max_string_bytes: 32 * 1024,
        max_rows: 100_000,
        max_columns: 256,
        max_cells: 500_000,
        max_dense_cells: 500_000,
        max_materialized_cells: 500_000,
        max_materialized_text_bytes: 32 * 1024 * 1024,
        max_output_bytes: MAX_DOCUMENT_BYTES,
    })
    .expect("Braid XLSX limits are within the native admitted range")
}

/// Reads one workbook with the saved settings under Braid's limits.
pub fn read(
    bytes: &[u8],
    schema: &ferrule_ir::SchemaNode,
    options: &FormatOptions,
) -> Result<(), XlsxFormatError> {
    ferrule_format_xlsx::from_bytes_bounded(
        bytes,
        schema,
        options.xlsx_sheet.as_deref(),
        options.xlsx_start_row.unwrap_or(1),
        &options.xlsx_columns,
        options.has_header_row.unwrap_or(true),
        &limits(bytes.len()),
    )
    .map(|_| ())
}

pub fn limited(error: &XlsxFormatError) -> bool {
    matches!(error, XlsxFormatError::LimitExceeded { .. })
}

#[cfg(test)]
mod tests {
    use super::*;

    fn xlsx() -> FormatOptions {
        FormatOptions {
            tabular_kind: Some(TabularBoundaryKind::Xlsx),
            ..Default::default()
        }
    }

    #[test]
    fn defaults_are_resolved_and_headers_are_output_only() {
        let value = settings(&xlsx(), false).unwrap();
        assert_eq!((value.start_row, value.has_header_row), (1, true));
        let headers = FormatOptions {
            xlsx_headers: vec!["Name".into()],
            ..xlsx()
        };
        assert!(settings(&headers, false).is_err());
        assert_eq!(settings(&headers, true).unwrap().headers, ["Name"]);
    }

    #[test]
    fn stateful_and_non_flat_layouts_are_refused() {
        for options in [
            FormatOptions {
                xlsx_update_existing: true,
                ..xlsx()
            },
            FormatOptions {
                xlsx_rows: vec![1],
                ..xlsx()
            },
            FormatOptions {
                delimiter: Some(','),
                ..xlsx()
            },
            FormatOptions {
                xlsx_sheet: Some(String::new()),
                ..xlsx()
            },
            FormatOptions {
                tabular_kind: Some(TabularBoundaryKind::Csv),
                ..Default::default()
            },
        ] {
            assert!(settings(&options, true).is_err());
        }
    }

    #[test]
    fn expansion_budget_follows_the_package_ratio() {
        assert_eq!(
            limits(1024).options().max_expanded_bytes,
            1024 * MAX_EXPANSION_RATIO
        );
        assert_eq!(
            limits(MAX_DOCUMENT_BYTES).options().max_expanded_bytes,
            32 * 1024 * 1024
        );
    }
}
