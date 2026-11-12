using Weft.Application;
using Weft.Domain;

namespace Weft.Tests;

public sealed class DraftPreviewExecutorTests
{
    [Theory]
    [InlineData(PreviewExecutionProfile.LegacyJson)]
    [InlineData(PreviewExecutionProfile.Document)]
    public async Task ExecutionUsesTheRetainedCheckpointWithoutReadingTheBaseObject(PreviewExecutionProfile profile)
    {
        var test = new Ports(profile);
        var executor = new PreviewRunExecutor(test, test, test, TimeProvider.System);
        Assert.Equal(PreviewExecutionOutcome.Completed, await executor.ExecuteAsync(test.Claim.OrganizationId, test.Claim.RunId, Guid.NewGuid()));
        Assert.Equal(test.Artifact.CopyBytes(), test.Executed!.Artifact.CopyBytes());
        Assert.Equal(PreviewRunOutcome.InvalidInput, test.Completed!.Outcome);
        Assert.Equal(0, test.ObjectReads);
        Assert.Equal(profile, test.Executed.Profile);
    }

    [Fact]
    public void CsvRevisionRequestsCannotRelabelAnUncommittedDraftSubject()
    {
        var test = new Ports();
        var submission = new PreviewRunSubmission(test.Claim.Subject, Guid.NewGuid(), PreviewTargetSelection.All,
            new(null, [], CsvPreviewTestData.Dialect), [], [])
        { Profile = PreviewExecutionProfile.CsvDocument };
        Assert.Throws<PreviewRunException>(() => PreviewRequestDocument.Encode(submission, PreviewLimits.Default));
        var revision = submission with { Subject = new RevisionPreviewSubject(test.Claim.Subject.ExecutionScope) };
        var restored = PreviewRequestDocument.Decode(PreviewRequestDocument.Encode(revision, PreviewLimits.Default));
        Assert.Equal(revision.Subject, restored.Subject);
        Assert.Equal(PreviewExecutionProfile.CsvDocument, restored.Profile);
        Assert.Equal(0, Assert.Single(restored.Inputs).ByteLength);
        Assert.Equal(CsvPreviewTestData.Dialect, restored.Inputs[0].CsvDialect);
    }

    [Theory]
    [InlineData("digest")]
    [InlineData("length")]
    [InlineData("version")]
    [InlineData("source")]
    public async Task SubstitutedStoredSubjectsOrNativeSourcesNeverReachExecution(string mismatch)
    {
        var test = new Ports();
        var draft = (DraftPreviewSubject)test.Claim.Subject;
        test.Claim = mismatch switch
        {
            "digest" => test.Claim with { Subject = new DraftPreviewSubject(draft.ExecutionScope, draft.DraftId, draft.Version, Sha256Digest.FromBytes([9]), draft.NativeByteCount) },
            "length" => test.Claim with { Subject = new DraftPreviewSubject(draft.ExecutionScope, draft.DraftId, draft.Version, draft.NativeDigest, draft.NativeByteCount + 1) },
            "version" => test.Claim with { Subject = new DraftPreviewSubject(draft.ExecutionScope, draft.DraftId, draft.Version + 1, draft.NativeDigest, draft.NativeByteCount) },
            _ => test.Claim with { Native = new StoredPreviewNative(new(PrivateObjectId.Restore(draft.ExecutionScope, Guid.NewGuid(), PrivateObjectClass.NativeArtifact), draft.NativeDigest, draft.NativeByteCount)) }
        };
        var executor = new PreviewRunExecutor(test, test, test, TimeProvider.System);
        Assert.Equal(PreviewExecutionOutcome.Completed, await executor.ExecuteAsync(test.Claim.OrganizationId, test.Claim.RunId, Guid.NewGuid()));
        Assert.Null(test.Executed);
        Assert.Equal(PreviewRunOutcome.ProtocolFailure, test.Completed!.Outcome);
        Assert.Equal(0, test.ObjectReads);
    }

    private sealed class Ports : IPreviewRunStore, IPrivateObjectStore, IMappingPreviewBackend
    {
        internal readonly NativeArtifact Artifact;
        internal PreviewRunClaim Claim;
        internal MappingPreviewRequest? Executed;
        internal PreviewRunCompletion? Completed;
        internal int ObjectReads;
        private readonly PreviewRunSubmission _submission;
        internal Ports(PreviewExecutionProfile profile = PreviewExecutionProfile.LegacyJson)
        {
            var scope = new RevisionScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            Artifact = NativeArtifact.Admit(scope, "opaque-checkpoint-not-the-base"u8, MappingEditPolicy.MaximumNativeBytes);
            var subject = new DraftPreviewSubject(scope, Guid.NewGuid(), 9007199254740993, Artifact.Digest, Artifact.ByteCount);
            _submission = new(subject, Guid.NewGuid(), PreviewTargetSelection.All, new(null, "input"u8), [], []) { Profile = profile };
            Claim = new(scope.OrganizationId, Guid.NewGuid(), 1, Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(1), subject,
                new(Guid.NewGuid()), PreviewRequestDocument.Encode(_submission, PreviewLimits.Default), PreviewRequestDocument.Clock(DateTimeOffset.UtcNow), new CheckpointPreviewNative(Artifact));
        }
        public Task<PreviewClaimResult> ClaimAsync(Guid organizationId, Guid runId, Guid owner, TimeSpan lease, CancellationToken cancellationToken = default) => Task.FromResult(new PreviewClaimResult(PreviewClaimOutcome.Claimed, Claim));
        public Task<IReadOnlyList<PreviewStoredInput>> ReadInputsAsync(PreviewRunClaim claim, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PreviewStoredInput>>([new(0, null, _submission.Primary.Digest, _submission.Primary.CopyBytes())]);
        public Task<PreviewClaimObservation> ObserveAsync(PreviewRunClaim claim, CancellationToken cancellationToken = default) => Task.FromResult(new PreviewClaimObservation(true, false));
        public Task<bool> CompleteAsync(PreviewRunClaim claim, PreviewRunCompletion completion, CancellationToken cancellationToken = default) { Completed = completion; return Task.FromResult(true); }
        public Task<MappingPreviewResult> PreviewAsync(MappingPreviewRequest request, CancellationToken cancellationToken = default)
        {
            Executed = request;
            return Task.FromResult(new MappingPreviewResult(MappingPreviewStatus.InvalidInput,
                new(request.Artifact.Scope, request.Artifact.Digest, "ferrule", "engine", "worker", "adapter", true),
                request.Profile == PreviewExecutionProfile.Document ? DocumentPreviewPolicy.Profile : PreviewPolicy.Profile,
                0, [], new("fixture", "Fixture refusal.", false, null, false)));
        }
        public Task<byte[]> ReadVerifiedAsync(RevisionScope scope, PrivateObjectReference reference, CancellationToken cancellationToken = default) { ObjectReads++; throw new InvalidOperationException("A draft preview must never read base native bytes."); }
        public Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(RevisionScope scope, PrivateObjectReference reference, Stream source, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PreviewSubmitResult> SubmitAsync(ActorContext actor, PreviewRunRecord record, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> ReleaseAsync(PreviewRunClaim claim, TimeSpan retryDelay, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PreviewCancelOutcome> CancelAsync(ActorContext actor, Guid organizationId, Guid runId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
