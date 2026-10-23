using System.Text.Json;
using Weft.Deployment;
using Weft.MappingBackends.Workers;

namespace Weft.FerruleService;

/// <summary>Measures a trusted installed image and proves its compiler sandbox without starting a listener.</summary>
internal static class FerruleServiceIdentity
{
    internal static async Task<int> RunAsync(Func<string, string?> read)
    {
        try
        {
            await using var runner = new ProcessCompileRunner(new(
                read(FerruleServiceSettings.WorkerExecutableSetting) ?? "/ferrule-host",
                read(FerruleServiceSettings.CompileLauncherSetting) ?? "/weft-compile-sandbox",
                read(FerruleServiceSettings.TemporaryRootSetting) ?? "/tmp",
                read(FerruleServiceSettings.CompileToolchainSetting) ?? BuildCompilation.DotnetRoot));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            if (!await runner.ProbeAsync(deadline.Token)) throw new InvalidOperationException();
            Console.Out.WriteLine(JsonSerializer.Serialize(new
            {
                schema = "weft.compiler-image-identity.v1",
                native = runner.BuildIdentity,
                toolchain = runner.ToolchainIdentity,
                build = runner.CompilationBuildIdentity
            }));
            return 0;
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            Console.Error.WriteLine("WeftFerruleServiceIdentityFailed");
            return 1;
        }
    }
}
