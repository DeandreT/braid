using Weft.Application;
using Weft.Application.Regression;
using Weft.Deployment;
using Weft.MappingBackends.Workers;

namespace Weft.MappingBackends;

/// <summary>The parent only frames verified bytes; compiled code is loaded by a separate restricted process.</summary>
public sealed class FerruleCompiledBuildRuntime : ICompiledBuildRuntime
{
    private readonly IInspectionWorkerRunner runner;
    private readonly BuildBackendIdentity compiler;
    public FerruleCompiledBuildRuntime(IInspectionWorkerRunner runner, BuildBackendIdentity compiler)
    {
        ArgumentNullException.ThrowIfNull(runner); ArgumentNullException.ThrowIfNull(compiler);
        if ((runner is DockerInspectionWorkerRunner docker && docker.Profile != DockerWorkerProfile.Compiled) ||
            (runner is FerruleServiceRunner service && service.ProfileName != FerruleServiceProtocol.CompiledProfile))
            throw new ArgumentException("Compiled execution requires its dedicated sandbox profile.", nameof(runner));
        if (compiler.BackendId != "ferrule" || compiler.EngineRevision != BuildWorkerProtocol.Engine ||
            compiler.AdapterVersion is not (FerruleBuildCompiler.Adapter or FerrulePreviewBackend.DocumentAdapterBuild))
            throw new ArgumentException("A qualified compiler identity is required.", nameof(compiler));
        if (runner.BuildIdentity.Length != 71 || !runner.BuildIdentity.StartsWith("sha256:", StringComparison.Ordinal) ||
            runner.BuildIdentity[7..].Any(c => c is not (>= 'a' and <= 'f') and not (>= '0' and <= '9')))
            throw new ArgumentException("An immutable runtime image is required.", nameof(runner));
        this.runner = runner; this.compiler = compiler;
    }
    public string RuntimeImage => runner.BuildIdentity;

    public async Task<CompiledBuildResult> ExecuteAsync(BuildArtifactReference artifact, BuildBytes verifiedArtifact, RegressionReplay replay,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact); ArgumentNullException.ThrowIfNull(verifiedArtifact); ArgumentNullException.ThrowIfNull(replay);
        cancellationToken.ThrowIfCancellationRequested();
        var documents = BuildWorkerProtocol.IsDocumentProfile(artifact.Intent.Profile.Id);
        var profile = artifact.Intent.Profile == FerruleBuildCompiler.X12InterchangesProfile ? FerruleBuildCompiler.X12InterchangesProfile :
            documents ? FerruleBuildCompiler.DocumentProfile : FerruleBuildCompiler.Profile;
        var expectedCompiler = new BuildBackendIdentity(compiler.BackendId, compiler.EngineRevision, compiler.EngineBuild,
            documents ? FerrulePreviewBackend.DocumentAdapterBuild : FerruleBuildCompiler.Adapter);
        if (artifact.Intent.Backend != expectedCompiler || artifact.Intent.Profile != profile || artifact.Intent.Boundaries.Any(value => !BuildWorkerProtocol.AdmitsFormat(profile.Id, value.Format)))
            return new(CompiledBuildOutcome.Unsupported);
        if (artifact.ContentDigest != verifiedArtifact.Digest || artifact.ByteCount != verifiedArtifact.Length)
            return new(CompiledBuildOutcome.IntegrityFailure);
        var namedInputs = artifact.Intent.Boundaries.Where(value => value.Kind == BuildBoundaryKind.NamedInput)
            .Select(value => value.Name).ToHashSet(StringComparer.Ordinal);
        if (!namedInputs.SetEquals(replay.NamedInputs.Select(value => value.Name)) ||
            replay.Target.Kind == PreviewTargetKind.Named && !artifact.Intent.Boundaries.Any(value =>
                value.Kind == BuildBoundaryKind.NamedOutput && value.Name == replay.Target.Name))
            return new(CompiledBuildOutcome.InvalidInput);
        var header = CompiledRuntimeProtocol.For(artifact, replay);
        var request = CompiledRuntimeProtocol.EncodeRequest(header, artifact.Intent, verifiedArtifact, replay);
        var reply = await runner.RunAsync(request, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (reply.Failure != WorkerProcessFailure.None) return new(CompiledBuildOutcome.Unavailable);
        CompiledRuntimeProtocol.Result result;
        try { result = CompiledRuntimeProtocol.DecodeResponse(reply.Output, header, artifact.Intent); }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or System.Text.Json.JsonException)
        { return new(CompiledBuildOutcome.IntegrityFailure); }
        return result.Header.Status switch
        {
            "succeeded" => new(CompiledBuildOutcome.Completed, result.Outputs),
            "invalidInput" => new(CompiledBuildOutcome.InvalidInput),
            "invalidParameter" => new(CompiledBuildOutcome.InvalidParameter),
            "unsupported" => new(CompiledBuildOutcome.Unsupported),
            "executionFailed" => new(CompiledBuildOutcome.ExecutionFailed),
            "resourceLimit" => new(CompiledBuildOutcome.ResourceLimit),
            "invalidArtifact" => new(CompiledBuildOutcome.IntegrityFailure),
            _ => new(CompiledBuildOutcome.IntegrityFailure),
        };
    }
}
