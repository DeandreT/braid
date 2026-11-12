use sha2::{Digest, Sha256};

use crate::protocol::{Limits, NativeLocator};

use super::types::{Detail, Gap, Literal, NodeReference};

pub(crate) const MAX_DEPTH: usize = 16;

pub(crate) struct Budget {
    maximum_items: usize,
    items: usize,
    maximum_diagnostics: usize,
    diagnostics: usize,
    pub maximum_text: usize,
}

impl Budget {
    pub fn new(limits: &Limits) -> Self {
        Self {
            maximum_items: limits.maximum_items as usize,
            items: 0,
            maximum_diagnostics: limits.maximum_diagnostics as usize,
            diagnostics: 0,
            maximum_text: limits.maximum_text_bytes as usize,
        }
    }

    pub fn charge(&mut self, count: usize) -> Result<(), &'static str> {
        let total = self
            .items
            .checked_add(count)
            .ok_or("Projection item count overflowed.")?;
        if total > self.maximum_items {
            return Err("The projection exceeds its item limit.");
        }
        self.items = total;
        Ok(())
    }

    pub fn text(&self, text: &str) -> Result<String, &'static str> {
        if text.len() > self.maximum_text {
            return Err("An exact projection string exceeds its text limit.");
        }
        Ok(text.into())
    }

    pub fn id(&self, kind: &str, parts: &[&str]) -> Result<String, &'static str> {
        let mut digest = Sha256::new();
        for part in std::iter::once(kind).chain(parts.iter().copied()) {
            digest.update((part.len() as u64).to_be_bytes());
            digest.update(part.as_bytes());
        }
        self.text(&format!("{kind}:{:x}", digest.finalize()))
    }

    pub fn pointer(
        &self,
        document_id: &str,
        pointer: String,
    ) -> Result<NativeLocator, &'static str> {
        Ok(NativeLocator::JsonPointer {
            document_id: self.text(document_id)?,
            pointer: self.text(&pointer)?,
        })
    }

    pub fn gap(
        &mut self,
        code: &str,
        message: &str,
        native_locator: NativeLocator,
        projection_locator: Option<NodeReference>,
    ) -> Result<Gap, &'static str> {
        if self.diagnostics >= self.maximum_diagnostics {
            return Err("The projection exceeds its diagnostic limit.");
        }
        self.diagnostics += 1;
        self.charge(4 + usize::from(projection_locator.is_some()) * 2)?;
        Ok(Gap {
            code: self.text(code)?,
            message: self.text(message)?,
            native_locator,
            projection_locator,
        })
    }

    pub fn detail(&mut self, detail: &Detail, depth: usize) -> Result<(), &'static str> {
        if depth > MAX_DEPTH {
            return Err("The projection exceeds its detail-depth limit.");
        }
        self.charge(1)?;
        match detail {
            Detail::Group { label, children } => {
                self.text(label)?;
                for child in children {
                    self.detail(child, depth + 1)?;
                }
            }
            Detail::Expression {
                operation,
                arguments,
            } => {
                self.text(operation)?;
                for argument in arguments {
                    self.detail(argument, depth + 1)?;
                }
            }
            Detail::Literal { value } => self.literal(value)?,
            Detail::Reference { .. } => self.charge(1)?,
            Detail::Unsupported {
                construct,
                description,
                ..
            } => {
                self.charge(1)?;
                self.text(construct)?;
                self.text(description)?;
            }
        }
        Ok(())
    }

    pub fn literal(&mut self, literal: &Literal) -> Result<(), &'static str> {
        self.charge(1)?;
        match literal {
            Literal::String { value } | Literal::ExactNumber { value, .. } => {
                self.text(value)?;
            }
            _ => {}
        }
        Ok(())
    }
}
