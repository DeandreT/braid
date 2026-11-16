using Npgsql;
using Braid.Application;
using Braid.Domain;

namespace Braid.Tests;

/// <summary>Actual PostgreSQL. Bulk headers, Linux identities and clock shifts are authored test metadata, never files or deletion proof.</summary>
internal static class PostgresMaintenanceObservationTestSupport
{
    internal static Guid OrderedId(int value) => Guid.Parse($"{(value == 1 ? 1 : value + 254):x8}-1122-3344-5566-778899aabbcc");

    internal static async Task<Guid[]> SeedUnattemptedAsync(PostgresPrivateObjectMaintenanceTestSupport test, int count)
    {
        if (count == 0) return [];
        var template = await test.CreateRejectedNativeAsync();
        var ids = Enumerable.Range(1, count).Select(OrderedId).ToArray();
        // Narrow owner-authored bulk fixture. All FK/CHECK/internal and supplementary
        // attachment guards stay active; the named ordinary transitions are restored
        // atomically before the admitted production observer is called.
        await test.OwnerEditAsync([
            ("revision_ingestions", "revision_ingestions_transition_guard"),
            ("private_object_retirements", "private_object_retirements_transition_guard")],
            new PostgresPrivateObjectMaintenanceTestSupport.OwnerStatement("""
                WITH input AS (SELECT unnest($1::uuid[]) AS retirement_id),
                copies AS MATERIALIZED (
                  SELECT x.retirement_id,(jsonb_populate_record(NULL::braid.revision_ingestions,
                    to_jsonb(i) || jsonb_build_object('operation_id',gen_random_uuid(),'idempotency_key',gen_random_uuid(),
                    'future_mapping_id',gen_random_uuid(),'future_revision_id',gen_random_uuid(),'native_object_id',gen_random_uuid(),
                    'lease_generation',0,'claim_count',0))).*
                  FROM input x CROSS JOIN braid.revision_ingestions i WHERE i.operation_id=$2),
                inserted AS (
                  INSERT INTO braid.revision_ingestions SELECT (jsonb_populate_record(NULL::braid.revision_ingestions,
                    to_jsonb(c)-'retirement_id')).* FROM copies c RETURNING *)
                INSERT INTO braid.private_object_retirements
                  (organization_id,project_id,mapping_id,revision_id,object_class,object_id,retirement_id,target_kind,
                   origin_kind,ingestion_operation_id,native_object_id,reason,eligibility_origin_at,retired_at)
                SELECT i.organization_id,i.project_id,i.future_mapping_id,i.future_revision_id,'native',i.native_object_id,
                  c.retirement_id,'final_object','native_ingestion',i.operation_id,i.native_object_id,
                  'terminal_native_stage',i.completed_at,clock_timestamp()-interval '10 minutes'
                FROM inserted i JOIN copies c ON c.operation_id=i.operation_id
                """, [ids, template.OperationId], count));
        return ids;
    }

    internal static async Task<string> SnapshotAsync(PostgresPrivateObjectMaintenanceTestSupport test) =>
        await test.Case.ScalarAsync<string>("""
            SELECT jsonb_build_object(
              'retirements',(SELECT coalesce(jsonb_agg(to_jsonb(r) ORDER BY retirement_id),'[]') FROM braid.private_object_retirements r),
              'attempts',(SELECT coalesce(jsonb_agg(to_jsonb(a) ORDER BY retirement_id,generation),'[]') FROM braid.private_object_cleanup_attempts a),
              'publications',(SELECT coalesce(jsonb_agg(to_jsonb(p) ORDER BY publication_id),'[]') FROM braid.private_object_publications p),
              'table_rights',(SELECT jsonb_agg(jsonb_build_array(c.relname,c.relacl::text) ORDER BY c.relname)
                FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='braid'),
              'column_rights',(SELECT jsonb_agg(jsonb_build_array(c.relname,a.attname,a.attacl::text) ORDER BY c.relname,a.attnum)
                FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace JOIN pg_attribute a ON a.attrelid=c.oid
                WHERE n.nspname='braid' AND a.attnum>0 AND NOT a.attisdropped))::text
            """);

    internal static async Task<PrivatePublicationReservation> BoundAsync(PostgresPrivateObjectMaintenanceTestSupport test,
        IPrivateObjectPublicationRegistry registry, PrivatePublicationOwner owner, PrivateObjectId target,
        PrivatePublicationFileIdentity identity, Guid? tempId = null)
    {
        var reserve = await registry.ReserveAsync(owner, PrivatePublicationStoreKind.FileSystem, identity.RootId, target, tempId ?? Guid.NewGuid(),
            PostgresPrivateObjectMaintenanceTestSupport.AbsenceDigest);
        Assert.Equal(PrivatePublicationReserveOutcome.Reserved, reserve.Outcome);
        var reservation = reserve.Snapshot!.Record.Reservation;
        var bound = await registry.BindAsync(reservation, identity, PostgresPrivateObjectMaintenanceTestSupport.BoundDigest);
        Assert.Equal(PrivatePublicationBindOutcome.Bound, bound.Outcome);
        return reservation;
    }

    internal static async Task AddAuthoredPublicationCopiesAsync(PostgresPrivateObjectMaintenanceTestSupport test,
        Guid templatePublicationId, Guid[] tempIds)
    {
        // Many matching registrations with one authored Linux identity deliberately
        // test output bounds. This does not claim that physical files shared an inode.
        await test.OwnerEditAsync([("private_object_publications", "private_object_publications_transition_guard")],
            new PostgresPrivateObjectMaintenanceTestSupport.OwnerStatement("""
                INSERT INTO braid.private_object_publications
                SELECT (jsonb_populate_record(NULL::braid.private_object_publications,
                  to_jsonb(p)||jsonb_build_object('publication_id',gen_random_uuid(),'temp_id',x.id))).*
                FROM braid.private_object_publications p CROSS JOIN unnest($1::uuid[]) x(id)
                WHERE p.publication_id=$2
                """, [tempIds, templatePublicationId], tempIds.Length));
    }

    internal static async Task AssertUnboundAsync(PostgresPrivateObjectMaintenanceTestSupport test, int expectedPid)
    {
        await using var connection = await test.MaintenanceDatabase.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT pg_backend_pid(),NULLIF(current_setting('braid.organization_id',true),''),
              NULLIF(current_setting('braid.project_id',true),'')
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(expectedPid, reader.GetInt32(0));
        Assert.True(reader.IsDBNull(1));
        Assert.True(reader.IsDBNull(2));
        Assert.False(await reader.ReadAsync());
    }
}
