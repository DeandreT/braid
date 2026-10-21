using Weft.Ai;
using Weft.Contracts.Projections;
using Weft.Domain;

namespace Weft.Application.MappingTools;

/// <summary>
/// Trusted read-only <see cref="IChatReadOnlyToolPort"/> and <see cref="IChatCitationPort"/> over a conversation's
/// exact retained reports. It intentionally has no derivation, payload, worker or provider port. Share one instance
/// per host: its local capacity covers both tool reads and citation checks.
/// </summary>
public sealed partial class MappingToolService : IChatReadOnlyToolPort, IChatCitationPort
{
    private const int MaximumRetrievedNodes = 1_024;
    private readonly IRevisionReportMetadataReader authority;
    private readonly IRevisionReportReader reports;
    private readonly MappingToolLimits limits;
    private readonly SemaphoreSlim capacity = new(2, 2);

    public MappingToolService(IRevisionReportMetadataReader authority, IRevisionReportReader reports,
        MappingToolLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(reports);
        this.authority = authority;
        this.reports = reports;
        this.limits = limits ?? new();
    }

    /// <summary>Side-effect free: closed name/schema and conversation ownership only. Execution rechecks both.</summary>
    public Task<bool> ValidateAsync(ChatReadOnlyToolContext context, AiToolIntent intent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(intent);
        ct.ThrowIfCancellationRequested();
        if (context.Actor.PrincipalId != context.Conversation.Key.Creator) return Task.FromResult(false);
        try
        {
            _ = MappingToolRegistry.Parse(intent.Name, intent.Arguments.Json, limits);
            return Task.FromResult(true);
        }
        catch (MappingToolRefusal) { return Task.FromResult(false); }
    }

    public async Task<ChatReadOnlyToolResult> ExecuteAsync(ChatReadOnlyToolContext context, AiToolIntent intent, CancellationToken ct)
    {
        var reply = await InvokeAsync(context, intent, ct);
        // Candidates stay unverified until the citation port checks them against the pinned reports.
        return new(reply.Result, reply.RetrievedNodes, reply.Citations);
    }

    /// <summary>Typed form of <see cref="ExecuteAsync"/>. Caller cancellation throws; every other refusal is categorical.</summary>
    public async Task<MappingToolReply> InvokeAsync(ChatReadOnlyToolContext context, AiToolIntent intent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(intent);
        cancellationToken.ThrowIfCancellationRequested();
        var actor = context.Actor;
        var conversation = context.Conversation;
        var budget = new ResultBudget(Math.Min(limits.OutputBytes, context.RemainingResultBytes),
            Math.Min(MaximumRetrievedNodes, context.RemainingRetrievedNodes));
        var admitted = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(limits.Duration);
        var token = deadline.Token;
        try
        {
            var source = await AuthorizeAsync(actor, conversation, null, token);
            if (!capacity.Wait(0)) return Failure(MappingToolOutcome.Busy);
            admitted = true;
            var call = MappingToolRegistry.Parse(intent.Name, intent.Arguments.Json, limits);
            var work = new MappingToolWork(limits.WorkUnits, token);
            MappingToolReply result;
            if (call.Name == "get_validation_issues")
            {
                var validation = await ReadValidationAsync(actor, conversation, source, token);
                result = Validation(validation, call, work, budget, token);
            }
            else
            {
                var projection = await ReadProjectionAsync(actor, conversation, source, token);
                result = Projection(projection, call, work, budget, token);
            }
            // Preparation is not authority. Recheck immediately before releasing even Unknown/empty results.
            await AuthorizeAsync(actor, conversation, source, token);
            token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { cancellationToken.ThrowIfCancellationRequested(); throw; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        { cancellationToken.ThrowIfCancellationRequested(); return Failure(MappingToolOutcome.TimedOut); }
        catch (MappingToolRefusal refusal)
        { cancellationToken.ThrowIfCancellationRequested(); return Failure(refusal.Outcome); }
        catch (RevisionReportServiceException)
        { cancellationToken.ThrowIfCancellationRequested(); return Failure(MappingToolOutcome.DependencyFailure); }
        catch (RevisionReportValidationException error)
        { cancellationToken.ThrowIfCancellationRequested(); return Failure(error.Failure == RevisionReportValidationFailure.ResourceLimit ? MappingToolOutcome.ResourceLimit : MappingToolOutcome.DependencyFailure); }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        { cancellationToken.ThrowIfCancellationRequested(); return Failure(MappingToolOutcome.DependencyFailure); }
        finally { if (admitted) capacity.Release(); }
    }

    private async Task<RevisionReportSource> AuthorizeAsync(ActorContext actor, ChatConversationIntent conversation,
        RevisionReportSource? expected, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Conversations are creator-private; another principal never borrows their pins.
        if (actor.PrincipalId != conversation.Key.Creator) throw new MappingToolRefusal(MappingToolOutcome.NotAuthorizedOrNotFound);
        var observation = await authority.GetSourceAsync(actor, conversation.Key.Revision, token);
        token.ThrowIfCancellationRequested();
        if (observation is null || observation.Actor != actor.PrincipalId || observation.Source.Scope != conversation.Key.Revision ||
            expected is not null && observation.Source != expected)
            throw new MappingToolRefusal(MappingToolOutcome.NotAuthorizedOrNotFound);
        return observation.Source;
    }

    private async Task<VerifiedProjectionReport> ReadProjectionAsync(ActorContext actor, ChatConversationIntent conversation,
        RevisionReportSource source, CancellationToken token)
    {
        var pin = conversation.Projection;
        var read = await reports.GetRetainedAsync(actor, conversation.Key.Revision,
            RevisionReportOperation.Projection, pin.DerivationId, cancellationToken: token);
        return CheckRead(actor, source, RevisionReportOperation.Projection, pin, read, token) as VerifiedProjectionReport ??
            throw new MappingToolRefusal(MappingToolOutcome.ReportUnavailable);
    }

    private async Task<VerifiedValidationReport> ReadValidationAsync(ActorContext actor, ChatConversationIntent conversation,
        RevisionReportSource source, CancellationToken token)
    {
        var pin = conversation.Validation;
        var projectionId = conversation.Projection.DerivationId;
        var read = await reports.GetRetainedAsync(actor, conversation.Key.Revision,
            RevisionReportOperation.Validation, pin.DerivationId, projectionId, token);
        if (CheckRead(actor, source, RevisionReportOperation.Validation, pin, read, token) is not VerifiedValidationReport validation ||
            validation.ProjectionDerivationId != projectionId)
            throw new MappingToolRefusal(MappingToolOutcome.ReportUnavailable);
        // The reader links by derivation UUID; the conversation additionally pins that projection's complete identity.
        _ = await ReadProjectionAsync(actor, conversation, source, token);
        return validation;
    }

    private static VerifiedRevisionReport CheckRead(ActorContext actor, RevisionReportSource source,
        RevisionReportOperation operation, ChatReportPin pin, RevisionReportReadResult read, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (read.Outcome == RevisionReportReadOutcome.NotAuthorizedOrNotFound)
            throw new MappingToolRefusal(MappingToolOutcome.NotAuthorizedOrNotFound);
        if (read.Outcome == RevisionReportReadOutcome.AdmissionBusy) throw new MappingToolRefusal(MappingToolOutcome.Busy);
        if (read.Outcome == RevisionReportReadOutcome.ResourceLimit) throw new MappingToolRefusal(MappingToolOutcome.ResourceLimit);
        if (read.Outcome != RevisionReportReadOutcome.Observed || read.Source is null || read.Report is not { } report)
            throw new MappingToolRefusal(MappingToolOutcome.ReportUnavailable);
        var metadata = report.Metadata;
        if (read.Source.Actor != actor.PrincipalId || read.Source.Source != source || read.SelectedResult != metadata ||
            metadata.Key.Source != source || metadata.Key.Operation != operation || Pin(metadata) != pin)
            throw new MappingToolRefusal(MappingToolOutcome.ReportUnavailable);
        return report;
    }

    /// <summary>The same exact-attempt identity that conversation creation and citation admission use.</summary>
    internal static ChatReportPin Pin(RevisionReportResult metadata) =>
        new(metadata.CacheEntryId, metadata.DerivationId, metadata.Key.Fingerprint, metadata.DerivedObject.Digest);

    private MappingToolReply Prepare(MappingToolOutcome outcome, ProjectionCoverage? coverage, MappingToolData? data,
        IReadOnlyList<ChatCitationTarget> citations, int retrievedNodes, ResultBudget budget, CancellationToken token)
    {
        // Refuse whole results. Never crop literals, details, citations or nodes to fit a remaining budget.
        if (citations.Count > limits.CitationCount || retrievedNodes > budget.RetrievedNodes)
            throw new MappingToolRefusal(MappingToolOutcome.ResourceLimit);
        var wire = new WireCitation[citations.Count];
        for (var index = 0; index < wire.Length; index++)
            wire[index] = new(citations[index].Kind, citations[index].ResourceId, citations[index].DiagnosticOrdinal);
        var envelope = new WireEnvelope(SchemaVersion, outcome, Evidence, coverage, data, wire);
        var bytes = RevisionReportResponseSerializer.Serialize(envelope, budget.Bytes, token);
        AiJsonObject result;
        // The provider JSON factory owns the depth/token/element/Unicode ceilings for tool results.
        try { result = AiJsonObject.CopyFrom(MappingToolRegistry.Utf8.GetString(bytes)); }
        catch (ArgumentException) { throw new MappingToolRefusal(MappingToolOutcome.ResourceLimit); }
        token.ThrowIfCancellationRequested();
        return new(outcome, coverage, data, citations, result, retrievedNodes);
    }

    private static MappingToolReply Failure(MappingToolOutcome outcome)
    {
        // Fixed categorical vocabulary only. This path cannot contain tool input, report text or exception details.
        var bytes = RevisionReportResponseSerializer.Serialize(new WireEnvelope(SchemaVersion,
            outcome, Evidence, null, null, Array.Empty<WireCitation>()), 1_024);
        return new(outcome, null, null, Array.Empty<ChatCitationTarget>(),
            AiJsonObject.CopyFrom(MappingToolRegistry.Utf8.GetString(bytes)), 0);
    }

    private const string SchemaVersion = "weft.mapping-tool-result.v1";
    private const string Evidence = "explanationOnly";

    private readonly record struct ResultBudget(int Bytes, int RetrievedNodes);

    private sealed record WireCitation(ChatCitationKind Kind, string? ResourceId, int? DiagnosticOrdinal)
    {
        public override string ToString() => nameof(WireCitation);
    }

    private sealed record WireEnvelope(string SchemaVersion, MappingToolOutcome Outcome, string Evidence,
        ProjectionCoverage? Coverage, MappingToolData? Data, IReadOnlyList<WireCitation> Citations)
    {
        public override string ToString() => nameof(WireEnvelope);
    }
}
