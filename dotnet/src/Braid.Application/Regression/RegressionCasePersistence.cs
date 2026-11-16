using Braid.Domain;

namespace Braid.Application.Regression;

public enum RegressionCaseSaveOutcome { Unavailable, Created, Appended, Replayed, Conflict }

/// <summary>Only an acknowledged save can carry its immutable stored reference.</summary>
public sealed class RegressionCaseSaveResult
{
    private RegressionCaseSaveResult(RegressionCaseSaveOutcome outcome, RegressionCaseReference? reference)
    {
        Outcome = outcome;
        Reference = reference;
    }

    public RegressionCaseSaveOutcome Outcome { get; }
    public RegressionCaseReference? Reference { get; }
    public static RegressionCaseSaveResult Saved(RegressionCaseSaveOutcome outcome, RegressionCaseReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (outcome is not (RegressionCaseSaveOutcome.Created or RegressionCaseSaveOutcome.Appended or RegressionCaseSaveOutcome.Replayed))
            throw new ArgumentOutOfRangeException(nameof(outcome));
        return new(outcome, reference);
    }
    public static RegressionCaseSaveResult Refused(RegressionCaseSaveOutcome outcome)
    {
        if (outcome is not (RegressionCaseSaveOutcome.Unavailable or RegressionCaseSaveOutcome.Conflict))
            throw new ArgumentOutOfRangeException(nameof(outcome));
        return new(outcome, null);
    }
    public override string ToString() => $"{nameof(RegressionCaseSaveResult)}:{Outcome}";
}

/// <summary>Metadata only: no input, output, parameter, content digest or payload-derived text.</summary>
public sealed record RegressionCaseSummary
{
    public RegressionCaseSummary(Guid caseId, long version, RevisionScope revision, PrincipalId owner,
        string name, DateTimeOffset createdAt, PrincipalId savedBy)
    {
        if (caseId == Guid.Empty || version < 1) throw new RegressionCaseException(RegressionCaseFailure.StorageIntegrity);
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(savedBy);
        CaseId = caseId;
        Version = version;
        Revision = revision;
        Owner = owner;
        Name = RegressionCaseValues.Text(name, 256);
        CreatedAt = createdAt.ToUniversalTime();
        SavedBy = savedBy;
    }

    public Guid CaseId { get; }
    public long Version { get; }
    public RevisionScope Revision { get; }
    public PrincipalId Owner { get; }
    public string Name { get; }
    public DateTimeOffset CreatedAt { get; }
    public PrincipalId SavedBy { get; }
    public override string ToString() => nameof(RegressionCaseSummary);
}

public sealed class RegressionCasePage
{
    public RegressionCasePage(IReadOnlyList<RegressionCaseSummary> cases, Guid? nextCaseId)
    {
        ArgumentNullException.ThrowIfNull(cases);
        if (cases.Count > 100 || cases.Any(item => item is null) || nextCaseId == Guid.Empty)
            throw new RegressionCaseException(RegressionCaseFailure.StorageIntegrity);
        Cases = Array.AsReadOnly(cases.ToArray());
        NextCaseId = nextCaseId;
    }
    public IReadOnlyList<RegressionCaseSummary> Cases { get; }
    public Guid? NextCaseId { get; }
    public override string ToString() => nameof(RegressionCasePage);
}

/// <summary>
/// Save is explicit project-fixture publication, restricted to current publishers/administrators.
/// Version one fixes the creator and mapping; appends keep both and advance exactly once. An exact
/// original retry returns its retained version, even if a newer head exists; changed retries conflict.
/// The envelope is stored atomically, independently of temporary preview retention. Success is returned
/// only after acknowledged commit. Storage errors and ambiguous commits remain exceptions.
/// </summary>
public interface IRegressionCaseWriter
{
    Task<RegressionCaseSaveResult> SaveAsync(ActorContext actor, RegressionCaseVersion version,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Current project members can list metadata. Full private case data requires current sample-run
/// authority (runner, publisher or administrator); this never grants production-payload access.
/// Each operation checks fresh authority and exact ancestry. Missing/denied private reads return null,
/// while malformed stored bytes and unavailable storage are failures, not absent data. Callers recheck
/// current authority at dispatch, rather than treating these returned facts as a lasting capability.
/// </summary>
public interface IRegressionCaseReader
{
    Task<RegressionCasePage> ListAsync(ActorContext actor, MappingScope mapping, int limit,
        Guid? afterCaseId = null, CancellationToken cancellationToken = default);
    Task<RegressionCaseVersion?> ReadAsync(ActorContext actor, MappingScope mapping, Guid caseId, long version,
        CancellationToken cancellationToken = default);
}

/// <summary>Exact retained case-version metadata under current project-read authority. Never opens the sample envelope.</summary>
public interface IRegressionCaseMetadataReader
{
    Task<RegressionCaseSummary?> ReadMetadataAsync(ActorContext actor, MappingScope mapping, Guid caseId, long version,
        CancellationToken cancellationToken = default);
}
