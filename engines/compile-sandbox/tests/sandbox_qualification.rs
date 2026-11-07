//! Compile-sandbox qualification. This target has no libtest harness: each launch runs the real launcher, which
//! executes a fresh copy of this executable as its program, so every control is observed after `execve`. Launches
//! can also simulate an older kernel's Landlock ABI, so the controls that must hold without Landlock's signal and
//! abstract-socket scopes (ABI 3 to 5) are qualified on any kernel.

use std::ffi::{CString, OsString};
use std::io::{BufRead, BufReader, Read, Write};
use std::os::fd::{AsRawFd, FromRawFd, OwnedFd, RawFd};
use std::os::unix::process::{CommandExt, ExitStatusExt};
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::time::{Duration, Instant};

use weft_compile_sandbox::{self as sandbox, FILTERS, GO, MINIMUM_LANDLOCK_ABI, READY_MARKER};

const HELPER: &str = "WEFT_COMPILE_SANDBOX_QUALIFICATION_HELPER";
const VICTIM: &str = "WEFT_COMPILE_SANDBOX_QUALIFICATION_VICTIM";
const LAUNCHER: &str = env!("CARGO_BIN_EXE_weft-compile-sandbox");
const MEMORY_BYTES: u64 = 1 << 30;
const FILE_BYTES: u64 = 1 << 20;
/// The kernel's own Landlock ABI, then every admitted ABI before 6, the first that scopes signals and abstract unix
/// sockets to the sandbox.
const ABIS: [Option<i64>; 4] = [None, Some(5), Some(4), Some(MINIMUM_LANDLOCK_ABI)];

fn main() {
    close_inherited_descriptors_on_exec();
    if let Ok(scenario) = std::env::var(HELPER) {
        helper(&scenario);
        return;
    }
    arguments_have_one_exact_shape();
    launcher_reports_every_control_before_the_program();
    program_runs_confined_after_execve();
    other_processes_are_unreachable_at_every_landlock_abi();
    without_an_output_grant_nothing_is_writable();
    anything_but_go_never_starts_the_program();
    inherited_descriptor_refuses_the_launch();
    missing_grant_refuses_the_launch();
    address_space_limit_refuses_large_reservations();
    cpu_limit_terminates_a_spinning_program();
    file_size_limit_stops_the_output_growing();
    println!("compile sandbox qualification: all scenarios passed");
}

fn arguments_have_one_exact_shape() {
    let parse = |values: &[&str]| {
        sandbox::parse_arguments(&values.iter().map(OsString::from).collect::<Vec<_>>())
    };
    let accepted = parse(&[
        "--cpu-seconds=10",
        "--memory-bytes=1073741824",
        "--open-files=64",
        "--file-bytes=1048576",
        "--output=out.dll",
        "--read=/usr/share/dotnet",
        "--read=/dev/urandom",
        "--execute=/lib64/ld-linux-x86-64.so.2",
        "--",
        "/usr/share/dotnet/dotnet",
        "exec",
        "@compiler.rsp",
    ])
    .unwrap();
    assert_eq!(accepted.cpu_seconds, 10);
    assert_eq!(accepted.memory_bytes, 1 << 30);
    assert_eq!(accepted.open_files, 64);
    assert_eq!(accepted.file_bytes, 1 << 20);
    assert_eq!(accepted.output, Some(CString::new("out.dll").unwrap()));
    assert_eq!(accepted.read.len(), 2);
    assert_eq!(accepted.execute.len(), 1);
    assert_eq!(accepted.command.len(), 3);
    // Without an output grant nothing is writable.
    let unwritable = parse(&[
        "--cpu-seconds=10",
        "--memory-bytes=1073741824",
        "--open-files=64",
        "--file-bytes=1048576",
        "--read=/usr/share/dotnet",
        "--",
        "/usr/share/dotnet/dotnet",
    ])
    .unwrap();
    assert_eq!(unwritable.output, None);
    assert_eq!(unwritable.read.len(), 1);
    let base = [
        "--cpu-seconds=10",
        "--memory-bytes=1073741824",
        "--open-files=64",
        "--file-bytes=1048576",
        "--output=out.dll",
    ];
    let with = |index: usize, value: &'static str| {
        let mut values = base.to_vec();
        values[index] = value;
        values.extend(["--", "/usr/bin/program"]);
        values
    };
    for refused in [
        with(0, "--cpu-seconds=010"),
        with(0, "--cpu-seconds=+10"),
        with(0, "--cpu-seconds=0"),
        with(0, "--cpu-seconds=601"),
        with(1, "--memory-bytes=1024"),
        with(2, "--open-files=4"),
        with(3, "--file-bytes=1"),
        with(4, "--output=dir/out.dll"),
        with(4, "--output=.."),
        with(4, "--output="),
        with(1, "--cpu-seconds=10"),
        base.to_vec(),
        [&base[..], &["--"]].concat(),
        [&base[..], &["--", "relative"]].concat(),
        [&base[..], &["--read=relative", "--", "/usr/bin/program"]].concat(),
        [&base[..], &["--read=/usr/../etc", "--", "/usr/bin/program"]].concat(),
        [&base[..], &["--read=/usr//lib", "--", "/usr/bin/program"]].concat(),
        [&base[..], &["--read=/usr/lib/", "--", "/usr/bin/program"]].concat(),
        [&base[..], &["--read=/", "--", "/usr/bin/program"]].concat(),
        [
            &base[..],
            &["--execute=/a", "--read=/b", "--", "/usr/bin/program"],
        ]
        .concat(),
        [&base[..], &["--write=/tmp", "--", "/usr/bin/program"]].concat(),
        [&base[1..], &["--", "/usr/bin/program"]].concat(),
        [
            &base[..4],
            &[
                "--read=/usr/lib",
                "--output=out.dll",
                "--",
                "/usr/bin/program",
            ],
        ]
        .concat(),
    ] {
        assert!(parse(&refused).is_err(), "{refused:?} must be refused");
    }
}

/// A private directory with one input and the pre-created output, beside a file the program must never read.
/// Everything is removed when it is dropped.
struct Private(PathBuf);

impl std::ops::Deref for Private {
    type Target = Path;

    fn deref(&self) -> &Path {
        &self.0
    }
}

impl Drop for Private {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(self.0.parent().unwrap());
    }
}

fn private_directory() -> Private {
    let parent = std::env::temp_dir().join(format!(
        "weft-compile-sandbox-{}-{}",
        std::process::id(),
        random_suffix()
    ));
    std::fs::create_dir(&parent).unwrap();
    std::fs::write(parent.join("secret"), b"secret").unwrap();
    let directory = parent.join("operation");
    std::fs::create_dir(&directory).unwrap();
    std::fs::write(directory.join("input.cs"), b"class Input {}").unwrap();
    std::fs::write(directory.join("output.dll"), b"").unwrap();
    let grant = parent.join("toolchain");
    std::fs::create_dir(&grant).unwrap();
    std::fs::write(grant.join("reference.dll"), b"reference").unwrap();
    Private(directory)
}

fn random_suffix() -> String {
    let mut bytes = [0u8; 8];
    std::fs::File::open("/dev/urandom")
        .unwrap()
        .read_exact(&mut bytes)
        .unwrap();
    bytes.iter().map(|byte| format!("{byte:02x}")).collect()
}

/// The directories of every file this executable has mapped (its shared libraries), so the copy the launcher
/// executes can load, plus the ELF interpreter, which `execve` opens for execution.
fn runtime_grants() -> (Vec<String>, String) {
    let maps = std::fs::read_to_string("/proc/self/maps").unwrap();
    let executable = std::env::current_exe().unwrap();
    let mut directories = maps
        .lines()
        .filter_map(|line| line.split_whitespace().nth(5))
        .filter(|path| path.starts_with('/') && Path::new(path) != executable)
        .filter_map(|path| {
            Path::new(path)
                .parent()
                .map(|parent| parent.display().to_string())
        })
        .collect::<Vec<_>>();
    directories.sort();
    directories.dedup();
    (directories, interpreter(&executable))
}

/// The PT_INTERP path of a 64-bit little-endian ELF executable.
fn interpreter(executable: &Path) -> String {
    let bytes = std::fs::read(executable).unwrap();
    let word = |offset: usize, length: usize| {
        let mut value = 0u64;
        for (index, byte) in bytes[offset..offset + length].iter().enumerate() {
            value |= u64::from(*byte) << (8 * index);
        }
        value as usize
    };
    let (table, size, count) = (word(0x20, 8), word(0x36, 2), word(0x38, 2));
    for index in 0..count {
        let header = table + index * size;
        if word(header, 4) == 3 {
            let (offset, length) = (word(header + 8, 8), word(header + 32, 8));
            let path = &bytes[offset..offset + length];
            return String::from_utf8(path[..path.len() - 1].to_vec()).unwrap();
        }
    }
    panic!("the test executable has no ELF interpreter");
}

fn launch_arguments(cpu_seconds: u64, memory_bytes: u64, extra: &[String]) -> Vec<String> {
    let (directories, interpreter) = runtime_grants();
    let mut arguments = vec![
        format!("--cpu-seconds={cpu_seconds}"),
        format!("--memory-bytes={memory_bytes}"),
        "--open-files=64".to_string(),
        format!("--file-bytes={FILE_BYTES}"),
        "--output=output.dll".to_string(),
    ];
    arguments.extend(
        directories
            .into_iter()
            .map(|directory| format!("--read={directory}")),
    );
    arguments.extend(extra.iter().cloned());
    arguments.push(format!("--execute={interpreter}"));
    arguments.push("--".to_string());
    arguments.push(std::env::current_exe().unwrap().display().to_string());
    arguments
}

fn launcher(scenario: &str, directory: &Path, arguments: &[String]) -> Child {
    launcher_at(None, scenario, directory, arguments, &[])
}

/// Starts the launcher on this kernel, or, given an older Landlock ABI, as on a kernel that reports it. A
/// user-notification filter installed just before `execve` answers the launcher's version query, so it applies
/// exactly that ABI's controls; this kernel still creates and enforces its ruleset.
fn launcher_at(
    abi: Option<i64>,
    scenario: &str,
    directory: &Path,
    arguments: &[String],
    environment: &[(&str, String)],
) -> Child {
    let mut command = Command::new(LAUNCHER);
    command
        .args(arguments)
        .env_clear()
        .env(HELPER, scenario)
        .envs(environment.iter().map(|(name, value)| (name, value)))
        .current_dir(directory)
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped());
    let Some(abi) = abi else {
        return command.spawn().unwrap();
    };
    let mut pair = [0; 2];
    assert_eq!(
        unsafe {
            libc::socketpair(
                libc::AF_UNIX,
                libc::SOCK_SEQPACKET | libc::SOCK_CLOEXEC,
                0,
                pair.as_mut_ptr(),
            )
        },
        0
    );
    let (supervisor, sender) =
        unsafe { (OwnedFd::from_raw_fd(pair[0]), OwnedFd::from_raw_fd(pair[1])) };
    let program = version_query_filter();
    let sending = sender.as_raw_fd();
    // Runs between fork and execve: system calls only, no allocation.
    unsafe {
        command.pre_exec(move || install_version_query_filter(&program, sending));
    }
    let child = command.spawn().unwrap();
    drop(sender);
    let listener = receive_descriptor(&supervisor);
    answer_version_query(&listener, abi);
    child
}

/// Notifies only `landlock_create_ruleset(NULL, 0, LANDLOCK_CREATE_RULESET_VERSION)`; everything else proceeds.
fn version_query_filter() -> Vec<libc::sock_filter> {
    const AUDIT_ARCH_X86_64: u32 = 0xc000_003e;
    let statement = |code: u16, jt: u8, jf: u8, k: u32| libc::sock_filter { code, jt, jf, k };
    vec![
        // Architecture, system call number and the low word of the third argument.
        statement(0x20, 0, 0, 4),
        statement(0x15, 0, 5, AUDIT_ARCH_X86_64),
        statement(0x20, 0, 0, 0),
        statement(0x15, 0, 3, libc::SYS_landlock_create_ruleset as u32),
        statement(0x20, 0, 0, 32),
        statement(0x15, 0, 1, 1),
        statement(0x06, 0, 0, libc::SECCOMP_RET_USER_NOTIF),
        statement(0x06, 0, 0, libc::SECCOMP_RET_ALLOW),
    ]
}

fn install_version_query_filter(
    program: &[libc::sock_filter],
    sender: RawFd,
) -> std::io::Result<()> {
    let failed = || Err(std::io::Error::last_os_error());
    if unsafe { libc::prctl(libc::PR_SET_NO_NEW_PRIVS, 1, 0, 0, 0) } != 0 {
        return failed();
    }
    let filter = libc::sock_fprog {
        len: program.len() as u16,
        filter: program.as_ptr().cast_mut(),
    };
    let listener = unsafe {
        libc::syscall(
            libc::SYS_seccomp,
            libc::SECCOMP_SET_MODE_FILTER,
            libc::SECCOMP_FILTER_FLAG_NEW_LISTENER,
            &raw const filter,
        )
    };
    if listener < 0 {
        return failed();
    }
    let listener = listener as RawFd;
    let mut byte = [0u8];
    let mut vector = libc::iovec {
        iov_base: byte.as_mut_ptr().cast(),
        iov_len: 1,
    };
    let mut control = [0u64; 4];
    let mut message: libc::msghdr = unsafe { std::mem::zeroed() };
    message.msg_iov = &raw mut vector;
    message.msg_iovlen = 1;
    message.msg_control = control.as_mut_ptr().cast();
    message.msg_controllen = unsafe { libc::CMSG_SPACE(size_of::<RawFd>() as u32) } as usize;
    unsafe {
        let header = libc::CMSG_FIRSTHDR(&raw const message);
        (*header).cmsg_level = libc::SOL_SOCKET;
        (*header).cmsg_type = libc::SCM_RIGHTS;
        (*header).cmsg_len = libc::CMSG_LEN(size_of::<RawFd>() as u32) as usize;
        libc::CMSG_DATA(header)
            .cast::<RawFd>()
            .write_unaligned(listener);
    }
    let sent = unsafe { libc::sendmsg(sender, &raw const message, 0) };
    // The launcher must inherit nothing beyond the standard streams; the message holds its own reference.
    unsafe { libc::close(listener) };
    if sent != 1 {
        return failed();
    }
    Ok(())
}

fn receive_descriptor(socket: &OwnedFd) -> OwnedFd {
    let mut byte = [0u8];
    let mut vector = libc::iovec {
        iov_base: byte.as_mut_ptr().cast(),
        iov_len: 1,
    };
    let mut control = [0u64; 4];
    let mut message: libc::msghdr = unsafe { std::mem::zeroed() };
    message.msg_iov = &raw mut vector;
    message.msg_iovlen = 1;
    message.msg_control = control.as_mut_ptr().cast();
    message.msg_controllen = size_of_val(&control);
    assert_eq!(
        unsafe { libc::recvmsg(socket.as_raw_fd(), &raw mut message, libc::MSG_CMSG_CLOEXEC) },
        1
    );
    unsafe {
        let header = libc::CMSG_FIRSTHDR(&raw const message);
        assert!(!header.is_null() && (*header).cmsg_type == libc::SCM_RIGHTS);
        OwnedFd::from_raw_fd(libc::CMSG_DATA(header).cast::<RawFd>().read_unaligned())
    }
}

/// Answers the launcher's one version query with `abi`, then closes the listener.
fn answer_version_query(listener: &OwnedFd, abi: i64) {
    const SECCOMP_IOCTL_NOTIF_RECV: libc::c_ulong = 0xc050_2100;
    const SECCOMP_IOCTL_NOTIF_SEND: libc::c_ulong = 0xc018_2101;
    let mut ready = libc::pollfd {
        fd: listener.as_raw_fd(),
        events: libc::POLLIN,
        revents: 0,
    };
    assert_eq!(
        unsafe { libc::poll(&raw mut ready, 1, 30_000) },
        1,
        "the launcher never asked for its Landlock ABI"
    );
    let mut notification: libc::seccomp_notif = unsafe { std::mem::zeroed() };
    assert_eq!(
        unsafe {
            libc::ioctl(
                listener.as_raw_fd(),
                SECCOMP_IOCTL_NOTIF_RECV,
                &raw mut notification,
            )
        },
        0
    );
    assert_eq!(
        i64::from(notification.data.nr),
        libc::SYS_landlock_create_ruleset
    );
    assert_eq!(notification.data.args[2], 1);
    let response = libc::seccomp_notif_resp {
        id: notification.id,
        val: abi,
        error: 0,
        flags: 0,
    };
    assert_eq!(
        unsafe {
            libc::ioctl(
                listener.as_raw_fd(),
                SECCOMP_IOCTL_NOTIF_SEND,
                &raw const response,
            )
        },
        0
    );
}

/// Gives launches only the descriptors each scenario passes, as the service does. A harness such as a CI runner can
/// leave descriptors open in this process without close-on-exec; the launcher correctly refuses to start with one.
fn close_inherited_descriptors_on_exec() {
    let descriptors: Vec<RawFd> = std::fs::read_dir("/proc/self/fd")
        .unwrap()
        .filter_map(|entry| entry.ok()?.file_name().to_str()?.parse().ok())
        .filter(|&descriptor| descriptor > 2)
        .collect();
    for descriptor in descriptors {
        // Only descriptor flags change; one already closed (such as the listing's own) fails with EBADF.
        let flags = unsafe { libc::fcntl(descriptor, libc::F_GETFD) };
        if flags >= 0 {
            unsafe { libc::fcntl(descriptor, libc::F_SETFD, flags | libc::FD_CLOEXEC) };
        }
    }
}

fn read_marker(child: &mut Child) -> [u8; 4] {
    let mut marker = [0u8; 4];
    child
        .stdout
        .as_mut()
        .unwrap()
        .read_exact(&mut marker)
        .unwrap();
    marker
}

fn go(child: &mut Child) {
    let mut input = child.stdin.take().unwrap();
    input.write_all(&[GO]).unwrap();
}

fn grant(directory: &Path) -> String {
    format!(
        "--read={}",
        directory.parent().unwrap().join("toolchain").display()
    )
}

fn launcher_reports_every_control_before_the_program() {
    let directory = private_directory();
    let mut child = launcher(
        "report",
        &directory,
        &launch_arguments(10, MEMORY_BYTES, &[grant(&directory)]),
    );
    assert_eq!(read_marker(&mut child), READY_MARKER);
    let pid = child.id();
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
        .parse::<usize>()
        .unwrap();
    let expected_filters = format!("Seccomp_filters:\t{}", inherited_filters + FILTERS);
    assert!(
        status.lines().any(|line| line == expected_filters),
        "{status}"
    );
    let limits = std::fs::read_to_string(format!("/proc/{pid}/limits")).unwrap();
    for (name, value) in [
        ("Max cpu time", "10".to_string()),
        ("Max file size", FILE_BYTES.to_string()),
        ("Max core file size", "0".to_string()),
        ("Max open files", "64".to_string()),
        ("Max locked memory", "0".to_string()),
        ("Max address space", MEMORY_BYTES.to_string()),
        ("Max file locks", "0".to_string()),
        ("Max msgqueue size", "0".to_string()),
        ("Max realtime priority", "0".to_string()),
    ] {
        let line = limits.lines().find(|line| line.starts_with(name)).unwrap();
        let fields = line[name.len()..].split_whitespace().collect::<Vec<_>>();
        assert_eq!(&fields[..2], &[value.as_str(), value.as_str()], "{line}");
    }
    // Non-dumpable before the program starts: even the same user cannot read its environment.
    assert_eq!(
        std::fs::read(format!("/proc/{pid}/environ"))
            .unwrap_err()
            .kind(),
        std::io::ErrorKind::PermissionDenied
    );
    go(&mut child);
    let output = child.wait_with_output().unwrap();
    assert!(
        output.status.success(),
        "{:?} {}",
        output.status,
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(String::from_utf8(output.stdout).unwrap(), "reported\n");
}

fn program_runs_confined_after_execve() {
    for abi in ABIS {
        program_runs_confined_after_execve_at(abi);
    }
}

fn program_runs_confined_after_execve_at(abi: Option<i64>) {
    let directory = private_directory();
    let mut child = launcher_at(
        abi,
        "probe",
        &directory,
        &launch_arguments(10, MEMORY_BYTES, &[grant(&directory)]),
        &[],
    );
    assert_eq!(read_marker(&mut child), READY_MARKER);
    go(&mut child);
    let output = child.wait_with_output().unwrap();
    assert!(
        output.status.success(),
        "Landlock ABI {abi:?}: {:?} {}",
        output.status,
        String::from_utf8_lossy(&output.stderr)
    );
    let expected = [
        "tcp-bind=PermissionDenied",
        "udp-bind=PermissionDenied",
        "unix-bind=PermissionDenied",
        "spawn-process=PermissionDenied",
        "spawn-thread=created",
        "read-grant=ok",
        "read-input=ok",
        "read-own-process=ok",
        "write-output=ok",
        "write-input=PermissionDenied",
        "truncate-input=PermissionDenied",
        "remove-input=PermissionDenied",
        "create-file=PermissionDenied",
        "create-directory=PermissionDenied",
        "read-parent-file=PermissionDenied",
        "list-parent=PermissionDenied",
        "read-other-process=PermissionDenied",
        "execute-other=EACCES",
        "change-mode=EPERM",
        "change-read-grant-mode-by-fd=EPERM",
        "set-read-grant-attribute-at=EPERM",
        "remove-read-grant-attribute-at=EPERM",
        "set-read-grant-file-attributes=EPERM",
        "set-read-grant-flags-by-ioctl=EPERM",
        "set-read-grant-extended-flags-by-ioctl=EPERM",
        "other-process-limits=EPERM",
        "shared-memory=EPERM",
        "signal-parent=EPERM",
        "signal-init=EPERM",
        "signal-group=EPERM",
        "signal-self=ok",
    ];
    assert_eq!(
        String::from_utf8(output.stdout)
            .unwrap()
            .lines()
            .collect::<Vec<_>>(),
        expected,
        "Landlock ABI {abi:?}"
    );
    assert_eq!(
        std::fs::read(directory.join("output.dll")).unwrap(),
        b"compiled"
    );
    assert_eq!(
        std::fs::read(directory.join("input.cs")).unwrap(),
        b"class Input {}"
    );
}

/// Every route by which a process of the same user can signal, trace, read, reschedule or otherwise affect another
/// process, tried against the service (an ordinary process of this user) and against a sibling compiler in its
/// own sandbox. Before Landlock ABI 6 only the syscall filter refuses the signal routes, so each is tried at
/// every admitted ABI. The victims must survive, and the program's own signals must keep working.
fn other_processes_are_unreachable_at_every_landlock_abi() {
    let expected = [
        "set-owner=EPERM",
        "set-owner-ex=EPERM",
        "set-owner-signal=EPERM",
        "set-asynchronous=EPERM",
        "set-non-blocking=ok",
        "set-lease=EPERM",
        "ioctl-set-owner=EPERM",
        "ioctl-set-process-group=EPERM",
        "ioctl-set-asynchronous=EPERM",
        "ioctl-set-terminal-group=EPERM",
        "ioctl-inject-terminal-input=EPERM",
        "signal=EPERM",
        "signal-thread=EPERM",
        "signal-group-thread=EPERM",
        "queue-signal=EPERM",
        "queue-thread-signal=EPERM",
        "open-process=EPERM",
        "signal-process-descriptor=EPERM",
        "copy-descriptor=EPERM",
        "advise-memory=EPERM",
        "release-memory=EPERM",
        "trace=EPERM",
        "read-memory=EPERM",
        "write-memory=EPERM",
        "compare-resources=EPERM",
        "robust-list=EPERM",
        "open-memory=EACCES",
        "read-status=EACCES",
        "list-processes=EACCES",
        "priority=EPERM",
        "affinity=EPERM",
        "scheduler=EPERM",
        "scheduler-parameters=EPERM",
        "scheduler-attributes=EPERM",
        "io-priority=EPERM",
        "limits=EPERM",
        "migrate-pages=EPERM",
        "move-pages=EPERM",
        "performance-events=EPERM",
        "io-uring=EPERM",
        "userfaultfd=EPERM",
        "user-keyring=EPERM",
        "add-key=EPERM",
        "unix-socket=EPERM",
        "socket-pair=EPERM",
        "own-robust-list=ok",
        "read-own-thread=ok",
        "signal-self=ok",
        "signal-own-thread=ok",
        "signal-other-own-thread=ok",
        "raise-self=handled",
    ];
    let mut failures = Vec::new();
    for abi in ABIS {
        for sandboxed in [false, true] {
            let context = format!(
                "Landlock ABI {}, {} victim",
                abi.map_or("of this kernel".to_string(), |abi| format!(
                    "{abi} (simulated)"
                )),
                if sandboxed {
                    "sandboxed sibling"
                } else {
                    "service"
                }
            );
            let victim_directory = private_directory();
            let mut victim = if sandboxed {
                let mut child = launcher_at(
                    abi,
                    "victim",
                    &victim_directory,
                    &launch_arguments(10, MEMORY_BYTES, &[]),
                    &[],
                );
                assert_eq!(read_marker(&mut child), READY_MARKER, "{context}");
                child.stdin.as_mut().unwrap().write_all(&[GO]).unwrap();
                child
            } else {
                Command::new(std::env::current_exe().unwrap())
                    .env_clear()
                    .env(HELPER, "victim")
                    .stdin(Stdio::piped())
                    .stdout(Stdio::piped())
                    .stderr(Stdio::piped())
                    .spawn()
                    .unwrap()
            };
            let mut victim_output = BufReader::new(victim.stdout.take().unwrap());
            let mut ready = String::new();
            victim_output.read_line(&mut ready).unwrap();
            assert_eq!(ready, "ready\n", "{context}");

            let directory = private_directory();
            let mut child = launcher_at(
                abi,
                "cross-process",
                &directory,
                &launch_arguments(10, MEMORY_BYTES, &[grant(&directory)]),
                &[(VICTIM, victim.id().to_string())],
            );
            assert_eq!(read_marker(&mut child), READY_MARKER, "{context}");
            go(&mut child);
            let output = child.wait_with_output().unwrap();
            drop(victim.stdin.take());
            let mut survived = String::new();
            victim_output.read_to_string(&mut survived).unwrap();
            let status = victim.wait().unwrap();

            let report = String::from_utf8_lossy(&output.stdout);
            let lines = report.lines().collect::<Vec<_>>();
            if !output.status.success() || lines != expected {
                failures.push(format!(
                    "{context}: program {:?} {}\n{}",
                    output.status,
                    String::from_utf8_lossy(&output.stderr),
                    (0..lines.len().max(expected.len()))
                        .filter_map(|index| {
                            let (line, wanted) = (lines.get(index), expected.get(index));
                            (line != wanted).then(|| {
                                format!(
                                    "  {} (expected {})",
                                    line.unwrap_or(&"nothing"),
                                    wanted.unwrap_or(&"nothing")
                                )
                            })
                        })
                        .collect::<Vec<_>>()
                        .join("\n")
                ));
            }
            if !status.success() || survived != "alive\n" {
                failures.push(format!("{context}: the victim was reached: {status:?}"));
            }
        }
    }
    assert!(failures.is_empty(), "{}", failures.join("\n"));
}

fn without_an_output_grant_nothing_is_writable() {
    let directory = private_directory();
    let arguments = launch_arguments(10, MEMORY_BYTES, &[])
        .into_iter()
        .filter(|argument| !argument.starts_with("--output="))
        .collect::<Vec<_>>();
    let mut child = launcher("write-output", &directory, &arguments);
    assert_eq!(read_marker(&mut child), READY_MARKER);
    go(&mut child);
    let output = child.wait_with_output().unwrap();
    assert!(output.status.success(), "{output:?}");
    assert_eq!(
        String::from_utf8(output.stdout).unwrap().trim(),
        "PermissionDenied"
    );
    assert!(
        std::fs::read(directory.join("output.dll"))
            .unwrap()
            .is_empty()
    );
}

fn anything_but_go_never_starts_the_program() {
    for send in [Some(b'X'), None] {
        let directory = private_directory();
        let mut child = launcher(
            "report",
            &directory,
            &launch_arguments(10, MEMORY_BYTES, &[]),
        );
        assert_eq!(read_marker(&mut child), READY_MARKER);
        let mut input = child.stdin.take().unwrap();
        if let Some(byte) = send {
            input.write_all(&[byte]).unwrap();
        }
        drop(input);
        let output = child.wait_with_output().unwrap();
        assert!(!output.status.success());
        assert!(output.stdout.is_empty());
    }
}

fn inherited_descriptor_refuses_the_launch() {
    let directory = private_directory();
    let output = Command::new(std::env::current_exe().unwrap())
        .env_clear()
        .env(HELPER, "leak-descriptor")
        .current_dir(&*directory)
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(String::from_utf8(output.stdout).unwrap().trim(), "refused");
}

fn missing_grant_refuses_the_launch() {
    let directory = private_directory();
    std::fs::remove_file(directory.join("output.dll")).unwrap();
    for arguments in [
        launch_arguments(10, MEMORY_BYTES, &[]),
        launch_arguments(
            10,
            MEMORY_BYTES,
            &["--read=/nonexistent-weft-grant".to_string()],
        ),
    ] {
        let mut child = launcher("report", &directory, &arguments);
        drop(child.stdin.take());
        let output = child.wait_with_output().unwrap();
        assert!(!output.status.success());
        assert!(output.stdout.is_empty());
        std::fs::write(directory.join("output.dll"), b"").unwrap();
        assert_eq!(
            output.status.code(),
            Some(i32::from(sandbox::SandboxError::PathGrant.exit_code()))
        );
        assert!(output.stderr.is_empty());
    }
}

fn run_to_end(scenario: &str, cpu_seconds: u64, memory_bytes: u64) -> std::process::Output {
    let directory = private_directory();
    let mut child = launcher(
        scenario,
        &directory,
        &launch_arguments(cpu_seconds, memory_bytes, &[]),
    );
    assert_eq!(read_marker(&mut child), READY_MARKER);
    go(&mut child);
    let started = Instant::now();
    loop {
        if child.try_wait().unwrap().is_some() {
            break;
        }
        assert!(
            started.elapsed() < Duration::from_secs(30),
            "{scenario} did not end"
        );
        std::thread::sleep(Duration::from_millis(50));
    }
    child.wait_with_output().unwrap()
}

fn address_space_limit_refuses_large_reservations() {
    let output = run_to_end("reserve", 10, 256 << 20);
    assert!(output.status.success(), "{output:?}");
    assert_eq!(
        String::from_utf8(output.stdout).unwrap().trim(),
        "small=ok large=refused"
    );
}

fn cpu_limit_terminates_a_spinning_program() {
    let output = run_to_end("spin", 1, MEMORY_BYTES);
    assert!(
        matches!(output.status.signal(), Some(libc::SIGXCPU | libc::SIGKILL)),
        "unexpected termination: {:?}",
        output.status
    );
}

fn file_size_limit_stops_the_output_growing() {
    let output = run_to_end("grow", 10, MEMORY_BYTES);
    assert_eq!(
        output.status.signal(),
        Some(libc::SIGXFSZ),
        "unexpected termination: {:?}",
        output.status
    );
}

fn helper(scenario: &str) {
    match scenario {
        "report" => println!("reported"),
        "write-output" => match std::fs::write("output.dll", b"compiled") {
            Ok(()) => println!("written"),
            Err(error) => println!("{:?}", error.kind()),
        },
        "probe" => probe(),
        "cross-process" => cross_process_probe(),
        "victim" => {
            // Default dispositions: SIGIO, SIGUSR1 or any other route that reaches this process ends it.
            let mut output = std::io::stdout();
            output.write_all(b"ready\n").unwrap();
            output.flush().unwrap();
            std::io::stdin().read_to_end(&mut Vec::new()).unwrap();
            output.write_all(b"alive\n").unwrap();
        }
        "leak-descriptor" => {
            // An inherited descriptor without close-on-exec must stop the launcher before its marker.
            let leaked = rustix::io::dup(std::io::stdout()).unwrap();
            let mut child = Command::new(LAUNCHER)
                .args(launch_arguments(10, MEMORY_BYTES, &[]))
                .env_clear()
                .env(HELPER, "report")
                .stdin(Stdio::piped())
                .stdout(Stdio::piped())
                .stderr(Stdio::piped())
                .spawn()
                .unwrap();
            drop(child.stdin.take());
            let output = child.wait_with_output().unwrap();
            drop(leaked);
            let refused = output.status.code()
                == Some(i32::from(sandbox::SandboxError::Descriptors.exit_code()))
                && output.stdout.is_empty()
                && output.stderr.is_empty();
            println!("{}", if refused { "refused" } else { "admitted" });
        }
        "reserve" => {
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
            let mut value = 0u64;
            loop {
                value = std::hint::black_box(value.wrapping_add(1));
            }
        }
        "grow" => {
            let mut output = std::fs::OpenOptions::new()
                .write(true)
                .open("output.dll")
                .unwrap();
            let chunk = vec![0u8; 64 << 10];
            loop {
                // Beyond the file-size limit the kernel delivers SIGXFSZ.
                output.write_all(&chunk).unwrap();
            }
        }
        other => panic!("unknown helper scenario {other}"),
    }
}

/// Runs inside the launched program, after `execve`, and reports each attempted operation.
// Calls newer than the pinned libc's tables.
const SYS_SETXATTRAT: libc::c_long = 463;
const SYS_REMOVEXATTRAT: libc::c_long = 466;
const SYS_FILE_GETATTR: libc::c_long = 468;
const SYS_FILE_SETATTR: libc::c_long = 469;

fn probe() {
    let parent = rustix::process::getppid().unwrap();
    let mut report = String::new();
    let kind = |result: std::io::Result<()>| match result {
        Ok(()) => "ok".to_string(),
        Err(error) => format!("{:?}", error.kind()),
    };
    let errno = |result: libc::c_int| {
        if result == 0 {
            return "ok".to_string();
        }
        match std::io::Error::last_os_error().raw_os_error() {
            Some(libc::EPERM) => "EPERM".to_string(),
            Some(libc::EACCES) => "EACCES".to_string(),
            other => format!("{other:?}"),
        }
    };
    let mut line = |name: &str, value: String| report.push_str(&format!("{name}={value}\n"));
    line(
        "tcp-bind",
        kind(std::net::TcpListener::bind("127.0.0.1:0").map(drop)),
    );
    line(
        "udp-bind",
        kind(std::net::UdpSocket::bind("127.0.0.1:0").map(drop)),
    );
    line(
        "unix-bind",
        kind(std::os::unix::net::UnixListener::bind("listener").map(drop)),
    );
    line(
        "spawn-process",
        kind(
            Command::new(std::env::current_exe().unwrap())
                .stdin(Stdio::piped())
                .stdout(Stdio::piped())
                .stderr(Stdio::piped())
                .spawn()
                .map(drop),
        ),
    );
    line(
        "spawn-thread",
        match std::thread::Builder::new().spawn(|| ()) {
            Ok(thread) => {
                thread.join().unwrap();
                "created".to_string()
            }
            Err(_) => "refused".to_string(),
        },
    );
    line(
        "read-grant",
        kind(std::fs::read("../toolchain/reference.dll").map(drop)),
    );
    line("read-input", kind(std::fs::read("input.cs").map(drop)));
    line(
        "read-own-process",
        kind(std::fs::read("/proc/self/status").map(drop)),
    );
    line(
        "write-output",
        kind(std::fs::write("output.dll", b"compiled")),
    );
    line(
        "write-input",
        kind(
            std::fs::OpenOptions::new()
                .append(true)
                .open("input.cs")
                .map(drop),
        ),
    );
    let input = CString::new("input.cs").unwrap();
    line(
        "truncate-input",
        kind(if unsafe { libc::truncate(input.as_ptr(), 0) } == 0 {
            Ok(())
        } else {
            Err(std::io::Error::last_os_error())
        }),
    );
    line("remove-input", kind(std::fs::remove_file("input.cs")));
    line("create-file", kind(std::fs::write("created", b"x")));
    line(
        "create-directory",
        kind(std::fs::create_dir("created-directory")),
    );
    line(
        "read-parent-file",
        kind(std::fs::read("../secret").map(drop)),
    );
    line("list-parent", kind(std::fs::read_dir("..").map(drop)));
    line(
        "read-other-process",
        kind(std::fs::read(format!("/proc/{}/status", parent.as_raw_nonzero())).map(drop)),
    );
    let other = CString::new("/bin/sh").unwrap();
    let arguments = [other.as_ptr(), std::ptr::null()];
    let environment = [std::ptr::null::<libc::c_char>()];
    line(
        "execute-other",
        errno(unsafe { libc::execve(other.as_ptr(), arguments.as_ptr(), environment.as_ptr()) }),
    );
    let output = CString::new("output.dll").unwrap();
    line(
        "change-mode",
        errno(unsafe { libc::chmod(output.as_ptr(), 0o600) }),
    );
    // Read access to a shared toolchain file must not permit changing its metadata.
    // fchmod does not require a descriptor opened for writing.
    use std::os::fd::AsRawFd;
    let reference = std::fs::File::open("../toolchain/reference.dll").unwrap();
    line(
        "change-read-grant-mode-by-fd",
        errno(unsafe { libc::fchmod(reference.as_raw_fd(), 0o600) }),
    );
    // The newer *at and attribute calls and the inode-flag ioctls also need only a read-only descriptor.
    let empty = CString::new("").unwrap();
    let attribute = CString::new("user.weft").unwrap();
    // struct xattr_args { u64 value; u32 size; u32 flags; }
    let value = *b"x";
    let attribute_value: [u64; 2] = [value.as_ptr() as u64, 1];
    let call = |result: libc::c_long| errno(if result == 0 { 0 } else { -1 });
    line(
        "set-read-grant-attribute-at",
        call(unsafe {
            libc::syscall(
                SYS_SETXATTRAT,
                reference.as_raw_fd(),
                empty.as_ptr(),
                libc::AT_EMPTY_PATH,
                attribute.as_ptr(),
                attribute_value.as_ptr(),
                size_of_val(&attribute_value),
            )
        }),
    );
    line(
        "remove-read-grant-attribute-at",
        call(unsafe {
            libc::syscall(
                SYS_REMOVEXATTRAT,
                reference.as_raw_fd(),
                empty.as_ptr(),
                libc::AT_EMPTY_PATH,
                attribute.as_ptr(),
            )
        }),
    );
    // struct file_attr { u64 fa_xflags; u32 fa_extsize; u32 fa_nextents; u32 fa_projid; u32 fa_cowextsize; }
    let mut file_attributes = [0u8; 24];
    let size = file_attributes.len();
    unsafe {
        libc::syscall(
            SYS_FILE_GETATTR,
            reference.as_raw_fd(),
            empty.as_ptr(),
            file_attributes.as_mut_ptr(),
            size,
            libc::AT_EMPTY_PATH,
        );
    }
    line(
        "set-read-grant-file-attributes",
        call(unsafe {
            libc::syscall(
                SYS_FILE_SETATTR,
                reference.as_raw_fd(),
                empty.as_ptr(),
                file_attributes.as_ptr(),
                size,
                libc::AT_EMPTY_PATH,
            )
        }),
    );
    let mut flags: libc::c_long = 0;
    unsafe { libc::ioctl(reference.as_raw_fd(), libc::FS_IOC_GETFLAGS, &raw mut flags) };
    line(
        "set-read-grant-flags-by-ioctl",
        errno(unsafe { libc::ioctl(reference.as_raw_fd(), libc::FS_IOC_SETFLAGS, &raw mut flags) }),
    );
    // struct fsxattr is 28 bytes; FS_IOC_FSGETXATTR and FS_IOC_FSSETXATTR.
    let mut extended = [0u8; 28];
    unsafe { libc::ioctl(reference.as_raw_fd(), 0x801c_581f, extended.as_mut_ptr()) };
    line(
        "set-read-grant-extended-flags-by-ioctl",
        errno(unsafe { libc::ioctl(reference.as_raw_fd(), 0x401c_5820, extended.as_mut_ptr()) }),
    );
    let mut limits = std::mem::MaybeUninit::<libc::rlimit64>::uninit();
    line(
        "other-process-limits",
        errno(unsafe {
            libc::prlimit64(
                parent.as_raw_nonzero().get(),
                libc::RLIMIT_NOFILE,
                std::ptr::null(),
                limits.as_mut_ptr(),
            )
        }),
    );
    let segment = unsafe { libc::shmget(libc::IPC_PRIVATE, 4096, libc::IPC_CREAT | 0o600) };
    line("shared-memory", errno(if segment >= 0 { 0 } else { -1 }));
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
    line("signal-parent", signal(Some(parent), false));
    line(
        "signal-init",
        signal(rustix::process::Pid::from_raw(1), false),
    );
    line("signal-group", signal(None, true));
    line(
        "signal-self",
        signal(Some(rustix::process::getpid()), false),
    );
    std::io::stdout().write_all(report.as_bytes()).unwrap();
}

/// Runs inside the launched program and tries each route to the victim, then the program's own signals.
fn cross_process_probe() {
    use std::sync::atomic::{AtomicBool, Ordering};

    const F_SETSIG: libc::c_int = 10;
    const F_SETOWN_EX: libc::c_int = 15;
    const F_OWNER_PID: libc::c_int = 1;
    const FIOSETOWN: libc::c_ulong = 0x8901;
    const SIOCSPGRP: libc::c_ulong = 0x8902;
    const PTRACE_SEIZE: libc::c_long = 0x4206;
    const KCMP_VM: libc::c_long = 1;
    const MADV_COLD: libc::c_long = 20;
    const IOPRIO_WHO_PROCESS: libc::c_long = 1;
    const IOPRIO_IDLE: libc::c_long = 3 << 13;
    const KEYCTL_GET_KEYRING_ID: libc::c_long = 0;
    const KEY_SPEC_USER_KEYRING: libc::c_long = -4;
    static HANDLED: AtomicBool = AtomicBool::new(false);
    extern "C" fn handle(_: libc::c_int) {
        HANDLED.store(true, Ordering::SeqCst);
    }

    let victim: libc::pid_t = std::env::var(VICTIM).unwrap().parse().unwrap();
    let target = libc::c_long::from(victim);
    let mut report = String::new();
    let mut line = |name: &str, result: libc::c_long| {
        let value = if result >= 0 {
            "ok".to_string()
        } else {
            match std::io::Error::last_os_error().raw_os_error() {
                Some(libc::EPERM) => "EPERM".to_string(),
                Some(libc::EACCES) => "EACCES".to_string(),
                other => format!("{other:?}"),
            }
        };
        report.push_str(&format!("{name}={value}\n"));
    };
    let call = |result: libc::c_int| libc::c_long::from(result);

    // Owner and asynchronous controls on a pipe, like the standard output the service reads: once an owner and
    // asynchronous mode are in force, the write below signals the owner with this process's credentials.
    let mut pipe = [0; 2];
    assert_eq!(
        unsafe { libc::pipe2(pipe.as_mut_ptr(), libc::O_CLOEXEC) },
        0
    );
    let (reader, writer) = (pipe[0], pipe[1]);
    line(
        "set-owner",
        call(unsafe { libc::fcntl(reader, libc::F_SETOWN, victim) }),
    );
    let owner = [F_OWNER_PID, victim];
    line(
        "set-owner-ex",
        call(unsafe { libc::fcntl(reader, F_SETOWN_EX, owner.as_ptr()) }),
    );
    line(
        "set-owner-signal",
        call(unsafe { libc::fcntl(reader, F_SETSIG, libc::SIGUSR1) }),
    );
    let flags = unsafe { libc::fcntl(reader, libc::F_GETFL) };
    line(
        "set-asynchronous",
        call(unsafe { libc::fcntl(reader, libc::F_SETFL, flags | libc::O_ASYNC) }),
    );
    line(
        "set-non-blocking",
        call(unsafe { libc::fcntl(reader, libc::F_SETFL, flags | libc::O_NONBLOCK) }),
    );
    let input = std::fs::File::open("input.cs").unwrap();
    line(
        "set-lease",
        call(unsafe { libc::fcntl(input.as_raw_fd(), libc::F_SETLEASE, libc::F_RDLCK) }),
    );
    line(
        "ioctl-set-owner",
        call(unsafe { libc::ioctl(reader, FIOSETOWN, &raw const victim) }),
    );
    line(
        "ioctl-set-process-group",
        call(unsafe { libc::ioctl(reader, SIOCSPGRP, &raw const victim) }),
    );
    let on: libc::c_int = 1;
    line(
        "ioctl-set-asynchronous",
        call(unsafe { libc::ioctl(reader, libc::FIOASYNC, &raw const on) }),
    );
    line(
        "ioctl-set-terminal-group",
        call(unsafe { libc::ioctl(reader, libc::TIOCSPGRP, &raw const victim) }),
    );
    let character = b'x';
    line(
        "ioctl-inject-terminal-input",
        call(unsafe { libc::ioctl(reader, libc::TIOCSTI, &raw const character) }),
    );
    assert_eq!(
        unsafe { libc::write(writer, (&raw const character).cast(), 1) },
        1
    );

    // Signals, directly or through a process descriptor.
    line("signal", call(unsafe { libc::kill(victim, libc::SIGUSR1) }));
    line("signal-thread", unsafe {
        libc::syscall(libc::SYS_tkill, victim, libc::SIGUSR1)
    });
    line("signal-group-thread", unsafe {
        libc::syscall(libc::SYS_tgkill, victim, victim, libc::SIGUSR1)
    });
    let mut information: libc::siginfo_t = unsafe { std::mem::zeroed() };
    information.si_signo = libc::SIGUSR1;
    information.si_code = libc::SI_QUEUE;
    line("queue-signal", unsafe {
        libc::syscall(
            libc::SYS_rt_sigqueueinfo,
            victim,
            libc::SIGUSR1,
            &raw const information,
        )
    });
    line("queue-thread-signal", unsafe {
        libc::syscall(
            libc::SYS_rt_tgsigqueueinfo,
            victim,
            victim,
            libc::SIGUSR1,
            &raw const information,
        )
    });
    let descriptor = unsafe { libc::syscall(libc::SYS_pidfd_open, victim, 0) };
    line("open-process", descriptor);
    // Without a process descriptor, an invalid one still shows whether the filter refuses the call.
    let descriptor = if descriptor >= 0 { descriptor } else { -1 };
    line("signal-process-descriptor", unsafe {
        libc::syscall(
            libc::SYS_pidfd_send_signal,
            descriptor,
            libc::SIGUSR1,
            std::ptr::null::<libc::siginfo_t>(),
            0,
        )
    });
    line("copy-descriptor", unsafe {
        libc::syscall(libc::SYS_pidfd_getfd, descriptor, 0, 0)
    });
    let mut page = [0u8; 8];
    let local = libc::iovec {
        iov_base: page.as_mut_ptr().cast(),
        iov_len: page.len(),
    };
    line("advise-memory", unsafe {
        libc::syscall(
            libc::SYS_process_madvise,
            descriptor,
            &raw const local,
            1,
            MADV_COLD,
            0,
        )
    });
    line("release-memory", unsafe {
        libc::syscall(libc::SYS_process_mrelease, descriptor, 0)
    });

    // Tracing and memory, including through procfs.
    line("trace", unsafe {
        libc::syscall(libc::SYS_ptrace, PTRACE_SEIZE, victim, 0, 0)
    });
    let remote = libc::iovec {
        iov_base: (&raw const local).cast_mut().cast(),
        iov_len: page.len(),
    };
    line("read-memory", unsafe {
        libc::syscall(
            libc::SYS_process_vm_readv,
            victim,
            &raw const local,
            1,
            &raw const remote,
            1,
            0,
        )
    });
    line("write-memory", unsafe {
        libc::syscall(
            libc::SYS_process_vm_writev,
            victim,
            &raw const local,
            1,
            &raw const remote,
            1,
            0,
        )
    });
    let own = libc::c_long::from(unsafe { libc::getpid() });
    line("compare-resources", unsafe {
        libc::syscall(libc::SYS_kcmp, own, target, KCMP_VM, 0, 0)
    });
    let mut head = std::ptr::null_mut::<libc::c_void>();
    let mut length = 0usize;
    line("robust-list", unsafe {
        libc::syscall(
            libc::SYS_get_robust_list,
            victim,
            &raw mut head,
            &raw mut length,
        )
    });
    let open = |path: String, flags: libc::c_int| {
        let path = CString::new(path).unwrap();
        let descriptor = unsafe { libc::open(path.as_ptr(), flags | libc::O_CLOEXEC) };
        if descriptor >= 0 {
            unsafe { libc::close(descriptor) };
        }
        libc::c_long::from(descriptor)
    };
    line(
        "open-memory",
        open(format!("/proc/{victim}/mem"), libc::O_RDONLY),
    );
    line(
        "read-status",
        open(format!("/proc/{victim}/status"), libc::O_RDONLY),
    );
    line(
        "list-processes",
        open("/proc".to_string(), libc::O_RDONLY | libc::O_DIRECTORY),
    );

    // Scheduling, limits and memory placement.
    line(
        "priority",
        call(unsafe { libc::setpriority(libc::PRIO_PROCESS, victim as libc::id_t, 19) }),
    );
    let mut processors: libc::cpu_set_t = unsafe { std::mem::zeroed() };
    unsafe { libc::CPU_SET(0, &mut processors) };
    line(
        "affinity",
        call(unsafe {
            libc::sched_setaffinity(victim, size_of::<libc::cpu_set_t>(), &raw const processors)
        }),
    );
    let parameters = libc::sched_param { sched_priority: 0 };
    line(
        "scheduler",
        call(unsafe { libc::sched_setscheduler(victim, libc::SCHED_BATCH, &raw const parameters) }),
    );
    line(
        "scheduler-parameters",
        call(unsafe { libc::sched_setparam(victim, &raw const parameters) }),
    );
    // struct sched_attr, version 0: its size first, then a policy and parameters of zero.
    let mut attributes = [0u32; 12];
    attributes[0] = size_of_val(&attributes) as u32;
    line("scheduler-attributes", unsafe {
        libc::syscall(libc::SYS_sched_setattr, victim, attributes.as_ptr(), 0)
    });
    line("io-priority", unsafe {
        libc::syscall(
            libc::SYS_ioprio_set,
            IOPRIO_WHO_PROCESS,
            victim,
            IOPRIO_IDLE,
        )
    });
    let core = libc::rlimit64 {
        rlim_cur: 0,
        rlim_max: 0,
    };
    line(
        "limits",
        call(unsafe {
            libc::prlimit64(
                victim,
                libc::RLIMIT_CORE,
                &raw const core,
                std::ptr::null_mut(),
            )
        }),
    );
    let nodes = [1u64];
    line("migrate-pages", unsafe {
        libc::syscall(
            libc::SYS_migrate_pages,
            victim,
            64,
            nodes.as_ptr(),
            nodes.as_ptr(),
        )
    });
    let mut status = [0i32];
    line("move-pages", unsafe {
        libc::syscall(
            libc::SYS_move_pages,
            victim,
            0,
            std::ptr::null::<*const libc::c_void>(),
            std::ptr::null::<libc::c_int>(),
            status.as_mut_ptr(),
            0,
        )
    });

    // Kernel interfaces that observe or share state across processes, and sockets (abstract unix sockets included).
    // struct perf_event_attr: a software clock of the victim.
    let mut event = [0u32; 32];
    event[0] = 1;
    event[1] = size_of_val(&event) as u32;
    line("performance-events", unsafe {
        libc::syscall(libc::SYS_perf_event_open, event.as_ptr(), victim, -1, -1, 0)
    });
    let mut ring = [0u8; 120];
    line("io-uring", unsafe {
        libc::syscall(libc::SYS_io_uring_setup, 1, ring.as_mut_ptr())
    });
    line("userfaultfd", unsafe {
        libc::syscall(libc::SYS_userfaultfd, libc::O_CLOEXEC)
    });
    line("user-keyring", unsafe {
        libc::syscall(
            libc::SYS_keyctl,
            KEYCTL_GET_KEYRING_ID,
            KEY_SPEC_USER_KEYRING,
            0,
        )
    });
    line("add-key", unsafe {
        libc::syscall(
            libc::SYS_add_key,
            c"user".as_ptr(),
            c"weft".as_ptr(),
            (&raw const character).cast::<libc::c_void>(),
            1,
            KEY_SPEC_USER_KEYRING,
        )
    });
    line(
        "unix-socket",
        call(unsafe { libc::socket(libc::AF_UNIX, libc::SOCK_STREAM | libc::SOCK_CLOEXEC, 0) }),
    );
    let mut pair = [0; 2];
    line(
        "socket-pair",
        call(unsafe {
            libc::socketpair(
                libc::AF_UNIX,
                libc::SOCK_STREAM | libc::SOCK_CLOEXEC,
                0,
                pair.as_mut_ptr(),
            )
        }),
    );

    // The program's own process stays reachable: the runtime signals itself and its threads.
    line("own-robust-list", unsafe {
        libc::syscall(libc::SYS_get_robust_list, 0, &raw mut head, &raw mut length)
    });
    line(
        "read-own-thread",
        open("/proc/thread-self/status".to_string(), libc::O_RDONLY),
    );
    line(
        "signal-self",
        call(unsafe { libc::kill(libc::getpid(), 0) }),
    );
    line("signal-own-thread", unsafe {
        libc::syscall(libc::SYS_tgkill, own, libc::gettid(), 0)
    });
    let (stop, parked) = std::sync::mpsc::channel::<()>();
    let thread = std::thread::spawn(move || parked.recv().unwrap_or(()));
    use std::os::unix::thread::JoinHandleExt;
    let delivered = unsafe { libc::pthread_kill(thread.as_pthread_t(), 0) };
    line(
        "signal-other-own-thread",
        if delivered == 0 { 0 } else { -1 },
    );
    stop.send(()).unwrap();
    thread.join().unwrap();
    unsafe { libc::signal(libc::SIGUSR2, handle as *const () as libc::sighandler_t) };
    let raised = unsafe { libc::raise(libc::SIGUSR2) };
    report.push_str(&format!(
        "raise-self={}\n",
        if raised == 0 && HANDLED.load(Ordering::SeqCst) {
            "handled"
        } else {
            "refused"
        }
    ));
    std::io::stdout().write_all(report.as_bytes()).unwrap();
}
