use std::io::{self, Write};

use serde::Serialize;

/// A bounded serialization sink shared by native and wasm hosts.
pub fn encode(value: &impl Serialize, maximum: usize) -> Result<Vec<u8>, serde_json::Error> {
    let mut writer = Bounded {
        bytes: Vec::new(),
        maximum,
    };
    serde_json::to_writer(&mut writer, value)?;
    Ok(writer.bytes)
}

struct Bounded {
    bytes: Vec<u8>,
    maximum: usize,
}

impl Write for Bounded {
    fn write(&mut self, bytes: &[u8]) -> io::Result<usize> {
        if bytes.len() > self.maximum.saturating_sub(self.bytes.len()) {
            return Err(io::Error::other("Serialized byte limit exceeded."));
        }
        self.bytes.extend_from_slice(bytes);
        Ok(bytes.len())
    }
    fn flush(&mut self) -> io::Result<()> {
        Ok(())
    }
}
