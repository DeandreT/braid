using Weft.Domain;

namespace Weft.Application;

public enum PreviewRunState
{
    Queued,
    Running,
    Completed,
    Canceled
}

/// <summary>A terminal run outcome: a preview status or a reason the run finished without execution.</summary>
public enum PreviewRunOutcome
{
    Succeeded,
    InvalidRequest,
    InvalidMapping,
    UnsupportedArtifact,
    UnsupportedCapability,
    MissingInput,
    InvalidInput,
    InvalidParameter,
    ExecutionFailed,
    ResourceLimit,
    ProtocolFailure,
    BackendUnavailable,
    BackendFailure,
    TimedOut,
    Canceled,
    IsolationFailure,
    AuthorityRevoked,
    AttemptsExhausted
}

/// <summary>A caller's preview request for one exact revision; RequestKey makes resubmission idempotent.</summary>
public sealed record PreviewRunSubmission(
    RevisionScope Revision,
    Guid RequestKey,
    PreviewTargetSelection Target,
    PreviewInputDocument Primary,
    IReadOnlyList<PreviewInputDocument> NamedInputs,
    IReadOnlyList<PreviewParameter> Parameters);

public enum PreviewSubmitOutcome
{
    Created,
    Replayed,
    Conflict,
    NotAuthorizedOrNotFound
}

public sealed record PreviewSubmitResult(PreviewSubmitOutcome Outcome, Guid? RunId);

/// <summary>Store input for a validated submission. The fingerprint is derived from the canonical document.</summary>
public sealed record PreviewRunRecord(
    PreviewRunSubmission Submission,
    string RequestDocument,
    string CurrentDateTime,
    int MaxAttempts,
    TimeSpan Retention);

public sealed record PreviewOutputDescriptor(
    int Ordinal,
    string BoundaryId,
    string TargetName,
    bool TargetNameTruncated,
    string MediaType,
    long RecordsWritten,
    Sha256Digest Digest,
    int ByteCount);

/// <summary>The submitter's view of one run; outputs are present only for a succeeded run.</summary>
public sealed record PreviewRunSnapshot(
    Guid RunId,
    RevisionScope Revision,
    Guid RequestKey,
    string RequestFingerprint,
    string CurrentDateTime,
    PreviewRunState State,
    int Attempt,
    int MaxAttempts,
    bool CancelRequested,
    PreviewRunOutcome? Outcome,
    MappingPreviewFailure? Failure,
    PreviewRunProvenance? Provenance,
    long? RecordsWritten,
    IReadOnlyList<PreviewOutputDescriptor> Outputs,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    DateTimeOffset ExpiresAtUtc);

public sealed record PreviewRunProvenance(bool Confirmed, string EngineRevision, string BackendBuild, string AdapterBuild, string Profile);

public enum PreviewClaimOutcome
{
    Claimed,
    Busy,
    Finished,
    Missing
}

/// <summary>An exclusive leased attempt. The token fences every later transition.</summary>
public sealed record PreviewRunClaim(
    Guid OrganizationId,
    Guid RunId,
    int Attempt,
    Guid LeaseToken,
    DateTimeOffset LeaseExpiresAtUtc,
    RevisionScope Revision,
    PrincipalId Submitter,
    string RequestDocument,
    string CurrentDateTime,
    PrivateObjectReference NativeObject);

public sealed record PreviewClaimResult(PreviewClaimOutcome Outcome, PreviewRunClaim? Claim);

public sealed record PreviewStoredInput(int Ordinal, string? Name, Sha256Digest Digest, byte[] Content);

public sealed record PreviewClaimObservation(bool ClaimCurrent, bool CancelRequested);

/// <summary>A terminal transition. Artifacts are published only with a succeeded outcome.</summary>
public sealed record PreviewRunCompletion(
    PreviewRunOutcome Outcome,
    MappingPreviewFailure? Failure,
    PreviewRunProvenance? Provenance,
    long RecordsWritten,
    IReadOnlyList<PreviewOutputArtifact> Artifacts);

public enum PreviewCancelOutcome
{
    Canceled,
    Requested,
    Finished,
    NotAuthorizedOrNotFound
}

public sealed record PreviewOutputContent(Sha256Digest Digest, byte[] Content);

/// <summary>Writer port. Each call is one short transaction; none spans worker or storage I/O.</summary>
public interface IPreviewRunStore
{
    Task<PreviewSubmitResult> SubmitAsync(ActorContext actor, PreviewRunRecord record, CancellationToken cancellationToken = default);
    Task<PreviewClaimResult> ClaimAsync(Guid organizationId, Guid runId, Guid owner, TimeSpan lease, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PreviewStoredInput>> ReadInputsAsync(PreviewRunClaim claim, CancellationToken cancellationToken = default);
    Task<PreviewClaimObservation> ObserveAsync(PreviewRunClaim claim, CancellationToken cancellationToken = default);
    /// <summary>Returns false when the claim was superseded; nothing is published then.</summary>
    Task<bool> CompleteAsync(PreviewRunClaim claim, PreviewRunCompletion completion, CancellationToken cancellationToken = default);
    Task<bool> ReleaseAsync(PreviewRunClaim claim, TimeSpan retryDelay, CancellationToken cancellationToken = default);
    Task<PreviewCancelOutcome> CancelAsync(ActorContext actor, Guid organizationId, Guid runId, CancellationToken cancellationToken = default);
}

/// <summary>Submitter-only reads with fresh project authority; unauthorized and missing are indistinguishable.</summary>
public interface IPreviewRunReader
{
    Task<PreviewRunSnapshot?> ReadAsync(ActorContext actor, Guid organizationId, Guid runId, CancellationToken cancellationToken = default);
    Task<PreviewOutputContent?> ReadOutputAsync(ActorContext actor, Guid organizationId, Guid runId, int ordinal,
        CancellationToken cancellationToken = default);
}

public sealed class PreviewRunOptions
{
    public int MaxAttempts { get; init; } = 3;
    public TimeSpan Lease { get; init; } = TimeSpan.FromSeconds(90);
    public TimeSpan Retention { get; init; } = TimeSpan.FromHours(24);
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan CancellationPollInterval { get; init; } = TimeSpan.FromSeconds(1);
    public PreviewLimits Limits { get; init; } = PreviewLimits.Default;
}

public sealed class PreviewRunException(string message) : Exception(message);
