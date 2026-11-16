using System.Security.Cryptography;
using System.Text.Json;
using Braid.Deployment;
using Braid.MappingBackends.Workers;

namespace Braid.FerruleService;

internal static class FerruleRuntimeIdentity
{
    internal static async Task<int> RunAsync(Func<string, string?> read)
    {
        try
        {
            var worker = read(FerruleServiceSettings.WorkerExecutableSetting) ?? "/ferrule-host";
            string native;
            using (var input = File.OpenRead(worker)) native = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(input));
            string identity;
            await using (var runner = new ProcessCompiledRuntimeRunner(new(native,
                read(FerruleServiceSettings.RuntimeAssemblySetting) ?? "/runtime/braid-compiled-runtime.dll",
                read(FerruleServiceSettings.RuntimeLauncherSetting) ?? "/braid-compile-sandbox",
                read(FerruleServiceSettings.TemporaryRootSetting) ?? "/tmp",
                read(FerruleServiceSettings.RuntimeRootSetting) ?? BuildCompilation.DotnetRoot)))
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                if (!await runner.ProbeAsync(deadline.Token)) throw new InvalidOperationException();
                identity = runner.RuntimeIdentity;
            }
            Console.WriteLine(JsonSerializer.Serialize(new { schema = "braid.compiled-runtime-image-identity.v1", native, runtime = identity }));
            return 0;
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            Console.Error.WriteLine("BraidFerruleRuntimeIdentityFailed");
            return 1;
        }
    }
}
