using System.Text.Json;
using NpgsqlTypes;
using Braid.Application;
using Braid.Domain;

namespace Braid.Persistence.Postgres;

public sealed partial class PostgresChatStore
{
    private async Task<ChatStoreOutcome> InsertQuotaTurnAsync(PostgresTransaction tx, ActorContext actor,
        ChatConversationKey key, ChatTurnIntent intent, string canonical, CancellationToken ct)
    {
        // A trusted local authority runs only after original-turn replay. It never
        // dispatches a model call to estimate usage, or accepts a caller's estimate.
        var bound = await _tokenBounds.BoundAsync(actor, key, intent, ct);
        if (bound is not null && bound.Request != AiTokenAdmissionRequest.From(key, intent))
            return ChatStoreOutcome.QuotaTokenBoundUnavailable;
        await using var command = Command(tx, """
            WITH quota AS MATERIALIZED (
                SELECT braid.reserve_ai_quota(@org,@creator,@conversation,@turn,@envelope,@fingerprint,@deadline,@maximum,@authority,@authorityVersion) AS result
            ), inserted AS (
                INSERT INTO braid.chat_turns(organization_id,creator_id,conversation_id,turn_id,original_request,request_id,original_envelope,canonical_envelope,envelope_fingerprint,profile_id,profile_version,
                    provider_id,model_id,metadata_policy_id,metadata_policy_version,profile_fingerprint,max_events,max_event_bytes,max_rounds,deadline)
                SELECT @org,@creator,@conversation,@turn,@request,@requestId,@originalEnvelope,@canonicalEnvelope,@envelopeFingerprint,@profile,@pv,@provider,@model,@policy,@mpv,@fingerprint,@events,@bytes,@rounds,@deadline
                    FROM quota WHERE result->>'outcome'='Applied'
                RETURNING turn_id
            ) SELECT result::text,(SELECT count(*) FROM inserted) FROM quota
            """, key, intent.TurnId);
        Add(command, "envelope", NpgsqlDbType.Text, intent.Envelope.Fingerprint.Value);
        Add(command, "fingerprint", NpgsqlDbType.Text, intent.Profile.ConfigurationFingerprint.Value);
        Add(command, "deadline", NpgsqlDbType.TimestampTz, intent.DeadlineUtc);
        Add(command, "maximum", NpgsqlDbType.Bigint, bound?.MaximumTokens);
        Add(command, "authority", NpgsqlDbType.Text, bound?.AuthorityId);
        Add(command, "authorityVersion", NpgsqlDbType.Integer, bound?.AuthorityVersion);
        Add(command, "request", NpgsqlDbType.Text, intent.OriginalRequest);
        Add(command, "requestId", NpgsqlDbType.Uuid, intent.Envelope.RequestId); Add(command, "originalEnvelope", NpgsqlDbType.Text, intent.Envelope.CanonicalJson);
        Add(command, "canonicalEnvelope", NpgsqlDbType.Text, canonical); Add(command, "envelopeFingerprint", NpgsqlDbType.Text, ChatCanonicalEnvelope.Fingerprint(canonical).Value);
        Add(command, "profile", NpgsqlDbType.Text, intent.Profile.ProfileId); Add(command, "pv", NpgsqlDbType.Integer, intent.Profile.Version);
        Add(command, "provider", NpgsqlDbType.Text, intent.Profile.ProviderId); Add(command, "model", NpgsqlDbType.Text, intent.Profile.ModelId);
        Add(command, "policy", NpgsqlDbType.Text, intent.Profile.MetadataPolicyId); Add(command, "mpv", NpgsqlDbType.Integer, intent.Profile.MetadataPolicyVersion);
        Add(command, "events", NpgsqlDbType.Integer, intent.Budget.MaxEvents); Add(command, "bytes", NpgsqlDbType.Integer, intent.Budget.MaxEventBytes);
        Add(command, "rounds", NpgsqlDbType.Integer, intent.Budget.MaxRounds);
        await using var rows = await command.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct) || rows.IsDBNull(0)) throw Invalid();
        var text = rows.GetString(0);
        if (text.Length > 1024) throw Invalid();
        using var document = JsonDocument.Parse(text);
        var value = document.RootElement;
        if (!Enum.TryParse<ChatStoreOutcome>(value.GetProperty("outcome").GetString(), out var outcome) || outcome is not
            (ChatStoreOutcome.Applied or ChatStoreOutcome.NotAuthorizedOrNotFound or ChatStoreOutcome.Expired or ChatStoreOutcome.Conflict
            or ChatStoreOutcome.QuotaTurnLimitReached or ChatStoreOutcome.QuotaTokenLimitReached
            or ChatStoreOutcome.QuotaTokenBoundUnavailable or ChatStoreOutcome.QuotaCoverageUnavailable)) throw Invalid();
        if (outcome == ChatStoreOutcome.Applied) ChatBounds.Instant(value.GetProperty("admittedAt").GetDateTimeOffset());
        if (rows.GetInt64(1) != (outcome == ChatStoreOutcome.Applied ? 1 : 0) || await rows.ReadAsync(ct)) throw Invalid();
        return outcome;
    }
}
