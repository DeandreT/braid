using Braid.Application;
using Braid.Domain;
using Braid.MappingBackends.Workers;

namespace Braid.MappingBackends;

/// <summary>Trusted report-only invocation. No storage or metadata operation is performed.</summary>
public sealed partial class FerruleRevisionReportBackend : IRevisionReportBackend
{
    private readonly IInspectionWorkerRunner _runner;
    private readonly RevisionReportBackendProfile _profile;
    private FerruleInspectionBackend? _inspection;

    public FerruleRevisionReportBackend(IInspectionWorkerRunner runner, RevisionReportBackendProfile trustedProfile)
    {
        ArgumentNullException.ThrowIfNull(runner);
        FerruleRevisionReportBackendFactory.RequireProfile(trustedProfile);
        _runner = runner;
        _profile = trustedProfile;
    }

    public async Task<RevisionReportBackendResult> DeriveAsync(RevisionReportKey key, NativeArtifact artifact,
        CancellationToken cancellationToken = default)
    {
        ValidateInput(key, artifact, _profile, cancellationToken);
        if (_runner.BuildIdentity != _profile.BackendBuild)
            return RevisionReportBackendResult.ForFailure(RevisionReportFailureCategory.ProfileUnavailable, cancellationToken: cancellationToken);
        if (artifact.ByteCount > key.Policy.NativeByteLimit)
            return RevisionReportBackendResult.ForFailure(RevisionReportFailureCategory.ResourceLimit, cancellationToken: cancellationToken);
        // The invocation adapter retains the same shared runner, including its quarantine state.
        var inspection = _inspection ??= new FerruleInspectionBackend(_runner);
        if (key is ProjectionCacheKey projectionKey)
        {
            var result = await inspection.ProjectReportAsync(artifact, key.Policy, cancellationToken).ConfigureAwait(false);
            var actual = Observe(result, artifact, key, cancellationToken);
            if (result.Status is MappingOperationStatus.IsolationFailure or MappingOperationStatus.Canceled)
                return RevisionReportBackendResult.ForFailure(Failure(result.Status, result.Diagnostics), actual);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Status != MappingOperationStatus.Succeeded)
                return RevisionReportBackendResult.ForFailure(Failure(result.Status, result.Diagnostics, cancellationToken), actual, cancellationToken);
            try
            {
                var normalized = FerruleReportNormalizer.Projection(result, artifact, key.Policy, cancellationToken);
                new FerruleRevisionReportFormatValidator().Validate(key, normalized, cancellationToken);
                var observed = RevisionReportProjectionValidation.Validate(key, normalized, key.Policy, cancellationToken);
                return RevisionReportBackendResult.ForProjection(projectionKey, normalized, observed, cancellationToken);
            }
            catch (RevisionReportValidationException error)
            {
                return RevisionReportBackendResult.ForFailure(error.Failure == RevisionReportValidationFailure.ResourceLimit
                    ? RevisionReportFailureCategory.ResourceLimit : RevisionReportFailureCategory.ProtocolFailure, actual, cancellationToken);
            }
        }
        else
        {
            var result = await inspection.ValidateReportAsync(artifact, key.Policy, cancellationToken).ConfigureAwait(false);
            var actual = Observe(result, artifact, key, cancellationToken);
            if (result.Status is MappingOperationStatus.IsolationFailure or MappingOperationStatus.Canceled)
                return RevisionReportBackendResult.ForFailure(Failure(result.Status, result.Diagnostics), actual);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.IsValid is null || result.Status is not (MappingOperationStatus.Succeeded or MappingOperationStatus.InvalidMapping))
                return RevisionReportBackendResult.ForFailure(Failure(result.Status, result.Diagnostics, cancellationToken), actual, cancellationToken);
            try
            {
                var normalized = FerruleReportNormalizer.Validation(result, artifact, key.Policy, cancellationToken: cancellationToken);
                new FerruleRevisionReportFormatValidator().Validate(key, normalized, cancellationToken);
                var observed = RevisionReportValidationValidation.Validate(key, normalized, key.Policy, cancellationToken);
                return RevisionReportBackendResult.ForValidation((ValidationCacheKey)key, normalized, observed, cancellationToken);
            }
            catch (RevisionReportValidationException error)
            {
                return RevisionReportBackendResult.ForFailure(error.Failure == RevisionReportValidationFailure.ResourceLimit
                    ? RevisionReportFailureCategory.ResourceLimit : RevisionReportFailureCategory.ProtocolFailure, actual, cancellationToken);
            }
        }
    }

    internal static void ValidateInput(RevisionReportKey key, NativeArtifact artifact,
        RevisionReportBackendProfile profile, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(artifact);
        token.ThrowIfCancellationRequested();
        if (key is not (ProjectionCacheKey or ValidationCacheKey) || key.Profile != profile ||
            key.Scope != artifact.Scope || key.NativeDigest != artifact.Digest || key.Source.NativeObject.ByteCount != artifact.ByteCount ||
            key.Source.SourceBackendId != "ferrule" || key.Source.LogicalDocument != InspectionPolicy.DocumentId ||
            RevisionReportFormat.FromKey(key) != FerruleRevisionReportBackendFactory.Format(key.Operation))
            throw new RevisionReportValidationException(RevisionReportValidationFailure.Integrity);
        // NativeArtifact has no independent object UUID or dependency-manifest observation.
        // Those immutable facts remain in the key and must be bound by the authorized source/read workflow.
    }
}
