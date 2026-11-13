//! One explicitly selected, reversible flat CSV document on each primary boundary.
//! TSV is the same native CSV codec with a saved tab delimiter, never a filename guess.
use ferrule_mapping::{FormatOptions, Project, TabularBoundaryKind};
use serde::{Deserialize, Serialize};

pub const CAPABILITY: &str = "weft.csv.flat-document.v1";
pub const DIALECT_SCHEMA: &str = "weft.csv-dialect.v1";

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct CsvDialect {
    pub schema: String,
    pub delimiter: char,
    pub quote: Option<char>,
    pub quote_disabled: bool,
    pub has_header_row: bool,
    pub utf8_bom: bool,
    pub preserve_empty_strings: bool,
}

impl CsvDialect {
    pub fn from_options(options: &FormatOptions) -> Result<Self, &'static str> {
        if options.tabular_kind != Some(TabularBoundaryKind::Csv) {
            return Err("CSV requires explicit saved tabular_kind=Csv.");
        }
        // The upstream policy checks the complete option bag, including repair markers.
        let policy = ferrule_codegen::CsvInputPolicy::from_input_format_options(options)
            .map_err(|_| "CSV requires supported saved dialect settings without foreign options or text repair dependencies.")?;
        Ok(Self {
            schema: DIALECT_SCHEMA.into(),
            delimiter: policy.delimiter.unwrap_or(','),
            quote: if policy.quote_disabled {
                None
            } else {
                Some(policy.quote.unwrap_or('"'))
            },
            quote_disabled: policy.quote_disabled,
            has_header_row: policy.has_headers,
            utf8_bom: policy.utf8_bom,
            preserve_empty_strings: policy.preserve_empty_strings,
        })
    }

    pub fn format(&self) -> &'static str {
        if self.delimiter == '\t' { "tsv" } else { "csv" }
    }

    pub fn media_type(&self) -> &'static str {
        if self.delimiter == '\t' {
            "text/tab-separated-values"
        } else {
            "text/csv"
        }
    }
}

/// Use the same strict boundary and program admission for preview and generated C#.
/// This intentionally refuses mixed plans, named endpoints, dynamic output and non-flat schemas.
pub fn admit(
    project: &Project,
) -> Result<ferrule_codegen::CsvDocumentBoundaryPolicy, &'static str> {
    CsvDialect::from_options(&project.source_options)?;
    CsvDialect::from_options(&project.target_options)?;
    let policy = ferrule_codegen::CsvDocumentBoundaryPolicy::from_project(project)
        .map_err(|_| "CSV preview/build requires primary flat CSV-to-CSV boundaries; named endpoints and mixed plans are not supported.")?;
    let program = ferrule_codegen::lower(project)
        .map_err(|_| "The mapping cannot be lowered for the flat CSV document capability.")?;
    ferrule_codegen::validate_csv_document_boundary(&program, &policy)
        .map_err(|_| "The mapping is outside the primary flat CSV document capability.")?;
    Ok(policy)
}
