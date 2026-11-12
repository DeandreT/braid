using Weft.Application;
using Weft.Domain;

namespace Weft.Tests;

/// <summary>Application boundary tests; the fake store does not establish database authorization or atomicity.</summary>
public sealed class ChatMappingEditApprovalTests
{
    private static Guid Id(int n) => Guid.Parse($"00000000-0000-4000-8000-{n:000000000000}");
    private static readonly ActorContext Actor = new(new PrincipalId(Id(1)));
    private static readonly RevisionScope Base = new(Id(2), Id(3), Id(4), Id(5));
    private static readonly ChatConversationKey Conversation = ChatConversationKey.ForWorkspace(Id(2), Actor.PrincipalId, Id(6));
    private static ChatMappingEditProposal Proposal(string? edit = null, long sequence = 2) => new(Conversation, Id(7), Id(8), "call-one", sequence,
        new(NativeArtifact.Admit(Base, "exact edited native"u8, 100), MappingEditScript.Parse(System.Text.Encoding.UTF8.GetBytes(edit ?? MappingEditScriptTests.Bind)), []));

    [Fact]
    public void Identity_is_stable_complete_and_content_cannot_replace_a_call()
    {
        var p = Proposal(); var same = Proposal(); var changed = Proposal(sequence: 3);
        Assert.Equal(p.ProposalId, same.ProposalId);
        Assert.Equal(p.RequestKey, same.RequestKey);
        Assert.NotEqual(p.ProposalId, p.RequestKey);
        Assert.NotEqual(Weft.Api.Chat.Workspace.WorkspaceToolService.WriteKey(p.TurnId, p.CallId), p.RequestKey);
        Assert.Equal(p.Fingerprint, same.Fingerprint);
        Assert.Equal(p.ProposalId, changed.ProposalId);
        Assert.NotEqual(p.Fingerprint, changed.Fingerprint);
        Assert.Equal(p.Content.Operations.Digest, MappingEditScript.Parse(p.Content.Operations.CopyUtf8()).Digest);
        Array.Fill(p.Content.Checkpoint.CopyBytes(), (byte)0);
        Assert.Equal("exact edited native"u8.ToArray(), p.Content.Checkpoint.CopyBytes());
        var differentNative = new ChatMappingEditProposal(Conversation, p.TurnId, p.RoundId, p.CallId, 2,
            new(NativeArtifact.Admit(Base, "another checkpoint"u8, 100), p.Content.Operations, []));
        var differentEdit = new ChatMappingEditProposal(Conversation, p.TurnId, p.RoundId, p.CallId, 2,
            new(p.Content.Checkpoint, MappingEditScript.Parse(System.Text.Encoding.UTF8.GetBytes(MappingEditScriptTests.Bind.Replace("9007199254740993", "42", StringComparison.Ordinal))), []));
        Assert.Equal(p.RequestKey, differentNative.RequestKey);
        Assert.NotEqual(p.Fingerprint, differentNative.Fingerprint);
        Assert.NotEqual(p.Fingerprint, differentEdit.Fingerprint);
        foreach (var changedIdentity in new[]
        {
            new ChatMappingEditProposal(Conversation, Id(70), p.RoundId, p.CallId, 2, p.Content),
            new ChatMappingEditProposal(Conversation, p.TurnId, Id(80), p.CallId, 2, p.Content),
            new ChatMappingEditProposal(Conversation, p.TurnId, p.RoundId, "call-two", 2, p.Content),
            new ChatMappingEditProposal(ChatConversationKey.ForWorkspace(Id(2), Actor.PrincipalId, Id(60)), p.TurnId, p.RoundId, p.CallId, 2, p.Content),
            new ChatMappingEditProposal(ChatConversationKey.ForWorkspace(Id(20), Actor.PrincipalId, Id(6)), p.TurnId, p.RoundId, p.CallId, 2,
                new(NativeArtifact.Admit(new(Id(20), Base.ProjectId, Base.MappingId, Base.RevisionId), p.Content.Checkpoint.CopyBytes(), 100), p.Content.Operations, [])),
            new ChatMappingEditProposal(ChatConversationKey.ForWorkspace(Id(2), new PrincipalId(Id(90)), Id(6)), p.TurnId, p.RoundId, p.CallId, 2, p.Content)
        })
        {
            Assert.NotEqual(p.RequestKey, changedIdentity.RequestKey);
            Assert.NotEqual(p.Fingerprint, changedIdentity.Fingerprint);
        }
    }

    [Fact]
    public async Task Creation_reads_and_generic_recovery_cannot_approve_or_first_reserve()
    {
        var f = new Fake(); var service = f.Service(); var p = f.Proposal;
        await service.CreateAsync(Actor, p);
        await service.ReadAsync(Actor, Conversation, p.TurnId, p.ProposalId, p.Fingerprint);
        await service.RecoverAsync(Actor, Conversation, p.TurnId, p.ProposalId, p.Fingerprint);
        f.State = f.Prepared(); // Simulate a crash after atomic approval, before reservation.
        await service.RecoverAsync(Actor, Conversation, p.TurnId, p.ProposalId, p.Fingerprint);
        Assert.Equal(0, f.Approvals); Assert.Empty(f.Appends); Assert.Equal(0, f.Resumes);
        await service.ApproveAsync(Actor, Conversation, p.TurnId, p.ProposalId, p.Fingerprint);
        Assert.Single(f.Appends);
    }

    [Theory]
    [InlineData(ChatMappingEditCreateOutcome.Created)]
    [InlineData(ChatMappingEditCreateOutcome.Existing)]
    [InlineData(ChatMappingEditCreateOutcome.Conflict)]
    [InlineData(ChatMappingEditCreateOutcome.Refused)]
    public async Task Creation_preserves_explicit_outcome_without_authorizing_a_save(ChatMappingEditCreateOutcome outcome)
    {
        var f = new Fake();
        f.CreateResult = new(outcome, outcome is ChatMappingEditCreateOutcome.Created or ChatMappingEditCreateOutcome.Existing ? f.State : null);
        Assert.Equal(f.CreateResult, await f.Service().CreateAsync(Actor, f.Proposal));
        Assert.Equal(0, f.Approvals); Assert.Empty(f.Appends);
    }

    [Fact]
    public async Task Creation_refuses_invalid_result_shapes_and_changed_summaries()
    {
        var f = new Fake();
        foreach (var invalid in new[]
        {
            new ChatMappingEditCreateResult(ChatMappingEditCreateOutcome.Created),
            new ChatMappingEditCreateResult(ChatMappingEditCreateOutcome.Existing),
            new ChatMappingEditCreateResult(ChatMappingEditCreateOutcome.Conflict, f.State),
            new ChatMappingEditCreateResult(ChatMappingEditCreateOutcome.Refused, f.State),
            new ChatMappingEditCreateResult((ChatMappingEditCreateOutcome)99),
            new ChatMappingEditCreateResult(ChatMappingEditCreateOutcome.Created, f.Prepared())
        })
        {
            f.CreateResult = invalid;
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service().CreateAsync(Actor, f.Proposal));
        }
        var p = f.Proposal;
        var changed = new ChatMappingEditProposal(p.Conversation, p.TurnId, p.RoundId, p.CallId, p.BaseSequence,
            new(p.Content.Checkpoint, p.Content.Operations, [new("field", "before", "after")]));
        f.CreateResult = new(ChatMappingEditCreateOutcome.Existing, new(changed, ChatMappingEditStatus.AwaitingApproval));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service().CreateAsync(Actor, p));
    }

    [Fact]
    public async Task Exact_stored_bytes_and_key_survive_lost_commit_ack_and_later_permission_loss()
    {
        var f = new Fake { Commit = true, LoseAck = true }; var p = f.Proposal; var service = f.Service();
        var saved = await service.ApproveAsync(Actor, Conversation, p.TurnId, p.ProposalId, p.Fingerprint);
        Assert.Equal(ChatMappingEditStatus.Saved, saved!.Status);
        var append = Assert.Single(f.Appends);
        Assert.Equal(p.RequestKey, append.Key); Assert.Equal(p.Content.Checkpoint.CopyBytes(), append.Bytes);
        Assert.Equal(new EqualHead(Base.RevisionId), append.Request.HeadCondition);
        f.CanEdit = false;
        f.HeadChanged = true;
        var replay = await service.ApproveAsync(Actor, Conversation, p.TurnId, p.ProposalId, p.Fingerprint);
        Assert.Equal(saved, replay); Assert.Single(f.Appends);
    }

    [Fact]
    public async Task Lost_prepare_ack_cannot_ingest_and_repeat_explicit_approval_bridges_gap()
    {
        var f = new Fake { LosePrepareAck = true }; var p = f.Proposal; var service = f.Service();
        await Assert.ThrowsAsync<IOException>(() => service.ApproveAsync(Actor, Conversation, p.TurnId, p.ProposalId, p.Fingerprint));
        Assert.Empty(f.Appends);
        await service.RecoverAsync(Actor, Conversation, p.TurnId, p.ProposalId, p.Fingerprint);
        Assert.Empty(f.Appends);
        f.LosePrepareAck = false;
        await service.ApproveAsync(Actor, Conversation, p.TurnId, p.ProposalId, p.Fingerprint);
        Assert.Equal(p.RequestKey, Assert.Single(f.Appends).Key);
    }

    [Fact]
    public async Task Wrong_actor_is_refused_before_store_and_wrong_fingerprint_cannot_append()
    {
        var f = new Fake(); var p = f.Proposal; var service = f.Service();
        await Assert.ThrowsAsync<ArgumentException>(() => service.ApproveAsync(new(new PrincipalId(Id(99))), Conversation, p.TurnId, p.ProposalId, p.Fingerprint));
        Assert.Equal(0, f.Approvals);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApproveAsync(Actor, Conversation, p.TurnId, p.ProposalId, Sha256Digest.FromBytes([9])));
        Assert.Empty(f.Appends);
    }

    [Theory]
    [InlineData("key")]
    [InlineData("native")]
    [InlineData("base")]
    [InlineData("round")]
    [InlineData("scope")]
    [InlineData("creator")]
    [InlineData("organization")]
    [InlineData("missingReceipt")]
    public async Task Stored_mismatches_fail_closed(string kind)
    {
        var f = new Fake(); var p = f.Proposal;
        var state = f.Prepared(); var receipt = state.Receipt!;
        f.State = kind switch
        {
            "key" => state with { Receipt = receipt with { Key = Id(99) } },
            "native" => state with { Receipt = receipt with { Native = NativeArtifact.Admit(Base, "wrong"u8, 100) } },
            "round" => state with { Receipt = receipt with { RoundId = Id(99) } },
            "base" => state with
            {
                Receipt = receipt with
                {
                    Proposal = new(ChatMappingWriteOutcome.Proposed, ChatMappingWriteReason.NotPermitted,
                new(Base.OrganizationId, Base.ProjectId, Base.MappingId, Id(99)), 2, null, null, null, null, 1, p.Content.Operations.Digest, [], false)
                }
            },
            "scope" => state with { Proposal = new(ChatConversationKey.ForWorkspace(Id(2), Actor.PrincipalId, Id(99)), p.TurnId, p.RoundId, p.CallId, 2, p.Content) },
            "creator" => state with { Proposal = new(ChatConversationKey.ForWorkspace(Id(2), new PrincipalId(Id(99)), Id(6)), p.TurnId, p.RoundId, p.CallId, 2, p.Content) },
            "organization" => state with
            {
                Proposal = new(ChatConversationKey.ForWorkspace(Id(20), Actor.PrincipalId, Id(6)), p.TurnId, p.RoundId, p.CallId, 2,
                    new(NativeArtifact.Admit(new(Id(20), Base.ProjectId, Base.MappingId, Base.RevisionId), p.Content.Checkpoint.CopyBytes(), 100), p.Content.Operations, []))
            },
            _ => state with { Receipt = null }
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service().ApproveAsync(Actor, Conversation, p.TurnId, p.ProposalId, p.Fingerprint));
        Assert.Empty(f.Appends);
    }

    [Fact]
    public async Task Fresh_permission_and_store_head_refusals_prevent_append()
    {
        var f = new Fake { CanEdit = false }; var p = f.Proposal;
        Assert.Equal(ChatMappingEditStatus.NotPermitted, (await f.Service().ApproveAsync(Actor, Conversation, p.TurnId, p.ProposalId, p.Fingerprint))!.Status);
        Assert.Empty(f.Appends);
        f.CanEdit = true; f.State = new(p, ChatMappingEditStatus.OutOfDate);
        Assert.Equal(ChatMappingEditStatus.OutOfDate, (await f.Service().ApproveAsync(Actor, Conversation, p.TurnId, p.ProposalId, p.Fingerprint))!.Status);
        Assert.Empty(f.Appends);
    }

    [Fact]
    public async Task Commit_head_race_and_ambiguous_append_are_reported_from_durable_store()
    {
        var f = new Fake { HeadChanged = true }; var p = f.Proposal;
        Assert.Equal(ChatMappingEditStatus.OutOfDate, (await f.Service().ApproveAsync(Actor, Conversation, p.TurnId, p.ProposalId, p.Fingerprint))!.Status);
        Assert.Single(f.Appends);
        var pending = new Fake { LoseAck = true }; p = pending.Proposal;
        Assert.Equal(ChatMappingEditStatus.Pending, (await pending.Service().ApproveAsync(Actor, Conversation, p.TurnId, p.ProposalId, p.Fingerprint))!.Status);
        await pending.Service().RecoverAsync(Actor, Conversation, p.TurnId, p.ProposalId, p.Fingerprint);
        Assert.Equal(1, pending.Resumes); Assert.Single(pending.Appends);
    }

    [Fact]
    public async Task Ingestion_permission_revocation_is_not_reported_as_approved_or_saved()
    {
        var f = new Fake { RevokeAtAppend = true }; var p = f.Proposal;
        var result = await f.Service().ApproveAsync(Actor, Conversation, p.TurnId, p.ProposalId, p.Fingerprint);
        Assert.Equal(ChatMappingEditStatus.NotPermitted, result!.Status);
        Assert.Null(result.Receipt!.Saved);
    }

    [Theory]
    [InlineData(RevisionIngestionRejection.EmptyNative)]
    [InlineData(RevisionIngestionRejection.NativeLimit)]
    [InlineData(RevisionIngestionRejection.UnregisteredBackend)]
    public async Task Explicit_pre_reservation_rejection_is_not_stale_approval(RevisionIngestionRejection reason)
    {
        var f = new Fake { AppendResult = () => RevisionIngestionResult.RejectedBeforeReservation(reason) };
        var p = f.Proposal;
        var result = await f.Service().ApproveAsync(Actor, Conversation, p.TurnId, p.ProposalId, p.Fingerprint);
        Assert.Equal(ChatMappingEditStatus.NotSaved, result!.Status);
        Assert.Null(result.Receipt!.OperationId);
        Assert.Null(result.Receipt.RevisionId);
        Assert.Null(result.Receipt.Sequence);
        // The refusal is an observed attempt result, not a fabricated durable ingestion terminal.
        Assert.Equal(ChatMappingEditStatus.Approved, f.State.Status);
        await f.Service().RecoverAsync(Actor, Conversation, p.TurnId, p.ProposalId, p.Fingerprint);
        Assert.Equal(0, f.Resumes); Assert.Single(f.Appends);
    }

    [Fact]
    public async Task Real_head_conflict_result_overrides_stale_prepared_state()
    {
        var f = new Fake(); var p = f.Proposal;
        f.AppendResult = () => IngestionResult(p, IngestionPhase.HeadConflict);
        var result = await f.Service().ApproveAsync(Actor, Conversation, p.TurnId, p.ProposalId, p.Fingerprint);
        Assert.Equal(ChatMappingEditStatus.OutOfDate, result!.Status);
        Assert.Equal(ChatMappingEditStatus.Approved, f.State.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Durable_saved_reread_wins_over_append_or_resume_refusal(bool resume)
    {
        var f = new Fake(); var p = f.Proposal;
        if (resume) f.State = f.Pending();
        RevisionIngestionResult RefuseAfterConcurrentSave()
        {
            f.State = f.Saved();
            return resume ? RevisionIngestionResult.WithoutReceipt(RevisionIngestionOutcome.NotAuthorizedOrNotFound)
                : RevisionIngestionResult.RejectedBeforeReservation(RevisionIngestionRejection.NativeLimit);
        }
        if (resume) f.ResumeResult = RefuseAfterConcurrentSave; else f.AppendResult = RefuseAfterConcurrentSave;
        var service = f.Service();
        var result = resume ? await service.RecoverAsync(Actor, Conversation, p.TurnId, p.ProposalId, p.Fingerprint)
            : await service.ApproveAsync(Actor, Conversation, p.TurnId, p.ProposalId, p.Fingerprint);
        Assert.Equal(ChatMappingEditStatus.Saved, result!.Status);
        Assert.Equal(Id(11), result.Receipt!.RevisionId);
    }

    [Fact]
    public async Task Resume_permission_revocation_after_fresh_check_is_reported()
    {
        var f = new Fake { ResumeResult = () => RevisionIngestionResult.WithoutReceipt(RevisionIngestionOutcome.NotAuthorizedOrNotFound) };
        f.State = f.Pending(); var p = f.Proposal;
        var result = await f.Service().RecoverAsync(Actor, Conversation, p.TurnId, p.ProposalId, p.Fingerprint);
        Assert.Equal(ChatMappingEditStatus.NotPermitted, result!.Status);
        Assert.Equal(1, f.Resumes); Assert.Empty(f.Appends);
    }

    [Fact]
    public async Task Resume_can_finish_saved_and_needs_source_never_first_reserves_or_repairs_bytes()
    {
        var f = new Fake(); var p = f.Proposal; f.State = f.Pending();
        f.ResumeResult = () => IngestionResult(p, IngestionPhase.Reserved);
        var pending = await f.Service().RecoverAsync(Actor, Conversation, p.TurnId, p.ProposalId, p.Fingerprint);
        Assert.Equal(ChatMappingEditStatus.Pending, pending!.Status);
        Assert.Equal(Id(10), pending.Receipt!.OperationId);
        Assert.Empty(f.Appends); Assert.Equal(1, f.Resumes);
        // Only repeat explicit approval supplies the retained bytes under the original key.
        f.AppendResult = () => { f.State = f.Saved(); return IngestionResult(p, IngestionPhase.Committed); };
        Assert.Equal(ChatMappingEditStatus.Saved, (await f.Service().ApproveAsync(Actor, Conversation, p.TurnId, p.ProposalId, p.Fingerprint))!.Status);
        Assert.Equal(p.RequestKey, Assert.Single(f.Appends).Key);
        Assert.Equal(p.Content.Checkpoint.CopyBytes(), f.Appends[0].Bytes);
        var resumed = new Fake(); p = resumed.Proposal; resumed.State = resumed.Pending();
        resumed.ResumeResult = () => { resumed.State = resumed.Saved(); return IngestionResult(p, IngestionPhase.Committed); };
        Assert.Equal(ChatMappingEditStatus.Saved, (await resumed.Service().RecoverAsync(Actor, Conversation, p.TurnId, p.ProposalId, p.Fingerprint))!.Status);
        Assert.Equal(1, resumed.Resumes); Assert.Empty(resumed.Appends);
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("sequence")]
    [InlineData("both")]
    [InlineData("emptyOperation")]
    public async Task Pending_receipt_with_committed_fields_or_empty_operation_fails_closed(string fault)
    {
        var f = new Fake(); var p = f.Proposal; f.State = f.Pending();
        f.State = f.State with
        {
            Receipt = f.State.Receipt! with
            {
                RevisionId = fault is "revision" or "both" ? Id(11) : null,
                Sequence = fault is "sequence" or "both" ? 3 : null,
                OperationId = fault == "emptyOperation" ? Guid.Empty : Id(10)
            }
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service().RecoverAsync(Actor, Conversation, p.TurnId, p.ProposalId, p.Fingerprint));
        Assert.Equal(0, f.Resumes); Assert.Empty(f.Appends);
    }

    [Fact]
    public async Task Saved_receipt_with_empty_operation_id_cannot_be_replayed()
    {
        var f = new Fake(); var p = f.Proposal; f.State = f.Saved();
        f.State = f.State with { Receipt = f.State.Receipt! with { OperationId = Guid.Empty } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service().ApproveAsync(Actor, Conversation, p.TurnId, p.ProposalId, p.Fingerprint));
        Assert.Empty(f.Appends);
    }

    // Build actual contract-valid ingestion results rather than returning ClaimLost for every scenario.
    private static RevisionIngestionResult IngestionResult(ChatMappingEditProposal proposal, IngestionPhase phase)
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var scope = new RevisionScope(Base.OrganizationId, Base.ProjectId, Base.MappingId, Id(11));
        var project = new ProjectScope(Base.OrganizationId, Base.ProjectId);
        var artifact = proposal.Content.Checkpoint;
        var profile = new IngestionBackendProfile("ferrule", "engine", "build", "adapter");
        var intent = IngestionIntent.ForAppend(new(Base.OrganizationId, Base.ProjectId, Base.MappingId), "ferrule", "entry.native.json",
            new EqualHead(Base.RevisionId), artifact.Digest, artifact.ByteCount);
        var native = new PrivateObjectReference(PrivateObjectId.Restore(scope, Id(12), PrivateObjectClass.NativeArtifact), artifact.Digest, artifact.ByteCount);
        var facts = new ReservedIngestionFacts(project, Id(10), Actor.PrincipalId, proposal.RequestKey,
            CanonicalFingerprint.Create(Actor.PrincipalId, project, intent), intent, scope, native, profile,
            new IngestionAdmissionPolicy("proposal-test", 4194304), now);
        using var diagnostics = System.Text.Json.JsonDocument.Parse("[]");
        var completion = phase == IngestionPhase.Reserved
            ? IngestionAttemptCompletion.FromOperationalFailure(IngestionFailureCode.NeedsSource)
            : IngestionAttemptCompletion.FromValidation(IngestionEvidenceDocument.Create(
                new(IngestionBackendStatus.Succeeded, new(scope, artifact.Digest, profile, true), true, 0, 0, false), diagnostics.RootElement));
        var attempt = new IngestionAttemptReceipt(1, now.AddSeconds(1), now.AddSeconds(2), completion, completion.Classify(facts));
        var committed = phase == IngestionPhase.Committed ? new CommittedRevisionFacts(scope, 3, native, profile,
            IngestionIntent.EntryDocument, IngestionIntent.EmptyDependencyIdentity, Actor.PrincipalId, now.AddSeconds(3), Id(10), 1, attempt.Classification!.Value) : null;
        var eligible = phase != IngestionPhase.Reserved;
        var metadata = new IngestionReceipt(facts, phase, 1, 1, eligible ? 1 : null, eligible ? now.AddSeconds(1) : null,
            eligible ? 1 : null, [attempt], committed, eligible ? now.AddSeconds(3) : null);
        return RevisionIngestionResult.ForReceipt(phase switch
        {
            IngestionPhase.HeadConflict => RevisionIngestionOutcome.HeadConflict,
            IngestionPhase.Committed => RevisionIngestionOutcome.CommittedValid,
            _ => RevisionIngestionOutcome.NeedsSource
        }, RevisionIngestionReceipt.FromMetadata(metadata));
    }

    private sealed class Fake : IChatMappingEditProposalStore, IProjectReader, IRevisionIngestionService
    {
        internal ChatMappingEditProposal Proposal { get; } = ChatMappingEditApprovalTests.Proposal();
        internal ChatMappingEditState State;
        internal ChatMappingEditCreateResult? CreateResult;
        internal bool CanEdit = true, Commit, LoseAck, LosePrepareAck, HeadChanged, RevokeAtAppend;
        internal int Approvals, Resumes;
        internal Func<RevisionIngestionResult>? AppendResult, ResumeResult;
        internal List<(Guid Key, AppendRevisionRequest Request, byte[] Bytes)> Appends = [];
        internal Fake() { State = new(Proposal, ChatMappingEditStatus.AwaitingApproval); }
        internal ChatMappingEditApprovalService Service() => new(this, this, this);
        internal ChatMappingEditState Prepared() => new(Proposal, ChatMappingEditStatus.Approved, new(Proposal.RequestKey, Proposal.RoundId, "pending",
            new(ChatMappingWriteOutcome.Proposed, ChatMappingWriteReason.NotPermitted, Base, 2, null, null, null, null,
                Proposal.Content.Operations.Count, Proposal.Content.Operations.Digest, [], false), null, null, null, Proposal.Content.Checkpoint));
        internal ChatMappingEditState Pending() => Prepared() with { Status = ChatMappingEditStatus.Pending, Receipt = Prepared().Receipt! with { OperationId = Id(10) } };
        internal ChatMappingEditState Saved() => Prepared() with { Status = ChatMappingEditStatus.Saved, Receipt = Prepared().Receipt! with { Status = "saved", OperationId = Id(10), RevisionId = Id(11), Sequence = 3 } };
        public Task<ChatMappingEditCreateResult> CreateAsync(ActorContext actor, ChatMappingEditProposal proposal, CancellationToken ct) =>
            Task.FromResult(CreateResult ?? new ChatMappingEditCreateResult(ChatMappingEditCreateOutcome.Existing, State));
        public Task<ChatMappingEditState?> ReadAsync(ActorContext actor, ChatConversationKey conversation, Guid turnId, Guid proposalId, CancellationToken ct) => Task.FromResult<ChatMappingEditState?>(State);
        public Task<IReadOnlyList<ChatMappingEditState>> ListAsync(ActorContext actor, ChatConversationKey conversation, Guid turnId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ChatMappingEditState>>(actor.PrincipalId == Proposal.Conversation.Creator && conversation == Proposal.Conversation && turnId == Proposal.TurnId ? [State] : []);
        public Task<ChatMappingEditState?> ApproveAndPrepareAsync(ActorContext actor, ChatConversationKey conversation, Guid turnId, Guid proposalId, Sha256Digest expectedFingerprint, CancellationToken ct)
        {
            Approvals++;
            if (State.Status == ChatMappingEditStatus.AwaitingApproval) State = Prepared();
            if (LosePrepareAck) throw new IOException("Lost acknowledgement.");
            return Task.FromResult<ChatMappingEditState?>(State);
        }
        public Task<ProjectAccess?> GetAccessAsync(ActorContext actor, ProjectScope scope, CancellationToken cancellationToken = default) =>
            Task.FromResult<ProjectAccess?>(new(scope, actor.PrincipalId, CanEdit ? ProjectRole.Publisher : ProjectRole.Viewer));
        public async Task<RevisionIngestionResult> AppendAsync(ActorContext actor, MappingScope target, Guid idempotencyKey, AppendRevisionRequest request, Stream nativeSource, CancellationToken cancellationToken = default)
        {
            Assert.Equal(new MappingScope(Base.OrganizationId, Base.ProjectId, Base.MappingId), target);
            using var bytes = new MemoryStream(); await nativeSource.CopyToAsync(bytes, cancellationToken);
            Appends.Add((idempotencyKey, request, bytes.ToArray()));
            if (AppendResult is not null) return AppendResult();
            if (RevokeAtAppend) return RevisionIngestionResult.WithoutReceipt(RevisionIngestionOutcome.NotAuthorizedOrNotFound);
            State = State with
            {
                Status = HeadChanged ? ChatMappingEditStatus.OutOfDate : Commit ? ChatMappingEditStatus.Saved : ChatMappingEditStatus.Pending,
                Receipt = State.Receipt! with
                {
                    Status = HeadChanged ? "notSaved" : Commit ? "saved" : "pending",
                    OperationId = Id(10),
                    RevisionId = Commit ? Id(11) : null,
                    Sequence = Commit ? 3 : null
                }
            };
            if (LoseAck) throw new IOException("Lost acknowledgement.");
            return RevisionIngestionResult.WithoutReceipt(RevisionIngestionOutcome.ClaimLost);
        }
        public Task<RevisionIngestionResult> ResumeAsync(ActorContext actor, ProjectScope project, Guid operationId, CancellationToken cancellationToken = default)
        { Assert.Equal(Id(10), operationId); Resumes++; return Task.FromResult(ResumeResult?.Invoke() ?? RevisionIngestionResult.WithoutReceipt(RevisionIngestionOutcome.ClaimLost)); }
        public Task<ProjectPage> ListVisibleProjectsAsync(ActorContext actor, Guid organizationId, int limit, Guid? afterProjectId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ProjectSummary?> GetVisibleProjectAsync(ActorContext actor, ProjectScope scope, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RevisionIngestionResult> CreateAsync(ActorContext actor, ProjectScope project, Guid idempotencyKey, CreateRevisionRequest request, Stream nativeSource, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RevisionIngestionReceipt?> GetOwnReceiptAsync(ActorContext actor, ProjectScope project, Guid operationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
