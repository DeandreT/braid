using System.Text;
using Weft.Domain;

namespace Weft.Application;

/// <summary>Accepted display metadata only; native facts and trusted execution configuration are server owned.</summary>
public sealed record CreateRevisionRequest
{
    public CreateRevisionRequest(IngestionCreateMetadata metadata, string backendId, string? originalFilename = null)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        Metadata = metadata;
        BackendId = RevisionIngestionGuard.RequireBackendId(backendId);
        OriginalFilename = RevisionIngestionGuard.RequireFilename(originalFilename);
    }

    public IngestionCreateMetadata Metadata { get; }
    public string BackendId { get; }
    public string OriginalFilename { get; }
}

/// <summary>Append cannot edit mapping metadata, ownership, or its persisted backend.</summary>
public sealed record AppendRevisionRequest
{
    public AppendRevisionRequest(HeadCondition? headCondition = null, string? originalFilename = null)
    {
        headCondition ??= new AnyHead();
        if (headCondition is not (AnyHead or EqualHead))
            throw new ArgumentException("The head condition must be known.", nameof(headCondition));
        HeadCondition = headCondition;
        OriginalFilename = RevisionIngestionGuard.RequireFilename(originalFilename);
    }

    public HeadCondition HeadCondition { get; }
    public string OriginalFilename { get; }
}

public enum RevisionIngestionOutcome
{
    CommittedValid = 1,
    CommittedDecodedInvalid = 2,
    InProgress = 3,
    NeedsSource = 4,
    RetryableFailure = 5,
    Rejected = 6,
    HeadConflict = 7,
    AttemptExhausted = 8,
    IdempotencyConflict = 9,
    ClaimLost = 10,
    NotAuthorizedOrNotFound = 11
}

public enum RevisionIngestionRejection
{
    EmptyNative = 1,
    NativeLimit = 2,
    UnregisteredBackend = 3,
    NativeDecode = 4,
    UnsupportedArtifact = 5,
    ResourceLimit = 6
}

/// <summary>Output-only owned projection; private storage, lease, worker profile, and evidence facts are omitted.</summary>
public sealed record RevisionIngestionReceipt
{
    private RevisionIngestionReceipt(IngestionReceipt receipt)
    {
        OperationId = receipt.Facts.OperationId;
        Project = receipt.Facts.Project;
        FutureMapping = new MappingScope(receipt.Facts.FutureRevision.OrganizationId,
            receipt.Facts.FutureRevision.ProjectId, receipt.Facts.FutureRevision.MappingId);
        FutureRevision = receipt.Facts.FutureRevision;
        Phase = receipt.Phase;
        ClaimCount = receipt.ClaimCount;
        ReservedAtUtc = receipt.Facts.ReservedAtUtc;
        CompletedAtUtc = receipt.CompletedAtUtc;
        OriginalFilename = receipt.Facts.Intent.OriginalFilename;
        CommittedSequence = receipt.CommittedRevision?.Sequence;
        CommittedAdmission = receipt.CommittedRevision?.InitialAdmission;
        LatestFailure = LatestFailureFor(receipt.Attempts.LastOrDefault(), receipt.Facts);
        RejectionReason = receipt.Phase == IngestionPhase.Rejected
            ? receipt.Attempts[^1].Classification switch
            {
                IngestionAdmissionClassification.RejectedNativeDecode => RevisionIngestionRejection.NativeDecode,
                IngestionAdmissionClassification.RejectedUnsupported => RevisionIngestionRejection.UnsupportedArtifact,
                IngestionAdmissionClassification.RejectedResourceLimit => RevisionIngestionRejection.ResourceLimit,
                _ => throw new ArgumentException("A rejected receipt requires an actual closed admission rejection.", nameof(receipt))
            }
            : null;
    }

    public Guid OperationId { get; }
    public ProjectScope Project { get; }
    public MappingScope FutureMapping { get; }
    public RevisionScope FutureRevision { get; }
    public IngestionPhase Phase { get; }
    public int ClaimCount { get; }
    public DateTimeOffset ReservedAtUtc { get; }
    public DateTimeOffset? CompletedAtUtc { get; }
    public string OriginalFilename { get; }
    public long? CommittedSequence { get; }
    public IngestionAdmissionClassification? CommittedAdmission { get; }
    public IngestionFailureCode? LatestFailure { get; }
    internal RevisionIngestionRejection? RejectionReason { get; }

    public static RevisionIngestionReceipt FromMetadata(IngestionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return new(receipt);
    }

    private static IngestionFailureCode? LatestFailureFor(IngestionAttemptReceipt? attempt, ReservedIngestionFacts reserved)
    {
        if (attempt?.Completion is not { } completion)
            return null;
        if (completion.Failure is { } failure)
            return failure;
        if (attempt.Classification != IngestionAdmissionClassification.RetryableFailure)
            return null;
        if (completion.Evidence is { } evidence &&
            (!IngestionAttemptCompletion.Matches(evidence.Facts.Provenance, reserved) ||
                evidence.HasDiagnosticCode("ferrule.digestMismatch") ||
                evidence.Facts.IsValid is not null && evidence.Facts.Status is not
                    (IngestionBackendStatus.Succeeded or IngestionBackendStatus.InvalidMapping)))
            return IngestionFailureCode.ProtocolFailure;
        return completion.Evidence?.Facts.Status switch
        {
            IngestionBackendStatus.BackendUnavailable => IngestionFailureCode.BackendUnavailable,
            IngestionBackendStatus.BackendFailure => IngestionFailureCode.BackendFailure,
            IngestionBackendStatus.TimedOut => IngestionFailureCode.TimedOut,
            IngestionBackendStatus.Canceled => IngestionFailureCode.Canceled,
            IngestionBackendStatus.IsolationFailure => IngestionFailureCode.IsolationFailure,
            _ => IngestionFailureCode.ProtocolFailure
        };
    }
}

public sealed record RevisionIngestionResult
{
    private RevisionIngestionResult(RevisionIngestionOutcome outcome, RevisionIngestionReceipt? receipt,
        RevisionIngestionRejection? rejection, IngestionFailureCode? failure)
    {
        Outcome = outcome;
        Receipt = receipt;
        Rejection = rejection;
        Failure = failure;
    }

    public RevisionIngestionOutcome Outcome { get; }
    public RevisionIngestionReceipt? Receipt { get; }
    public RevisionIngestionRejection? Rejection { get; }
    public IngestionFailureCode? Failure { get; }

    public static RevisionIngestionResult ForReceipt(RevisionIngestionOutcome outcome, RevisionIngestionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var pending = receipt.Phase is IngestionPhase.Reserved or IngestionPhase.Staged or IngestionPhase.Ready;
        var agrees = outcome switch
        {
            RevisionIngestionOutcome.CommittedValid => receipt.Phase == IngestionPhase.Committed &&
                receipt.CommittedAdmission == IngestionAdmissionClassification.EligibleValid,
            RevisionIngestionOutcome.CommittedDecodedInvalid => receipt.Phase == IngestionPhase.Committed &&
                receipt.CommittedAdmission == IngestionAdmissionClassification.EligibleDecodedInvalid,
            RevisionIngestionOutcome.InProgress => pending && receipt.LatestFailure is null,
            RevisionIngestionOutcome.NeedsSource => pending && receipt.LatestFailure == IngestionFailureCode.NeedsSource,
            RevisionIngestionOutcome.RetryableFailure => pending && receipt.LatestFailure is not null &&
                receipt.LatestFailure is not (IngestionFailureCode.NeedsSource or IngestionFailureCode.EvidenceLimit),
            RevisionIngestionOutcome.Rejected => receipt.Phase == IngestionPhase.Rejected && receipt.RejectionReason is not null,
            RevisionIngestionOutcome.HeadConflict => receipt.Phase == IngestionPhase.HeadConflict,
            RevisionIngestionOutcome.AttemptExhausted => receipt.Phase == IngestionPhase.AttemptExhausted,
            _ => false
        };
        if (!agrees)
            throw new ArgumentOutOfRangeException(nameof(outcome), "The outcome must agree with the actual receipt.");
        return new(outcome, receipt, outcome == RevisionIngestionOutcome.Rejected ? receipt.RejectionReason : null,
            receipt.LatestFailure);
    }

    public static RevisionIngestionResult RejectedBeforeReservation(RevisionIngestionRejection rejection)
    {
        if (rejection is not (RevisionIngestionRejection.EmptyNative or RevisionIngestionRejection.NativeLimit or
            RevisionIngestionRejection.UnregisteredBackend))
            throw new ArgumentOutOfRangeException(nameof(rejection), "Only request admission can reject before reservation.");
        return new(RevisionIngestionOutcome.Rejected, null, rejection, null);
    }

    public static RevisionIngestionResult WithoutReceipt(RevisionIngestionOutcome outcome)
    {
        if (outcome is not (RevisionIngestionOutcome.IdempotencyConflict or RevisionIngestionOutcome.ClaimLost or
            RevisionIngestionOutcome.NotAuthorizedOrNotFound))
            throw new ArgumentOutOfRangeException(nameof(outcome), "This outcome requires actual receipt or admission facts.");
        return new(outcome, null, null, null);
    }
}

/// <summary>Trusted internal ingestion API; inputs contain no client evidence, profile, reference, or lease.</summary>
public interface IRevisionIngestionService
{
    /// <summary>Consumes a caller-owned stream from its current position after fresh Publisher admission.</summary>
    Task<RevisionIngestionResult> CreateAsync(ActorContext actor, ProjectScope project, Guid idempotencyKey,
        CreateRevisionRequest request, Stream nativeSource, CancellationToken cancellationToken = default);

    /// <summary>Uses the target's persisted backend and consumes caller-owned bytes after fresh admission.</summary>
    Task<RevisionIngestionResult> AppendAsync(ActorContext actor, MappingScope target, Guid idempotencyKey,
        AppendRevisionRequest request, Stream nativeSource, CancellationToken cancellationToken = default);

    /// <summary>Resumes one original-actor operation with fresh authority; no automatic claim loop or source repair.</summary>
    Task<RevisionIngestionResult> ResumeAsync(ActorContext actor, ProjectScope project, Guid operationId,
        CancellationToken cancellationToken = default);

    /// <summary>Returns null for absence, other original actors, or lost Publisher authority.</summary>
    Task<RevisionIngestionReceipt?> GetOwnReceiptAsync(ActorContext actor, ProjectScope project, Guid operationId,
        CancellationToken cancellationToken = default);
}

public sealed record IngestionAppendTarget
{
    public IngestionAppendTarget(MappingScope scope, string backendId)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Scope = scope;
        BackendId = RevisionIngestionGuard.RequireBackendId(backendId);
    }

    public MappingScope Scope { get; }
    public string BackendId { get; }
}

/// <summary>Fresh scoped reads on the ingestion pool, released before source, storage, or worker awaits.</summary>
public interface IIngestionWorkflowReader
{
    /// <summary>Looks up an original-actor key. Create has no target; append requires exact project ancestry.</summary>
    Task<IngestionReceipt?> FindOwnReceiptAsync(ActorContext actor, ProjectScope project,
        IngestionOperationKind kind, MappingScope? target, Guid idempotencyKey,
        CancellationToken cancellationToken = default);

    /// <summary>Returns null for a missing/denied target; only immutable scope and backend are returned.</summary>
    Task<IngestionAppendTarget?> GetAppendTargetAsync(ActorContext actor, MappingScope target,
        CancellationToken cancellationToken = default);
}

internal static class RevisionIngestionGuard
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static string RequireBackendId(string backendId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backendId);
        RequireUtf8(backendId, 128, nameof(backendId));
        return backendId;
    }

    internal static string RequireFilename(string? originalFilename)
    {
        var filename = originalFilename ?? IngestionIntent.EntryDocument;
        if (filename.Length == 0 || filename is "." or ".." || filename.Any(character =>
            character is '/' or '\\' || char.IsControl(character)))
            throw new ArgumentException("The display filename must be a safe basename.", nameof(originalFilename));
        RequireUtf8(filename, 255, nameof(originalFilename));
        return filename;
    }

    internal static void RequireNativeLimit(long nativeByteLimit)
    {
        if (nativeByteLimit is < 1 or > IngestionAdmissionPolicy.MaximumNativeByteCount)
            throw new ArgumentOutOfRangeException(nameof(nativeByteLimit), "Native limits must be positive reductions of the admission ceiling.");
    }

    private static void RequireUtf8(string value, int maximumBytes, string parameterName)
    {
        if (value.Contains('\0'))
            throw new ArgumentException("Persisted ingestion text cannot contain NUL.", parameterName);
        try
        {
            if (StrictUtf8.GetByteCount(value) > maximumBytes)
                throw new ArgumentException("Ingestion text exceeds its UTF8 byte limit.", parameterName);
        }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException("Ingestion text must contain valid Unicode.", parameterName);
        }
    }
}
