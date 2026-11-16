using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Braid.StorageProtocolProbe;

/// <summary>A test-only restriction installed exclusively inside the owned child.</summary>
[SupportedOSPlatform("linux")]
internal static class ProbeKernelFaults
{
    private const int SetNoNewPrivileges = 38;
    private const long SeccompSyscall = 317;
    private const uint SetModeFilter = 1;
    private const uint SynchronizeThreads = 1;
    private const uint AuditArchitectureX64 = 0xc000003e;
    private const uint FlockSyscall = 73;
    private const int PermissionDenied = 1; // EPERM
    private const int BadDescriptor = 9; // EBADF
    private const int ExclusiveNonblocking = 2 | 4;
    private const ushort LoadWordAbsolute = 0x20;
    private const ushort JumpEqualConstant = 0x15;
    private const ushort ReturnConstant = 0x06;
    private const uint ReturnErrno = 0x00050000;
    private const uint ReturnAllow = 0x7fff0000;

    internal static bool DenyFlock()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64 ||
            IntPtr.Size != 8 || Marshal.SizeOf<FilterInstruction>() != 8 || Marshal.SizeOf<FilterProgram>() != 16 ||
            Marshal.OffsetOf<FilterProgram>(nameof(FilterProgram.Instructions)).ToInt64() != 8)
            throw new PlatformNotSupportedException();

        // This harmless real syscall proves the unfiltered error first. An
        // invalid descriptor cannot lock a file or affect the parent process.
        RequireFlockError(BadDescriptor);
        FilterInstruction[] instructions =
        [
            new(LoadWordAbsolute, 0, 0, 4), // seccomp_data.arch
            new(JumpEqualConstant, 1, 0, AuditArchitectureX64),
            new(ReturnConstant, 0, 0, ReturnErrno | (uint)PermissionDenied),
            new(LoadWordAbsolute, 0, 0, 0), // seccomp_data.nr
            new(JumpEqualConstant, 0, 1, FlockSyscall),
            new(ReturnConstant, 0, 0, ReturnErrno | (uint)PermissionDenied),
            new(ReturnConstant, 0, 0, ReturnAllow)
        ];
        var pinned = GCHandle.Alloc(instructions, GCHandleType.Pinned);
        try
        {
            var program = new FilterProgram(checked((ushort)instructions.Length), pinned.AddrOfPinnedObject());
            if (Prctl(SetNoNewPrivileges, 1, 0, 0, 0) != 0 ||
                Seccomp(SeccompSyscall, SetModeFilter, SynchronizeThreads, ref program) != 0)
                throw new InvalidOperationException("The owned kernel filter was not installed.");
        }
        finally { pinned.Free(); }

        // ERRNO returns before syscall execution. The changed actual native
        // result proves this is kernel enforcement, never a managed fake.
        RequireFlockError(PermissionDenied);
        return true;
    }

    private static void RequireFlockError(int expected)
    {
        var result = Flock(-1, ExclusiveNonblocking);
        var error = Marshal.GetLastPInvokeError();
        if (result != -1 || error != expected)
            throw new InvalidOperationException("The owned kernel filter observation did not match.");
    }

    // Linux UAPI sock_filter/sock_fprog, seccomp_data and x86_64 syscall ABI.
    // https://www.kernel.org/doc/html/latest/userspace-api/seccomp_filter.html
    // https://github.com/torvalds/linux/blob/master/include/uapi/linux/filter.h
    // https://github.com/torvalds/linux/blob/master/include/uapi/linux/seccomp.h
    // https://github.com/torvalds/linux/blob/master/arch/x86/entry/syscalls/syscall_64.tbl
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FilterInstruction(ushort code, byte jumpTrue, byte jumpFalse, uint value)
    {
        internal readonly ushort Code = code;
        internal readonly byte JumpTrue = jumpTrue;
        internal readonly byte JumpFalse = jumpFalse;
        internal readonly uint Value = value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FilterProgram(ushort length, IntPtr instructions)
    {
        internal readonly ushort Length = length;
        internal readonly IntPtr Instructions = instructions;
    }

    [DllImport("libc", EntryPoint = "prctl", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Prctl(int option, nuint argument2, nuint argument3, nuint argument4, nuint argument5);

    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern long Seccomp(long number, nuint operation, nuint flags, ref FilterProgram program);

    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Flock(int descriptor, int operation);
}
