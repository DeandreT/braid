using Weft.Contracts.Projections;
using Weft.Contracts.Validation;
using Weft.Domain;

namespace Weft.Application;

public interface IRevisionReportBackend
{
    Task<RevisionReportBackendResult> DeriveAsync(RevisionReportKey key, NativeArtifact artifact,
        CancellationToken cancellationToken = default);
}

/// <summary>Owned normalized facts. A backend never supplies a private derived reference.</summary>
public sealed record RevisionReportBackendResult
{
    private RevisionReportBackendResult(RevisionReportKey? key, ProjectionReportV1? projectionReport,
        ValidationReportV1? validationReport, RevisionReportObservation? observation, RevisionReportFailureCategory? failure)
    {
        Key = key;
        ProjectionReport = projectionReport;
        ValidationReport = validationReport;
        Observation = observation;
        Failure = failure;
    }

    public bool IsUsable => Key is not null;
    public RevisionReportKey? Key { get; }
    public ProjectionReportV1? ProjectionReport { get; }
    public ValidationReportV1? ValidationReport { get; }
    public RevisionReportObservation? Observation { get; }
    public RevisionReportFailureCategory? Failure { get; }

    public static RevisionReportBackendResult ForProjection(ProjectionCacheKey key, ProjectionReportV1 report,
        RevisionReportObservation observation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(observation);
        var owned = RevisionReportSnapshots.CopyProjection(report, key.Policy, cancellationToken);
        var actual = RevisionReportProjectionValidation.Validate(key, owned, key.Policy, cancellationToken);
        RevisionReportWorkflowValue.MatchObservation(observation, actual);
        RequireUsable(key, actual);
        return new(key, owned, null, actual, null);
    }

    public static RevisionReportBackendResult ForValidation(ValidationCacheKey key, ValidationReportV1 report,
        RevisionReportObservation observation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(observation);
        var owned = RevisionReportSnapshots.CopyValidation(report, key.Policy, cancellationToken);
        foreach (var diagnostic in owned.Diagnostics)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (diagnostic.ProjectionLocator is not null)
                throw new RevisionReportValidationException(RevisionReportValidationFailure.Integrity);
        }
        var actual = RevisionReportValidationValidation.Validate(key, owned, key.Policy, cancellationToken);
        RevisionReportWorkflowValue.MatchObservation(observation, actual);
        RequireUsable(key, actual);
        return new(key, null, owned, actual, null);
    }

    public static RevisionReportBackendResult ForFailure(RevisionReportFailureCategory failure,
        RevisionReportObservation? observation = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = RevisionReportCompletion.ForFailure(failure, observation);
        return new(null, null, null, observation, failure);
    }

    private static void RequireUsable(RevisionReportKey key, RevisionReportObservation observation)
    {
        var matching = observation.Operation == key.Operation && observation.Provenance is { Confirmed: true } provenance &&
            provenance.Scope == key.Scope && provenance.NativeDigest == key.NativeDigest && provenance.Profile == key.Profile;
        var eligible = key.Operation switch
        {
            RevisionReportOperation.Projection => observation.Status == RevisionReportBackendStatus.Succeeded && observation.Projection?.Coverage is not null,
            RevisionReportOperation.Validation => observation.Status == RevisionReportBackendStatus.Succeeded && observation.Validation?.IsValid == true ||
                observation.Status == RevisionReportBackendStatus.InvalidMapping && observation.Validation?.IsValid == false,
            _ => false
        };
        if (!matching || !eligible)
            throw new RevisionReportValidationException(RevisionReportValidationFailure.Integrity);
        if (key.Source.NativeObject.ByteCount > key.Policy.NativeByteLimit)
            throw new RevisionReportValidationException(RevisionReportValidationFailure.ResourceLimit);
    }
}
