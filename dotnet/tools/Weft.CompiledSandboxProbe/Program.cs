using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

if (!OperatingSystem.IsLinux()) return 1;
var outside = (await Console.In.ReadToEndAsync()).Trim();
if (outside.Length is < 1 or > 4096) return 1;
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
Console.WriteLine(Environment.GetEnvironmentVariable("WEFT_RUNTIME_PROBE_SECRET") is null ? "environment=clean" : "environment=inherited");
return 0;

internal static class Native
{
    [DllImport("libc", EntryPoint = "fork", SetLastError = true)] internal static extern int Fork();
    [DllImport("libc", EntryPoint = "_exit")] internal static extern void Exit(int code);
    [DllImport("libc", EntryPoint = "waitpid")] internal static extern int Wait(int pid, out int status, int flags);
    internal const int SetOwner = 8;
    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)] internal static extern int Control(int descriptor, int command, int argument);
    [DllImport("libc", EntryPoint = "getppid")] internal static extern int Parent();
}
