using Braid.Application.Regression;
using Braid.MappingBackends.Workers;

namespace Braid.MappingBackends;

/// <summary>Trusted local installation only. No request can select the executable, mode or budgets.</summary>
public sealed class LocalXmlComparisonHost : IAsyncDisposable
{
    private readonly ProcessCompiledRuntimeRunner runner;
    public SavedXmlComparison Comparison { get; }

    public LocalXmlComparisonHost(string identity, string assembly, string launcher, string temporaryRoot, string toolchain)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        runner = new(new(identity, assembly, launcher, temporaryRoot, toolchain, TimeSpan.FromSeconds(15))
            { Operation = CompiledRuntimeOperation.XmlCanonicalComparison });
        Comparison = new(new XmlCanonicalComparisonWorker(runner, new(new(2 * Original, 32), new(Original, 4 * Original, 8 * Original))));
    }

    private const int Original = 256 * 1024;
    public string RuntimeIdentity => runner.RuntimeIdentity;
    public ValueTask DisposeAsync() => runner.DisposeAsync();
}
