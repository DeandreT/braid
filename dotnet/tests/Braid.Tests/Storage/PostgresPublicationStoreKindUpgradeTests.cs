using System.Data;
using Npgsql;
using NpgsqlTypes;
using Braid.Application;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

/// <summary>
/// Genuine upgrade from the 22-script prefix. The ingestion claim and its publications are written
/// through the actual ingestion login and the prefix's own guards, as that build registered them;
/// the bound identity is authored metadata and no private store runs.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresPublicationStoreKindUpgradeTests(PostgresFixture fixture)
{
    private const int PrefixVersion = 22;

    [PostgresFact]
    public async Task ExistingRegistrationsBecomeFilesystemRowsWithTheirLabelAndNewRowsCarryTheirOwnKind()
    {
        await using var test = await fixture.CreateCaseAsync(migrate: false);
        await using (var prefix = new MigrationRunner(test.MigrationConnectionString,
                         MigrationRunner.EmbeddedMigrations.Take(PrefixVersion).ToArray()))
            await prefix.MigrateAsync();
        Assert.Equal(Enumerable.Range(1, PrefixVersion), await test.ScalarAsync<int[]>(
            "SELECT array_agg(version ORDER BY version) FROM braid.schema_migrations"));
        Assert.False(await test.ScalarAsync<bool>("""
            SELECT EXISTS(SELECT 1 FROM information_schema.columns
                WHERE table_schema='braid' AND table_name='private_object_publications' AND column_name='storage_kind')
            """));

        var seed = await PostgresSeedData.CreateAsync(test);
        var actor = new ActorContext(new(Guid.NewGuid()));
        var project = new ProjectScope(seed.OrganizationA, seed.ProjectA);
        await test.ExecuteAsync("INSERT INTO braid.principals(id,active) VALUES ($1,true)", actor.PrincipalId.Value);
        await test.ExecuteAsync("INSERT INTO braid.organization_memberships(organization_id,principal_id,active) VALUES ($1,$2,true)",
            project.OrganizationId, actor.PrincipalId.Value);
        await seed.AddMembershipAsync(test, project.OrganizationId, project.ProjectId, actor.PrincipalId.Value, "publisher");

        var (facts, claim) = await ReserveAndClaimAsync(test, actor, project);
        var owner = PrivatePublicationOwner.ForIngestion(actor, claim);
        var root = Guid.NewGuid();
        var identity = PostgresPrivateObjectMaintenanceTestSupport.SyntheticIdentity(root);
        var boundTemp = Guid.NewGuid();
        var reservedTemp = Guid.NewGuid();
        await RegisterAsPrefixAsync(test, facts, claim, root, boundTemp, identity);
        await RegisterAsPrefixAsync(test, facts, claim, root, reservedTemp, null);
        const string snapshot = "SELECT jsonb_agg(to_jsonb(p) ORDER BY publication_id)::text FROM braid.private_object_publications p";
        var before = await test.ScalarAsync<string>(snapshot);
        Assert.Equal(new[] { PrivatePublicationFileIdentity.FileSystemFormat },
            await test.ScalarAsync<string[]>("SELECT array_agg(DISTINCT format) FROM braid.private_object_publications"));

        await test.MigrateAsync();

        Assert.Equal(Enumerable.Range(1, MigrationRunner.EmbeddedMigrations.Count), await test.ScalarAsync<int[]>(
            "SELECT array_agg(version ORDER BY version) FROM braid.schema_migrations"));
        Assert.Equal(MaintenanceSchemaContract.StoreKindMigrationChecksum, await test.ScalarAsync<string>(
            "SELECT checksum FROM braid.schema_migrations WHERE version=23"));
        // Every retained fact is unchanged; the explicit kind records the store that wrote it.
        Assert.Equal(before, await test.ScalarAsync<string>(
            "SELECT jsonb_agg(to_jsonb(p) - 'storage_kind' ORDER BY publication_id)::text FROM braid.private_object_publications p"));
        Assert.Equal(new[] { "filesystem|" + PrivatePublicationFileIdentity.FileSystemFormat }, await test.ScalarAsync<string[]>(
            "SELECT array_agg(DISTINCT storage_kind || '|' || format) FROM braid.private_object_publications"));

        // Admitted current pools read the upgraded rows only as the filesystem store's registrations.
        await using var ingestion = new PostgresIngestionDatabase(test.IngestionConnectionString);
        var registry = new PostgresIngestionPublicationRegistry(ingestion);
        var bound = Assert.IsType<PrivatePublicationSnapshot>(await registry.GetAsync(owner, PrivatePublicationStoreKind.FileSystem,
            root, facts.NativeObject.Id, boundTemp));
        Assert.Equal(PrivatePublicationStoreKind.FileSystem, bound.Record.Reservation.StoreKind);
        Assert.Equal(identity, bound.Record.Binding!.ActualIdentity);
        var reserved = Assert.IsType<PrivatePublicationSnapshot>(await registry.GetAsync(owner, PrivatePublicationStoreKind.FileSystem,
            root, facts.NativeObject.Id, reservedTemp));
        Assert.Null(reserved.Record.Binding);
        Assert.Null(await registry.GetAsync(owner, PrivatePublicationStoreKind.AzureBlob, root, facts.NativeObject.Id, boundTemp));
        Assert.Equal(PrivatePublicationReserveOutcome.Conflict, (await registry.ReserveAsync(owner, PrivatePublicationStoreKind.AzureBlob,
            root, facts.NativeObject.Id, reservedTemp, PostgresPrivateObjectMaintenanceTestSupport.AbsenceDigest)).Outcome);
        Assert.Equal(PrivatePublicationBindOutcome.Bound, (await registry.BindAsync(reserved.Record.Reservation,
            PostgresPrivateObjectMaintenanceTestSupport.SyntheticIdentity(root), PostgresPrivateObjectMaintenanceTestSupport.BoundDigest)).Outcome);

        foreach (var kind in new[] { PrivatePublicationStoreKind.FileSystem, PrivatePublicationStoreKind.AzureBlob })
        {
            var added = await registry.ReserveAsync(owner, kind, Guid.NewGuid(), facts.NativeObject.Id, Guid.NewGuid(),
                PostgresPrivateObjectMaintenanceTestSupport.AbsenceDigest);
            Assert.Equal(PrivatePublicationReserveOutcome.Reserved, added.Outcome);
            var expected = PostgresPrivatePublicationStoreKindTests.Expected(kind);
            Assert.Equal(new[] { expected.Kind, expected.Format }, await test.ScalarAsync<string[]>(
                "SELECT ARRAY[storage_kind,format] FROM braid.private_object_publications WHERE publication_id=$1",
                added.Snapshot!.Record.Reservation.PublicationId));
        }
        await using var maintenance = new PostgresPrivateObjectMaintenanceDatabase(test.MaintenanceConnectionString);
        Assert.Empty((await new PostgresPrivateObjectMaintenanceStore(maintenance).ClaimAsync(project, Guid.NewGuid())).Claims);
    }

    private static async Task<(ReservedIngestionFacts Facts, IngestionClaim Claim)> ReserveAndClaimAsync(PostgresCase test,
        ActorContext actor, ProjectScope project)
    {
        var intent = PostgresIngestionTestSupport.CreateIntent();
        var profile = PostgresIngestionTestSupport.Profile;
        var policy = PostgresIngestionTestSupport.Policy;
        var fingerprint = CanonicalFingerprint.Create(actor.PrincipalId, project, intent);
        var operation = Guid.NewGuid();
        var revision = new RevisionScope(project.OrganizationId, project.ProjectId, Guid.NewGuid(), Guid.NewGuid());
        var native = new PrivateObjectReference(PrivateObjectId.Create(revision, PrivateObjectClass.NativeArtifact),
            intent.NativeDigest, intent.NativeByteCount);
        var key = Guid.NewGuid();
        var instance = Guid.NewGuid();
        var token = Guid.NewGuid();
        await using var connection = await OpenIngestionAsync(test, project);
        await using var transaction = connection.Transaction;
        DateTimeOffset reservedAt;
        await using (var reserve = Command(connection, """
            INSERT INTO braid.revision_ingestions
            (organization_id,project_id,operation_id,actor_principal_id,operation_kind,idempotency_key,
             fingerprint_version,intent_fingerprint,target_mapping_id,future_mapping_id,future_revision_id,
             native_object_id,native_class,native_digest,native_byte_count,logical_entry_document,
             dependency_manifest_id,backend_id,engine_revision,backend_build,adapter_build,admission_policy_id,
             effective_native_byte_limit,display_filename,create_name,create_description,head_condition,
             expected_head_revision_id,created_at,phase,lease_generation,claim_count)
            VALUES (@organization,@project,@operation,@actor,'create',@key,1,@fingerprint,NULL,@mapping,@revision,
                    @object,'native',@digest,@bytes,@document,@dependencies,@backend,@engine,@build,@adapter,
                    @policy,@limit,@filename,@name,@description,'any',NULL,clock_timestamp(),'reserved',0,0)
            RETURNING created_at
            """))
        {
            Add(reserve, "organization", NpgsqlDbType.Uuid, project.OrganizationId);
            Add(reserve, "project", NpgsqlDbType.Uuid, project.ProjectId);
            Add(reserve, "operation", NpgsqlDbType.Uuid, operation);
            Add(reserve, "actor", NpgsqlDbType.Uuid, actor.PrincipalId.Value);
            Add(reserve, "key", NpgsqlDbType.Uuid, key);
            Add(reserve, "fingerprint", NpgsqlDbType.Text, fingerprint.Digest.Value);
            Add(reserve, "mapping", NpgsqlDbType.Uuid, revision.MappingId);
            Add(reserve, "revision", NpgsqlDbType.Uuid, revision.RevisionId);
            Add(reserve, "object", NpgsqlDbType.Uuid, native.Id.ObjectId);
            Add(reserve, "digest", NpgsqlDbType.Text, native.Digest.Value);
            Add(reserve, "bytes", NpgsqlDbType.Bigint, native.ByteCount);
            Add(reserve, "document", NpgsqlDbType.Text, intent.LogicalDocument);
            Add(reserve, "dependencies", NpgsqlDbType.Text, intent.DependencyIdentity);
            Add(reserve, "backend", NpgsqlDbType.Text, profile.BackendId);
            Add(reserve, "engine", NpgsqlDbType.Text, profile.EngineRevision);
            Add(reserve, "build", NpgsqlDbType.Text, profile.BackendBuild);
            Add(reserve, "adapter", NpgsqlDbType.Text, profile.AdapterBuild);
            Add(reserve, "policy", NpgsqlDbType.Text, policy.Identity);
            Add(reserve, "limit", NpgsqlDbType.Bigint, policy.EffectiveNativeByteLimit);
            Add(reserve, "filename", NpgsqlDbType.Text, intent.OriginalFilename);
            Add(reserve, "name", NpgsqlDbType.Text, intent.CreateMetadata?.Name);
            Add(reserve, "description", NpgsqlDbType.Text, intent.CreateMetadata?.Description);
            await using var reader = await reserve.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            reservedAt = reader.GetFieldValue<DateTimeOffset>(0);
        }
        DateTimeOffset expiry;
        await using (var claim = Command(connection, """
            WITH changed AS (
                UPDATE braid.revision_ingestions SET lease_owner_id=@instance,lease_token=@token,lease_generation=1,
                    claim_count=1,lease_expires_at=clock_timestamp() + interval '120 seconds'
                WHERE organization_id=@organization AND project_id=@project AND operation_id=@operation
                  AND phase='reserved' AND lease_generation=0
                RETURNING *
            ), attempt AS (
                INSERT INTO braid.revision_ingestion_attempts
                (organization_id,project_id,operation_id,generation,claimant_instance_id,claim_token,started_at)
                SELECT organization_id,project_id,operation_id,lease_generation,lease_owner_id,lease_token,
                       lease_expires_at - interval '120 seconds' FROM changed
                RETURNING generation
            )
            SELECT (SELECT lease_expires_at FROM changed) FROM attempt
            """))
        {
            Add(claim, "organization", NpgsqlDbType.Uuid, project.OrganizationId);
            Add(claim, "project", NpgsqlDbType.Uuid, project.ProjectId);
            Add(claim, "operation", NpgsqlDbType.Uuid, operation);
            Add(claim, "instance", NpgsqlDbType.Uuid, instance);
            Add(claim, "token", NpgsqlDbType.Uuid, token);
            await using var reader = await claim.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            expiry = reader.GetFieldValue<DateTimeOffset>(0);
            Assert.False(await reader.ReadAsync());
        }
        await transaction.CommitAsync();
        var facts = new ReservedIngestionFacts(project, operation, actor.PrincipalId, key, fingerprint, intent,
            revision, native, profile, policy, reservedAt);
        return (facts, new IngestionClaim(project, operation, actor.PrincipalId, instance, token, 1, expiry));
    }

    // The prefix build's own registration and binding statements; the prefix guard stamps the label.
    private static async Task RegisterAsPrefixAsync(PostgresCase test, ReservedIngestionFacts facts, IngestionClaim claim,
        Guid root, Guid temp, PrivatePublicationFileIdentity? identity)
    {
        await using var connection = await OpenIngestionAsync(test, facts.Project);
        await using var transaction = connection.Transaction;
        Guid publication;
        await using (var insert = Command(connection, """
            INSERT INTO braid.private_object_publications
                (organization_id,project_id,mapping_id,revision_id,object_class,object_id,root_id,temp_id,
                 owner_kind,actor_principal_id,ingestion_operation_id,cache_entry_id,derivation_id,
                 owner_generation,owner_instance_id,owner_token,owner_lease_expires_at,absence_record_digest)
            VALUES (@organization,@project,@mapping,@revision,'native',@object,@root,@temp,
                 'native_ingestion',@actor,@operation,NULL,NULL,@generation,@instance,@token,@expires,@absence)
            RETURNING publication_id
            """))
        {
            AddOwner(insert, facts, claim);
            Add(insert, "root", NpgsqlDbType.Uuid, root);
            Add(insert, "temp", NpgsqlDbType.Uuid, temp);
            Add(insert, "absence", NpgsqlDbType.Text, PostgresPrivateObjectMaintenanceTestSupport.AbsenceDigest.Value);
            publication = Assert.IsType<Guid>(await insert.ExecuteScalarAsync());
        }
        if (identity is not null)
        {
            await using var bind = Command(connection, """
                UPDATE braid.private_object_publications SET
                    control_namespace_id=@control,linux_boot_id=@boot,mount_namespace_device=@namespaceDevice,
                    mount_namespace_inode=@namespaceInode,effective_user_id=@euid,
                    file_inode=@fileInode,file_device_major=@fileMajor,file_device_minor=@fileMinor,file_mount_id=@fileMount,
                    directory_inode=@directoryInode,directory_device_major=@directoryMajor,directory_device_minor=@directoryMinor,
                    directory_mount_id=@directoryMount,bound_record_digest=@boundDigest
                WHERE organization_id=@organization AND project_id=@project AND publication_id=@publication AND bound_at IS NULL
                """);
            Add(bind, "organization", NpgsqlDbType.Uuid, facts.Project.OrganizationId);
            Add(bind, "project", NpgsqlDbType.Uuid, facts.Project.ProjectId);
            Add(bind, "publication", NpgsqlDbType.Uuid, publication);
            Add(bind, "control", NpgsqlDbType.Uuid, identity.ControlNamespaceId);
            Add(bind, "boot", NpgsqlDbType.Uuid, identity.LinuxBootId);
            Add(bind, "namespaceDevice", NpgsqlDbType.Numeric, (decimal)identity.MountNamespaceDevice);
            Add(bind, "namespaceInode", NpgsqlDbType.Numeric, (decimal)identity.MountNamespaceInode);
            Add(bind, "euid", NpgsqlDbType.Bigint, (long)identity.EffectiveUserId);
            Add(bind, "fileInode", NpgsqlDbType.Numeric, (decimal)identity.FileIdentity.Inode);
            Add(bind, "fileMajor", NpgsqlDbType.Bigint, (long)identity.FileIdentity.DeviceMajor);
            Add(bind, "fileMinor", NpgsqlDbType.Bigint, (long)identity.FileIdentity.DeviceMinor);
            Add(bind, "fileMount", NpgsqlDbType.Numeric, (decimal)identity.FileIdentity.MountId);
            Add(bind, "directoryInode", NpgsqlDbType.Numeric, (decimal)identity.DirectoryIdentity.Inode);
            Add(bind, "directoryMajor", NpgsqlDbType.Bigint, (long)identity.DirectoryIdentity.DeviceMajor);
            Add(bind, "directoryMinor", NpgsqlDbType.Bigint, (long)identity.DirectoryIdentity.DeviceMinor);
            Add(bind, "directoryMount", NpgsqlDbType.Numeric, (decimal)identity.DirectoryIdentity.MountId);
            Add(bind, "boundDigest", NpgsqlDbType.Text, PostgresPrivateObjectMaintenanceTestSupport.BoundDigest.Value);
            Assert.Equal(1, await bind.ExecuteNonQueryAsync());
        }
        await transaction.CommitAsync();
    }

    private static void AddOwner(NpgsqlCommand command, ReservedIngestionFacts facts, IngestionClaim claim)
    {
        Add(command, "organization", NpgsqlDbType.Uuid, facts.Project.OrganizationId);
        Add(command, "project", NpgsqlDbType.Uuid, facts.Project.ProjectId);
        Add(command, "mapping", NpgsqlDbType.Uuid, facts.FutureRevision.MappingId);
        Add(command, "revision", NpgsqlDbType.Uuid, facts.FutureRevision.RevisionId);
        Add(command, "object", NpgsqlDbType.Uuid, facts.NativeObject.Id.ObjectId);
        Add(command, "actor", NpgsqlDbType.Uuid, claim.OriginalActor.Value);
        Add(command, "operation", NpgsqlDbType.Uuid, claim.OperationId);
        Add(command, "generation", NpgsqlDbType.Bigint, claim.Generation);
        Add(command, "instance", NpgsqlDbType.Uuid, claim.ServerInstanceId);
        Add(command, "token", NpgsqlDbType.Uuid, claim.LeaseToken);
        Add(command, "expires", NpgsqlDbType.TimestampTz, claim.LeaseExpiresAtUtc);
    }

    private sealed class IngestionConnection(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
    {
        internal NpgsqlConnection Connection { get; } = connection;
        internal NpgsqlTransaction Transaction { get; } = transaction;
        public ValueTask DisposeAsync() => Connection.DisposeAsync();
    }

    // The ordinary ingestion login, bound to its organization exactly as its pool binds it.
    private static async Task<IngestionConnection> OpenIngestionAsync(PostgresCase test, ProjectScope project)
    {
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(test.IngestionConnectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await using (var scope = new NpgsqlCommand("SELECT set_config('braid.organization_id', $1, true)", connection, transaction))
        {
            scope.Parameters.AddWithValue(project.OrganizationId.ToString("D"));
            await scope.ExecuteNonQueryAsync();
        }
        return new(connection, transaction);
    }

    private static NpgsqlCommand Command(IngestionConnection connection, string sql) =>
        new(sql, connection.Connection, connection.Transaction);

    private static void Add(NpgsqlCommand command, string name, NpgsqlDbType type, object? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value ?? DBNull.Value });
}
