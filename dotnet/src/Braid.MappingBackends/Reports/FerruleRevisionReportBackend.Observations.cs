using Braid.Application;
using Braid.Contracts.Projections;
using Braid.Domain;

namespace Braid.MappingBackends;

public sealed partial class FerruleRevisionReportBackend
{
    private static RevisionReportObservation? Observe(MappingProjectionResult result, NativeArtifact artifact,
        RevisionReportKey key, CancellationToken token)
    {
        if (result.Status is MappingOperationStatus.IsolationFailure or MappingOperationStatus.Canceled) token = CancellationToken.None;
        try
        {
            var provenance = ObservedProvenance(result.Provenance, artifact, token);
            var diagnostics = DiagnosticCount(result.Diagnostics, token);
            RevisionReportProjectionSummary summary;
            if (result.Projection is { } document)
            {
                var coverage = document.Coverage.Status switch
                {
                    CoverageStatus.Complete => RevisionReportCoverage.Complete,
                    CoverageStatus.Partial => RevisionReportCoverage.Partial,
                    CoverageStatus.Unavailable => RevisionReportCoverage.Unavailable,
                    _ => throw new ArgumentException("Unknown coverage.")
                };
                var gaps = document.Coverage.Gaps.Count;
                var items = checked(document.Boundaries.Count + document.Fields.Count + document.Parameters.Count +
                    document.Rules.Count + document.Controls.Count + document.Relationships.Count + document.Diagnostics.Count +
                    document.Capabilities.Count + gaps);
                summary = new(coverage, items, document.Diagnostics.Count, gaps, diagnostics);
            }
            else summary = new(null, 0, 0, 0, diagnostics);
            token.ThrowIfCancellationRequested();
            return new(key.Operation, Status(result.Status), provenance, summary, null);
        }
        catch (Exception error) when (error is ArgumentException or RevisionReportValidationException or OverflowException or NullReferenceException)
        { return null; }
    }

    private static RevisionReportObservation? Observe(MappingValidationResult result, NativeArtifact artifact,
        RevisionReportKey key, CancellationToken token)
    {
        if (result.Status is MappingOperationStatus.IsolationFailure or MappingOperationStatus.Canceled) token = CancellationToken.None;
        try
        {
            var provenance = ObservedProvenance(result.Provenance, artifact, token);
            var diagnostics = DiagnosticCount(result.Diagnostics, token);
            return new(key.Operation, Status(result.Status), provenance, null,
                new(result.IsValid, result.TotalIssueCount, result.EmittedIssueCount, result.Truncated, diagnostics));
        }
        catch (Exception error) when (error is ArgumentException or RevisionReportValidationException)
        { return null; }
    }

    private static RevisionReportProvenance ObservedProvenance(MappingOperationProvenance value, NativeArtifact artifact,
        CancellationToken token)
    {
        _ = FerruleReportNormalizer.Provenance(value, artifact, token);
        return new(value.Scope, value.NativeDigest,
            new(value.BackendId, value.EngineRevision, value.BackendBuild, value.AdapterBuild), value.Confirmed);
    }

    private static int DiagnosticCount(IReadOnlyList<MappingDiagnostic> diagnostics, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (diagnostics is null || diagnostics.Count > RevisionReportPolicy.MaximumDiagnosticLimit)
            throw new ArgumentException("Diagnostic facts are not independently bounded.");
        var count = diagnostics.Count;
        for (var index = 0; index < count; index++)
        {
            token.ThrowIfCancellationRequested();
            if (diagnostics.Count != count || diagnostics[index] is not { } diagnostic || !Enum.IsDefined(diagnostic.Severity))
                throw new ArgumentException("Diagnostic facts are inconsistent.");
        }
        return count;
    }

    private static RevisionReportFailureCategory Failure(MappingOperationStatus status, IReadOnlyList<MappingDiagnostic> diagnostics,
        CancellationToken token = default)
    {
        if (status == MappingOperationStatus.IsolationFailure) return RevisionReportFailureCategory.IsolationFailure;
        if (status == MappingOperationStatus.Canceled) return RevisionReportFailureCategory.Canceled;
        if (diagnostics is not null && diagnostics.Count <= RevisionReportPolicy.MaximumDiagnosticLimit)
        {
            var count = diagnostics.Count;
            for (var index = 0; index < count; index++)
            {
                token.ThrowIfCancellationRequested();
                if (diagnostics.Count != count) return RevisionReportFailureCategory.ProtocolFailure;
                var diagnostic = diagnostics[index];
                if (diagnostic?.Code == "ferrule.digestMismatch") return RevisionReportFailureCategory.ProtocolFailure;
            }
            for (var index = 0; index < count; index++)
            {
                token.ThrowIfCancellationRequested();
                if (diagnostics.Count != count) return RevisionReportFailureCategory.ProtocolFailure;
                var diagnostic = diagnostics[index];
                if (diagnostic?.Code == "ferrule.nativeDecodeFailed") return RevisionReportFailureCategory.UnsupportedArtifact;
            }
        }
        return status switch
        {
            MappingOperationStatus.UnsupportedArtifact => RevisionReportFailureCategory.UnsupportedArtifact,
            MappingOperationStatus.ResourceLimit => RevisionReportFailureCategory.ResourceLimit,
            MappingOperationStatus.BackendUnavailable => RevisionReportFailureCategory.BackendUnavailable,
            MappingOperationStatus.BackendFailure => RevisionReportFailureCategory.BackendFailure,
            MappingOperationStatus.TimedOut => RevisionReportFailureCategory.TimedOut,
            _ => RevisionReportFailureCategory.ProtocolFailure
        };
    }

    private static RevisionReportBackendStatus Status(MappingOperationStatus status) => status switch
    {
        MappingOperationStatus.Succeeded => RevisionReportBackendStatus.Succeeded,
        MappingOperationStatus.InvalidMapping => RevisionReportBackendStatus.InvalidMapping,
        MappingOperationStatus.UnsupportedArtifact => RevisionReportBackendStatus.UnsupportedArtifact,
        MappingOperationStatus.ResourceLimit => RevisionReportBackendStatus.ResourceLimit,
        MappingOperationStatus.ProtocolFailure => RevisionReportBackendStatus.ProtocolFailure,
        MappingOperationStatus.BackendUnavailable => RevisionReportBackendStatus.BackendUnavailable,
        MappingOperationStatus.BackendFailure => RevisionReportBackendStatus.BackendFailure,
        MappingOperationStatus.TimedOut => RevisionReportBackendStatus.TimedOut,
        MappingOperationStatus.Canceled => RevisionReportBackendStatus.Canceled,
        MappingOperationStatus.IsolationFailure => RevisionReportBackendStatus.IsolationFailure,
        _ => throw new ArgumentException("Unknown backend status.")
    };
}
