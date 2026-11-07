use std::io::{self, Write};
use std::process::ExitCode;

use weft_ferrule_host::sandbox;

fn main() -> ExitCode {
    let arguments = std::env::args_os().skip(1).collect::<Vec<_>>();
    let input = io::stdin();
    let output = io::stdout();
    let mut output = output.lock();
    match sandbox::parse_arguments(&arguments) {
        // The container worker: Docker supplies the isolation.
        Ok(None) => {}
        // A Ferrule service child: every control is in force before the marker and before any input.
        Ok(Some(limits)) => {
            if let Err(error) = sandbox::enter(&limits) {
                return ExitCode::from(error.exit_code());
            }
            if output
                .write_all(&sandbox::READY_MARKER)
                .and_then(|()| output.flush())
                .is_err()
            {
                return ExitCode::from(sandbox::SandboxError::ReadyMarker.exit_code());
            }
        }
        Err(error) => return ExitCode::from(error.exit_code()),
    }
    match weft_ferrule_host::handle_stream(&mut input.lock(), &mut output) {
        Ok(()) => ExitCode::SUCCESS,
        Err(_) => ExitCode::FAILURE,
    }
}
