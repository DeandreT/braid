using Weft.Domain;

namespace Weft.Application.Regression;

public enum RegressionRunState { Queued, Running, Completed, Canceled, AttemptsExhausted, AuthorityRevoked }
public enum RegressionRunSubmitOutcome { Created, Replayed, Conflict, NotAuthorizedOrNotFound, Busy }
public enum RegressionRunClaimOutcome { Claimed, Busy, Finished, Missing }
public sealed record RegressionRunSubmission(ActorContext Actor, Guid RunId, RegressionCaseReference Case);
public sealed record RegressionRunClaimResult(RegressionRunClaimOutcome Outcome, RegressionRunClaim? Claim);

/// <summary>Database-clock lease and immutable input. The token fences all later transitions.</summary>
public sealed record RegressionRunClaim(Guid RunId, PrincipalId Submitter, RegressionCaseReference Case,
    int Attempt, Guid Worker, Guid Token, DateTimeOffset ExpiresAt);

/// <summary>One bounded immutable snapshot; private completion bytes are copied on access.</summary>
public sealed class RegressionRunSnapshot
{
    private readonly byte[]? completion;
    public RegressionRunSnapshot(Guid runId, PrincipalId submitter, RegressionCaseReference reference,
        RegressionRunState state, int attempt, DateTimeOffset createdAt, byte[]? completion, Sha256Digest? digest)
    {
        if (runId == Guid.Empty || !Enum.IsDefined(state) || attempt is < 0 or > 3 ||
            (completion is null) != (digest is null) || (state == RegressionRunState.Completed) != (completion is not null))
            throw new InvalidOperationException("Regression run snapshot refused.");
        ArgumentNullException.ThrowIfNull(submitter);
        ArgumentNullException.ThrowIfNull(reference);
        if (completion is not null && (completion.Length > RegressionRunResultCodec.MaximumBytes || Sha256Digest.FromBytes(completion) != digest))
            throw new InvalidOperationException("Regression run completion refused.");
        RunId = runId;
        Submitter = submitter;
        Case = reference;
        State = state;
        Attempt = attempt;
        CreatedAt = createdAt;
        this.completion = completion?.ToArray();
        CompletionDigest = digest;
    }
    public Guid RunId { get; }
    public PrincipalId Submitter { get; }
    public RegressionCaseReference Case { get; }
    public RegressionRunState State { get; }
    public int Attempt { get; }
    public DateTimeOffset CreatedAt { get; }
    public Sha256Digest? CompletionDigest { get; }
    public byte[]? CopyCompletion() => completion?.ToArray();
    public override string ToString() => $"{nameof(RegressionRunSnapshot)}:{State}";
}

public sealed record RegressionRunSummary(Guid RunId, Guid CaseId, long Version, RevisionScope Revision,
    RegressionRunState State, int Attempt, DateTimeOffset CreatedAt);
public sealed record RegressionRunPage(IReadOnlyList<RegressionRunSummary> Runs, Guid? NextRunId);

public interface IRegressionRunReader
{
    Task<RegressionRunSnapshot?> ReadAsync(ActorContext actor, Guid organizationId, Guid runId, CancellationToken token = default);
    Task<RegressionRunPage> ListAsync(ActorContext actor, MappingScope mapping, int limit, Guid? afterRunId = null, CancellationToken token = default);
}

public interface IRegressionRunStore : IRegressionRunReader
{
    Task<RegressionRunSubmitOutcome> SubmitAsync(RegressionRunSubmission submission, CancellationToken token = default);
    Task<RegressionRunClaimResult> ClaimAsync(Guid organizationId, Guid runId, Guid worker, CancellationToken token = default);
    Task<bool> ObserveAsync(RegressionRunClaim claim, CancellationToken token = default);
    Task<bool> CompleteAsync(RegressionRunClaim claim, RegressionCaseExecution completion, CancellationToken token = default);
    Task<bool> ReleaseAsync(RegressionRunClaim claim, CancellationToken token = default);
    Task<bool> CancelAsync(ActorContext actor, Guid organizationId, Guid runId, CancellationToken token = default);
}
