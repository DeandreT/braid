using System.Globalization;
using System.Text;
using System.Text.Json;
using Braid.Domain;

namespace Braid.Application;

/// <summary>Canonical v1 identity of every immutable admitted input, not just the user text.</summary>
public static class ChatCanonicalEnvelope
{
    public static string Turn(ChatConversationIntent conversation, ChatTurnIntent turn) => ChatCanonicalJson.Write(w =>
    {
        var key = conversation.Key;
        // A workspace turn always records its resolved focus; a revision turn never has one.
        if ((key.Kind == ChatConversationKind.Workspace) != (turn.Envelope.Focus is not null)) throw ChatBounds.Invalid();
        w.WriteStartObject();
        if (key.Revision is { } scope)
        {
            w.WriteString("schema", "braid.chat.turn.v1");
            w.WriteString("organization", scope.OrganizationId); w.WriteString("creator", key.Creator.Value);
            w.WriteString("project", scope.ProjectId); w.WriteString("mapping", scope.MappingId); w.WriteString("revision", scope.RevisionId);
            w.WriteString("conversation", key.ConversationId); w.WriteString("retainUntil", conversation.RetainUntilUtc.ToString("O", CultureInfo.InvariantCulture));
            w.WritePropertyName("projection"); WritePin(w, conversation.PinnedProjection); w.WritePropertyName("validation"); WritePin(w, conversation.PinnedValidation);
        }
        else
        {
            w.WriteString("schema", "braid.chat.workspace-turn.v1");
            w.WriteString("organization", key.OrganizationId); w.WriteString("creator", key.Creator.Value);
            w.WriteString("conversation", key.ConversationId); w.WriteString("retainUntil", conversation.RetainUntilUtc.ToString("O", CultureInfo.InvariantCulture));
            w.WriteStartObject("focus");
            if (conversation.Focus is { } focus)
            {
                w.WriteString("project", focus.Mapping.ProjectId); w.WriteString("mapping", focus.Mapping.MappingId);
                if (focus.RevisionId is { } revision) w.WriteString("revision", revision); else w.WriteNull("revision");
            }
            w.WriteEndObject();
        }
        w.WriteString("turn", turn.TurnId); w.WritePropertyName("original"); w.WriteRawValue(turn.Envelope.CanonicalJson);
        w.WriteStartObject("profile"); w.WriteString("id", turn.Profile.ProfileId); w.WriteNumber("version", turn.Profile.Version);
        w.WriteString("provider", turn.Profile.ProviderId); w.WriteString("model", turn.Profile.ModelId);
        w.WriteString("metadataPolicy", turn.Profile.MetadataPolicyId); w.WriteNumber("metadataPolicyVersion", turn.Profile.MetadataPolicyVersion);
        w.WriteString("fingerprintSchema", turn.Profile.FingerprintSchema);
        w.WriteString("fingerprint", turn.Profile.ConfigurationFingerprint.Value); w.WriteEndObject();
        w.WriteStartObject("persistenceBudget"); w.WriteNumber("events", turn.Budget.MaxEvents); w.WriteNumber("bytes", turn.Budget.MaxEventBytes);
        w.WriteNumber("rounds", turn.Budget.MaxRounds); w.WriteEndObject();
        w.WriteString("deadline", turn.DeadlineUtc.ToString("O", CultureInfo.InvariantCulture)); w.WriteEndObject();
    }, 1048576);
    public static Sha256Digest Fingerprint(string canonical) => Sha256Digest.FromBytes(Encoding.UTF8.GetBytes(canonical));
    private static void WritePin(Utf8JsonWriter w, ChatReportPin pin)
    {
        w.WriteStartObject(); w.WriteString("entry", pin.CacheEntryId); w.WriteString("attempt", pin.DerivationId);
        w.WriteString("key", pin.KeyFingerprint.Value); w.WriteString("digest", pin.ObjectDigest.Value); w.WriteEndObject();
    }
}
