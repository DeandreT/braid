using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Braid.Api.Catalog;
using Braid.Application;
using Braid.Contracts.Chat;

namespace Braid.Api.Chat;

public static partial class ChatEndpoints
{
    private static void MapMappingEditProposals(IEndpointRouteBuilder routes, string turn)
    {
        var proposal = turn + "/mapping-edit-proposals/{proposalId}";
        Describe<ChatMappingEditProposalPageV1>(routes.MapGet(turn + "/mapping-edit-proposals", ListMappingEditProposals), "GetChatMappingEditProposals",
            "Read at most eight retained proposals with fresh creator and project authority, independently of stream delivery.");
        Describe<ChatMappingEditProposalV1>(routes.MapGet(proposal, ReadMappingEditProposal), "GetChatMappingEditProposal",
            "Read one retained proposal with fresh creator and project authority. Does not approve or save.");
        Describe<ChatMappingEditOperationsV1>(routes.MapGet(proposal + "/operations", ReadMappingEditOperations), "GetChatMappingEditOperations",
            "Read the complete canonical edit script for exact review. Requires expectedFingerprint query; never returns native bytes.")
            .DescribeProposalFingerprint();
        Describe<ChatMappingEditProposalV1>(routes.MapPost(proposal + "/approve", ApproveMappingEditProposal), "ApproveChatMappingEditProposal",
            "Explicitly approve exactly the stored proposal and fingerprint, including after its turn ends.")
            .DescribeBody<ChatMappingEditApprovalV1>();
        Describe<ChatMappingEditProposalV1>(routes.MapPost(proposal + "/recover", RecoverMappingEditProposal), "RecoverChatMappingEditProposal",
            "Resume only an existing reservation for this exact proposal. Never approves, first-reserves or supplies source.")
            .DescribeBody<ChatMappingEditApprovalV1>();
    }

    private static Task<IResult> ReadMappingEditProposal(string organizationId, string conversationId, string turnId, string proposalId,
        HttpContext context) => MappingEditProposal(organizationId, conversationId, turnId, proposalId, context, ProposalAction.Read);
    private static Task<IResult> ReadMappingEditOperations(string organizationId, string conversationId, string turnId, string proposalId,
        HttpContext context) => MappingEditProposal(organizationId, conversationId, turnId, proposalId, context, ProposalAction.Operations);
    private static Task<IResult> ApproveMappingEditProposal(string organizationId, string conversationId, string turnId, string proposalId,
        HttpContext context) => MappingEditProposal(organizationId, conversationId, turnId, proposalId, context, ProposalAction.Approve);
    private static Task<IResult> RecoverMappingEditProposal(string organizationId, string conversationId, string turnId, string proposalId,
        HttpContext context) => MappingEditProposal(organizationId, conversationId, turnId, proposalId, context, ProposalAction.Recover);

    private enum ProposalAction { Read, Operations, Approve, Recover }

    private static Task<IResult> ListMappingEditProposals(string organizationId, string conversationId, string turnId,
        HttpContext context) => WithChat(context, async chat =>
    {
        ChatRequestParser.RequireQuery(context.Request);
        var key = WorkspaceKey(chat, organizationId, conversationId);
        var turn = ChatRequestParser.Id(turnId);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        bounded.CancelAfter(TimeSpan.FromSeconds(15));
        if (key is null || await chat.Host.Stores.ReaderTurns.ReadTurnAsync(chat.Actor, key, turn, bounded.Token) is null) return NotFound();
        if (ProposalService(context.RequestServices) is not { } service)
            return Error(ChatErrorCodeV1.Unavailable, StatusCodes.Status503ServiceUnavailable);
        var states = await service.ListAsync(chat.Actor, key, turn, bounded.Token);
        return Results.Ok(new ChatMappingEditProposalPageV1(states.Select(ProposalReview).ToArray()));
    });

    private static Task<IResult> MappingEditProposal(string organizationId, string conversationId, string turnId, string proposalId,
        HttpContext context, ProposalAction action) => WithChat(context, async chat =>
    {
        var write = action is ProposalAction.Approve or ProposalAction.Recover;
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        bounded.CancelAfter(TimeSpan.FromSeconds(60));
        ChatRequestParser.RequireQuery(context.Request, action == ProposalAction.Operations ? ["expectedFingerprint"] : []);
        var expected = write ? await ChatRequestParser.ReadApprovalAsync(context.Request, bounded.Token)
            : action == ProposalAction.Operations ? ChatRequestParser.Fingerprint(context.Request.Query["expectedFingerprint"].FirstOrDefault()) : null;
        var key = WorkspaceKey(chat, organizationId, conversationId);
        var turn = ChatRequestParser.Id(turnId);
        var proposal = ChatRequestParser.Id(proposalId);
        if (key is null || await chat.Host.Stores.ReaderTurns.ReadTurnAsync(chat.Actor, key, turn, bounded.Token) is null) return NotFound();
        // There is no fallback to direct editing when production proposal persistence is unavailable.
        if (ProposalService(context.RequestServices) is not { } service)
            return Error(ChatErrorCodeV1.Unavailable, StatusCodes.Status503ServiceUnavailable);
        var state = await service.ReadAsync(chat.Actor, key, turn, proposal, bounded.Token);
        if (state is null) return NotFound();
        if (expected is not null && expected != state.Proposal.Fingerprint)
            return Error(ChatErrorCodeV1.IdempotencyConflict, StatusCodes.Status409Conflict);
        if (action == ProposalAction.Operations)
        {
            var content = state.Proposal.Content;
            var bytes = content.Operations.CopyUtf8();
            if (bytes.Length > MappingEditPolicy.MaximumOperationsBytes) throw new InvalidOperationException("Proposal operations exceed their bound.");
            return Results.Ok(new ChatMappingEditOperationsV1(ChatPublicMapper.Id(proposal), state.Proposal.Fingerprint.Value,
                content.Operations.Digest.Value, content.Operations.Count, Encoding.UTF8.GetString(bytes)));
        }
        if (write)
        {
            if (context.RequestServices.GetService<CatalogMappingEditOptions>()?.Enabled != true)
                return Error(ChatErrorCodeV1.Unavailable, StatusCodes.Status503ServiceUnavailable);
            state = action == ProposalAction.Approve
                ? await service.ApproveAsync(chat.Actor, key, turn, proposal, expected!, bounded.Token)
                : await service.RecoverAsync(chat.Actor, key, turn, proposal, expected!, bounded.Token);
        }
        return state is null ? NotFound() : Results.Ok(ProposalReview(state));
    }, unsafeRequest: action is ProposalAction.Approve or ProposalAction.Recover);

    internal static ChatMappingEditApprovalService? ProposalService(IServiceProvider services) =>
        services.GetService<IChatMappingEditProposalStore>() is not null && services.GetService<IProjectReader>() is not null &&
        services.GetService<IRevisionIngestionService>() is not null ? services.GetService<ChatMappingEditApprovalService>() : null;

    internal static ChatMappingEditProposalV1 ProposalReview(ChatMappingEditState state)
    {
        var proposal = state.Proposal;
        var content = proposal.Content;
        var status = state.Status switch
        {
            ChatMappingEditStatus.AwaitingApproval => "awaitingApproval",
            ChatMappingEditStatus.Approved => "approved",
            ChatMappingEditStatus.Pending => "pending",
            ChatMappingEditStatus.Saved => "saved",
            ChatMappingEditStatus.NotSaved => "notSaved",
            ChatMappingEditStatus.NotPermitted => "notPermitted",
            ChatMappingEditStatus.OutOfDate => "outOfDate",
            _ => throw new InvalidOperationException("Unknown proposal status.")
        };
        return new(ChatPublicMapper.Id(proposal.ProposalId), proposal.Fingerprint.Value, status,
            CatalogPublicMapper.RevisionScope(content.Base), proposal.BaseSequence, content.Operations.Count, content.Operations.Digest.Value,
            content.Changes.Select(change => new ChatMappingChangeV1(change.Field, change.Before, change.After)).ToArray(),
            content.ChangesTruncated, state.Receipt?.Saved?.RevisionId is { } revision ? ChatPublicMapper.Id(revision) : null,
            state.Receipt?.Saved?.Sequence);
    }
}
