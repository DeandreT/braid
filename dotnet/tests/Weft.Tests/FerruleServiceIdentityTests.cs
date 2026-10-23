using System.Text.Json;
using Weft.MappingBackends.Workers;

namespace Weft.Tests;

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
                ["WEFT_FERRULE_SERVICE_WORKER_EXECUTABLE"] = FerruleServiceTestSupport.Executable!,
                ["WEFT_FERRULE_SERVICE_TEMPORARY_ROOT"] = root,
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
            Assert.Equal("weft.compiler-image-identity.v1", identity.RootElement.GetProperty("schema").GetString());
            Assert.Equal(expected.BuildIdentity, identity.RootElement.GetProperty("native").GetString());
            Assert.Equal(expected.ToolchainIdentity, identity.RootElement.GetProperty("toolchain").GetString());
            Assert.Equal(expected.CompilationBuildIdentity, identity.RootElement.GetProperty("build").GetString());
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
