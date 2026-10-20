using System.Text;
using System.Text.Json;

namespace Weft.Domain;

public enum ChatUsageOrigin { ProviderObservation = 1, ProviderRoundTerminal = 2, ProviderOpenRejection = 3 }
public enum ChatProviderFinishReason { Stop = 1, ToolCalls, OutputLimit, Refused, ContentFiltered }
public enum ChatProviderFailureCode
{
    InvalidRequest = 1, ProfileUnavailable, MetadataDisallowed, CapabilityUnavailable, CredentialUnavailable,
    Busy, BudgetExceeded, Cancelled, DeadlineExceeded, TransportFailure, ProviderAuthentication,
    ProviderRateLimited, ProviderRejected, ProviderUnavailable, UnsupportedResponse, MalformedResponse, TruncatedResponse
}
public enum ChatDispatchObservation { NotAttempted = 1, Attempted = 2 }
public enum ChatCitationKind { Boundary = 1, Field, Parameter, Rule, Control, ProjectionDiagnostic, ValidationDiagnostic, ProjectionReport, ValidationReport }

/// <summary>Per-round observation, never an inferred aggregate. All-null is unknown, not zero.</summary>
public sealed record ChatUsageFact
{
    public ChatUsageFact(Guid roundId, ChatUsageOrigin origin, long? input, long? output, long? total)
    {
        ChatBounds.Id(roundId);
        if (!Enum.IsDefined(origin) || new[] { input, output, total }.Any(n => n is < 0 or > 1_000_000_000_000)) throw ChatBounds.Invalid();
        if (origin == ChatUsageOrigin.ProviderOpenRejection && (input is not null || output is not null || total is not null)) throw ChatBounds.Invalid();
        RoundId = roundId; Origin = origin; InputTokens = input; OutputTokens = output; TotalTokens = total;
    }
    public Guid RoundId { get; }
    public ChatUsageOrigin Origin { get; }
    public long? InputTokens { get; }
    public long? OutputTokens { get; }
    public long? TotalTokens { get; }
    public bool IsKnown => InputTokens is not null || OutputTokens is not null || TotalTokens is not null;
    public override string ToString() => nameof(ChatUsageFact);
}

public sealed record ChatProviderFailureFact
{
    public ChatProviderFailureFact(Guid roundId, ChatProviderFailureCode code, ChatDispatchObservation dispatch)
    {
        ChatBounds.Id(roundId);
        if (!Enum.IsDefined(code) || !Enum.IsDefined(dispatch)) throw ChatBounds.Invalid();
        RoundId = roundId; Code = code; Dispatch = dispatch;
    }
    public Guid RoundId { get; }
    public ChatProviderFailureCode Code { get; }
    public ChatDispatchObservation Dispatch { get; }
    public override string ToString() => nameof(ChatProviderFailureFact);
}

/// <summary>Diagnostic ordinals are zero-based in the exact retained report's emitted Diagnostics array.</summary>
public sealed record ChatCitationTarget
{
    public ChatCitationTarget(ChatCitationKind kind, string? resourceId = null, int? diagnosticOrdinal = null)
    {
        if (!Enum.IsDefined(kind)) throw ChatBounds.Invalid();
        if (kind is ChatCitationKind.ProjectionDiagnostic or ChatCitationKind.ValidationDiagnostic)
        { if (resourceId is not null || diagnosticOrdinal is null or < 0 or > 32767) throw ChatBounds.Invalid(); }
        else if (kind is ChatCitationKind.ProjectionReport or ChatCitationKind.ValidationReport)
        { if (resourceId is not null || diagnosticOrdinal is not null) throw ChatBounds.Invalid(); }
        else
        { if (diagnosticOrdinal is not null) throw ChatBounds.Invalid(); resourceId = ChatBounds.Text(resourceId!, 2048, false); }
        Kind = kind; ResourceId = resourceId; DiagnosticOrdinal = diagnosticOrdinal;
    }
    public ChatCitationKind Kind { get; }
    public string? ResourceId { get; }
    public int? DiagnosticOrdinal { get; }
    public override string ToString() => nameof(ChatCitationTarget);
}

/// <summary>Persistable facts, not proof of validation. Admission requires the Application proof type.</summary>
public sealed record ChatCitationFact(RevisionScope Revision, ChatReportPin Report, ChatCitationTarget Target)
{
    public override string ToString() => nameof(ChatCitationFact);
}

public sealed record ChatTerminalMetadata
{
    public ChatTerminalMetadata(ChatUsageFact? usage = null, ChatProviderFailureFact? failure = null, ChatProviderFinishReason? finishReason = null)
    {
        if (usage is null && failure is null || usage is not null && failure is not null && usage.RoundId != failure.RoundId) throw ChatBounds.Invalid();
        if (finishReason is { } reason && (!Enum.IsDefined(reason) || failure is not null || usage is null)) throw ChatBounds.Invalid();
        Usage = usage; Failure = failure; FinishReason = finishReason;
    }
    public ChatUsageFact? Usage { get; }
    public ChatProviderFailureFact? Failure { get; }
    public ChatProviderFinishReason? FinishReason { get; }
    public override string ToString() => nameof(ChatTerminalMetadata);
}

public sealed record ChatRoundCompletion
{
    public ChatRoundCompletion(ChatRoundOutcome outcome, ChatTerminalMetadata? metadata = null)
    {
        if (outcome is not (ChatRoundOutcome.ToolCalls or ChatRoundOutcome.Stopped or ChatRoundOutcome.Failed) ||
            metadata?.Usage is { Origin: not (ChatUsageOrigin.ProviderRoundTerminal or ChatUsageOrigin.ProviderOpenRejection) } ||
            metadata?.Failure is not null && outcome != ChatRoundOutcome.Failed) throw ChatBounds.Invalid();
        if (metadata?.FinishReason is { } finish && (outcome == ChatRoundOutcome.Failed || (outcome == ChatRoundOutcome.ToolCalls) != (finish == ChatProviderFinishReason.ToolCalls))) throw ChatBounds.Invalid();
        if (metadata is { Usage: { Origin: ChatUsageOrigin.ProviderOpenRejection } } && (outcome != ChatRoundOutcome.Failed || metadata.Failure is null)) throw ChatBounds.Invalid();
        Outcome = outcome; Metadata = metadata;
    }
    public ChatRoundOutcome Outcome { get; }
    public ChatTerminalMetadata? Metadata { get; }
    public override string ToString() => nameof(ChatRoundCompletion);
}

/// <summary>Explicit policy minimum. Passing this instant never automatically retires replay protection.</summary>
public sealed record ChatErasurePolicy
{
    public ChatErasurePolicy(string policyId, int version, DateTimeOffset identityRetirementNotBeforeUtc)
    {
        PolicyId = ChatBounds.Identifier(policyId); if (version < 1) throw ChatBounds.Invalid();
        ChatBounds.Instant(identityRetirementNotBeforeUtc);
        Version = version; IdentityRetirementNotBeforeUtc = identityRetirementNotBeforeUtc;
    }
    public string PolicyId { get; }
    public int Version { get; }
    public DateTimeOffset IdentityRetirementNotBeforeUtc { get; }
    public override string ToString() => nameof(ChatErasurePolicy);
}

/// <summary>Versioned exact compact JSON. Fixed property order, UTF-8, no whitespace, nulls preserved.</summary>
public static class ChatMetadataCodec
{
    public static string Usage(ChatUsageFact value) => Json(w => WriteUsage(w, value));
    public static string Failure(ChatProviderFailureFact value) => Json(w => WriteFailure(w, value));
    public static string Citation(ChatCitationFact value) => Json(w =>
    {
        w.WriteStartObject(); w.WriteNumber("v", 1);
        w.WriteString("organization", value.Revision.OrganizationId); w.WriteString("project", value.Revision.ProjectId);
        w.WriteString("mapping", value.Revision.MappingId); w.WriteString("revision", value.Revision.RevisionId);
        w.WriteString("entry", value.Report.CacheEntryId); w.WriteString("attempt", value.Report.DerivationId);
        w.WriteString("key", value.Report.KeyFingerprint.Value); w.WriteString("digest", value.Report.ObjectDigest.Value);
        w.WriteNumber("kind", (int)value.Target.Kind); w.WriteString("resource", value.Target.ResourceId);
        if (value.Target.DiagnosticOrdinal is { } n) w.WriteNumber("ordinal", n); else w.WriteNull("ordinal");
        w.WriteEndObject();
    });
    public static string Terminal(ChatTerminalMetadata value) => Json(w =>
    {
        w.WriteStartObject(); w.WriteNumber("v", 1); w.WritePropertyName("usage");
        if (value.Usage is { } usage) WriteUsage(w, usage); else w.WriteNullValue();
        w.WritePropertyName("failure"); if (value.Failure is { } failure) WriteFailure(w, failure); else w.WriteNullValue();
        if (value.FinishReason is { } finish) w.WriteNumber("finish", (int)finish); else w.WriteNull("finish");
        w.WriteEndObject();
    });
    public static ChatUsageFact ReadUsage(string json) => Parse(json, ReadUsage, Usage);
    public static ChatProviderFailureFact ReadFailure(string json) => Parse(json, ReadFailure, Failure);
    public static ChatCitationFact ReadCitation(string json) => Parse<ChatCitationFact>(json, e => new(
        new(e.GetProperty("organization").GetGuid(), e.GetProperty("project").GetGuid(), e.GetProperty("mapping").GetGuid(), e.GetProperty("revision").GetGuid()),
        new(e.GetProperty("entry").GetGuid(), e.GetProperty("attempt").GetGuid(), Sha256Digest.Parse(e.GetProperty("key").GetString()!), Sha256Digest.Parse(e.GetProperty("digest").GetString()!)),
        new((ChatCitationKind)e.GetProperty("kind").GetInt32(), e.GetProperty("resource").GetString(), Number(e, "ordinal") is { } n ? checked((int)n) : null)), Citation);
    public static ChatTerminalMetadata ReadTerminal(string json) => Parse<ChatTerminalMetadata>(json, e => new(
        e.GetProperty("usage").ValueKind == JsonValueKind.Null ? null : ReadUsage(e.GetProperty("usage")),
        e.GetProperty("failure").ValueKind == JsonValueKind.Null ? null : ReadFailure(e.GetProperty("failure")),
        e.GetProperty("finish").ValueKind == JsonValueKind.Null ? null : (ChatProviderFinishReason)e.GetProperty("finish").GetInt32()), Terminal);
    public static string Json(Action<Utf8JsonWriter> write)
    {
        using var bytes = new MemoryStream(); using (var writer = new Utf8JsonWriter(bytes)) write(writer);
        return ChatBounds.Text(Encoding.UTF8.GetString(bytes.ToArray()), 8192, false);
    }
    private static void WriteUsage(Utf8JsonWriter w, ChatUsageFact value)
    {
        w.WriteStartObject(); w.WriteNumber("v", 1); w.WriteString("round", value.RoundId); w.WriteNumber("origin", (int)value.Origin);
        w.WriteBoolean("known", value.IsKnown); WriteNumber(w, "input", value.InputTokens); WriteNumber(w, "output", value.OutputTokens); WriteNumber(w, "total", value.TotalTokens); w.WriteEndObject();
    }
    private static void WriteFailure(Utf8JsonWriter w, ChatProviderFailureFact value)
    {
        w.WriteStartObject(); w.WriteNumber("v", 1); w.WriteString("round", value.RoundId);
        w.WriteNumber("code", (int)value.Code); w.WriteNumber("dispatch", (int)value.Dispatch); w.WriteEndObject();
    }
    private static void WriteNumber(Utf8JsonWriter w, string name, long? n) { if (n is { } value) w.WriteNumber(name, value); else w.WriteNull(name); }
    private static long? Number(JsonElement e, string name) => e.GetProperty(name).ValueKind == JsonValueKind.Null ? null : e.GetProperty(name).GetInt64();
    private static ChatUsageFact ReadUsage(JsonElement e) => new(e.GetProperty("round").GetGuid(), (ChatUsageOrigin)e.GetProperty("origin").GetInt32(), Number(e, "input"), Number(e, "output"), Number(e, "total"));
    private static ChatProviderFailureFact ReadFailure(JsonElement e) => new(e.GetProperty("round").GetGuid(), (ChatProviderFailureCode)e.GetProperty("code").GetInt32(), (ChatDispatchObservation)e.GetProperty("dispatch").GetInt32());
    private static T Parse<T>(string json, Func<JsonElement, T> read, Func<T, string> write)
    {
        try
        {
            ChatBounds.Text(json, 8192, false);
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var value = read(document.RootElement);
            // Reject unknown/duplicate properties, alternate versions, noncanonical numbers, and lossy null handling.
            if (write(value) != json) throw ChatBounds.Invalid();
            return value;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or OverflowException or KeyNotFoundException or ArgumentException)
        { throw ChatBounds.Invalid(); }
    }
}
