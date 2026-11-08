// Synthetic qualification entrypoint, not a production privacy repair.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;

internal static class PrivacyProbe
{
    [DllImport("libc", EntryPoint = "prctl", SetLastError = true)]
    private static extern int Prctl(int option, ulong a, ulong b, ulong c, ulong d);
    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern long Syscall(long number, ulong a, ulong b, ulong c, ulong d, ulong e, ulong f);

    private static (long Result, int Errno) Attempt(Func<long> action)
    {
        var result = action();
        return (result, result < 0 ? Marshal.GetLastPInvokeError() : 0);
    }

    private static void Report(string phase, IntPtr memory, Dictionary<string, object> extra)
    {
        var status = File.ReadAllLines("/proc/self/status");
        extra["phase"] = phase; extra["pid"] = Environment.ProcessId;
        extra["dump"] = Prctl(3, 0, 0, 0, 0); extra["address"] = memory.ToInt64();
        extra["exe"] = Environment.ProcessPath ?? "";
        foreach (var name in new[] { "Uid:", "Gid:", "TracerPid:", "CapInh:", "CapPrm:", "CapEff:", "CapAmb:" })
            extra[name] = Array.Find(status, line => line.StartsWith(name, StringComparison.Ordinal))?[name.Length..].Trim() ?? "missing";
        Console.WriteLine(JsonSerializer.Serialize(extra)); Console.Out.Flush();
    }

    private static int Main(string[] args)
    {
        var control = args.Length == 1 && args[0] == "control";
        if (args.Length != 0 && !control) return 2;
        var memory = Marshal.AllocHGlobal(16);
        try
        {
            for (var i = 0; i < 16; i++) Marshal.WriteByte(memory, i, 0x51);
            Report("managed-start", memory, new());
            if (Console.ReadLine() != "M") return 3;
            var mutations = new Dictionary<string, object>();
            if (!control)
            {
                foreach (var option in new ulong[] { 4, (1UL << 32) | 4, 0xffff_ffff_0000_0004, 0x59616d61, (1UL << 32) | 0x59616d61 })
                    foreach (var value in (option & uint.MaxValue) == 4 ? new ulong[] { 0, 1 } : new ulong[] { 0 })
                    {
                        var result = Attempt(() => Syscall(157, option, value, 0, 0, 0, 0));
                        mutations[$"{option}:{value}"] = new[] { result.Result, result.Errno };
                    }
                var thread = new Thread(() =>
                {
                    var result = Attempt(() => Prctl(4, 1, 0, 0, 0));
                    mutations["thread"] = new[] { result.Result, result.Errno };
                    mutations["thread-dump"] = Prctl(3, 0, 0, 0, 0);
                });
                thread.Start(); thread.Join();
            }
            Report("after-mutations", memory, new() { ["mutations"] = mutations });
            return Console.ReadLine() == "Q" ? 0 : 4;
        }
        finally { Marshal.FreeHGlobal(memory); }
    }
}
