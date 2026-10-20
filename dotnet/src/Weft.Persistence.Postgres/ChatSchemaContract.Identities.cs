namespace Weft.Persistence.Postgres;

internal static partial class ChatSchemaContract
{
    // Authored expected deparse forms, not adopted target state. As with the
    // predecessor descriptors, pristine actual-PG admission remains a held gate.
    private static Table Identities => new("chat_identities",
        [.. Uuids("organization_id", "creator_id"), new("identity_kind", "integer"), .. Uuids("identity_id", "conversation_id"),
          new("turn_id", "uuid", NotNull: false), new("erased_at", "timestamp with time zone", NotNull: false),
          new("retirement_policy_id", "text", NotNull: false), new("retirement_policy_version", "integer", NotNull: false),
          new("retirement_not_before", "timestamp with time zone", NotNull: false)],
        [Key("chat_identities_pkey", "organization_id", "creator_id", "identity_kind", "identity_id"),
          Check("chat_identities_identity_kind_check", "((identity_kind >= 1) AND (identity_kind <= 4))"),
          Check("chat_identities_identity_id_check", IdCheck("identity_id")), Check("chat_identities_conversation_id_check", IdCheck("conversation_id")),
          Check("chat_identities_scope_check", "((organization_id <> '00000000-0000-0000-0000-000000000000'::uuid) AND (creator_id <> '00000000-0000-0000-0000-000000000000'::uuid))"),
          Check("chat_identities_association_check", "(((identity_kind = 1) AND (turn_id IS NULL) AND (identity_id = conversation_id)) OR ((identity_kind = ANY (ARRAY[2, 3, 4])) AND (turn_id IS NOT NULL) AND (turn_id <> '00000000-0000-0000-0000-000000000000'::uuid) AND ((identity_kind <> 2) OR (identity_id = turn_id))))"),
          Check("chat_identities_erasure_check", "(((erased_at IS NULL) AND (retirement_policy_id IS NULL) AND (retirement_policy_version IS NULL) AND (retirement_not_before IS NULL)) OR ((erased_at IS NOT NULL) AND isfinite(erased_at) AND (erased_at > '0001-01-01 00:00:00+00'::timestamp with time zone) AND (retirement_policy_id IS NOT NULL) AND (retirement_policy_id ~ '^[a-z0-9][a-z0-9._-]{0,63}$'::text) AND (retirement_policy_version IS NOT NULL) AND (retirement_policy_version > 0) AND (retirement_not_before IS NOT NULL) AND isfinite(retirement_not_before) AND (retirement_not_before > erased_at) AND (retirement_not_before <= '9999-12-31 23:59:59.999999+00'::timestamp with time zone)))")]);
}
