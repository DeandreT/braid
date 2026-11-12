using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Weft.Ai;
using Weft.Api.Catalog;
using Weft.Application;
using Weft.Application.MappingTools;
using Weft.Domain;

namespace Weft.Api.Chat.Workspace;

/// <summary>
/// The trusted tool, citation and turn-context port for workspace conversations. Every call is bound to the
/// conversation's creator and organization, freshly re-authorized as that user through the same read ports and read
/// sequences the HTTP API uses, and returns a complete bounded result; lists that would not fit are truncated
/// explicitly. Its only write saves a typed mapping edit as the next version through the user's own edit authority;
/// it has no derivation, run, build, activation or provider port. Share one instance per host.
/// </summary>
public sealed partial class WorkspaceToolService : IChatReadOnlyToolPort, IChatWorkspaceCitationPort, IChatTurnContextPort, IChatPayloadAuthorizationPort
{
    private const string Schema = "weft.workspace-tool-result.v1";
    private static readonly TimeSpan CallDuration = TimeSpan.FromSeconds(20);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };
    private readonly IServiceProvider services;
    private readonly ChatApiOptions options;
    private readonly WorkspaceToolFeatures features;
    private readonly MappingToolService reports;
    private readonly IWorkspaceUserGuide? guide;
    private readonly SemaphoreSlim capacity = new(4, 4);

    public WorkspaceToolService(IServiceProvider services, ChatApiOptions options, WorkspaceToolFeatures features,
        IWorkspaceUserGuide? guide = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(features);
        if (features.Guide != guide is not null) throw new ArgumentException("The guide feature requires its reader.", nameof(guide));
        this.services = services;
        this.options = options;
        this.features = features;
        this.guide = guide;
        Manifest = WorkspaceToolRegistry.Definitions(features);
        Grounding = WorkspaceChatGrounding.Pin(Manifest);
        reports = new MappingToolService(services.GetRequiredService<IRevisionReportMetadataReader>(),
            services.GetRequiredService<IRevisionReportReader>(), new MappingToolLimits(outputBytes: WorkspaceChatGrounding.CallResultBytes));
        // Payload data needs the restricted grant reader; without it the payload manifest does not exist.
        if ((features.Previews || features.Regressions) && services.GetService<IProjectPayloadPermissionReader>() is not null &&
            services.GetService<IProjectPayloadGrantPresence>() is not null)
        {
            PayloadManifest = WorkspaceToolRegistry.Definitions(features, payloads: true);
            PayloadGrounding = WorkspaceChatGrounding.Pin(PayloadManifest.Value);
        }
    }

    /// <summary>The manifest offered to function-tool profiles.</summary>
    public ImmutableArray<AiToolDefinition> Manifest { get; }
    internal ChatGroundingPolicyPin Grounding { get; }
    /// <summary>The manifest with the payload data tools, for turns whose profile and creator may share sample data.</summary>
    public ImmutableArray<AiToolDefinition>? PayloadManifest { get; }
    internal ChatGroundingPolicyPin? PayloadGrounding { get; }

    /// <summary>The pinned manifest a turn may use and whether it offers payload data; null for any other pin.</summary>
    private (ImmutableArray<AiToolDefinition> Tools, bool Payloads)? ManifestOf(ChatTurnIntent turn) =>
        turn.Envelope.GroundingPolicy == Grounding ? (Manifest, false)
        : PayloadGrounding is { } pin && turn.Envelope.GroundingPolicy == pin ? (PayloadManifest!.Value, true) : null;

    /// <summary>Payload-capable or unrecognized historical turns must be read again through current authorized tools, not copied to a model.</summary>
    public bool IncludeHistory(ChatTurnIntent turn) => ManifestOf(turn) is { Payloads: false };

    /// <summary>
    /// A workspace turn needs a function-tool profile that may receive mapping metadata, under one of this host's
    /// pins; a turn pinned with the payload tools also needs a profile that may receive payload data.
    /// </summary>
    internal bool Allows(ChatModelProfile profile, ChatTurnIntent turn) =>
        (profile.Spec.Capabilities & AiCapability.FunctionTools) != 0 && profile.Spec.MappingMetadataAllowed &&
        ManifestOf(turn) is { } pinned && (!pinned.Payloads || profile.Spec.PayloadsAllowed);

    /// <summary>A new provider/tool boundary cannot reuse a payload manifest after the creator loses its last current grant.</summary>
    internal async Task<bool> AllowsAsync(ActorContext actor, Guid organizationId, ChatModelProfile profile, ChatTurnIntent turn,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Allows(profile, turn)) return false;
        return ManifestOf(turn) is { Payloads: false } ||
            await services.GetRequiredService<IProjectPayloadGrantPresence>().AnyAsync(actor, organizationId, cancellationToken);
    }

    /// <summary>Re-authorizes every project that actually contributed payload bytes to this turn, never a model's citation text.</summary>
    public async Task<bool> AllowsAsync(ActorContext actor, ChatConversationIntent conversation, ChatTurnIntent turn,
        AiProfileSpec profile, IReadOnlyList<ChatPayloadDisclosureFact> sources, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (conversation.Key.Kind != ChatConversationKind.Workspace || conversation.Key.Creator != actor.PrincipalId ||
            !profile.PayloadsAllowed || !profile.MappingMetadataAllowed || ManifestOf(turn) is not { Payloads: true } || sources.Count > 8)
            return false;
        foreach (var projectId in sources.Select(source => source.ProjectId).Distinct())
        {
            var scope = new ProjectScope(conversation.Key.OrganizationId, projectId);
            var project = await Catalog.GetProjectAsync(actor, scope, ct);
            var grant = await services.GetRequiredService<IProjectPayloadPermissionReader>().ObserveAsync(actor, scope, ct);
            ct.ThrowIfCancellationRequested();
            if (project is null || project.Summary.Scope != scope || project.Access.Scope != scope || project.Access.PrincipalId != actor.PrincipalId ||
                !project.Access.Can(ProjectAction.ReadMetadata) || grant is null || !grant.Active || grant.Scope != scope || grant.PrincipalId != actor.PrincipalId)
                return false;
        }
        return true;
    }

    /// <summary>
    /// The grounding a new workspace turn pins, observed freshly as its creator: the payload manifest only when the
    /// profile allows payload data and the creator holds an active payload-read grant on a readable project of the
    /// organization; otherwise the payload tools are not offered at all. Null when the profile cannot run a turn.
    /// </summary>
    public async Task<ChatGroundingPolicyPin?> GroundingForAsync(ActorContext actor, Guid organizationId, ChatModelProfile profile,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(profile);
        if ((profile.Spec.Capabilities & AiCapability.FunctionTools) == 0 || !profile.Spec.MappingMetadataAllowed) return null;
        if (PayloadGrounding is { } payloads && profile.Spec.PayloadsAllowed &&
            await services.GetRequiredService<IProjectPayloadGrantPresence>().AnyAsync(actor, organizationId, ct))
            return payloads;
        return Grounding;
    }

    public Task<bool> ValidateAsync(ChatReadOnlyToolContext context, AiToolIntent intent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(intent);
        ct.ThrowIfCancellationRequested();
        if (!Bound(context) || ManifestOf(context.Turn) is not { } manifest) return Task.FromResult(false);
        try { _ = WorkspaceToolRegistry.Parse(intent.Name, intent.Arguments.Json, manifest.Tools); return Task.FromResult(true); }
        catch (WorkspaceToolRefusal) { return Task.FromResult(false); }
    }

    public async Task<ChatReadOnlyToolResult> ExecuteAsync(ChatReadOnlyToolContext context, AiToolIntent intent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(intent);
        ct.ThrowIfCancellationRequested();
        var budget = new Budget(Math.Min(WorkspaceChatGrounding.CallResultBytes, context.RemainingResultBytes), context.RemainingRetrievedNodes);
        if (!Bound(context) || ManifestOf(context.Turn) is not { } manifest)
            return Failure(intent.Name, WorkspaceToolOutcome.NotFound, budget);
        var admitted = false;
        CallScope? scope = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(intent.Name == WorkspaceToolRegistry.SaveEdit ? EditDuration : CallDuration);
        var token = deadline.Token;
        try
        {
            var call = WorkspaceToolRegistry.Parse(intent.Name, intent.Arguments.Json, manifest.Tools);
            scope = new CallScope(context.Actor, context.Conversation, context.Turn, budget, manifest.Payloads, intent.CallId, context.RoundId);
            await AdmitAsync(scope, token);
            if (!capacity.Wait(0)) return Failure(call.Name, WorkspaceToolOutcome.Busy, budget);
            admitted = true;
            var reply = await DispatchAsync(scope, call, token);
            // Preparation is not authority: the organization admission is observed again before release.
            await AdmitAsync(scope, token);
            token.ThrowIfCancellationRequested();
            return WithWrite(reply, scope);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { ct.ThrowIfCancellationRequested(); throw; }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        { ct.ThrowIfCancellationRequested(); return WithWrite(Failure(intent.Name, WorkspaceToolOutcome.TimedOut, budget), scope); }
        catch (WorkspaceToolRefusal refusal)
        { ct.ThrowIfCancellationRequested(); return WithWrite(Failure(intent.Name, refusal.Outcome, budget), scope); }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        { ct.ThrowIfCancellationRequested(); return WithWrite(Failure(intent.Name, WorkspaceToolOutcome.Unavailable, budget), scope); }
        finally { if (admitted) capacity.Release(); }
    }

    private Task<ChatReadOnlyToolResult> DispatchAsync(CallScope scope, WorkspaceToolCall call, CancellationToken token) => call.Name switch
    {
        "list_projects" => ListProjectsAsync(scope, call, token),
        "search_catalog" => SearchCatalogAsync(scope, call, token),
        "get_project" => GetProjectAsync(scope, call, token),
        "get_mapping" => GetMappingAsync(scope, call, token),
        "list_revisions" => ListRevisionsAsync(scope, call, token),
        "get_revision" => GetRevisionAsync(scope, call, token),
        "compare_revisions" => CompareRevisionsAsync(scope, call, token),
        "list_runs" => ListRunsAsync(scope, call, token),
        "get_run" => GetRunAsync(scope, call, token),
        "get_capabilities" => GetCapabilitiesAsync(scope, call, token),
        "search_user_guide" => SearchGuideAsync(scope, call, token),
        "get_user_guide_section" => GetGuideSectionAsync(scope, call, token),
        WorkspaceToolRegistry.SaveEdit => SaveEditAsync(scope, call, token),
        _ when WorkspaceToolRegistry.IsReportTool(call.Name) => ReportAsync(scope, call, token),
        _ when WorkspaceToolRegistry.IsPayloadTool(call.Name) && scope.Payloads => PayloadAsync(scope, call, token),
        _ => throw new WorkspaceToolRefusal(WorkspaceToolOutcome.InvalidCall)
    };

    /// <summary>Conversations are creator-private; another principal or a revision conversation never uses these tools.</summary>
    private static bool Bound(ChatReadOnlyToolContext context) =>
        context.Conversation.Key.Kind == ChatConversationKind.Workspace && context.Actor.PrincipalId == context.Conversation.Key.Creator;

    private async Task AdmitAsync(CallScope scope, CancellationToken token)
    {
        var admission = await Catalog.GetOrganizationAdmissionAsync(scope.Actor, scope.Organization, token);
        token.ThrowIfCancellationRequested();
        if (admission is null || admission.OrganizationId != scope.Organization || admission.PrincipalId != scope.Actor.PrincipalId)
            throw new WorkspaceToolRefusal(WorkspaceToolOutcome.NotFound);
    }

    private ICatalogReader Catalog => services.GetRequiredService<ICatalogReader>();
    private ICatalogResourceLocator Locator => services.GetRequiredService<ICatalogResourceLocator>();
    private T? Optional<T>() where T : class => services.GetService<T>();

    private async Task<MappingScope> LocateMappingAsync(CallScope scope, Guid mappingId, CancellationToken token) =>
        await Locator.LocateMappingAsync(scope.Actor, scope.Organization, mappingId, token) ?? throw new WorkspaceToolRefusal(WorkspaceToolOutcome.NotFound);

    private async Task<RevisionScope> LocateRevisionAsync(CallScope scope, Guid revisionId, CancellationToken token) =>
        await Locator.LocateRevisionAsync(scope.Actor, scope.Organization, revisionId, token) ?? throw new WorkspaceToolRefusal(WorkspaceToolOutcome.NotFound);

    /// <summary>
    /// Serializes the envelope within the call's byte budget. A list result keeps the longest prefix that fits and says
    /// so; a result that cannot fit even empty is refused whole. Literal values are never cropped.
    /// </summary>
    private static ChatReadOnlyToolResult Reply<TItem>(string tool, Budget budget, IReadOnlyList<TItem> items, bool truncated,
        Func<IReadOnlyList<TItem>, object> data, Func<TItem, IEnumerable<ChatWorkspaceCitationFact>> citations,
        ChatPayloadDisclosureFact? shared = null, int extraNodes = 0,
        IEnumerable<ChatWorkspaceCitationFact>? retainedCitations = null)
    {
        if (budget.Nodes < extraNodes) throw new WorkspaceToolRefusal(WorkspaceToolOutcome.ResourceLimit);
        var count = Math.Min(items.Count, Math.Max(0, budget.Nodes - extraNodes));
        truncated |= count < items.Count;
        while (true)
        {
            var kept = items.Take(count).ToArray();
            var bytes = Serialize(new Envelope(Schema, tool, WorkspaceToolOutcome.Available, truncated || count < items.Count, data(kept)));
            if (bytes.Length <= budget.Bytes)
            {
                var cited = (retainedCitations ?? []).Concat(kept.SelectMany(citations)).Distinct()
                    .Take(ChatReadOnlyToolResult.MaxCitationCandidates).ToArray();
                return new(AiJsonObject.CopyFrom(Encoding.UTF8.GetString(bytes)), kept.Length + extraNodes, workspaceCitations: cited, dataShared: shared);
            }
            if (count == 0) throw new WorkspaceToolRefusal(WorkspaceToolOutcome.ResourceLimit);
            count = count == 1 ? 0 : count / 2;
        }
    }

    private static ChatReadOnlyToolResult Single(string tool, Budget budget, object data, IEnumerable<ChatWorkspaceCitationFact> citations,
        ChatPayloadDisclosureFact? shared = null)
    {
        if (budget.Nodes < 1) throw new WorkspaceToolRefusal(WorkspaceToolOutcome.ResourceLimit);
        return Reply(tool, budget, [0], false, _ => data, _ => citations, shared, extraNodes: 0);
    }

    private static ChatReadOnlyToolResult Outcome(string tool, Budget budget, WorkspaceToolOutcome outcome, object? data,
        IEnumerable<ChatWorkspaceCitationFact>? citations = null)
    {
        var bytes = Serialize(new Envelope(Schema, tool, outcome, false, data));
        if (bytes.Length > budget.Bytes) return Failure(tool, WorkspaceToolOutcome.ResourceLimit, budget);
        return new(AiJsonObject.CopyFrom(Encoding.UTF8.GetString(bytes)), 0,
            workspaceCitations: (citations ?? []).Take(ChatReadOnlyToolResult.MaxCitationCandidates));
    }

    /// <summary>Fixed categorical vocabulary only: never arguments, catalog text or exception details.</summary>
    private static ChatReadOnlyToolResult Failure(string tool, WorkspaceToolOutcome outcome, Budget budget)
    {
        var name = tool is { Length: > 0 and <= 64 } && tool.All(c => char.IsAsciiLetterLower(c) || c == '_') ? tool : "unknown";
        var bytes = Serialize(new Envelope(Schema, name, outcome, false, null));
        return new(AiJsonObject.CopyFrom(Encoding.UTF8.GetString(bytes)), 0);
    }

    private static byte[] Serialize(object value) => JsonSerializer.SerializeToUtf8Bytes(value, Json);

    internal static string Id(Guid value) => value.ToString("D");

    private sealed record Envelope(string Schema, string Tool, WorkspaceToolOutcome Outcome, bool Truncated, object? Data)
    {
        public override string ToString() => nameof(Envelope);
    }

    private readonly record struct Budget(int Bytes, int Nodes);

    private sealed record CallScope(ActorContext Actor, ChatConversationIntent Conversation, ChatTurnIntent? Turn, Budget Budget,
        bool Payloads = false, string? CallId = null, Guid? RoundId = null)
    {
        internal Guid Organization => Conversation.Key.OrganizationId;
        /// <summary>The mapping write or proposal this call made, kept so no later refusal can hide it.</summary>
        internal ChatMappingWriteFact? Written { get; set; }
        internal Guid? SaveKey { get; set; }
        public override string ToString() => nameof(CallScope);
    }

    public override string ToString() => nameof(WorkspaceToolService);
}
