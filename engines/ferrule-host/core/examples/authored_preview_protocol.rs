//! Portable qualification process, not the production resource-isolated host.
fn main() {
    std::panic::set_hook(Box::new(|_| {}));
    let result = braid_ferrule_core::authored_document::transport::serve(
        &mut std::io::stdin().lock(),
        &mut std::io::stdout().lock(),
    );
    if result.is_err() {
        std::process::exit(2);
    }
}
