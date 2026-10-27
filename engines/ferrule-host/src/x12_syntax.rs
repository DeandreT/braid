//! A complete fixed-width ISA service declaration, shared by admission and limits.

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) struct X12Syntax {
    pub element: u8,
    pub component: u8,
    pub segment: u8,
    pub repetition: Option<u8>,
}

pub(crate) fn read(bytes: &[u8]) -> Option<X12Syntax> {
    let isa = bytes.get(..106)?;
    if !isa.starts_with(b"ISA") || isa[..105].iter().any(|byte| !(b' '..=b'~').contains(byte)) {
        return None;
    }
    let element = isa[3];
    let positions = [
        3, 6, 17, 20, 31, 34, 50, 53, 69, 76, 81, 83, 89, 99, 101, 103,
    ];
    if isa[..105]
        .iter()
        .enumerate()
        .any(|(index, byte)| (*byte == element) != positions.contains(&index) || *byte == isa[105])
        || !isa[84..89].iter().all(u8::is_ascii_digit)
    {
        return None;
    }
    let syntax = X12Syntax {
        element,
        component: isa[104],
        segment: isa[105],
        repetition: (&isa[84..89] >= b"00501".as_slice()).then_some(isa[82]),
    };
    if !syntax.element.is_ascii_punctuation()
        || !syntax.component.is_ascii_punctuation()
        || !(syntax.segment.is_ascii_punctuation() || syntax.segment == b'\n')
        || syntax.element == syntax.component
        || syntax.element == syntax.segment
        || syntax.component == syntax.segment
        || syntax.repetition.is_some_and(|value| {
            !value.is_ascii_punctuation()
                || [syntax.element, syntax.component, syntax.segment].contains(&value)
        })
    {
        return None;
    }
    Some(syntax)
}
