using Braid.Domain;

namespace Braid.Application;

public enum AiQuotaAdmission { Allowed = 1, TurnLimitReached, TokenLimitReached, TokenBoundUnavailable, TokenBoundMismatch, CoverageUnavailable }

public sealed record AiTokenAdmissionRequest
{
    internal AiTokenAdmissionRequest(ChatConversationKey conversation, Guid turnId, Sha256Digest envelope, Sha256Digest profile,
        ChatTurnBudget budget, DateTimeOffset deadlineUtc)
    {
        ArgumentNullException.ThrowIfNull(conversation); ArgumentNullException.ThrowIfNull(envelope); ArgumentNullException.ThrowIfNull(profile);
        if (turnId == Guid.Empty) throw new ArgumentException("A turn identity is required.");
        ArgumentNullException.ThrowIfNull(budget); ChatBounds.Instant(deadlineUtc);
        Conversation = conversation; TurnId = turnId; EnvelopeFingerprint = envelope; ProfileFingerprint = profile;
        Budget = budget; DeadlineUtc = deadlineUtc;
    }

    public ChatConversationKey Conversation { get; }
    public Guid TurnId { get; }
    public Sha256Digest EnvelopeFingerprint { get; }
    public Sha256Digest ProfileFingerprint { get; }
    public ChatTurnBudget Budget { get; }
    public DateTimeOffset DeadlineUtc { get; }

    public static AiTokenAdmissionRequest From(ChatConversationKey conversation, ChatTurnIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        return new(conversation, intent.TurnId, intent.Envelope.Fingerprint, intent.Profile.ConfigurationFingerprint, intent.Budget, intent.DeadlineUtc);
    }
}

/// <summary>Issued only by trusted, qualified application code, never by a request or an operator-entered estimate.</summary>
public sealed class AiTokenUpperBound
{
    internal AiTokenUpperBound(AiTokenAdmissionRequest request, string authorityId, int authorityVersion, long maximumTokens)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrEmpty(authorityId) || authorityId.Length > 64 || authorityId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-')) ||
            authorityVersion < 1 || maximumTokens < 1) throw new ArgumentException("Invalid verified token bound.");
        Request = request; AuthorityId = authorityId; AuthorityVersion = authorityVersion; MaximumTokens = maximumTokens;
    }

    public AiTokenAdmissionRequest Request { get; }
    public string AuthorityId { get; }
    public int AuthorityVersion { get; }
    /// <summary>Covers all input, output and later rounds the turn may dispatch.</summary>
    public long MaximumTokens { get; }
}

public interface IAiTokenBoundAuthority
{
    Task<AiTokenUpperBound?> BoundAsync(ActorContext actor, ChatConversationKey conversation, ChatTurnIntent intent, CancellationToken token);
}

/// <summary>No provider profile acquires a token guarantee merely by being configured.</summary>
public sealed class UnavailableAiTokenBoundAuthority : IAiTokenBoundAuthority
{
    public Task<AiTokenUpperBound?> BoundAsync(ActorContext actor, ChatConversationKey conversation, ChatTurnIntent intent, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult<AiTokenUpperBound?>(null);
    }
}

/// <summary>Hard-cap admission. Check and reserve atomically; uncertain completed usage retains its full reservation.</summary>
public static class AiTokenAdmission
{
    public static AiQuotaAdmission Evaluate(AiTokenAdmissionRequest request, long admittedTurns, long committedTokens,
        bool completeCoverage, long? maximumTurns, long? maximumTokens, AiTokenUpperBound? bound)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (admittedTurns < 0 || committedTokens < 0 || maximumTurns is < 1 || maximumTokens is < 1)
            throw new ArgumentException("Invalid AI quota observation.");
        if ((maximumTurns is not null || maximumTokens is not null) && !completeCoverage) return AiQuotaAdmission.CoverageUnavailable;
        if (maximumTurns is { } turns && admittedTurns >= turns) return AiQuotaAdmission.TurnLimitReached;
        if (maximumTokens is not { } tokens) return AiQuotaAdmission.Allowed;
        if (bound is null) return AiQuotaAdmission.TokenBoundUnavailable;
        if (bound.Request != request) return AiQuotaAdmission.TokenBoundMismatch;
        if (committedTokens >= tokens || bound.MaximumTokens > tokens - committedTokens) return AiQuotaAdmission.TokenLimitReached;
        return AiQuotaAdmission.Allowed;
    }
}
