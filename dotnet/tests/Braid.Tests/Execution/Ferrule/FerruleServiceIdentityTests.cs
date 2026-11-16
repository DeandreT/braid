using System.Text.Json;
using Braid.MappingBackends.Workers;

namespace Braid.Tests;

public sealed class FerruleServiceIdentityTests
{
    [ServiceCompileFact]
    public async Task The_image_identity_command_proves_the_sandbox_and_reports_only_the_three_pins()
    {
        var root = FerruleServiceTestSupport.TemporaryRoot();
        try
        {
            await using var expected = new ProcessCompileRunner(FerruleServiceTestSupport.CompileOptions(root));
            var environment = new Dictionary<string, string>(PackagedFerruleService.Compilation)
            {
                ["BRAID_FERRULE_SERVICE_WORKER_EXECUTABLE"] = FerruleServiceTestSupport.Executable!,
                ["BRAID_FERRULE_SERVICE_TEMPORARY_ROOT"] = root,
                ["UNRELATED_PRIVATE_VALUE"] = "must-not-appear"
            };
            using var process = PackagedFerruleService.Launch(environment, "identity");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(70)); }
            finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", await stderr);
            var text = await stdout;
            Assert.DoesNotContain(root, text, StringComparison.Ordinal);
            Assert.DoesNotContain("must-not-appear", text, StringComparison.Ordinal);
            using var identity = JsonDocument.Parse(text);
            Assert.Equal(4, identity.RootElement.EnumerateObject().Count());
            Assert.Equal("braid.compiler-image-identity.v1", identity.RootElement.GetProperty("schema").GetString());
            Assert.Equal(expected.BuildIdentity, identity.RootElement.GetProperty("native").GetString());
            Assert.Equal(expected.ToolchainIdentity, identity.RootElement.GetProperty("toolchain").GetString());
            Assert.Equal(expected.CompilationBuildIdentity, identity.RootElement.GetProperty("build").GetString());
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [ServiceCompileFact]
    public async Task An_unusable_launcher_or_toolchain_fails_closed_with_only_the_fixed_failure_line()
    {
        foreach (var kind in new[] { "missing-launcher", "unproven-launcher", "empty-toolchain" })
        {
            var root = FerruleServiceTestSupport.TemporaryRoot();
            var toolchain = FerruleServiceTestSupport.TemporaryRoot();
            try
            {
                var environment = new Dictionary<string, string>(PackagedFerruleService.Compilation)
                {
                    ["BRAID_FERRULE_SERVICE_WORKER_EXECUTABLE"] = FerruleServiceTestSupport.Executable!,
                    ["BRAID_FERRULE_SERVICE_TEMPORARY_ROOT"] = root
                };
                switch (kind)
                {
                    case "missing-launcher": environment["BRAID_FERRULE_SERVICE_COMPILE_LAUNCHER"] = Path.Combine(root, "absent"); break;
                    // An installed executable that never reports the sandbox ready, so the probe cannot prove it.
                    case "unproven-launcher": environment["BRAID_FERRULE_SERVICE_COMPILE_LAUNCHER"] = "/usr/bin/true"; break;
                    default: environment["BRAID_FERRULE_SERVICE_COMPILE_TOOLCHAIN"] = toolchain; break;
                }
                using var process = PackagedFerruleService.Launch(environment, "identity");
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(70)); }
                finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
                Assert.True(process.ExitCode == 1, kind);
                Assert.Equal("", await stdout);
                Assert.Equal("BraidFerruleServiceIdentityFailed\n", await stderr);
                Assert.Empty(Directory.EnumerateFileSystemEntries(root));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
                Directory.Delete(toolchain, recursive: true);
            }
        }
    }
}
