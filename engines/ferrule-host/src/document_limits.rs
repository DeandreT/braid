//! Allocation-free document limits checked before parsing and before publishing.
use crate::document_format::DocumentFormat;

pub const MAX_EDI_BYTES: usize = 2 * 1024 * 1024;
pub const MAX_EDI_SEGMENTS: usize = 32_768;
pub const MAX_EDI_SEGMENT_BYTES: usize = 16 * 1024;
pub const MAX_JSON_NESTING: usize = 64;

pub fn admitted_schema(format: DocumentFormat, schema: &ferrule_ir::SchemaNode) -> bool {
    if format == DocumentFormat::Xml {
        return crate::xml_document::bounded_closed_schema(schema).is_ok();
    }
    fn visit(
        schema: &ferrule_ir::SchemaNode,
        depth: usize,
        nodes: &mut usize,
        scalars: &mut usize,
    ) -> bool {
        *nodes += 1;
        if depth > 64 || *nodes > 8192 {
            return false;
        }
        match &schema.kind {
            ferrule_ir::SchemaKind::Group {
                children, dynamic, ..
            } => {
                children
                    .iter()
                    .all(|child| visit(child, depth + 1, nodes, scalars))
                    && dynamic
                        .as_deref()
                        .is_none_or(|child| visit(child, depth + 1, nodes, scalars))
            }
            _ => {
                *scalars += 1;
                true
            }
        }
    }
    let mut nodes = 0;
    let mut scalars = 0;
    visit(schema, 1, &mut nodes, &mut scalars) && (format == DocumentFormat::Json || scalars > 0)
}

pub fn within_limits(
    format: DocumentFormat,
    bytes: &[u8],
    configured_segment: Option<char>,
) -> bool {
    if format == DocumentFormat::Xml {
        return crate::xml_document::Safety::new(crate::xml_document::MAX_DOCUMENT_BYTES)
            .and_then(|safety| safety.validate_bytes(bytes))
            .is_ok();
    }
    if matches!(format, DocumentFormat::Csv | DocumentFormat::Tsv) {
        // Native CSV parsing supplies cell/record/column/node limits. The outer
        // per-run limit is still enforced by preview before publication.
        return bytes.len() <= crate::preview::protocol::MAX_PREVIEW_OUTPUT_BYTES;
    }
    if format == DocumentFormat::Json {
        let mut quoted = false;
        let mut escaped = false;
        let mut depth = 0usize;
        for byte in bytes {
            if escaped {
                escaped = false;
                continue;
            }
            if quoted && *byte == b'\\' {
                escaped = true;
                continue;
            }
            if *byte == b'"' {
                quoted = !quoted;
                continue;
            }
            if !quoted {
                match byte {
                    b'[' | b'{' => {
                        depth += 1;
                        if depth > MAX_JSON_NESTING {
                            return false;
                        }
                    }
                    b']' | b'}' => depth = depth.saturating_sub(1),
                    _ => {}
                }
            }
        }
        return true;
    }
    if bytes.len() > MAX_EDI_BYTES {
        return false;
    }
    let Ok(text) = std::str::from_utf8(bytes) else {
        return true;
    }; // Parser owns encoding diagnostics.
    let text = text.trim_start();
    if format == DocumentFormat::X12 {
        return x12_within_limits(text.as_bytes(), configured_segment.unwrap_or('~'));
    }
    edifact_within_limits(text.as_bytes())
}

// Framing only: control values, counts, schema and encoding remain the envelope/codec's job.
// These states mirror the envelope's open interchange/group/message boundaries so an UNA
// substring in a field (or an unfinished envelope) cannot reset the size counter.
#[derive(Clone, Copy, PartialEq, Eq)]
enum EdifactFrame {
    Outside,
    Interchange,
    Group,
    Message,
    GroupMessage,
    Standalone,
    Invalid,
}

impl EdifactFrame {
    fn after(self, tag: &[u8]) -> Self {
        use EdifactFrame::{
            Group, GroupMessage, Interchange, Invalid, Message, Outside, Standalone,
        };
        match (self, tag) {
            (Outside, b"UNB") => Interchange,
            (Outside, b"UNH") => Standalone,
            (Interchange, b"UNG") => Group,
            (Interchange, b"UNH") => Message,
            (Interchange, b"UNZ") => Outside,
            (Group, b"UNH") => GroupMessage,
            (Group, b"UNE") => Interchange,
            (Message, b"UNT") => Interchange,
            (GroupMessage, b"UNT") => Group,
            (Standalone, b"UNT") => Outside,
            (_, b"UNB" | b"UNG" | b"UNH" | b"UNT" | b"UNE" | b"UNZ") => Invalid,
            (Message | GroupMessage | Standalone, _) => self,
            _ => Invalid,
        }
    }
}

fn edifact_advice(bytes: &[u8]) -> Option<(u8, u8, u8)> {
    let advice = bytes.get(..9)?;
    if !advice.starts_with(b"UNA") {
        return None;
    }
    for (index, value) in advice[3..].iter().enumerate() {
        if index == 4 && *value == b' ' {
            continue;
        }
        if !value.is_ascii_punctuation() || advice[3..3 + index].contains(value) {
            return None;
        }
    }
    Some((advice[4], advice[6], advice[8]))
}

fn edifact_within_limits(bytes: &[u8]) -> bool {
    let mut frame = EdifactFrame::Outside;
    let mut advice = false;
    let mut offset = 0;
    let mut element = b'+';
    let mut terminator = b'\'';
    let mut release = b'?';
    let mut segments = 0usize;
    while offset < bytes.len() {
        while bytes.get(offset).is_some_and(u8::is_ascii_whitespace) {
            offset += 1;
        }
        if offset == bytes.len() {
            break;
        }
        if frame == EdifactFrame::Outside && !advice {
            element = b'+';
            terminator = b'\'';
            release = b'?';
            if let Some(syntax) = edifact_advice(&bytes[offset..]) {
                (element, release, terminator) = syntax;
                offset += 9; // Consume advice atomically even if it contains the previous terminator.
                advice = true;
                segments += 1;
                if segments > MAX_EDI_SEGMENTS {
                    return false;
                }
                continue;
            }
        }
        let start = offset;
        let mut escaped = false;
        let mut ended = false;
        while let Some(&byte) = bytes.get(offset) {
            offset += 1;
            if offset - start > MAX_EDI_SEGMENT_BYTES {
                return false;
            }
            if escaped {
                escaped = false;
            } else if byte == release {
                escaped = true;
            } else if byte == terminator {
                ended = true;
                break;
            }
        }
        if ended {
            segments += 1;
            if segments > MAX_EDI_SEGMENTS {
                return false;
            }
            let tag = bytes[start..offset]
                .split(|byte| *byte == element || *byte == terminator)
                .next()
                .unwrap_or_default();
            frame = frame.after(tag);
            advice = false;
        }
    }
    true
}

fn x12_within_limits(bytes: &[u8], mut terminator: char) -> bool {
    let mut offset = 0;
    let mut segments = 0;
    while offset < bytes.len() {
        while bytes.get(offset).is_some_and(u8::is_ascii_whitespace) {
            offset += 1;
        }
        if offset == bytes.len() {
            break;
        }
        // Consume the complete declaration atomically: an old terminator may
        // be this interchange's component or repetition separator.
        if let Some(syntax) = crate::x12_syntax::read(&bytes[offset..]) {
            terminator = char::from(syntax.segment);
            offset += 106;
        } else {
            let mut encoded = [0; 4];
            let delimiter = terminator.encode_utf8(&mut encoded).as_bytes();
            let length = bytes[offset..]
                .windows(delimiter.len())
                .position(|window| window == delimiter)
                .map_or(bytes.len() - offset, |index| index + delimiter.len());
            if length > MAX_EDI_SEGMENT_BYTES {
                return false;
            }
            offset += length;
        }
        segments += 1;
        if segments > MAX_EDI_SEGMENTS {
            return false;
        }
    }
    true
}

#[cfg(test)]
mod tests {
    use super::*;
    const ISA: &str = "ISA*00*          *00*          *ZZ*WEFTSENDER     *ZZ*WEFTRECEIVER   *261024*1200*^*00501*000000330*0*T*:~";

    fn edifact(reference: &str, body: &str, count: usize, custom: bool) -> String {
        if custom {
            format!(
                "UNA;|.! $UNB|UNOC;3|S|R|261026;1200|{reference}$UNH|M{reference}|ORDERS;D;96A;UN${body}UNT|{}|M{reference}$UNZ|1|{reference}$",
                count + 2
            )
        } else {
            format!(
                "UNB+UNOC:3+S+R+261026:1200+{reference}'UNH+M{reference}+ORDERS:D:96A:UN'{body}UNT+{}+M{reference}'UNZ+1+{reference}'",
                count + 2
            )
        }
    }

    #[test]
    fn mixed_edifact_limits_follow_envelope_boundaries_and_each_release_character() {
        let first = edifact("A", "BGM+220+FIRST?'VALUE'", 1, false);
        let body = format!("BGM|220|{}!!VALUE!$LITERAL$", "X".repeat(100)).repeat(180);
        let second = edifact("B", &body, 180, true);
        let text = format!("{first}\r\n \t{second}");
        assert!(second.len() > MAX_EDI_SEGMENT_BYTES);
        assert!(crate::edi_envelope::validate(DocumentFormat::Edifact, text.as_bytes()).is_ok());
        assert!(within_limits(
            DocumentFormat::Edifact,
            text.as_bytes(),
            None
        ));

        let cross_role = second
            .replace("UNA;", "UNA'")
            .replace("UNOC;3", "UNOC'3")
            .replace("ORDERS;D;96A;UN", "ORDERS'D'96A'UN")
            .replace('$', "?");
        let cross_role = format!("{first}\r\n{cross_role}");
        assert!(
            crate::edi_envelope::validate(DocumentFormat::Edifact, cross_role.as_bytes()).is_ok()
        );
        assert!(within_limits(
            DocumentFormat::Edifact,
            cross_role.as_bytes(),
            None
        ));

        let oversized = edifact(
            "B",
            &format!("BGM|220|{}$", "X".repeat(MAX_EDI_SEGMENT_BYTES)),
            1,
            true,
        );
        assert!(!within_limits(
            DocumentFormat::Edifact,
            format!("{first}{oversized}").as_bytes(),
            None
        ));
        let mismatch = text.replace("UNT|182|MB", "UNT|999|MB");
        assert!(
            crate::edi_envelope::validate(DocumentFormat::Edifact, mismatch.as_bytes()).is_err()
        );
        assert!(within_limits(
            DocumentFormat::Edifact,
            mismatch.as_bytes(),
            None
        ));
    }

    #[test]
    fn edifact_returns_to_defaults_after_unz_and_a_standalone_unt() {
        let first = edifact("A", "BGM|220|FIRST!!VALUE!$LITERAL$", 1, true);
        let second = edifact("B", "BGM+220+DEFAULT?'VALUE'", 1, false);
        for text in [
            format!("{first}\r\n{second}"),
            format!("UNA;|.! $UNH|S|ORDERS;D;96A;UN$BGM|220|ONE$UNT|3|S$\n{second}"),
        ] {
            assert!(
                crate::edi_envelope::validate(DocumentFormat::Edifact, text.as_bytes()).is_ok()
            );
            assert!(within_limits(
                DocumentFormat::Edifact,
                text.as_bytes(),
                None
            ));
        }
    }

    #[test]
    fn advice_looking_field_data_never_resets_an_edifact_segment_budget() {
        let literal = "BEFOREUNA;|.! $AFTER";
        let text = edifact("A", &format!("BGM+220+{literal}'"), 1, false);
        assert!(crate::edi_envelope::validate(DocumentFormat::Edifact, text.as_bytes()).is_ok());
        assert!(within_limits(
            DocumentFormat::Edifact,
            text.as_bytes(),
            None
        ));
        let oversized = edifact("A", &format!("BGM+220+{}'", literal.repeat(1000)), 1, false);
        assert!(!within_limits(
            DocumentFormat::Edifact,
            oversized.as_bytes(),
            None
        ));
        // An apparent trailer/advice inside a released field is not a new interchange.
        let released = format!(
            "UNB+UNOC:3+S+R+261026:1200+A'UNH+M+ORDERS:D:96A:UN'BGM+220+{}'",
            "UNZ+1+A?'UNA;|.! $".repeat(1500)
        );
        assert!(!within_limits(
            DocumentFormat::Edifact,
            released.as_bytes(),
            None
        ));
        let malformed = format!("UNA:++? ${}$", "X".repeat(MAX_EDI_SEGMENT_BYTES));
        assert!(!within_limits(
            DocumentFormat::Edifact,
            malformed.as_bytes(),
            None
        ));
    }

    #[test]
    fn headerless_unicode_terminators_keep_their_complete_utf8_sequence() {
        // Both code points truncate to ASCII space if cast to u8.
        for terminator in ['\u{2020}', '\u{10020}'] {
            let small = format!("ISA*X X{terminator}ISA*Y Y{terminator}");
            assert!(within_limits(
                DocumentFormat::X12,
                small.as_bytes(),
                Some(terminator)
            ));
            let oversized = format!("ISA*{}{terminator}", "X ".repeat(10_000));
            assert!(!within_limits(
                DocumentFormat::X12,
                oversized.as_bytes(),
                Some(terminator)
            ));
            let at_limit = format!(
                "{}{}",
                "X".repeat(MAX_EDI_SEGMENT_BYTES - terminator.len_utf8()),
                terminator
            );
            assert!(within_limits(
                DocumentFormat::X12,
                at_limit.as_bytes(),
                Some(terminator)
            ));
            let over_limit = format!("X{at_limit}");
            assert!(!within_limits(
                DocumentFormat::X12,
                over_limit.as_bytes(),
                Some(terminator)
            ));
        }
    }

    #[test]
    fn mixed_interchange_limits_use_each_complete_declaration() {
        let alternate: String = ISA
            .chars()
            .map(|c| match c {
                '*' => '|',
                ':' => '~',
                '~' => ':',
                '^' => '!',
                _ => c,
            })
            .collect();
        let valid = format!("{ISA}BGM*OK~{alternate}{}", "BGM|A~B!C~D:".repeat(4_000));
        assert!(within_limits(DocumentFormat::X12, valid.as_bytes(), None));
        let too_long = format!("{ISA}BGM*OK~{alternate}BGM|{}:", "A~".repeat(10_000));
        assert!(!within_limits(
            DocumentFormat::X12,
            too_long.as_bytes(),
            None
        ));
        let too_many = format!(
            "{ISA}{}{alternate}BGM|OK:",
            "BGM*OK~".repeat(MAX_EDI_SEGMENTS - 1)
        );
        assert!(!within_limits(
            DocumentFormat::X12,
            too_many.as_bytes(),
            None
        ));
    }

    #[test]
    fn isa_lookalikes_cannot_override_the_declared_segment_terminator() {
        for terminator in ['~', '!'] {
            let partial = format!("ISA*{}{terminator}", "X".repeat(20_000));
            assert!(!within_limits(
                DocumentFormat::X12,
                partial.as_bytes(),
                Some(terminator)
            ));
        }
        for (offset, replacement) in [(6, 'X'), (84, 'A'), (104, '*'), (82, ':'), (7, 'é')] {
            let mut header: Vec<char> = ISA.chars().collect();
            header[offset] = replacement;
            header[105] = '!';
            let text = header.into_iter().collect::<String>() + &"X!".repeat(10_000) + "~";
            assert!(crate::x12_syntax::read(text.as_bytes()).is_none());
            assert!(!within_limits(
                DocumentFormat::X12,
                text.as_bytes(),
                Some('~')
            ));
        }
        assert!(within_limits(DocumentFormat::X12, b"ISA*OK~", Some('~')));
    }

    #[test]
    fn complete_isa_syntax_overrides_mapping_defaults_without_weakening_bounds() {
        for terminator in ['~', '!', '\n'] {
            let header = ISA.replace('~', &terminator.to_string());
            let text = header.clone() + &format!("BEG*OK{terminator}").repeat(4_000);
            assert!(within_limits(
                DocumentFormat::X12,
                text.as_bytes(),
                Some('|')
            ));
            let oversized = header + &"X".repeat(20_000) + &terminator.to_string();
            assert!(!within_limits(
                DocumentFormat::X12,
                oversized.as_bytes(),
                Some('|')
            ));
        }
    }
    #[test]
    fn json_depth_ignores_quoted_brackets_and_keeps_legacy_independent() {
        assert!(within_limits(
            DocumentFormat::Json,
            br#"{"value":"[[[\\\"{{{"}"#,
            None
        ));
        assert!(!within_limits(
            DocumentFormat::Json,
            "[".repeat(65).as_bytes(),
            None
        ));
    }
    #[test]
    fn edi_limits_bound_bytes_segments_and_segment_size_with_release_characters() {
        assert!(!within_limits(
            DocumentFormat::X12,
            &vec![b'x'; MAX_EDI_BYTES + 1],
            None
        ));
        assert!(!within_limits(
            DocumentFormat::Edifact,
            "A'".repeat(MAX_EDI_SEGMENTS + 1).as_bytes(),
            None
        ));
        assert!(!within_limits(
            DocumentFormat::X12,
            &vec![b'x'; MAX_EDI_SEGMENT_BYTES + 1],
            None
        ));
        assert!(within_limits(
            DocumentFormat::Edifact,
            "A?'B'".repeat(MAX_EDI_SEGMENTS).as_bytes(),
            None
        ));
        assert!(within_limits(DocumentFormat::X12, b"A!B!", Some('!')));
    }
}
