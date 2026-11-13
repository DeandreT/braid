using System.Runtime.Versioning;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

public sealed partial class MappingDraftPersistenceTests
{
    [PostgresFact]
    [SupportedOSPlatform("linux")]
    public async Task DraftPreviewRetainsItsOwnCheckpointAndUsesAuthorEditAuthorityAfterDiscard()
    {
        await using var test = await PreviewRunTestSupport.CreateAsync(fixture);
        await using var reader = new PostgresMappingDraftDatabase(test.Case.RegressionConnectionString("weft_draft_reader"), MappingDraftDatabaseProfile.Reader);
        await using var writer = new PostgresMappingDraftDatabase(test.Case.RegressionConnectionString("weft_draft_writer"), MappingDraftDatabaseProfile.Writer);
        var drafts = new PostgresMappingDraftStore(reader, writer);
        var mapping = new MappingScope(test.Revision.OrganizationId, test.Revision.ProjectId, test.Revision.MappingId);
        var immutableBase = (await drafts.SelectBaseAsync(test.Alice, mapping, test.Revision.RevisionId, false))!;
        var checkpoint = Checkpoint(test.Revision);
        var draft = (await drafts.CreateAsync(test.Alice, mapping, Guid.NewGuid(), immutableBase, checkpoint)).Draft!;
        var subject = new DraftPreviewSubject(test.Revision, draft.Id, draft.Version, checkpoint.Artifact.Digest, checkpoint.Artifact.ByteCount);
        var submission = test.Submission() with { Subject = subject };
        var submitted = await test.Service().SubmitAsync(test.Alice, submission);
        Assert.Equal(PreviewSubmitOutcome.Created, submitted.Outcome);
        var run = submitted.RunId!.Value;
        var snapshot = await test.Service().ReadAsync(test.Alice, mapping.OrganizationId, run);
        Assert.Equal(subject, snapshot!.Subject);
        Assert.Equal(TimeSpan.FromHours(24), snapshot.ExpiresAtUtc - snapshot.CreatedAtUtc);
        await drafts.DiscardAsync(test.Alice, mapping, draft.Id, 0);
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.mapping_draft_checkpoints"));
        Assert.Equal(checkpoint.Artifact.CopyBytes(), await test.Case.ScalarAsync<byte[]>("SELECT content FROM weft.preview_documents WHERE direction=3"));
        // Replaying an admitted intent never rereads today's draft or base native object.
        Assert.Equal(PreviewSubmitOutcome.Replayed, (await test.Service().SubmitAsync(test.Alice, submission)).Outcome);
        Assert.Equal(PreviewSubmitOutcome.Conflict, (await test.Service().SubmitAsync(test.Alice, submission with { Primary = new(null, "different"u8) })).Outcome);
        var backend = new PreviewRunTestSupport.ScriptedBackend(MappingPreviewStatus.InvalidInput);
        Assert.Equal(PreviewExecutionOutcome.Completed, await test.Executor(backend).ExecuteAsync(mapping.OrganizationId, run, Guid.NewGuid()));
        Assert.Equal(checkpoint.Artifact.CopyBytes(), backend.LastRequest!.Artifact.CopyBytes());
        Assert.Equal(subject, (await test.Service().ReadAsync(test.Alice, mapping.OrganizationId, run))!.Subject);
        await test.Case.ExecuteAsync("UPDATE weft.project_memberships SET role='runner',version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3",
            mapping.OrganizationId, mapping.ProjectId, test.Alice.PrincipalId.Value);
        Assert.Null(await test.Service().ReadAsync(test.Alice, mapping.OrganizationId, run));
        Assert.Equal(PreviewCancelOutcome.NotAuthorizedOrNotFound, await test.Service().CancelAsync(test.Alice, mapping.OrganizationId, run));
        Assert.Equal(PreviewSubmitOutcome.NotAuthorizedOrNotFound, (await test.Service().SubmitAsync(test.Alice, submission)).Outcome);
        // Ordinary revision previews keep the Runner permission and existing storage path.
        var revisionSubmission = test.Submission();
        var ordinary = await test.Service().SubmitAsync(test.Alice, revisionSubmission);
        Assert.Equal(PreviewSubmitOutcome.Created, ordinary.Outcome);
        Assert.IsType<RevisionPreviewSubject>((await test.Service().ReadAsync(test.Alice, mapping.OrganizationId, ordinary.RunId!.Value))!.Subject);
    }
}
