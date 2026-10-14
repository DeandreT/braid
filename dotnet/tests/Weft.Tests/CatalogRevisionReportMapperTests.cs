using System.Text.Json;
using Weft.Api.Catalog;
using Weft.Application;
using Weft.Contracts.Catalog;
using Weft.Contracts.Operations;
using Weft.Contracts.Projections;
using Weft.Domain;
using F = Weft.Tests.CatalogRevisionReportTestFacts;

namespace Weft.Tests;

public sealed class CatalogRevisionReportMapperTests
{
    [Theory]
    [InlineData(ProjectRole.Viewer)]
    [InlineData(ProjectRole.Runner)]
    [InlineData(ProjectRole.Publisher)]
    [InlineData(ProjectRole.Administrator)]
    public void ReportFlagsUseReadMetadataForEveryRoleAndStayIndependentOfCreation(ProjectRole role)
    {
        var revision = F.Revision(role);
        var project = new CatalogProject(new(revision.Access.Scope, "project", "", F.Actor.PrincipalId, F.Start, F.Start, 1), revision.Access);
        var off = CatalogPublicMapper.Project(project);
        Assert.False(off.Access.Actions.ReadReports);
        Assert.False(off.Access.Actions.DeriveReports);
        var on = CatalogPublicMapper.Project(project, readReportsEnabled: true, deriveReportsEnabled: true);
        Assert.True(on.Access.Actions.ReadReports);
        Assert.True(on.Access.Actions.DeriveReports);
        Assert.False(on.Access.Actions.CreateProjects);
        Assert.False(on.Access.Actions.UploadRevisions);
        var context = CatalogPublicMapper.Context(new(F.Scope.OrganizationId, F.Actor.PrincipalId, "organization"), "token",
            readReportsEnabled: true, deriveReportsEnabled: true);
        Assert.True(context.Features.ReadReports);
        Assert.True(context.Features.DeriveReports);
        Assert.False(context.OrganizationActions.CreateProjects);
    }

    [Theory]
    [InlineData(RevisionReportReadOutcome.NotAuthorizedOrNotFound, 404, CatalogErrorCodeV1.NotFound)]
    [InlineData(RevisionReportReadOutcome.AdmissionBusy, 429, CatalogErrorCodeV1.RevisionReportAdmissionBusy)]
    [InlineData(RevisionReportReadOutcome.ProfileUnavailable, 503, CatalogErrorCodeV1.RevisionReportUnavailable)]
    public void NoDataReadOutcomesStayTransportErrors(RevisionReportReadOutcome outcome, int status, CatalogErrorCodeV1 code)
    {
        var mapped = Map(RevisionReportReadResult.WithoutData(outcome));
        Assert.Equal(status, mapped.StatusCode);
        Assert.Equal(code, mapped.Error!.Code);
        Assert.Null(mapped.Projection);
        Assert.Null(mapped.Source);
        Assert.Null(mapped.Limits);
    }

    [Theory]
    [InlineData(RevisionReportEnsureOutcome.ClaimLost, 409, CatalogErrorCodeV1.RevisionReportClaimLost)]
    [InlineData(RevisionReportEnsureOutcome.SelectionChanged, 409, CatalogErrorCodeV1.RevisionReportSelectionChanged)]
    [InlineData(RevisionReportEnsureOutcome.GenerationExhausted, 409, CatalogErrorCodeV1.RevisionReportGenerationExhausted)]
    [InlineData(RevisionReportEnsureOutcome.AdmissionBusy, 429, CatalogErrorCodeV1.RevisionReportAdmissionBusy)]
    [InlineData(RevisionReportEnsureOutcome.NotAuthorizedOrNotFound, 404, CatalogErrorCodeV1.NotFound)]
    [InlineData(RevisionReportEnsureOutcome.ProfileUnavailable, 503, CatalogErrorCodeV1.RevisionReportUnavailable)]
    public void EnsureRefusalsNeverBecomeReportWrappers(RevisionReportEnsureOutcome outcome, int status, CatalogErrorCodeV1 code)
    {
        var mapped = CatalogRevisionReportMapper.Ensure(F.Actor, F.Revision(), RevisionReportOperation.Projection, null,
            RevisionReportEnsureResult.WithoutData(outcome), new(true, true), default);
        Assert.Equal(status, mapped.StatusCode);
        Assert.Equal(code, mapped.Error!.Code);
        Assert.Null(mapped.Projection);
        Assert.Null(mapped.Location);
    }

    [Theory]
    [InlineData(false, 60)]
    [InlineData(true, 30)]
    public void AcceptedEnsureUsesActualDbDeltaRoundedUpAndCanonicalCurrentUrl(bool retry, int seconds)
    {
        var key = F.ProjectionKey();
        var result = retry ? RevisionReportEnsureResult.ForRetryDelayed(F.Failed(key))
            : RevisionReportEnsureResult.ForPending(F.Pending(key));
        var mapped = CatalogRevisionReportMapper.Ensure(F.Actor, F.Revision(), key.Operation, null, result, new(true, true), default);
        Assert.Equal(202, mapped.StatusCode);
        Assert.Equal(seconds, mapped.RetryAfterSeconds);
        Assert.Equal($"/api/v1/organizations/{F.Scope.OrganizationId:D}/projects/{F.Scope.ProjectId:D}/mappings/{F.Scope.MappingId:D}/revisions/{F.Scope.RevisionId:D}/reports/projection", mapped.Location);
        Assert.Null(mapped.Projection!.Result);
        Assert.Null(mapped.Projection.Report);
        Assert.Equal("9223372036854775807", mapped.Projection.CurrentAttempt!.Generation);
        Assert.Equal(retry ? OperationStatusV1.ResourceLimit : (OperationStatusV1?)null, mapped.Projection.Status);
    }

    [Fact]
    public void ActualFalseValidationMapsAvailableWithoutAReconstructedAttempt()
    {
        var verified = F.VerifiedValidation();
        var mapped = CatalogRevisionReportMapper.Read(F.Actor, F.Revision(), RevisionReportOperation.Validation,
            verified.Metadata.DerivationId, null, RevisionReportReadResult.ForAvailable(F.Authority(), verified), new(true), default);
        Assert.Equal(200, mapped.StatusCode);
        Assert.Equal(OperationStatusV1.InvalidMapping, mapped.Validation!.Status);
        Assert.False(mapped.Validation.Report!.IsValid);
        Assert.Equal(CatalogRevisionReportStateV1.Available, mapped.Validation.State);
        Assert.Null(mapped.Validation.CurrentAttempt);
        Assert.Null(mapped.Validation.NextAttemptAtUtc);
        Assert.Equal("9223372036854775807", mapped.Validation.Result!.Generation);
        var json = JsonSerializer.Serialize(mapped.Validation, ProjectionJson.CreateOptions());
        Assert.DoesNotContain("cacheEntryId", json, StringComparison.Ordinal);
        Assert.DoesNotContain("candidateObjectId", json, StringComparison.Ordinal);
        Assert.DoesNotContain("lease", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("policy", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(RevisionReportReadOutcome.ObjectUnavailable, CatalogRevisionReportReadOutcomeV1.ObjectUnavailable)]
    [InlineData(RevisionReportReadOutcome.StorageIntegrity, CatalogRevisionReportReadOutcomeV1.StorageIntegrity)]
    [InlineData(RevisionReportReadOutcome.UnsupportedFormat, CatalogRevisionReportReadOutcomeV1.UnsupportedFormat)]
    [InlineData(RevisionReportReadOutcome.ResourceLimit, CatalogRevisionReportReadOutcomeV1.ResourceLimit)]
    public void ObjectRefusalKeepsSuccessIdentityAndHonestStatusWithoutPayload(RevisionReportReadOutcome refusal,
        CatalogRevisionReportReadOutcomeV1 wire)
    {
        var verified = F.VerifiedProjection();
        var mapped = Map(RevisionReportReadResult.ForReadFailure(F.Authority(), verified.Metadata, refusal));
        Assert.Equal(200, mapped.StatusCode);
        Assert.Equal(wire, mapped.Projection!.ReadOutcome);
        Assert.Equal(CatalogRevisionReportStateV1.Available, mapped.Projection.State);
        Assert.Equal(OperationStatusV1.Succeeded, mapped.Projection.Status);
        Assert.NotNull(mapped.Projection.Result);
        Assert.Null(mapped.Projection.Report);
        Assert.Null(mapped.Projection.Failure);
    }

    [Fact]
    public void DiscardedFailedDiagnosticsAreNotInventedAndNullableStatusStaysNull()
    {
        var failed = F.Failed(F.ProjectionKey(), null, RevisionReportFailureCategory.StorageIo);
        var mapped = Map(RevisionReportReadResult.ForSnapshot(failed));
        Assert.Equal(CatalogRevisionReportStateV1.Failed, mapped.Projection!.State);
        Assert.Null(mapped.Projection.Status);
        Assert.Null(mapped.Projection.CurrentAttempt!.Status);
        Assert.Equal(CatalogRevisionReportFailureV1.StorageIo, mapped.Projection.Failure);
        Assert.Null(mapped.Projection.Report);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void DishonestActorScopeDigestAndNativeCountStayInconsistencies(int changed)
    {
        var verified = F.VerifiedProjection();
        var summary = F.Revision().Summary;
        var scope = changed == 1 ? new RevisionScope(Guid.Parse("abcdef01-0000-4000-8000-0000000000aa"),
            F.Scope.ProjectId, F.Scope.MappingId, F.Scope.RevisionId) : F.Scope;
        var revision = new CatalogRevision(new(scope, summary.Sequence, summary.Admission, summary.Creator, summary.CommittedAtUtc,
            summary.OriginalFilename, summary.LogicalDocument, changed == 2 ? Sha256Digest.Parse(new string('3', 64)) : summary.NativeDigest,
            changed == 3 ? 4 : summary.NativeByteCount, summary.BackendId),
            new(new(scope.OrganizationId, scope.ProjectId), changed == 0
                ? new PrincipalId(Guid.Parse("abcdef01-0000-4000-8000-0000000000bb")) : F.Actor.PrincipalId, ProjectRole.Viewer));
        Assert.Throws<InvalidOperationException>(() => CatalogRevisionReportMapper.Read(F.Actor, revision, RevisionReportOperation.Projection,
            null, null, RevisionReportReadResult.ForAvailable(F.Authority(), verified), new(true), default));
    }

    [Fact]
    public void RetainedAndVerifiedLinkSelectorsMustMatchAndWrongOperationCannotBeMapped()
    {
        var verified = F.VerifiedProjection();
        var wrong = Guid.Parse("abcdef01-0000-4000-8000-0000000000cc");
        Assert.Throws<InvalidOperationException>(() => CatalogRevisionReportMapper.Read(F.Actor, F.Revision(), RevisionReportOperation.Projection,
            wrong, null, RevisionReportReadResult.ForAvailable(F.Authority(), verified), new(true), default));
        var linked = F.VerifiedValidation(selectedProjection: F.ProjectionId);
        var read = RevisionReportReadResult.ForAvailable(F.Authority(), linked);
        Assert.Throws<InvalidOperationException>(() => CatalogRevisionReportMapper.Read(F.Actor, F.Revision(), RevisionReportOperation.Validation,
            null, wrong, read, new(true), default));
        Assert.Throws<InvalidOperationException>(() => Map(read));
    }

    internal static CatalogRevisionReportMappedResponse Map(RevisionReportReadResult read, RevisionReportPolicy? caps = null) =>
        CatalogRevisionReportMapper.Read(F.Actor, F.Revision(), RevisionReportOperation.Projection, null, null, read, new(true, false, caps), default);
}
