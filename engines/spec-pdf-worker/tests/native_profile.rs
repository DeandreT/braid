use std::process::Command;

#[test]
fn caller_paths_and_changed_native_resource_profiles_are_refused() {
    for arguments in [
        vec!["document.pdf"],
        vec!["https://example.invalid/document.pdf"],
        vec![
            "--service-sandbox",
            "--cpu-seconds=11",
            "--memory-bytes=536870912",
            "--open-files=64",
        ],
        vec![
            "--service-sandbox",
            "--cpu-seconds=10",
            "--memory-bytes=268435456",
            "--open-files=64",
        ],
        vec![
            "--service-sandbox",
            "--cpu-seconds=10",
            "--memory-bytes=536870912",
            "--open-files=65",
        ],
    ] {
        let output = Command::new(env!("CARGO_BIN_EXE_braid-spec-pdf-worker"))
            .args(arguments)
            .output()
            .unwrap();
        assert_eq!(output.status.code(), Some(80));
        assert!(output.stdout.is_empty());
        assert!(output.stderr.is_empty());
    }
}

#[cfg(not(all(target_os = "linux", target_arch = "x86_64")))]
#[test]
fn native_sandbox_does_not_fall_back_to_unrestricted_execution() {
    let output = Command::new(env!("CARGO_BIN_EXE_braid-spec-pdf-worker"))
        .args([
            "--service-sandbox",
            "--cpu-seconds=10",
            "--memory-bytes=536870912",
            "--open-files=64",
        ])
        .output()
        .unwrap();
    assert_eq!(output.status.code(), Some(81));
    assert!(output.stdout.is_empty());
    assert!(output.stderr.is_empty());
}
