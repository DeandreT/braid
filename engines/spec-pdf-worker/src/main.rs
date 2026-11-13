use std::io::{Read, Write};
use weft_spec_pdf_worker::{MAX_INPUT_BYTES, encode, extract};

// Share the reviewed native self-hardening implementation, not the hosted-.NET launcher.
#[path = "../../ferrule-host/src/sandbox.rs"]
#[allow(dead_code)] // The shared module includes startup categories used only on Linux.
mod sandbox;

fn main() {
    std::panic::set_hook(Box::new(|_| {}));
    let arguments: Vec<_> = std::env::args_os().skip(1).collect();
    match sandbox::parse_arguments(&arguments) {
        Ok(None) => (), // Portable protocol tests; production always selects the fixed sandbox profile.
        Ok(Some(limits)) => {
            if limits.cpu_seconds != 10
                || limits.memory_bytes != 536_870_912
                || limits.open_files != 64
            {
                std::process::exit(80);
            }
            if let Err(error) = sandbox::enter(&limits) {
                std::process::exit(error.exit_code().into());
            }
            if std::io::stdout().write_all(&sandbox::READY_MARKER).is_err()
                || std::io::stdout().flush().is_err()
            {
                std::process::exit(89);
            }
        }
        Err(error) => std::process::exit(error.exit_code().into()),
    }
    // Never emit parser messages, source bytes or paths on stderr. A truncated
    // input is refused without claiming a digest of the original captured source.
    let mut bytes = Vec::new();
    if std::io::stdin()
        .take(MAX_INPUT_BYTES as u64 + 1)
        .read_to_end(&mut bytes)
        .is_err()
        || bytes.len() > MAX_INPUT_BYTES
    {
        std::process::exit(2);
    }
    match encode(&extract(&bytes)) {
        Ok(result) if std::io::stdout().write_all(&result).is_ok() => (),
        _ => std::process::exit(3),
    }
}
