use std::io::{self, Read, Write};

use serde::Serialize;

use crate::protocol::{MAX_RESPONSE_BYTES, Response};

pub fn read_frame(reader: &mut impl Read, maximum_bytes: usize) -> Result<Vec<u8>, &'static str> {
    let mut prefix = [0u8; 4];
    reader
        .read_exact(&mut prefix)
        .map_err(|_| "A frame length is incomplete.")?;
    let length = u32::from_be_bytes(prefix) as usize;
    if length == 0 || length > maximum_bytes {
        return Err("A frame length exceeds its admitted range.");
    }
    let mut bytes = vec![0; length];
    reader
        .read_exact(&mut bytes)
        .map_err(|_| "A frame body is incomplete.")?;
    Ok(bytes)
}

pub fn require_eof(reader: &mut impl Read) -> Result<(), &'static str> {
    let mut trailing = [0u8; 1];
    loop {
        match reader.read(&mut trailing) {
            Ok(0) => return Ok(()),
            Ok(_) => return Err("Trailing request bytes are not admitted."),
            Err(error) if error.kind() == io::ErrorKind::Interrupted => {}
            Err(_) => return Err("The request stream could not be completed."),
        }
    }
}

pub fn encode_response(
    response: &Response,
    maximum_bytes: usize,
) -> Result<Vec<u8>, serde_json::Error> {
    encode_bounded(response, maximum_bytes.min(MAX_RESPONSE_BYTES))
}

/// Serializes without allocating beyond the supplied byte bound.
pub fn encode_bounded(
    value: &impl Serialize,
    maximum_bytes: usize,
) -> Result<Vec<u8>, serde_json::Error> {
    let mut writer = BoundedWriter {
        bytes: Vec::new(),
        maximum_bytes,
    };
    serde_json::to_writer(&mut writer, value)?;
    Ok(writer.bytes)
}

pub fn write_response(writer: &mut impl Write, response: &Response) -> io::Result<()> {
    let bytes = match encode_response(response, MAX_RESPONSE_BYTES) {
        Ok(bytes) => bytes,
        Err(_) => {
            let failure = response.replace_result_with_failure(
                "resultLimitExceeded",
                "The response exceeds its compiled byte limit.",
            );
            encode_response(&failure, MAX_RESPONSE_BYTES).map_err(io::Error::other)?
        }
    };
    let length = u32::try_from(bytes.len()).map_err(io::Error::other)?;
    writer.write_all(&length.to_be_bytes())?;
    writer.write_all(&bytes)?;
    writer.flush()
}

struct BoundedWriter {
    bytes: Vec<u8>,
    maximum_bytes: usize,
}

impl Write for BoundedWriter {
    fn write(&mut self, bytes: &[u8]) -> io::Result<usize> {
        if bytes.len() > self.maximum_bytes.saturating_sub(self.bytes.len()) {
            return Err(io::Error::other("Response byte limit exceeded."));
        }
        self.bytes.extend_from_slice(bytes);
        Ok(bytes.len())
    }

    fn flush(&mut self) -> io::Result<()> {
        Ok(())
    }
}
