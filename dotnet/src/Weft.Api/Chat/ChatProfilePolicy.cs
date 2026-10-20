using Weft.Ai;
using Weft.Application;
using Weft.Domain;

namespace Weft.Api.Chat;

/// <summary>
/// Current trusted destination policy: the exact configured selectable profile, plus fresh catalog
/// project/revision read authority. Rechecked by the orchestrator before each provider and tool boundary.
/// </summary>
internal sealed class ChatProfilePolicy(ChatApiOptions options, ICatalogReader catalog) : IChatProfilePolicy
{
    public async Task<bool> AllowsAsync(ActorContext actor, ChatConversationIntent conversation, ChatTurnIntent turn,
        AiProfileSpec profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentNullException.ThrowIfNull(profile);
        var configured = options.Find(profile.Key.Id, profile.Key.Version);
        if (configured is null || !ReferenceEquals(configured.Spec, profile) || configured.Pin != turn.Profile ||
            actor.PrincipalId != conversation.Key.Creator)
            return false;
        var scope = conversation.Key.Revision;
        var revision = await catalog.GetRevisionAsync(actor, scope, ct).ConfigureAwait(false);
        return revision is not null && revision.Summary.Scope == scope &&
            revision.Access.Scope == new ProjectScope(scope.OrganizationId, scope.ProjectId) &&
            revision.Access.PrincipalId == actor.PrincipalId && revision.Access.Can(ProjectAction.ReadMetadata);
    }

    public override string ToString() => nameof(ChatProfilePolicy);
}
