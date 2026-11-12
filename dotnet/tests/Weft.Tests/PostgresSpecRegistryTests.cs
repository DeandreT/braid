using Npgsql;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

/// <summary>Actual PostgreSQL transaction/role tests. Require a disposable database with the REAL ordered 0044 installed.</summary>
public sealed class PostgresSpecRegistryTests
{
    [SpecPostgresFact]
    public async Task Create_and_reserve_are_exact_idempotent_and_metadata_membership_is_not_content_access()
    {
        await using var f = await Fixture.Create();
        var key = Guid.NewGuid();
        Assert.Equal(await f.Writer.CreateAsync(f.Scope, key, default), await f.Writer.CreateAsync(f.Scope, key, default));
        var reference = f.Reference(); var attempt = Guid.NewGuid();
        var first = await f.Writer.ReserveAsync(reference, SpecSourceFormat.Text, attempt, 1, null, default);
        Assert.Equal(first, await f.Writer.ReserveAsync(reference, SpecSourceFormat.Text, attempt, 1, null, default));
        await Error(SpecStorageError.Conflict, () => f.Writer.ReserveAsync(new(reference.Id, Sha256Digest.FromBytes([2]), 1), SpecSourceFormat.Text, attempt, 1, null, default));
        await f.Owner("UPDATE weft.project_payload_read_grants SET active=false,version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3", f.Scope.Project.OrganizationId, f.Scope.Project.ProjectId, f.Actor);
        await Error(SpecStorageError.NotAdmitted, () => f.Reader.AuthorizeAsync(f.Scope, false, default));
        await Error(SpecStorageError.NotAdmitted, () => f.Writer.AdmitPublicationAsync(first.Claim, default));
    }

    [SpecPostgresFact]
    public async Task Competing_source_reservations_have_one_cas_winner()
    {
        await using var f = await Fixture.Create();
        await f.Writer.CreateAsync(f.Scope, Guid.NewGuid(), default);
        async Task<SpecStorageError?> Reserve()
        {
            try { await f.Writer.ReserveAsync(f.Reference(), SpecSourceFormat.Text, Guid.NewGuid(), 1, null, default); return null; }
            catch (SpecStorageException e) { return e.Error; }
        }
        var results = await Task.WhenAll(Reserve(), Reserve());
        Assert.Single(results, result => result is null);
        Assert.Single(results, result => result == SpecStorageError.Conflict);
    }

    [SpecPostgresFact]
    public async Task Publication_holds_database_fence_and_stale_worker_is_refused_after_tombstone()
    {
        await using var f = await Fixture.Create();
        await f.Writer.CreateAsync(f.Scope, Guid.NewGuid(), default);
        var reservation = await f.Writer.ReserveAsync(f.Reference(), SpecSourceFormat.Text, Guid.NewGuid(), 1, null, default);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publication = f.Writer.ExecutePublicationAsync(reservation.Claim, async _ =>
        {
            entered.SetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(15)); return SpecPublishResult.Created;
        }, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var tombstone = f.Writer.TombstoneAsync(f.Scope, null, 2, Guid.NewGuid(), default);
        try { await f.WaitForDatabaseBlock(); Assert.False(tombstone.IsCompleted); }
        finally { release.TrySetResult(); }
        await publication; Assert.True((await tombstone).Tombstoned);
        var invoked = false;
        await Error(SpecStorageError.NotAdmitted, () => f.Writer.ExecutePublicationAsync(reservation.Claim, _ =>
        { invoked = true; return Task.FromResult(SpecPublishResult.Created); }, default));
        Assert.False(invoked);
        var retirement = Assert.Single(await f.Maintenance.RetirementsAsync(f.Scope, default));
        Assert.Equal(SpecRetireResult.Missing, await f.Maintenance.ExecuteRetirementAsync(retirement, _ => Task.FromResult(SpecRetireResult.Missing), default));
        Assert.Empty(await f.Maintenance.RetirementsAsync(f.Scope, default));
    }

    [SpecPostgresFact]
    public async Task Failed_callback_rolls_back_acknowledgement_and_same_claim_is_recoverable()
    {
        await using var f = await Fixture.Create();
        await f.Writer.CreateAsync(f.Scope, Guid.NewGuid(), default);
        var reservation = await f.Writer.ReserveAsync(f.Reference(), SpecSourceFormat.Text, Guid.NewGuid(), 1, null, default);
        await Error(SpecStorageError.Unavailable, () => f.Writer.ExecutePublicationAsync(reservation.Claim, _ => throw new IOException("uncertain physical result"), default));
        await Error(SpecStorageError.NotAdmitted, () => f.Reader.FindAsync(f.Scope, reservation.Claim.Reference.Id.ObjectId, default));
        Assert.Equal(SpecPublishResult.AlreadyPresent, await f.Writer.ExecutePublicationAsync(reservation.Claim, _ => Task.FromResult(SpecPublishResult.AlreadyPresent), default));
        Assert.Equal(reservation.Claim.Reference, await f.Reader.FindAsync(f.Scope, reservation.Claim.Reference.Id.ObjectId, default));
    }

    [SpecPostgresFact]
    public async Task Content_grant_revocation_waits_for_admitted_read_then_denies_the_next_read()
    {
        await using var f = await Fixture.Create();
        await f.Writer.CreateAsync(f.Scope, Guid.NewGuid(), default);
        var reservation = await f.Writer.ReserveAsync(f.Reference(), SpecSourceFormat.Text, Guid.NewGuid(), 1, null, default);
        await f.Writer.ExecutePublicationAsync(reservation.Claim, _ => Task.FromResult(SpecPublishResult.Created), default);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = f.Reader.ExecuteReadAsync(f.Store, reservation.Claim.Reference, async _ =>
        {
            entered.SetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(15)); return new byte[] { 1 };
        }, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var revoke = f.Owner("UPDATE weft.project_payload_read_grants SET active=false,version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3",
            f.Scope.Project.OrganizationId, f.Scope.Project.ProjectId, f.Actor);
        try { await f.WaitForDatabaseBlock(); Assert.False(revoke.IsCompleted); }
        finally { release.TrySetResult(); }
        Assert.Equal(new byte[] { 1 }, await read);
        await revoke;
        await Error(SpecStorageError.NotAdmitted, () => f.Reader.FindAsync(f.Scope, reservation.Claim.Reference.Id.ObjectId, default));
    }

    [SpecPostgresFact]
    public async Task Temporary_inode_binding_is_cas_recoverable_and_cleanup_uses_issued_exact_claim()
    {
        await using var f = await Fixture.Create(SpecStoreKind.FileSystem);
        await f.Writer.CreateAsync(f.Scope, Guid.NewGuid(), default);
        var reservation = await f.Writer.ReserveAsync(f.Reference(), SpecSourceFormat.Text, Guid.NewGuid(), 1, null, default);
        var temp = Guid.NewGuid();
        var reserved = await f.Writer.AdvanceTemporaryAsync(reservation.Claim, temp, SpecTemporaryPhase.Reserved, SpecBundleLifecycleTests.Evidence(), null);
        Assert.Equal(reserved.SealDigest, (await f.Writer.AdvanceTemporaryAsync(reservation.Claim, temp, SpecTemporaryPhase.Reserved, SpecBundleLifecycleTests.Evidence(), null)).SealDigest);
        await Error(SpecStorageError.Conflict, () => f.Writer.AdvanceTemporaryAsync(reservation.Claim, temp, SpecTemporaryPhase.Bound, SpecBundleLifecycleTests.Evidence(true), Sha256Digest.FromBytes([9])));
        var bound = await f.Writer.AdvanceTemporaryAsync(reservation.Claim, temp, SpecTemporaryPhase.Bound, SpecBundleLifecycleTests.Evidence(true), reserved.SealDigest);
        var prepared = await f.Writer.AdvanceTemporaryAsync(reservation.Claim, temp, SpecTemporaryPhase.Prepared, SpecBundleLifecycleTests.Evidence(true), bound.SealDigest);
        Assert.Equal(prepared.SealDigest, Assert.Single(await f.Writer.RecoverTemporariesAsync(reservation.Claim)).SealDigest);
        await f.Writer.TombstoneAsync(f.Scope, null, 2, Guid.NewGuid(), default);
        var retirement = Assert.Single(await f.Maintenance.RetirementsAsync(f.Scope, default));
        var candidate = Assert.Single(await f.Maintenance.RetiringTemporariesAsync(retirement));
        Assert.NotNull(candidate.RetirementId);
        var called = false;
        await Error(SpecStorageError.NotAdmitted, () => f.Maintenance.ExecuteTemporaryRetirementAsync(retirement,
            candidate with { SealDigest = Sha256Digest.FromBytes([8]) }, _ => { called = true; return Task.CompletedTask; }));
        Assert.False(called);
        await f.Maintenance.ExecuteTemporaryRetirementAsync(retirement, candidate, _ => { called = true; return Task.CompletedTask; });
        Assert.True(called);
        Assert.True(Assert.Single(await f.Maintenance.RetiringTemporariesAsync(retirement)).Retired);
    }

    [SpecPostgresFact]
    public async Task Restricted_login_cannot_read_tables_or_mutate_through_reader_gate()
    {
        await using var f = await Fixture.Create();
        await f.Writer.CreateAsync(f.Scope, Guid.NewGuid(), default);
        var reservation = await f.Writer.ReserveAsync(f.Reference(), SpecSourceFormat.Text, Guid.NewGuid(), 1, null, default);
        await using var connection = new NpgsqlConnection(Environment.GetEnvironmentVariable("WEFT_SPEC_TEST_READER"));
        await connection.OpenAsync();
        await using var table = new NpgsqlCommand("SELECT * FROM weft.spec_objects", connection);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => table.ExecuteNonQueryAsync())).SqlState);
        await using var gate = new NpgsqlCommand("SELECT * FROM weft.spec_object_gate($1,$2,$3,$4,$5,$6,'publish')", connection);
        foreach (var id in new[] { f.Actor, f.Scope.Project.OrganizationId, f.Scope.Project.ProjectId, f.Scope.BundleId, reservation.Claim.Reference.Id.ObjectId, reservation.Claim.AttemptId }) gate.Parameters.AddWithValue(id);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => gate.ExecuteNonQueryAsync())).SqlState);
    }

    private static async Task Error(SpecStorageError expected, Func<Task> action) => Assert.Equal(expected, (await Assert.ThrowsAsync<SpecStorageException>(action)).Error);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly List<PostgresSpecBundleDatabase> databases = [];
        internal Guid Actor { get; } = Guid.NewGuid();
        internal Guid Store { get; } = Guid.NewGuid();
        internal SpecBundleScope Scope { get; } = new(new(Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid());
        private string ApplicationName { get; } = "weft-spec-qualification-" + Guid.NewGuid().ToString("N");
        internal PostgresSpecBundleSession Writer { get; private set; } = null!;
        internal PostgresSpecBundleSession Reader { get; private set; } = null!;
        internal PostgresSpecBundleSession Maintenance { get; private set; } = null!;
        internal SpecObjectReference Reference() => new(new(new(Scope, Guid.NewGuid(), 1), SpecObjectKind.Original, Guid.NewGuid()), Sha256Digest.FromBytes([1]), 1);
        internal static async Task<Fixture> Create(SpecStoreKind kind = SpecStoreKind.Blob)
        {
            var f = new Fixture();
            // No schema bootstrap, role creation, migration renumbering or placeholder predecessors in this suite.
            await f.Owner("""
                DO $check$ BEGIN
                    IF current_user<>'weft_migrator' OR (SELECT count(*) FROM weft.schema_migrations WHERE version IN (42,43,44))<>3 THEN
                        RAISE EXCEPTION 'A disposable database with real migrations through 0044 is required.';
                    END IF;
                END $check$;
                """);
            await f.Owner("INSERT INTO weft.organizations(id,name) VALUES($1,'Spec qualification')", f.Scope.Project.OrganizationId);
            await f.Owner("INSERT INTO weft.principals(id) VALUES($1)", f.Actor);
            await f.Owner("INSERT INTO weft.organization_memberships(organization_id,principal_id) VALUES($1,$2)", f.Scope.Project.OrganizationId, f.Actor);
            await f.Owner("INSERT INTO weft.projects(organization_id,id,name,creator_principal_id) VALUES($1,$2,'Spec qualification',$3)", f.Scope.Project.OrganizationId, f.Scope.Project.ProjectId, f.Actor);
            await f.Owner("INSERT INTO weft.project_memberships(organization_id,project_id,principal_id,role) VALUES($1,$2,$3,'publisher')", f.Scope.Project.OrganizationId, f.Scope.Project.ProjectId, f.Actor);
            await f.Owner("INSERT INTO weft.project_payload_read_grants(organization_id,project_id,principal_id) VALUES($1,$2,$3)", f.Scope.Project.OrganizationId, f.Scope.Project.ProjectId, f.Actor);
            var binding = new SpecStoreBinding(f.Store, kind, Sha256Digest.FromBytes(f.Store.ToByteArray()));
            await f.Owner("INSERT INTO weft.spec_stores(id,kind,location_digest,active) VALUES($1,$2,$3,true)", f.Store, kind == SpecStoreKind.Blob ? "blob" : "filesystem", binding.LocationDigest.Value);
            PostgresSpecBundleDatabase Open(string name, SpecDatabaseProfile profile)
            {
                var builder = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable(name)) { ApplicationName = f.ApplicationName };
                var database = new PostgresSpecBundleDatabase(builder.ConnectionString, profile); f.databases.Add(database); return database;
            }
            f.Writer = Open("WEFT_SPEC_TEST_WRITER", SpecDatabaseProfile.Writer).ForActor(new(new(f.Actor)), binding);
            f.Reader = Open("WEFT_SPEC_TEST_READER", SpecDatabaseProfile.Reader).ForActor(new(new(f.Actor)), binding);
            f.Maintenance = Open("WEFT_SPEC_TEST_MAINTENANCE", SpecDatabaseProfile.Maintenance).ForMaintenance(binding);
            return f;
        }
        internal async Task Owner(string sql, params object[] parameters)
        {
            var builder = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("WEFT_SPEC_TEST_OWNER")) { ApplicationName = ApplicationName };
            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync(); await using var command = new NpgsqlCommand(sql, connection);
            foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter);
            await command.ExecuteNonQueryAsync();
        }
        internal async Task WaitForDatabaseBlock()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await using var connection = new NpgsqlConnection(Environment.GetEnvironmentVariable("WEFT_SPEC_TEST_OWNER"));
            await connection.OpenAsync(timeout.Token);
            while (true)
            {
                await using var command = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE application_name=$1 AND cardinality(pg_blocking_pids(pid))>0)", connection);
                command.Parameters.AddWithValue(ApplicationName);
                if (await command.ExecuteScalarAsync(timeout.Token) is true) return;
                await Task.Delay(20, timeout.Token);
            }
        }
        public async ValueTask DisposeAsync()
        {
            foreach (var database in databases) await database.DisposeAsync();
            // Intentionally retain append-only fixture evidence. Run only against an explicitly disposable test database.
        }
    }
}

public sealed class SpecPostgresFactAttribute : FactAttribute
{
    public SpecPostgresFactAttribute()
    {
        if (new[] { "WEFT_SPEC_TEST_OWNER", "WEFT_SPEC_TEST_READER", "WEFT_SPEC_TEST_WRITER", "WEFT_SPEC_TEST_MAINTENANCE" }
            .Any(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))))
            Skip = "Requires actual disposable PostgreSQL with real 0042/0043/0044, provisioned spec roles and four explicit test connections.";
    }
}
