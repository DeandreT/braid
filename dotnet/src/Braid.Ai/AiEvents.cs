using System;

namespace Braid.Ai;

public abstract class AiRoundEvent
{
    private protected AiRoundEvent() { }
    public sealed override string ToString() => nameof(AiRoundEvent);
}
public sealed class AiTextDelta : AiRoundEvent
{
    private AiTextDelta(string text) { Text = text; }
    public string Text { get; }
    public static AiTextDelta Create(string text) => new AiTextDelta(AiGuard.Text(text, AiCeilings.DeltaBytes, false));
}
public sealed class AiToolIntentReady : AiRoundEvent
{
    private AiToolIntentReady(AiToolIntent intent) { Intent = intent; }
    public AiToolIntent Intent { get; }
    public static AiToolIntentReady Create(AiToolIntent intent) => new AiToolIntentReady(intent ?? throw AiGuard.Invalid());
}
public sealed class AiUsageObserved : AiRoundEvent
{
    private AiUsageObserved(AiTokenUsage usage) { Usage = usage; }
    public AiTokenUsage Usage { get; }
    public static AiUsageObserved Create(AiTokenUsage usage) => new AiUsageObserved(usage ?? throw AiGuard.Invalid());
}
public sealed class AiRoundFinished : AiRoundEvent
{
    private AiRoundFinished(AiFinishReason reason, AiTokenUsage usage) { Reason = reason; Usage = usage; }
    public AiFinishReason Reason { get; }
    public AiTokenUsage Usage { get; }
    public static AiRoundFinished Create(AiFinishReason reason, AiTokenUsage usage)
    {
        if (!Enum.IsDefined(reason) || usage is null) throw AiGuard.Invalid();
        return new AiRoundFinished(reason, usage);
    }
}
public sealed class AiRoundFailed : AiRoundEvent
{
    private AiRoundFailed(AiFailure failure, AiTokenUsage usage) { Failure = failure; Usage = usage; }
    public AiFailure Failure { get; }
    public AiTokenUsage Usage { get; }
    public static AiRoundFailed Create(AiFailure failure, AiTokenUsage usage) => new AiRoundFailed(
        failure ?? throw AiGuard.Invalid(), usage ?? throw AiGuard.Invalid());
}
public abstract class AiTurnOpenResult
{
    private protected AiTurnOpenResult() { }
    public sealed override string ToString() => nameof(AiTurnOpenResult);
}
public sealed class TurnOpened : AiTurnOpenResult
{
    internal TurnOpened(AiTurnLease turn) { Turn = turn; }
    public AiTurnLease Turn { get; }
}
public sealed class TurnRejected : AiTurnOpenResult
{
    internal TurnRejected(AiFailure failure) { Failure = failure; }
    public AiFailure Failure { get; }
}
public abstract class AiRoundOpenResult
{
    private protected AiRoundOpenResult() { }
    public sealed override string ToString() => nameof(AiRoundOpenResult);
}
public sealed class RoundOpened : AiRoundOpenResult
{
    internal RoundOpened(AiRoundStream stream, int ordinal) { Stream = stream; Ordinal = ordinal; }
    public AiRoundStream Stream { get; }
    public int Ordinal { get; }
}
public sealed class RoundRejected : AiRoundOpenResult
{
    internal RoundRejected(AiFailure failure) { Failure = failure; }
    public AiFailure Failure { get; }
}
