//! Cross-target harness: exact same request as the dedicated worker, without a sandbox.
use std::io::{self, Read, Write};

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let mut bytes = Vec::new();
    io::stdin()
        .take(
            (braid_ferrule_host::protocol::MAX_NATIVE_BYTES
                + braid_ferrule_core::edit::MAX_OPERATIONS_BYTES
                + 16396) as u64,
        )
        .read_to_end(&mut bytes)?;
    braid_ferrule_host::handle_stream(&mut bytes.as_slice(), &mut io::stdout().lock())?;
    io::stdout().flush()?;
    Ok(())
}
