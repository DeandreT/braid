//! Self-hardening launcher for one operation of the portable Ferrule service that runs a .NET program, such as
//! the compiler of a compile operation.
//!
//! The service writes an operation's trusted inputs (for a compile: the generated sources, the compiler response
//! file and an empty output file) into a fresh private directory, then starts `braid-compile-sandbox` there with
//! fixed limits and a closed managed profile. Before the program runs, the launcher applies hard resource limits,
//! becomes non-dumpable, sets no-new-privileges, confines the filesystem with Landlock (read-only grants and private
//! directory, write access to at most one existing output file, no execution or TCP) and
//! installs syscall filters (no sockets, no new processes, no tracing or memory access, signals, descriptor owners,
//! scheduling or limits for other processes, no System V IPC or message queues, no namespace or mount operations).
//! The filters alone keep the program from other processes of the same user wherever Landlock does not scope
//! signals and abstract sockets (ABI 3 to 5). It then writes
//! [`READY_MARKER`] and waits for [`GO`], so the service can verify the applied state in procfs before the program
//! starts. The pinned hostfxr loads in this process: no exec resets dumpability. The deployment must supply a
//! trusted origin without a preattached tracer or preopened foreign memory handle; this is not process-birth
//! protection. Untrusted workloads start only after hardening, and already hardened peers cannot trace them.

use std::ffi::{CString, OsString};
use std::fmt;
use std::ops::RangeInclusive;

#[cfg(all(target_os = "linux", target_arch = "x86_64"))]
mod hostfxr;
#[cfg(any(test, all(target_os = "linux", target_arch = "x86_64")))]
mod hostfxr_abi;

/// Written once to standard output after every control is in force and before the program starts.
pub const READY_MARKER: [u8; 4] = *b"BFC2";
/// The one byte on standard input that starts the program; anything else stops the launcher.
pub const GO: u8 = b'G';
/// Landlock ABI 3 (Linux 6.2) also handles truncation, so no file outside the grants can be cut.
pub const MINIMUM_LANDLOCK_ABI: i64 = 3;
/// Syscall filters the launcher installs; the service verifies the count in procfs.
pub const FILTERS: usize = 2;

const CPU_SECONDS: RangeInclusive<u64> = 1..=600;
const MEMORY_BYTES: RangeInclusive<u64> = (64 << 20)..=(16 << 30);
const OPEN_FILES: RangeInclusive<u64> = 8..=1024;
const FILE_BYTES: RangeInclusive<u64> = (1 << 20)..=(1 << 30);
const MAXIMUM_PATHS: usize = 16;
pub const SDK_VERSION: &str = "10.0.112";
pub const RUNTIME_VERSION: &str = "10.0.12";

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ManagedProfile {
    Compile,
    Runtime(CString, RuntimeOperation),
}

/// The runtime assembly's fixed entry operations; the trusted service selects one, never a request.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RuntimeOperation {
    Mapping,
    XmlCanonicalComparison,
}

/// The only argument the launcher forwards to a managed program.
pub const XML_COMPARISON_ARGUMENT: &str = "--xml-c14n10-comments";

/// One launch, selected by the trusted service, never by a request.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Launch {
    pub cpu_seconds: u64,
    pub memory_bytes: u64,
    pub open_files: u64,
    pub file_bytes: u64,
    /// A file name in the working directory: the only file the program may write. Without it nothing is writable.
    pub output: Option<CString>,
    /// Directories the program may read beneath, or single files such as /dev/urandom.
    pub read: Vec<CString>,
    /// Exact installed root, never resolved by PATH, nethost or a newest-version search.
    pub dotnet_root: CString,
    pub profile: ManagedProfile,
}

/// Closed startup categories. Exit codes carry no native error text or process operands.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SandboxError {
    Arguments,
    Platform,
    Procfs,
    Descriptors,
    Limits,
    NonDumpable,
    NoNewPrivileges,
    FilterInvalid,
    FilterInstall,
    ReadyMarker,
    LandlockUnavailable,
    PathGrant,
    LandlockRestrict,
    Confinement,
    Origin,
    Execute,
}

impl SandboxError {
    /// Shared with the service's internal admission diagnostics, not a worker response.
    pub const fn exit_code(self) -> u8 {
        match self {
            Self::Arguments => 80,
            Self::Platform => 81,
            Self::Procfs => 82,
            Self::Descriptors => 83,
            Self::Limits => 84,
            Self::NonDumpable => 85,
            Self::NoNewPrivileges => 86,
            Self::FilterInvalid => 87,
            Self::FilterInstall => 88,
            Self::ReadyMarker => 89,
            Self::LandlockUnavailable => 90,
            Self::PathGrant => 91,
            Self::LandlockRestrict => 92,
            Self::Confinement => 93,
            Self::Origin => 94,
            Self::Execute => 1,
        }
    }
}

impl fmt::Display for SandboxError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(match self {
            Self::Arguments => "The launcher arguments are not admitted.",
            Self::Platform => "The compile sandbox requires Linux on x86_64.",
            Self::Procfs => "Procfs is unavailable.",
            Self::Descriptors => "The launcher inherited an unexpected file descriptor.",
            Self::Limits => "The launcher could not apply its resource limits.",
            Self::NonDumpable => "The launcher could not become non-dumpable.",
            Self::NoNewPrivileges => "The launcher could not set no-new-privileges.",
            Self::FilterInvalid => "The launcher syscall filter is invalid.",
            Self::FilterInstall => "The launcher could not install its syscall filter.",
            Self::ReadyMarker => "The launcher could not write its readiness marker.",
            Self::LandlockUnavailable => "Landlock is unavailable.",
            Self::PathGrant => "The launcher could not grant a path.",
            Self::LandlockRestrict => "The launcher could not enforce Landlock.",
            Self::Confinement => "The launcher could not confirm its filesystem confinement.",
            Self::Origin => "The launcher requires a single-threaded, stable non-root, capless and untraced origin.",
            Self::Execute => "The pinned managed host could not run the application.",
        })
    }
}

impl std::error::Error for SandboxError {}

#[cfg(test)]
mod argument_tests {
    use super::*;

    #[test]
    fn only_closed_managed_profiles_are_admitted() {
        let base = [
            "--cpu-seconds=30",
            "--memory-bytes=3221225472",
            "--open-files=128",
            "--file-bytes=1048576",
            "--dotnet-root=/usr/share/dotnet",
        ];
        let parse = |tail: &[&str]| {
            parse_arguments(
                &base
                    .iter()
                    .chain(tail)
                    .map(OsString::from)
                    .collect::<Vec<_>>(),
            )
        };
        assert_eq!(
            parse(&["--profile=compile"]).unwrap().profile,
            ManagedProfile::Compile
        );
        assert!(matches!(
            parse(&["--profile=runtime", "--runtime-assembly=/runtime/fixed.dll"])
                .unwrap()
                .profile,
            ManagedProfile::Runtime(_, RuntimeOperation::Mapping)
        ));
        let xml = parse(&[
            "--profile=runtime",
            "--runtime-assembly=/runtime/fixed.dll",
            "--read=/runtime",
            XML_COMPARISON_ARGUMENT,
        ])
        .unwrap();
        assert!(matches!(
            xml.profile,
            ManagedProfile::Runtime(_, RuntimeOperation::XmlCanonicalComparison)
        ));
        assert_eq!(xml.read, [CString::new("/runtime").unwrap()]);
        for tail in [
            vec!["--profile=compile", XML_COMPARISON_ARGUMENT],
            vec![
                "--profile=runtime",
                "--runtime-assembly=/runtime/fixed.dll",
                XML_COMPARISON_ARGUMENT,
                "--read=/runtime",
            ],
            vec![
                "--profile=runtime",
                "--runtime-assembly=/runtime/fixed.dll",
                XML_COMPARISON_ARGUMENT,
                XML_COMPARISON_ARGUMENT,
            ],
            vec![
                "--profile=runtime",
                "--runtime-assembly=/runtime/fixed.dll",
                "--xml-c14n10",
            ],
            vec![],
            vec!["--profile=runtime"],
            vec!["--profile=runtime", "--runtime-assembly=relative.dll"],
            vec!["--profile=runtime", "--runtime-assembly=/a/../fixed.dll"],
            vec!["--profile=runtime", "--runtime-assembly=/runtime/fixed.exe"],
            vec!["--profile=compile", "--profile=compile"],
            vec!["--profile=compile", "--dotnet-root=/other"],
            vec!["--profile=compile", "--execute=/bin/sh"],
            vec!["--profile=compile", "--", "/bin/sh"],
            vec![
                "--profile=runtime",
                "--runtime-assembly=/runtime/fixed.dll",
                "extra",
            ],
            vec!["--profile=sdk"],
        ] {
            assert!(parse(&tail).is_err(), "{tail:?}");
        }
    }
}

/// Reads the exact argument shape:
/// `--cpu-seconds=N --memory-bytes=N --open-files=N --file-bytes=N [--output=NAME]
/// --dotnet-root=PATH --profile=compile|runtime [--runtime-assembly=PATH] [--read=PATH]...
/// [--xml-c14n10-comments]` (the last only with the runtime profile)
pub fn parse_arguments(arguments: &[OsString]) -> Result<Launch, SandboxError> {
    let invalid = SandboxError::Arguments;
    let [cpu, memory, files, file_bytes, rest @ ..] = arguments else {
        return Err(invalid);
    };
    let value = |argument: &OsString, name: &str, range: RangeInclusive<u64>| {
        let text = argument.to_str()?.strip_prefix(name)?;
        // Canonical decimal only: no sign, padding or leading zero.
        if text.is_empty()
            || text.len() > 20
            || text.starts_with('0')
            || !text.bytes().all(|b| b.is_ascii_digit())
        {
            return None;
        }
        text.parse::<u64>()
            .ok()
            .filter(|value| range.contains(value))
    };
    let (Some(cpu_seconds), Some(memory_bytes), Some(open_files), Some(file_bytes)) = (
        value(cpu, "--cpu-seconds=", CPU_SECONDS),
        value(memory, "--memory-bytes=", MEMORY_BYTES),
        value(files, "--open-files=", OPEN_FILES),
        value(file_bytes, "--file-bytes=", FILE_BYTES),
    ) else {
        return Err(invalid);
    };
    let (output, rest) = match rest {
        [first, rest @ ..]
            if first
                .to_str()
                .is_some_and(|text| text.starts_with("--output=")) =>
        {
            (
                Some(
                    first
                        .to_str()
                        .and_then(|text| text.strip_prefix("--output="))
                        .filter(|name| file_name(name))
                        .and_then(|name| CString::new(name).ok())
                        .ok_or(SandboxError::Arguments)?,
                ),
                rest,
            )
        }
        _ => (None, rest),
    };
    let [root, profile, rest @ ..] = rest else {
        return Err(invalid);
    };
    let dotnet_root = root
        .to_str()
        .and_then(|value| value.strip_prefix("--dotnet-root="))
        .and_then(absolute)
        .ok_or(SandboxError::Arguments)?;
    let (profile, rest) = match (profile.to_str(), rest) {
        (Some("--profile=compile"), rest)
            if output
                .as_ref()
                .is_none_or(|name| name == c"Ferrule.Generated.dll") =>
        {
            (ManagedProfile::Compile, rest)
        }
        (Some("--profile=runtime"), [assembly, rest @ ..]) if output.is_none() => {
            let path = assembly
                .to_str()
                .and_then(|value| value.strip_prefix("--runtime-assembly="))
                .filter(|value| value.ends_with(".dll"))
                .and_then(absolute)
                .ok_or(SandboxError::Arguments)?;
            // Only the final argument can select the fixed XML operation.
            match rest {
                [rest @ .., last] if last.to_str() == Some(XML_COMPARISON_ARGUMENT) => (
                    ManagedProfile::Runtime(path, RuntimeOperation::XmlCanonicalComparison),
                    rest,
                ),
                _ => (
                    ManagedProfile::Runtime(path, RuntimeOperation::Mapping),
                    rest,
                ),
            }
        }
        _ => return Err(invalid),
    };
    let mut read = Vec::new();
    for option in rest {
        let text = option.to_str().ok_or(SandboxError::Arguments)?;
        if let Some(path) = text.strip_prefix("--read=") {
            read.push(absolute(path).ok_or(SandboxError::Arguments)?);
        } else {
            return Err(invalid);
        }
    }
    if read.len() > MAXIMUM_PATHS {
        return Err(invalid);
    }
    Ok(Launch {
        cpu_seconds,
        memory_bytes,
        open_files,
        file_bytes,
        output,
        read,
        dotnet_root,
        profile,
    })
}

/// A normalized absolute path other than the root: no empty, `.` or `..` component and no trailing slash.
fn absolute(path: &str) -> Option<CString> {
    let components = path.strip_prefix('/')?;
    if path.len() > 4096 || components.is_empty() || !components.split('/').all(file_name) {
        return None;
    }
    CString::new(path).ok()
}

fn file_name(name: &str) -> bool {
    !name.is_empty()
        && name.len() <= 255
        && name != "."
        && name != ".."
        && !name.contains(['/', '\0'])
}

#[cfg(not(all(target_os = "linux", target_arch = "x86_64")))]
pub fn enter(_launch: &Launch) -> Result<(), SandboxError> {
    Err(SandboxError::Platform)
}

/// Applies every control to the calling single-threaded process. Any failure leaves the caller to exit without
/// starting the program.
#[cfg(all(target_os = "linux", target_arch = "x86_64"))]
pub fn enter(launch: &Launch) -> Result<(), SandboxError> {
    require_standard_descriptors_only()?;
    require_unprivileged_origin()?;
    prefer_out_of_memory_kill();
    let ruleset = landlock::Ruleset::new(launch)?;
    apply_limits(launch)?;
    rustix::process::set_dumpable_behavior(rustix::process::DumpableBehavior::NotDumpable)
        .map_err(|_| SandboxError::NonDumpable)?;
    rustix::thread::set_no_new_privs(true).map_err(|_| SandboxError::NoNewPrivileges)?;
    ruleset.restrict_self()?;
    confirm_confinement()?;
    for program in filters()? {
        seccompiler::apply_filter(&program).map_err(|_| SandboxError::FilterInstall)?;
    }
    require_unprivileged_origin()?;
    if unsafe { libc::prctl(libc::PR_GET_DUMPABLE, 0, 0, 0, 0) } != 0 {
        return Err(SandboxError::NonDumpable);
    }
    if !rustix::thread::no_new_privs().map_err(|_| SandboxError::NoNewPrivileges)? {
        return Err(SandboxError::NoNewPrivileges);
    }
    Ok(())
}

/// Runs the one managed application in the hardened process, without any exec or runtime fallback.
#[cfg(all(target_os = "linux", target_arch = "x86_64"))]
pub fn execute(launch: &Launch) -> Result<i32, SandboxError> {
    hostfxr::run(launch)
}

#[cfg(not(all(target_os = "linux", target_arch = "x86_64")))]
pub fn execute(_launch: &Launch) -> Result<i32, SandboxError> {
    Err(SandboxError::Platform)
}

#[cfg(all(target_os = "linux", target_arch = "x86_64"))]
fn require_unprivileged_origin() -> Result<(), SandboxError> {
    let status = std::fs::read_to_string("/proc/self/status").map_err(|_| SandboxError::Procfs)?;
    if status
        .lines()
        .find_map(|line| line.strip_prefix("Threads:"))
        .map(str::trim)
        != Some("1")
    {
        return Err(SandboxError::Origin);
    }
    for field in ["Uid:", "Gid:"] {
        let ids = status
            .lines()
            .find_map(|line| line.strip_prefix(field))
            .ok_or(SandboxError::Origin)?;
        let values = ids
            .split_whitespace()
            .map(str::parse::<u32>)
            .collect::<Result<Vec<_>, _>>()
            .map_err(|_| SandboxError::Origin)?;
        if values.len() != 4 || values[0] == 0 || values.iter().any(|id| *id != values[0]) {
            return Err(SandboxError::Origin);
        }
    }
    for field in ["CapInh:", "CapPrm:", "CapEff:", "CapAmb:", "TracerPid:"] {
        let value = status
            .lines()
            .find_map(|line| line.strip_prefix(field))
            .and_then(|text| {
                u64::from_str_radix(text.trim(), if field == "TracerPid:" { 10 } else { 16 }).ok()
            });
        if value != Some(0) {
            return Err(SandboxError::Origin);
        }
    }
    Ok(())
}

/// The parent must not leak any descriptor beyond the three standard streams.
#[cfg(all(target_os = "linux", target_arch = "x86_64"))]
fn require_standard_descriptors_only() -> Result<(), SandboxError> {
    let failure = SandboxError::Descriptors;
    let own_listing = std::path::PathBuf::from(format!("/proc/{}/fd", std::process::id()));
    let mut listing_descriptors = 0;
    for entry in std::fs::read_dir("/proc/self/fd").map_err(|_| SandboxError::Procfs)? {
        let entry = entry.map_err(|_| SandboxError::Procfs)?;
        let descriptor = entry
            .file_name()
            .to_str()
            .and_then(|name| name.parse::<u32>().ok())
            .ok_or(SandboxError::Procfs)?;
        if descriptor <= 2 {
            continue;
        }
        // The directory handle of this listing is the only other admitted descriptor.
        if std::fs::read_link(entry.path()).ok().as_deref() != Some(own_listing.as_path()) {
            return Err(failure);
        }
        listing_descriptors += 1;
    }
    if listing_descriptors > 1 {
        return Err(failure);
    }
    Ok(())
}

/// Under memory pressure the kernel kills a compiler before the service. Unprivileged processes may raise
/// their own score; a platform that refuses it keeps its default choice.
#[cfg(all(target_os = "linux", target_arch = "x86_64"))]
fn prefer_out_of_memory_kill() {
    let _ = std::fs::write("/proc/self/oom_score_adj", b"1000");
}

#[cfg(all(target_os = "linux", target_arch = "x86_64"))]
fn apply_limits(launch: &Launch) -> Result<(), SandboxError> {
    use rustix::process::{Resource, Rlimit, setrlimit};
    let fixed = |value: u64| Rlimit {
        current: Some(value),
        maximum: Some(value),
    };
    // The compiler needs threads, so the process count stays the user's; the filter refuses new processes.
    for (resource, value) in [
        (Resource::Cpu, launch.cpu_seconds),
        (Resource::As, launch.memory_bytes),
        (Resource::Nofile, launch.open_files),
        (Resource::Fsize, launch.file_bytes),
        (Resource::Core, 0),
        (Resource::Memlock, 0),
        (Resource::Msgqueue, 0),
        (Resource::Locks, 0),
        (Resource::Rtprio, 0),
    ] {
        setrlimit(resource, fixed(value)).map_err(|_| SandboxError::Limits)?;
    }
    Ok(())
}

/// The parent of the private directory, like every path without a grant, must be unreadable.
#[cfg(all(target_os = "linux", target_arch = "x86_64"))]
fn confirm_confinement() -> Result<(), SandboxError> {
    match std::fs::File::open("..") {
        Err(error) if error.kind() == std::io::ErrorKind::PermissionDenied => Ok(()),
        _ => Err(SandboxError::Confinement),
    }
}

#[cfg(all(target_os = "linux", target_arch = "x86_64"))]
mod landlock {
    use std::ffi::CStr;
    use std::os::fd::{AsRawFd, FromRawFd, OwnedFd};

    use super::{Launch, MINIMUM_LANDLOCK_ABI, SandboxError};

    #[repr(C)]
    struct RulesetAttr {
        handled_access_fs: u64,
        handled_access_net: u64,
        scoped: u64,
    }

    #[repr(C, packed)]
    struct PathBeneathAttr {
        allowed_access: u64,
        parent_fd: i32,
    }

    const CREATE_RULESET_VERSION: u32 = 1;
    const RULE_PATH_BENEATH: libc::c_int = 1;
    const EXECUTE: u64 = 1 << 0;
    const WRITE_FILE: u64 = 1 << 1;
    const READ_FILE: u64 = 1 << 2;
    const READ_DIR: u64 = 1 << 3;
    /// Every right of ABI 1: execute, write, read, read directory, remove and make every kind of entry.
    const ABI_1: u64 = (1 << 13) - 1;
    const REFER: u64 = 1 << 13;
    const TRUNCATE: u64 = 1 << 14;
    const IOCTL_DEV: u64 = 1 << 15;
    const FILE_RIGHTS: u64 = EXECUTE | WRITE_FILE | READ_FILE | TRUNCATE | IOCTL_DEV;
    const BIND_TCP: u64 = 1 << 0;
    const CONNECT_TCP: u64 = 1 << 1;
    const SCOPE_ABSTRACT_UNIX_SOCKET: u64 = 1 << 0;
    const SCOPE_SIGNAL: u64 = 1 << 1;

    pub(super) struct Ruleset {
        descriptor: OwnedFd,
        handled: u64,
    }

    impl Ruleset {
        /// Handles every access right the kernel knows, so anything not granted below is denied.
        pub(super) fn new(launch: &Launch) -> Result<Self, SandboxError> {
            let unavailable = SandboxError::LandlockUnavailable;
            let abi = unsafe {
                libc::syscall(
                    libc::SYS_landlock_create_ruleset,
                    std::ptr::null::<RulesetAttr>(),
                    0usize,
                    CREATE_RULESET_VERSION,
                )
            };
            if abi < MINIMUM_LANDLOCK_ABI {
                return Err(unavailable);
            }
            let mut handled = ABI_1 | REFER | TRUNCATE;
            if abi >= 5 {
                handled |= IOCTL_DEV;
            }
            // Fields of later ABIs stay zero for older kernels, which accept a larger zero-filled structure.
            let attribute = RulesetAttr {
                handled_access_fs: handled,
                handled_access_net: if abi >= 4 { BIND_TCP | CONNECT_TCP } else { 0 },
                scoped: if abi >= 6 {
                    SCOPE_ABSTRACT_UNIX_SOCKET | SCOPE_SIGNAL
                } else {
                    0
                },
            };
            let descriptor = unsafe {
                libc::syscall(
                    libc::SYS_landlock_create_ruleset,
                    &attribute as *const RulesetAttr,
                    size_of::<RulesetAttr>(),
                    0u32,
                )
            };
            if descriptor < 0 {
                return Err(unavailable);
            }
            let ruleset = Self {
                descriptor: unsafe { OwnedFd::from_raw_fd(descriptor as i32) },
                handled,
            };
            for directory in &launch.read {
                ruleset.add(directory, READ_FILE | READ_DIR)?;
            }
            // The private directory is read-only; its one output file may be rewritten, never extended to others.
            ruleset.add(c".", READ_FILE | READ_DIR)?;
            if let Some(output) = &launch.output {
                ruleset.add(output, READ_FILE | WRITE_FILE | TRUNCATE)?;
            }
            // The .NET host reads its own process state, not another process's.
            ruleset.add(c"/proc/self", READ_FILE | READ_DIR)?;
            Ok(ruleset)
        }

        fn add(&self, path: &CStr, access: u64) -> Result<(), SandboxError> {
            let failure = SandboxError::PathGrant;
            let raw = unsafe { libc::open(path.as_ptr(), libc::O_PATH | libc::O_CLOEXEC) };
            if raw < 0 {
                return Err(failure);
            }
            let parent = unsafe { OwnedFd::from_raw_fd(raw) };
            let mut status = std::mem::MaybeUninit::<libc::stat>::uninit();
            if unsafe { libc::fstat(parent.as_raw_fd(), status.as_mut_ptr()) } != 0 {
                return Err(failure);
            }
            // A file grant carries only the rights that apply to files, such as reading a device like /dev/urandom.
            let directory = unsafe { status.assume_init() }.st_mode & libc::S_IFMT == libc::S_IFDIR;
            let applicable = if directory {
                access
            } else {
                access & FILE_RIGHTS
            };
            let attribute = PathBeneathAttr {
                allowed_access: applicable & self.handled,
                parent_fd: parent.as_raw_fd(),
            };
            let added = unsafe {
                libc::syscall(
                    libc::SYS_landlock_add_rule,
                    self.descriptor.as_raw_fd(),
                    RULE_PATH_BENEATH,
                    &attribute as *const PathBeneathAttr,
                    0u32,
                )
            };
            if added != 0 {
                return Err(failure);
            }
            Ok(())
        }

        /// Requires no-new-privileges; the domain then holds for this process and everything it executes.
        pub(super) fn restrict_self(self) -> Result<(), SandboxError> {
            let restricted = unsafe {
                libc::syscall(
                    libc::SYS_landlock_restrict_self,
                    self.descriptor.as_raw_fd(),
                    0u32,
                )
            };
            if restricted != 0 {
                return Err(SandboxError::LandlockRestrict);
            }
            Ok(())
        }
    }
}

/// Two deny lists, each allowing every other syscall: one refuses with `EPERM`, the other reports `clone3` as
/// missing so the C library creates threads with `clone`, whose flags the first list can inspect.
#[cfg(all(target_os = "linux", target_arch = "x86_64"))]
fn filters() -> Result<Vec<seccompiler::BpfProgram>, SandboxError> {
    use std::collections::BTreeMap;

    use seccompiler::{
        BpfProgram, SeccompAction, SeccompCmpArgLen, SeccompCmpOp, SeccompCondition, SeccompFilter,
        SeccompRule, TargetArch, sock_filter,
    };

    let invalid = || SandboxError::FilterInvalid;
    let compile =
        |rules: BTreeMap<i64, Vec<SeccompRule>>, errno: i32| -> Result<BpfProgram, SandboxError> {
            let filter = SeccompFilter::new(
                rules,
                SeccompAction::Allow,
                SeccompAction::Errno(errno as u32),
                TargetArch::x86_64,
            )
            .map_err(|_| invalid())?;
            filter.try_into().map_err(|_| invalid())
        };
    let rule = |length: SeccompCmpArgLen, operator: SeccompCmpOp, value: u64| {
        SeccompCondition::new(0, length, operator, value)
            .and_then(|condition| SeccompRule::new(vec![condition]))
            .map_err(|_| invalid())
    };

    let mut denied = DENIED
        .iter()
        .map(|&number| (number, Vec::new()))
        .collect::<BTreeMap<_, _>>();
    // Landlock scopes signals only from ABI 6, and the service and every child run as one user, so these filters
    // alone keep a child from signaling them on older kernels.
    // Signals may address only this process; kill(0) and kill(-1) reach other processes.
    let own = u64::from(std::process::id());
    for number in [libc::SYS_kill, libc::SYS_tkill, libc::SYS_tgkill] {
        denied.insert(
            number,
            vec![rule(SeccompCmpArgLen::Dword, SeccompCmpOp::Ne, own)?],
        );
    }
    // Limits, CPU affinity and the robust futex list only of this process (pid 0); other processes of the same user
    // stay untouched.
    for number in [
        libc::SYS_prlimit64,
        libc::SYS_sched_setaffinity,
        libc::SYS_get_robust_list,
    ] {
        denied.insert(
            number,
            vec![rule(SeccompCmpArgLen::Dword, SeccompCmpOp::Ne, 0)?],
        );
    }
    // A descriptor's owner receives SIGIO, or any signal F_SETSIG selects, whenever the descriptor becomes ready,
    // sent with this process's credentials; a lease break signals its holder and stalls other openers. No owner can
    // be set and no descriptor made asynchronous; every other status flag stays settable.
    let command = |value: libc::c_int| {
        SeccompCondition::new(1, SeccompCmpArgLen::Dword, SeccompCmpOp::Eq, value as u64)
            .map_err(|_| invalid())
    };
    let mut fcntl = Vec::new();
    for value in [libc::F_SETOWN, F_SETOWN_EX, F_SETSIG, libc::F_SETLEASE] {
        fcntl.push(SeccompRule::new(vec![command(value)?]).map_err(|_| invalid())?);
    }
    let asynchronous = libc::O_ASYNC as u64;
    fcntl.push(
        SeccompRule::new(vec![
            command(libc::F_SETFL)?,
            SeccompCondition::new(
                2,
                SeccompCmpArgLen::Dword,
                SeccompCmpOp::MaskedEq(asynchronous),
                asynchronous,
            )
            .map_err(|_| invalid())?,
        ])
        .map_err(|_| invalid())?,
    );
    denied.insert(libc::SYS_fcntl, fcntl);
    // prctl takes an int option. Inspect its low word so noncanonical upper bits cannot bypass the denial.
    denied.insert(
        libc::SYS_prctl,
        [libc::PR_SET_DUMPABLE, libc::PR_SET_PTRACER]
            .into_iter()
            .map(|option| rule(SeccompCmpArgLen::Dword, SeccompCmpOp::Eq, option as u64))
            .collect::<Result<Vec<_>, _>>()?,
    );
    // Threads only: a clone without CLONE_THREAD, or with any namespace flag, would be a new process.
    let thread = libc::CLONE_THREAD as u64;
    let mut clone = vec![rule(
        SeccompCmpArgLen::Qword,
        SeccompCmpOp::MaskedEq(thread),
        0,
    )?];
    for namespace in [
        libc::CLONE_NEWNS,
        libc::CLONE_NEWCGROUP,
        libc::CLONE_NEWUTS,
        libc::CLONE_NEWIPC,
        libc::CLONE_NEWUSER,
        libc::CLONE_NEWPID,
        libc::CLONE_NEWNET,
    ] {
        let flag = namespace as u32 as u64;
        clone.push(rule(
            SeccompCmpArgLen::Qword,
            SeccompCmpOp::MaskedEq(flag),
            flag,
        )?);
    }
    denied.insert(libc::SYS_clone, clone);
    // Inode flags, project attributes and versions also change through ioctl on any descriptor of the owner, and
    // the ioctl forms of the owner and asynchronous controls, a terminal's process group and terminal input reach
    // other processes; every other ioctl stays available.
    let mut ioctl = Vec::new();
    for request in METADATA_IOCTLS.into_iter().chain(PROCESS_IOCTLS) {
        ioctl.push(
            SeccompCondition::new(1, SeccompCmpArgLen::Dword, SeccompCmpOp::Eq, request)
                .and_then(|condition| SeccompRule::new(vec![condition]))
                .map_err(|_| invalid())?,
        );
    }
    denied.insert(libc::SYS_ioctl, ioctl);

    let mut programs = vec![
        compile(denied, libc::EPERM)?,
        compile(
            BTreeMap::from([(libc::SYS_clone3, Vec::new())]),
            libc::ENOSYS,
        )?,
    ];
    // The x32 ABI shares the x86_64 audit architecture; its numbers would bypass the lists above.
    let x32_guard = [
        sock_filter {
            code: 0x20,
            jt: 0,
            jf: 0,
            k: 0,
        },
        sock_filter {
            code: 0x35,
            jt: 0,
            jf: 1,
            k: 0x4000_0000,
        },
        sock_filter {
            code: 0x06,
            jt: 0,
            jf: 0,
            k: libc::SECCOMP_RET_KILL_PROCESS,
        },
    ];
    // Every jump is relative, so instructions prepended to a complete program keep its targets.
    programs[0].splice(0..0, x32_guard);
    debug_assert_eq!(programs.len(), FILTERS);
    Ok(programs)
}

// Calls newer than the pinned libc's tables; a kernel without them reports ENOSYS.
#[cfg(all(target_os = "linux", target_arch = "x86_64"))]
const SYS_SETXATTRAT: i64 = 463;
#[cfg(all(target_os = "linux", target_arch = "x86_64"))]
const SYS_REMOVEXATTRAT: i64 = 466;
#[cfg(all(target_os = "linux", target_arch = "x86_64"))]
const SYS_OPEN_TREE_ATTR: i64 = 467;
#[cfg(all(target_os = "linux", target_arch = "x86_64"))]
const SYS_FILE_SETATTR: i64 = 469;

// fcntl commands missing from the pinned libc's tables.
#[cfg(all(target_os = "linux", target_arch = "x86_64"))]
const F_SETSIG: libc::c_int = 10;
#[cfg(all(target_os = "linux", target_arch = "x86_64"))]
const F_SETOWN_EX: libc::c_int = 15;

/// `FIOSETOWN`, `SIOCSPGRP`, `FIOASYNC`, `TIOCSPGRP` and `TIOCSTI`.
#[cfg(all(target_os = "linux", target_arch = "x86_64"))]
const PROCESS_IOCTLS: [u64; 5] = [0x8901, 0x8902, 0x5452, 0x5410, 0x5412];

/// `FS_IOC_SETFLAGS`, `FS_IOC32_SETFLAGS`, `FS_IOC_SETVERSION`, `FS_IOC32_SETVERSION` and `FS_IOC_FSSETXATTR`.
#[cfg(all(target_os = "linux", target_arch = "x86_64"))]
const METADATA_IOCTLS: [u64; 5] = [
    0x4008_6602,
    0x4004_6602,
    0x4008_7602,
    0x4004_7602,
    0x401c_5820,
];

#[cfg(all(target_os = "linux", target_arch = "x86_64"))]
const DENIED: &[i64] = &[
    libc::SYS_socket,
    libc::SYS_socketpair,
    libc::SYS_connect,
    libc::SYS_bind,
    libc::SYS_listen,
    libc::SYS_accept,
    libc::SYS_accept4,
    libc::SYS_fork,
    libc::SYS_vfork,
    libc::SYS_execve,
    libc::SYS_execveat,
    libc::SYS_setuid,
    libc::SYS_setgid,
    libc::SYS_setreuid,
    libc::SYS_setregid,
    libc::SYS_setresuid,
    libc::SYS_setresgid,
    libc::SYS_setfsuid,
    libc::SYS_setfsgid,
    libc::SYS_setgroups,
    libc::SYS_capset,
    libc::SYS_ptrace,
    libc::SYS_process_vm_readv,
    libc::SYS_process_vm_writev,
    libc::SYS_process_madvise,
    libc::SYS_process_mrelease,
    libc::SYS_kcmp,
    libc::SYS_pidfd_open,
    libc::SYS_pidfd_getfd,
    libc::SYS_pidfd_send_signal,
    libc::SYS_rt_sigqueueinfo,
    libc::SYS_rt_tgsigqueueinfo,
    libc::SYS_migrate_pages,
    libc::SYS_move_pages,
    libc::SYS_unshare,
    libc::SYS_setns,
    libc::SYS_mount,
    libc::SYS_umount2,
    libc::SYS_pivot_root,
    libc::SYS_chroot,
    libc::SYS_open_tree,
    SYS_OPEN_TREE_ATTR,
    libc::SYS_move_mount,
    libc::SYS_fsopen,
    libc::SYS_fsconfig,
    libc::SYS_fsmount,
    libc::SYS_fspick,
    libc::SYS_mount_setattr,
    libc::SYS_bpf,
    libc::SYS_perf_event_open,
    libc::SYS_userfaultfd,
    libc::SYS_io_uring_setup,
    libc::SYS_io_uring_enter,
    libc::SYS_io_uring_register,
    libc::SYS_keyctl,
    libc::SYS_add_key,
    libc::SYS_request_key,
    libc::SYS_personality,
    libc::SYS_init_module,
    libc::SYS_finit_module,
    libc::SYS_delete_module,
    libc::SYS_kexec_load,
    libc::SYS_kexec_file_load,
    libc::SYS_reboot,
    libc::SYS_swapon,
    libc::SYS_swapoff,
    libc::SYS_acct,
    libc::SYS_quotactl,
    libc::SYS_uselib,
    libc::SYS_name_to_handle_at,
    libc::SYS_open_by_handle_at,
    libc::SYS_setrlimit,
    libc::SYS_setpriority,
    libc::SYS_ioprio_set,
    libc::SYS_sched_setattr,
    libc::SYS_sched_setscheduler,
    libc::SYS_sched_setparam,
    // Landlock does not cover inode metadata: mode, ownership, extended attributes, attributes and timestamps, by
    // path or by descriptor (a read-only descriptor suffices for the descriptor variants).
    libc::SYS_chmod,
    libc::SYS_fchmod,
    libc::SYS_fchmodat,
    libc::SYS_fchmodat2,
    libc::SYS_chown,
    libc::SYS_fchown,
    libc::SYS_lchown,
    libc::SYS_fchownat,
    libc::SYS_setxattr,
    libc::SYS_fsetxattr,
    libc::SYS_lsetxattr,
    libc::SYS_removexattr,
    libc::SYS_fremovexattr,
    libc::SYS_lremovexattr,
    SYS_SETXATTRAT,
    SYS_REMOVEXATTRAT,
    SYS_FILE_SETATTR,
    libc::SYS_utime,
    libc::SYS_utimes,
    libc::SYS_futimesat,
    libc::SYS_utimensat,
    // System V IPC and message queues outlive the process; the service could not remove them.
    libc::SYS_msgget,
    libc::SYS_msgsnd,
    libc::SYS_msgrcv,
    libc::SYS_msgctl,
    libc::SYS_semget,
    libc::SYS_semop,
    libc::SYS_semtimedop,
    libc::SYS_semctl,
    libc::SYS_shmget,
    libc::SYS_shmat,
    libc::SYS_shmctl,
    libc::SYS_shmdt,
    libc::SYS_mq_open,
    libc::SYS_mq_unlink,
    libc::SYS_mq_timedsend,
    libc::SYS_mq_timedreceive,
    libc::SYS_mq_notify,
    libc::SYS_mq_getsetattr,
];

#[cfg(all(test, target_os = "linux", target_arch = "x86_64"))]
mod tests {
    #[test]
    fn exec_and_credential_mutations_are_unconditionally_denied() {
        for syscall in [
            libc::SYS_execve,
            libc::SYS_execveat,
            libc::SYS_setuid,
            libc::SYS_setgid,
            libc::SYS_setreuid,
            libc::SYS_setregid,
            libc::SYS_setresuid,
            libc::SYS_setresgid,
            libc::SYS_setfsuid,
            libc::SYS_setfsgid,
            libc::SYS_setgroups,
            libc::SYS_capset,
        ] {
            assert!(super::DENIED.contains(&syscall));
        }
    }

    #[test]
    fn startup_exit_categories_are_fixed() {
        use super::SandboxError;

        for (error, code) in [
            (SandboxError::Arguments, 80),
            (SandboxError::Platform, 81),
            (SandboxError::Procfs, 82),
            (SandboxError::Descriptors, 83),
            (SandboxError::Limits, 84),
            (SandboxError::NonDumpable, 85),
            (SandboxError::NoNewPrivileges, 86),
            (SandboxError::FilterInvalid, 87),
            (SandboxError::FilterInstall, 88),
            (SandboxError::ReadyMarker, 89),
            (SandboxError::LandlockUnavailable, 90),
            (SandboxError::PathGrant, 91),
            (SandboxError::LandlockRestrict, 92),
            (SandboxError::Confinement, 93),
            (SandboxError::Origin, 94),
            (SandboxError::Execute, 1),
        ] {
            assert_eq!(error.exit_code(), code);
        }
    }

    #[test]
    fn metadata_mutations_are_denied_for_paths_and_file_descriptors() {
        for syscall in [
            libc::SYS_chmod,
            libc::SYS_fchmod,
            libc::SYS_fchmodat,
            libc::SYS_fchmodat2,
            libc::SYS_chown,
            libc::SYS_fchown,
            libc::SYS_lchown,
            libc::SYS_fchownat,
            libc::SYS_setxattr,
            libc::SYS_fsetxattr,
            libc::SYS_lsetxattr,
            libc::SYS_removexattr,
            libc::SYS_fremovexattr,
            libc::SYS_lremovexattr,
            libc::SYS_utime,
            libc::SYS_utimes,
            libc::SYS_futimesat,
            libc::SYS_utimensat,
            super::SYS_SETXATTRAT,
            super::SYS_REMOVEXATTRAT,
            super::SYS_FILE_SETATTR,
        ] {
            assert!(super::DENIED.contains(&syscall));
        }
    }

    /// Calls that can reach another process of the same user; before Landlock ABI 6 only the filter refuses those
    /// that signal it.
    #[test]
    fn calls_that_reach_other_processes_are_denied() {
        for syscall in [
            libc::SYS_ptrace,
            libc::SYS_process_vm_readv,
            libc::SYS_process_vm_writev,
            libc::SYS_process_madvise,
            libc::SYS_process_mrelease,
            libc::SYS_kcmp,
            libc::SYS_pidfd_open,
            libc::SYS_pidfd_getfd,
            libc::SYS_pidfd_send_signal,
            libc::SYS_rt_sigqueueinfo,
            libc::SYS_rt_tgsigqueueinfo,
            libc::SYS_migrate_pages,
            libc::SYS_move_pages,
            libc::SYS_setpriority,
            libc::SYS_ioprio_set,
            libc::SYS_sched_setattr,
            libc::SYS_sched_setscheduler,
            libc::SYS_sched_setparam,
            libc::SYS_perf_event_open,
            libc::SYS_userfaultfd,
            libc::SYS_io_uring_setup,
            libc::SYS_io_uring_enter,
            libc::SYS_io_uring_register,
            libc::SYS_keyctl,
            libc::SYS_add_key,
            libc::SYS_request_key,
            libc::SYS_socket,
            libc::SYS_socketpair,
        ] {
            assert!(super::DENIED.contains(&syscall));
        }
        assert_eq!(super::filters().unwrap().len(), super::FILTERS);
    }
}
