using System.Security.Cryptography;
using Weft.Application;
using Weft.Domain;

namespace Weft.MappingBackends.Workers;

/// <summary>Trusted installation composition. Requests provide only captured bytes and bounded extraction limits.
/// Reuses the .NET runtime launcher, procfs admission, deadlines, output limits and owned shutdown of the compiled runner.</summary>
public sealed class QualifiedOfficeProcessWorker : ISpecOfficeExtractionWorker, IAsyncDisposable
{
    private readonly ProcessCompiledRuntimeRunner? runner;
    private readonly SemaphoreSlim concurrency = new(1, 1);
    private QualifiedOfficeProcessWorker(ProcessCompiledRuntimeRunner? runner, string? refusal)
    { this.runner = runner; Refusal = refusal; }
    public bool Available => runner?.Available == true;
    public string? Refusal { get; }
    public string? RuntimeIdentity => runner?.RuntimeIdentity;

    public static async Task<QualifiedOfficeProcessWorker> CreateAsync(string runtimeAssembly, string expectedAssemblyDigest,
        string expectedRuntimeIdentity, string launcherExecutable, string temporaryRoot, string dotnetRoot, CancellationToken token = default)
        => await CreateOwnedAsync(runtimeAssembly, expectedAssemblyDigest, expectedRuntimeIdentity, launcherExecutable,
            temporaryRoot, dotnetRoot, null, token).ConfigureAwait(false);

    // Register the runner before probing so the host retains ownership even if startup/cleanup fails.
    internal static async Task<QualifiedOfficeProcessWorker> CreateOwnedAsync(string runtimeAssembly, string expectedAssemblyDigest,
        string expectedRuntimeIdentity, string launcherExecutable, string temporaryRoot, string dotnetRoot,
        Action<IAsyncDisposable>? own, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux()) return new(null, "document-worker-platform-unsupported");
        ProcessCompiledRuntimeRunner? candidate = null;
        try
        {
            // Deployment must make this measured installation immutable. Neither protocol strings nor paths pin its build.
            using (var file = File.OpenRead(runtimeAssembly))
                if ("sha256:" + Convert.ToHexStringLower(SHA256.HashData(file)) != expectedAssemblyDigest)
                    return new(null, "document-worker-build-mismatch");
            candidate = new(new ProcessCompiledRuntimeOptions(expectedAssemblyDigest, runtimeAssembly, launcherExecutable, temporaryRoot, dotnetRoot)
            { Operation = CompiledRuntimeOperation.OfficeExtraction });
            own?.Invoke(candidate);
            if (candidate.RuntimeIdentity != expectedRuntimeIdentity || !await candidate.ProbeAsync(token).ConfigureAwait(false))
            {
                if (own is null) await candidate.DisposeAsync().ConfigureAwait(false);
                return new(null, "document-worker-sandbox-unqualified");
            }
            return new(candidate, null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
        {
            if (own is null && candidate is not null) await candidate.DisposeAsync().ConfigureAwait(false);
            return new(null, "document-worker-installation-unavailable");
        }
        catch
        {
            if (own is null && candidate is not null) await candidate.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<SpecOfficeExtractionResult> ExtractAsync(SpecCapturedSource source, SpecOfficeExtractionLimits limits, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Available) throw new SpecOfficeWorkerUnavailableException();
        await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Available) throw new SpecOfficeWorkerUnavailableException();
            var response = await runner!.RunAsync(SpecOfficeProcessProtocol.EncodeRequest(source, limits), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (response.Failure != WorkerProcessFailure.None) throw new SpecOfficeWorkerUnavailableException();
            return SpecOfficeProcessProtocol.DecodeResponse(response.Output, source, limits);
        }
        finally { concurrency.Release(); }
    }
    public ValueTask DisposeAsync() => runner?.DisposeAsync() ?? ValueTask.CompletedTask;
}
