//! Iterative, zero-copy quick-xml event scan before any DOM/projection. Values
//! are checked without unescaping into an unbounded String. Only namespace
//! declarations are retained, bounded by original bytes, depth and attributes.
use std::collections::{BTreeMap, BTreeSet};

use quick_xml::Reader;
use quick_xml::events::{BytesStart, Event};

#[path = "xml_name_chars.rs"]
mod name_chars;

use super::{
    Error, MAX_ATTRIBUTES, MAX_DEPTH, MAX_DOCUMENT_BYTES, MAX_INPUTS, MAX_NODES, MAX_OUTPUTS,
    Resource, Role,
};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct DocumentStats {
    pub bytes: usize,
    pub nodes: usize,
    pub maximum_depth: usize,
    pub maximum_attributes: usize,
}

#[derive(Clone, Copy, Debug)]
pub struct Safety {
    maximum_bytes: usize,
}

impl Safety {
    pub fn new(maximum_bytes: usize) -> Result<Self, Error> {
        if maximum_bytes == 0 || maximum_bytes > MAX_DOCUMENT_BYTES {
            return Err(Error::InvalidLimits);
        }
        Ok(Self { maximum_bytes })
    }

    pub fn validate_bytes(&self, bytes: &[u8]) -> Result<DocumentStats, Error> {
        self.validate_bytes_with_cancel(bytes, || false)
    }

    pub fn validate_set(
        &self,
        documents: &[&[u8]],
        role: Role,
    ) -> Result<Vec<DocumentStats>, Error> {
        self.validate_set_with_cancel(documents, role, || false)
    }

    /// First check the complete byte set; no caller should publish any part
    /// until this returns success for every document.
    pub fn validate_set_with_cancel(
        &self,
        documents: &[&[u8]],
        role: Role,
        mut cancelled: impl FnMut() -> bool,
    ) -> Result<Vec<DocumentStats>, Error> {
        let count_limit = if role == Role::Input {
            MAX_INPUTS
        } else {
            MAX_OUTPUTS
        };
        if documents.is_empty() || documents.len() > count_limit {
            return Err(Error::ResourceLimit(Resource::DocumentCount));
        }
        let mut total = 0usize;
        for document in documents {
            poll(&mut cancelled)?;
            total = total
                .checked_add(document.len())
                .filter(|n| *n <= self.maximum_bytes)
                .ok_or(Error::ResourceLimit(Resource::AggregateBytes))?;
        }
        documents
            .iter()
            .map(|bytes| self.validate_bytes_with_cancel(bytes, &mut cancelled))
            .collect()
    }

    pub fn validate_bytes_with_cancel(
        &self,
        bytes: &[u8],
        mut cancelled: impl FnMut() -> bool,
    ) -> Result<DocumentStats, Error> {
        poll(&mut cancelled)?;
        if bytes.len() > self.maximum_bytes {
            return Err(Error::ResourceLimit(Resource::DocumentBytes));
        }
        let text = std::str::from_utf8(bytes).map_err(|_| Error::InvalidEncoding)?;
        // XML 1.0 character check in bounded work chunks, including ignored
        // content. References are validated separately against the same range.
        for (index, ch) in text.chars().enumerate() {
            if index % 2048 == 0 {
                poll(&mut cancelled)?;
            }
            if !xml_char(ch) {
                return Err(Error::InvalidInput);
            }
        }
        let text = text.strip_prefix('\u{feff}').unwrap_or(text);
        if text.is_empty() || text.starts_with('\u{feff}') {
            return Err(Error::InvalidInput);
        }
        let mut reader = Reader::from_str(text);
        let config = reader.config_mut();
        config.trim_text(false);
        config.check_comments = true;
        config.check_end_names = true;
        config.allow_unmatched_ends = false;
        config.allow_dangling_amp = false;
        config.expand_empty_elements = false;
        let mut stats = DocumentStats {
            bytes: bytes.len(),
            nodes: 1,
            maximum_depth: 0,
            maximum_attributes: 0,
        };
        let mut scopes: Vec<Vec<String>> = Vec::new();
        let mut namespaces = Namespaces::default();
        let mut roots = 0usize;
        let mut first_event = true;
        let mut text_run = false;
        loop {
            poll(&mut cancelled)?;
            let event = reader.read_event().map_err(|_| Error::InvalidInput)?;
            let empty = matches!(&event, Event::Empty(_));
            match event {
                Event::Decl(declaration) => {
                    if !first_event {
                        return Err(Error::InvalidDeclaration);
                    }
                    // Validate the complete declaration, including ordering,
                    // duplicate/unknown pseudoattributes, not just version().
                    let start = BytesStart::from_content(
                        std::str::from_utf8(declaration.as_ref())
                            .map_err(|_| Error::InvalidDeclaration)?,
                        3,
                    );
                    let start_bytes: &[u8] = start.as_ref();
                    if !start_bytes.starts_with(b"xml ")
                        && !start_bytes.get(3).is_some_and(|b| xml_space(*b))
                    {
                        return Err(Error::InvalidDeclaration);
                    }
                    let mut state = 0;
                    for attribute in start.attributes().with_checks(false) {
                        let attribute = attribute.map_err(|_| Error::InvalidDeclaration)?;
                        require_attribute_separator(&start, attribute.key.as_ref())
                            .map_err(|_| Error::InvalidDeclaration)?;
                        match (state, attribute.key.as_ref(), attribute.value.as_ref()) {
                            (0, b"version", b"1.0") => state = 1,
                            (1, b"encoding", value) if value.eq_ignore_ascii_case(b"utf-8") => {
                                state = 2
                            }
                            (1 | 2, b"standalone", b"yes" | b"no") => state = 3,
                            _ => return Err(Error::InvalidDeclaration),
                        }
                    }
                    if state == 0 {
                        return Err(Error::InvalidDeclaration);
                    }
                    text_run = false;
                }
                Event::DocType(_) => return Err(Error::DtdProhibited),
                Event::Start(start) | Event::Empty(start) => {
                    let depth = scopes.len() + 1;
                    if depth > MAX_DEPTH {
                        return Err(Error::ResourceLimit(Resource::PhysicalDepth));
                    }
                    if scopes.is_empty() {
                        roots += 1;
                        if roots != 1 {
                            return Err(Error::InvalidInput);
                        }
                    }
                    stats.maximum_depth = stats.maximum_depth.max(depth);
                    add_node(&mut stats)?;
                    let declarations =
                        check_start(&start, &mut namespaces, &mut stats, &mut cancelled)?;
                    if empty {
                        namespaces.pop(declarations);
                    } else {
                        scopes.push(declarations);
                    }
                    text_run = false;
                }
                Event::End(_) => {
                    namespaces.pop(scopes.pop().ok_or(Error::InvalidInput)?);
                    text_run = false;
                }
                Event::Text(value) => {
                    let text_bytes: &[u8] = value.as_ref();
                    if text_bytes.windows(3).any(|v| v == b"]]>") {
                        return Err(Error::InvalidInput);
                    }
                    if scopes.is_empty() {
                        if !text_bytes.iter().copied().all(xml_space) {
                            return Err(Error::InvalidInput);
                        }
                    } else if !text_bytes.is_empty() && !text_run {
                        add_node(&mut stats)?;
                        text_run = true;
                    }
                }
                Event::CData(_) => {
                    if scopes.is_empty() {
                        return Err(Error::InvalidInput);
                    }
                    // Even an empty CDATA section is a physical text run.
                    if !text_run {
                        add_node(&mut stats)?;
                        text_run = true;
                    }
                }
                Event::GeneralRef(value) => {
                    if scopes.is_empty() {
                        return Err(Error::InvalidInput);
                    }
                    let reference_bytes: &[u8] = value.as_ref();
                    reference(reference_bytes, &mut cancelled)?;
                    if !text_run {
                        add_node(&mut stats)?;
                        text_run = true;
                    }
                }
                Event::Comment(value) => {
                    let comment_bytes: &[u8] = value.as_ref();
                    if comment_bytes.last() == Some(&b'-') {
                        return Err(Error::InvalidInput);
                    }
                    add_node(&mut stats)?;
                    text_run = false;
                }
                Event::PI(value) => {
                    let target =
                        std::str::from_utf8(value.target()).map_err(|_| Error::InvalidInput)?;
                    if target.eq_ignore_ascii_case("xml") {
                        return Err(Error::InvalidInput);
                    }
                    // XmlReader.Create uses ParseQName(false) for PI targets:
                    // no colon, even though element/attribute QNames permit one.
                    if !ncname_is_supported(target) {
                        return Err(Error::UnsupportedName);
                    }
                    add_node(&mut stats)?;
                    text_run = false;
                }
                Event::Eof => {
                    if roots != 1 || !scopes.is_empty() {
                        return Err(Error::InvalidInput);
                    }
                    return Ok(stats);
                }
            }
            first_event = false;
        }
    }
}

fn poll(cancelled: &mut impl FnMut() -> bool) -> Result<(), Error> {
    if cancelled() {
        Err(Error::Cancelled)
    } else {
        Ok(())
    }
}
fn add_node(stats: &mut DocumentStats) -> Result<(), Error> {
    stats.nodes = stats
        .nodes
        .checked_add(1)
        .filter(|n| *n <= MAX_NODES)
        .ok_or(Error::ResourceLimit(Resource::PhysicalNodes))?;
    Ok(())
}
fn xml_space(byte: u8) -> bool {
    matches!(byte, b' ' | b'\t' | b'\r' | b'\n')
}
fn xml_char(ch: char) -> bool {
    matches!(ch as u32, 0x9 | 0xa | 0xd | 0x20..=0xd7ff | 0xe000..=0xfffd | 0x10000..=0x10ffff)
}

// The shared physical-name profile is the pinned .NET reader's Fourth Edition
// BMP NCName table, not the broader Fifth Edition set. It does not constrain
// character data, attribute values or namespace URI strings.
pub(super) fn ncname_is_supported(name: &str) -> bool {
    let mut chars = name.chars();
    chars.next().is_some_and(name_chars::is_ncname_start) && chars.all(name_chars::is_ncname_char)
}
fn qname(bytes: &[u8]) -> Result<(&str, &str), Error> {
    let name = std::str::from_utf8(bytes).map_err(|_| Error::InvalidInput)?;
    let (prefix, local) = name.split_once(':').unwrap_or(("", name));
    if (!prefix.is_empty() && !ncname_is_supported(prefix))
        || !ncname_is_supported(local)
        || name.starts_with(':')
    {
        return Err(Error::UnsupportedName);
    }
    Ok((prefix, local))
}

fn reference(bytes: &[u8], cancelled: &mut impl FnMut() -> bool) -> Result<char, Error> {
    let named = match bytes {
        b"amp" => Some('&'),
        b"lt" => Some('<'),
        b"gt" => Some('>'),
        b"apos" => Some('\''),
        b"quot" => Some('"'),
        _ => None,
    };
    if let Some(ch) = named {
        return Ok(ch);
    }
    let (digits, radix) = if let Some(digits) = bytes.strip_prefix(b"#x") {
        (digits, 16u32)
    } else if let Some(digits) = bytes.strip_prefix(b"#") {
        (digits, 10u32)
    } else {
        return Err(Error::InvalidInput);
    };
    if digits.is_empty() {
        return Err(Error::InvalidInput);
    }
    let mut value = 0u32;
    for (index, &digit) in digits.iter().enumerate() {
        if index % 2048 == 0 {
            poll(cancelled)?;
        }
        let digit = match digit {
            b'0'..=b'9' => u32::from(digit - b'0'),
            b'a'..=b'f' if radix == 16 => u32::from(digit - b'a') + 10,
            b'A'..=b'F' if radix == 16 => u32::from(digit - b'A') + 10,
            _ => return Err(Error::InvalidInput),
        };
        value = value
            .checked_mul(radix)
            .and_then(|n| n.checked_add(digit))
            .ok_or(Error::InvalidInput)?;
    }
    char::from_u32(value)
        .filter(|ch| xml_char(*ch))
        .ok_or(Error::InvalidInput)
}

fn attribute_chars(
    value: &[u8],
    cancelled: &mut impl FnMut() -> bool,
    mut accept: impl FnMut(char),
) -> Result<(), Error> {
    let text = std::str::from_utf8(value).map_err(|_| Error::InvalidInput)?;
    let mut offset = 0usize;
    while offset < text.len() {
        poll(cancelled)?;
        // Bounded chunks also cover huge attributes that need no unescaping.
        let end = text.len().min(offset + 2048);
        while offset < end {
            if value[offset] == b'&' {
                let start = offset + 1;
                offset = start;
                while offset < value.len() && value[offset] != b';' {
                    if offset.is_multiple_of(2048) {
                        poll(cancelled)?;
                    }
                    offset += 1;
                }
                if offset == value.len() {
                    return Err(Error::InvalidInput);
                }
                accept(reference(&value[start..offset], cancelled)?);
                offset += 1;
            } else {
                let ch = text[offset..].chars().next().ok_or(Error::InvalidInput)?;
                if ch == '<' {
                    return Err(Error::InvalidInput);
                }
                offset += ch.len_utf8();
                if ch == '\r' && value.get(offset) == Some(&b'\n') {
                    offset += 1;
                }
                accept(if matches!(ch, '\t' | '\n' | '\r') {
                    ' '
                } else {
                    ch
                });
            }
        }
    }
    Ok(())
}

const XML_NS: &str = "http://www.w3.org/XML/1998/namespace";
const XMLNS_NS: &str = "http://www.w3.org/2000/xmlns/";
// Logarithmic lookup avoids rescanning depth * attributes bindings for every
// physical name in an adversarial document. Shadowed bindings are restored.
#[derive(Default)]
struct Namespaces {
    bindings: BTreeMap<String, Vec<String>>,
}
impl Namespaces {
    fn resolve(&self, prefix: &str) -> Option<&str> {
        if prefix == "xml" {
            return Some(XML_NS);
        }
        self.bindings
            .get(prefix)
            .and_then(|stack| stack.last())
            .map(String::as_str)
            .or(if prefix.is_empty() { Some("") } else { None })
    }
    fn push(&mut self, declarations: Vec<(String, String)>) -> Vec<String> {
        declarations
            .into_iter()
            .map(|(prefix, uri)| {
                self.bindings.entry(prefix.clone()).or_default().push(uri);
                prefix
            })
            .collect()
    }
    fn pop(&mut self, prefixes: Vec<String>) {
        for prefix in prefixes {
            if let Some(stack) = self.bindings.get_mut(&prefix) {
                stack.pop();
                if stack.is_empty() {
                    self.bindings.remove(&prefix);
                }
            }
        }
    }
}

fn check_start(
    start: &BytesStart<'_>,
    namespaces: &mut Namespaces,
    stats: &mut DocumentStats,
    cancelled: &mut impl FnMut() -> bool,
) -> Result<Vec<String>, Error> {
    let name = start.name();
    let (element_prefix, _) = qname(name.as_ref())?;
    let mut raw_names = BTreeSet::new();
    let mut attributes = Vec::new();
    // Disable the library's unbounded duplicate collector. Count physical
    // attributes first, including declarations, before allocating any values.
    for attribute in start.attributes().with_checks(false) {
        poll(cancelled)?;
        if attributes.len() == MAX_ATTRIBUTES {
            return Err(Error::ResourceLimit(Resource::Attributes));
        }
        let attribute = attribute.map_err(|_| Error::InvalidInput)?;
        require_attribute_separator(start, attribute.key.as_ref())?;
        if !raw_names.insert(attribute.key.into_inner()) {
            return Err(Error::InvalidInput);
        }
        qname(attribute.key.as_ref())?;
        attributes.push(attribute);
    }
    stats.maximum_attributes = stats.maximum_attributes.max(attributes.len());
    let mut declarations = Vec::new();
    for attribute in &attributes {
        let (prefix, local) = qname(attribute.key.as_ref())?;
        let declared = if prefix == "xmlns" {
            Some(local)
        } else if prefix.is_empty() && local == "xmlns" {
            Some("")
        } else {
            None
        };
        if let Some(declared) = declared {
            let mut uri = String::new();
            attribute_chars(attribute.value.as_ref(), cancelled, |ch| uri.push(ch))?;
            if declared == "xmlns"
                || uri == XMLNS_NS
                || (declared == "xml") != (uri == XML_NS)
                || (!declared.is_empty() && uri.is_empty())
            {
                return Err(Error::InvalidInput);
            }
            declarations.push((declared.to_owned(), uri));
        } else {
            attribute_chars(attribute.value.as_ref(), cancelled, |_| {})?;
        }
    }
    let scope = namespaces.push(declarations);
    if element_prefix == "xmlns" || namespaces.resolve(element_prefix).is_none() {
        return Err(Error::InvalidInput);
    }
    let mut expanded = BTreeSet::new();
    for attribute in &attributes {
        let (prefix, local) = qname(attribute.key.as_ref())?;
        if prefix == "xmlns" || (prefix.is_empty() && local == "xmlns") {
            continue;
        }
        let uri = if prefix.is_empty() {
            ""
        } else {
            namespaces.resolve(prefix).ok_or(Error::InvalidInput)?
        };
        if !expanded.insert((uri, local)) {
            return Err(Error::InvalidInput);
        }
    }
    Ok(scope)
}

// quick-xml's attribute iterator does not require whitespace after the previous
// quoted value. Keys borrow this event's slice; inspect the preceding byte
// without unsafe code, retaining XML's mandatory attribute separator.
fn require_attribute_separator(start: &BytesStart<'_>, key: &[u8]) -> Result<(), Error> {
    let start_bytes: &[u8] = start.as_ref();
    let offset = (key.as_ptr() as usize)
        .checked_sub(start_bytes.as_ptr() as usize)
        .and_then(|offset| offset.checked_sub(1))
        .ok_or(Error::InvalidInput)?;
    if start_bytes.get(offset).is_some_and(|b| xml_space(*b)) {
        Ok(())
    } else {
        Err(Error::InvalidInput)
    }
}
