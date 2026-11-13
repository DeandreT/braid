using Weft.Domain;

namespace Weft.Application;

/// <summary>A durable, immutable pre-write intent. Receipt identity is the ingestion key, not a live chat fence.</summary>
public sealed record ChatMappingWriteIntent(ChatConversationKey Conversation, Guid TurnId, Guid RoundId, string CallId,
    Guid Key, ChatMappingWriteFact Proposal, NativeArtifact Native)
{
    public override string ToString() => nameof(ChatMappingWriteIntent);
}

/// <summary>The immutable intent and atomic no-content commit fact are the audit, independently of ingestion retention.</summary>
public sealed record ChatMappingWriteReceipt(Guid Key, Guid RoundId, string Status, ChatMappingWriteFact Proposal,
    Guid? OperationId, Guid? RevisionId, long? Sequence, NativeArtifact? Native)
{
    public ChatMappingWriteFact? Saved => Status == "saved" && RevisionId is { } revision && Sequence is { } sequence
        ? new(ChatMappingWriteOutcome.Saved, null, Proposal.Base, Proposal.BaseSequence, revision, sequence, null, null,
            Proposal.OperationCount, Proposal.EditDigest, Proposal.Changes, Proposal.ChangesTruncated) : null;
    public override string ToString() => nameof(ChatMappingWriteReceipt);
}

public interface IChatMappingWriteJournal
{
    /// <summary>Only an acknowledged immutable intent admits an append. Retries must match its exact call, base and native digest.</summary>
    Task<bool> PrepareAsync(ActorContext actor, ChatMappingWriteIntent intent, CancellationToken ct);
    /// <summary>Fresh creator and project read authority. Includes exact matching ingestion receipts even after the turn ends.</summary>
    Task<IReadOnlyList<ChatMappingWriteReceipt>> ReadAsync(ActorContext actor, ChatConversationKey conversation, Guid turnId, CancellationToken ct);
}

/// <summary>Recovery never invokes a model or replays a turn. Only an already reserved ingestion can be resumed.</summary>
public sealed class ChatMappingWriteRecovery(IChatMappingWriteJournal journal, IRevisionIngestionService? ingestion = null,
    IProjectReader? projects = null, TimeSpan? attemptDuration = null)
{
    public async Task<IReadOnlyList<ChatMappingWriteReceipt>> ReadAsync(ActorContext actor, ChatConversationKey conversation,
        Guid turnId, bool resume, CancellationToken ct)
    {
        var receipts = await ReadJournalAsync(actor, conversation, turnId, ct).ConfigureAwait(false);
        if (receipts.Count > 8) throw new InvalidOperationException("The save journal exceeded its admitted call limit.");
        if (!resume || ingestion is null || projects is null) return receipts;
        // Two joined lanes keep all eight accepted calls fair without letting an early stalled save consume the
        // route deadline. No retry loop, detached work or new reservation. Ingestion cancellation bookkeeping
        // gets its existing two-second bound after each three-second slice, leaving room for both journal reads.
        using var recovery = CancellationTokenSource.CreateLinkedTokenSource(ct);
        recovery.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            await Parallel.ForEachAsync(receipts.Where(r => r.Status == "pending" && r.OperationId is not null),
                new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = recovery.Token }, async (receipt, caller) =>
        {
            caller.ThrowIfCancellationRequested();
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(caller);
            attempt.CancelAfter(attemptDuration ?? TimeSpan.FromSeconds(3));
            // Resume cannot first-reserve or resupply bytes. NeedsSource remains pending until the creator
            // explicitly approves that exact proposal again. A refresh never grants fresh save authority.
            try
            {
                var revision = receipt.Proposal.Base;
                var project = new ProjectScope(revision.OrganizationId, revision.ProjectId);
                var access = await projects.GetAccessAsync(actor, project, attempt.Token).ConfigureAwait(false);
                if (access is null || access.Scope != project || access.PrincipalId != actor.PrincipalId || !access.Can(ProjectAction.EditMapping)) return;
                await ingestion.ResumeAsync(actor, project, receipt.OperationId!.Value, attempt.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException) && !ct.IsCancellationRequested)
            {
                // A lost acknowledgement is not failure or success. Re-read the durable own-receipt below; leave
                // a still-pending save retryable and do not prevent other independent accepted saves from recovering.
            }
        }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (recovery.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // The recovery work budget is not the caller's read budget. Join the lanes, then report whatever
            // actually committed. The route's 60-second bound retains time for this final authority-checked read.
        }
        return await ReadJournalAsync(actor, conversation, turnId, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ChatMappingWriteReceipt>> ReadJournalAsync(ActorContext actor, ChatConversationKey conversation,
        Guid turnId, CancellationToken ct)
    {
        using var read = CancellationTokenSource.CreateLinkedTokenSource(ct);
        read.CancelAfter(TimeSpan.FromSeconds(15));
        return await journal.ReadAsync(actor, conversation, turnId, read.Token).ConfigureAwait(false);
    }
}
