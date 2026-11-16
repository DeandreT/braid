using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

if (!OperatingSystem.IsLinux()) return 1;
Native.RequirePrivacy();
await Task.Yield(); // The real host must support the asynchronous managed entrypoint as well.
var outside = (await Console.In.ReadToEndAsync()).Trim();
if (outside.Length is < 1 or > 4096) return 1;
// Qualification of post-ready exits: managed code can end the hosted process with any status.
if (outside.StartsWith("exit=", StringComparison.Ordinal) && int.TryParse(outside.AsSpan(5), out var status))
{
    Console.Write("synthetic-private-native-output");
    Console.Error.Write("synthetic-private-native-error");
    Console.Out.Flush();
    Environment.Exit(status);
}
void Attempt(string name, Action action)
{
    try { action(); Console.WriteLine(name + "=allowed"); }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException or SocketException or System.ComponentModel.Win32Exception)
    { Console.WriteLine(name + "=denied"); }
}
Attempt("outside-read", () => _ = File.ReadAllText(outside));
Attempt("private-write", () => File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "new-file"), "not permitted"));
Attempt("metadata", () => File.SetUnixFileMode(typeof(Program).Assembly.Location, UnixFileMode.UserRead | UnixFileMode.UserWrite));
Attempt("network", () => { using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp); });
Attempt("process", () =>
{
    var child = Native.Fork();
    if (child == 0) Native.Exit(0);
    if (child < 0)
    {
        if (Marshal.GetLastPInvokeError() != 1) throw new InvalidOperationException("Unexpected fork failure.");
        throw new System.ComponentModel.Win32Exception(1);
    }
    Native.Wait(child, out _, 0);
});
// A descriptor owner would let the child choose a signal for the service that drains its output.
Attempt("signal-owner", () =>
{
    if (Native.Control(1, Native.SetOwner, Native.Parent()) == 0)
    {
        Native.Control(1, Native.SetOwner, 0);
        return;
    }
    if (Marshal.GetLastPInvokeError() != 1) throw new InvalidOperationException("Unexpected fcntl failure.");
    throw new System.ComponentModel.Win32Exception(1);
});
Console.WriteLine(Environment.GetEnvironmentVariable("BRAID_RUNTIME_PROBE_SECRET") is null ? "environment=clean" : "environment=inherited");
Native.RequirePrivacy();
Console.WriteLine("privacy=sealed");
return 0;

internal static class Native
{
    [DllImport("libc", EntryPoint = "prctl", SetLastError = true)] internal static extern int Prctl(int option, ulong a, ulong b, ulong c, ulong d);
    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)] internal static extern long Syscall(long number, ulong a, ulong b, ulong c, ulong d, ulong e, ulong f);

    internal static void RequirePrivacy()
    {
        if (Prctl(3, 0, 0, 0, 0) != 0) throw new InvalidOperationException("Managed process is dumpable.");
        void Denied(long result)
        {
            if (result != -1 || Marshal.GetLastPInvokeError() != 1) throw new InvalidOperationException("Privacy mutation was not denied.");
        }
        foreach (var option in new ulong[] { 4, (1UL << 32) | 4, 0xffff_ffff_0000_0004, 0x59616d61, (1UL << 32) | 0x59616d61 })
            foreach (var value in (option & uint.MaxValue) == 4 ? new ulong[] { 0, 1 } : new ulong[] { 0 }) Denied(Syscall(157, option, value, 0, 0, 0, 0));
        // x86-64: invalid exec pointers would otherwise produce a different errno.
        foreach (var syscall in new long[] { 59, 322 })
            Denied(Syscall(syscall, 0, 0, 0, 0, 0, 0));
        var uid = (ulong)User(); var gid = (ulong)Group();
        // Same-ID setters and a valid empty capset are harmless, but would succeed without the filter.
        foreach (var (syscall, id) in new (long, ulong)[] { (105, uid), (106, gid), (113, uid), (114, gid), (117, uid), (119, gid), (122, uid), (123, gid) })
            Denied(Syscall(syscall, id, id, id, 0, 0, 0));
        Denied(Syscall(116, 0, 0, 0, 0, 0, 0));
        var header = Marshal.AllocHGlobal(8); var capabilities = Marshal.AllocHGlobal(24);
        try
        {
            Marshal.WriteInt32(header, 0, 0x20080522); Marshal.WriteInt32(header, 4, 0);
            for (var offset = 0; offset < 24; offset += 4) Marshal.WriteInt32(capabilities, offset, 0);
            Denied(Syscall(126, (ulong)header.ToInt64(), (ulong)capabilities.ToInt64(), 0, 0, 0, 0));
        }
        finally { Marshal.FreeHGlobal(header); Marshal.FreeHGlobal(capabilities); }
        var thread = new Thread(() =>
        {
            Denied(Prctl(4, 1, 0, 0, 0));
            if (Prctl(3, 0, 0, 0, 0) != 0) throw new InvalidOperationException("Managed thread is dumpable.");
        });
        thread.Start(); thread.Join();
        if (Prctl(3, 0, 0, 0, 0) != 0) throw new InvalidOperationException("Managed process privacy changed.");
    }
    [DllImport("libc", EntryPoint = "fork", SetLastError = true)] internal static extern int Fork();
    [DllImport("libc", EntryPoint = "getuid")] internal static extern uint User();
    [DllImport("libc", EntryPoint = "getgid")] internal static extern uint Group();
    [DllImport("libc", EntryPoint = "_exit")] internal static extern void Exit(int code);
    [DllImport("libc", EntryPoint = "waitpid")] internal static extern int Wait(int pid, out int status, int flags);
    internal const int SetOwner = 8;
    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)] internal static extern int Control(int descriptor, int command, int argument);
    [DllImport("libc", EntryPoint = "getppid")] internal static extern int Parent();
}
