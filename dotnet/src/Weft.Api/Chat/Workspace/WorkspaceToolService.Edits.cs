using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Weft.Api.Catalog;
using Weft.Application;
using Weft.Domain;

namespace Weft.Api.Chat.Workspace;

public sealed partial class WorkspaceToolService
{
    private static readonly TimeSpan EditDuration = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The one write. Replays a typed edit on the exact base the model names and, only when the creator may edit this
    /// mapping now and that base is still the head, saves it as the next version. Otherwise nothing is saved and the
    /// result is a read-only proposal. It never changes the version in use or starts a build, run or activation.
    /// </summary>
    private async Task<ChatReadOnlyToolResult> SaveEditAsync(CallScope scope, WorkspaceToolCall call, CancellationToken token)
    {
        if (!features.LegacyDirectSave || services.GetService<ChatMappingEditRuntimeOptions>()?.LegacyDirectSaveEnabled != true ||
            services.GetService<MappingDirectEditService>() is not { } editor || scope.Turn is null || scope.CallId is null || scope.RoundId is null)
            throw new WorkspaceToolRefusal(WorkspaceToolOutcome.Unavailable);
        var mapping = await LocateMappingAsync(scope, call.Id("mappingId"), token);
        var baseRevision = new RevisionScope(mapping.OrganizationId, mapping.ProjectId, mapping.MappingId, call.Id("baseRevisionId"));
        MappingEditScript operations;
        try { operations = MappingEditScript.Parse(call.Object("edit")); }
        catch (ArgumentException) { return Outcome(call.Name, scope.Budget, WorkspaceToolOutcome.InvalidCall, new { problem = "The edit is not a valid ferrule.edit.v1 script." }); }
        if (operations.Count == 0)
            return Outcome(call.Name, scope.Budget, WorkspaceToolOutcome.InvalidCall, new { problem = "The edit has no operations." });
        var read = await CatalogEndpoints.ReadableMappingAsync(services, scope.Actor, mapping, token) ?? throw new WorkspaceToolRefusal(WorkspaceToolOutcome.NotFound);
        var revision = await CatalogEndpoints.ReadableRevisionAsync(services, scope.Actor, baseRevision, token) ?? throw new WorkspaceToolRefusal(WorkspaceToolOutcome.NotFound);
        var key = WriteKey(scope.Turn.TurnId, scope.CallId);
        var journal = services.GetService<IChatMappingWriteJournal>();
        if (journal is not null && (await journal.ReadAsync(scope.Actor, scope.Conversation.Key, scope.Turn.TurnId, token))
            .SingleOrDefault(item => item.Key == key) is { } prior)
        {
            if (prior.Proposal.Base != baseRevision || prior.Proposal.EditDigest != operations.Digest || prior.Proposal.OperationCount != operations.Count)
                throw new WorkspaceToolRefusal(WorkspaceToolOutcome.InvalidCall);
            // A prior commit is still a success if a later edit moved the head. Never replay it as another append.
            if (prior.Saved is { } saved) return WriteReply(scope, call, saved);
            return Outcome(call.Name, scope.Budget, WorkspaceToolOutcome.Unavailable,
                new { problem = "The save has a durable receipt. Refresh its save status; don't repeat the edit.", requestId = Id(key), status = prior.Status });
        }
        // Permission is observed now, at write time; the database and ingestion check the same authority again.
        var write = journal is not null && services.GetService<CatalogMappingEditOptions>()?.Enabled == true && read.Access.Can(ProjectAction.EditMapping) &&
            read.Summary.BackendId == "ferrule";
        var result = await editor.ApplyAsync(scope.Actor, baseRevision, operations, key, write, token, async (changeSet, native, ct) =>
        {
            scope.SaveKey = key;
            return await journal!.PrepareAsync(scope.Actor, new(scope.Conversation.Key, scope.Turn.TurnId, scope.RoundId.Value,
                scope.CallId, key, Proposal(changeSet), native), ct);
        });

        ChatMappingWriteFact Proposal(IReadOnlyList<MappingEditChange> sourceChanges) => new ChatMappingWriteFact(ChatMappingWriteOutcome.Proposed,
            ChatMappingWriteReason.NotPermitted, baseRevision, revision.Summary.Sequence, null, null, null, null, operations.Count,
            operations.Digest, sourceChanges.Take(ChatMappingWriteFact.MaximumChanges).Select(change => new ChatMappingChange(change.Field, change.Before, change.After)).ToArray(),
            sourceChanges.Count > ChatMappingWriteFact.MaximumChanges).Fitted();
        switch (result.Outcome)
        {
            case MappingDirectEditOutcome.NotAuthorizedOrNotFound: throw new WorkspaceToolRefusal(WorkspaceToolOutcome.NotFound);
            case MappingDirectEditOutcome.Busy: throw new WorkspaceToolRefusal(WorkspaceToolOutcome.Busy);
            case MappingDirectEditOutcome.Unavailable: throw new WorkspaceToolRefusal(WorkspaceToolOutcome.Unavailable);
            case MappingDirectEditOutcome.Rejected:
                var failure = result.Failure!;
                return Outcome(call.Name, scope.Budget, WorkspaceToolOutcome.InvalidCall, new
                {
                    problem = "The edit does not apply to this version.",
                    code = failure.Code,
                    operationIndex = failure.OperationIndex,
                    message = failure.Message.Length > 256 ? failure.Message[..256] : failure.Message
                });
            case MappingDirectEditOutcome.Pending:
                return Outcome(call.Name, scope.Budget, WorkspaceToolOutcome.Unavailable, new { problem = "Saving is pending. Its durable save status can be refreshed without repeating the edit.", requestId = Id(key), status = "pending" });
        }
        var changes = result.Changes.Take(ChatMappingWriteFact.MaximumChanges).Select(change => new ChatMappingChange(change.Field, change.Before, change.After)).ToArray();
        var truncated = result.Changes.Count > changes.Length;
        ChatMappingWriteFact fact;
        if (result.Outcome == MappingDirectEditOutcome.Saved)
        {
            var receipt = result.Ingestion!.Receipt!;
            fact = new(ChatMappingWriteOutcome.Saved, null, baseRevision, revision.Summary.Sequence, receipt.FutureRevision.RevisionId,
                receipt.CommittedSequence, null, null, operations.Count, operations.Digest, changes, truncated);
        }
        else if (result.Outcome == MappingDirectEditOutcome.OutOfDate)
        {
            var head = (await CatalogEndpoints.ReadableMappingAsync(services, scope.Actor, mapping, token) ?? read).Summary.Head;
            fact = new(ChatMappingWriteOutcome.Proposed, ChatMappingWriteReason.OutOfDate, baseRevision, revision.Summary.Sequence, null, null,
                head.Scope.RevisionId, head.Sequence, operations.Count, operations.Digest, changes, truncated);
        }
        else
            fact = new(ChatMappingWriteOutcome.Proposed, ChatMappingWriteReason.NotPermitted, baseRevision, revision.Summary.Sequence, null, null,
                null, null, operations.Count, operations.Digest, changes, truncated);
        return WriteReply(scope, call, fact.Fitted());
    }

    private static ChatReadOnlyToolResult WriteReply(CallScope scope, WorkspaceToolCall call, ChatMappingWriteFact fact)
    {
        var baseRevision = fact.Base;
        var mapping = new MappingScope(baseRevision.OrganizationId, baseRevision.ProjectId, baseRevision.MappingId);
        scope.Written = fact;
        var cited = fact.RevisionId is { } saved ? new RevisionScope(mapping.OrganizationId, mapping.ProjectId, mapping.MappingId, saved) : baseRevision;
        var data = new
        {
            outcome = fact.Outcome == ChatMappingWriteOutcome.Saved ? "saved" : "proposed",
            reason = fact.Reason switch { ChatMappingWriteReason.NotPermitted => "notPermitted", ChatMappingWriteReason.OutOfDate => "outOfDate", _ => null },
            mappingId = Id(mapping.MappingId),
            baseRevisionId = Id(baseRevision.RevisionId),
            baseSequence = fact.BaseSequence,
            revisionId = fact.RevisionId is { } id ? Id(id) : null,
            sequence = fact.Sequence,
            headRevisionId = fact.HeadRevisionId is { } headId ? Id(headId) : null,
            headSequence = fact.HeadSequence,
            changes = fact.Changes.Select(change => new { field = change.Field, before = change.Before, after = change.After }),
            changesTruncated = fact.ChangesTruncated
        };
        return Single(call.Name, scope.Budget, data, [ChatWorkspaceCitationFact.Revision(cited)]);
    }

    /// <summary>One idempotency key per tool call of a turn, so a repeated dispatch replays the same append.</summary>
    internal static Guid WriteKey(Guid turnId, string callId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes("weft.chat-mapping-write.v1\n" + Id(turnId) + "\n" + callId));
        digest[6] = (byte)(digest[6] & 0x0F | 0x80); digest[8] = (byte)(digest[8] & 0x3F | 0x80);
        return new Guid(digest.AsSpan(0, 16), bigEndian: true);
    }

    /// <summary>A write that happened is reported whatever the call returns afterwards.</summary>
    private static ChatReadOnlyToolResult WithWrite(ChatReadOnlyToolResult result, CallScope? scope)
    {
        if (scope?.Written is { } fact)
            return result.MappingWrite is not null ? result
                : new(result.Result, result.RetrievedNodes, result.Citations, result.WorkspaceCitations, result.DataShared, fact);
        return scope?.SaveKey is not { } key ? result : Outcome(WorkspaceToolRegistry.SaveEdit, scope.Budget, WorkspaceToolOutcome.Unavailable,
            new { problem = "Save status is unconfirmed. Read the durable save receipt before repeating any edit.", requestId = Id(key), status = "unconfirmed" });
    }
}
