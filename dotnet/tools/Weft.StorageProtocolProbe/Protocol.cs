using System.Text.Json;
using System.Text.Json.Serialization;
using Weft.Application;
using Weft.Domain;
using Weft.Storage.FileSystem;

namespace Weft.StorageProtocolProbe;

// These test-only commands arrive exclusively on stdin. They are never responses,
// readiness contents, command-line arguments or environment variables.
internal sealed record ProbeCommand
{
    public required string Operation { get; init; }
    public required string RootPath { get; init; }
    public required string ApplicationContentRoot { get; init; }
    public string? RepositoryRoot { get; init; }
    public required string RegistryMode { get; init; }
    public string? ConnectionString { get; init; }
    public ReferenceCommand? Reference { get; init; }
    public OwnerCommand? Owner { get; init; }
    public CleanupCommand? Cleanup { get; init; }
    public string? Base64Content { get; init; }
    public int? PatternLength { get; init; }
    public string? GateDirectory { get; init; }
    public string? GateStage { get; init; }
    public string? GateRecordKind { get; init; }
    public bool DenyFlockAfterOpen { get; init; }
    public bool OfflineAcknowledged { get; init; }
}

internal sealed record ReferenceCommand
{
    public required Guid OrganizationId { get; init; }
    public required Guid ProjectId { get; init; }
    public required Guid MappingId { get; init; }
    public required Guid RevisionId { get; init; }
    public required Guid ObjectId { get; init; }
    public required string ObjectClass { get; init; }
    public required string Sha256 { get; init; }
    public required long ByteCount { get; init; }

    internal PrivateObjectReference Restore()
    {
        var scope = new RevisionScope(OrganizationId, ProjectId, MappingId, RevisionId);
        var kind = ObjectClass switch
        {
            "NativeArtifact" => PrivateObjectClass.NativeArtifact,
            "DerivedArtifact" => PrivateObjectClass.DerivedArtifact,
            _ => throw new InvalidDataException()
        };
        return new(PrivateObjectId.Restore(scope, ObjectId, kind), Protocol.Digest(Sha256), ByteCount);
    }
}

internal sealed record OwnerCommand
{
    public required string Kind { get; init; }
    public required Guid ActorId { get; init; }
    public required Guid ServerInstanceId { get; init; }
    public required Guid LeaseToken { get; init; }
    public required long Generation { get; init; }
    public required DateTimeOffset LeaseExpiresAtUtc { get; init; }
    public ProjectScope? Project { get; init; }
    public Guid? OperationId { get; init; }
    public Guid? CacheEntryId { get; init; }
    public Guid? DerivationId { get; init; }
    public Guid? CandidateObjectId { get; init; }
    public DateTimeOffset? StartedAtUtc { get; init; }
    public Guid? RepairTargetDerivationId { get; init; }
    public string? CanonicalKeyBase64 { get; init; }

    internal PrivatePublicationOwner Restore()
    {
        Protocol.Utc(LeaseExpiresAtUtc);
        var actor = new ActorContext(new PrincipalId(ActorId));
        if (Kind == "ingestion")
        {
            if (Project is null || OperationId is null || CacheEntryId is not null || DerivationId is not null ||
                CandidateObjectId is not null || StartedAtUtc is not null || RepairTargetDerivationId is not null ||
                CanonicalKeyBase64 is not null)
                throw new InvalidDataException();
            return PrivatePublicationOwner.ForIngestion(actor, new IngestionClaim(Project, OperationId.Value,
                actor.PrincipalId, ServerInstanceId, LeaseToken, Generation, LeaseExpiresAtUtc));
        }
        if (Kind != "report" || Project is not null || OperationId is not null || CacheEntryId is null ||
            DerivationId is null || CandidateObjectId is null || StartedAtUtc is null || CanonicalKeyBase64 is null)
            throw new InvalidDataException();
        Protocol.Utc(StartedAtUtc.Value);
        var bytes = Protocol.Base64(CanonicalKeyBase64, V2PublicationRecordCodec.MaximumKeyBytes);
        // The actual private codec reconstitutes the complete typed key and then
        // requires exact canonical bytes, including all policy/source fields.
        var key = V2ReportKeyCodec.Decode(bytes);
        var candidate = PrivateObjectId.Restore(key.Scope, CandidateObjectId.Value, PrivateObjectClass.DerivedArtifact);
        return PrivatePublicationOwner.ForReport(actor, new RevisionReportClaim(CacheEntryId.Value, DerivationId.Value,
            key, actor.PrincipalId, ServerInstanceId, LeaseToken, Generation, StartedAtUtc.Value,
            LeaseExpiresAtUtc, candidate, RepairTargetDerivationId));
    }
}

internal sealed record CleanupCommand
{
    public required Guid RetirementId { get; init; }
    public required string TargetKind { get; init; }
    public Guid? PublicationAttemptId { get; init; }
    public required OriginCommand Origin { get; init; }
    public required string Reason { get; init; }
    public required DateTimeOffset EligibilityOriginAtUtc { get; init; }
    public required DateTimeOffset RetiredAtUtc { get; init; }
    public PublicationCommand? Publication { get; init; }
    public required Guid AttemptId { get; init; }
    public required long Generation { get; init; }
    public required Guid ServerInstanceId { get; init; }
    public required Guid LeaseToken { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public required DateTimeOffset LeaseExpiresAtUtc { get; init; }

    internal ObjectCleanupClaim Restore(PrivateObjectId targetObject)
    {
        Protocol.Utc(EligibilityOriginAtUtc);
        Protocol.Utc(RetiredAtUtc);
        Protocol.Utc(StartedAtUtc);
        Protocol.Utc(LeaseExpiresAtUtc);
        var targetKind = TargetKind switch
        {
            "FinalObject" => CleanupTargetKind.FinalObject,
            "TrackedTemporary" => CleanupTargetKind.TrackedTemporary,
            _ => throw new InvalidDataException()
        };
        var reason = Reason switch
        {
            "TerminalNativeStage" => ObjectCleanupReason.TerminalNativeStage,
            "FailedDerivedCandidate" => ObjectCleanupReason.FailedDerivedCandidate,
            "ExpiredDerivedCandidate" => ObjectCleanupReason.ExpiredDerivedCandidate,
            "AbandonedTrackedTemporary" => ObjectCleanupReason.AbandonedTrackedTemporary,
            _ => throw new InvalidDataException()
        };
        var target = new ObjectCleanupTarget(targetObject, targetKind, PublicationAttemptId);
        var retirement = new ObjectCleanupRetirement(RetirementId, target, Origin.Restore(targetObject.Scope), reason,
            EligibilityOriginAtUtc, RetiredAtUtc, Publication?.Restore(targetObject));
        return new(retirement, AttemptId, Generation, ServerInstanceId, LeaseToken, StartedAtUtc, LeaseExpiresAtUtc);
    }
}

internal sealed record OriginCommand
{
    public required string Kind { get; init; }
    public Guid? OperationId { get; init; }
    public Guid? CacheEntryId { get; init; }
    public Guid? DerivationId { get; init; }
    public long? Generation { get; init; }

    internal ObjectCleanupOrigin Restore(RevisionScope scope) => Kind switch
    {
        "ingestion" when OperationId is { } operation && CacheEntryId is null && DerivationId is null && Generation is null =>
            ObjectCleanupOrigin.ForIngestion(scope, operation),
        "report" when OperationId is null && CacheEntryId is { } cache && DerivationId is { } derivation && Generation is { } generation =>
            ObjectCleanupOrigin.ForReport(scope, cache, derivation, generation),
        _ => throw new InvalidDataException()
    };
}

internal sealed record PublicationCommand
{
    public required Guid PublicationId { get; init; }
    public required Guid RootId { get; init; }
    public required Guid TempId { get; init; }
    public required OwnerCommand Owner { get; init; }
    public required string AbsenceRecordDigest { get; init; }
    public required DateTimeOffset RegisteredAtUtc { get; init; }
    public BindingCommand? Binding { get; init; }

    internal PrivatePublicationRecord Restore(PrivateObjectId targetObject)
    {
        Protocol.Utc(RegisteredAtUtc);
        var reservation = new PrivatePublicationReservation(PublicationId, new(RootId, targetObject, TempId),
            Owner.Restore(), Protocol.Digest(AbsenceRecordDigest), RegisteredAtUtc);
        return new(reservation, Binding?.Restore());
    }
}

internal sealed record BindingCommand
{
    public required PrivatePublicationFileIdentity ActualIdentity { get; init; }
    public required string BoundRecordDigest { get; init; }
    public required DateTimeOffset BoundAtUtc { get; init; }

    internal PrivatePublicationBinding Restore()
    {
        Protocol.Utc(BoundAtUtc);
        return new(ActualIdentity, Protocol.Digest(BoundRecordDigest), BoundAtUtc);
    }
}

internal static class Protocol
{
    internal const int MaximumCommandBytes = 24 * 1024 * 1024;
    internal const int MaximumSourceBytes = 16 * 1024 * 1024;
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };

    internal static Sha256Digest Digest(string value)
    {
        if (value.Length != 64)
            throw new InvalidDataException();
        var result = Sha256Digest.Parse(value);
        if (result.Value != value)
            throw new InvalidDataException();
        return result;
    }

    internal static byte[] Base64(string value, int maximumBytes)
    {
        if (value.Length > ((maximumBytes + 2L) / 3) * 4)
            throw new InvalidDataException();
        var result = Convert.FromBase64String(value);
        if (result.Length > maximumBytes || Convert.ToBase64String(result) != value)
            throw new InvalidDataException();
        return result;
    }

    internal static void Utc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
            throw new InvalidDataException();
    }
}
