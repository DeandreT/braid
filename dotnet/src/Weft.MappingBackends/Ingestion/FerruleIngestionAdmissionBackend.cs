using Weft.Application;
using Weft.Domain;
using Weft.MappingBackends.Workers;

namespace Weft.MappingBackends.Ingestion;

/// <summary>Composes the qualified validation adapter without interpreting or changing native bytes.</summary>
public sealed class FerruleIngestionAdmissionBackend : IIngestionAdmissionBackend, IExecutionAdmission
{
    private readonly FerruleInspectionBackend _backend;
    private readonly IInspectionWorkerRunner _runner;
    public bool CanExecute => _runner is not IExecutionAdmission { CanExecute: false };

    public FerruleIngestionAdmissionBackend(IInspectionWorkerRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
        _backend = new FerruleInspectionBackend(runner);
    }

    /// <summary>The server derives this registration from the actual runner, never from an upload.</summary>
    public static IngestionBackendRegistration CreateRegistration(IInspectionWorkerRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        var profile = new IngestionBackendProfile("ferrule", FerruleInspectionBackend.EngineRevision,
            runner.BuildIdentity, FerruleInspectionBackend.AdapterBuild);
        return new(profile, InspectionPolicy.MaximumNativeBytes, new FerruleIngestionAdmissionBackend(runner));
    }

    public async Task<IngestionAttemptCompletion> ValidateAsync(NativeArtifact artifact,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        cancellationToken.ThrowIfCancellationRequested();
        var result = await _backend.ValidateAsync(artifact, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return IngestionValidationMapper.Map(result);
    }
}
