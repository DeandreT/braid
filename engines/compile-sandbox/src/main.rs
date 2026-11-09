#[cfg(all(target_os = "linux", target_arch = "x86_64"))]
use std::io::{self, Write};
use std::process::ExitCode;

#[cfg(all(target_os = "linux", target_arch = "x86_64"))]
use weft_compile_sandbox as sandbox;

#[cfg(not(all(target_os = "linux", target_arch = "x86_64")))]
fn main() -> ExitCode {
    ExitCode::FAILURE
}

#[cfg(all(target_os = "linux", target_arch = "x86_64"))]
fn main() -> ExitCode {
    let arguments = std::env::args_os().skip(1).collect::<Vec<_>>();
    let launch = match sandbox::parse_arguments(&arguments) {
        Ok(launch) => launch,
        Err(error) => return ExitCode::from(error.exit_code()),
    };
    // Every control is in force before the marker; the program starts only after the service has verified them.
    if let Err(error) = sandbox::enter(&launch) {
        return ExitCode::from(error.exit_code());
    }
    let mut output = io::stdout().lock();
    if output
        .write_all(&sandbox::READY_MARKER)
        .and_then(|()| output.flush())
        .is_err()
    {
        return ExitCode::from(sandbox::SandboxError::ReadyMarker.exit_code());
    }
    drop(output);
    // One unbuffered byte: nothing beyond it is consumed from the program's standard input.
    let mut go = 0u8;
    if unsafe { libc::read(0, (&raw mut go).cast(), 1) } != 1 || go != sandbox::GO {
        return ExitCode::FAILURE;
    }
    match sandbox::execute(&launch) {
        Ok(0) => ExitCode::SUCCESS,
        _ => ExitCode::FAILURE,
    }
}
