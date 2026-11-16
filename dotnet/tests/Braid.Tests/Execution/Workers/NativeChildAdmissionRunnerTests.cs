using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using Braid.Deployment;
using Braid.MappingBackends.Workers;
using static Braid.Tests.FerruleServiceTestSupport;

namespace Braid.Tests;

[Collection(FerruleServiceProcessCollection.Name)]
public sealed class NativeChildAdmissionRunnerTests
{
    [Theory]
    [MemberData(nameof(StartupMarkers))]
    public async Task Worker_marker_refusals_preserve_only_the_fixed_startup_category(string marker, int code, string category)
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = TemporaryRoot();
        using var installation = new FerruleServiceCompileTests.FakeInstallation();
        try
        {
            var worker = installation.Script("worker", StartupScript(Marker(marker, "WFS"), code));
            await using var runner = new ProcessWorkerRunner(new(worker, DockerWorkerProfile.Inspection, root));
            var result = await runner.RunAsync(new byte[] { (byte)'Q' }, default);
            AssertRefusal(result, category);
            Assert.True(runner.Available);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [ServiceCompileTheory]
    [MemberData(nameof(StartupMarkers))]
    public async Task Compile_marker_refusals_propagate_through_the_real_generation_stage(string marker, int code, string category)
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = TemporaryRoot();
        using var installation = new FerruleServiceCompileTests.FakeInstallation();
        try
        {
            var launcher = NativeStartup(installation, Marker(marker, "WFC"), code);
            await using var runner = new ProcessCompileRunner(new(Executable!, launcher, root, installation.Toolchain));
            var result = await runner.RunAsync(BuildFrame("valid-basic", "compile"), default);
            AssertRefusal(result, category);
            Assert.True(runner.Available);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [MemberData(nameof(StartupMarkers))]
    public async Task Runtime_operation_and_probe_marker_refusals_preserve_only_the_fixed_startup_category(string marker, int code, string category)
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = TemporaryRoot();
        using var installation = new FerruleServiceCompileTests.FakeInstallation();
        try
        {
            var launcher = NativeStartup(installation, Marker(marker, "WFC"), code);
            var entry = RuntimeEntry(installation, "synthetic runtime entry");
            await using var runner = new ProcessCompiledRuntimeRunner(new(NativeIdentity, entry, launcher, root, installation.Toolchain));
            var result = await runner.RunAsync(new byte[] { (byte)'Q' }, default);
            AssertRefusal(result, category);
            Assert.True(runner.Available);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
            var probe = await runner.ProbeResultAsync(default);
            AssertRefusal(probe, category);
            Assert.True(runner.Available);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [ServiceCompileTheory]
    [MemberData(nameof(PostReadyExitCodes))]
    public async Task Runtime_reserved_exits_after_verified_ready_are_worker_failures_not_admission_failures(int code)
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = TemporaryRoot();
        try
        {
            // The pinned host runs a real managed entry in-process only after the verified BFC2/go handshake;
            // that entry then ends the hosted process with the chosen status.
            var entry = Path.Combine(AppContext.BaseDirectory, "CompiledSandboxProbe", "Braid.CompiledSandboxProbe.dll");
            await using var runner = new ProcessCompiledRuntimeRunner(new(PackagedFerruleService.Identity, entry, Launcher!, root, Toolchain));
            var result = await runner.RunAsync(System.Text.Encoding.ASCII.GetBytes("exit=" + code.ToString(CultureInfo.InvariantCulture)), default);
            Assert.Equal(ChildAdmissionFailure.None, result.AdmissionFailure);
            if (code == 0)
            {
                Assert.Equal(WorkerProcessFailure.None, result.Failure);
                Assert.True(result.Output.AsSpan().SequenceEqual("synthetic-private-native-output"u8), "The runtime stand-in did not complete its fixed control frame.");
            }
            else AssertWorkerFailure(result);
            Assert.True(runner.Available);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    public static IEnumerable<object[]> StartupMarkers()
    {
        foreach (var marker in new[] { "wrong", "partial", "absent" })
            foreach (var (code, category) in StartupCodes)
                yield return [marker, code, marker == "wrong" ? "marker-mismatch" : category];
    }

    public static IEnumerable<object[]> PostReadyExitCodes() =>
        Enumerable.Range(80, 15).Prepend(0).Select(code => new object[] { code });

    private static (int Code, string Category)[] StartupCodes =>
    [
        (80, "marker-missing/arguments"),
        (81, "marker-missing/platform"),
        (82, "marker-missing/descriptor-inspection"),
        (83, "marker-missing/inherited-descriptors"),
        (84, "marker-missing/resource-limits"),
        (85, "marker-missing/non-dumpable"),
        (86, "marker-missing/no-new-privileges"),
        (87, "marker-missing/filter-construction"),
        (88, "marker-missing/filter-installation"),
        (89, "marker-missing/marker-write"),
        (90, "marker-missing/landlock-unavailable"),
        (91, "marker-missing/path-grant"),
        (92, "marker-missing/landlock-restriction"),
        (93, "marker-missing/filesystem-confinement"),
        (94, "marker-missing/unprivileged-origin"),
        (0, "marker-missing/unknown-startup"),
        (1, "marker-missing/unknown-startup"),
        (79, "marker-missing/unknown-startup"),
        (95, "marker-missing/unknown-startup"),
        (137, "marker-missing/unknown-startup"),
        (255, "marker-missing/unknown-startup")
    ];

    private static string NativeIdentity => "sha256:" + new string('a', 64);

    private static string Marker(string kind, string prefix) => kind switch
    {
        "wrong" => prefix + "0",
        "partial" => prefix,
        "absent" => "",
        _ => throw new ArgumentException("Unknown synthetic marker kind.", nameof(kind))
    };

    private static string StartupScript(string marker, int code) =>
        "#!/bin/sh\nprintf '%s' '" + marker + "'\nprintf '%s' synthetic-private-native-error >&2\nexit " +
        code.ToString(CultureInfo.InvariantCulture) + "\n";

    // A launcher must be a native ELF image (a shell script is refused before it runs), so build a tiny one.
    [SupportedOSPlatform("linux")]
    private static string NativeStartup(FerruleServiceCompileTests.FakeInstallation installation, string marker, int code)
    {
        var source = Path.Combine(installation.Directory, "launcher.c");
        var launcher = Path.Combine(installation.Directory, "launcher");
        File.WriteAllText(source, "#include <unistd.h>\nint main(void) { static const char marker[] = \"" + marker +
            "\", error[] = \"synthetic-private-native-error\"; write(1, marker, sizeof marker - 1); write(2, error, sizeof error - 1); return " +
            code.ToString(CultureInfo.InvariantCulture) + "; }\n");
        using var compiler = Process.Start(new ProcessStartInfo("cc", ["-O0", "-o", launcher, source]) { RedirectStandardError = true })!;
        var diagnostics = compiler.StandardError.ReadToEnd();
        compiler.WaitForExit();
        Assert.True(compiler.ExitCode == 0, diagnostics);
        return launcher;
    }

    [SupportedOSPlatform("linux")]
    private static string RuntimeEntry(FerruleServiceCompileTests.FakeInstallation installation, string content)
    {
        var directory = Path.Combine(installation.Directory, "runtime");
        Directory.CreateDirectory(directory);
        var entry = Path.Combine(directory, "entry.dll");
        File.WriteAllText(entry, content);
        return entry;
    }

    private static void AssertRefusal(WorkerProcessResult result, string category)
    {
        Assert.Equal(WorkerProcessFailure.SandboxUnavailable, result.Failure);
        Assert.Equal(category, ChildAdmissionDiagnostics.Category(result.AdmissionFailure));
        Assert.True(result.Output.Length == 0, "An admission refusal exposed native output.");
        NativeChildAdmissionDiagnosticsTests.AssertPublicResultUnchanged(result);
    }

    private static void AssertWorkerFailure(WorkerProcessResult result)
    {
        Assert.Equal(WorkerProcessFailure.WorkerFailed, result.Failure);
        Assert.Equal(ChildAdmissionFailure.None, result.AdmissionFailure);
        Assert.Equal("none", ChildAdmissionDiagnostics.Category(result.AdmissionFailure));
        Assert.True(result.Output.Length == 0, "A post-ready failure exposed native output.");
        NativeChildAdmissionDiagnosticsTests.AssertPublicResultUnchanged(result);
    }
}
