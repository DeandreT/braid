//! Shared, self-authored physical-safety inputs only. This is not native/generated
//! execution qualification or a replacement for resource-boundary tests.
use std::collections::BTreeSet;

use serde::Deserialize;
use sha2::{Digest, Sha256};
use weft_ferrule_host::xml_document::{self as xml, Error, Safety};

#[path = "../src/xml_name_chars.rs"]
mod name_chars;

const CORPUS: &[u8] = include_bytes!("corpora/xml-safety-cases.json");

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Corpus {
    schema: String,
    description: String,
    limits: Limits,
    cases: Vec<Case>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Limits {
    maximum_bytes: usize,
    maximum_depth: usize,
    maximum_nodes: usize,
    maximum_attributes: usize,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Case {
    id: String,
    payload: Payload,
    expected_c_sharp: CSharpOutcome,
    expected_native: NativeOutcome,
}

#[derive(Deserialize)]
#[serde(tag = "encoding", rename_all = "lowercase", deny_unknown_fields)]
enum Payload {
    Utf8 { text: String },
    Bytes { bytes: Vec<u8> },
}

impl Payload {
    fn bytes(&self) -> &[u8] {
        match self {
            Self::Utf8 { text } => text.as_bytes(),
            Self::Bytes { bytes } => bytes,
        }
    }
}

#[derive(Deserialize, PartialEq, Eq)]
enum CSharpOutcome {
    Accept,
    InvalidUtf8,
    InvalidXml,
    InvalidDeclaration,
    DocumentRequired,
    UnsupportedName,
}

#[derive(Deserialize)]
enum NativeOutcome {
    Accept,
    InvalidInput,
    InvalidEncoding,
    InvalidDeclaration,
    DtdProhibited,
    UnsupportedName,
}

impl NativeOutcome {
    fn failure(&self) -> Option<Error> {
        match self {
            Self::Accept => None,
            Self::InvalidInput => Some(Error::InvalidInput),
            Self::InvalidEncoding => Some(Error::InvalidEncoding),
            Self::InvalidDeclaration => Some(Error::InvalidDeclaration),
            Self::DtdProhibited => Some(Error::DtdProhibited),
            Self::UnsupportedName => Some(Error::UnsupportedName),
        }
    }
}

#[test]
fn authored_physical_safety_corpus_matches_native_categories()
-> Result<(), Box<dyn std::error::Error>> {
    let corpus: Corpus = serde_json::from_slice(CORPUS)?;
    assert_eq!(corpus.schema, "weft.xml-safety-cases.v1");
    assert!(!corpus.description.is_empty());
    assert_eq!(corpus.limits.maximum_bytes, xml::MAX_DOCUMENT_BYTES);
    assert_eq!(corpus.limits.maximum_depth, xml::MAX_DEPTH);
    assert_eq!(corpus.limits.maximum_nodes, xml::MAX_NODES);
    assert_eq!(corpus.limits.maximum_attributes, xml::MAX_ATTRIBUTES);
    assert!((15..=65).contains(&corpus.cases.len()));
    let safety = Safety::new(corpus.limits.maximum_bytes)?;
    let mut ids = BTreeSet::new();
    for case in &corpus.cases {
        assert!(
            case.id.starts_with("xml-")
                && case.id.split('-').all(|part| {
                    !part.is_empty()
                        && part
                            .bytes()
                            .all(|b| b.is_ascii_lowercase() || b.is_ascii_digit())
                }),
            "invalid fixture ID: {}",
            case.id
        );
        assert!(ids.insert(&case.id), "duplicate fixture ID: {}", case.id);
        let expected = case.expected_native.failure();
        assert_eq!(
            case.expected_c_sharp == CSharpOutcome::Accept,
            expected.is_none(),
            "{}: runtimes must expect the same accept/refuse decision",
            case.id
        );
        let bytes = case.payload.bytes();
        let actual = safety.validate_bytes(bytes);
        assert_eq!(actual.as_ref().err().copied(), expected, "{}", case.id);
        if let Ok(stats) = actual {
            assert_eq!(stats.bytes, bytes.len(), "{}", case.id);
        }
    }
    Ok(())
}

#[test]
fn frozen_name_ranges_are_sorted_disjoint_compact_and_source_identical() {
    let mut hash = Sha256::new();
    for (ranges, count, members) in [
        (name_chars::NCNAME_START, 205, 34_515),
        (name_chars::NCNAME_CONTINUE, 287, 35_121),
    ] {
        assert_eq!(ranges.len(), count);
        assert!(ranges.iter().all(|&(low, high)| low <= high));
        assert!(
            ranges
                .windows(2)
                .all(|pair| u32::from(pair[0].1) + 1 < u32::from(pair[1].0))
        );
        assert_eq!(
            ranges
                .iter()
                .map(|&(low, high)| usize::from(high - low) + 1)
                .sum::<usize>(),
            members
        );
        for &(low, high) in ranges {
            hash.update(low.to_le_bytes());
            hash.update(high.to_le_bytes());
        }
    }
    // Oracle from the exact v10.0.12 XmlCharType.cs table, selecting bit 4
    // then bit 8, coalescing each independently, and encoding each (low, high)
    // as two little-endian u16s. Not calculated from the Rust source ranges.
    assert_eq!(
        format!("{:x}", hash.finalize()),
        "3893d0d4e3716187e7d666a376704becdf4b3293c5f61b37bae7efb5763dac82"
    );
}

#[test]
fn name_predicates_match_the_official_pinned_table_for_every_bmp_codepoint() {
    let mut hash = Sha256::new();
    let mut starts = 0;
    let mut continuations = 0;
    for cp in 0..=0xffff {
        let ch = char::from_u32(cp);
        let start = ch.is_some_and(name_chars::is_ncname_start);
        let continuation = ch.is_some_and(name_chars::is_ncname_char);
        starts += usize::from(start);
        continuations += usize::from(continuation);
        assert!(!start || continuation, "U+{cp:04X}");
        hash.update([(u8::from(start) * 4) | (u8::from(continuation) * 8)]);
    }
    assert_eq!((starts, continuations), (34_515, 35_121));
    // Independent oracle: SHA-256 of the 65,536 official table bytes masked
    // with 0x0c, in ascending codepoint order. Includes surrogate entries (0).
    // The source-file/table provenance is retained in xml_name_chars.rs.
    assert_eq!(
        format!("{:x}", hash.finalize()),
        "88879a458c965976597161f946a3c14b16a3a92f6d65d7da431280513f75c13c"
    );
    for ch in ['\u{10000}', '\u{1f680}', '\u{10ffff}', ':'] {
        assert!(!name_chars::is_ncname_start(ch));
        assert!(!name_chars::is_ncname_char(ch));
    }
}
