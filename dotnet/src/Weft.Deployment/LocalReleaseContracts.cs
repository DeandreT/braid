using System.Text.Json;
using Weft.Application;
using Weft.Domain;

namespace Weft.Deployment;

/// <summary>A trusted local target registration. It contains no address, credential or caller-selected executable.</summary>
public sealed record LocalReleaseTarget
{
    public LocalReleaseTarget(ReleaseTargetReference reference, BuildRuntimeProfile profile, string runtimeImage)
    {
        ArgumentNullException.ThrowIfNull(reference); ArgumentNullException.ThrowIfNull(profile);
        if (runtimeImage is not { Length: 71 } || !runtimeImage.StartsWith("sha256:", StringComparison.Ordinal) ||
            runtimeImage[7..].Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("An immutable runtime image is required.", nameof(runtimeImage));
        Reference = reference; Profile = profile; RuntimeImage = runtimeImage;
    }
    public ReleaseTargetReference Reference { get; }
    public BuildRuntimeProfile Profile { get; }
    public string RuntimeImage { get; }
    public byte[] CopyCanonicalBytes()
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteString("schema", "weft.local-release-target.v1");
            json.WriteString("organizationId", Reference.Mapping.OrganizationId.ToString("D"));
            json.WriteString("projectId", Reference.Mapping.ProjectId.ToString("D"));
            json.WriteString("mappingId", Reference.Mapping.MappingId.ToString("D"));
            json.WriteString("environmentId", Reference.Environment.EnvironmentId.ToString("D"));
            json.WriteNumber("environmentVersion", Reference.Environment.Version);
            json.WriteString("targetId", Reference.TargetId.ToString("D"));
            json.WriteString("runtimeImage", RuntimeImage);
            json.WritePropertyName("profile");
            JsonSerializer.Serialize(json, Profile, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            json.WriteEndObject();
        }
        return stream.ToArray();
    }
}

public sealed record LocalReleaseTargetSnapshot(LocalReleaseTarget Target, bool Enabled, Guid? ActiveReleaseId);
public enum ReleaseWriteOutcome { Created, Replayed, Conflict, Refused }
public enum ReleaseTransitionOutcome { Activated, Replayed, Canceled, Conflict, Refused }
public enum ReleaseAuditKind { Requested, Activated, Canceled, Rejected }
public enum ReleaseRejection { None, StaleActiveRelease, TargetUnavailable, ApprovalRevoked }
public sealed record ReleaseAuditEntry(int Sequence, ReleaseAuditKind Kind, DateTimeOffset RecordedAt, ReleaseRejection Rejection);

/// <summary>Active means a completed activation; the separate target pointer determines whether it is still current.</summary>
public sealed class LocalReleaseSnapshot
{
    public LocalReleaseSnapshot(ReleaseIntent intent, ReleasePhase phase, IEnumerable<ReleaseAuditEntry> audit)
    {
        ArgumentNullException.ThrowIfNull(intent); ArgumentNullException.ThrowIfNull(audit);
        var entries = audit.ToArray();
        if (phase is not (ReleasePhase.Requested or ReleasePhase.Active or ReleasePhase.Canceled or ReleasePhase.Failed) ||
            entries.Length != (phase == ReleasePhase.Requested ? 1 : 2) || entries.Any(e => e is null) || entries[0].Sequence != 1 ||
            entries[0].Kind != ReleaseAuditKind.Requested || entries[0].Rejection != ReleaseRejection.None ||
            entries.Any(e => !Enum.IsDefined(e.Kind) || !Enum.IsDefined(e.Rejection)) ||
            entries.Length == 2 && (entries[1].Sequence != 2 || entries[1].RecordedAt < entries[0].RecordedAt ||
                entries[1].Kind != (phase == ReleasePhase.Active ? ReleaseAuditKind.Activated : phase == ReleasePhase.Canceled ? ReleaseAuditKind.Canceled : ReleaseAuditKind.Rejected) ||
                (phase == ReleasePhase.Failed) != (entries[1].Rejection != ReleaseRejection.None)))
            throw new InvalidOperationException("Invalid local release history.");
        Intent = intent; Phase = phase; Audit = Array.AsReadOnly(entries);
    }
    public ReleaseIntent Intent { get; }
    public ReleasePhase Phase { get; }
    public IReadOnlyList<ReleaseAuditEntry> Audit { get; }
    public override string ToString() => nameof(LocalReleaseSnapshot);
}

public interface ILocalReleaseStore
{
    Task<ReleaseWriteOutcome> RegisterAsync(ActorContext actor, LocalReleaseTarget target, CancellationToken token = default);
    Task<bool> DisableAsync(ActorContext actor, ReleaseTargetReference target, CancellationToken token = default);
    Task<LocalReleaseTargetSnapshot?> ReadTargetAsync(ActorContext actor, ReleaseTargetReference target, CancellationToken token = default);
    Task<ReleaseWriteOutcome> RequestAsync(ActorContext actor, ReleaseIntent intent, CancellationToken token = default);
    Task<ReleaseTransitionOutcome> ApplyAsync(ActorContext actor, ReleaseTargetReference target, Guid releaseId, CancellationToken token = default);
    Task<ReleaseTransitionOutcome> CancelAsync(ActorContext actor, ReleaseTargetReference target, Guid releaseId, CancellationToken token = default);
    Task<LocalReleaseSnapshot?> ReadAsync(ActorContext actor, ReleaseTargetReference target, Guid releaseId, CancellationToken token = default);
}
