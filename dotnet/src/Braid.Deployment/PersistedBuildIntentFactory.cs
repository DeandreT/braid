using Braid.Application;
using Braid.Application.Regression;
using Braid.Contracts.Operations;
using Braid.Contracts.Projections;
using Braid.Domain;

namespace Braid.Deployment;

/// <summary>Authoritative native boundary inspection for CSV; projection recognition is not compiler admission.</summary>
public interface ICsvBuildBoundaryReader
{
    Task<CsvBuildBoundaryObservation?> ReadAsync(ActorContext actor, RevisionReportSource source, CancellationToken token = default);
}
public sealed record CsvBuildBoundaryObservation(RevisionScope Revision, Sha256Digest NativeDigest,
    BuildBackendIdentity Backend, IReadOnlyList<BuildBoundary> Boundaries);

/// <summary>Uses stored verified report facts as requirements, never as compiler capability evidence.</summary>
public sealed class PersistedBuildIntentFactory(IBuildMetadataReader metadata, IRevisionReportMetadataReader sources,
    IRevisionReportReader reports, RegressionRunReadService evidence, BuildBackendIdentity backend, BuildRuntimeProfile profile,
    BuildBackendIdentity? documentBackend = null, BuildRuntimeProfile? documentProfile = null, BuildRuntimeProfile? interchangeProfile = null,
    BuildBackendIdentity? csvBackend = null, BuildRuntimeProfile? csvProfile = null, ICsvBuildBoundaryReader? csvBoundaries = null,
    BuildBackendIdentity? xmlBackend = null, BuildRuntimeProfile? xmlProfile = null, ICsvBuildBoundaryReader? xmlBoundaries = null, IReadOnlyDictionary<BuildRuntimeProfile,(BuildBackendIdentity Backend, ICsvBuildBoundaryReader Reader)>? byteAdapters = null) : IBuildIntentFactory
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
        var selectedProfile = retained.Environment.Profile;
        var csv = csvProfile is not null && selectedProfile == csvProfile && selectedProfile.Id == BuildWorkerProtocol.CsvProfile;
        var xml = xmlProfile is not null && selectedProfile == xmlProfile && selectedProfile.Id == BuildWorkerProtocol.XmlProfile;
        var bytes = byteAdapters?.GetValueOrDefault(selectedProfile);
        var document = selectedProfile == documentProfile || selectedProfile == interchangeProfile;
        var selectedBackend = selectedProfile == profile ? backend : csv && csvBoundaries is not null ? csvBackend : xml && xmlBoundaries is not null ? xmlBackend : document ? documentBackend : bytes?.Backend;
        if (!retained.Environment.Active || selectedBackend is null) return null;
        var source = await sources.GetSourceAsync(actor, scope, token).ConfigureAwait(false);
        if (source is null) return null;
        if (source.Actor != actor.PrincipalId || source.Source.Scope != scope) throw Inconsistent();
        if (source.Source.SourceBackendId != selectedBackend.BackendId) return null;
        var run = await evidence.ReadAsync(actor, mapping.OrganizationId, selection.RegressionEvidenceId, token).ConfigureAwait(false);
        if (run?.Result?.Comparison?.Outcome != RegressionComparisonOutcome.Passed || run.Result.Comparison.Policy != RegressionComparisonService.ExactBytesPolicy || run.Run.Case.Revision != scope ||
            run.Run.Case.NativeDigest != source.Source.NativeObject.Digest || run.Runtime is null ||
            run.Runtime.BackendId != selectedBackend.BackendId || run.Runtime.EngineRevision != selectedBackend.EngineRevision ||
            run.Runtime.BackendBuild != selectedBackend.EngineBuild || run.Runtime.AdapterBuild != selectedBackend.AdapterVersion ||
            run.Runtime.Profile != BuildWorkerProtocol.PreviewProfileFor(selectedProfile.Id)) return null;
        var observed = await reports.GetCurrentAsync(actor, scope, RevisionReportOperation.Projection, cancellationToken: token).ConfigureAwait(false);
        if (observed.Report is not VerifiedProjectionReport verified || observed.Outcome != RevisionReportReadOutcome.Observed) return null;
        if (observed.Source?.Actor != actor.PrincipalId || observed.Source.Source != source.Source ||
            verified.Metadata.Key.Source != source.Source) throw Inconsistent();
        var selected = verified.Metadata.Key.Profile;
        if (selected.BackendId != selectedBackend.BackendId || selected.BackendRevision != selectedBackend.EngineRevision || selected.BackendBuild != selectedBackend.EngineBuild)
            return null;
        if (verified.Report.Status != OperationStatusV1.Succeeded || verified.Report.Provenance?.Confirmed != true ||
            verified.Report.Document is not { } projection || projection.Coverage.Status != CoverageStatus.Complete || projection.Coverage.Gaps.Count != 0)
            return null;
        if (projection.Boundaries.Count is < 1 or > 128) return null;
        var boundaries = new List<BuildBoundary>(projection.Boundaries.Count);
        if (!csv && !xml && bytes is null) foreach (var boundary in projection.Boundaries)
            {
                token.ThrowIfCancellationRequested();
                if (!(document ? BuildWorkerProtocol.AdmitsFormat(selectedProfile.Id, boundary.Format) : boundary.Format == "json") ||
                    !Enum.IsDefined(boundary.Role) || boundary.Shape is BoundaryShape.Unknown or BoundaryShape.DocumentSet)
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
        if (csv || xml || bytes is not null)
        {
            var native = await (bytes?.Reader ?? (xml ? xmlBoundaries : csvBoundaries))!.ReadAsync(actor, source.Source, token).ConfigureAwait(false);
            if (native is null) return null;
            if (native.Revision != scope || native.NativeDigest != source.Source.NativeObject.Digest || native.Backend != selectedBackend) throw Inconsistent();
            if (xml && native.Boundaries.Any(b => b.Format != "xml" || b.XmlMetadata is null)) return null;
            if (csv && !BuildWorkerProtocol.AdmitsCsvPlan(native.Boundaries)) return null;
            boundaries.AddRange(native.Boundaries);
        }
        if (boundaries.Distinct().Count() != boundaries.Count ||
            selectedProfile == interchangeProfile && !boundaries.Any(value => value.Format == "x12") ||
            boundaries.Count(value => value.Kind == BuildBoundaryKind.PrimaryInput) != 1 ||
            boundaries.Count(value => value.Kind == BuildBoundaryKind.PrimaryOutput) != 1) return null;
        var fresh = await sources.GetSourceAsync(actor, scope, token).ConfigureAwait(false);
        if (fresh is null) return null;
        if (fresh.Actor != actor.PrincipalId || fresh.Source != source.Source) throw Inconsistent();
        var current = await metadata.ReadEnvironmentAsync(actor, environment, token).ConfigureAwait(false);
        if (current is null || !current.Environment.Active || current.Environment.Reference != environment || current.Environment.Profile != selectedProfile) return null;
        return new(selection.BuildId, scope, source.Source.NativeObject.Digest, selectedBackend, selectedProfile, boundaries,
            environment, approvalId, selection.RegressionEvidenceId);
    }
    private static InvalidOperationException Inconsistent() => new("Stored build facts are inconsistent.");
}
