using System.Text;

namespace Weft.Domain;

/// <summary>Stable ownership, independent of login epochs and administrator roles.</summary>
public sealed record ChatConversationKey
{
    public ChatConversationKey(RevisionScope revision, PrincipalId creator, Guid conversationId)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(creator);
        ChatBounds.Id(conversationId);
        Revision = revision;
        Creator = creator;
        ConversationId = conversationId;
    }
    public RevisionScope Revision { get; }
    public PrincipalId Creator { get; }
    public Guid ConversationId { get; }
    public override string ToString() => nameof(ChatConversationKey);
}

/// <summary>Exact retained successful attempt, not the mutable selected-report pointer.</summary>
public sealed record ChatReportPin
{
    public ChatReportPin(Guid cacheEntryId, Guid derivationId, Sha256Digest keyFingerprint, Sha256Digest objectDigest)
    {
        ChatBounds.Id(cacheEntryId);
        ChatBounds.Id(derivationId);
        ArgumentNullException.ThrowIfNull(keyFingerprint);
        ArgumentNullException.ThrowIfNull(objectDigest);
        CacheEntryId = cacheEntryId;
        DerivationId = derivationId;
        KeyFingerprint = keyFingerprint;
        ObjectDigest = objectDigest;
    }
    public Guid CacheEntryId { get; }
    public Guid DerivationId { get; }
    public Sha256Digest KeyFingerprint { get; }
    public Sha256Digest ObjectDigest { get; }
    public override string ToString() => nameof(ChatReportPin);
}

public sealed record ChatConversationIntent
{
    /// <param name="retainUntilUtc">Absolute deadline supplied by trusted retention policy, never a browser default.</param>
    public ChatConversationIntent(ChatConversationKey key, ChatReportPin projection, ChatReportPin validation,
        DateTimeOffset retainUntilUtc)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(validation);
        ChatBounds.Instant(retainUntilUtc);
        if (projection.DerivationId == validation.DerivationId) throw ChatBounds.Invalid();
        Key = key;
        Projection = projection;
        Validation = validation;
        RetainUntilUtc = retainUntilUtc;
    }
    public ChatConversationKey Key { get; }
    public ChatReportPin Projection { get; }
    public ChatReportPin Validation { get; }
    public DateTimeOffset RetainUntilUtc { get; }
    public override string ToString() => nameof(ChatConversationIntent);
}

/// <summary>No endpoint or credential value. Configuration fingerprint is resolved by trusted catalog composition.</summary>
public sealed record ChatProfilePin
{
    public ChatProfilePin(string profileId, int version, string providerId, string modelId,
        string metadataPolicyId, int metadataPolicyVersion, Sha256Digest configurationFingerprint)
    {
        ProfileId = ChatBounds.Identifier(profileId);
        ProviderId = ChatBounds.Identifier(providerId);
        ModelId = ChatBounds.Text(modelId, 256, false);
        if (ModelId.Any(char.IsControl)) throw ChatBounds.Invalid();
        MetadataPolicyId = ChatBounds.Identifier(metadataPolicyId);
        if (version < 1 || metadataPolicyVersion < 1) throw ChatBounds.Invalid();
        ArgumentNullException.ThrowIfNull(configurationFingerprint);
        Version = version;
        MetadataPolicyVersion = metadataPolicyVersion;
        ConfigurationFingerprint = configurationFingerprint;
    }
    public string ProfileId { get; }
    public int Version { get; }
    public string ProviderId { get; }
    public string ModelId { get; }
    public string MetadataPolicyId { get; }
    public int MetadataPolicyVersion { get; }
    public Sha256Digest ConfigurationFingerprint { get; }
    public string FingerprintSchema => "weft.chat.profile.v1";
    public override string ToString() => nameof(ChatProfilePin);
}

/// <summary>Persistence ceilings, not provider token or wire limits.</summary>
public sealed record ChatTurnBudget
{
    public ChatTurnBudget(int maxEvents, int maxEventBytes, int maxRounds)
    {
        if (maxEvents is < 2 or > 4096 || maxEventBytes is < 1 or > 4194304 || maxRounds is < 1 or > 4)
            throw ChatBounds.Invalid();
        MaxEvents = maxEvents;
        MaxEventBytes = maxEventBytes;
        MaxRounds = maxRounds;
    }
    public int MaxEvents { get; }
    public int MaxEventBytes { get; }
    public int MaxRounds { get; }
    public override string ToString() => nameof(ChatTurnBudget);
}

public sealed record ChatTurnIntent
{
    public ChatTurnIntent(Guid turnId, ChatOriginalEnvelope envelope, ChatProfilePin profile, ChatTurnBudget budget,
        DateTimeOffset deadlineUtc)
    {
        ChatBounds.Id(turnId);
        ArgumentNullException.ThrowIfNull(envelope);
        OriginalRequest = envelope.UserText;
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(budget);
        if (budget.MaxRounds > envelope.ProviderBudget.MaxRounds) throw ChatBounds.Invalid();
        ChatBounds.Instant(deadlineUtc);
        TurnId = turnId;
        Envelope = envelope;
        Profile = profile;
        Budget = budget;
        DeadlineUtc = deadlineUtc;
    }
    public Guid TurnId { get; }
    public string OriginalRequest { get; }
    public ChatOriginalEnvelope Envelope { get; }
    public ChatProfilePin Profile { get; }
    public ChatTurnBudget Budget { get; }
    public DateTimeOffset DeadlineUtc { get; }
    public override string ToString() => nameof(ChatTurnIntent);
}

public enum ChatTurnPhase { Active = 1, Completed, Failed, Cancelled, Interrupted }
public enum ChatRoundOutcome { Uncertain = 1, ToolCalls, Stopped, Failed }
public enum ChatEventKind { Started = 1, TextDelta, ToolStarted, ToolCompleted, Completed, Failed, Cancelled, Interrupted, Usage, ProviderFailure, Citation, RoundCompleted }

/// <summary>One irreversible dispatch reservation. Uncertain includes reserved-but-unsent.</summary>
public sealed record ChatRoundIntent
{
    public ChatRoundIntent(Guid roundId, Guid fence, Guid instanceId, string exactRequest)
    {
        ChatBounds.Id(roundId);
        ChatBounds.Id(fence);
        ChatBounds.Id(instanceId);
        RoundId = roundId;
        Fence = fence;
        InstanceId = instanceId;
        ExactRequest = ChatBounds.Text(exactRequest, 2097152, false);
    }
    public Guid RoundId { get; }
    public Guid Fence { get; }
    public Guid InstanceId { get; }
    public string ExactRequest { get; }
    public override string ToString() => nameof(ChatRoundIntent);
}

/// <summary>Private bounded event data. Tool execution/validation is not authorized by this type.</summary>
public sealed record ChatEventData
{
    private ChatEventData(Guid eventId, ChatEventKind kind, string text, string? metadata)
    {
        ChatBounds.Id(eventId); EventId = eventId; Kind = kind; Text = text; MetadataJson = metadata;
        ByteCount = Encoding.UTF8.GetByteCount(text) + (metadata is null ? 0 : Encoding.UTF8.GetByteCount(metadata));
        if (ByteCount > 8192) throw ChatBounds.Invalid();
    }
    public ChatEventData(Guid eventId, ChatEventKind kind, string text)
        : this(eventId, kind, ChatBounds.Text(text, 8192, false), null)
    {
        if (kind is not (ChatEventKind.TextDelta or ChatEventKind.ToolStarted or ChatEventKind.ToolCompleted))
            throw ChatBounds.Invalid();
    }
    public static ChatEventData ForUsage(Guid eventId, ChatUsageFact usage) => new(eventId, ChatEventKind.Usage, "", ChatMetadataCodec.Usage(usage));
    public static ChatEventData ForFailure(Guid eventId, ChatProviderFailureFact failure) => new(eventId, ChatEventKind.ProviderFailure, "", ChatMetadataCodec.Failure(failure));
    public Guid EventId { get; }
    public ChatEventKind Kind { get; }
    public string Text { get; }
    public string? MetadataJson { get; }
    public int ByteCount { get; }
    public override string ToString() => nameof(ChatEventData);
}

public static class ChatBounds
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public static ArgumentException Invalid() => new("Invalid chat facts.");
    public static void Id(Guid id) { if (id == Guid.Empty) throw Invalid(); }
    public static string Identifier(string value)
    {
        if (value is null || value.Length is < 1 or > 64 || !char.IsAsciiLetterLower(value[0]) && !char.IsAsciiDigit(value[0]) ||
            value.Any(c => !char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c is not ('.' or '_' or '-'))) throw Invalid();
        return value;
    }
    public static void Instant(DateTimeOffset value)
    {
        // Match PostgreSQL microsecond precision rather than silently rounding immutable inputs.
        if (value.Offset != TimeSpan.Zero || value.Ticks % 10 != 0 || value == DateTimeOffset.MinValue || value == DateTimeOffset.MaxValue)
            throw Invalid();
    }
    public static string Text(string value, int maxBytes, bool allowEmpty)
    {
        if (value is null || value.Length > maxBytes || value.Contains('\0') || (!allowEmpty && string.IsNullOrWhiteSpace(value)))
            throw Invalid();
        try { if (StrictUtf8.GetByteCount(value) > maxBytes) throw Invalid(); }
        catch (EncoderFallbackException) { throw Invalid(); }
        return value;
    }
}
