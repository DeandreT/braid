using Braid.Domain;

namespace Braid.Application;

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

/// <summary>A caller's preview request for one immutable subject; RequestKey makes resubmission idempotent.</summary>
public sealed record PreviewRunSubmission(
    PreviewSubject Subject,
    Guid RequestKey,
    PreviewTargetSelection Target,
    PreviewInputDocument Primary,
    IReadOnlyList<PreviewInputDocument> NamedInputs,
    IReadOnlyList<PreviewParameter> Parameters)
{
    public PreviewExecutionProfile Profile { get; init; } = PreviewExecutionProfile.LegacyJson;
}

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
    int ByteCount)
{
    public CsvDocumentDialect? CsvDialect { get; init; }
    public XmlDocumentMetadata? XmlMetadata { get; init; }
}

/// <summary>The submitter's view of one run; outputs are present only for a succeeded run.</summary>
public sealed record PreviewRunSnapshot(
    Guid RunId,
    PreviewSubject Subject,
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
    PreviewSubject Subject,
    PrincipalId Submitter,
    string RequestDocument,
    string CurrentDateTime,
    PreviewNativeSource Native);

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

public sealed record PreviewOutputContent(Sha256Digest Digest, byte[] Content)
{
    public string MediaType { get; init; } = PreviewPolicy.MediaType;
    public CsvDocumentDialect? CsvDialect { get; init; }
    public XmlDocumentMetadata? XmlMetadata { get; init; }
}

/// <summary>Writer port. Each call is one short transaction; none spans worker or storage I/O.</summary>
public interface IPreviewRunStore
{
    /// <summary>
    /// Proves the trusted current preview persistence contract before submission preflight can read
    /// private native bytes. This performs no publication, queuing or authority grant; submission
    /// must still recheck its schema and the actor's authority in its own transaction.
    /// </summary>
    Task RequireSubmissionAdmissionAsync(CancellationToken cancellationToken = default);
    Task<PreviewSubmitResult> SubmitAsync(ActorContext actor, PreviewRunRecord record, CancellationToken cancellationToken = default);
    Task<PreviewClaimResult> ClaimAsync(Guid organizationId, Guid runId, Guid owner, TimeSpan lease, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PreviewStoredInput>> ReadInputsAsync(PreviewRunClaim claim, CancellationToken cancellationToken = default);
    Task<PreviewClaimObservation> ObserveAsync(PreviewRunClaim claim, CancellationToken cancellationToken = default);
    /// <summary>Returns false when the claim was superseded; nothing is published then.</summary>
    Task<bool> CompleteAsync(PreviewRunClaim claim, PreviewRunCompletion completion, CancellationToken cancellationToken = default);
    Task<bool> ReleaseAsync(PreviewRunClaim claim, TimeSpan retryDelay, CancellationToken cancellationToken = default);
    Task<PreviewCancelOutcome> CancelAsync(ActorContext actor, Guid organizationId, Guid runId, CancellationToken cancellationToken = default);
}

public sealed record PreviewRunSummary(Guid RunId, RevisionScope Revision, PreviewRunState State, PreviewRunOutcome? Outcome,
    DateTimeOffset CreatedAtUtc, DateTimeOffset? FinishedAtUtc, DateTimeOffset ExpiresAtUtc);

/// <summary>Newest first; NextRunId continues the same mapping and submitter's history.</summary>
public sealed record PreviewRunPage(IReadOnlyList<PreviewRunSummary> Runs, Guid? NextRunId);

/// <summary>A submitted input: ordinal 0 is the primary document, later ordinals are named sources.</summary>
public sealed record PreviewInputDescriptor(int Ordinal, string? Name, Sha256Digest Digest, int ByteCount);

/// <summary>Submitter-only reads with fresh project authority; unauthorized and missing are indistinguishable.</summary>
public interface IPreviewRunReader
{
    Task<DraftPreviewSubject?> ReadDraftSubjectAsync(ActorContext actor, MappingScope mapping, Guid requestKey,
        CancellationToken cancellationToken = default) => Task.FromResult<DraftPreviewSubject?>(null);
    Task<PreviewRunSnapshot?> ReadAsync(ActorContext actor, Guid organizationId, Guid runId, CancellationToken cancellationToken = default);
    Task<PreviewOutputContent?> ReadOutputAsync(ActorContext actor, Guid organizationId, Guid runId, int ordinal,
        CancellationToken cancellationToken = default);
    /// <summary>The caller's own runs of one mapping. A cursor outside the same listing yields an empty page.</summary>
    Task<PreviewRunPage> ListAsync(ActorContext actor, MappingScope mapping, int limit, Guid? afterRunId = null,
        CancellationToken cancellationToken = default);
    /// <summary>Null when the run is not the caller's visible run; every run has at least one input.</summary>
    Task<IReadOnlyList<PreviewInputDescriptor>?> ReadInputDescriptorsAsync(ActorContext actor, Guid organizationId, Guid runId,
        CancellationToken cancellationToken = default);
    Task<PreviewOutputContent?> ReadInputAsync(ActorContext actor, Guid organizationId, Guid runId, int ordinal,
        CancellationToken cancellationToken = default);
}

public sealed class PreviewRunOptions
{
    public bool XmlEnabled { get; init; }
    public int MaxAttempts { get; init; } = 3;
    public TimeSpan Lease { get; init; } = TimeSpan.FromSeconds(90);
    public TimeSpan Retention { get; init; } = TimeSpan.FromHours(24);
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan CancellationPollInterval { get; init; } = TimeSpan.FromSeconds(1);
    public PreviewLimits Limits { get; init; } = PreviewLimits.Default;
}

public sealed class PreviewRunException(string message) : Exception(message);
