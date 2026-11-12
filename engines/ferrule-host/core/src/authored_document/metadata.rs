use super::{Failure, Format};
use ferrule_ir::SchemaNode;
use ferrule_mapping::FormatOptions;
use serde::Serialize;

#[derive(Debug, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct CsvDialect {
    pub delimiter: char,
    pub quote: Option<char>,
    pub header_row: bool,
    pub utf8_bom: bool,
    pub preserve_empty_strings: bool,
}

/// Effective output settings, not filename-derived labels. Version lists contain
/// distinct versions observed in the successfully validated output envelopes.
#[derive(Debug, Serialize)]
#[serde(tag = "kind", rename_all = "camelCase")]
pub enum Dialect {
    Json,
    Xml {
        root: String,
        namespace: Option<String>,
    },
    Csv(CsvDialect),
    X12 {
        element: char,
        component: char,
        segment: char,
        repetition: Option<char>,
        versions: Vec<String>,
    },
    Edifact {
        element: char,
        component: char,
        segment: char,
        release: char,
        repetition: Option<char>,
        versions: Vec<String>,
    },
}

pub(super) fn describe(
    format: Format,
    schema: &SchemaNode,
    options: &FormatOptions,
    bytes: &[u8],
) -> Result<Dialect, Failure> {
    Ok(match format {
        Format::Json => Dialect::Json,
        Format::Xml => Dialect::Xml {
            root: schema.name.clone(),
            namespace: schema
                .xml_namespace
                .as_ref()
                .and_then(|n| n.uri())
                .map(str::to_owned),
        },
        Format::Csv | Format::Tsv => Dialect::Csv(CsvDialect {
            delimiter: options.delimiter.unwrap_or(','),
            quote: (!options.csv_quote_disabled).then(|| options.csv_quote.unwrap_or('"')),
            header_row: options.has_header_row.unwrap_or(true),
            utf8_bom: options.csv_utf8_bom,
            preserve_empty_strings: options.csv_preserve_empty_strings,
        }),
        Format::X12 | Format::Edifact => {
            let text = std::str::from_utf8(bytes).map_err(|_| Failure::InvalidOutput)?;
            let segments = if format == Format::X12 {
                ferrule_format_edi::x12::tokenize(text)
            } else {
                ferrule_format_edi::edifact::tokenize(text)
            }
            .map_err(|_| Failure::InvalidOutput)?;
            let mut versions = std::collections::BTreeSet::new();
            for s in segments {
                if format == Format::X12 && s.id == "ISA" {
                    let v = s
                        .elements
                        .get(11)
                        .and_then(|r| r.first())
                        .and_then(|c| c.first())
                        .ok_or(Failure::InvalidOutput)?;
                    versions.insert(v.clone());
                } else if format == Format::Edifact && s.id == "UNB" {
                    let v = s
                        .elements
                        .first()
                        .and_then(|r| r.first())
                        .and_then(|c| c.get(1))
                        .ok_or(Failure::InvalidOutput)?;
                    versions.insert(v.clone());
                }
            }
            if versions.len() > 32 || versions.iter().any(|v| v.len() > 16) {
                return Err(Failure::InvalidOutput);
            }
            if format == Format::X12 {
                let s = options
                    .x12_separators
                    .unwrap_or(ferrule_mapping::X12Separators {
                        element: '*',
                        component: ':',
                        segment: '~',
                        repetition: Some('^'),
                        release: None,
                    });
                Dialect::X12 {
                    element: s.element,
                    component: s.component,
                    segment: s.segment,
                    repetition: if versions.iter().any(|v| v.as_str() >= "00501") {
                        s.repetition
                    } else {
                        None
                    },
                    versions: versions.into_iter().collect(),
                }
            } else {
                Dialect::Edifact {
                    element: '+',
                    component: ':',
                    segment: '\'',
                    release: '?',
                    repetition: versions
                        .iter()
                        .any(|v| v.parse::<u32>().is_ok_and(|v| v >= 4))
                        .then_some('*'),
                    versions: versions.into_iter().collect(),
                }
            }
        }
    })
}
