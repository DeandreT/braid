//! Self-hardening launcher for one operation of the portable Ferrule service that runs a .NET program, such as
//! the compiler of a compile operation.
//!
//! The service writes an operation's trusted inputs (for a compile: the generated sources, the compiler response
//! file and an empty output file) into a fresh private directory, then starts `weft-compile-sandbox` there with
//! fixed limits and the exact program command. Before the program runs, the launcher applies hard resource limits,
//! becomes non-dumpable, sets no-new-privileges, confines the filesystem with Landlock (read-only grants and private
//! directory, write access to at most one existing output file, execute access to the program only, no TCP) and
//! installs syscall filters (no network sockets, no new processes, no tracing, no signals or limits for other
//! processes, no System V IPC or message queues, no namespace or mount operations). It then writes
//! [`READY_MARKER`] and waits for [`GO`], so the service can verify the applied state in procfs before the program
//! starts. Every control survives `execve`.

use std::ffi::{CString, OsString};
use std::fmt;
use std::ops::RangeInclusive;
use std::os::unix::ffi::OsStrExt;

/// Written once to standard output after every control is in force and before the program starts.
pub const READY_MARKER: [u8; 4] = *b"WFC1";
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
const MAXIMUM_ARGUMENTS: usize = 64;

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
    /// Further files the program may execute, such as the .NET host's ELF interpreter.
    pub execute: Vec<CString>,
    /// The absolute program to execute, followed by its arguments.
    pub command: Vec<CString>,
}

#[derive(Debug)]
pub struct SandboxError(&'static str);

impl fmt::Display for SandboxError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(self.0)
    }
}

impl std::error::Error for SandboxError {}

/// Reads the exact argument shape:
/// `--cpu-seconds=N --memory-bytes=N --open-files=N --file-bytes=N [--output=NAME] [--read=PATH]... [--execute=FILE]... -- PROGRAM [ARGUMENT]...`
pub fn parse_arguments(arguments: &[OsString]) -> Result<Launch, SandboxError> {
    let invalid = SandboxError("The launcher arguments are not admitted.");
    let separator = arguments
        .iter()
        .position(|argument| argument == "--")
        .ok_or(SandboxError("The launcher arguments are not admitted."))?;
    let (options, command) = arguments.split_at(separator);
    let command = &command[1..];
    let [cpu, memory, files, file_bytes, rest @ ..] = options else {
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
                        .ok_or(SandboxError("The launcher arguments are not admitted."))?,
                ),
                rest,
            )
        }
        _ => (None, rest),
    };
    let mut read = Vec::new();
    let mut execute = Vec::new();
    for option in rest {
        let text = option
            .to_str()
            .ok_or(SandboxError("The launcher arguments are not admitted."))?;
        // Every read grant precedes every execute grant.
        if let Some(path) = text.strip_prefix("--read=")
            && execute.is_empty()
        {
            read.push(
                absolute(path).ok_or(SandboxError("The launcher arguments are not admitted."))?,
            );
        } else if let Some(path) = text.strip_prefix("--execute=") {
            execute.push(
                absolute(path).ok_or(SandboxError("The launcher arguments are not admitted."))?,
            );
        } else {
            return Err(invalid);
        }
    }
    if read.len() > MAXIMUM_PATHS
        || execute.len() > MAXIMUM_PATHS
        || command.is_empty()
        || command.len() > MAXIMUM_ARGUMENTS
    {
        return Err(invalid);
    }
    let program = command[0]
        .to_str()
        .and_then(absolute)
        .ok_or(SandboxError("The launcher arguments are not admitted."))?;
    let mut owned = vec![program];
    for argument in &command[1..] {
        owned.push(
            CString::new(argument.as_bytes())
                .map_err(|_| SandboxError("The launcher arguments are not admitted."))?,
        );
    }
    Ok(Launch {
        cpu_seconds,
        memory_bytes,
        open_files,
        file_bytes,
        output,
        read,
        execute,
        command: owned,
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
    Err(SandboxError(
        "The compile sandbox requires Linux on x86_64.",
    ))
}

/// Applies every control to the calling single-threaded process. Any failure leaves the caller to exit without
/// starting the program.
#[cfg(all(target_os = "linux", target_arch = "x86_64"))]
pub fn enter(launch: &Launch) -> Result<(), SandboxError> {
    require_standard_descriptors_only()?;
    prefer_out_of_memory_kill();
    let ruleset = landlock::Ruleset::new(launch)?;
    apply_limits(launch)?;
    rustix::process::set_dumpable_behavior(rustix::process::DumpableBehavior::NotDumpable)
        .map_err(|_| SandboxError("The launcher could not become non-dumpable."))?;
    rustix::thread::set_no_new_privs(true)
        .map_err(|_| SandboxError("The launcher could not set no-new-privileges."))?;
    ruleset.restrict_self()?;
    confirm_confinement()?;
    for program in filters()? {
        seccompiler::apply_filter(&program)
            .map_err(|_| SandboxError("The launcher could not install its syscall filter."))?;
    }
    Ok(())
}

/// Replaces the launcher with the program. Returns only on failure.
pub fn execute(launch: &Launch) -> SandboxError {
    let mut arguments = launch
        .command
        .iter()
        .map(|argument| argument.as_ptr())
        .collect::<Vec<_>>();
    arguments.push(std::ptr::null());
    // The service supplied the complete environment; it passes unchanged.
    unsafe extern "C" {
        static environ: *const *const libc::c_char;
    }
    unsafe {
        libc::execve(launch.command[0].as_ptr(), arguments.as_ptr(), environ);
    }
    SandboxError("The launcher could not start the program.")
}

/// The parent must not leak any descriptor beyond the three standard streams.
#[cfg(all(target_os = "linux", target_arch = "x86_64"))]
fn require_standard_descriptors_only() -> Result<(), SandboxError> {
    let failure = SandboxError("The launcher inherited an unexpected file descriptor.");
    let own_listing = std::path::PathBuf::from(format!("/proc/{}/fd", std::process::id()));
    let mut listing_descriptors = 0;
    for entry in
        std::fs::read_dir("/proc/self/fd").map_err(|_| SandboxError("Procfs is unavailable."))?
    {
        let entry = entry.map_err(|_| SandboxError("Procfs is unavailable."))?;
        let descriptor = entry
            .file_name()
            .to_str()
            .and_then(|name| name.parse::<u32>().ok())
            .ok_or(SandboxError("Procfs is unavailable."))?;
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
        setrlimit(resource, fixed(value))
            .map_err(|_| SandboxError("The launcher could not apply its resource limits."))?;
    }
    Ok(())
}

/// The parent of the private directory, like every path without a grant, must be unreadable.
#[cfg(all(target_os = "linux", target_arch = "x86_64"))]
fn confirm_confinement() -> Result<(), SandboxError> {
    match std::fs::File::open("..") {
        Err(error) if error.kind() == std::io::ErrorKind::PermissionDenied => Ok(()),
        _ => Err(SandboxError(
            "The launcher could not confirm its filesystem confinement.",
        )),
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
            let unavailable = SandboxError("Landlock is unavailable.");
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
            for file in launch.execute.iter().chain(&launch.command[..1]) {
                ruleset.add(file, EXECUTE | READ_FILE)?;
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
            let failure = SandboxError("The launcher could not grant a path.");
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
                return Err(SandboxError("The launcher could not enforce Landlock."));
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

    let invalid = || SandboxError("The launcher syscall filter is invalid.");
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
    // Signals may address only this process; kill(0) and kill(-1) reach other processes.
    let own = u64::from(std::process::id());
    for number in [libc::SYS_kill, libc::SYS_tkill, libc::SYS_tgkill] {
        denied.insert(
            number,
            vec![rule(SeccompCmpArgLen::Dword, SeccompCmpOp::Ne, own)?],
        );
    }
    // Limits and CPU affinity only of this process (pid 0); other processes of the same user stay untouched.
    for number in [libc::SYS_prlimit64, libc::SYS_sched_setaffinity] {
        denied.insert(
            number,
            vec![rule(SeccompCmpArgLen::Dword, SeccompCmpOp::Ne, 0)?],
        );
    }
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
    libc::SYS_execveat,
    libc::SYS_ptrace,
    libc::SYS_process_vm_readv,
    libc::SYS_process_vm_writev,
    libc::SYS_process_madvise,
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
    // Landlock does not cover ownership, mode or extended attributes of paths.
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
        ] {
            assert!(super::DENIED.contains(&syscall));
        }
    }
}
