namespace Weft.Tests;

/// <summary>The additive draft suffix excluded when comparing older populated schema prefixes.</summary>
internal static class MappingDraftSchemaTestContract
{
    internal const string DraftChecksum = "4a708309aff9a60f433be23d9c01bed63eedb1178ccc482850fb5ceff1533fd7";
    internal const string PreviewChecksum = "7340f7304d72c7e2b177bca1057c2dc747471f3fa58b4c6150fbbe12f3c631ec";
    internal static readonly string[] Tables = ["mapping_drafts", "mapping_draft_checkpoints", "revision_edit_provenance"];
    internal static readonly string[] Roles = ["weft_draft_reader", "weft_draft_writer"];
    internal static readonly string[] PreviewParentForeignKeys = ["preview_runs_draft_base_fkey"];
    internal static async Task AdmitPreviewParentAsync(PostgresCase test)
    {
        Assert.True(await test.ScalarAsync<bool>("""
            SELECT count(*)=1 FROM pg_catalog.pg_constraint c
            WHERE c.conname='preview_runs_draft_base_fkey' AND c.conrelid='weft.preview_runs'::regclass
                AND c.confrelid='weft.mapping_revisions'::regclass AND c.contype='f' AND c.convalidated
                AND ARRAY(SELECT a.attname::text FROM unnest(c.conkey) WITH ORDINALITY k(attnum,n)
                    JOIN pg_catalog.pg_attribute a ON a.attrelid=c.conrelid AND a.attnum=k.attnum ORDER BY k.n)
                    =ARRAY['organization_id','project_id','mapping_id','draft_base_revision_id']::text[]
                AND ARRAY(SELECT a.attname::text FROM unnest(c.confkey) WITH ORDINALITY k(attnum,n)
                    JOIN pg_catalog.pg_attribute a ON a.attrelid=c.confrelid AND a.attnum=k.attnum ORDER BY k.n)
                    =ARRAY['organization_id','project_id','mapping_id','id']::text[]
            """), "The draft preview parent foreign key is not the exact admitted scoped delta.");
    }
    internal static readonly string[] Functions =
    [
        "draft_authorized", "draft_base", "draft_read", "draft_list_admitted", "draft_observe_creation",
        "draft_checkpoint_read", "draft_checkpoint_summary", "draft_checkpoint_write", "draft_create",
        "draft_put_operations", "draft_put_checkpoint", "draft_rebase", "draft_begin_save", "draft_reconcile_internal",
        "draft_reconcile", "draft_discard", "draft_expire", "preview_subject_authorized", "preview_submit_draft",
        "preview_draft_subject", "preview_claim_native", "preview_draft_subject_by_key"
    ];
}
