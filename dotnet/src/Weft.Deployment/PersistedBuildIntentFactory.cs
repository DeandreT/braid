using Weft.Application;
using Weft.Application.Regression;
using Weft.Contracts.Operations;
using Weft.Contracts.Projections;
using Weft.Domain;

namespace Weft.Deployment;

/// <summary>Uses stored verified report facts as requirements, never as compiler capability evidence.</summary>
public sealed class PersistedBuildIntentFactory(IBuildMetadataReader metadata, IRevisionReportMetadataReader sources,
    IRevisionReportReader reports, RegressionRunReadService evidence, BuildBackendIdentity backend, BuildRuntimeProfile profile) : IBuildIntentFactory
{
    public async Task<BuildIntent?> CreateAsync(ActorContext actor, MappingScope mapping, Guid approvalId,
        BuildApprovalSelection selection, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(selection);
        if (approvalId == Guid.Empty || selection.BuildId == Guid.Empty || selection.RevisionId == Guid.Empty ||
            selection.EnvironmentId == Guid.Empty || selection.EnvironmentVersion < 1 || selection.RegressionEvidenceId == Guid.Empty)
            throw new ArgumentException("Invalid build selectors.");
        var scope = new RevisionScope(mapping.OrganizationId, mapping.ProjectId, mapping.MappingId, selection.RevisionId);
        var environment = new BuildEnvironmentReference(new(mapping.OrganizationId, mapping.ProjectId), selection.EnvironmentId, selection.EnvironmentVersion);
        var retained = await metadata.ReadEnvironmentAsync(actor, environment, token).ConfigureAwait(false);
        if (retained is null) return null;
        if (retained.Environment.Reference != environment) throw Inconsistent();
        if (!retained.Environment.Active || retained.Environment.Profile != profile) return null;
        var source = await sources.GetSourceAsync(actor, scope, token).ConfigureAwait(false);
        if (source is null) return null;
        if (source.Actor != actor.PrincipalId || source.Source.Scope != scope) throw Inconsistent();
        if (source.Source.SourceBackendId != backend.BackendId) return null;
        var run = await evidence.ReadAsync(actor, mapping.OrganizationId, selection.RegressionEvidenceId, token).ConfigureAwait(false);
        if (run?.Result?.Comparison?.Outcome != RegressionComparisonOutcome.Passed || run.Run.Case.Revision != scope ||
            run.Run.Case.NativeDigest != source.Source.NativeObject.Digest || run.Runtime is null ||
            run.Runtime.BackendId != backend.BackendId || run.Runtime.EngineRevision != backend.EngineRevision ||
            run.Runtime.BackendBuild != backend.EngineBuild || run.Runtime.AdapterBuild != backend.AdapterVersion) return null;
        var observed = await reports.GetCurrentAsync(actor, scope, RevisionReportOperation.Projection, cancellationToken: token).ConfigureAwait(false);
        if (observed.Report is not VerifiedProjectionReport verified || observed.Outcome != RevisionReportReadOutcome.Observed) return null;
        if (observed.Source?.Actor != actor.PrincipalId || observed.Source.Source != source.Source ||
            verified.Metadata.Key.Source != source.Source) throw Inconsistent();
        var selected = verified.Metadata.Key.Profile;
        if (selected.BackendId != backend.BackendId || selected.BackendRevision != backend.EngineRevision || selected.BackendBuild != backend.EngineBuild)
            return null;
        if (verified.Report.Status != OperationStatusV1.Succeeded || verified.Report.Provenance?.Confirmed != true ||
            verified.Report.Document is not { } document || document.Coverage.Status != CoverageStatus.Complete || document.Coverage.Gaps.Count != 0)
            return null;
        if (document.Boundaries.Count is < 1 or > 128) return null;
        var boundaries = new List<BuildBoundary>(document.Boundaries.Count);
        foreach (var boundary in document.Boundaries)
        {
            token.ThrowIfCancellationRequested();
            if (boundary.Format != "json" || !Enum.IsDefined(boundary.Role) || boundary.Shape is BoundaryShape.Unknown or BoundaryShape.DocumentSet)
                return null;
            var kind = boundary.Role switch
            {
                BoundaryRole.PrimaryInput => BuildBoundaryKind.PrimaryInput,
                BoundaryRole.NamedInput => BuildBoundaryKind.NamedInput,
                BoundaryRole.PrimaryOutput => BuildBoundaryKind.PrimaryOutput,
                BoundaryRole.NamedOutput => BuildBoundaryKind.NamedOutput,
                _ => throw Inconsistent()
            };
            boundaries.Add(new(kind, kind is BuildBoundaryKind.PrimaryInput or BuildBoundaryKind.PrimaryOutput ? null : boundary.Name, boundary.Format));
        }
        if (boundaries.Distinct().Count() != boundaries.Count ||
            boundaries.Count(value => value.Kind == BuildBoundaryKind.PrimaryInput) != 1 ||
            boundaries.Count(value => value.Kind == BuildBoundaryKind.PrimaryOutput) != 1) return null;
        var fresh = await sources.GetSourceAsync(actor, scope, token).ConfigureAwait(false);
        if (fresh is null) return null;
        if (fresh.Actor != actor.PrincipalId || fresh.Source != source.Source) throw Inconsistent();
        var current = await metadata.ReadEnvironmentAsync(actor, environment, token).ConfigureAwait(false);
        if (current is null || !current.Environment.Active || current.Environment.Reference != environment || current.Environment.Profile != profile) return null;
        return new(selection.BuildId, scope, source.Source.NativeObject.Digest, backend, profile, boundaries,
            environment, approvalId, selection.RegressionEvidenceId);
    }
    private static InvalidOperationException Inconsistent() => new("Stored build facts are inconsistent.");
}
