namespace Braid.Persistence.Postgres;

internal static partial class ChatSchemaContract
{
    internal const string EditProposalMigrationName = "0043_chat_edit_proposals";
    internal const string EditProposalMigrationChecksum = "cc4aaaf412e827a9a235d56dc781c4a4c06359a8b53c2852bc62670b31b0c6c7";
    private static readonly Lazy<string> EditProposalSource = new(() => LoadSource(EditProposalMigrationName, EditProposalMigrationChecksum));

    private static Table EditProposals => new("chat_edit_proposals",
        [.. Uuids("organization_id", "creator_id", "proposal_id", "request_key", "conversation_id", "turn_id", "round_id"),
         new("call_id", "text", C: true), .. Uuids("project_id", "mapping_id", "base_revision_id"), new("base_sequence", "bigint"),
         new("fingerprint", "text"), new("identity_facts", "text", C: true), new("generation_id", "uuid"),
         new("generation_provenance", "text", C: true), new("recorded_at", "timestamp with time zone")],
        [Key("chat_edit_proposals_pkey", "organization_id", "creator_id", "proposal_id"),
         Unique("chat_edit_proposals_request_key", "organization_id", "creator_id", "request_key"),
         Unique("chat_edit_proposals_call_key", "organization_id", "creator_id", "conversation_id", "turn_id", "round_id", "call_id")]);

    private static Table EditContent => new("chat_edit_content",
        [.. Uuids("organization_id", "creator_id", "proposal_id", "conversation_id"), new("encoding", "bytea"), new("native", "bytea"),
         new("journal_proposal", "text", C: true)],
        [Key("chat_edit_content_pkey", "organization_id", "creator_id", "proposal_id"),
         Foreign("chat_edit_content_conversation_fkey", ["organization_id", "creator_id", "conversation_id"], "chat_conversations",
             ["organization_id", "creator_id", "conversation_id"], "c")]);

    private static Table EditApprovals => new("chat_edit_approvals",
        [.. Uuids("organization_id", "creator_id", "proposal_id", "request_key"), new("fingerprint", "text"), new("approved_at", "timestamp with time zone")],
        [Key("chat_edit_approvals_pkey", "organization_id", "creator_id", "proposal_id")]);
}
