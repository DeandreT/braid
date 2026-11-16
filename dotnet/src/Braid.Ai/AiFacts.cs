using System;

namespace Braid.Ai;

public enum AiDialect { ChatCompletionsSseV1, OllamaChatNdjsonV1, DatabricksChatCompletionsSseV1 }
[Flags]
public enum AiCapability { Text = 1, FunctionTools = 2 }
public enum AiAuthentication { None, Bearer }
public enum AiFinishReason { Stop, ToolCalls, OutputLimit, Refused, ContentFiltered }
public enum AiDispatchState { NotAttempted, Attempted }
public enum AiFailureCode
{
    InvalidRequest, ProfileUnavailable, MetadataDisallowed, CapabilityUnavailable,
    CredentialUnavailable, Busy, BudgetExceeded, Cancelled, DeadlineExceeded,
    TransportFailure, ProviderAuthentication, ProviderRateLimited, ProviderRejected,
    ProviderUnavailable, UnsupportedResponse, MalformedResponse, TruncatedResponse
}

public sealed class AiFailure
{
    private AiFailure(AiFailureCode code, AiDispatchState dispatch) { Code = code; Dispatch = dispatch; }
    public AiFailureCode Code { get; }
    public AiDispatchState Dispatch { get; }
    public static AiFailure Create(AiFailureCode code, AiDispatchState dispatch)
    {
        if (!Enum.IsDefined(code) || !Enum.IsDefined(dispatch)) throw AiGuard.Invalid();
        return new AiFailure(code, dispatch);
    }
    public override string ToString() => nameof(AiFailure);
}

public sealed class AiTokenUsage
{
    private AiTokenUsage(long? input, long? output, long? total)
    { InputTokens = input; OutputTokens = output; TotalTokens = total; }
    public long? InputTokens { get; }
    public long? OutputTokens { get; }
    public long? TotalTokens { get; }
    public static AiTokenUsage Unknown { get; } = new AiTokenUsage(null, null, null);
    public static AiTokenUsage Create(long? inputTokens, long? outputTokens, long? totalTokens)
    {
        foreach (long? value in new[] { inputTokens, outputTokens, totalTokens })
            if (value is < 0 or > 1_000_000_000_000) throw AiGuard.Invalid();
        return new AiTokenUsage(inputTokens, outputTokens, totalTokens);
    }
    public override string ToString() => nameof(AiTokenUsage);
}

public sealed class AiTurnLimits
{
    private AiTurnLimits(TimeSpan duration, int rounds, long request, long wire, long text, int tools)
    { Duration = duration; MaxRounds = rounds; MaxRequestBytes = request; MaxWireBytes = wire; MaxTextBytes = text; MaxToolCalls = tools; }
    public TimeSpan Duration { get; }
    public int MaxRounds { get; }
    public long MaxRequestBytes { get; }
    public long MaxWireBytes { get; }
    public long MaxTextBytes { get; }
    public int MaxToolCalls { get; }
    public static AiTurnLimits Default { get; } = Create(TimeSpan.FromSeconds(180), 4, 1_048_576, 4_194_304, 131_072, 8);
    public static AiTurnLimits Create(TimeSpan duration, int maxRounds, long maxRequestBytes,
        long maxWireBytes, long maxTextBytes, int maxToolCalls)
    {
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromSeconds(180)
            || maxRounds is < 1 or > 5 || maxRequestBytes is < 1 or > 1_048_576
            || maxWireBytes is < 1 or > 4_194_304 || maxTextBytes is < 1 or > 131_072
            || maxToolCalls is < 1 or > 8) throw AiGuard.Invalid();
        return new AiTurnLimits(duration, maxRounds, maxRequestBytes, maxWireBytes, maxTextBytes, maxToolCalls);
    }
    public override string ToString() => nameof(AiTurnLimits);
}

internal static class AiCeilings
{
    internal const int Profiles = 64, Messages = 64, Definitions = 24, Calls = 8;
    internal const int MessageBytes = 65_536, MessageTotalBytes = 131_072, RequestBytes = 262_144;
    internal const int SchemaBytes = 8_192, SchemasTotalBytes = 49_152, DescriptionBytes = 1_024;
    internal const int ArgumentBytes = 16_384, ResultBytes = 32_768, JsonBytes = 131_072;
    internal const int RoundWireBytes = 2_097_152, LineBytes = 131_072, DeltaBytes = 8_192;
    internal const int Frames = 8_192, Events = 16_384, JsonDepth = 16, JsonTokens = 4_096, JsonElements = 2_048;
}

internal sealed class AiProtocolException : Exception
{
    internal AiProtocolException(AiFailureCode code) : base("AI protocol refused.") { Code = code; }
    internal AiFailureCode Code { get; }
    public override string ToString() => nameof(AiProtocolException);
}
