using System.Text.Json;
using Weft.Application;
using Weft.Application.Comparisons;
using Weft.Contracts.Comparisons;
using Weft.Domain;

namespace Weft.Tests;

public sealed partial class RevisionComparisonTests
{
    [Fact]
    public void ACheckpointIsCitedAsAnExactDraftVersionAndNeverAsACommittedRevision()
    {
        var fixture = Direct();
        var (draft, checkpoint) = Draft(fixture);
        var format = RevisionReportFormat.FromKey(fixture.Key);
        var result = new RevisionComparisonService().Compare(fixture.Side(fixture.Validation(true)),
            RevisionComparisonSide.CreateDraft(draft, checkpoint, format, fixture.Key.Policy));
        Assert.Equal("revision", result.Left.Kind);
        Assert.Equal("draft", result.Right.Kind);
        Assert.Null(result.Right.Revision);
        Assert.Null(result.Right.Sequence);
        Assert.Null(result.Right.Projection);
        Assert.Null(result.Right.Validation);
        Assert.NotNull(result.Right.Draft);
        Assert.Equal(draft.Id.ToString("D"), result.Right.Draft.DraftId);
        Assert.Equal("9007199254740993", result.Right.Draft.Version);
        Assert.Equal(checkpoint.Artifact.Digest.Value, result.Right.NativeDigest.Value);
        Assert.False(result.HasObservedDifferences);
        Assert.False(result.HasUncertainty);
    }

    [Fact]
    public void AStaleOrSubstitutedCheckpointCannotEnterComparison()
    {
        var fixture = Direct();
        var (draft, checkpoint) = Draft(fixture);
        var format = RevisionReportFormat.FromKey(fixture.Key);
        Invalid(() => RevisionComparisonSide.CreateDraft(draft with { Version = draft.Version + 1 }, checkpoint, format, fixture.Key.Policy));
        Invalid(() => RevisionComparisonSide.CreateDraft(draft with { Checkpoint = draft.Checkpoint! with { NativeDigest = Sha256Digest.FromBytes([9]) } }, checkpoint, format, fixture.Key.Policy));
        Invalid(() => RevisionComparisonSide.CreateDraft(draft, checkpoint, new(format.BackendId, format.Operation, format.SchemaVersion, "other.algorithm", format.CodecVersion), fixture.Key.Policy));
    }

    private static (MappingDraftSnapshot, MappingEditCheckpoint) Draft(Fixture fixture)
    {
        var artifact = NativeArtifact.Admit(fixture.Catalog.Scope, [1, 2, 3], MappingEditPolicy.MaximumNativeBytes);
        var validation = fixture.Validation(true).Report;
        using var empty = JsonDocument.Parse("[]");
        var provenance = new IngestionProvenance(artifact.Scope, artifact.Digest,
            new(Profile.BackendId, Profile.BackendRevision, Profile.BackendBuild, Profile.AdapterBuild), true);
        var evidence = IngestionEvidenceDocument.Create(new(IngestionBackendStatus.Succeeded, provenance, true, 0, 0, false), empty.RootElement);
        var checkpoint = new MappingEditCheckpoint(artifact, evidence, validation, fixture.Report);
        const long version = 9007199254740993;
        var draft = new MappingDraftSnapshot(Id(7000), new(artifact.Scope.OrganizationId, artifact.Scope.ProjectId, artifact.Scope.MappingId), Actor,
            new(artifact.Scope, fixture.Catalog.Sequence, fixture.Key.Source.NativeObject), MappingDraftState.Open, version, 1, Sha256Digest.FromBytes([4]),
            Started, Started, Started.AddDays(30), new(version, artifact.Digest, artifact.ByteCount, true, 0), null, null, null, null);
        return (draft, checkpoint);
    }
}
