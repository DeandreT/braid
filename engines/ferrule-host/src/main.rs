use std::io;
use std::process::ExitCode;

fn main() -> ExitCode {
    let input = io::stdin();
    let output = io::stdout();
    match weft_ferrule_host::handle_stream(&mut input.lock(), &mut output.lock()) {
        Ok(()) => ExitCode::SUCCESS,
        Err(_) => ExitCode::FAILURE,
    }
}
