use std::io::{Read, Write};
use weft_spec_pdf_worker::{MAX_INPUT_BYTES, encode, extract};

fn main() {
    std::panic::set_hook(Box::new(|_| {}));
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
