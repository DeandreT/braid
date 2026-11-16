using Braid.Application;
using Braid.Domain;

namespace Braid.Tests;

public sealed class ChatMappingWriteRecoveryTests
{
    [Fact]
    public async Task Recovery_resumes_only_the_existing_operation_without_resupplying_bytes()
    {
        var f = new Fake();
        var service = new ChatMappingWriteRecovery(f, f, f);
        Assert.Null((await service.ReadAsync(f.Actor, f.Conversation, f.Turn, false, default)).Single().Saved);
        Assert.Equal(0, f.Resumes);
        var result = await service.ReadAsync(f.Actor, f.Conversation, f.Turn, true, default);
        Assert.NotNull(result.Single().Saved);
        // Recreated service, retried acknowledgement, no further version is appended.
        var restarted = new ChatMappingWriteRecovery(f, f, f);
        Assert.NotNull((await restarted.ReadAsync(f.Actor, f.Conversation, f.Turn, true, default)).Single().Saved);
        Assert.Equal(1, f.Resumes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_commit_followed_by_cancellation_or_missing_ack_is_recovered_without_any_journal_completion_write(bool cancel)
    {
        var f = new Fake { AfterCommit = () => cancel ? new OperationCanceledException() : new InvalidOperationException("ack lost") };
        var service = new ChatMappingWriteRecovery(f, f, f);
        Assert.NotNull((await service.ReadAsync(f.Actor, f.Conversation, f.Turn, true, default)).Single().Saved);
        var restarted = new ChatMappingWriteRecovery(f, f, f);
        Assert.NotNull((await restarted.ReadAsync(f.Actor, f.Conversation, f.Turn, true, default)).Single().Saved);
        Assert.Equal(1, f.Resumes);
        Assert.Equal(0, f.Prepares);
    }

    [Theory]
    [InlineData("notSaved", true)]
    [InlineData("pending", false)]
    public async Task Unreserved_or_terminal_operations_are_never_submitted(string status, bool reserved)
    {
        var f = new Fake { Status = status, Reserved = reserved };
        var result = await new ChatMappingWriteRecovery(f, f, f).ReadAsync(f.Actor, f.Conversation, f.Turn, true, default);
        Assert.Null(result.Single().Saved); Assert.Equal(0, f.Resumes);
    }

    [Theory]
    [InlineData(ProjectRole.Viewer)]
    [InlineData(ProjectRole.Runner)]
    public async Task Edit_permission_is_observed_now_not_inherited_from_the_intent(ProjectRole role)
    {
        var f = new Fake { Role = role };
        await new ChatMappingWriteRecovery(f, f, f).ReadAsync(f.Actor, f.Conversation, f.Turn, true, default);
        Assert.Equal(0, f.Resumes);
        // If permission is restored, exactly the same original save can be continued, never a new call.
        f.Role = ProjectRole.Publisher;
        Assert.NotNull((await new ChatMappingWriteRecovery(f, f, f).ReadAsync(f.Actor, f.Conversation, f.Turn, true, default)).Single().Saved);
        Assert.Equal(1, f.Resumes);
    }

    [Fact]
    public async Task A_retryable_failure_is_bounded_and_is_not_reported_as_saved()
    {
        var f = new Fake { RemainPending = true };
        var result = await new ChatMappingWriteRecovery(f, f, f).ReadAsync(f.Actor, f.Conversation, f.Turn, true, default);
        Assert.Equal("pending", result.Single().Status); Assert.Null(result.Single().Saved); Assert.Equal(1, f.Resumes);
    }

    [Fact]
    public async Task Needs_source_stays_pending_and_recovery_does_not_require_native_bytes_from_the_journal()
    {
        var f = new Fake { RemainPending = true, IncludeNative = false, Result = RevisionIngestionOutcome.NeedsSource };
        var result = await new ChatMappingWriteRecovery(f, f, f).ReadAsync(f.Actor, f.Conversation, f.Turn, true, default);
        Assert.Equal("pending", result.Single().Status);
        Assert.Equal(1, f.Resumes);
        Assert.Equal(0, f.Prepares);
    }

    [Fact]
    public async Task Caller_cancellation_after_commit_leaves_a_recoverable_success()
    {
        using var cancel = new CancellationTokenSource();
        var f = new Fake { AfterCommit = () => { cancel.Cancel(); return new OperationCanceledException(cancel.Token); } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ChatMappingWriteRecovery(f, f, f)
            .ReadAsync(f.Actor, f.Conversation, f.Turn, true, cancel.Token));
        Assert.NotNull((await new ChatMappingWriteRecovery(f, f, f).ReadAsync(f.Actor, f.Conversation, f.Turn, false, default)).Single().Saved);
        Assert.Equal(1, f.Resumes);
    }

    [Fact]
    public async Task Receipt_read_failure_after_commit_can_be_recovered_after_restart_without_appending_again()
    {
        var f = new Fake { FailReadAt = 2 };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ChatMappingWriteRecovery(f, f, f)
            .ReadAsync(f.Actor, f.Conversation, f.Turn, true, default));
        Assert.NotNull((await new ChatMappingWriteRecovery(f, f, f).ReadAsync(f.Actor, f.Conversation, f.Turn, true, default)).Single().Saved);
        Assert.Equal(1, f.Resumes);
    }

    [Fact]
    public async Task A_failed_pending_save_does_not_prevent_an_independent_success_from_recovering()
    {
        var failed = new Fake { RemainPending = true, AfterCommit = () => new InvalidOperationException("temporary storage failure") };
        var succeeds = new Fake();
        var batch = new Batch(failed, succeeds);
        var receipts = await new ChatMappingWriteRecovery(batch, batch, batch).ReadAsync(batch.Actor, batch.Conversation, batch.Turn, true, default);
        Assert.Equal("pending", receipts[0].Status); Assert.Null(receipts[0].Saved);
        Assert.NotNull(receipts[1].Saved); Assert.Equal(1, failed.Resumes); Assert.Equal(1, succeeds.Resumes);
    }

    [Fact]
    public async Task A_stalled_first_save_gets_only_its_slice_and_later_saves_and_final_read_remain_fair_on_repeated_requests()
    {
        var stalled = new Fake { Stall = true };
        var succeeds = new Fake();
        var batch = new Batch(stalled, succeeds);
        var recovery = new ChatMappingWriteRecovery(batch, batch, batch, TimeSpan.FromMilliseconds(40));
        using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        for (var request = 0; request < 2; request++)
        {
            var receipts = await recovery.ReadAsync(batch.Actor, batch.Conversation, batch.Turn, true, bounded.Token);
            Assert.Equal("pending", receipts[0].Status); Assert.NotNull(receipts[1].Saved);
        }
        Assert.Equal(2, stalled.Resumes); Assert.Equal(1, succeeds.Resumes);
        Assert.False(bounded.IsCancellationRequested);
    }

    private sealed class Batch(Fake first, Fake second) : Fake
    {
        public override async Task<IReadOnlyList<ChatMappingWriteReceipt>> ReadAsync(ActorContext actor, ChatConversationKey conversation, Guid turnId, CancellationToken ct) =>
            [.. await first.ReadAsync(actor, conversation, turnId, ct), .. await second.ReadAsync(actor, conversation, turnId, ct)];
        public override Task<RevisionIngestionResult> ResumeAsync(ActorContext actor, ProjectScope project, Guid operationId,
            CancellationToken cancellationToken = default) =>
            (operationId == first.Operation ? first : second).ResumeAsync(actor, project, operationId, cancellationToken);
    }

    private class Fake : IChatMappingWriteJournal, IRevisionIngestionService, IProjectReader
    {
        internal readonly ActorContext Actor = WorkspaceToolFixture.Actor;
        internal readonly ChatConversationKey Conversation = ChatConversationKey.ForWorkspace(WorkspaceToolFixture.Organization, WorkspaceToolFixture.Actor.PrincipalId, Guid.NewGuid());
        internal readonly Guid Turn = Guid.NewGuid(), Key = Guid.NewGuid(), Round = Guid.NewGuid(), Operation = Guid.NewGuid(), Revision = Guid.NewGuid();
        internal string Status = "pending";
        internal bool Reserved = true, RemainPending, Stall;
        internal bool IncludeNative = true;
        internal RevisionIngestionOutcome Result = RevisionIngestionOutcome.ClaimLost;
        internal ProjectRole Role = ProjectRole.Publisher;
        internal int Resumes, Prepares;
        internal int Reads, FailReadAt;
        internal Func<Exception>? AfterCommit;
        private static NativeArtifact Native => MappingDraftPersistenceTests.Checkpoint(WorkspaceToolFixture.Head).Artifact;
        private static ChatMappingWriteFact Proposal => new(ChatMappingWriteOutcome.Proposed, ChatMappingWriteReason.NotPermitted,
            WorkspaceToolFixture.Head, 2, null, null, null, null, 1, Sha256Digest.FromBytes([1]), [], false);

        public Task<bool> PrepareAsync(ActorContext actor, ChatMappingWriteIntent intent, CancellationToken ct) { Prepares++; throw new InvalidOperationException(); }
        public virtual Task<IReadOnlyList<ChatMappingWriteReceipt>> ReadAsync(ActorContext actor, ChatConversationKey conversation, Guid turnId, CancellationToken ct)
        {
            if (++Reads == FailReadAt) throw new InvalidOperationException("Journal unavailable after commit");
            return Task.FromResult<IReadOnlyList<ChatMappingWriteReceipt>>([new(Key, Round, Status, Proposal, Reserved ? Operation : null,
                Status == "saved" ? Revision : null, Status == "saved" ? 3 : null, Status == "pending" && Reserved && IncludeNative ? Native : null)]);
        }
        public virtual async Task<RevisionIngestionResult> ResumeAsync(ActorContext actor, ProjectScope project, Guid operationId,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(Actor, actor); Assert.Equal(WorkspaceToolFixture.Head.ProjectId, project.ProjectId);
            Assert.Equal(WorkspaceToolFixture.Organization, project.OrganizationId); Assert.Equal(Operation, operationId);
            Resumes++;
            if (Stall) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (!RemainPending) Status = "saved";
            if (AfterCommit?.Invoke() is { } error) throw error;
            return RevisionIngestionResult.WithoutReceipt(Result);
        }
        public Task<ProjectAccess?> GetAccessAsync(ActorContext actor, ProjectScope scope, CancellationToken cancellationToken = default) => Task.FromResult<ProjectAccess?>(new(scope, actor.PrincipalId, Role));
        public Task<ProjectPage> ListVisibleProjectsAsync(ActorContext actor, Guid organizationId, int limit, Guid? afterProjectId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ProjectSummary?> GetVisibleProjectAsync(ActorContext actor, ProjectScope scope, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RevisionIngestionResult> CreateAsync(ActorContext actor, ProjectScope project, Guid idempotencyKey, CreateRevisionRequest request, Stream nativeSource, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RevisionIngestionResult> AppendAsync(ActorContext actor, MappingScope target, Guid idempotencyKey,
            AppendRevisionRequest request, Stream nativeSource, CancellationToken cancellationToken = default) =>
            throw new Xunit.Sdk.XunitException("Recovery must never append or resupply source.");
        public Task<RevisionIngestionReceipt?> GetOwnReceiptAsync(ActorContext actor, ProjectScope project, Guid operationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
