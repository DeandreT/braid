//! Opt-in self-hardening for one request of the portable Ferrule service.
//!
//! The service starts every request as a fresh `ferrule-host --service-sandbox ...` process with an empty
//! environment and a private empty working directory. Before any request byte is read, the process applies
//! its fixed resource limits, becomes non-dumpable, sets no-new-privileges and installs syscall filters that
//! deny network sockets, process and thread creation, exec, tracing, signals to other processes, file opening
//! and filesystem changes. It then writes [`READY_MARKER`], so the parent can verify the applied state in
//! procfs before it sends the request. The Docker worker never selects this mode.

use std::ffi::OsString;
use std::fmt;

pub const FLAG: &str = "--service-sandbox";
/// Written once to standard output after every control is in force and before any input is read.
pub const READY_MARKER: [u8; 4] = *b"WFS1";

const CPU_SECONDS: std::ops::RangeInclusive<u64> = 1..=600;
const MEMORY_BYTES: std::ops::RangeInclusive<u64> = (16 << 20)..=(8 << 30);
const OPEN_FILES: std::ops::RangeInclusive<u64> = 8..=1024;

/// Hard limits selected by the trusted service profile, never by a request.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Limits {
    pub cpu_seconds: u64,
    pub memory_bytes: u64,
    pub open_files: u64,
}

#[derive(Debug)]
pub struct SandboxError(&'static str);

impl fmt::Display for SandboxError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(self.0)
    }
}

impl std::error::Error for SandboxError {}

/// Reads the exact argument shape. No arguments selects the unchanged container mode.
pub fn parse_arguments(arguments: &[OsString]) -> Result<Option<Limits>, SandboxError> {
    let invalid = SandboxError("The worker arguments are not admitted.");
    let [flag, cpu, memory, files] = match arguments {
        [] => return Ok(None),
        [flag, cpu, memory, files] => [flag, cpu, memory, files],
        _ => return Err(invalid),
    };
    let value = |argument: &OsString, name: &str, range: std::ops::RangeInclusive<u64>| {
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
    if flag != FLAG {
        return Err(invalid);
    }
    match (
        value(cpu, "--cpu-seconds=", CPU_SECONDS),
        value(memory, "--memory-bytes=", MEMORY_BYTES),
        value(files, "--open-files=", OPEN_FILES),
    ) {
        (Some(cpu_seconds), Some(memory_bytes), Some(open_files)) => Ok(Some(Limits {
            cpu_seconds,
            memory_bytes,
            open_files,
        })),
        _ => Err(invalid),
    }
}

#[cfg(not(all(target_os = "linux", target_arch = "x86_64")))]
pub fn enter(_limits: &Limits) -> Result<(), SandboxError> {
    Err(SandboxError(
        "The service sandbox requires Linux on x86_64.",
    ))
}

/// Applies every control to the calling single-threaded process. Any failure leaves the caller to exit
/// without reading input.
#[cfg(all(target_os = "linux", target_arch = "x86_64"))]
pub fn enter(limits: &Limits) -> Result<(), SandboxError> {
    require_standard_descriptors_only()?;
    apply_limits(limits)?;
    rustix::process::set_dumpable_behavior(rustix::process::DumpableBehavior::NotDumpable)
        .map_err(|_| SandboxError("The worker could not become non-dumpable."))?;
    rustix::thread::set_no_new_privs(true)
        .map_err(|_| SandboxError("The worker could not set no-new-privileges."))?;
    for program in filters()? {
        seccompiler::apply_filter(&program)
            .map_err(|_| SandboxError("The worker could not install its syscall filter."))?;
    }
    Ok(())
}

/// The parent must not leak any descriptor beyond the three standard streams.
#[cfg(all(target_os = "linux", target_arch = "x86_64"))]
fn require_standard_descriptors_only() -> Result<(), SandboxError> {
    let failure = SandboxError("The worker inherited an unexpected file descriptor.");
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

#[cfg(all(target_os = "linux", target_arch = "x86_64"))]
fn apply_limits(limits: &Limits) -> Result<(), SandboxError> {
    use rustix::process::{Resource, Rlimit, setrlimit};
    let fixed = |value: u64| Rlimit {
        current: Some(value),
        maximum: Some(value),
    };
    for (resource, value) in [
        (Resource::Cpu, limits.cpu_seconds),
        (Resource::As, limits.memory_bytes),
        (Resource::Nofile, limits.open_files),
        // No process or thread of this user can be created by the worker; the filter enforces it again.
        (Resource::Nproc, 1),
        (Resource::Fsize, 0),
        (Resource::Core, 0),
        (Resource::Memlock, 0),
        (Resource::Msgqueue, 0),
        (Resource::Locks, 0),
        (Resource::Rtprio, 0),
    ] {
        setrlimit(resource, fixed(value))
            .map_err(|_| SandboxError("The worker could not apply its resource limits."))?;
    }
    Ok(())
}

/// Three disjoint deny lists, each allowing every other syscall, so each group keeps its own errno.
#[cfg(all(target_os = "linux", target_arch = "x86_64"))]
fn filters() -> Result<Vec<seccompiler::BpfProgram>, SandboxError> {
    use std::collections::BTreeMap;

    use seccompiler::{
        BpfProgram, SeccompAction, SeccompCmpArgLen, SeccompCmpOp, SeccompCondition, SeccompFilter,
        SeccompRule, TargetArch, sock_filter,
    };

    let compile =
        |rules: BTreeMap<i64, Vec<SeccompRule>>, errno: i32| -> Result<BpfProgram, SandboxError> {
            let filter = SeccompFilter::new(
                rules,
                SeccompAction::Allow,
                SeccompAction::Errno(errno as u32),
                TargetArch::x86_64,
            )
            .map_err(|_| SandboxError("The worker syscall filter is invalid."))?;
            let program: BpfProgram = filter
                .try_into()
                .map_err(|_| SandboxError("The worker syscall filter is invalid."))?;
            Ok(program)
        };
    let unconditional = |numbers: &[i64]| {
        numbers
            .iter()
            .map(|&number| (number, Vec::new()))
            .collect::<BTreeMap<_, _>>()
    };

    let mut denied = unconditional(DENIED);
    // Signals may address only this process; kill(0) and kill(-1) reach other processes.
    let own = u64::from(std::process::id());
    let other_target = || -> Result<Vec<SeccompRule>, SandboxError> {
        let condition = SeccompCondition::new(0, SeccompCmpArgLen::Dword, SeccompCmpOp::Ne, own)
            .map_err(|_| SandboxError("The worker syscall filter is invalid."))?;
        Ok(vec![SeccompRule::new(vec![condition]).map_err(|_| {
            SandboxError("The worker syscall filter is invalid.")
        })?])
    };
    for number in [libc::SYS_kill, libc::SYS_tkill, libc::SYS_tgkill] {
        denied.insert(number, other_target()?);
    }

    let mut programs = vec![
        compile(denied, libc::EPERM)?,
        // Opening behaves as if the filesystem held nothing, like the worker's empty image.
        compile(unconditional(OPENING), libc::ENOENT)?,
        // Changes behave as on a read-only root.
        compile(unconditional(CHANGING), libc::EROFS)?,
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
    libc::SYS_clone,
    libc::SYS_clone3,
    libc::SYS_execve,
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
    libc::SYS_name_to_handle_at,
    libc::SYS_open_by_handle_at,
    libc::SYS_setrlimit,
    libc::SYS_setpriority,
    libc::SYS_ioprio_set,
    libc::SYS_sched_setattr,
    libc::SYS_sched_setscheduler,
    libc::SYS_sched_setparam,
];

#[cfg(all(target_os = "linux", target_arch = "x86_64"))]
const OPENING: &[i64] = &[libc::SYS_open, libc::SYS_openat, libc::SYS_openat2];

#[cfg(all(target_os = "linux", target_arch = "x86_64"))]
const CHANGING: &[i64] = &[
    libc::SYS_creat,
    libc::SYS_mkdir,
    libc::SYS_mkdirat,
    libc::SYS_mknod,
    libc::SYS_mknodat,
    libc::SYS_unlink,
    libc::SYS_unlinkat,
    libc::SYS_rmdir,
    libc::SYS_rename,
    libc::SYS_renameat,
    libc::SYS_renameat2,
    libc::SYS_link,
    libc::SYS_linkat,
    libc::SYS_symlink,
    libc::SYS_symlinkat,
    libc::SYS_truncate,
    libc::SYS_chmod,
    libc::SYS_fchmodat,
    libc::SYS_chown,
    libc::SYS_lchown,
    libc::SYS_fchownat,
    libc::SYS_utime,
    libc::SYS_utimes,
    libc::SYS_utimensat,
    libc::SYS_futimesat,
    libc::SYS_setxattr,
    libc::SYS_lsetxattr,
    libc::SYS_removexattr,
    libc::SYS_lremovexattr,
];
