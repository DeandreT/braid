using System.Text.Json;
using Weft.Domain;

namespace Weft.Deployment;

public enum ReleaseAction { Activate, Rollback }
public enum ReleasePhase { Requested, Applying, Active, Failed, Canceled, Uncertain }

/// <summary>A registered target identity, not a caller-selected URL, credential or deployment path.</summary>
public sealed record ReleaseTargetReference
{
    public ReleaseTargetReference(MappingScope mapping, BuildEnvironmentReference environment, Guid targetId)
    {
        ArgumentNullException.ThrowIfNull(mapping); ArgumentNullException.ThrowIfNull(environment);
        if (targetId == Guid.Empty || mapping.OrganizationId != environment.Project.OrganizationId || mapping.ProjectId != environment.Project.ProjectId)
            throw new ArgumentException("Release target scope mismatch.");
        Mapping = mapping; Environment = environment; TargetId = targetId;
    }
    public MappingScope Mapping { get; }
    public BuildEnvironmentReference Environment { get; }
    public Guid TargetId { get; }
}

/// <summary>One immutable desired effect. A retained intent is not permission to issue or repeat an external effect.</summary>
public sealed class ReleaseIntent
{
    public ReleaseIntent(Guid releaseId, PrincipalId actor, ReleaseTargetReference target, BuildQualification qualification,
        ReleaseAction action, Guid? expectedActiveReleaseId, Guid? rollbackOfReleaseId = null)
    {
        ArgumentNullException.ThrowIfNull(actor); ArgumentNullException.ThrowIfNull(target); ArgumentNullException.ThrowIfNull(qualification);
        if (releaseId == Guid.Empty || !Enum.IsDefined(action) || expectedActiveReleaseId == Guid.Empty || rollbackOfReleaseId == Guid.Empty ||
            expectedActiveReleaseId == releaseId || rollbackOfReleaseId == releaseId ||
            (action == ReleaseAction.Rollback ? rollbackOfReleaseId is null || expectedActiveReleaseId is null : rollbackOfReleaseId is not null))
            throw new ArgumentException("Invalid release selectors.");
        var intent = qualification.Artifact.Intent;
        if (qualification.Actor != actor || intent.Revision.OrganizationId != target.Mapping.OrganizationId ||
            intent.Revision.ProjectId != target.Mapping.ProjectId || intent.Revision.MappingId != target.Mapping.MappingId || intent.Environment != target.Environment)
            throw new ArgumentException("A release must retain its qualified actor, mapping and exact environment version.");
        ReleaseId = releaseId; Actor = actor; Target = target; Qualification = qualification; Action = action;
        ExpectedActiveReleaseId = expectedActiveReleaseId; RollbackOfReleaseId = rollbackOfReleaseId;
        Fingerprint = Sha256Digest.FromBytes(CopyCanonicalBytes());
    }
    public Guid ReleaseId { get; }
    public PrincipalId Actor { get; }
    public ReleaseTargetReference Target { get; }
    public BuildQualification Qualification { get; }
    public ReleaseAction Action { get; }
    public Guid? ExpectedActiveReleaseId { get; }
    public Guid? RollbackOfReleaseId { get; }
    public Sha256Digest Fingerprint { get; }

    public byte[] CopyCanonicalBytes()
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteString("schema", "weft.release-intent.v1");
            json.WriteString("releaseId", ReleaseId.ToString("D"));
            json.WriteString("actorId", Actor.Value.ToString("D"));
            json.WriteString("organizationId", Target.Mapping.OrganizationId.ToString("D"));
            json.WriteString("projectId", Target.Mapping.ProjectId.ToString("D"));
            json.WriteString("mappingId", Target.Mapping.MappingId.ToString("D"));
            json.WriteString("targetId", Target.TargetId.ToString("D"));
            json.WriteString("environmentId", Target.Environment.EnvironmentId.ToString("D"));
            json.WriteNumber("environmentVersion", Target.Environment.Version);
            json.WriteString("qualificationId", Qualification.Id.ToString("D"));
            json.WriteString("qualificationDigest", Qualification.Fingerprint.Value);
            json.WriteString("buildId", Qualification.Artifact.Intent.BuildId.ToString("D"));
            json.WriteString("buildIntentDigest", Qualification.Artifact.Intent.Fingerprint.Value);
            json.WriteString("artifactDigest", Qualification.Artifact.ContentDigest.Value);
            json.WriteString("runtimeImage", Qualification.RuntimeImage);
            json.WriteString("action", Action == ReleaseAction.Activate ? "activate" : "rollback");
            json.WriteString("expectedActiveReleaseId", ExpectedActiveReleaseId?.ToString("D"));
            json.WriteString("rollbackOfReleaseId", RollbackOfReleaseId?.ToString("D"));
            json.WriteEndObject();
        }
        return stream.ToArray();
    }
    public override string ToString() => nameof(ReleaseIntent);
}
