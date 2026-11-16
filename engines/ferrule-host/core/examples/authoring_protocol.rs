//! Qualification-only portable wire harness. NOT an isolated production worker.
use braid_ferrule_core::{authoring::transport, protocol::MAX_HEADER_BYTES};
use std::io::{self, Read};

fn main() -> io::Result<()> {
    let mut input = io::stdin().lock();
    let mut length = [0; 4];
    input.read_exact(&mut length)?;
    let length = u32::from_be_bytes(length) as usize;
    if length == 0 || length > MAX_HEADER_BYTES {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "Invalid header length",
        ));
    }
    let mut header = vec![0; length];
    input.read_exact(&mut header)?;
    transport::handle(&header, &mut input, &mut io::stdout().lock())
}
