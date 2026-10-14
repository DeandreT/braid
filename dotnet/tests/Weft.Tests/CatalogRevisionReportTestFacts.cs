using Weft.Application;
using Weft.Contracts.Examples;
using Weft.Contracts.Operations;
using Weft.Contracts.Projections;
using Weft.Contracts.Validation;
using Weft.Domain;

namespace Weft.Tests;

/// <summary>Authored synthetic metadata and neutral owned reports; no database, object store or worker execution.</summary>
internal static class CatalogRevisionReportTestFacts
{
    internal static readonly RevisionScope Scope = new(
        Guid.Parse("abcdef01-0000-4000-8000-000000000001"), Guid.Parse("abcdef01-0000-4000-8000-000000000002"),
        Guid.Parse("abcdef01-0000-4000-8000-000000000003"), Guid.Parse("abcdef01-0000-4000-8000-000000000004"));
    internal static readonly ActorContext Actor = new(new(Guid.Parse("abcdef01-0000-4000-8000-000000000005")));
    internal static readonly DateTimeOffset Start = new(2026, 10, 14, 22, 0, 0, TimeSpan.Zero);
    internal static readonly Guid DerivationId = Guid.Parse("abcdef01-0000-4000-8000-000000000006");
    internal static readonly Guid ProjectionId = Guid.Parse("abcdef01-0000-4000-8000-000000000010");
    internal static readonly RevisionReportBackendProfile Profile = new("neutral", "v1", "build", "adapter");
    internal static readonly RevisionReportSource Source = new(Scope,
        new(PrivateObjectId.Restore(Scope, Guid.Parse("abcdef01-0000-4000-8000-000000000007"), PrivateObjectClass.NativeArtifact),
            Sha256Digest.Parse(new string('1', 64)), 3), IngestionIntent.EntryDocument, "fixed-dependencies", Profile.BackendId);

    internal static CatalogRevision Revision(ProjectRole role = ProjectRole.Viewer, PrincipalId? actor = null) => new(
        new(Scope, long.MaxValue, IngestionAdmissionClassification.EligibleValid, Actor.PrincipalId, Start,
            "native.json", IngestionIntent.EntryDocument, Source.NativeObject.Digest, 3, Profile.BackendId),
        new(new(Scope.OrganizationId, Scope.ProjectId), actor ?? Actor.PrincipalId, role));

    internal static AuthorizedRevisionReportSource Authority(DateTimeOffset? observed = null) =>
        new(Source, Actor.PrincipalId, observed ?? Start.AddSeconds(3));

    internal static ProjectionCacheKey ProjectionKey(RevisionReportPolicy? policy = null) =>
        new(Source, Profile, "weft.mapping-projection.v1", "neutral.projection.v1", "weft.revision-report-codec.v1", policy ?? new());
    internal static ValidationCacheKey ValidationKey(RevisionReportPolicy? policy = null) =>
        new(Source, Profile, "weft.mapping-validation.v1", "neutral.validation.v1", "weft.revision-report-codec.v1", policy ?? new());

    internal static RevisionReportReadSnapshot Empty(RevisionReportKey key) => new(Authority(), key, null, 0, null, null);
    internal static RevisionReportReadSnapshot Pending(RevisionReportKey key) => new(Authority(Start.AddTicks(1)), key,
        Guid.Parse("abcdef01-0000-4000-8000-000000000008"), long.MaxValue, null,
        new(DerivationId, long.MaxValue, Start, Start.AddSeconds(60), null, null));

    internal static RevisionReportReadSnapshot Failed(RevisionReportKey key,
        RevisionReportBackendStatus? status = RevisionReportBackendStatus.ResourceLimit,
        RevisionReportFailureCategory failure = RevisionReportFailureCategory.ResourceLimit) => new(
        Authority(Start.AddSeconds(2).AddTicks(1)), key, Guid.Parse("abcdef01-0000-4000-8000-000000000008"), long.MaxValue, null,
        new(DerivationId, long.MaxValue, Start, Start.AddSeconds(60), Start.AddSeconds(2),
            RevisionReportCompletion.ForFailure(failure, status is { } actual ? new(key.Operation, actual, null, null, null) : null)));

    internal static ProjectionReportV1 Projection(ProjectionCacheKey key, IReadOnlyList<ProjectionParameter>? parameters = null,
        IReadOnlyList<ProjectionControl>? controls = null)
    {
        var scope = WireScope();
        var digest = new ArtifactDigest("sha256", Source.NativeObject.Digest.Value);
        var backend = new BackendIdentity(Profile.BackendId, Profile.BackendRevision, Profile.BackendBuild);
        return new(OperationStatusV1.Succeeded, new(scope, digest, backend, Profile.AdapterBuild, true),
            new(new(scope, digest, backend, Profile.AdapterBuild, key.AlgorithmVersion), new(CoverageStatus.Complete, []),
                [], [], parameters ?? [], [], controls ?? [], [], [], []), []);
    }

    internal static VerifiedProjectionReport VerifiedProjection(RevisionReportPolicy? policy = null,
        IReadOnlyList<ProjectionParameter>? parameters = null, IReadOnlyList<ProjectionControl>? controls = null,
        RevisionReportPolicy? readCaps = null)
    {
        var key = ProjectionKey(policy);
        var report = Projection(key, parameters, controls);
        var observed = new RevisionReportObservation(key.Operation, RevisionReportBackendStatus.Succeeded,
            new(Scope, key.NativeDigest, Profile, true), new(RevisionReportCoverage.Complete,
                (parameters?.Count ?? 0) + (controls?.Count ?? 0), 0, 0, 0), null);
        return VerifiedProjectionReport.Create(Result(key, observed), report, readCaps ?? new());
    }

    internal static VerifiedValidationReport VerifiedValidation(bool valid = false, Guid? selectedProjection = null,
        string message = "actual mapping finding")
    {
        var key = ValidationKey();
        var provenance = new OperationProvenanceV1(WireScope(), new("sha256", key.NativeDigest.Value),
            new(Profile.BackendId, Profile.BackendRevision, Profile.BackendBuild), Profile.AdapterBuild, true);
        var native = new JsonPointerNativeLocator(Source.LogicalDocument, "/parameters/0");
        ProjectionNodeReference? link = null;
        if (selectedProjection is { } projectionId)
        {
            var projectionKey = ProjectionKey();
            var parameter = new ProjectionParameter("p", "source", new(FieldDataTypeKind.Scalar, [ScalarDataType.String]),
                FieldRequirement.Optional, null, native);
            var projectionObservation = new RevisionReportObservation(RevisionReportOperation.Projection, RevisionReportBackendStatus.Succeeded,
                new(Scope, key.NativeDigest, Profile, true), new(RevisionReportCoverage.Complete, 1, 0, 0, 0), null);
            var projection = VerifiedProjectionReport.Create(Result(projectionKey, projectionObservation, projectionId),
                Projection(projectionKey, [parameter]), new());
            link = ProjectionOriginLinks.FromVerifiedReport(projection, new()).Find(native);
        }
        ValidationDiagnosticV1[] diagnostics = valid ? [] :
            [new("invalid", DiagnosticSeverity.Error, message, false, native,
                new FailureRuleNativeOwnerV1("0"), null, false, link)];
        var report = new ValidationReportV1(valid ? OperationStatusV1.Succeeded : OperationStatusV1.InvalidMapping,
            provenance, valid, diagnostics.Length, diagnostics.Length, false, diagnostics);
        var observed = new RevisionReportObservation(key.Operation,
            valid ? RevisionReportBackendStatus.Succeeded : RevisionReportBackendStatus.InvalidMapping,
            new(Scope, key.NativeDigest, Profile, true), null, new(valid, diagnostics.Length, diagnostics.Length, false, diagnostics.Length));
        return VerifiedValidationReport.Create(Result(key, observed), report, new(), selectedProjection);
    }

    internal static RevisionReportResult Result(RevisionReportKey key, RevisionReportObservation observation, Guid? derivationId = null) => new(
        derivationId ?? DerivationId, Guid.Parse("abcdef01-0000-4000-8000-000000000008"), key, long.MaxValue,
        Actor.PrincipalId, Start, Start.AddSeconds(2),
        new(PrivateObjectId.Restore(Scope, Guid.Parse("abcdef01-0000-4000-8000-000000000009"), PrivateObjectClass.DerivedArtifact),
            Sha256Digest.Parse(new string('2', 64)), 1), observation);

    internal static ScopedRevisionId WireScope() => new(Scope.OrganizationId.ToString("D"), Scope.ProjectId.ToString("D"),
        Scope.MappingId.ToString("D"), Scope.RevisionId.ToString("D"));
}
