using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace Weft.Domain;

/// <summary>Trusted versioned instruction/tool manifest and bounded retrieval policy, never user-supplied authority.</summary>
public sealed record ChatGroundingPolicyPin
{
    public ChatGroundingPolicyPin(string id, int version, Sha256Digest instructionsFingerprint, Sha256Digest toolsFingerprint,
        int maxRetrievedNodes, int maxToolResultBytes, int maxToolIterations)
    {
        Id = ChatBounds.Identifier(id);
        ArgumentNullException.ThrowIfNull(instructionsFingerprint); ArgumentNullException.ThrowIfNull(toolsFingerprint);
        if (version < 1 || maxRetrievedNodes is < 1 or > 1024 || maxToolResultBytes is < 1 or > 32768 || maxToolIterations is < 1 or > 8) throw ChatBounds.Invalid();
        Version = version; InstructionsFingerprint = instructionsFingerprint; ToolsFingerprint = toolsFingerprint;
        MaxRetrievedNodes = maxRetrievedNodes; MaxToolResultBytes = maxToolResultBytes; MaxToolIterations = maxToolIterations;
    }
    public string Id { get; }
    public int Version { get; }
    public Sha256Digest InstructionsFingerprint { get; }
    public Sha256Digest ToolsFingerprint { get; }
    public int MaxRetrievedNodes { get; }
    public int MaxToolResultBytes { get; }
    public int MaxToolIterations { get; }
    public override string ToString() => nameof(ChatGroundingPolicyPin);
}

/// <summary>Explicit provider limits matching the frozen #111 ceilings; no production retention default.</summary>
public sealed record ChatProviderBudget
{
    public ChatProviderBudget(long durationTicks, int rounds, long requestBytes, long wireBytes, long textBytes, int toolCalls, int maxOutputTokens)
    {
        if (durationTicks is < 1 or > 1_800_000_000 || rounds is < 1 or > 4 || requestBytes is < 1 or > 1048576 ||
            wireBytes is < 1 or > 4194304 || textBytes is < 1 or > 131072 || toolCalls is < 1 or > 8 || maxOutputTokens is < 1 or > 2048) throw ChatBounds.Invalid();
        DurationTicks = durationTicks; MaxRounds = rounds; MaxRequestBytes = requestBytes; MaxWireBytes = wireBytes;
        MaxTextBytes = textBytes; MaxToolCalls = toolCalls; MaxOutputTokens = maxOutputTokens;
    }
    public long DurationTicks { get; }
    public int MaxRounds { get; }
    public long MaxRequestBytes { get; }
    public long MaxWireBytes { get; }
    public long MaxTextBytes { get; }
    public int MaxToolCalls { get; }
    public int MaxOutputTokens { get; }
    public override string ToString() => nameof(ChatProviderBudget);
}

/// <summary>
/// V1 admits mapping metadata only: text and exact ordered selectors, no payload/attachment/action envelope.
/// V2 is a workspace turn's request: the same inputs plus the focus resolved when the turn started.
/// </summary>
public sealed class ChatOriginalEnvelope : IEquatable<ChatOriginalEnvelope>
{
    public ChatOriginalEnvelope(Guid requestId, string userText, IEnumerable<ChatCitationTarget> selectors, ChatProviderBudget providerBudget,
        ChatGroundingPolicyPin groundingPolicy, ChatTurnFocus? focus = null)
    {
        ChatBounds.Id(requestId); UserText = ChatBounds.Text(userText, 65536, false);
        ArgumentNullException.ThrowIfNull(selectors); ArgumentNullException.ThrowIfNull(providerBudget);
        ArgumentNullException.ThrowIfNull(groundingPolicy);
        var owned = new List<ChatCitationTarget>();
        foreach (var selector in selectors)
        {
            if (selector is null || owned.Count == 32 || owned.Contains(selector)) throw ChatBounds.Invalid();
            owned.Add(selector);
        }
        RequestId = requestId; Selectors = owned.ToImmutableArray(); ProviderBudget = providerBudget; GroundingPolicy = groundingPolicy; Focus = focus;
        CanonicalJson = ChatCanonicalJson.Write(w =>
        {
            w.WriteStartObject(); w.WriteString("schema", focus is null ? "weft.chat.original.v1" : "weft.chat.original.v2");
            w.WriteString("request", requestId); w.WriteString("text", UserText);
            w.WriteStartArray("selectors"); foreach (var target in Selectors)
            {
                w.WriteStartObject(); w.WriteNumber("kind", (int)target.Kind); w.WriteString("resource", target.ResourceId);
                if (target.DiagnosticOrdinal is { } n) w.WriteNumber("ordinal", n); else w.WriteNull("ordinal"); w.WriteEndObject();
            }
            w.WriteEndArray(); w.WriteStartObject("groundingPolicy"); w.WriteString("id", groundingPolicy.Id); w.WriteNumber("version", groundingPolicy.Version);
            w.WriteString("instructionsFingerprint", groundingPolicy.InstructionsFingerprint.Value); w.WriteString("toolsFingerprint", groundingPolicy.ToolsFingerprint.Value);
            w.WriteNumber("retrievedNodes", groundingPolicy.MaxRetrievedNodes); w.WriteNumber("toolResultBytes", groundingPolicy.MaxToolResultBytes);
            w.WriteNumber("toolIterations", groundingPolicy.MaxToolIterations); w.WriteEndObject();
            w.WriteStartObject("providerBudget"); w.WriteNumber("durationTicks", providerBudget.DurationTicks);
            w.WriteNumber("rounds", providerBudget.MaxRounds); w.WriteNumber("requestBytes", providerBudget.MaxRequestBytes);
            w.WriteNumber("wireBytes", providerBudget.MaxWireBytes); w.WriteNumber("textBytes", providerBudget.MaxTextBytes);
            w.WriteNumber("toolCalls", providerBudget.MaxToolCalls); w.WriteNumber("outputTokens", providerBudget.MaxOutputTokens);
            w.WriteEndObject();
            if (focus is not null)
            {
                w.WriteStartObject("focus"); w.WriteNumber("state", (int)focus.State);
                if (focus.Revision is { } revision)
                {
                    w.WriteString("organization", revision.OrganizationId); w.WriteString("project", revision.ProjectId);
                    w.WriteString("mapping", revision.MappingId); w.WriteString("revision", revision.RevisionId);
                }
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }, 524288);
        Fingerprint = Sha256Digest.FromBytes(Encoding.UTF8.GetBytes(CanonicalJson));
    }
    public Guid RequestId { get; }
    public string UserText { get; }
    public ImmutableArray<ChatCitationTarget> Selectors { get; }
    public ChatProviderBudget ProviderBudget { get; }
    public ChatGroundingPolicyPin GroundingPolicy { get; }
    /// <summary>The focus a workspace turn resolved at its start; null for a revision conversation's turn.</summary>
    public ChatTurnFocus? Focus { get; }
    public string CanonicalJson { get; }
    public Sha256Digest Fingerprint { get; }
    public bool Equals(ChatOriginalEnvelope? other) => other is not null && CanonicalJson == other.CanonicalJson;
    public override bool Equals(object? obj) => obj is ChatOriginalEnvelope other && Equals(other);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(CanonicalJson);
    public override string ToString() => nameof(ChatOriginalEnvelope);
    public static ChatOriginalEnvelope Restore(string json)
    {
        try
        {
            ChatBounds.Text(json, 524288, false); using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var e = document.RootElement; var budget = e.GetProperty("providerBudget"); var policy = e.GetProperty("groundingPolicy");
            var value = new ChatOriginalEnvelope(e.GetProperty("request").GetGuid(), e.GetProperty("text").GetString()!,
                e.GetProperty("selectors").EnumerateArray().Select(t => new ChatCitationTarget((ChatCitationKind)t.GetProperty("kind").GetInt32(),
                    t.GetProperty("resource").GetString(), t.GetProperty("ordinal").ValueKind == JsonValueKind.Null ? null : t.GetProperty("ordinal").GetInt32())),
                new(budget.GetProperty("durationTicks").GetInt64(), budget.GetProperty("rounds").GetInt32(), budget.GetProperty("requestBytes").GetInt64(),
                    budget.GetProperty("wireBytes").GetInt64(), budget.GetProperty("textBytes").GetInt64(), budget.GetProperty("toolCalls").GetInt32(), budget.GetProperty("outputTokens").GetInt32()),
                new(policy.GetProperty("id").GetString()!, policy.GetProperty("version").GetInt32(), Sha256Digest.Parse(policy.GetProperty("instructionsFingerprint").GetString()!),
                    Sha256Digest.Parse(policy.GetProperty("toolsFingerprint").GetString()!), policy.GetProperty("retrievedNodes").GetInt32(), policy.GetProperty("toolResultBytes").GetInt32(), policy.GetProperty("toolIterations").GetInt32()),
                e.GetProperty("schema").GetString() == "weft.chat.original.v2" ? ReadFocus(e.GetProperty("focus")) : null);
            if (value.CanonicalJson != json) throw ChatBounds.Invalid(); return value;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or FormatException or OverflowException)
        { throw ChatBounds.Invalid(); }
    }

    private static ChatTurnFocus ReadFocus(JsonElement e) => (ChatTurnFocusState)e.GetProperty("state").GetInt32() switch
    {
        ChatTurnFocusState.None => ChatTurnFocus.None,
        ChatTurnFocusState.Unavailable => ChatTurnFocus.Unavailable,
        ChatTurnFocusState.Resolved => ChatTurnFocus.Resolved(new(e.GetProperty("organization").GetGuid(), e.GetProperty("project").GetGuid(),
            e.GetProperty("mapping").GetGuid(), e.GetProperty("revision").GetGuid())),
        _ => throw ChatBounds.Invalid()
    };
}

public static class ChatCanonicalJson
{
    /// <summary>Default System.Text.Json encoder, fixed emitted order, UTF-8 without BOM, compact JSON, no Unicode normalization.</summary>
    public static string Write(Action<Utf8JsonWriter> write, int maximumBytes)
    {
        using var bytes = new MemoryStream(); using (var writer = new Utf8JsonWriter(bytes)) write(writer);
        return ChatBounds.Text(Encoding.UTF8.GetString(bytes.ToArray()), maximumBytes, false);
    }
}
