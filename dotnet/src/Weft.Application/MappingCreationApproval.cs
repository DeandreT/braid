using Weft.Domain;

namespace Weft.Application;

/// <summary>Deliberate approval intent, not authority. Only the authenticated approval store can retain it as an approved decision.</summary>
public sealed class CreationApprovalDecision
{
    public CreationApprovalDecision(Sha256Digest proposal, IEnumerable<string> acceptedCriticalAssumptions)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var ids = acceptedCriticalAssumptions.Take(65).ToArray();
        if (ids.Length > 64 || ids.Any(string.IsNullOrEmpty) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            throw new ArgumentException("Approval decisions must uniquely identify critical assumptions.");
        Array.Sort(ids, StringComparer.Ordinal);
        Proposal = proposal; AcceptedCriticalAssumptions = Array.AsReadOnly(ids);
    }
    public Sha256Digest Proposal { get; }
    public IReadOnlyList<string> AcceptedCriticalAssumptions { get; }
    internal bool Matches(MappingCreationProposal proposal) => Proposal == proposal.Fingerprint &&
        AcceptedCriticalAssumptions.SequenceEqual(proposal.Notes.Assumptions.Where(a => a.Critical).Select(a => a.Id).Order(StringComparer.Ordinal));
}

public sealed record MappingCreationState(MappingCreationProposal Proposal, CreationApprovalDecision? Approval = null)
{
    public bool Approved => Approval is not null;
}

/// <summary>Durable actor/project-owned immutable retention. Same id/key and fingerprint replays; any substitution conflicts.
/// Reads recheck content authority. Retention checks current spec identity and verified generation; it never approves.</summary>
public interface IMappingCreationProposalStore
{
    Task<MappingCreationState?> RetainAsync(ActorContext actor, MappingCreationProposal proposal, CancellationToken ct);
    Task<MappingCreationState?> ReadAsync(ActorContext actor, ProjectScope project, Guid proposalId, CancellationToken ct);
}

/// <summary>Required durable approval integration, not implemented by a model-facing writer.
/// Approval CAS binds author, project, proposal, fingerprint, exact assumption decisions and stable ingestion key. Critical questions refuse.
/// Acquire rechecks current Publisher/content authority and exact spec/lifecycle versions. Missing/revoked/stale denies.
/// The lease MUST fence invalidation until disposal, including across ingestion reservation/commit. A check-then-act adapter is invalid.
/// Existing approval acquisition never creates approval. Lost acknowledgements replay the same durable decision.
/// No production composition may enable creation until an implementation satisfies this contract.</summary>
public interface IMappingCreationApprovalStore
{
    Task<IMappingCreationApprovalLease?> AcquireAsync(ActorContext actor, ProjectScope project, Guid proposalId,
        Sha256Digest fingerprint, CreationApprovalDecision? decision, CancellationToken ct);
}

public interface IMappingCreationApprovalLease : IAsyncDisposable
{
    MappingCreationState State { get; }
}

public sealed record MappingCreationObservation(MappingCreationState State, RevisionIngestionReceipt? Receipt,
    RevisionIngestionOutcome? AttemptOutcome = null);

/// <summary>Dedicated authenticated-call orchestration. Transport must authenticate the actor and protect deliberate approval.
/// Do not expose ApproveAsync as a model tool. Discovery and recovery cannot grant approval or reserve fresh ingestion.</summary>
public sealed class MappingCreationApprovalService(IMappingCreationProposalStore proposals, IMappingCreationApprovalStore approvals,
    IRevisionIngestionService ingestion, IIngestionWorkflowReader receipts)
{
    public async Task<MappingCreationState?> RetainAsync(ActorContext actor, MappingCreationProposal proposal, CancellationToken ct = default)
    {
        if (actor.PrincipalId != proposal.Author) throw new ArgumentException("Only the proposal author may retain it.");
        return Validate(await proposals.RetainAsync(actor, proposal, ct).ConfigureAwait(false), actor, proposal.Project, proposal.Id, proposal.Fingerprint);
    }

    public async Task<MappingCreationObservation?> ReadAsync(ActorContext actor, ProjectScope project, Guid id, Sha256Digest fingerprint, CancellationToken ct = default)
    {
        var state = Validate(await proposals.ReadAsync(actor, project, id, ct).ConfigureAwait(false), actor, project, id, fingerprint);
        return state is null ? null : new(state, await Receipt(actor, state, ct).ConfigureAwait(false));
    }

    public Task<MappingCreationObservation?> ApproveAsync(ActorContext actor, ProjectScope project, Guid id, Sha256Digest fingerprint,
        CreationApprovalDecision? decision = null, CancellationToken ct = default) =>
        Execute(actor, project, id, fingerprint, true, decision ?? new(fingerprint, []), ct);

    public Task<MappingCreationObservation?> RecoverAsync(ActorContext actor, ProjectScope project, Guid id, Sha256Digest fingerprint, CancellationToken ct = default) =>
        Execute(actor, project, id, fingerprint, false, null, ct);

    private async Task<MappingCreationObservation?> Execute(ActorContext actor, ProjectScope project, Guid id, Sha256Digest fingerprint,
        bool approve, CreationApprovalDecision? decision, CancellationToken ct)
    {
        // Authorized durable terminal receipts remain observable even if the spec is now stale.
        var before = await ReadAsync(actor, project, id, fingerprint, ct).ConfigureAwait(false);
        if (before is null || before.Receipt?.Phase is IngestionPhase.Committed or IngestionPhase.Rejected or IngestionPhase.HeadConflict or IngestionPhase.AttemptExhausted)
            return before;
        if (before.State.Proposal.Notes.HasCriticalQuestions || !approve && (!before.State.Approved || before.Receipt is null)) return before;
        if (approve && !decision!.Matches(before.State.Proposal)) throw new ArgumentException("Every exact critical assumption requires explicit acknowledgement.");
        await using var lease = await approvals.AcquireAsync(actor, project, id, fingerprint, decision, ct).ConfigureAwait(false);
        if (lease is null) return null;
        var state = Validate(lease.State, actor, project, id, fingerprint)!;
        if (!state.Approved || state.Proposal.Notes.HasCriticalQuestions) throw new InvalidOperationException("Invalid approval lease.");
        var p = state.Proposal;
        var existing = await Receipt(actor, state, ct).ConfigureAwait(false);
        if (existing?.Phase is IngestionPhase.Committed or IngestionPhase.Rejected or IngestionPhase.HeadConflict or IngestionPhase.AttemptExhausted)
            return new(state, existing);
        if (!approve && existing is null) return new(state, null);
        RevisionIngestionOutcome? outcome = null;
        try
        {
            if (approve)
            {
                using var native = new MemoryStream(p.Compilation.CopyNative(), false);
                outcome = (await ingestion.CreateAsync(actor, project, p.RequestKey, p.Request, native, ct).ConfigureAwait(false)).Outcome;
            }
            else outcome = (await ingestion.ResumeAsync(actor, project, existing!.OperationId, ct).ConfigureAwait(false)).Outcome;
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException) && !ct.IsCancellationRequested)
        {
            // An ambiguous acknowledgement is not success. Discover by the original actor/key below; never allocate a replacement.
        }
        // Read again through current content authority; revocation must not reveal retained content.
        var observed = await ReadAsync(actor, project, id, fingerprint, ct).ConfigureAwait(false);
        return observed is null ? null : observed with { AttemptOutcome = outcome };
    }

    private async Task<RevisionIngestionReceipt?> Receipt(ActorContext actor, MappingCreationState state, CancellationToken ct)
    {
        var p = state.Proposal;
        var receipt = await receipts.FindOwnReceiptAsync(actor, p.Project, IngestionOperationKind.Create, null, p.RequestKey, ct).ConfigureAwait(false);
        if (receipt is null) return null;
        var expected = IngestionIntent.ForCreate(p.Request.Metadata, p.Request.BackendId, p.Request.OriginalFilename,
            p.Compilation.NativeDigest, p.Compilation.CopyNative().Length);
        if (!state.Approved || receipt.Facts.Project != p.Project || receipt.Facts.OriginalActor != actor.PrincipalId ||
            receipt.Facts.IdempotencyKey != p.RequestKey || receipt.Facts.Intent != expected)
            throw new InvalidOperationException("Creation receipt does not match approved content.");
        return RevisionIngestionReceipt.FromMetadata(receipt);
    }

    private static MappingCreationState? Validate(MappingCreationState? state, ActorContext actor, ProjectScope project, Guid id, Sha256Digest fingerprint)
    {
        if (id == Guid.Empty) throw new ArgumentException("Proposal identity is required.");
        if (state is not null && (state.Proposal.Author != actor.PrincipalId || state.Proposal.Project != project ||
            state.Proposal.Id != id || state.Proposal.Fingerprint != fingerprint || state.Approved &&
            (state.Proposal.Notes.HasCriticalQuestions || !state.Approval!.Matches(state.Proposal))))
            throw new InvalidOperationException("Creation store returned substituted content.");
        return state;
    }
}
