using Braid.Domain;

namespace Braid.Application;

public enum RevisionReportBackendStatus
{
    Succeeded = 1,
    InvalidMapping = 2,
    UnsupportedArtifact = 3,
    ResourceLimit = 4,
    ProtocolFailure = 5,
    BackendUnavailable = 6,
    BackendFailure = 7,
    TimedOut = 8,
    Canceled = 9,
    IsolationFailure = 10
}

public enum RevisionReportCoverage { Complete = 1, Partial = 2, Unavailable = 3 }

public enum RevisionReportFailureCategory
{
    ObjectUnavailable = 1,
    StorageIntegrity = 2,
    StorageIo = 3,
    BackendUnavailable = 4,
    BackendFailure = 5,
    TimedOut = 6,
    Canceled = 7,
    IsolationFailure = 8,
    ProtocolFailure = 9,
    ProfileUnavailable = 10,
    ResourceLimit = 11,
    UnsupportedArtifact = 12
}

public enum RevisionReportRepairReason { Missing = 1, Integrity = 2 }
public enum RevisionReportState { NotDerived = 1, Pending = 2, Available = 3, Failed = 4 }

public enum RevisionReportStoreOutcome
{
    Claimed = 1,
    Available = 2,
    Pending = 3,
    RetryDelayed = 4,
    Applied = 5,
    ClaimLost = 6,
    SelectionChanged = 7,
    GenerationExhausted = 8,
    NotAuthorizedOrNotFound = 9
}

/// <summary>One database-clock authority observation, never a cached permission grant.</summary>
public sealed record AuthorizedRevisionReportSource
{
    public AuthorizedRevisionReportSource(RevisionReportSource source, PrincipalId actor, DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(actor);
        Source = source;
        Actor = actor;
        ObservedAtUtc = ReportMetadataValue.Utc(observedAtUtc, nameof(observedAtUtc));
    }

    public RevisionReportSource Source { get; }
    public PrincipalId Actor { get; }
    public DateTimeOffset ObservedAtUtc { get; }
}

/// <summary>Actual backend observations; object identity and entry facts belong to the authorized key.</summary>
public sealed record RevisionReportProvenance
{
    public RevisionReportProvenance(RevisionScope scope, Sha256Digest nativeDigest,
        RevisionReportBackendProfile profile, bool confirmed)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(nativeDigest);
        ArgumentNullException.ThrowIfNull(profile);
        Scope = scope;
        NativeDigest = nativeDigest;
        Profile = profile;
        Confirmed = confirmed;
    }

    public RevisionScope Scope { get; }
    public Sha256Digest NativeDigest { get; }
    public RevisionReportBackendProfile Profile { get; }
    public bool Confirmed { get; }
}

public sealed record RevisionReportProjectionSummary
{
    public RevisionReportProjectionSummary(RevisionReportCoverage? coverage, int documentItemCount,
        int documentDiagnosticCount, int coverageGapCount, int operationDiagnosticCount)
    {
        if (coverage is { } value)
            ReportMetadataValue.Known(value, nameof(coverage));
        ReportMetadataValue.Count(documentItemCount, RevisionReportPolicy.MaximumItemLimit, nameof(documentItemCount));
        ReportMetadataValue.Count(documentDiagnosticCount, RevisionReportPolicy.MaximumDiagnosticLimit, nameof(documentDiagnosticCount));
        ReportMetadataValue.Count(coverageGapCount, RevisionReportPolicy.MaximumDiagnosticLimit, nameof(coverageGapCount));
        ReportMetadataValue.Count(operationDiagnosticCount, RevisionReportPolicy.MaximumDiagnosticLimit, nameof(operationDiagnosticCount));
        if (coverage is null && (documentItemCount != 0 || documentDiagnosticCount != 0 || coverageGapCount != 0))
            throw new ArgumentException("An absent projection document cannot have document counts.", nameof(coverage));
        if (documentDiagnosticCount + coverageGapCount > documentItemCount)
            throw new ArgumentException("Document diagnostics and gaps are included in its item count.", nameof(documentItemCount));
        if (coverage == RevisionReportCoverage.Complete && coverageGapCount != 0 ||
            coverage is RevisionReportCoverage.Partial or RevisionReportCoverage.Unavailable && coverageGapCount == 0)
            throw new ArgumentException("Projection coverage and gaps must agree.", nameof(coverageGapCount));

        Coverage = coverage;
        DocumentItemCount = documentItemCount;
        DocumentDiagnosticCount = documentDiagnosticCount;
        CoverageGapCount = coverageGapCount;
        OperationDiagnosticCount = operationDiagnosticCount;
    }

    public RevisionReportCoverage? Coverage { get; }
    /// <summary>Total top-level document array entries, including diagnostics and coverage gaps.</summary>
    public int DocumentItemCount { get; }
    public int DocumentDiagnosticCount { get; }
    public int CoverageGapCount { get; }
    public int OperationDiagnosticCount { get; }
}

public sealed record RevisionReportValidationSummary
{
    public RevisionReportValidationSummary(bool? isValid, int totalIssueCount, int emittedIssueCount,
        bool truncated, int diagnosticCount)
    {
        ReportMetadataValue.Count(totalIssueCount, RevisionReportPolicy.MaximumValidationIssueLimit, nameof(totalIssueCount));
        ReportMetadataValue.Count(emittedIssueCount, Math.Min(totalIssueCount, RevisionReportPolicy.MaximumDiagnosticLimit), nameof(emittedIssueCount));
        ReportMetadataValue.Count(diagnosticCount, RevisionReportPolicy.MaximumDiagnosticLimit, nameof(diagnosticCount));
        if (isValid is not null && diagnosticCount != emittedIssueCount)
            throw new ArgumentException("An observed validity result must count its emitted diagnostics.", nameof(diagnosticCount));

        IsValid = isValid;
        TotalIssueCount = totalIssueCount;
        EmittedIssueCount = emittedIssueCount;
        Truncated = truncated;
        DiagnosticCount = diagnosticCount;
    }

    public bool? IsValid { get; }
    public int TotalIssueCount { get; }
    public int EmittedIssueCount { get; }
    public bool Truncated { get; }
    public int DiagnosticCount { get; }
}

/// <summary>Bounded actual observations. Absence is distinct from an explicitly present empty observation.</summary>
public sealed record RevisionReportObservation
{
    public RevisionReportObservation(RevisionReportOperation operation, RevisionReportBackendStatus? status,
        RevisionReportProvenance? provenance, RevisionReportProjectionSummary? projection,
        RevisionReportValidationSummary? validation)
    {
        ReportMetadataValue.Known(operation, nameof(operation));
        if (status is { } value)
            ReportMetadataValue.Known(value, nameof(status));
        if (operation == RevisionReportOperation.Projection && validation is not null ||
            operation == RevisionReportOperation.Validation && projection is not null)
            throw new ArgumentException("A report observation cannot contain another operation's summary.");

        Operation = operation;
        Status = status;
        Provenance = provenance;
        Projection = projection;
        Validation = validation;
    }

    public RevisionReportOperation Operation { get; }
    public RevisionReportBackendStatus? Status { get; }
    public RevisionReportProvenance? Provenance { get; }
    public RevisionReportProjectionSummary? Projection { get; }
    public RevisionReportValidationSummary? Validation { get; }
}

/// <summary>Trusted typed publication facts only; no backend execution or object verification is performed here.</summary>
public sealed record RevisionReportCompletion
{
    private RevisionReportCompletion(RevisionReportKey? key, RevisionReportFailureCategory? failure,
        RevisionReportObservation? observation, PrivateObjectReference? derivedObject)
    {
        Key = key;
        Failure = failure;
        Observation = observation;
        DerivedObject = derivedObject;
    }

    public RevisionReportKey? Key { get; }
    public bool IsUsable => Key is not null;
    public RevisionReportFailureCategory? Failure { get; }
    public RevisionReportObservation? Observation { get; }
    public PrivateObjectReference? DerivedObject { get; }

    public static RevisionReportCompletion ForUsable(RevisionReportKey key, RevisionReportObservation observation,
        PrivateObjectReference derivedObject)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(derivedObject);
        if (observation.Operation != key.Operation || observation.Provenance is not { Confirmed: true } provenance ||
            provenance.Scope != key.Scope || provenance.NativeDigest != key.NativeDigest || provenance.Profile != key.Profile)
            throw new ArgumentException("A usable report requires confirmed matching provenance.", nameof(observation));

        var eligible = key.Operation switch
        {
            RevisionReportOperation.Projection => observation.Status == RevisionReportBackendStatus.Succeeded &&
                observation.Projection?.Coverage is not null,
            RevisionReportOperation.Validation =>
                observation.Status == RevisionReportBackendStatus.Succeeded && observation.Validation?.IsValid == true ||
                observation.Status == RevisionReportBackendStatus.InvalidMapping && observation.Validation?.IsValid == false,
            _ => false
        };
        if (!eligible)
            throw new ArgumentException("A report requires an actual eligible operation result.", nameof(observation));
        if (key.Source.NativeObject.ByteCount > key.Policy.NativeByteLimit)
            throw new ArgumentException("The native source exceeds the effective report policy.", nameof(key));
        ReportMetadataValue.Derived(derivedObject, key, nameof(derivedObject));
        if (observation.Projection is { } projection)
        {
            ReportMetadataValue.Count(projection.DocumentItemCount, key.Policy.ItemLimit, nameof(observation));
            ReportMetadataValue.Count(projection.DocumentDiagnosticCount, key.Policy.DiagnosticLimit, nameof(observation));
            ReportMetadataValue.Count(projection.CoverageGapCount, key.Policy.DiagnosticLimit, nameof(observation));
            ReportMetadataValue.Count(projection.OperationDiagnosticCount, key.Policy.DiagnosticLimit, nameof(observation));
        }
        if (observation.Validation is { } validation)
        {
            ReportMetadataValue.Count(validation.TotalIssueCount, key.Policy.ValidationIssueLimit, nameof(observation));
            ReportMetadataValue.Count(validation.DiagnosticCount, key.Policy.DiagnosticLimit, nameof(observation));
        }
        return new(key, null, observation, derivedObject);
    }

    public static RevisionReportCompletion ForFailure(RevisionReportFailureCategory failure,
        RevisionReportObservation? observation = null)
    {
        ReportMetadataValue.Known(failure, nameof(failure));
        return new(null, failure, observation, null);
    }
}

/// <summary>Database-issued fenced intent. It carries no permission grant and is never bound from HTTP.</summary>
public sealed record RevisionReportClaim
{
    public RevisionReportClaim(Guid cacheEntryId, Guid derivationId, RevisionReportKey key,
        PrincipalId originalActor, Guid serverInstanceId, Guid leaseToken, long generation,
        DateTimeOffset startedAtUtc, DateTimeOffset leaseExpiresAtUtc, PrivateObjectId candidateObjectId,
        Guid? repairTargetDerivationId = null)
    {
        CacheEntryId = ReportMetadataValue.Id(cacheEntryId, nameof(cacheEntryId));
        DerivationId = ReportMetadataValue.Id(derivationId, nameof(derivationId));
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(originalActor);
        ArgumentNullException.ThrowIfNull(candidateObjectId);
        ServerInstanceId = ReportMetadataValue.Id(serverInstanceId, nameof(serverInstanceId));
        LeaseToken = ReportMetadataValue.Id(leaseToken, nameof(leaseToken));
        Generation = ReportMetadataValue.Generation(generation);
        ReportMetadataValue.Lease(startedAtUtc, leaseExpiresAtUtc);
        if (candidateObjectId.Class != PrivateObjectClass.DerivedArtifact || candidateObjectId.Scope != key.Scope)
            throw new ArgumentException("The candidate object must be derived in the exact key scope.", nameof(candidateObjectId));
        ReportMetadataValue.RepairId(repairTargetDerivationId, derivationId);

        Key = key;
        OriginalActor = originalActor;
        StartedAtUtc = startedAtUtc;
        LeaseExpiresAtUtc = leaseExpiresAtUtc;
        CandidateObjectId = candidateObjectId;
        RepairTargetDerivationId = repairTargetDerivationId;
    }

    public Guid CacheEntryId { get; }
    public Guid DerivationId { get; }
    public RevisionReportKey Key { get; }
    public PrincipalId OriginalActor { get; }
    public Guid ServerInstanceId { get; }
    public Guid LeaseToken { get; }
    public long Generation { get; }
    public DateTimeOffset StartedAtUtc { get; }
    public DateTimeOffset LeaseExpiresAtUtc { get; }
    public PrivateObjectId CandidateObjectId { get; }
    public Guid? RepairTargetDerivationId { get; }
}

public sealed record RevisionReportAttemptSummary
{
    public RevisionReportAttemptSummary(Guid derivationId, long generation, DateTimeOffset startedAtUtc,
        DateTimeOffset leaseExpiresAtUtc, DateTimeOffset? finishedAtUtc, RevisionReportCompletion? completion,
        Guid? repairTargetDerivationId = null)
    {
        DerivationId = ReportMetadataValue.Id(derivationId, nameof(derivationId));
        Generation = ReportMetadataValue.Generation(generation);
        ReportMetadataValue.Lease(startedAtUtc, leaseExpiresAtUtc);
        if ((finishedAtUtc is null) != (completion is null))
            throw new ArgumentException("A terminal attempt requires both finish time and completion.");
        if (finishedAtUtc is { } finished)
            ReportMetadataValue.Finish(startedAtUtc, finished);
        ReportMetadataValue.RepairId(repairTargetDerivationId, derivationId);
        StartedAtUtc = startedAtUtc;
        LeaseExpiresAtUtc = leaseExpiresAtUtc;
        FinishedAtUtc = finishedAtUtc;
        Completion = completion;
        RepairTargetDerivationId = repairTargetDerivationId;
    }

    public Guid DerivationId { get; }
    public long Generation { get; }
    public DateTimeOffset StartedAtUtc { get; }
    public DateTimeOffset LeaseExpiresAtUtc { get; }
    public DateTimeOffset? FinishedAtUtc { get; }
    public RevisionReportCompletion? Completion { get; }
    public Guid? RepairTargetDerivationId { get; }
}

/// <summary>An immutable successful attempt/result. Cache selection never changes this identity's meaning.</summary>
public sealed record RevisionReportResult
{
    public RevisionReportResult(Guid derivationId, Guid cacheEntryId, RevisionReportKey key, long generation,
        PrincipalId originalActor, DateTimeOffset startedAtUtc, DateTimeOffset finishedAtUtc,
        PrivateObjectReference derivedObject, RevisionReportObservation observation)
    {
        DerivationId = ReportMetadataValue.Id(derivationId, nameof(derivationId));
        CacheEntryId = ReportMetadataValue.Id(cacheEntryId, nameof(cacheEntryId));
        ArgumentNullException.ThrowIfNull(originalActor);
        _ = RevisionReportCompletion.ForUsable(key, observation, derivedObject);
        Generation = ReportMetadataValue.Generation(generation);
        ReportMetadataValue.Finish(startedAtUtc, finishedAtUtc);
        Key = key;
        OriginalActor = originalActor;
        StartedAtUtc = startedAtUtc;
        FinishedAtUtc = finishedAtUtc;
        DerivedObject = derivedObject;
        Observation = observation;
    }

    public Guid DerivationId { get; }
    public Guid CacheEntryId { get; }
    public RevisionReportKey Key { get; }
    public long Generation { get; }
    public PrincipalId OriginalActor { get; }
    public DateTimeOffset StartedAtUtc { get; }
    public DateTimeOffset FinishedAtUtc { get; }
    public PrivateObjectReference DerivedObject { get; }
    public RevisionReportObservation Observation { get; }
}

/// <summary>Trusted exact-attempt lookup, including failures for ambiguous-commit resolution.</summary>
public sealed record AuthorizedRevisionReportAttempt
{
    public AuthorizedRevisionReportAttempt(AuthorizedRevisionReportSource source, RevisionReportKey key,
        Guid cacheEntryId, RevisionReportAttemptSummary attempt, RevisionReportResult? result)
    {
        ReportMetadataValue.Source(source, key);
        CacheEntryId = ReportMetadataValue.Id(cacheEntryId, nameof(cacheEntryId));
        ArgumentNullException.ThrowIfNull(attempt);
        ReportMetadataValue.Result(key, cacheEntryId, attempt, result);
        Source = source;
        Key = key;
        Attempt = attempt;
        Result = result;
    }

    public AuthorizedRevisionReportSource Source { get; }
    public RevisionReportKey Key { get; }
    public Guid CacheEntryId { get; }
    public RevisionReportAttemptSummary Attempt { get; }
    public RevisionReportResult? Result { get; }
}

/// <summary>One authorized DB-clock read snapshot. An expired intent is pending, not a fabricated backend timeout.</summary>
public sealed record RevisionReportReadSnapshot
{
    public RevisionReportReadSnapshot(AuthorizedRevisionReportSource source, RevisionReportKey key,
        Guid? cacheEntryId, long generation, RevisionReportResult? selectedResult,
        RevisionReportAttemptSummary? currentAttempt)
    {
        ReportMetadataValue.Source(source, key);
        if (cacheEntryId == Guid.Empty)
            throw new ArgumentException("A cache entry identifier cannot be empty.", nameof(cacheEntryId));
        ArgumentOutOfRangeException.ThrowIfNegative(generation);
        if (generation == 0)
        {
            if (selectedResult is not null || currentAttempt is not null)
                throw new ArgumentException("An unclaimed key cannot have attempt or result pointers.");
        }
        else
        {
            if (cacheEntryId is null || currentAttempt is null || currentAttempt.Generation != generation)
                throw new ArgumentException("A claimed key requires its exact current attempt.");
            if (currentAttempt.Completion?.Observation is { } observed && observed.Operation != key.Operation)
                throw new ArgumentException("The current observation must match the key operation.", nameof(currentAttempt));
            ReportMetadataValue.Result(key, cacheEntryId.Value, currentAttempt, selectedResult);
        }
        Source = source;
        Key = key;
        CacheEntryId = cacheEntryId;
        Generation = generation;
        SelectedResult = selectedResult;
        CurrentAttempt = currentAttempt;
    }

    public AuthorizedRevisionReportSource Source { get; }
    public RevisionReportKey Key { get; }
    public Guid? CacheEntryId { get; }
    public long Generation { get; }
    public RevisionReportResult? SelectedResult { get; }
    public RevisionReportAttemptSummary? CurrentAttempt { get; }
    public RevisionReportState State => SelectedResult is not null ? RevisionReportState.Available :
        CurrentAttempt is null ? RevisionReportState.NotDerived :
        CurrentAttempt.Completion is null ? RevisionReportState.Pending : RevisionReportState.Failed;
    public bool IsClaimLive => CurrentAttempt is { Completion: null } attempt && Source.ObservedAtUtc < attempt.LeaseExpiresAtUtc;
    public DateTimeOffset? NextAttemptAtUtc => CurrentAttempt is null || SelectedResult is not null ? null :
        CurrentAttempt.FinishedAtUtc is { } finished ? finished.AddSeconds(30) : CurrentAttempt.LeaseExpiresAtUtc;
}

public sealed record RevisionReportRepairTarget
{
    public RevisionReportRepairTarget(RevisionReportKey key, Guid cacheEntryId, Guid derivationId,
        PrivateObjectReference derivedObject, RevisionReportRepairReason reason)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(derivedObject);
        ReportMetadataValue.Derived(derivedObject, key, nameof(derivedObject));
        ReportMetadataValue.Known(reason, nameof(reason));
        CacheEntryId = ReportMetadataValue.Id(cacheEntryId, nameof(cacheEntryId));
        DerivationId = ReportMetadataValue.Id(derivationId, nameof(derivationId));
        Key = key;
        DerivedObject = derivedObject;
        Reason = reason;
    }

    public RevisionReportKey Key { get; }
    public Guid CacheEntryId { get; }
    public Guid DerivationId { get; }
    public PrivateObjectReference DerivedObject { get; }
    public RevisionReportRepairReason Reason { get; }
}

public sealed record RevisionReportStoreResult
{
    private RevisionReportStoreResult(RevisionReportStoreOutcome outcome, RevisionReportReadSnapshot? snapshot,
        RevisionReportClaim? claim)
    {
        Outcome = outcome;
        Snapshot = snapshot;
        Claim = claim;
    }

    public RevisionReportStoreOutcome Outcome { get; }
    public RevisionReportReadSnapshot? Snapshot { get; }
    public RevisionReportClaim? Claim { get; }

    public static RevisionReportStoreResult ForClaim(RevisionReportReadSnapshot snapshot, RevisionReportClaim claim)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(claim);
        if (snapshot.State != RevisionReportState.Pending || !snapshot.IsClaimLive || snapshot.Key != claim.Key ||
            snapshot.CacheEntryId != claim.CacheEntryId || snapshot.Generation != claim.Generation ||
            snapshot.Source.Actor != claim.OriginalActor || snapshot.CurrentAttempt is not { } attempt ||
            attempt.DerivationId != claim.DerivationId || attempt.StartedAtUtc != claim.StartedAtUtc ||
            attempt.LeaseExpiresAtUtc != claim.LeaseExpiresAtUtc || attempt.RepairTargetDerivationId != claim.RepairTargetDerivationId)
            throw new ArgumentException("A claimed result requires its matching live snapshot.", nameof(snapshot));
        return new(RevisionReportStoreOutcome.Claimed, snapshot, claim);
    }

    public static RevisionReportStoreResult ForSnapshot(RevisionReportStoreOutcome outcome, RevisionReportReadSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var coherent = outcome switch
        {
            RevisionReportStoreOutcome.Available => snapshot.State == RevisionReportState.Available,
            RevisionReportStoreOutcome.Pending => snapshot.State == RevisionReportState.Pending && snapshot.IsClaimLive,
            RevisionReportStoreOutcome.RetryDelayed => snapshot.State == RevisionReportState.Failed &&
                snapshot.NextAttemptAtUtc is { } retryAt && retryAt > snapshot.Source.ObservedAtUtc,
            RevisionReportStoreOutcome.Applied => snapshot.CurrentAttempt?.Completion is not null,
            _ => false
        };
        if (!coherent)
            throw new ArgumentException("The store outcome must match its snapshot.", nameof(outcome));
        return new(outcome, snapshot, null);
    }

    public static RevisionReportStoreResult WithoutData(RevisionReportStoreOutcome outcome)
    {
        if (outcome is not (RevisionReportStoreOutcome.ClaimLost or RevisionReportStoreOutcome.SelectionChanged or
            RevisionReportStoreOutcome.GenerationExhausted or RevisionReportStoreOutcome.NotAuthorizedOrNotFound))
            throw new ArgumentOutOfRangeException(nameof(outcome));
        return new(outcome, null, null);
    }
}

/// <summary>
/// Each read observes fresh active principal, organization admission and project ReadMetadata access.
/// Scope/source and DB-clock observations are obtained in the same statement. Infrastructure failures
/// remain exceptions. Retained lookup is trusted metadata: future public report selection uses only Result.
/// </summary>
public interface IRevisionReportMetadataReader
{
    Task<AuthorizedRevisionReportSource?> GetSourceAsync(ActorContext actor, RevisionScope scope,
        CancellationToken cancellationToken = default);
    Task<RevisionReportReadSnapshot?> GetCurrentAsync(ActorContext actor, RevisionReportKey key,
        CancellationToken cancellationToken = default);
    Task<AuthorizedRevisionReportAttempt?> GetRetainedAsync(ActorContext actor, RevisionScope scope,
        RevisionReportOperation operation, Guid derivationId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Short metadata transactions only, never storage/worker IO. After a key-row wait, a subsequent
/// READ COMMITTED statement checks fresh full-scope authority/source. Finish fences original actor,
/// full key, object, instance/token/generation and unexpired DB60s lease; terminal facts are immutable.
/// Explicit retry uses DB30s delay; generation overflow refuses without deselection. Repair binds exact
/// selected result and atomically creates a new object intent while retaining immutable old history.
/// </summary>
public interface IRevisionReportMetadataStore
{
    Task<RevisionReportStoreResult> TryClaimAsync(ActorContext actor, RevisionReportKey key, Guid serverInstanceId,
        CancellationToken cancellationToken = default);
    Task<RevisionReportStoreResult> FinishAsync(ActorContext actor, RevisionReportClaim claim,
        RevisionReportCompletion completion, CancellationToken cancellationToken = default);
    Task<RevisionReportStoreResult> RepairAsync(ActorContext actor, RevisionReportRepairTarget target,
        Guid serverInstanceId, CancellationToken cancellationToken = default);
}

internal static class ReportMetadataValue
{
    internal static Guid Id(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("A report identifier cannot be empty.", parameterName);
        return value;
    }

    internal static long Generation(long value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
        return value;
    }

    internal static void Known<T>(T value, string parameterName) where T : struct, Enum
    {
        if (!Enum.IsDefined(value))
            throw new ArgumentOutOfRangeException(parameterName, value, "A report category must be known.");
    }

    internal static void Count(int value, int maximum, string parameterName)
    {
        if (value < 0 || value > maximum)
            throw new ArgumentOutOfRangeException(parameterName, value, "A report count exceeds its bound.");
    }

    internal static DateTimeOffset Utc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
            throw new ArgumentException("A report timestamp must be UTC.", parameterName);
        return value;
    }

    internal static void Lease(DateTimeOffset start, DateTimeOffset expiry)
    {
        Utc(start, nameof(start));
        Utc(expiry, nameof(expiry));
        if (expiry != start.AddSeconds(60))
            throw new ArgumentException("A report lease must last exactly sixty seconds.", nameof(expiry));
    }

    internal static void Finish(DateTimeOffset start, DateTimeOffset finish)
    {
        Utc(start, nameof(start));
        Utc(finish, nameof(finish));
        if (finish < start || finish >= start.AddSeconds(60))
            throw new ArgumentException("A report completion must be within its original lease.", nameof(finish));
    }

    internal static void RepairId(Guid? value, Guid newId)
    {
        if (value == Guid.Empty || value == newId)
            throw new ArgumentException("A repair target must be another nonempty derivation identifier.", nameof(value));
    }

    internal static void Derived(PrivateObjectReference reference, RevisionReportKey key, string parameterName)
    {
        if (reference.Id.Scope != key.Scope || reference.Id.Class != PrivateObjectClass.DerivedArtifact ||
            reference.ByteCount < 1 || reference.ByteCount > key.Policy.StoredObjectByteLimit)
            throw new ArgumentException("A derived reference must match the key scope and effective byte bound.", parameterName);
    }

    internal static void Source(AuthorizedRevisionReportSource source, RevisionReportKey key)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(key);
        if (source.Source != key.Source)
            throw new ArgumentException("The authorized source must match the complete key source.", nameof(source));
    }

    internal static void Result(RevisionReportKey key, Guid cacheId, RevisionReportAttemptSummary attempt,
        RevisionReportResult? result)
    {
        if (attempt.Completion?.Observation is { } observation && observation.Operation != key.Operation)
            throw new ArgumentException("The attempt observation must match the key operation.", nameof(attempt));
        if (attempt.Completion?.IsUsable == true)
        {
            if (result is null || attempt.Completion.Key != key || result.Key != key || result.CacheEntryId != cacheId ||
                result.DerivationId != attempt.DerivationId || result.Generation != attempt.Generation ||
                result.StartedAtUtc != attempt.StartedAtUtc || result.FinishedAtUtc != attempt.FinishedAtUtc ||
                result.DerivedObject != attempt.Completion.DerivedObject || result.Observation != attempt.Completion.Observation)
                throw new ArgumentException("A usable attempt requires its exact immutable result.", nameof(result));
        }
        else if (result is not null)
            throw new ArgumentException("An unfinished or failed attempt cannot contain a usable result.", nameof(result));
    }
}
