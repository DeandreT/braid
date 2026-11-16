//! Service-child sandbox qualification. This target has no libtest harness: each scenario that applies the
//! sandbox runs in a fresh single-threaded copy of this executable, so filters, limits and descriptor checks
//! never affect the parent or other scenarios.

mod support;

use std::ffi::OsString;
use std::io::{Read, Write};
use std::os::unix::process::ExitStatusExt;
use std::path::PathBuf;
use std::process::{Command, Stdio};
use std::time::{Duration, Instant};

use braid_ferrule_host::preview::protocol::{
    MAX_PREVIEW_INPUT_BYTES, MAX_PREVIEW_OUTPUT_BYTES, PREVIEW_PROTOCOL_VERSION, PreviewInput,
    PreviewLimits, PreviewParameter, PreviewRequestHeader, PreviewTarget, PreviewValue,
};
use braid_ferrule_host::sandbox::{self, Limits};
use serde_json::json;
use sha2::{Digest, Sha256};

const HELPER: &str = "BRAID_SANDBOX_QUALIFICATION_HELPER";
const WORKER: &str = env!("CARGO_BIN_EXE_braid-ferrule-host");
const LIMITS: Limits = Limits {
    cpu_seconds: 10,
    memory_bytes: 268_435_456,
    open_files: 64,
};

fn main() {
    support::close_inherited_descriptors_on_exec();
    if let Ok(scenario) = std::env::var(HELPER) {
        helper(&scenario);
        return;
    }
    arguments_have_one_exact_shape();
    sandboxed_worker_reports_every_control_before_input();
    sandboxed_worker_answers_with_container_mode_bytes();
    refused_arguments_never_read_input();
    inherited_descriptor_refuses_the_sandbox();
    filters_deny_network_processes_files_and_foreign_signals();
    address_space_limit_refuses_large_reservations();
    cpu_limit_terminates_a_spinning_worker();
    println!("sandbox qualification: all scenarios passed");
}

fn arguments_have_one_exact_shape() {
    let parse = |values: &[&str]| {
        sandbox::parse_arguments(&values.iter().map(OsString::from).collect::<Vec<_>>())
    };
    assert_eq!(parse(&[]).unwrap(), None);
    assert_eq!(
        parse(&[
            sandbox::FLAG,
            "--cpu-seconds=10",
            "--memory-bytes=268435456",
            "--open-files=64"
        ])
        .unwrap(),
        Some(LIMITS)
    );
    for refused in [
        &[
            "--cpu-seconds=10",
            "--memory-bytes=268435456",
            "--open-files=64",
        ][..],
        &[sandbox::FLAG],
        &[
            sandbox::FLAG,
            "--memory-bytes=268435456",
            "--cpu-seconds=10",
            "--open-files=64",
        ],
        &[
            sandbox::FLAG,
            "--cpu-seconds=010",
            "--memory-bytes=268435456",
            "--open-files=64",
        ],
        &[
            sandbox::FLAG,
            "--cpu-seconds=+10",
            "--memory-bytes=268435456",
            "--open-files=64",
        ],
        &[
            sandbox::FLAG,
            "--cpu-seconds=0",
            "--memory-bytes=268435456",
            "--open-files=64",
        ],
        &[
            sandbox::FLAG,
            "--cpu-seconds=601",
            "--memory-bytes=268435456",
            "--open-files=64",
        ],
        &[
            sandbox::FLAG,
            "--cpu-seconds=10",
            "--memory-bytes=1024",
            "--open-files=64",
        ],
        &[
            sandbox::FLAG,
            "--cpu-seconds=10",
            "--memory-bytes=268435456",
            "--open-files=4",
        ],
        &[
            sandbox::FLAG,
            "--cpu-seconds=10",
            "--memory-bytes=268435456",
            "--open-files=64",
            "extra",
        ],
        &["inspect"],
    ] {
        assert!(parse(refused).is_err(), "{refused:?} must be refused");
    }
}

fn worker_command(arguments: &[String]) -> Command {
    let mut command = Command::new(WORKER);
    command
        .args(arguments)
        .env_clear()
        .current_dir(private_directory())
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped());
    command
}

fn sandbox_arguments(limits: Limits) -> Vec<String> {
    vec![
        sandbox::FLAG.into(),
        format!("--cpu-seconds={}", limits.cpu_seconds),
        format!("--memory-bytes={}", limits.memory_bytes),
        format!("--open-files={}", limits.open_files),
    ]
}

fn private_directory() -> PathBuf {
    let directory = std::env::temp_dir().join(format!(
        "braid-sandbox-{}-{}",
        std::process::id(),
        random_suffix()
    ));
    std::fs::create_dir(&directory).unwrap();
    directory
}

fn random_suffix() -> String {
    let mut bytes = [0u8; 8];
    std::fs::File::open("/dev/urandom")
        .unwrap()
        .read_exact(&mut bytes)
        .unwrap();
    bytes.iter().map(|byte| format!("{byte:02x}")).collect()
}

fn sandboxed_worker_reports_every_control_before_input() {
    let mut worker = worker_command(&sandbox_arguments(LIMITS)).spawn().unwrap();
    let mut marker = [0u8; 4];
    worker
        .stdout
        .as_mut()
        .unwrap()
        .read_exact(&mut marker)
        .unwrap();
    assert_eq!(marker, sandbox::READY_MARKER);
    let pid = worker.id();
    let status = std::fs::read_to_string(format!("/proc/{pid}/status")).unwrap();
    assert!(
        status.lines().any(|line| line == "NoNewPrivs:\t1"),
        "{status}"
    );
    assert!(status.lines().any(|line| line == "Seccomp:\t2"), "{status}");
    let inherited_filters = std::fs::read_to_string("/proc/self/status")
        .unwrap()
        .lines()
        .find_map(|line| line.strip_prefix("Seccomp_filters:\t"))
        .unwrap()
        .parse::<u32>()
        .unwrap();
    let expected_filters = format!("Seccomp_filters:\t{}", inherited_filters + 3);
    assert!(
        status.lines().any(|line| line == expected_filters),
        "{status}"
    );
    let limits = std::fs::read_to_string(format!("/proc/{pid}/limits")).unwrap();
    for (name, value) in [
        ("Max cpu time", "10"),
        ("Max file size", "0"),
        ("Max core file size", "0"),
        ("Max processes", "1"),
        ("Max open files", "64"),
        ("Max locked memory", "0"),
        ("Max address space", "268435456"),
        ("Max file locks", "0"),
        ("Max msgqueue size", "0"),
        ("Max realtime priority", "0"),
    ] {
        let line = limits.lines().find(|line| line.starts_with(name)).unwrap();
        let fields = line[name.len()..].split_whitespace().collect::<Vec<_>>();
        assert_eq!(&fields[..2], &[value, value], "{line}");
    }
    // Non-dumpable: even the same user cannot read its environment or memory maps.
    assert_eq!(
        std::fs::read(format!("/proc/{pid}/environ"))
            .unwrap_err()
            .kind(),
        std::io::ErrorKind::PermissionDenied
    );
    assert!(std::fs::read(format!("/proc/{pid}/maps")).is_err());
    drop(worker.stdin.take());
    let output = worker.wait_with_output().unwrap();
    // Closing input without a frame yields one bounded failure response.
    assert!(output.status.success());
    assert!(output.stderr.is_empty());
}

fn inspection_frame(operation: &str, native: &[u8]) -> Vec<u8> {
    let header = json!({
        "protocolVersion": "braid.ferrule-host.v1",
        "requestId": "11111111-1111-4111-8111-111111111111",
        "operation": operation,
        "revision": revision(),
        "nativeDigest": digest(native),
        "documentId": "entry.native.json",
        "limits": { "maximumDiagnostics": 128, "maximumItems": 8192, "maximumTextBytes": 2048 }
    });
    frames(&[&serde_json::to_vec(&header).unwrap(), native])
}

fn preview_frame(native: &[u8], primary: &[u8], rates: &[u8]) -> Vec<u8> {
    let header = PreviewRequestHeader {
        protocol_version: PREVIEW_PROTOCOL_VERSION.into(),
        request_id: "11111111-1111-4111-8111-111111111111".into(),
        revision: serde_json::from_value(revision()).unwrap(),
        native_digest: digest(native),
        document_id: "entry.native.json".into(),
        target: PreviewTarget::All,
        current_date_time: "2026-10-20T01:02:03.5+02:00".into(),
        parameters: vec![
            PreviewParameter {
                name: "region".into(),
                value: PreviewValue::String("west".into()),
            },
            PreviewParameter {
                name: "express".into(),
                value: PreviewValue::Boolean(true),
            },
        ],
        inputs: vec![
            PreviewInput::Primary {
                byte_length: primary.len() as u32,
                sha256: digest(primary),
            },
            PreviewInput::Named {
                name: "rates".into(),
                byte_length: rates.len() as u32,
                sha256: digest(rates),
            },
        ],
        limits: PreviewLimits {
            maximum_input_bytes: MAX_PREVIEW_INPUT_BYTES as u32,
            maximum_output_bytes: MAX_PREVIEW_OUTPUT_BYTES as u32,
            maximum_artifacts: 64,
            maximum_text_bytes: 2048,
        },
    };
    frames(&[
        &serde_json::to_vec(&header).unwrap(),
        native,
        primary,
        rates,
    ])
}

fn revision() -> serde_json::Value {
    json!({
        "organizationId": "22222222-2222-4222-8222-222222222222",
        "projectId": "33333333-3333-4333-8333-333333333333",
        "mappingId": "44444444-4444-4444-8444-444444444444",
        "revisionId": "55555555-5555-4555-8555-555555555555"
    })
}

fn frames(parts: &[&[u8]]) -> Vec<u8> {
    let mut bytes = Vec::new();
    for part in parts {
        bytes.extend_from_slice(&(part.len() as u32).to_be_bytes());
        bytes.extend_from_slice(part);
    }
    bytes
}

fn digest(bytes: &[u8]) -> String {
    format!("{:x}", Sha256::digest(bytes))
}

fn run(arguments: &[String], request: &[u8]) -> Vec<u8> {
    let mut worker = worker_command(arguments).spawn().unwrap();
    worker.stdin.take().unwrap().write_all(request).unwrap();
    let output = worker.wait_with_output().unwrap();
    assert!(
        output.status.success(),
        "{:?} {}",
        output.status,
        String::from_utf8_lossy(&output.stderr)
    );
    assert!(output.stderr.is_empty());
    output.stdout
}

fn sandboxed_worker_answers_with_container_mode_bytes() {
    let fixtures = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("tests/fixtures");
    let read = |name: &str| std::fs::read(fixtures.join(name)).unwrap();
    let preview = read("preview-orders.json");
    let order =
        br#"{"id":"SO-1","lines":[{"sku":"A","qty":2},{"sku":"B","qty":9007199254740993}]}"#;
    let mut requests = Vec::new();
    let mut fixtures = std::fs::read_dir(&fixtures)
        .unwrap()
        .map(|entry| entry.unwrap().path())
        .filter(|path| {
            path.extension()
                .is_some_and(|extension| extension == "json")
        })
        .collect::<Vec<_>>();
    fixtures.sort();
    assert!(fixtures.len() >= 13);
    for fixture in &fixtures {
        let native = std::fs::read(fixture).unwrap();
        for operation in ["inspect", "validate", "project"] {
            requests.push(inspection_frame(operation, &native));
        }
    }
    requests.push(preview_frame(&preview, order, br#"{"currency":"EUR"}"#));
    let mut prefixed = sandbox::READY_MARKER.to_vec();
    for request in requests {
        let container = run(&[], &request);
        let service = run(&sandbox_arguments(LIMITS), &request);
        assert!(service.starts_with(&sandbox::READY_MARKER));
        prefixed.truncate(sandbox::READY_MARKER.len());
        prefixed.extend_from_slice(&container);
        assert_eq!(
            service, prefixed,
            "sandboxed output must equal container-mode output after the marker"
        );
    }
}

fn refused_arguments_never_read_input() {
    for arguments in [
        vec!["--service-sandbox".to_string()],
        vec!["inspect".to_string()],
    ] {
        let mut worker = worker_command(&arguments).spawn().unwrap();
        // The worker exits without reading; a write may race with its exit.
        let _ = worker
            .stdin
            .take()
            .unwrap()
            .write_all(&inspection_frame("inspect", b"{}"));
        let output = worker.wait_with_output().unwrap();
        assert!(!output.status.success());
        assert_eq!(
            output.status.code(),
            Some(i32::from(sandbox::SandboxError::Arguments.exit_code()))
        );
        assert!(output.stdout.is_empty());
        assert!(output.stderr.is_empty());
    }
}

fn helper_command(scenario: &str) -> Command {
    let mut command = Command::new(std::env::current_exe().unwrap());
    command
        .env_clear()
        .env(HELPER, scenario)
        .current_dir(private_directory())
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped());
    command
}

fn helper_output(scenario: &str) -> String {
    let output = helper_command(scenario).output().unwrap();
    assert!(
        output.status.success(),
        "{scenario}: {:?} {}",
        output.status,
        String::from_utf8_lossy(&output.stderr)
    );
    String::from_utf8(output.stdout).unwrap()
}

fn inherited_descriptor_refuses_the_sandbox() {
    assert_eq!(helper_output("leak-descriptor").trim(), "refused");
}

fn filters_deny_network_processes_files_and_foreign_signals() {
    let report = helper_output("probe");
    let expected = [
        "tcp-bind=PermissionDenied",
        "udp-bind=PermissionDenied",
        "open=NotFound",
        "create-directory=ReadOnlyFilesystem",
        "spawn-process=PermissionDenied",
        "spawn-thread=refused",
        "signal-parent=EPERM",
        "signal-init=EPERM",
        "signal-group=EPERM",
        "signal-self=ok",
        "write-standard-output=ok",
    ];
    assert_eq!(report.lines().collect::<Vec<_>>(), expected);
}

fn address_space_limit_refuses_large_reservations() {
    assert_eq!(helper_output("reserve").trim(), "small=ok large=refused");
}

fn cpu_limit_terminates_a_spinning_worker() {
    let started = Instant::now();
    let mut child = helper_command("spin").spawn().unwrap();
    let status = loop {
        if let Some(status) = child.try_wait().unwrap() {
            break status;
        }
        assert!(
            started.elapsed() < Duration::from_secs(20),
            "the CPU limit did not terminate the worker"
        );
        std::thread::sleep(Duration::from_millis(50));
    };
    assert!(
        matches!(status.signal(), Some(libc::SIGXCPU | libc::SIGKILL)),
        "unexpected termination: {status:?}"
    );
}

fn helper(scenario: &str) {
    match scenario {
        "leak-descriptor" => {
            // An inherited descriptor without close-on-exec must stop the worker before its marker.
            let leaked = rustix::io::dup(std::io::stdout()).unwrap();
            let mut worker = Command::new(WORKER)
                .args(sandbox_arguments(LIMITS))
                .env_clear()
                .stdin(Stdio::piped())
                .stdout(Stdio::piped())
                .stderr(Stdio::piped())
                .spawn()
                .unwrap();
            drop(worker.stdin.take());
            let output = worker.wait_with_output().unwrap();
            drop(leaked);
            let refused = output.status.code()
                == Some(i32::from(sandbox::SandboxError::Descriptors.exit_code()))
                && output.stdout.is_empty()
                && output.stderr.is_empty();
            println!("{}", if refused { "refused" } else { "admitted" });
        }
        "probe" => {
            // Report through a buffer: the standard output pipe stays usable under the filter.
            let parent = rustix::process::getppid().unwrap();
            sandbox::enter(&LIMITS).unwrap();
            let mut report = String::new();
            let kind = |result: std::io::Result<()>| match result {
                Ok(()) => "ok".to_string(),
                Err(error) => format!("{:?}", error.kind()),
            };
            report += &format!(
                "tcp-bind={}\n",
                kind(std::net::TcpListener::bind("127.0.0.1:0").map(drop))
            );
            report += &format!(
                "udp-bind={}\n",
                kind(std::net::UdpSocket::bind("127.0.0.1:0").map(drop))
            );
            report += &format!(
                "open={}\n",
                kind(std::fs::File::open("/proc/self/status").map(drop))
            );
            report += &format!(
                "create-directory={}\n",
                kind(std::fs::create_dir("created"))
            );
            report += &format!(
                "spawn-process={}\n",
                // Pipes only: opening /dev/null would already be refused as a missing file.
                kind(
                    Command::new(WORKER)
                        .stdin(Stdio::piped())
                        .stdout(Stdio::piped())
                        .spawn()
                        .map(drop)
                )
            );
            let thread = std::thread::Builder::new().spawn(|| ());
            report += &format!(
                "spawn-thread={}\n",
                if thread.is_err() {
                    "refused"
                } else {
                    "created"
                }
            );
            let signal = |target: Option<rustix::process::Pid>, group: bool| {
                let result = match (target, group) {
                    (Some(pid), false) => rustix::process::test_kill_process(pid),
                    (Some(pid), true) => rustix::process::test_kill_process_group(pid),
                    (None, _) => rustix::process::test_kill_current_process_group(),
                };
                match result {
                    Ok(()) => "ok".to_string(),
                    Err(error) if error == rustix::io::Errno::PERM => "EPERM".to_string(),
                    Err(error) => format!("{error:?}"),
                }
            };
            report += &format!("signal-parent={}\n", signal(Some(parent), false));
            report += &format!(
                "signal-init={}\n",
                signal(rustix::process::Pid::from_raw(1), false)
            );
            report += &format!("signal-group={}\n", signal(None, true));
            report += &format!(
                "signal-self={}\n",
                signal(Some(rustix::process::getpid()), false)
            );
            report += "write-standard-output=ok\n";
            std::io::stdout().write_all(report.as_bytes()).unwrap();
        }
        "reserve" => {
            sandbox::enter(&LIMITS).unwrap();
            let mut small = Vec::<u8>::new();
            let small_result = small.try_reserve_exact(16 << 20);
            let mut large = Vec::<u8>::new();
            let large_result = large.try_reserve_exact(512 << 20);
            println!(
                "small={} large={}",
                if small_result.is_ok() {
                    "ok"
                } else {
                    "refused"
                },
                if large_result.is_err() {
                    "refused"
                } else {
                    "admitted"
                }
            );
        }
        "spin" => {
            sandbox::enter(&Limits {
                cpu_seconds: 1,
                ..LIMITS
            })
            .unwrap();
            let mut value = 0u64;
            loop {
                value = std::hint::black_box(value.wrapping_add(1));
            }
        }
        other => panic!("unknown helper scenario {other}"),
    }
}
