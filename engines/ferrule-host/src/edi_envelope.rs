//! Bounded envelope count/reference admission before invoking a document codec.
//! This is not a trading-partner schema validator and never emits payload values.
use crate::document_format::DocumentFormat;

#[derive(Debug, PartialEq, Eq)]
pub struct EnvelopeError {
    pub interchange: usize,
    pub segment: usize,
    pub tag: String,
    pub element: usize,
}

#[derive(Default)]
struct Field {
    value: String,
    composite: bool,
    repeated: bool,
}

#[derive(Default)]
struct State {
    interchange_number: usize,
    interchange: Option<String>,
    group: Option<String>,
    message: Option<(String, usize)>,
    groups: usize,
    messages: usize,
    group_messages: usize,
}

impl State {
    fn open(&self) -> bool {
        self.interchange.is_some() || self.group.is_some() || self.message.is_some()
    }

    fn accept(
        &mut self,
        x12: bool,
        number: usize,
        fields: &[Field],
        repetition: bool,
    ) -> Result<(), usize> {
        let tag = fields[0].value.as_str();
        let reference = |element: usize| {
            fields
                .get(element)
                .filter(|field| {
                    !field.value.is_empty() && !field.composite && !(repetition && field.repeated)
                })
                .map(|field| field.value.clone())
                .ok_or(element)
        };
        let count = |element: usize, expected: usize| {
            let value = reference(element)?;
            if !value.is_empty()
                && value.bytes().all(|byte| byte.is_ascii_digit())
                && value.parse::<usize>() == Ok(expected)
            {
                Ok(())
            } else {
                Err(element)
            }
        };
        if tag == if x12 { "ISA" } else { "UNB" } {
            if self.open() {
                return Err(0);
            }
            self.interchange = Some(reference(if x12 { 13 } else { 5 })?);
            self.interchange_number += 1;
            self.groups = 0;
            self.messages = 0;
        } else if tag == if x12 { "GS" } else { "UNG" } {
            if self.interchange.is_none()
                || self.group.is_some()
                || self.message.is_some()
                || self.messages != 0
            {
                return Err(0);
            }
            self.group = Some(reference(if x12 { 6 } else { 5 })?);
            self.groups += 1;
            self.group_messages = 0;
        } else if tag == if x12 { "ST" } else { "UNH" } {
            if self.message.is_some()
                || x12 && self.group.is_none()
                || !x12 && self.groups != 0 && self.group.is_none()
            {
                return Err(0);
            }
            self.message = Some((reference(if x12 { 2 } else { 1 })?, number));
            if !x12 && self.interchange.is_none() {
                self.interchange_number += 1;
            }
        } else if tag == if x12 { "SE" } else { "UNT" } {
            let (control, start) = self.message.as_ref().ok_or(0usize)?;
            count(1, number - start + 1)?;
            if reference(2)? != *control {
                return Err(2);
            }
            self.message = None;
            if self.group.is_some() {
                self.group_messages += 1;
            } else {
                self.messages += 1;
            }
        } else if tag == if x12 { "GE" } else { "UNE" } {
            if self.interchange.is_none() || self.message.is_some() {
                return Err(0);
            }
            let control = self.group.as_ref().ok_or(0usize)?;
            count(1, self.group_messages)?;
            if reference(2)? != *control {
                return Err(2);
            }
            self.group = None;
        } else if tag == if x12 { "IEA" } else { "UNZ" } {
            if self.group.is_some() || self.message.is_some() {
                return Err(0);
            }
            let control = self.interchange.as_ref().ok_or(0usize)?;
            count(
                1,
                if x12 || self.groups != 0 {
                    self.groups
                } else {
                    self.messages
                },
            )?;
            if reference(2)? != *control {
                return Err(2);
            }
            self.interchange = None;
        } else if self.message.is_none()
            && !(x12 && self.interchange.is_some() && self.group.is_none() && tag == "TA1")
        {
            return Err(0);
        }
        Ok(())
    }
}

pub fn validate(format: DocumentFormat, bytes: &[u8]) -> Result<(), EnvelopeError> {
    if format == DocumentFormat::Json {
        return Ok(());
    }
    let x12 = format == DocumentFormat::X12;
    let interchange = std::cell::Cell::new(1);
    let error = |segment: usize, tag: &str, element| EnvelopeError {
        interchange: interchange.get(),
        segment,
        tag: if tag.len() <= 3
            && tag
                .bytes()
                .all(|byte| byte.is_ascii_uppercase() || byte.is_ascii_digit())
        {
            tag.into()
        } else {
            String::new()
        },
        element,
    };
    let mut offset = 0;
    let mut number = 0;
    let mut state = State::default();
    let mut element = b'+';
    let mut terminator = b'\'';
    let mut release = Some(b'?');
    let mut component = b':';
    let mut repetition = None;
    let mut advised_repetition = None;
    let mut advice = false;
    while offset < bytes.len() {
        interchange.set(if state.open() {
            state.interchange_number.max(1)
        } else {
            state.interchange_number + 1
        });
        while offset < bytes.len() && bytes[offset].is_ascii_whitespace() {
            offset += 1;
        }
        if offset == bytes.len() {
            break;
        }
        if number >= 32768 {
            return Err(error(number + 1, "", 0));
        }
        if x12 && bytes[offset..].starts_with(b"ISA") {
            if state.open() {
                return Err(error(number + 1, "ISA", 0));
            }
            let syntax = crate::x12_syntax::read(&bytes[offset..])
                .ok_or_else(|| error(number + 1, "ISA", 0))?;
            element = syntax.element;
            terminator = syntax.segment;
            release = None;
            component = syntax.component;
            repetition = syntax.repetition;
        } else if !x12 && bytes[offset..].starts_with(b"UNA") {
            if state.open() || advice || bytes.len() - offset < 9 {
                return Err(error(number + 1, "UNA", 0));
            }
            element = bytes[offset + 4];
            terminator = bytes[offset + 8];
            release = Some(bytes[offset + 6]);
            component = bytes[offset + 3];
            advised_repetition = (bytes[offset + 7] != b' ').then_some(bytes[offset + 7]);
            repetition = advised_repetition;
            offset += 9;
            advice = true;
            continue;
        } else if !x12 && !state.open() && !advice {
            element = b'+';
            terminator = b'\'';
            release = Some(b'?');
            component = b':';
            advised_repetition = None;
            repetition = None;
        }
        if !element.is_ascii_punctuation()
            || !(terminator.is_ascii_punctuation() || x12 && terminator == b'\n')
            || element == terminator
            || release == Some(element)
            || release == Some(terminator)
        {
            return Err(error(number + 1, "", 0));
        }
        let start = offset;
        let mut fields = Vec::new();
        let mut value = Vec::new();
        let mut composite = false;
        let mut repeated = false;
        let isa = bytes[offset..].starts_with(b"ISA");
        let mut ended = false;
        while offset < bytes.len() && offset - start < 16384 {
            let byte = bytes[offset];
            offset += 1;
            if Some(byte) == release {
                if offset == bytes.len() {
                    return Err(error(number + 1, "", 0));
                }
                value.push(bytes[offset]);
                offset += 1;
            } else if byte == element || byte == terminator {
                fields.push(Field {
                    value: String::from_utf8(std::mem::take(&mut value))
                        .map_err(|_| error(number + 1, "", 0))?,
                    composite,
                    repeated,
                });
                composite = false;
                repeated = false;
                if fields.len() > 257 {
                    return Err(error(number + 1, "", 0));
                }
                if byte == terminator {
                    ended = true;
                    break;
                }
            } else {
                if byte < b' ' || byte == 127 {
                    return Err(error(number + 1, "", 0));
                }
                if !isa {
                    composite |= byte == component;
                    repeated |= Some(byte) == repetition
                        || !x12 && byte == advised_repetition.unwrap_or(b'*');
                }
                value.push(byte);
            }
        }
        if !ended || fields.is_empty() {
            return Err(error(number + 1, "", 0));
        }
        let tag = &fields[0].value;
        if tag.is_empty()
            || tag.len() > 3
            || !tag.as_bytes()[0].is_ascii_uppercase()
            || !tag
                .bytes()
                .all(|byte| byte.is_ascii_uppercase() || byte.is_ascii_digit())
        {
            return Err(error(number + 1, "", 0));
        }
        if advice && tag != "UNB" && tag != "UNH" {
            return Err(error(number + 1, tag, 0));
        }
        advice = false;
        if !x12 && tag == "UNB" {
            let version = fields
                .get(1)
                .and_then(|field| field.value.split(char::from(component)).nth(1));
            if !matches!(version, Some("1" | "2" | "3" | "4"))
                || version != Some("4") && advised_repetition.is_some()
            {
                return Err(error(number + 1, tag, 1));
            }
            repetition = (version == Some("4")).then_some(advised_repetition.unwrap_or(b'*'));
        }
        number += 1;
        state
            .accept(x12, number, &fields, repetition.is_some())
            .map_err(|element| EnvelopeError {
                interchange: state.interchange_number.max(1),
                ..error(number, tag, element)
            })?;
    }
    if state.open() || advice || number == 0 {
        return Err(error(number.max(1), "", 0));
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    const X12: &str = "ISA*00*          *00*          *ZZ*WEFTSENDER     *ZZ*WEFTRECEIVER   *261024*1200*^*00501*000000330*0*T*:~GS*PO*WEFTSENDER*WEFTRECEIVER*20261024*1200*330*X*005010~ST*850*0330~BEG*00*SA*WEFT-330~SE*3*0330~GE*1*330~IEA*1*000000330~";
    const EDIFACT: &str = "UNB+UNOC:3+SENDER+RECEIVER+261024:1200+330'UNH+330+ORDERS:D:96A:UN'BGM+220+WEFT-330'UNT+3+330'UNZ+1+330'";

    #[test]
    fn complete_multiple_interchanges_and_repeated_values_preserve_structure() {
        for (format, document, repeated) in [
            (DocumentFormat::X12, X12, "ONE^TWO:3"),
            (DocumentFormat::Edifact, EDIFACT, "ONE*TW?+O:3"),
        ] {
            assert_eq!(validate(format, document.as_bytes()), Ok(()));
            let multi = format!("{document}\n{}", document.replace("WEFT-330", repeated));
            assert_eq!(validate(format, multi.as_bytes()), Ok(()));
        }
        let messages = EDIFACT.replace(
            "UNZ+1+330'",
            "UNH+331+ORDERS:D:96A:UN'BGM+220+SECOND'UNT+3+331'UNZ+2+330'",
        );
        assert_eq!(
            validate(DocumentFormat::Edifact, messages.as_bytes()),
            Ok(())
        );
    }

    #[test]
    fn reference_and_count_failures_are_located_without_payload_values() {
        for (format, document, from, to, tag, element) in [
            (DocumentFormat::X12, X12, "SE*3*0330", "SE*9*0330", "SE", 1),
            (DocumentFormat::X12, X12, "SE*3*0330", "SE*3*9999", "SE", 2),
            (DocumentFormat::X12, X12, "GE*1*330", "GE*2*330", "GE", 1),
            (
                DocumentFormat::X12,
                X12,
                "IEA*1*000000330",
                "IEA*1*000000999",
                "IEA",
                2,
            ),
            (
                DocumentFormat::Edifact,
                EDIFACT,
                "UNT+3+330",
                "UNT+9+330",
                "UNT",
                1,
            ),
            (
                DocumentFormat::Edifact,
                EDIFACT,
                "UNZ+1+330",
                "UNZ+1+999",
                "UNZ",
                2,
            ),
        ] {
            let error = validate(format, document.replace(from, to).as_bytes()).unwrap_err();
            assert_eq!(error.tag, tag);
            assert_eq!(error.element, element);
        }
    }

    #[test]
    fn acknowledgements_keep_their_own_envelope_counts() {
        let ack = X12.replace(
            "ST*850*0330~BEG*00*SA*WEFT-330~SE*3*0330~",
            "ST*999*0330~AK1*PO*330~IK2*850*0330~AK9*A*1*1*1~SE*5*0330~",
        );
        assert_eq!(validate(DocumentFormat::X12, ack.as_bytes()), Ok(()));
        let ta1 = X12[..X12.find("GS*").unwrap()].to_owned()
            + "TA1*000000329*261024*1200*A*000~IEA*0*000000330~";
        assert_eq!(validate(DocumentFormat::X12, ta1.as_bytes()), Ok(()));
        let contrl = "UNB+UNOC:3+SENDER+RECEIVER+261024:1200+1'UNH+1+CONTRL:D:96A:UN'UCI+330+SENDER+RECEIVER+7'UNT+3+1'UNZ+1+1'";
        assert_eq!(validate(DocumentFormat::Edifact, contrl.as_bytes()), Ok(()));
    }

    #[test]
    fn escaped_edifact_separators_and_custom_service_advice_are_lexical() {
        assert_eq!(
            validate(DocumentFormat::X12, X12.replace('~', "\n").as_bytes()),
            Ok(())
        );
        let custom = "UNA>|.! ~".to_owned()
            + &EDIFACT
                .replace('+', "|")
                .replace(':', ">")
                .replace('\'', "~");
        assert_eq!(validate(DocumentFormat::Edifact, custom.as_bytes()), Ok(()));
        assert_eq!(
            validate(
                DocumentFormat::Edifact,
                EDIFACT.replace("WEFT-330", "WEFT?+?'330").as_bytes()
            ),
            Ok(())
        );
        assert!(validate(DocumentFormat::Edifact, b"UNH+1+ORDERS:D:96A:UN'UNT+9+1'").is_err());
    }

    #[test]
    fn control_fields_are_complete_scalars_but_release_characters_preserve_literal_content() {
        for (format, input) in [
            (
                DocumentFormat::Edifact,
                "UNH+1:A+ORDERS:D:96A:UN'BGM+220+X'UNT+3+1:B'".to_owned(),
            ),
            (
                DocumentFormat::Edifact,
                "UNH+1:A+ORDERS:D:96A:UN'UNT+2+1:A'".to_owned(),
            ),
            (
                DocumentFormat::Edifact,
                "UNA:+.?*'UNH+1*2+ORDERS:D:96A:UN'UNT+2+1*2'".to_owned(),
            ),
            (
                DocumentFormat::Edifact,
                EDIFACT.replace("UNT+3+", "UNT+3:4+"),
            ),
            (
                DocumentFormat::Edifact,
                EDIFACT
                    .replace("UNOC:3", "UNOC:4")
                    .replace("+330'", "+330*2'"),
            ),
            (
                DocumentFormat::X12,
                X12.replace("ST*850*0330", "ST*850*0330:2"),
            ),
            (DocumentFormat::X12, X12.replace("SE*3*0330", "SE*3^4*0330")),
            (DocumentFormat::X12, X12.replace("GE*1*330", "GE*1*330^2")),
            (DocumentFormat::X12, X12.replace("IEA*1*", "IEA*1:2*")),
        ] {
            assert!(validate(format, input.as_bytes()).is_err(), "{input}");
        }
        for reference in ["1?:A", "1?*A"] {
            let input = format!("UNA:+.?*'UNH+{reference}+ORDERS:D:96A:UN'UNT+2+{reference}'");
            assert_eq!(validate(DocumentFormat::Edifact, input.as_bytes()), Ok(()));
        }
    }
}
