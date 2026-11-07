using Weft.Ai;
using Weft.Application;
using Weft.Domain;

namespace Weft.Api.Chat;

/// <summary>
/// Current trusted destination policy: the exact configured selectable profile, plus fresh catalog
/// project/revision read authority, or for a workspace conversation fresh organization admission. Rechecked
/// by the orchestrator before each provider and tool boundary; workspace tools recheck their own resources.
/// </summary>
internal sealed class ChatProfilePolicy(ChatApiOptions options, ICatalogReader catalog,
    Func<ActorContext, Guid, ChatModelProfile, ChatTurnIntent, CancellationToken, Task<bool>>? workspace = null) : IChatProfilePolicy
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
        if (conversation.Key.Revision is not { } scope)
        {
            var admission = await catalog.GetOrganizationAdmissionAsync(actor, conversation.Key.OrganizationId, ct).ConfigureAwait(false);
            return admission is not null && admission.OrganizationId == conversation.Key.OrganizationId &&
                admission.PrincipalId == actor.PrincipalId && workspace is not null &&
                await workspace(actor, conversation.Key.OrganizationId, configured, turn, ct).ConfigureAwait(false);
        }
        var revision = await catalog.GetRevisionAsync(actor, scope, ct).ConfigureAwait(false);
        return revision is not null && revision.Summary.Scope == scope &&
            revision.Access.Scope == new ProjectScope(scope.OrganizationId, scope.ProjectId) &&
            revision.Access.PrincipalId == actor.PrincipalId && revision.Access.Can(ProjectAction.ReadMetadata);
    }

    public override string ToString() => nameof(ChatProfilePolicy);
}
