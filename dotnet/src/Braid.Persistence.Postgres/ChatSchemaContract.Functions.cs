using Npgsql;

namespace Braid.Persistence.Postgres;

internal static partial class ChatSchemaContract
{
    private sealed record Function(string Name, string Types, string[] Names, string Result = "trigger",
        string Language = "plpgsql", string Volatility = "v", string Parallel = "u", bool Definer = false,
        bool Scoped = false, bool TableResult = false, string[]? Roles = null)
    {
        internal string Signature => "braid." + Name + "(" + Types + ")";
    }
    private static readonly string[] Owners = ["braid_chat_reader", "braid_chat_writer"];
    private static readonly string[] Writer = ["braid_chat_writer"];
    private static readonly string[] Lifecycle = ["braid_chat_lifecycle"];
    private static readonly string[] Profiles = ["braid_chat_reader", "braid_chat_writer", "braid_chat_lifecycle", "braid_chat_approver"];
    private const string EraseTypes = "uuid,uuid,uuid,uuid,uuid,uuid,text,integer,timestamp with time zone";
    private static readonly string[] TargetNames = ["p_org", "p_creator", "p_conversation", "p_project", "p_mapping", "p_revision"];
    private static readonly string[] EraseNames = [.. TargetNames, "p_policy", "p_policy_version", "p_retirement_not_before"];
    private static readonly Function[] Functions =
    [
        new("chat_authorized", "uuid,uuid,uuid", ["org", "project", "creator"], "boolean", "sql", "s", Roles: Owners),
        new("chat_is_lifecycle", "", [], "boolean", "sql", "s", Roles: Writer),
        new("chat_metadata_valid", "integer,text,uuid", ["kind", "value", "expected_round"], "boolean", Volatility: "i", Parallel: "s", Roles: Writer),
        new("guard_chat_identity", "", []),
        new("check_chat_identity_erasure", "", [], Scoped: true),
        new("guard_chat_conversation", "", []), new("guard_chat_child", "", []),
        new("check_chat_counters", "", [], Scoped: true),
        new("chat_reserve_identity", "uuid,uuid,uuid,uuid,integer,uuid,uuid",
            ["p_org", "p_creator", "p_conversation", "p_project", "p_kind", "p_identity", "p_turn"], "text", Definer: true, Roles: Writer),
        new("chat_schema_lock", "", [], "void", Definer: true, Roles: Profiles),
        new("chat_lifecycle_observe", "uuid,uuid,uuid,uuid,uuid,uuid,uuid", [.. TargetNames, "p_turn", "phase", "version"],
            "record", Definer: true, TableResult: true, Roles: Lifecycle),
        new("chat_lifecycle_close", "uuid,uuid,uuid,uuid,uuid,uuid,uuid,bigint,integer,uuid,text",
            [.. TargetNames, "p_turn", "p_expected", "p_phase", "p_event", "p_metadata"], "text", Definer: true, Scoped: true, Roles: Lifecycle),
        new("chat_erase_expired", EraseTypes, EraseNames, "text", Roles: Writer),
        new("chat_lifecycle_erase", EraseTypes, EraseNames, "text", Definer: true, Scoped: true, Roles: Lifecycle),
        new("guard_chat_payload_audit", "", []),
        new("audit_chat_payload", "", [], Definer: true),
        new("guard_chat_mapping_write_audit", "", []),
        new("audit_chat_mapping_write", "", [], Definer: true),
        new("guard_chat_mapping_write_content", "", []),
        new("audit_chat_mapping_write_commit", "", [], Definer: true),
        new("chat_mapping_write_prepare", "uuid,uuid,uuid,uuid,uuid,text,uuid,text,bytea",
            ["p_org", "p_creator", "p_conversation", "p_turn", "p_round", "p_call", "p_key", "p_proposal", "p_native"], "boolean", Definer: true),
        new("chat_mapping_write_read", "uuid,uuid,uuid,uuid", ["p_org", "p_creator", "p_conversation", "p_turn"], "text", Definer: true, Roles: Owners),
        new("chat_edit_create", "uuid,uuid,uuid,uuid,text,uuid,text,bytea,bytea,text",
            ["p_org", "p_creator", "p_conversation", "p_turn", "p_facts", "p_generation", "p_provenance", "p_encoding", "p_native", "p_journal"], "text", Definer: true, Roles: Writer),
        new("chat_edit_read", "uuid,uuid,uuid,uuid,uuid", ["p_org", "p_creator", "p_conversation", "p_turn", "p_proposal"], "text", Definer: true,
            Roles: ["braid_chat_reader", "braid_chat_writer", "braid_chat_approver"]),
        new("chat_edit_list", "uuid,uuid,uuid,uuid", ["p_org", "p_creator", "p_conversation", "p_turn"], "text", Definer: true,
            Roles: ["braid_chat_reader", "braid_chat_writer", "braid_chat_approver"]),
        new("chat_edit_approve", "uuid,uuid,uuid,uuid,uuid,text", ["p_org", "p_creator", "p_conversation", "p_turn", "p_proposal", "p_fingerprint"], "text", Definer: true,
            Roles: ["braid_chat_approver"])
    ];
    internal static string[] FunctionGrants(ChatDatabaseProfile profile) => Functions
        .Where(f => f.Roles?.Contains(ChatRoleGuard.RoleName(profile), StringComparer.Ordinal) == true).Select(f => f.Signature)
        .Concat(profile == ChatDatabaseProfile.Writer ? [AiQuotaSchemaAdmission.ReserveSignature] : Array.Empty<string>()).ToArray();
    internal static string[] FunctionNames => Functions.Select(f => f.Name).ToArray();

    private static async Task ValidateFunctionsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct,
        IEnumerable<Function>? selected = null)
    {
        foreach (var f in selected ?? Functions)
        {
            await using var command = new NpgsqlCommand("""
                SELECT count(*)=1 AND COALESCE(bool_and(o.rolname='braid_migrator' AND l.lanname=$2 AND f.prokind='f'
                    AND f.prosecdef=$3 AND f.proretset=$4 AND NOT f.proleakproof AND NOT f.proisstrict
                    AND f.provolatile=$5::"char" AND f.proparallel=$6::"char"
                    AND pg_catalog.oidvectortypes(f.proargtypes)=array_to_string(string_to_array($7,','),', ')
                    AND f.pronargs=cardinality(CASE WHEN $7='' THEN ARRAY[]::text[] ELSE string_to_array($7,',') END)
                    AND pg_catalog.format_type(f.prorettype,NULL)=$8
                    AND f.pronargdefaults=0 AND f.proargdefaults IS NULL
                    AND CASE WHEN $4 THEN f.proallargtypes=ARRAY[2950,2950,2950,2950,2950,2950,2950,23,20]::oid[]
                        AND f.proargmodes=ARRAY['i','i','i','i','i','i','i','t','t']::"char"[]
                        ELSE f.proallargtypes IS NULL AND f.proargmodes IS NULL END
                    AND f.provariadic=0 AND f.probin IS NULL AND f.prosqlbody IS NULL AND f.prosupport=0 AND f.procost=100
                    AND f.prorows=CASE WHEN $4 THEN 1000 ELSE 0 END
                    AND COALESCE(f.proargnames,ARRAY[]::text[])=$9::text[] AND f.proconfig=$10::text[] AND f.prosrc=$11
                    AND (SELECT count(*)=cardinality($12::text[]) AND bool_and(a.grantor=f.proowner AND NOT a.is_grantable
                        AND a.privilege_type='EXECUTE' AND role.rolname=ANY($12::text[]))
                        FROM pg_catalog.aclexplode(COALESCE(f.proacl,pg_catalog.acldefault('f',f.proowner))) a
                        LEFT JOIN pg_catalog.pg_roles role ON role.oid=a.grantee)),false)
                FROM pg_catalog.pg_proc f JOIN pg_catalog.pg_namespace n ON n.oid=f.pronamespace
                JOIN pg_catalog.pg_roles o ON o.oid=f.proowner JOIN pg_catalog.pg_language l ON l.oid=f.prolang
                WHERE n.nspname='braid' AND f.proname=$1
                """, connection, transaction);
            command.Parameters.AddWithValue(f.Name); command.Parameters.AddWithValue(f.Language);
            command.Parameters.AddWithValue(f.Definer); command.Parameters.AddWithValue(f.TableResult);
            command.Parameters.AddWithValue(f.Volatility); command.Parameters.AddWithValue(f.Parallel);
            command.Parameters.AddWithValue(f.Types); command.Parameters.AddWithValue(f.Result); command.Parameters.AddWithValue(f.Names);
            command.Parameters.AddWithValue(f.Scoped ? new[] { SearchPath, "braid.organization_id=", "braid.chat_principal_id=" } : new[] { SearchPath });
            command.Parameters.AddWithValue(Body(f.Name)); command.Parameters.AddWithValue(new[] { "braid_migrator" }.Concat(f.Roles ?? []).ToArray());
            if (await command.ExecuteScalarAsync(ct) is not true) throw Refusal();
        }
    }
}
