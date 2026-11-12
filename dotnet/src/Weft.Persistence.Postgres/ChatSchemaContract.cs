using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Weft.Persistence.Postgres;

// Source-prepared exact contract, not a capture adopted from a target database.
// The provisional slot and canonical deparse strings must be qualified after P4.
internal static partial class ChatSchemaContract
{
    internal const string MigrationName = "0011_private_chat";
    internal const string MigrationChecksum = "a563ed67fcc29f46aa1b3121ba826767631631a6ff74859d7664057047dbf7dc";
    // The workspace migration adds the organization-scoped kind, its events and the payload audit, and
    // replaces exactly the routines it names; every other routine body remains 0011's.
    internal const string WorkspaceMigrationName = "0032_workspace_chat";
    internal const string WorkspaceMigrationChecksum = "a4e8e24cf0a8fd38a23cc604d0077ad7c1345692686a51a952d90c767ff85d80";
    internal static int WorkspaceMigrationVersion => int.Parse(WorkspaceMigrationName[..4], System.Globalization.CultureInfo.InvariantCulture);
    // The mapping write migration adds the write event and its audit, and replaces exactly the routines it names.
    internal const string MappingWriteMigrationName = "0039_chat_mapping_writes";
    internal const string MappingWriteMigrationChecksum = "9f0fa1e0799d540d99d14417be21576b4265bfca77262a7c96b9f04b7dca3b26";
    internal static int MappingWriteMigrationVersion => int.Parse(MappingWriteMigrationName[..4], System.Globalization.CultureInfo.InvariantCulture);
    internal const string WriteJournalMigrationName = "0040_chat_mapping_write_journal";
    internal const string WriteJournalMigrationChecksum = "64dbd178aa7a9d5cd902b4b165cddbf095df13af9178b6d38e5963729e3399ef";
    internal static int WriteJournalMigrationVersion => int.Parse(WriteJournalMigrationName[..4], System.Globalization.CultureInfo.InvariantCulture);
    internal static readonly string[] ChatTables = ["chat_conversations", "chat_turns", "chat_rounds", "chat_events", "chat_identities", "chat_payload_audit", "chat_mapping_write_audit", "chat_mapping_write_intents", "chat_mapping_write_content", "chat_mapping_write_commits", "chat_edit_proposals", "chat_edit_content", "chat_edit_approvals"];
    private static readonly string[] Dependencies =
        ["principals", "organization_memberships", "project_memberships", "mappings", "mapping_revisions", "revision_ingestions", "revision_derivations", "revision_derivation_attempts"];
    private const string SearchPath = "search_path=pg_catalog, weft, pg_temp";
    private sealed record Column(string Name, string Type, bool NotNull = true, string? Default = null, bool C = false);
    private sealed record Constraint(string Name, char Kind, string[] Columns, string? Target = null,
        string[]? TargetColumns = null, string Delete = "r", string Update = "r", bool Deferred = false, string? Check = null);
    private sealed record Table(string Name, Column[] Columns, Constraint[] Constraints);
    private static Column[] Uuids(params string[] names) => names.Select(name => new Column(name, "uuid")).ToArray();
    private static Constraint Key(string name, params string[] columns) => new(name, 'p', columns);
    private static Constraint Unique(string name, params string[] columns) => new(name, 'u', columns);
    private static Constraint Foreign(string name, string[] columns, string target, string[] targetColumns, string delete = "r") =>
        new(name, 'f', columns, target, targetColumns, delete);
    private static Constraint Check(string name, string expression) => new(name, 'c', [], Check: "CHECK (" + expression + ")");
    private static string IdCheck(string name) => "(" + name + " <> '00000000-0000-0000-0000-000000000000'::uuid)";
    private static string DigestCheck(string name) => "(" + name + " ~ '^[0-9a-f]{64}$'::text)";
    private static string IdentifierCheck(string name, int limit) =>
        "(((octet_length(" + name + ") >= 1) AND (octet_length(" + name + ") <= " + limit + ")) AND (" + name + " ~ '^[a-z0-9][a-z0-9._-]*$'::text))";
    private static string TextCheck(string name, int limit) =>
        "(((octet_length(" + name + ") >= 1) AND (octet_length(" + name + ") <= " + limit + ")) AND (btrim(" + name + ") <> ''::text))";

    private static Column[] OptionalUuids(params string[] names) => names.Select(name => new Column(name, "uuid", NotNull: false)).ToArray();
    private const string PinColumns = "project_id, mapping_id, revision_id, projection_entry_id, projection_id, projection_key, projection_digest, validation_entry_id, validation_id, validation_key, validation_digest";

    // The workspace migration relaxes the revision pin columns, appends kind/focus and constrains each kind's shape.
    private static readonly Table Conversations = new("chat_conversations",
        [.. Uuids("organization_id", "creator_id", "conversation_id"),
         .. OptionalUuids("project_id", "mapping_id", "revision_id", "projection_entry_id", "projection_id"),
         new("projection_key", "text", NotNull: false), new("projection_digest", "text", NotNull: false),
         .. OptionalUuids("validation_entry_id", "validation_id"),
         new("validation_key", "text", NotNull: false), new("validation_digest", "text", NotNull: false),
         new("created_at", "timestamp with time zone", Default: "statement_timestamp()"), new("retain_until", "timestamp with time zone"),
         new("conversation_kind", "integer"), .. OptionalUuids("focus_project_id", "focus_mapping_id", "focus_revision_id")],
        [Key("chat_conversations_pkey", "organization_id", "creator_id", "conversation_id"),
         Foreign("chat_conversations_creator_fkey", ["creator_id"], "principals", ["id"]),
         Foreign("chat_conversations_focus_mapping_fkey", ["organization_id", "focus_project_id", "focus_mapping_id"], "mappings", ["organization_id", "project_id", "id"]),
         Foreign("chat_conversations_focus_revision_fkey", ["organization_id", "focus_project_id", "focus_mapping_id", "focus_revision_id"], "mapping_revisions", ["organization_id", "project_id", "mapping_id", "id"]),
         Check("chat_conversations_kind_check", "(((conversation_kind = 1) AND (num_nulls(" + PinColumns + ") = 0) AND (num_nonnulls(focus_project_id, focus_mapping_id, focus_revision_id) = 0)) OR ((conversation_kind = 2) AND (num_nonnulls(" + PinColumns + ") = 0) AND ((focus_project_id IS NULL) = (focus_mapping_id IS NULL)) AND ((focus_revision_id IS NULL) OR (focus_mapping_id IS NOT NULL))))"),
         Foreign("chat_conversations_revision_fkey", ["organization_id", "project_id", "mapping_id", "revision_id"], "mapping_revisions", ["organization_id", "project_id", "mapping_id", "id"]),
         Foreign("chat_conversations_projection_fkey", ["organization_id", "project_id", "mapping_id", "revision_id", "projection_id"], "revision_derivation_attempts", ["organization_id", "project_id", "mapping_id", "revision_id", "derivation_id"]),
         Foreign("chat_conversations_validation_fkey", ["organization_id", "project_id", "mapping_id", "revision_id", "validation_id"], "revision_derivation_attempts", ["organization_id", "project_id", "mapping_id", "revision_id", "derivation_id"]),
         Check("chat_conversations_conversation_id_check", IdCheck("conversation_id")),
         Check("chat_conversations_projection_key_check", DigestCheck("projection_key")), Check("chat_conversations_projection_digest_check", DigestCheck("projection_digest")),
         Check("chat_conversations_validation_key_check", DigestCheck("validation_key")), Check("chat_conversations_validation_digest_check", DigestCheck("validation_digest")),
         Check("chat_conversations_check", "(projection_id <> validation_id)"),
         Check("chat_conversations_check1", "(isfinite(created_at) AND isfinite(retain_until) AND (created_at > '0001-01-01 00:00:00+00'::timestamp with time zone) AND (retain_until > created_at) AND (retain_until <= '9999-12-31 23:59:59.999999+00'::timestamp with time zone))")]);

    private static readonly Table Turns = new("chat_turns",
        [.. Uuids("organization_id", "creator_id", "conversation_id", "turn_id"), new("original_request", "text", C: true),
          new("request_id", "uuid"), new("original_envelope", "text", C: true), new("canonical_envelope", "text", C: true), new("envelope_fingerprint", "text"),
         new("profile_id", "text"), new("profile_version", "integer"), new("provider_id", "text"), new("model_id", "text"),
         new("metadata_policy_id", "text"), new("metadata_policy_version", "integer"), new("profile_fingerprint", "text"),
         new("max_events", "integer"), new("max_event_bytes", "integer"), new("max_rounds", "integer"), new("deadline", "timestamp with time zone"),
         new("created_at", "timestamp with time zone", Default: "statement_timestamp()"), new("phase", "integer", Default: "1"), new("version", "bigint", Default: "1"),
         new("event_count", "integer", Default: "1"), new("event_bytes", "integer", Default: "0"), new("round_count", "integer", Default: "0"),
         new("finished_at", "timestamp with time zone", NotNull: false)],
        [Key("chat_turns_pkey", "organization_id", "creator_id", "conversation_id", "turn_id"),
          Unique("chat_turns_identity_key", "organization_id", "creator_id", "turn_id"), Unique("chat_turns_request_key", "organization_id", "creator_id", "request_id"),
         Foreign("chat_turns_conversation_fkey", ["organization_id", "creator_id", "conversation_id"], "chat_conversations", ["organization_id", "creator_id", "conversation_id"], "c"),
          Check("chat_turns_turn_id_check", IdCheck("turn_id")), Check("chat_turns_original_request_check", TextCheck("original_request", 65536)),
          Check("chat_turns_request_id_check", IdCheck("request_id")),
          Check("chat_turns_original_envelope_check", "((octet_length(original_envelope) >= 1) AND (octet_length(original_envelope) <= 524288))"),
          Check("chat_turns_canonical_envelope_check", "((octet_length(canonical_envelope) >= 1) AND (octet_length(canonical_envelope) <= 1048576))"),
          Check("chat_turns_envelope_fingerprint_check", "(envelope_fingerprint = encode(sha256(convert_to(canonical_envelope, 'UTF8'::name)), 'hex'::text))"),
         Check("chat_turns_profile_id_check", IdentifierCheck("profile_id", 64)), Check("chat_turns_profile_version_check", "(profile_version > 0)"),
         Check("chat_turns_provider_id_check", IdentifierCheck("provider_id", 64)), Check("chat_turns_model_id_check", TextCheck("model_id", 256)),
         Check("chat_turns_metadata_policy_id_check", IdentifierCheck("metadata_policy_id", 64)), Check("chat_turns_metadata_policy_version_check", "(metadata_policy_version > 0)"),
         Check("chat_turns_profile_fingerprint_check", DigestCheck("profile_fingerprint")),
         Check("chat_turns_max_events_check", "((max_events >= 2) AND (max_events <= 4096))"),
         Check("chat_turns_max_event_bytes_check", "((max_event_bytes >= 1) AND (max_event_bytes <= 4194304))"),
           Check("chat_turns_max_rounds_check", "((max_rounds >= 1) AND (max_rounds <= 5))"),
         Check("chat_turns_phase_check", "((phase >= 1) AND (phase <= 5))"), Check("chat_turns_version_check", "(version > 0)"),
         Check("chat_turns_check", "(((event_count >= 1) AND (event_count <= max_events)) AND ((event_bytes >= 0) AND (event_bytes <= max_event_bytes)) AND ((round_count >= 0) AND (round_count <= max_rounds)))"),
         Check("chat_turns_check1", "(isfinite(created_at) AND isfinite(deadline) AND (created_at > '0001-01-01 00:00:00+00'::timestamp with time zone) AND (created_at < deadline) AND (deadline <= '9999-12-31 23:59:59.999999+00'::timestamp with time zone))"),
         Check("chat_turns_check2", "(((phase = 1) AND (finished_at IS NULL)) OR ((phase <> 1) AND (finished_at IS NOT NULL) AND isfinite(finished_at) AND (finished_at >= created_at) AND (finished_at <= '9999-12-31 23:59:59.999999+00'::timestamp with time zone)))")]);

    private static readonly Table Rounds = new("chat_rounds",
        [.. Uuids("organization_id", "creator_id", "conversation_id", "turn_id", "round_id"), new("ordinal", "integer"), .. Uuids("fence", "instance_id"),
          new("exact_request", "text", C: true), new("result_metadata", "text", NotNull: false, C: true), new("outcome", "integer", Default: "1"),
         new("reserved_at", "timestamp with time zone", Default: "statement_timestamp()"), new("finished_at", "timestamp with time zone", NotNull: false)],
        [Key("chat_rounds_pkey", "organization_id", "creator_id", "conversation_id", "turn_id", "round_id"),
          Unique("chat_rounds_identity_key", "organization_id", "creator_id", "round_id"),
         Unique("chat_rounds_ordinal_key", "organization_id", "creator_id", "conversation_id", "turn_id", "ordinal"),
         Foreign("chat_rounds_turn_fkey", ["organization_id", "creator_id", "conversation_id", "turn_id"], "chat_turns", ["organization_id", "creator_id", "conversation_id", "turn_id"], "c"),
           Check("chat_rounds_round_id_check", IdCheck("round_id")), Check("chat_rounds_ordinal_check", "((ordinal >= 1) AND (ordinal <= 5))"),
         Check("chat_rounds_fence_check", IdCheck("fence")), Check("chat_rounds_instance_id_check", IdCheck("instance_id")),
          Check("chat_rounds_exact_request_check", TextCheck("exact_request", 2097152)), Check("chat_rounds_outcome_check", "((outcome >= 1) AND (outcome <= 4))"),
          Check("chat_rounds_result_metadata_check", "((result_metadata IS NULL) OR ((octet_length(result_metadata) >= 1) AND (octet_length(result_metadata) <= 2048)))"),
         Check("chat_rounds_reserved_at_check", "(isfinite(reserved_at) AND (reserved_at > '0001-01-01 00:00:00+00'::timestamp with time zone) AND (reserved_at <= '9999-12-31 23:59:59.999999+00'::timestamp with time zone))"),
         Check("chat_rounds_check", "(((outcome = 1) AND (finished_at IS NULL)) OR ((outcome <> 1) AND (finished_at IS NOT NULL) AND isfinite(finished_at) AND (finished_at >= reserved_at) AND (finished_at <= '9999-12-31 23:59:59.999999+00'::timestamp with time zone)))")]);

    private static readonly Table Events = new("chat_events",
        [.. Uuids("organization_id", "creator_id", "conversation_id", "turn_id"), new("sequence", "integer"), new("event_id", "uuid"),
          new("kind", "integer"), new("private_text", "text", C: true), new("round_id", "uuid", NotNull: false), new("dispatch_fence", "uuid", NotNull: false),
          new("metadata_json", "text", NotNull: false, C: true)],
        [Key("chat_events_pkey", "organization_id", "creator_id", "conversation_id", "turn_id", "sequence"),
         Unique("chat_events_event_id_key", "organization_id", "creator_id", "conversation_id", "turn_id", "event_id"),
         Foreign("chat_events_turn_fkey", ["organization_id", "creator_id", "conversation_id", "turn_id"], "chat_turns", ["organization_id", "creator_id", "conversation_id", "turn_id"], "c"),
         Foreign("chat_events_round_fkey", ["organization_id", "creator_id", "conversation_id", "turn_id", "round_id"], "chat_rounds", ["organization_id", "creator_id", "conversation_id", "turn_id", "round_id"], "c"),
         Check("chat_events_sequence_check", "((sequence >= 1) AND (sequence <= 4096))"), Check("chat_events_event_id_check", IdCheck("event_id")),
          Check("chat_events_kind_check", "((kind >= 1) AND (kind <= 15))"),
          Check("chat_events_metadata_json_check", "((metadata_json IS NULL) OR ((octet_length(metadata_json) >= 1) AND (octet_length(metadata_json) <= 8192)))"),
          Check("chat_events_check", "(((kind = ANY (ARRAY[2, 3, 4])) AND (round_id IS NOT NULL) AND (dispatch_fence IS NOT NULL) AND (metadata_json IS NULL) AND ((octet_length(private_text) >= 1) AND (octet_length(private_text) <= 8192)) AND (btrim(private_text) <> ''::text)) OR ((kind = ANY (ARRAY[9, 10, 11, 12, 13, 14, 15])) AND (round_id IS NOT NULL) AND (dispatch_fence IS NOT NULL) AND (metadata_json IS NOT NULL) AND (private_text = ''::text)) OR ((kind = 1) AND (round_id IS NULL) AND (dispatch_fence IS NULL) AND (private_text = ''::text) AND (metadata_json IS NULL)) OR ((kind = ANY (ARRAY[5, 6, 7, 8])) AND (round_id IS NULL) AND (dispatch_fence IS NULL) AND (private_text = ''::text) AND ((metadata_json IS NULL) OR (octet_length(metadata_json) <= 2048))))")]);

    private static readonly Table PayloadAudit = new("chat_payload_audit",
        [.. Uuids("organization_id", "creator_id", "event_id", "conversation_id", "turn_id", "round_id", "project_id"),
         new("source_kind", "integer"), new("subject_id", "uuid"), new("subject_version", "bigint", NotNull: false), new("byte_count", "integer"),
         new("profile_id", "text"), new("profile_version", "integer"), new("provider_id", "text"), new("model_id", "text"),
         new("metadata_policy_id", "text"), new("metadata_policy_version", "integer"), new("recorded_at", "timestamp with time zone")],
        [Key("chat_payload_audit_pkey", "organization_id", "creator_id", "event_id"),
         Check("chat_payload_audit_source_kind_check", "((source_kind >= 1) AND (source_kind <= 5))"),
         Check("chat_payload_audit_byte_count_check", "((byte_count >= 1) AND (byte_count <= 1048576))"),
         Check("chat_payload_audit_subject_check", "(((source_kind = 3) = (subject_version IS NOT NULL)) AND ((subject_version IS NULL) OR (subject_version > 0)))")]);

    private static readonly Table MappingWriteAudit = new("chat_mapping_write_audit",
        [.. Uuids("organization_id", "creator_id", "event_id", "conversation_id", "turn_id", "round_id", "project_id", "mapping_id", "base_revision_id"),
         new("base_sequence", "bigint"), new("revision_id", "uuid", NotNull: false), new("revision_sequence", "bigint", NotNull: false),
         new("outcome", "integer"), new("reason", "integer", NotNull: false), new("operation_count", "integer"), new("edit_digest", "text"),
         new("profile_id", "text"), new("profile_version", "integer"), new("provider_id", "text"), new("model_id", "text"),
         new("metadata_policy_id", "text"), new("metadata_policy_version", "integer"), new("recorded_at", "timestamp with time zone")],
        [Key("chat_mapping_write_audit_pkey", "organization_id", "creator_id", "event_id")]);

    private static readonly Table WriteIntents = new("chat_mapping_write_intents",
        [.. Uuids("organization_id", "creator_id", "request_key", "conversation_id", "turn_id", "round_id"), new("call_id", "text"),
         .. Uuids("project_id", "mapping_id", "base_revision_id"), new("base_sequence", "bigint"), new("operation_count", "integer"),
         new("edit_digest", "text"), new("native_digest", "text"), new("native_byte_count", "bigint"),
         new("profile_id", "text"), new("profile_version", "integer"), new("provider_id", "text"), new("model_id", "text"),
         new("metadata_policy_id", "text"), new("metadata_policy_version", "integer"), new("recorded_at", "timestamp with time zone")],
        [Key("chat_mapping_write_intents_pkey", "organization_id", "creator_id", "request_key"),
          Unique("chat_mapping_write_intents_call_key", "organization_id", "creator_id", "conversation_id", "turn_id", "round_id", "call_id")]);
    private static readonly Table WriteContent = new("chat_mapping_write_content",
        [.. Uuids("organization_id", "creator_id", "request_key", "conversation_id"), new("proposal", "text", C: true), new("native", "bytea")],
        [Key("chat_mapping_write_content_pkey", "organization_id", "creator_id", "request_key"),
         Foreign("chat_mapping_write_content_conversation_fkey", ["organization_id", "creator_id", "conversation_id"], "chat_conversations", ["organization_id", "creator_id", "conversation_id"], "c")]);
    private static readonly Table WriteCommits = new("chat_mapping_write_commits",
        [.. Uuids("organization_id", "creator_id", "request_key", "operation_id", "project_id", "mapping_id", "revision_id"),
         new("revision_sequence", "bigint"), new("committed_at", "timestamp with time zone")],
        [Key("chat_mapping_write_commits_pkey", "organization_id", "creator_id", "request_key")]);
    private static readonly Table[] NewTables = [Conversations, Turns, Rounds, Events, Identities, PayloadAudit, MappingWriteAudit, WriteIntents, WriteContent, WriteCommits, EditProposals, EditContent, EditApprovals];
    private static readonly Table History = new("schema_migrations",
        [new("version", "integer"), new("checksum", "text"), new("applied_at", "timestamp with time zone", Default: "CURRENT_TIMESTAMP")],
        [Key("schema_migrations_pkey", "version"), Check("schema_migrations_version_check", "(version > 0)"),
         Check("schema_migrations_checksum_check", DigestCheck("checksum"))]);
    internal static readonly string[] PrefixChecksums =
    [
        "0a6c95e1357d9fba7736dd5870895a11443b0e924989163675f00ba2e858f9d1",
        "d61a8bc2d56b6a39607414cf0f14c58db511594369ac124fd270a08f77455ae5",
        "0ed73cc9d597390366b661beb24f3bdfeddf1dbd9d49812ae575c2c42283fff3",
        "7335f18b8c5686b9e3ba3a2b9719d52e51fb22f5c07071e201bb7a239a48b113",
        "2ae8603df7c7ecd56e587ae65d32e1225b8569702e98ff24e5d0fc1694fae5c1",
        "b156596e07ec82a5cc8c11ef801d9ca5e15d4e6e64c76faf13635c005c986777",
        "39cbd985bf607ae1fac08700794ad7bd939988d4fe49766f181ca6984b2a29fa",
        "437630e970879ff5c03e60d46cfd972fdf82799f1ac4a68ff4fe020cec985546",
        "de0a8f45d704c667c95a8b029fb78785c1be4638b93093bde3c25f082938ea36",
        "73b4713aa5e2bafd886abc616c6e03dbf3bb6544cb24d55ea26b8134209f0e24"
    ];
    private static readonly Lazy<string> Source = new(() => LoadSource(MigrationName, MigrationChecksum));
    private static readonly Lazy<string> WorkspaceSource = new(() => LoadSource(WorkspaceMigrationName, WorkspaceMigrationChecksum));
    private static readonly Lazy<string> MappingWriteSource = new(() => LoadSource(MappingWriteMigrationName, MappingWriteMigrationChecksum));
    private static readonly Lazy<string> WriteJournalSource = new(() => LoadSource(WriteJournalMigrationName, WriteJournalMigrationChecksum));
    private static string LoadSource(string name, string checksum)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
            "Weft.Persistence.Postgres.Migrations." + name + ".sql") ?? throw Refusal();
        using var buffer = new MemoryStream(); stream.CopyTo(buffer);
        var bytes = buffer.ToArray();
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != checksum) throw Refusal();
        return new UTF8Encoding(false, true).GetString(bytes);
    }
    // A routine a later migration replaces or adds has exactly its latest body; any other keeps 0011's.
    private static string Body(string name)
    {
        var body = Body(EditProposalSource.Value, name) ?? Body(WriteJournalSource.Value, name) ?? Body(MappingWriteSource.Value, name) ?? Body(WorkspaceSource.Value, name) ?? Body(Source.Value, name) ?? throw Refusal();
        if (name != "chat_metadata_valid") return body;
        const string marker = "$creation_citation$";
        var sql = DocumentEvidenceSchemaAdmission.MigrationSql;
        var start = sql.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = sql.IndexOf(marker, start, StringComparison.Ordinal);
        if (start < marker.Length || end < start) throw Refusal();
        return body.Replace("j->>'source' !~ '^[1-4]$'", "j->>'source' !~ '^[1-5]$'", StringComparison.Ordinal)
            .Replace("ELSIF kind=13 THEN", sql[start..end], StringComparison.Ordinal);
    }
    private static string? Body(string source, string name)
    {
        var marker = "$" + name + "$";
        var opening = source.IndexOf("AS " + marker, StringComparison.Ordinal);
        if (opening < 0) return null;
        var start = opening + 3 + marker.Length;
        var end = source.IndexOf(marker + ";", start, StringComparison.Ordinal);
        if (end < start || source.IndexOf("AS " + marker, start, StringComparison.Ordinal) >= 0) throw Refusal();
        return source[start..end];
    }
    internal static string[] ColumnGrants(ChatDatabaseProfile profile)
    {
        if (profile == ChatDatabaseProfile.Approver) return ["schema_migrations|version|SELECT", "schema_migrations|checksum|SELECT"];
        var role = ChatRoleGuard.RoleName(profile);
        var result = new List<string>();
        foreach (Match grant in Regex.Matches(Source.Value + "\n" + WorkspaceSource.Value + "\n" + MappingWriteSource.Value,
            @"GRANT (?<right>SELECT|INSERT|UPDATE)\((?<columns>[a-z_,\s]+)\)\s+ON weft\.(?<table>[a-z_]+) TO (?<roles>[a-z_,\s]+);",
            RegexOptions.CultureInvariant))
        {
            if (!grant.Groups["roles"].Value.Split(',').Select(r => r.Trim()).Contains(role, StringComparer.Ordinal)) continue;
            foreach (var column in grant.Groups["columns"].Value.Split(','))
                result.Add(grant.Groups["table"].Value + "|" + column.Trim() + "|" + grant.Groups["right"].Value);
        }
        if (result.Count == 0 || result.Distinct(StringComparer.Ordinal).Count() != result.Count) throw Refusal();
        return result.ToArray();
    }
    private static IEnumerable<Table> SourceTables() => Dependencies.Select(name =>
    {
        var table = name == "revision_ingestions" ? CatalogSchemaContract.Tables[name] : RevisionDerivationSchemaContract.Tables[name];
        return new Table(table.Name, table.Columns.Select(c => new Column(c.Name, c.Type, c.NotNull, c.Default)).ToArray(),
            table.Constraints.Select(c => new Constraint(c.Name, c.Kind, c.Columns, c.ReferencedTable, c.ReferencedColumns,
                Update: name is "revision_derivations" or "revision_derivation_attempts" ? "r" : "a", Deferred: c.Deferred,
                Check: c.Kind == 'c' && RevisionDerivationSchemaContract.TryGetCheck(c.Name, out var check) ? check : null)).ToArray());
    });
    private static InvalidOperationException Refusal() => new("Private chat schema does not match its exact trusted source contract.");
}
