using System.Text;
using Weft.Contracts.Projections;
using Weft.Contracts.Validation;
using Weft.Domain;

namespace Weft.Application;

public enum RevisionReportValidationFailure { Integrity = 1, ResourceLimit = 2 }

/// <summary>A bounded pure report refusal; no input text or physical resource detail is retained.</summary>
public sealed class RevisionReportValidationException : Exception
{
    public RevisionReportValidationException(RevisionReportValidationFailure failure)
        : base(failure switch
        {
            RevisionReportValidationFailure.Integrity => "The revision report is inconsistent.",
            RevisionReportValidationFailure.ResourceLimit => "The revision report exceeds an effective limit.",
            _ => throw new ArgumentOutOfRangeException(nameof(failure))
        }) => Failure = failure;

    public RevisionReportValidationFailure Failure { get; }
}

public sealed class RevisionReportServiceException : Exception
{
    public RevisionReportServiceException(RevisionReportFailureCategory failure)
        : base("The revision report operation could not be completed.")
    {
        ReportMetadataValue.Known(failure, nameof(failure));
        Failure = failure;
    }

    public RevisionReportFailureCategory Failure { get; }
}

public enum RevisionReportReadOutcome
{
    Observed = 1,
    NotAuthorizedOrNotFound = 2,
    AdmissionBusy = 3,
    ObjectUnavailable = 4,
    StorageIntegrity = 5,
    UnsupportedFormat = 6,
    ProfileUnavailable = 7,
    ResourceLimit = 8,
    SelectedProjectionUnavailable = 9
}

public enum RevisionReportEnsureOutcome
{
    Observed = 1,
    Pending = 2,
    RetryDelayed = 3,
    AdmissionBusy = 4,
    ClaimLost = 5,
    SelectionChanged = 6,
    GenerationExhausted = 7,
    NotAuthorizedOrNotFound = 8,
    ProfileUnavailable = 9
}

/// <summary>Owned accepted report facts, separate from any current authority observation.</summary>
public abstract record VerifiedRevisionReport
{
    private protected VerifiedRevisionReport(RevisionReportResult metadata) => Metadata = metadata;
    public RevisionReportResult Metadata { get; }
}

public sealed record VerifiedProjectionReport : VerifiedRevisionReport
{
    private VerifiedProjectionReport(RevisionReportResult metadata, ProjectionReportV1 report) : base(metadata) => Report = report;
    public ProjectionReportV1 Report { get; }

    public static VerifiedProjectionReport Create(RevisionReportResult metadata, ProjectionReportV1 report,
        RevisionReportPolicy readCaps, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(report);
        if (metadata.Key is not ProjectionCacheKey)
            throw new ArgumentException("Projection metadata is required.", nameof(metadata));
        var caps = RevisionReportWorkflowValue.EffectiveReadCaps(metadata.Key, readCaps);
        if (metadata.DerivedObject.ByteCount > caps.StoredObjectByteLimit)
            throw new RevisionReportValidationException(RevisionReportValidationFailure.ResourceLimit);
        var owned = RevisionReportSnapshots.CopyProjection(report, caps, cancellationToken);
        var observed = RevisionReportProjectionValidation.Validate(metadata.Key, owned, caps, cancellationToken);
        RevisionReportWorkflowValue.MatchObservation(metadata.Observation, observed);
        return new(metadata, owned);
    }
}

public sealed record VerifiedValidationReport : VerifiedRevisionReport
{
    private VerifiedValidationReport(RevisionReportResult metadata, ValidationReportV1 report, Guid? projectionDerivationId)
        : base(metadata)
    {
        Report = report;
        ProjectionDerivationId = projectionDerivationId;
    }

    public ValidationReportV1 Report { get; }
    public Guid? ProjectionDerivationId { get; }

    /// <summary>A nonnull link selection is supplied only by trusted code after verifying that exact projection.</summary>
    public static VerifiedValidationReport Create(RevisionReportResult metadata, ValidationReportV1 report,
        RevisionReportPolicy readCaps, Guid? projectionDerivationId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(report);
        if (metadata.Key is not ValidationCacheKey)
            throw new ArgumentException("Validation metadata is required.", nameof(metadata));
        if (projectionDerivationId == Guid.Empty)
            throw new ArgumentException("A selected projection identifier cannot be empty.", nameof(projectionDerivationId));
        var caps = RevisionReportWorkflowValue.EffectiveReadCaps(metadata.Key, readCaps);
        if (metadata.DerivedObject.ByteCount > caps.StoredObjectByteLimit)
            throw new RevisionReportValidationException(RevisionReportValidationFailure.ResourceLimit);
        var owned = RevisionReportSnapshots.CopyValidation(report, caps, cancellationToken);
        if (projectionDerivationId is null)
        {
            foreach (var diagnostic in owned.Diagnostics)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (diagnostic.ProjectionLocator is not null)
                    throw new RevisionReportValidationException(RevisionReportValidationFailure.Integrity);
            }
        }
        var observed = RevisionReportValidationValidation.Validate(metadata.Key, owned, caps, cancellationToken);
        RevisionReportWorkflowValue.MatchObservation(metadata.Observation, observed);
        return new(metadata, owned, projectionDerivationId);
    }
}

public sealed record RevisionReportReadResult
{
    private RevisionReportReadResult(RevisionReportReadOutcome outcome, AuthorizedRevisionReportSource? source,
        RevisionReportReadSnapshot? snapshot, VerifiedRevisionReport? report, RevisionReportResult? selectedResult)
    {
        Outcome = outcome;
        Source = source;
        Snapshot = snapshot;
        Report = report;
        SelectedResult = selectedResult;
    }

    public RevisionReportReadOutcome Outcome { get; }
    public AuthorizedRevisionReportSource? Source { get; }
    public RevisionReportReadSnapshot? Snapshot { get; }
    public VerifiedRevisionReport? Report { get; }
    public RevisionReportResult? SelectedResult { get; }
    public RevisionReportState? State => SelectedResult is not null ? RevisionReportState.Available : Snapshot?.State;
    public RevisionReportFailureCategory? Failure => Snapshot?.CurrentAttempt?.Completion?.Failure;
    public DateTimeOffset? NextAttemptAtUtc => Snapshot?.NextAttemptAtUtc;
    public Guid? ProjectionDerivationId => (Report as VerifiedValidationReport)?.ProjectionDerivationId;

    public static RevisionReportReadResult ForSnapshot(RevisionReportReadSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.State == RevisionReportState.Available)
            throw new ArgumentException("Available metadata requires a verified report.", nameof(snapshot));
        return new(RevisionReportReadOutcome.Observed, snapshot.Source, snapshot, null, null);
    }

    public static RevisionReportReadResult ForAvailable(AuthorizedRevisionReportSource source, VerifiedRevisionReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        ReportMetadataValue.Source(source, report.Metadata.Key);
        return new(RevisionReportReadOutcome.Observed, source, null, report, report.Metadata);
    }

    public static RevisionReportReadResult ForReadFailure(AuthorizedRevisionReportSource source,
        RevisionReportResult selectedResult, RevisionReportReadOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(selectedResult);
        ReportMetadataValue.Source(source, selectedResult.Key);
        if (outcome is not (RevisionReportReadOutcome.ObjectUnavailable or RevisionReportReadOutcome.StorageIntegrity or
            RevisionReportReadOutcome.UnsupportedFormat or RevisionReportReadOutcome.ResourceLimit or
            RevisionReportReadOutcome.SelectedProjectionUnavailable))
            throw new ArgumentOutOfRangeException(nameof(outcome));
        return new(outcome, source, null, null, selectedResult);
    }

    public static RevisionReportReadResult WithoutData(RevisionReportReadOutcome outcome)
    {
        if (outcome is not (RevisionReportReadOutcome.NotAuthorizedOrNotFound or RevisionReportReadOutcome.AdmissionBusy or
            RevisionReportReadOutcome.ProfileUnavailable))
            throw new ArgumentOutOfRangeException(nameof(outcome));
        return new(outcome, null, null, null, null);
    }
}

public sealed record RevisionReportEnsureResult
{
    private RevisionReportEnsureResult(RevisionReportEnsureOutcome outcome, RevisionReportReadResult? read,
        RevisionReportReadSnapshot? snapshot)
    {
        Outcome = outcome;
        Read = read;
        Snapshot = snapshot;
    }

    public RevisionReportEnsureOutcome Outcome { get; }
    public RevisionReportReadResult? Read { get; }
    public RevisionReportReadSnapshot? Snapshot { get; }
    public DateTimeOffset? NextAttemptAtUtc => Snapshot?.NextAttemptAtUtc ?? Read?.NextAttemptAtUtc;

    public static RevisionReportEnsureResult ForObserved(RevisionReportReadResult read)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (read.Source is null)
            throw new ArgumentException("An observed result requires actual authorized data.", nameof(read));
        return new(RevisionReportEnsureOutcome.Observed, read, null);
    }

    public static RevisionReportEnsureResult ForPending(RevisionReportReadSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.State != RevisionReportState.Pending || !snapshot.IsClaimLive)
            throw new ArgumentException("Pending admission requires a live unfinished attempt.", nameof(snapshot));
        return new(RevisionReportEnsureOutcome.Pending, null, snapshot);
    }

    public static RevisionReportEnsureResult ForRetryDelayed(RevisionReportReadSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.State != RevisionReportState.Failed || snapshot.NextAttemptAtUtc <= snapshot.Source.ObservedAtUtc)
            throw new ArgumentException("Retry delay requires an observed database retry time in the future.", nameof(snapshot));
        return new(RevisionReportEnsureOutcome.RetryDelayed, null, snapshot);
    }

    public static RevisionReportEnsureResult WithoutData(RevisionReportEnsureOutcome outcome)
    {
        if (outcome is not (RevisionReportEnsureOutcome.AdmissionBusy or RevisionReportEnsureOutcome.ClaimLost or
            RevisionReportEnsureOutcome.SelectionChanged or RevisionReportEnsureOutcome.GenerationExhausted or
            RevisionReportEnsureOutcome.NotAuthorizedOrNotFound or RevisionReportEnsureOutcome.ProfileUnavailable))
            throw new ArgumentOutOfRangeException(nameof(outcome));
        return new(outcome, null, null);
    }
}

public interface IRevisionReportReader
{
    Task<RevisionReportReadResult> GetCurrentAsync(ActorContext actor, RevisionScope scope, RevisionReportOperation operation,
        Guid? projectionDerivationId = null, CancellationToken cancellationToken = default);
    Task<RevisionReportReadResult> GetRetainedAsync(ActorContext actor, RevisionScope scope, RevisionReportOperation operation,
        Guid derivationId, Guid? projectionDerivationId = null, CancellationToken cancellationToken = default);
}

public interface IRevisionReportDeriver
{
    Task<RevisionReportEnsureResult> EnsureAsync(ActorContext actor, RevisionScope scope, RevisionReportOperation operation,
        Guid? repairDerivationId = null, CancellationToken cancellationToken = default);
}

internal static class RevisionReportWorkflowValue
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static string Text(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length is 0 or > 128)
            throw new ArgumentException("A report identity must be bounded text.", parameterName);
        try
        {
            if (value.Contains('\0') || string.IsNullOrWhiteSpace(value) || StrictUtf8.GetByteCount(value) > 128)
                throw new ArgumentException("A report identity must be bounded valid UTF8 text.", parameterName);
        }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException("A report identity must be valid Unicode.", parameterName);
        }
        return value;
    }

    internal static void MatchObservation(RevisionReportObservation expected, RevisionReportObservation actual)
    {
        if (expected != actual)
            throw new RevisionReportValidationException(RevisionReportValidationFailure.Integrity);
    }

    internal static RevisionReportPolicy EffectiveReadCaps(RevisionReportKey key, RevisionReportPolicy activeCaps)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(activeCaps);
        var stored = key.Policy;
        // A derived-only read binds historical native facts; it acquires no native bytes.
        return new(stored.NativeByteLimit, Math.Min(stored.WorkerResponseByteLimit, activeCaps.WorkerResponseByteLimit),
            Math.Min(stored.ItemLimit, activeCaps.ItemLimit), Math.Min(stored.ResponseItemLimit, activeCaps.ResponseItemLimit),
            Math.Min(stored.JsonDepthLimit, activeCaps.JsonDepthLimit), Math.Min(stored.DetailDepthLimit, activeCaps.DetailDepthLimit),
            Math.Min(stored.DiagnosticLimit, activeCaps.DiagnosticLimit), Math.Min(stored.ValidationIssueLimit, activeCaps.ValidationIssueLimit),
            Math.Min(stored.TextByteLimit, activeCaps.TextByteLimit), Math.Min(stored.HeaderByteLimit, activeCaps.HeaderByteLimit),
            Math.Min(stored.StoredObjectByteLimit, activeCaps.StoredObjectByteLimit), Math.Min(stored.ResponseByteLimit, activeCaps.ResponseByteLimit));
    }
}
