using System.Security.Cryptography;
using Weft.Application;
using Weft.Domain;

namespace Weft.MappingBackends.Workers;

/// <summary>Native PDF composition. Public activation remains closed until the installed Linux profile is qualified.</summary>
public sealed class QualifiedPdfProcessWorker : ISpecPdfExtractionWorker, IAsyncDisposable
{
    private readonly ProcessWorkerRunner? runner;
    private readonly SpecPdfExtractionProfile profile;
    private readonly SemaphoreSlim concurrency = new(1, 1);
    private QualifiedPdfProcessWorker(SpecPdfExtractionProfile profile, ProcessWorkerRunner? runner, string? refusal)
    { this.profile = profile; this.runner = runner; Refusal = refusal; }
    public bool Available => runner?.Available == true;
    public string? Refusal { get; }
    public Sha256Digest ProfileDigest => profile.Digest;

    public static QualifiedPdfProcessWorker Create(SpecPdfExtractionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new(profile, null, OperatingSystem.IsLinux() ? "pdf-worker-linux-qualification-pending" : "pdf-worker-platform-unsupported");
    }

    // Internal native admission entrypoint. Trusted host startup additionally verifies the immutable installation;
    // the public convenience factory stays closed and no request/configuration boolean asserts qualification.
    internal static async Task<QualifiedPdfProcessWorker> CreateForQualificationAsync(string executable, string temporaryRoot,
        SpecPdfExtractionProfile profile, Sha256Digest expectedProfileDigest, CancellationToken token, Action<IAsyncDisposable>? own = null)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux()) return Create(profile);
        if (profile.Digest != expectedProfileDigest) return new(profile, null, "pdf-worker-profile-mismatch");
        ProcessWorkerRunner? candidate = null;
        try
        {
            var options = new ProcessWorkerOptions(executable, DockerWorkerProfile.Pdf, temporaryRoot);
            if (!MatchesBuild(executable, profile)) return new(profile, null, "pdf-worker-build-mismatch");
            candidate = new(options);
            own?.Invoke(candidate);
            if (candidate.BuildIdentity != "sha256:" + profile.ExecutableDigest.Value)
            { if (own is null) await candidate.DisposeAsync().ConfigureAwait(false); return new(profile, null, "pdf-worker-build-mismatch"); }
            // Exercise the actual parser under pre-input sandbox admission with a nonprivate malformed document.
            var probe = await candidate.RunAsync(ReadOnlyMemory<byte>.Empty, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (probe.Failure != WorkerProcessFailure.None) throw new SpecPdfWorkerUnavailableException();
            var identity = new SpecSourceIdentity(new(new ProjectScope(Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid()), Guid.NewGuid(), 1);
            var response = SpecPdfWorkerResponse.Decode(probe.Output, 0, identity, Sha256Digest.FromBytes([]), 0);
            if (response.Status != SpecPdfWorkerStatus.Malformed) throw new SpecPdfWorkerProtocolException();
            return new(profile, candidate, null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or SpecPdfWorkerUnavailableException or SpecPdfWorkerProtocolException)
        {
            if (own is null && candidate is not null) await candidate.DisposeAsync().ConfigureAwait(false);
            return new(profile, null, "pdf-worker-sandbox-unqualified");
        }
        catch
        {
            if (own is null && candidate is not null) await candidate.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
    internal static bool MatchesBuild(string executable, SpecPdfExtractionProfile profile)
    {
        using var file = File.OpenRead(executable);
        return Convert.ToHexStringLower(SHA256.HashData(file)) == profile.ExecutableDigest.Value;
    }
    public async Task<SpecPdfExtractionResult> ExtractAsync(SpecCapturedSource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Available) throw new SpecPdfWorkerUnavailableException();
        if (source.Format != SpecSourceFormat.Pdf || source.ByteCount > SpecPdfWorkerResponse.MaximumSourceBytes) throw new SpecPdfWorkerProtocolException();
        await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Available) throw new SpecPdfWorkerUnavailableException();
            var result = await runner!.RunAsync(source.CopyBytes(), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Failure != WorkerProcessFailure.None) throw new SpecPdfWorkerUnavailableException();
            return SpecPdfExtractionResult.Decode(result.Output, 0, source, profile);
        }
        finally { concurrency.Release(); }
    }
    public ValueTask DisposeAsync() => runner?.DisposeAsync() ?? ValueTask.CompletedTask;
}
