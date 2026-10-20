using System.Text;
using Weft.Ai;
using Weft.Domain;

namespace Weft.Application;

/// <summary>Pure bridge over frozen #111 public facts. It never opens a turn/round or resolves credentials.</summary>
public static class ChatAiBridge
{
    public static ChatGroundingPolicyPin PinGroundingPolicy(string id, int version, string trustedInstructions,
        IEnumerable<AiToolDefinition> approvedTools, int maxRetrievedNodes, int maxToolResultBytes, int maxToolIterations)
    {
        ChatBounds.Text(trustedInstructions, 65536, false);
        return new(id, version, Sha256Digest.FromBytes(Encoding.UTF8.GetBytes(trustedInstructions)), ToolsFingerprint(approvedTools),
            maxRetrievedNodes, maxToolResultBytes, maxToolIterations);
    }
    private static Sha256Digest ToolsFingerprint(IEnumerable<AiToolDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var tools = new List<AiToolDefinition>();
        foreach (var tool in definitions) { if (tool is null || tools.Count == 16) throw ChatBounds.Invalid(); tools.Add(tool); }
        var canonical = ChatCanonicalJson.Write(w =>
        {
            w.WriteStartObject(); w.WriteString("schema", "weft.chat.tools.v1"); w.WriteStartArray("tools");
            foreach (var tool in tools) { w.WriteStartObject(); w.WriteString("name", tool.Name); w.WriteString("description", tool.Description); w.WriteString("parametersJson", tool.Parameters.Json); w.WriteEndObject(); }
            w.WriteEndArray(); w.WriteEndObject();
        }, 1048576);
        return Sha256Digest.FromBytes(Encoding.UTF8.GetBytes(canonical));
    }
    public static ChatProfilePin PinProfile(string providerId, AiProfileSpec profile)
    {
        ArgumentNullException.ThrowIfNull(profile); ChatBounds.Identifier(providerId);
        // Every nonsecret AiProfileSpec property is included. A reference is not a resolved secret value.
        var canonical = ChatCanonicalJson.Write(w =>
        {
            w.WriteStartObject(); w.WriteString("schema", "weft.chat.profile.v1"); w.WriteString("provider", providerId);
            w.WriteString("id", profile.Key.Id); w.WriteNumber("version", profile.Key.Version); w.WriteString("dialect", profile.Dialect.ToString());
            w.WriteString("model", profile.Model); w.WriteString("endpoint", profile.Endpoint.AbsoluteUri); w.WriteBoolean("loopbackFixture", profile.LoopbackFixture);
            w.WriteString("authentication", profile.Authentication.ToString()); w.WriteString("credentialReference", profile.CredentialReference);
            w.WriteNumber("capabilities", (int)profile.Capabilities); w.WriteString("metadataPolicy", profile.MetadataPolicyId);
            w.WriteNumber("metadataPolicyVersion", profile.MetadataPolicyVersion); w.WriteBoolean("mappingMetadataAllowed", profile.MappingMetadataAllowed);
            w.WriteBoolean("requestThinkFalse", profile.RequestThinkFalse); w.WriteEndObject();
        }, 16384);
        return new(profile.Key.Id, profile.Key.Version, providerId, profile.Model, profile.MetadataPolicyId, profile.MetadataPolicyVersion,
            Sha256Digest.FromBytes(Encoding.UTF8.GetBytes(canonical)));
    }

    public static AiProfileKey ProfileKey(ChatProfilePin pinned, AiProfileSpec resolved)
    {
        ArgumentNullException.ThrowIfNull(pinned);
        if (PinProfile(pinned.ProviderId, resolved) != pinned) throw ChatBounds.Invalid();
        return resolved.Key;
    }
    public static AiTurnLimits OpenLimits(ChatTurnFacts turn, DateTimeOffset admittedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(turn); ChatBounds.Instant(admittedAtUtc);
        if (turn.Phase != ChatTurnPhase.Active || admittedAtUtc < turn.CreatedAtUtc) throw ChatBounds.Invalid();
        var b = turn.Intent.Envelope.ProviderBudget;
        var remaining = Math.Min(b.DurationTicks - (admittedAtUtc - turn.CreatedAtUtc).Ticks, (turn.Intent.DeadlineUtc - admittedAtUtc).Ticks);
        if (remaining <= 0) throw ChatBounds.Invalid();
        return AiTurnLimits.Create(TimeSpan.FromTicks(remaining), b.MaxRounds, b.MaxRequestBytes, b.MaxWireBytes, b.MaxTextBytes, b.MaxToolCalls);
    }

    public static ChatPreparedRound PrepareRound(ChatConversationIntent conversation, ChatTurnIntent turn, AiProfileSpec resolvedProfile, ChatGroundingPolicyPin resolvedGroundingPolicy,
        AiRoundRequest request, Guid fence, Guid instanceId)
    {
        ArgumentNullException.ThrowIfNull(conversation); ArgumentNullException.ThrowIfNull(turn); ArgumentNullException.ThrowIfNull(request);
        if (PinProfile(turn.Profile.ProviderId, resolvedProfile) != turn.Profile || !resolvedProfile.MappingMetadataAllowed ||
            request.MaxOutputTokens != turn.Envelope.ProviderBudget.MaxOutputTokens || turn.Budget.MaxRounds > turn.Envelope.ProviderBudget.MaxRounds) throw ChatBounds.Invalid();
        var policy = turn.Envelope.GroundingPolicy;
        if (policy != resolvedGroundingPolicy) throw ChatBounds.Invalid();
        if (request.Messages[0] is not AiSystemMessage instructions || Sha256Digest.FromBytes(Encoding.UTF8.GetBytes(instructions.Text)) != policy.InstructionsFingerprint ||
            ToolsFingerprint(request.Tools) != policy.ToolsFingerprint) throw ChatBounds.Invalid();
        var canonical = ChatCanonicalJson.Write(w =>
        {
            w.WriteStartObject(); w.WriteString("schema", "weft.chat.round.v1"); w.WriteString("turnFingerprint", ChatCanonicalEnvelope.Fingerprint(ChatCanonicalEnvelope.Turn(conversation, turn)).Value);
            w.WriteString("round", request.RoundId); w.WriteNumber("outputTokens", request.MaxOutputTokens); w.WriteStartArray("messages");
            foreach (var message in request.Messages)
            {
                w.WriteStartObject();
                switch (message)
                {
                    case AiSystemMessage system: w.WriteString("role", "system"); w.WriteString("text", system.Text); break;
                    case AiUserMessage user: w.WriteString("role", "user"); w.WriteString("text", user.Text); break;
                    case AiAssistantMessage assistant:
                        w.WriteString("role", "assistant"); w.WriteString("text", assistant.Text); w.WriteStartArray("calls");
                        foreach (var call in assistant.ToolCalls) { w.WriteStartObject(); w.WriteString("id", call.CallId); w.WriteString("name", call.Name); w.WriteString("argumentsJson", call.Arguments.Json); w.WriteEndObject(); }
                        w.WriteEndArray(); break;
                    case AiToolMessage tool:
                        w.WriteString("role", "tool"); w.WriteString("call", tool.CallId); w.WriteString("name", tool.Name); w.WriteString("resultJson", tool.Result.Json); break;
                    default: throw ChatBounds.Invalid();
                }
                w.WriteEndObject();
            }
            w.WriteEndArray(); w.WriteStartArray("tools");
            foreach (var tool in request.Tools) { w.WriteStartObject(); w.WriteString("name", tool.Name); w.WriteString("description", tool.Description); w.WriteString("parametersJson", tool.Parameters.Json); w.WriteEndObject(); }
            w.WriteEndArray(); w.WriteEndObject();
        }, 2097152);
        return new(request, new(request.RoundId, fence, instanceId, canonical), turn);
    }

    public static ChatUsageFact Usage(Guid roundId, AiTokenUsage usage, ChatUsageOrigin origin)
    { ArgumentNullException.ThrowIfNull(usage); return new(roundId, origin, usage.InputTokens, usage.OutputTokens, usage.TotalTokens); }
    public static ChatProviderFailureFact Failure(Guid roundId, AiFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        var code = failure.Code switch
        {
            AiFailureCode.InvalidRequest => ChatProviderFailureCode.InvalidRequest,
            AiFailureCode.ProfileUnavailable => ChatProviderFailureCode.ProfileUnavailable,
            AiFailureCode.MetadataDisallowed => ChatProviderFailureCode.MetadataDisallowed,
            AiFailureCode.CapabilityUnavailable => ChatProviderFailureCode.CapabilityUnavailable,
            AiFailureCode.CredentialUnavailable => ChatProviderFailureCode.CredentialUnavailable,
            AiFailureCode.Busy => ChatProviderFailureCode.Busy,
            AiFailureCode.BudgetExceeded => ChatProviderFailureCode.BudgetExceeded,
            AiFailureCode.Cancelled => ChatProviderFailureCode.Cancelled,
            AiFailureCode.DeadlineExceeded => ChatProviderFailureCode.DeadlineExceeded,
            AiFailureCode.TransportFailure => ChatProviderFailureCode.TransportFailure,
            AiFailureCode.ProviderAuthentication => ChatProviderFailureCode.ProviderAuthentication,
            AiFailureCode.ProviderRateLimited => ChatProviderFailureCode.ProviderRateLimited,
            AiFailureCode.ProviderRejected => ChatProviderFailureCode.ProviderRejected,
            AiFailureCode.ProviderUnavailable => ChatProviderFailureCode.ProviderUnavailable,
            AiFailureCode.UnsupportedResponse => ChatProviderFailureCode.UnsupportedResponse,
            AiFailureCode.MalformedResponse => ChatProviderFailureCode.MalformedResponse,
            AiFailureCode.TruncatedResponse => ChatProviderFailureCode.TruncatedResponse,
            _ => throw ChatBounds.Invalid()
        };
        return new(roundId, code, failure.Dispatch switch { AiDispatchState.NotAttempted => ChatDispatchObservation.NotAttempted, AiDispatchState.Attempted => ChatDispatchObservation.Attempted, _ => throw ChatBounds.Invalid() });
    }
    public static ChatRoundCompletion Completion(Guid roundId, AiRoundEvent terminal) => terminal switch
    {
        AiRoundFinished finished => new(finished.Reason == AiFinishReason.ToolCalls ? ChatRoundOutcome.ToolCalls : ChatRoundOutcome.Stopped,
            new(Usage(roundId, finished.Usage, ChatUsageOrigin.ProviderRoundTerminal), finishReason: finished.Reason switch
            {
                AiFinishReason.Stop => ChatProviderFinishReason.Stop,
                AiFinishReason.ToolCalls => ChatProviderFinishReason.ToolCalls,
                AiFinishReason.OutputLimit => ChatProviderFinishReason.OutputLimit,
                AiFinishReason.Refused => ChatProviderFinishReason.Refused,
                AiFinishReason.ContentFiltered => ChatProviderFinishReason.ContentFiltered,
                _ => throw ChatBounds.Invalid()
            })),
        AiRoundFailed failed => new(ChatRoundOutcome.Failed, new(Usage(roundId, failed.Usage, ChatUsageOrigin.ProviderRoundTerminal), Failure(roundId, failed.Failure))),
        _ => throw ChatBounds.Invalid()
    };
    public static ChatRoundCompletion Rejection(Guid reservedRoundId, AiFailure failure) => new(ChatRoundOutcome.Failed,
        new(new(reservedRoundId, ChatUsageOrigin.ProviderOpenRejection, null, null, null), Failure(reservedRoundId, failure)));
}

/// <summary>A locally prepared immutable request, consumed once after the exact original reservation acknowledges.</summary>
public sealed class ChatPreparedRound
{
    private readonly AiRoundRequest _request;
    private readonly ChatTurnIntent _turn;
    private int _taken;
    internal ChatPreparedRound(AiRoundRequest request, ChatRoundIntent intent, ChatTurnIntent turn) { _request = request; Intent = intent; _turn = turn; }
    public ChatRoundIntent Intent { get; }
    public AiRoundRequest TakeAfterAcknowledgement(ChatRoundWriteResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var round = result.Round; var turn = result.Turn;
        if (result.Outcome != ChatStoreOutcome.Applied || round is null || turn is null || round.Intent != Intent || round.Outcome != ChatRoundOutcome.Uncertain ||
            turn.Phase != ChatTurnPhase.Active || turn.Intent != _turn || round.Ordinal != turn.RoundCount ||
            Interlocked.Exchange(ref _taken, 1) != 0) throw new InvalidOperationException("Chat dispatch permission is unavailable.");
        return _request; // Exactly Intent.RoundId; no replacement UUID, deserialization or retry.
    }
    public override string ToString() => nameof(ChatPreparedRound);
}
