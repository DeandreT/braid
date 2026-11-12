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

public enum ChatWorkspaceCitationKind
{
    Project = 1, Mapping, Revision, Report, PreviewRun, RegressionCase, RegressionRun, BuildRun, BuildApproval, BuildEnvironment, GuideSection
}

/// <summary>
/// A workspace citation's exact identity: a catalog resource, a resource of one exact retained report, a run or
/// control record, or a user-guide section. Persistable facts, not proof: only a fresh verification admits one.
/// </summary>
public sealed record ChatWorkspaceCitationFact
{
    private ChatWorkspaceCitationFact(ChatWorkspaceCitationKind kind, Guid organizationId, Guid? projectId = null, Guid? mappingId = null,
        Guid? revisionId = null, ChatReportPin? report = null, ChatCitationTarget? target = null, Guid? subjectId = null,
        long? version = null, string? sectionId = null)
    {
        ChatBounds.Id(organizationId);
        foreach (var id in new[] { projectId, mappingId, revisionId, subjectId }) if (id is { } value) ChatBounds.Id(value);
        if (version is < 1 || sectionId is not null && !SectionPattern(sectionId)) throw ChatBounds.Invalid();
        Kind = kind; OrganizationId = organizationId; ProjectId = projectId; MappingId = mappingId; RevisionId = revisionId;
        Report = report; Target = target; SubjectId = subjectId; Version = version; SectionId = sectionId;
    }
    public static ChatWorkspaceCitationFact Project(ProjectScope project) =>
        new(ChatWorkspaceCitationKind.Project, project.OrganizationId, project.ProjectId);
    public static ChatWorkspaceCitationFact Mapping(MappingScope mapping) =>
        new(ChatWorkspaceCitationKind.Mapping, mapping.OrganizationId, mapping.ProjectId, mapping.MappingId);
    public static ChatWorkspaceCitationFact Revision(RevisionScope revision) =>
        new(ChatWorkspaceCitationKind.Revision, revision.OrganizationId, revision.ProjectId, revision.MappingId, revision.RevisionId);
    /// <summary>A projection/validation resource, whole report or emitted diagnostic of one exact retained report.</summary>
    public static ChatWorkspaceCitationFact ReportResource(RevisionScope revision, ChatReportPin report, ChatCitationTarget target) =>
        new(ChatWorkspaceCitationKind.Report, revision.OrganizationId, revision.ProjectId, revision.MappingId, revision.RevisionId,
            report ?? throw ChatBounds.Invalid(), target ?? throw ChatBounds.Invalid());
    public static ChatWorkspaceCitationFact PreviewRun(RevisionScope revision, Guid runId) =>
        new(ChatWorkspaceCitationKind.PreviewRun, revision.OrganizationId, revision.ProjectId, revision.MappingId, revision.RevisionId, subjectId: Required(runId));
    public static ChatWorkspaceCitationFact RegressionCase(MappingScope mapping, Guid caseId, long version) =>
        new(ChatWorkspaceCitationKind.RegressionCase, mapping.OrganizationId, mapping.ProjectId, mapping.MappingId, subjectId: Required(caseId),
            version: version >= 1 ? version : throw ChatBounds.Invalid());
    public static ChatWorkspaceCitationFact RegressionRun(MappingScope mapping, Guid runId) =>
        new(ChatWorkspaceCitationKind.RegressionRun, mapping.OrganizationId, mapping.ProjectId, mapping.MappingId, subjectId: Required(runId));
    public static ChatWorkspaceCitationFact BuildRun(MappingScope mapping, Guid buildId) =>
        new(ChatWorkspaceCitationKind.BuildRun, mapping.OrganizationId, mapping.ProjectId, mapping.MappingId, subjectId: Required(buildId));
    public static ChatWorkspaceCitationFact BuildApproval(MappingScope mapping, Guid approvalId) =>
        new(ChatWorkspaceCitationKind.BuildApproval, mapping.OrganizationId, mapping.ProjectId, mapping.MappingId, subjectId: Required(approvalId));
    public static ChatWorkspaceCitationFact BuildEnvironment(ProjectScope project, Guid environmentId, long version) =>
        new(ChatWorkspaceCitationKind.BuildEnvironment, project.OrganizationId, project.ProjectId, subjectId: Required(environmentId),
            version: version >= 1 ? version : throw ChatBounds.Invalid());
    /// <summary>A section of the user guide shipped with this server; it carries the organization it was cited in.</summary>
    public static ChatWorkspaceCitationFact GuideSection(Guid organizationId, string sectionId) =>
        new(ChatWorkspaceCitationKind.GuideSection, organizationId, sectionId: sectionId ?? throw ChatBounds.Invalid());

    public ChatWorkspaceCitationKind Kind { get; }
    public Guid OrganizationId { get; }
    public Guid? ProjectId { get; }
    public Guid? MappingId { get; }
    public Guid? RevisionId { get; }
    public ChatReportPin? Report { get; }
    public ChatCitationTarget? Target { get; }
    public Guid? SubjectId { get; }
    public long? Version { get; }
    public string? SectionId { get; }
    public ProjectScope? ProjectScope => ProjectId is { } project ? new(OrganizationId, project) : null;
    public MappingScope? MappingScope => ProjectId is { } project && MappingId is { } mapping ? new(OrganizationId, project, mapping) : null;
    public RevisionScope? RevisionScope => ProjectId is { } project && MappingId is { } mapping && RevisionId is { } revision
        ? new(OrganizationId, project, mapping, revision) : null;
    /// <summary>Lower-case letters, digits and hyphens, 1–64 characters, starting with a letter or digit.</summary>
    public static bool SectionPattern(string value) =>
        value is { Length: >= 1 and <= 64 } && (char.IsAsciiLetterLower(value[0]) || char.IsAsciiDigit(value[0])) &&
        value.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');
    private static Guid Required(Guid id) { ChatBounds.Id(id); return id; }
    public override string ToString() => nameof(ChatWorkspaceCitationFact);
}

public enum ChatPayloadSource { PreviewInput = 1, PreviewOutput, RegressionCase, RegressionRunOutput }

/// <summary>Payload bytes one tool call returned to the model: identities and the byte count, never content.</summary>
public sealed record ChatPayloadDisclosureFact
{
    public const int MaximumBytes = 1_048_576;
    public ChatPayloadDisclosureFact(Guid projectId, ChatPayloadSource source, Guid subjectId, long? version, int byteCount)
    {
        ChatBounds.Id(projectId); ChatBounds.Id(subjectId);
        if (!Enum.IsDefined(source) || byteCount is < 1 or > MaximumBytes ||
            (source == ChatPayloadSource.RegressionCase) != (version is not null) || version is < 1) throw ChatBounds.Invalid();
        ProjectId = projectId; Source = source; SubjectId = subjectId; Version = version; ByteCount = byteCount;
    }
    public Guid ProjectId { get; }
    public ChatPayloadSource Source { get; }
    /// <summary>The preview run, regression case or regression run.</summary>
    public Guid SubjectId { get; }
    /// <summary>The regression case version; null for runs.</summary>
    public long? Version { get; }
    public int ByteCount { get; }
    public override string ToString() => nameof(ChatPayloadDisclosureFact);
}

public enum ChatMappingWriteOutcome { Saved = 1, Proposed }
public enum ChatMappingWriteReason { NotPermitted = 1, OutOfDate }

/// <summary>One field's plain-language rule before and after an edit, from verified projections, never model text.</summary>
public sealed record ChatMappingChange
{
    public const int MaximumText = 256;
    public ChatMappingChange(string field, string before, string after)
    {
        Field = Bound(field); Before = Bound(before); After = Bound(after);
    }
    public string Field { get; }
    public string Before { get; }
    public string After { get; }
    private static string Bound(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumText || value.Any(char.IsControl)) throw ChatBounds.Invalid();
        return ChatBounds.Text(value, 1024, false);
    }
    public override string ToString() => nameof(ChatMappingChange);
}

/// <summary>
/// A mapping edit a workspace tool saved as the next version of its exact base, or showed read-only because the
/// user may not edit or the base is no longer current. Identities, versions, the edit's size and digest, and at
/// most <see cref="MaximumChanges"/> changes; never the edit itself or any sample data.
/// </summary>
public sealed record ChatMappingWriteFact
{
    public const int MaximumChanges = 8;
    public ChatMappingWriteFact(ChatMappingWriteOutcome outcome, ChatMappingWriteReason? reason, RevisionScope baseRevision, long baseSequence,
        Guid? revisionId, long? sequence, Guid? headRevisionId, long? headSequence, int operationCount, Sha256Digest editDigest,
        IReadOnlyList<ChatMappingChange> changes, bool changesTruncated)
    {
        ArgumentNullException.ThrowIfNull(baseRevision); ArgumentNullException.ThrowIfNull(editDigest); ArgumentNullException.ThrowIfNull(changes);
        ChatBounds.Id(baseRevision.OrganizationId); ChatBounds.Id(baseRevision.ProjectId); ChatBounds.Id(baseRevision.MappingId); ChatBounds.Id(baseRevision.RevisionId);
        if (!Enum.IsDefined(outcome) || reason is { } r && !Enum.IsDefined(r) || baseSequence < 1 || operationCount is < 1 or > 2000 ||
            changes.Count > MaximumChanges || changes.Any(change => change is null) ||
            revisionId is null != sequence is null || headRevisionId is null != headSequence is null ||
            revisionId == Guid.Empty || headRevisionId == Guid.Empty || sequence < 1 || headSequence < 1) throw ChatBounds.Invalid();
        if (outcome == ChatMappingWriteOutcome.Saved
                ? reason is not null || revisionId is null || headRevisionId is not null || sequence <= baseSequence
                : reason is null || revisionId is not null || (reason == ChatMappingWriteReason.OutOfDate) != (headRevisionId is not null))
            throw ChatBounds.Invalid();
        Outcome = outcome; Reason = reason; Base = baseRevision; BaseSequence = baseSequence; RevisionId = revisionId; Sequence = sequence;
        HeadRevisionId = headRevisionId; HeadSequence = headSequence; OperationCount = operationCount; EditDigest = editDigest;
        Changes = changes.ToArray(); ChangesTruncated = changesTruncated;
    }
    public ChatMappingWriteOutcome Outcome { get; }
    public ChatMappingWriteReason? Reason { get; }
    public RevisionScope Base { get; }
    public long BaseSequence { get; }
    /// <summary>The saved version; null for a proposal.</summary>
    public Guid? RevisionId { get; }
    public long? Sequence { get; }
    /// <summary>The newer version observed for an out-of-date proposal.</summary>
    public Guid? HeadRevisionId { get; }
    public long? HeadSequence { get; }
    public int OperationCount { get; }
    public Sha256Digest EditDigest { get; }
    public IReadOnlyList<ChatMappingChange> Changes { get; }
    public bool ChangesTruncated { get; }

    /// <summary>The same write with the longest prefix of its changes whose record fits the bounded event.</summary>
    public ChatMappingWriteFact Fitted()
    {
        for (var count = Changes.Count; ; count--)
        {
            var fact = new ChatMappingWriteFact(Outcome, Reason, Base, BaseSequence, RevisionId, Sequence, HeadRevisionId, HeadSequence,
                OperationCount, EditDigest, Changes.Take(count).ToArray(), ChangesTruncated || count < Changes.Count);
            try { _ = ChatMetadataCodec.MappingWrite(fact); return fact; }
            catch (ArgumentException) when (count > 0) { }
        }
    }
    public override string ToString() => nameof(ChatMappingWriteFact);
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
    public static string WorkspaceCitation(ChatWorkspaceCitationFact value) => Json(w =>
    {
        w.WriteStartObject(); w.WriteNumber("v", 1); w.WriteNumber("kind", (int)value.Kind);
        w.WriteString("organization", value.OrganizationId); WriteId(w, "project", value.ProjectId);
        WriteId(w, "mapping", value.MappingId); WriteId(w, "revision", value.RevisionId);
        WriteId(w, "entry", value.Report?.CacheEntryId); WriteId(w, "attempt", value.Report?.DerivationId);
        w.WriteString("key", value.Report?.KeyFingerprint.Value); w.WriteString("digest", value.Report?.ObjectDigest.Value);
        WriteNumber(w, "targetKind", value.Target is { } target ? (int)target.Kind : null);
        w.WriteString("resource", value.Target?.ResourceId); WriteNumber(w, "ordinal", value.Target?.DiagnosticOrdinal);
        WriteId(w, "subject", value.SubjectId); WriteNumber(w, "version", value.Version); w.WriteString("section", value.SectionId);
        w.WriteEndObject();
    });
    public static string Disclosure(ChatPayloadDisclosureFact value) => Json(w =>
    {
        w.WriteStartObject(); w.WriteNumber("v", 1); w.WriteString("project", value.ProjectId); w.WriteNumber("source", (int)value.Source);
        w.WriteString("subject", value.SubjectId); WriteNumber(w, "version", value.Version); w.WriteNumber("bytes", value.ByteCount);
        w.WriteEndObject();
    });
    public static string MappingWrite(ChatMappingWriteFact value) => Json(w =>
    {
        w.WriteStartObject(); w.WriteNumber("v", 1); w.WriteNumber("outcome", (int)value.Outcome); WriteNumber(w, "reason", (int?)value.Reason);
        w.WriteString("organization", value.Base.OrganizationId); w.WriteString("project", value.Base.ProjectId);
        w.WriteString("mapping", value.Base.MappingId); w.WriteString("base", value.Base.RevisionId); w.WriteNumber("baseSequence", value.BaseSequence);
        WriteId(w, "revision", value.RevisionId); WriteNumber(w, "sequence", value.Sequence);
        WriteId(w, "head", value.HeadRevisionId); WriteNumber(w, "headSequence", value.HeadSequence);
        w.WriteNumber("operations", value.OperationCount); w.WriteString("digest", value.EditDigest.Value);
        w.WriteStartArray("changes");
        foreach (var change in value.Changes)
        {
            w.WriteStartObject(); w.WriteString("field", change.Field); w.WriteString("before", change.Before); w.WriteString("after", change.After); w.WriteEndObject();
        }
        w.WriteEndArray(); w.WriteBoolean("truncated", value.ChangesTruncated);
        w.WriteEndObject();
    });
    public static ChatMappingWriteFact ReadMappingWrite(string json) => Parse<ChatMappingWriteFact>(json, e => new(
        (ChatMappingWriteOutcome)e.GetProperty("outcome").GetInt32(), Number(e, "reason") is { } reason ? (ChatMappingWriteReason)checked((int)reason) : null,
        new(e.GetProperty("organization").GetGuid(), e.GetProperty("project").GetGuid(), e.GetProperty("mapping").GetGuid(), e.GetProperty("base").GetGuid()),
        e.GetProperty("baseSequence").GetInt64(), OptionalId(e, "revision"), Number(e, "sequence"), OptionalId(e, "head"), Number(e, "headSequence"),
        e.GetProperty("operations").GetInt32(), Sha256Digest.Parse(e.GetProperty("digest").GetString()!),
        e.GetProperty("changes").EnumerateArray().Select(change => new ChatMappingChange(change.GetProperty("field").GetString()!,
            change.GetProperty("before").GetString()!, change.GetProperty("after").GetString()!)).ToArray(),
        e.GetProperty("truncated").GetBoolean()), MappingWrite);
    public static ChatWorkspaceCitationFact ReadWorkspaceCitation(string json) => Parse(json, e =>
    {
        var organization = e.GetProperty("organization").GetGuid();
        Guid Id(string name) => e.GetProperty(name).GetGuid();
        var kind = (ChatWorkspaceCitationKind)e.GetProperty("kind").GetInt32();
        return kind switch
        {
            ChatWorkspaceCitationKind.Project => ChatWorkspaceCitationFact.Project(new(organization, Id("project"))),
            ChatWorkspaceCitationKind.Mapping => ChatWorkspaceCitationFact.Mapping(new(organization, Id("project"), Id("mapping"))),
            ChatWorkspaceCitationKind.Revision => ChatWorkspaceCitationFact.Revision(new(organization, Id("project"), Id("mapping"), Id("revision"))),
            ChatWorkspaceCitationKind.Report => ChatWorkspaceCitationFact.ReportResource(new(organization, Id("project"), Id("mapping"), Id("revision")),
                new(Id("entry"), Id("attempt"), Sha256Digest.Parse(e.GetProperty("key").GetString()!), Sha256Digest.Parse(e.GetProperty("digest").GetString()!)),
                new((ChatCitationKind)e.GetProperty("targetKind").GetInt32(), e.GetProperty("resource").GetString(),
                    Number(e, "ordinal") is { } n ? checked((int)n) : null)),
            ChatWorkspaceCitationKind.PreviewRun => ChatWorkspaceCitationFact.PreviewRun(new(organization, Id("project"), Id("mapping"), Id("revision")), Id("subject")),
            ChatWorkspaceCitationKind.RegressionCase => ChatWorkspaceCitationFact.RegressionCase(new(organization, Id("project"), Id("mapping")), Id("subject"), e.GetProperty("version").GetInt64()),
            ChatWorkspaceCitationKind.RegressionRun => ChatWorkspaceCitationFact.RegressionRun(new(organization, Id("project"), Id("mapping")), Id("subject")),
            ChatWorkspaceCitationKind.BuildRun => ChatWorkspaceCitationFact.BuildRun(new(organization, Id("project"), Id("mapping")), Id("subject")),
            ChatWorkspaceCitationKind.BuildApproval => ChatWorkspaceCitationFact.BuildApproval(new(organization, Id("project"), Id("mapping")), Id("subject")),
            ChatWorkspaceCitationKind.BuildEnvironment => ChatWorkspaceCitationFact.BuildEnvironment(new(organization, Id("project")), Id("subject"), e.GetProperty("version").GetInt64()),
            ChatWorkspaceCitationKind.GuideSection => ChatWorkspaceCitationFact.GuideSection(organization, e.GetProperty("section").GetString()!),
            _ => throw ChatBounds.Invalid()
        };
    }, WorkspaceCitation);
    public static ChatPayloadDisclosureFact ReadDisclosure(string json) => Parse<ChatPayloadDisclosureFact>(json, e => new(
        e.GetProperty("project").GetGuid(), (ChatPayloadSource)e.GetProperty("source").GetInt32(), e.GetProperty("subject").GetGuid(),
        Number(e, "version"), e.GetProperty("bytes").GetInt32()), Disclosure);
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
    private static void WriteId(Utf8JsonWriter w, string name, Guid? id) { if (id is { } value) w.WriteString(name, value); else w.WriteNull(name); }
    private static long? Number(JsonElement e, string name) => e.GetProperty(name).ValueKind == JsonValueKind.Null ? null : e.GetProperty(name).GetInt64();
    private static Guid? OptionalId(JsonElement e, string name) => e.GetProperty(name).ValueKind == JsonValueKind.Null ? null : e.GetProperty(name).GetGuid();
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
