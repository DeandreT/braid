using Npgsql;
using NpgsqlTypes;
using Braid.Application;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

/// <summary>
/// Actual PostgreSQL registrations by both explicit store kinds. Identities, ages and cleanup
/// dispositions are authored metadata; no private store is opened here.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresPrivatePublicationStoreKindTests(PostgresFixture fixture)
{
    private static readonly PrivatePublicationStoreKind[] Kinds =
        [PrivatePublicationStoreKind.FileSystem, PrivatePublicationStoreKind.AzureBlob];

    [PostgresFact]
    public async Task EachRegisteringStoreKindRetainsItsOwnLabelAndRecoveryNeverCrossesKinds()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        var native = await test.ReserveNativeAsync();
        var report = await test.ReserveReportAsync();
        var owners = new (IPrivateObjectPublicationRegistry Registry, PrivatePublicationOwner Owner, PrivateObjectId Target)[]
        {
            (test.NativePublications, PrivatePublicationOwner.ForIngestion(test.Actor, await test.ClaimNativeAsync(native)), native.NativeObject.Id),
            (test.ReportPublications, PrivatePublicationOwner.ForReport(test.Actor, report), report.CandidateObjectId)
        };
        foreach (var (registry, owner, target) in owners)
            foreach (var kind in Kinds)
            {
                var other = Other(kind);
                var root = Guid.NewGuid();
                var temp = Guid.NewGuid();
                var reserved = await registry.ReserveAsync(owner, kind, root, target, temp, PostgresPrivateObjectMaintenanceTestSupport.AbsenceDigest);
                Assert.Equal(PrivatePublicationReserveOutcome.Reserved, reserved.Outcome);
                var reservation = reserved.Snapshot!.Record.Reservation;
                Assert.Equal(kind, reservation.StoreKind);
                Assert.Equal(Expected(kind), await LabelAsync(test, reservation.PublicationId));

                // The same natural key under another kind conflicts, and recovery never adopts it.
                Assert.Equal(PrivatePublicationReserveOutcome.Conflict, (await registry.ReserveAsync(owner, other, root, target, temp,
                    PostgresPrivateObjectMaintenanceTestSupport.AbsenceDigest)).Outcome);
                Assert.Null(await registry.GetAsync(owner, other, root, target, temp));
                var bound = await registry.BindAsync(reservation, PostgresPrivateObjectMaintenanceTestSupport.SyntheticIdentity(root),
                    PostgresPrivateObjectMaintenanceTestSupport.BoundDigest);
                Assert.Equal(PrivatePublicationBindOutcome.Bound, bound.Outcome);
                Assert.Equal(bound.Snapshot!.Record, (await registry.GetAsync(owner, kind, root, target, temp))!.Record);
                Assert.Null(await registry.GetAsync(owner, other, root, target, temp));
                Assert.Equal(Expected(kind), await LabelAsync(test, reservation.PublicationId));
            }
        Assert.Equal(new long[] { 0, 0, 4 }, await test.CountsAsync());
    }

    [PostgresFact]
    public async Task RawRegistrationRequiresAnAdmittedExplicitKindAndTheGuardAloneStampsTheLabel()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        var facts = await test.ReserveNativeAsync();
        var claim = await test.ClaimNativeAsync(facts);
        foreach (var kind in new string?[] { null, "azure", "Filesystem", "azure-blob.v1" })
        {
            await using var transaction = await test.IngestionDatabase.BeginTransactionAsync(test.Project.OrganizationId);
            await using var insert = RawInsert(transaction, facts, claim, kind);
            var refusal = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
            Assert.Equal(("23514", "Publication requires its registering store's explicit kind."), (refusal.SqlState, refusal.MessageText));
        }
        // The label itself is never caller intent: the ordinary login cannot even name the column.
        foreach (var kind in Kinds)
        {
            await using var transaction = await test.IngestionDatabase.BeginTransactionAsync(test.Project.OrganizationId);
            await using var insert = RawInsert(transaction, facts, claim, Expected(kind).Kind, Expected(kind).Format);
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync())).SqlState);
        }
        Assert.Equal(new long[] { 0, 0, 0 }, await test.CountsAsync());

        foreach (var kind in Kinds)
        {
            await using var transaction = await test.IngestionDatabase.BeginTransactionAsync(test.Project.OrganizationId);
            await using (var insert = RawInsert(transaction, facts, claim, Expected(kind).Kind))
            await using (var reader = await insert.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(Expected(kind).Format, reader.GetString(0));
            }
            await transaction.CommitAsync();
        }
        Assert.Equal(new long[] { 0, 0, 2 }, await test.CountsAsync());

        // Even the owner, with the guard disabled, cannot pair a kind with another kind's label.
        var existing = await test.Case.ScalarAsync<Guid>(
            "SELECT publication_id FROM braid.private_object_publications WHERE storage_kind='filesystem'");
        var mismatch = await Assert.ThrowsAsync<PostgresException>(() => test.OwnerEditAsync(
            [("private_object_publications", "private_object_publications_transition_guard")],
            new PostgresPrivateObjectMaintenanceTestSupport.OwnerStatement("""
                INSERT INTO braid.private_object_publications SELECT (jsonb_populate_record(NULL::braid.private_object_publications,
                    to_jsonb(p) || jsonb_build_object('publication_id',gen_random_uuid(),'temp_id',gen_random_uuid(),'storage_kind','azure-blob'))).*
                FROM braid.private_object_publications p WHERE publication_id=$1
                """, [existing], 1)));
        Assert.Equal(("23514", "private_object_publications_format_check"), (mismatch.SqlState, mismatch.ConstraintName));
        Assert.Equal(new long[] { 0, 0, 2 }, await test.CountsAsync());
    }

    [PostgresFact]
    public async Task MaintenanceClaimsAndAcknowledgesTrackedTemporariesOfBothKindsUnderTheirOwnLabels()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        var registered = new Dictionary<PrivatePublicationStoreKind, PrivatePublicationReservation>();
        foreach (var kind in Kinds)
        {
            var facts = await test.ReserveNativeAsync();
            var owner = PrivatePublicationOwner.ForIngestion(test.Actor, await test.ClaimNativeAsync(facts));
            var root = Guid.NewGuid();
            var reserved = await test.NativePublications.ReserveAsync(owner, kind, root, facts.NativeObject.Id, Guid.NewGuid(),
                PostgresPrivateObjectMaintenanceTestSupport.AbsenceDigest);
            var bound = await test.NativePublications.BindAsync(reserved.Snapshot!.Record.Reservation,
                PostgresPrivateObjectMaintenanceTestSupport.SyntheticIdentity(root), PostgresPrivateObjectMaintenanceTestSupport.BoundDigest);
            Assert.Equal(PrivatePublicationBindOutcome.Bound, bound.Outcome);
            registered.Add(kind, await test.SyntheticAgePublicationAsync(bound.Snapshot!.Record.Reservation, TimeSpan.FromHours(25)));
        }

        var batch = await test.Maintenance.ClaimAsync(test.Project, Guid.NewGuid());
        Assert.Equal(2, batch.Claims.Count);
        foreach (var claim in batch.Claims)
        {
            var publication = Assert.IsType<PrivatePublicationRecord>(claim.Retirement.Publication);
            var kind = publication.Reservation.StoreKind;
            Assert.Equal(registered[kind], publication.Reservation);
            Assert.Equal(CleanupTargetKind.TrackedTemporary, claim.Retirement.Target.Kind);
            // The full transported claim, including its label, must equal the retained claim to acknowledge it.
            var finished = await test.Maintenance.FinishAsync(claim, ObjectCleanupDisposition.Missing);
            Assert.Equal(ObjectCleanupFinishOutcome.Applied, finished.Outcome);
            Assert.Equal(claim, finished.Attempt!.Claim);
            Assert.Equal(claim, (await test.Maintenance.GetAttemptAsync(test.Project, claim.Retirement.RetirementId, claim.Generation))!.Claim);
            Assert.Equal(Expected(kind), await LabelAsync(test, publication.Reservation.PublicationId));
        }
        Assert.Equal(registered.Keys.Order(), batch.Claims.Select(claim => claim.Retirement.Publication!.Reservation.StoreKind).Order());
        Assert.Equal(new long[] { 2, 2, 2 }, await test.CountsAsync());
    }

    internal static (string Kind, string Format) Expected(PrivatePublicationStoreKind kind) => kind switch
    {
        PrivatePublicationStoreKind.FileSystem => ("filesystem", PrivatePublicationFileIdentity.FileSystemFormat),
        PrivatePublicationStoreKind.AzureBlob => ("azure-blob", PrivatePublicationFileIdentity.AzureBlobFormat),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    internal static async Task<(string Kind, string Format)> LabelAsync(PostgresPrivateObjectMaintenanceTestSupport test, Guid publicationId)
    {
        var row = await test.Case.ScalarAsync<string[]>(
            "SELECT ARRAY[storage_kind,format] FROM braid.private_object_publications WHERE publication_id=$1", publicationId);
        return (row[0], row[1]);
    }

    private static PrivatePublicationStoreKind Other(PrivatePublicationStoreKind kind) =>
        kind == PrivatePublicationStoreKind.FileSystem ? PrivatePublicationStoreKind.AzureBlob : PrivatePublicationStoreKind.FileSystem;

    // The ordinary registration statement, issued directly through the actual ingestion login.
    private static NpgsqlCommand RawInsert(PostgresIngestionTransaction transaction, ReservedIngestionFacts facts, IngestionClaim claim,
        string? kind, string? format = null)
    {
        var command = transaction.CreateCommand($"""
            INSERT INTO braid.private_object_publications
                (organization_id,project_id,mapping_id,revision_id,object_class,object_id,root_id,temp_id,
                 owner_kind,actor_principal_id,ingestion_operation_id,owner_generation,owner_instance_id,owner_token,
                 owner_lease_expires_at,absence_record_digest,storage_kind{(format is null ? "" : ",format")})
            VALUES ($1,$2,$3,$4,'native',$5,$6,$7,'native_ingestion',$8,$9,$10,$11,$12,$13,$14,$15{(format is null ? "" : ",$16")})
            RETURNING format
            """);
        var scope = facts.FutureRevision;
        foreach (var (type, value) in new (NpgsqlDbType, object?)[]
        {
            (NpgsqlDbType.Uuid, scope.OrganizationId), (NpgsqlDbType.Uuid, scope.ProjectId), (NpgsqlDbType.Uuid, scope.MappingId),
            (NpgsqlDbType.Uuid, scope.RevisionId), (NpgsqlDbType.Uuid, facts.NativeObject.Id.ObjectId), (NpgsqlDbType.Uuid, Guid.NewGuid()),
            (NpgsqlDbType.Uuid, Guid.NewGuid()), (NpgsqlDbType.Uuid, claim.OriginalActor.Value), (NpgsqlDbType.Uuid, claim.OperationId),
            (NpgsqlDbType.Bigint, claim.Generation), (NpgsqlDbType.Uuid, claim.ServerInstanceId), (NpgsqlDbType.Uuid, claim.LeaseToken),
            (NpgsqlDbType.TimestampTz, claim.LeaseExpiresAtUtc), (NpgsqlDbType.Text, PostgresPrivateObjectMaintenanceTestSupport.AbsenceDigest.Value),
            (NpgsqlDbType.Text, kind)
        })
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = type, Value = value ?? DBNull.Value });
        if (format is not null) command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = format });
        return command;
    }
}
