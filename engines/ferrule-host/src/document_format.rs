use ferrule_mapping::{EdiAutocomplete, EdiBoundaryKind, FormatOptions};

/// Document families admitted by the document-preview profile. The legacy JSON
/// profile keeps its own admission rules; these identities never come from an
/// uploaded filename, a design-time path or a caller-supplied media type.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum DocumentFormat {
    Json,
    X12,
    Edifact,
    Csv,
    Tsv,
    Xml,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum FormatRefusal {
    Unspecified,
    UnsupportedFamily,
    ConflictingOptions,
    UnresolvedConfiguration,
}

impl DocumentFormat {
    pub const fn id(self) -> &'static str {
        match self {
            Self::Json => "json",
            Self::X12 => "x12",
            Self::Edifact => "edifact",
            Self::Csv => "csv",
            Self::Tsv => "tsv",
            Self::Xml => "xml",
        }
    }

    pub const fn extension(self) -> &'static str {
        match self {
            Self::Json => "json",
            Self::X12 => "x12",
            Self::Edifact => "edi",
            Self::Csv => "csv",
            Self::Tsv => "tsv",
            Self::Xml => "xml",
        }
    }

    pub const fn media_type(self) -> &'static str {
        match self {
            Self::Json => "application/json",
            Self::X12 => "application/edi-x12",
            Self::Edifact => "application/edifact",
            Self::Csv => "text/csv",
            Self::Tsv => "text/tab-separated-values",
            Self::Xml => "application/xml",
        }
    }

    /// Require an explicit family and refuse every option outside that family's
    /// closed allowlist before invoking Ferrule. In particular, a file extension
    /// cannot turn a network, database, XML or spreadsheet boundary into EDI.
    pub fn from_options(options: &FormatOptions) -> Result<Self, FormatRefusal> {
        if options.tabular_kind == Some(ferrule_mapping::TabularBoundaryKind::Csv) {
            return crate::csv_document::CsvDialect::from_options(options)
                .map(|dialect| {
                    if dialect.delimiter == '\t' {
                        Self::Tsv
                    } else {
                        Self::Csv
                    }
                })
                .map_err(|_| FormatRefusal::ConflictingOptions);
        }
        if options.edi_config_reference.is_some() {
            return Err(FormatRefusal::UnresolvedConfiguration);
        }
        let format = match options.edi_kind {
            Some(EdiBoundaryKind::X12) => Self::X12,
            Some(EdiBoundaryKind::Edifact) => Self::Edifact,
            Some(_) => return Err(FormatRefusal::UnsupportedFamily),
            None if options.json_document => Self::Json,
            None => return Err(FormatRefusal::Unspecified),
        };
        let mut admitted = FormatOptions {
            mfd_decimal_input_names: options.mfd_decimal_input_names.clone(),
            mfd_optional_parser_input_names: options.mfd_optional_parser_input_names.clone(),
            ..Default::default()
        };
        match format {
            Self::Json => {
                admitted.json_document = true;
                // This is schema provenance, not a resource the runtime opens.
                admitted.json_schema_unresolved_reference =
                    options.json_schema_unresolved_reference.clone();
            }
            Self::X12 | Self::Edifact => {
                if !matches!(
                    (&options.edi_autocomplete, format),
                    (None, _)
                        | (Some(EdiAutocomplete::X12(_)), Self::X12)
                        | (Some(EdiAutocomplete::Edifact(_)), Self::Edifact)
                ) {
                    return Err(FormatRefusal::ConflictingOptions);
                }
                admitted.edi_kind = options.edi_kind;
                admitted.lenient_segments = options.lenient_segments;
                admitted.edi_implied_decimals = options.edi_implied_decimals.clone();
                admitted.edi_lexical_formats = options.edi_lexical_formats.clone();
                admitted.edi_value_constraints = options.edi_value_constraints.clone();
                admitted.edi_autocomplete = options.edi_autocomplete.clone();
                if format == Self::X12 {
                    admitted.x12_separators = options.x12_separators;
                    admitted.x12_interchange_version = options.x12_interchange_version.clone();
                }
            }
            Self::Csv | Self::Tsv => unreachable!("CSV returned after its closed option admission"),
            Self::Xml => unreachable!("XML requires its separate preview admission"),
        }
        if *options != admitted {
            return Err(FormatRefusal::ConflictingOptions);
        }
        Ok(format)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use ferrule_mapping::{EdiConfigDependency, EdifactAutocomplete, X12Autocomplete};

    #[test]
    fn plain_defaults_never_guess_a_document_family() {
        assert_eq!(
            DocumentFormat::from_options(&FormatOptions::default()),
            Err(FormatRefusal::Unspecified)
        );
    }

    #[test]
    fn each_explicit_family_has_a_distinct_media_type_and_identity() {
        for (kind, json, expected) in [
            (None, true, DocumentFormat::Json),
            (Some(EdiBoundaryKind::X12), false, DocumentFormat::X12),
            (
                Some(EdiBoundaryKind::Edifact),
                false,
                DocumentFormat::Edifact,
            ),
        ] {
            let options = FormatOptions {
                edi_kind: kind,
                json_document: json,
                ..Default::default()
            };
            assert_eq!(DocumentFormat::from_options(&options), Ok(expected));
            assert!(!expected.id().is_empty());
            assert!(!expected.extension().is_empty());
        }
        assert_ne!(
            DocumentFormat::X12.media_type(),
            DocumentFormat::Edifact.media_type()
        );
    }

    #[test]
    fn other_edi_families_are_not_silently_advertised() {
        for kind in [
            EdiBoundaryKind::Hl7,
            EdiBoundaryKind::Tradacoms,
            EdiBoundaryKind::Idoc,
            EdiBoundaryKind::SwiftMt,
        ] {
            assert_eq!(
                DocumentFormat::from_options(&FormatOptions {
                    edi_kind: Some(kind),
                    ..Default::default()
                }),
                Err(FormatRefusal::UnsupportedFamily)
            );
        }
    }

    #[test]
    fn edi_boundaries_reject_conflicting_format_and_stateful_options() {
        for kind in [EdiBoundaryKind::X12, EdiBoundaryKind::Edifact] {
            let plain = FormatOptions {
                edi_kind: Some(kind),
                ..Default::default()
            };
            let conflicts = [
                FormatOptions {
                    json_document: true,
                    ..plain.clone()
                },
                FormatOptions {
                    xml_document: true,
                    ..plain.clone()
                },
                FormatOptions {
                    json_lines: true,
                    ..plain.clone()
                },
                FormatOptions {
                    delimiter: Some(','),
                    ..plain.clone()
                },
                FormatOptions {
                    xlsx_sheet: Some("Orders".into()),
                    ..plain.clone()
                },
                FormatOptions {
                    xlsx_update_existing: true,
                    ..plain
                },
            ];
            for options in conflicts {
                assert_eq!(
                    DocumentFormat::from_options(&options),
                    Err(FormatRefusal::ConflictingOptions)
                );
            }
        }
    }

    #[test]
    fn x12_settings_are_retained_only_for_x12() {
        let options = FormatOptions {
            edi_kind: Some(EdiBoundaryKind::X12),
            lenient_segments: true,
            x12_interchange_version: Some("00501".into()),
            ..Default::default()
        };
        assert_eq!(
            DocumentFormat::from_options(&options),
            Ok(DocumentFormat::X12)
        );
        assert_eq!(
            DocumentFormat::from_options(&FormatOptions {
                edi_kind: Some(EdiBoundaryKind::Edifact),
                ..options
            }),
            Err(FormatRefusal::ConflictingOptions)
        );
    }

    #[test]
    fn unresolved_edi_configuration_is_never_reopened_or_inferred() {
        for dependency in [
            EdiConfigDependency::missing_configuration(),
            EdiConfigDependency::external_reference("../private/parser.sef"),
        ] {
            assert_eq!(
                DocumentFormat::from_options(&FormatOptions {
                    edi_kind: Some(EdiBoundaryKind::X12),
                    edi_config_reference: Some(dependency),
                    ..Default::default()
                }),
                Err(FormatRefusal::UnresolvedConfiguration)
            );
        }
    }

    #[test]
    fn autocomplete_must_belong_to_the_selected_envelope_family() {
        let x12 = EdiAutocomplete::X12(X12Autocomplete {
            request_acknowledgement: true,
            transaction_set: Some("940".into()),
        });
        let edifact = EdiAutocomplete::Edifact(EdifactAutocomplete {
            syntax_level: Some("UNOC".into()),
            syntax_version: Some("3".into()),
            controlling_agency: Some("UN".into()),
            message_type: Some("ORDERS".into()),
        });
        for (kind, own, other, expected) in [
            (
                EdiBoundaryKind::X12,
                x12.clone(),
                edifact.clone(),
                DocumentFormat::X12,
            ),
            (
                EdiBoundaryKind::Edifact,
                edifact,
                x12,
                DocumentFormat::Edifact,
            ),
        ] {
            let options = FormatOptions {
                edi_kind: Some(kind),
                edi_autocomplete: Some(own),
                ..Default::default()
            };
            assert_eq!(DocumentFormat::from_options(&options), Ok(expected));
            assert_eq!(
                DocumentFormat::from_options(&FormatOptions {
                    edi_autocomplete: Some(other),
                    ..options
                }),
                Err(FormatRefusal::ConflictingOptions)
            );
        }
    }

    #[test]
    fn json_cannot_carry_unrelated_edi_or_parser_options() {
        let plain = FormatOptions {
            json_document: true,
            ..Default::default()
        };
        for options in [
            FormatOptions {
                lenient_segments: true,
                ..plain.clone()
            },
            FormatOptions {
                json5: true,
                ..plain.clone()
            },
            FormatOptions {
                xml_document: true,
                ..plain.clone()
            },
            FormatOptions {
                x12_interchange_version: Some("00501".into()),
                ..plain
            },
        ] {
            assert_eq!(
                DocumentFormat::from_options(&options),
                Err(FormatRefusal::ConflictingOptions)
            );
        }
    }
}
