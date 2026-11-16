using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

// Source-only qualification helper. Not a DocumentWorker replacement or a production entrypoint.
// Always install/pin separately and run behind the unchanged Office runtime launcher/profile.
if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return 80;
try
{
    Native.RequireFixedOfficeProfile();
    var buffer = new byte[129];
    var used = 0;
    var input = Console.OpenStandardInput();
    while (used < buffer.Length)
    {
        var read = await input.ReadAsync(buffer.AsMemory(used));
        if (read == 0) break;
        used += read;
    }
    if (used == buffer.Length) return 81;
    var action = new UTF8Encoding(false, true).GetString(buffer, 0, used);
    switch (action)
    {
        case "memory":
            Native.ReservationProbe();
            Console.Write("small=ok large=ENOMEM\n");
            return 0;
        case "overflow":
            // Exactly one byte over the real Office transport cap; no huge managed allocation and no cap change.
            const int maximumOfficeResponseBytes = 10 * 1024 * 1024 + 2048;
            var output = Console.OpenStandardOutput();
            var chunk = new byte[8192];
            Array.Fill(chunk, (byte)'x');
            for (var remaining = maximumOfficeResponseBytes + 1; remaining > 0;)
            {
                var count = Math.Min(chunk.Length, remaining);
                await output.WriteAsync(chunk.AsMemory(0, count));
                remaining -= count;
            }
            await output.FlushAsync();
            return 0;
        case "cpu":
            // Optional future direct-launch witness action. Current test selection DOES NOT use this as a CPU-signal oracle.
            // With the real runner, 30s wall cancellation races the unchanged 30s CPU bound. Never relabel TimedOut as SIGXCPU.
            Console.Write("cpu-start\n"); Console.Out.Flush();
            while (true) Thread.SpinWait(100_000);
        default:
            return 82;
    }
}
catch (Exception error) when (error is IOException or InvalidOperationException or DecoderFallbackException)
{
    // Closed diagnostic: never expose caller bytes, native messages or paths.
    Console.Error.Write("BraidInstalledResourceProbeFailed\n");
    return 83;
}

[SupportedOSPlatform("linux")]
internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Limit { internal ulong Soft; internal ulong Hard; }
    [DllImport("libc", EntryPoint = "prctl", SetLastError = true)]
    private static extern int Prctl(int option, ulong a, ulong b, ulong c, ulong d);
    [DllImport("libc", EntryPoint = "getrlimit", SetLastError = true)]
    private static extern int GetLimit(int resource, out Limit limit);
    [DllImport("libc", EntryPoint = "getuid")]
    private static extern uint User();
    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint EffectiveUser();
    [DllImport("libc", EntryPoint = "mmap", SetLastError = true)]
    private static extern IntPtr Map(IntPtr address, nuint bytes, int protection, int flags, int descriptor, long offset);
    [DllImport("libc", EntryPoint = "munmap", SetLastError = true)]
    private static extern int Unmap(IntPtr address, nuint bytes);

    internal static void RequireFixedOfficeProfile()
    {
        if (User() == 0 || User() != EffectiveUser() || Prctl(3, 0, 0, 0, 0) != 0 ||
            Prctl(39, 0, 0, 0, 0) != 1 || Prctl(21, 0, 0, 0, 0) != 2) throw new InvalidOperationException();
        foreach (var (resource, expected) in new (int, ulong)[] { (0, 30), (1, 1 << 20), (7, 128), (9, 3UL << 30) })
            if (GetLimit(resource, out var value) != 0 || value.Soft != expected || value.Hard != expected)
                throw new InvalidOperationException();
        // These self-checks are an additional guard against accidental portable invocation, never parent admission.
        // The normal parent still checks UID/capabilities, exact filter count, all hard/soft limits and hidden environment.
    }

    internal static void ReservationProbe()
    {
        const nuint smallBytes = 64 * 1024;
        var small = Map(IntPtr.Zero, smallBytes, 3, 0x22, -1, 0); // PROT_READ|WRITE, MAP_PRIVATE|ANONYMOUS.
        if (small == new IntPtr(-1)) throw new InvalidOperationException();
        try
        {
            ulong requestedBytes = 4UL << 30;
            var largeBytes = checked((nuint)requestedBytes); // Runtime x64 guard; avoid a native-int constant-overflow build warning.
            var large = Map(IntPtr.Zero, largeBytes, 3, 0x22, -1, 0);
            var error = Marshal.GetLastPInvokeError();
            if (large != new IntPtr(-1))
            {
                if (Unmap(large, largeBytes) != 0) throw new InvalidOperationException();
                throw new InvalidOperationException();
            }
            if (error != 12) throw new InvalidOperationException(); // ENOMEM, not a generic permission/loader failure.
        }
        finally { if (Unmap(small, smallBytes) != 0) throw new InvalidOperationException(); }
        // No pages of the large reservation are touched: this tests RLIMIT_AS, not RSS, swap exhaustion or a managed OOM.
    }
}
