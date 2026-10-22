use std::io::{self, Write};
use std::process::ExitCode;

use weft_compile_sandbox as sandbox;

fn main() -> ExitCode {
    let arguments = std::env::args_os().skip(1).collect::<Vec<_>>();
    let Ok(launch) = sandbox::parse_arguments(&arguments) else {
        return ExitCode::FAILURE;
    };
    // Every control is in force before the marker; the program starts only after the service has verified them.
    if sandbox::enter(&launch).is_err() {
        return ExitCode::FAILURE;
    }
    let mut output = io::stdout().lock();
    if output
        .write_all(&sandbox::READY_MARKER)
        .and_then(|()| output.flush())
        .is_err()
    {
        return ExitCode::FAILURE;
    }
    drop(output);
    // One unbuffered byte: nothing beyond it is consumed from the program's standard input.
    let mut go = 0u8;
    if unsafe { libc::read(0, (&raw mut go).cast(), 1) } != 1 || go != sandbox::GO {
        return ExitCode::FAILURE;
    }
    let _ = sandbox::execute(&launch);
    ExitCode::FAILURE
}
