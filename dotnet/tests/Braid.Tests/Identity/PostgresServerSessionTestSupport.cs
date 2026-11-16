using System.Diagnostics;
using Npgsql;
using Braid.Application;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

/// <summary>Owned PG metadata cases with opaque synthetic bytes; no protected-ticket codec or authentication.</summary>
internal sealed class PostgresServerSessionTestSupport : IAsyncDisposable
{
    internal PostgresCase Case { get; }
    internal Guid Organization { get; }
    internal Guid OtherOrganization { get; }
    internal PrincipalId Principal { get; }
    internal ExternalIdentityBinding Binding { get; }
    internal ExternalIdentityBindingProvisioner IdentityOwner { get; }
    internal PostgresServerSessionDatabase ReaderDatabase { get; }
    internal PostgresServerSessionDatabase WriterDatabase { get; }
    internal PostgresServerSessionReader Reader { get; }
    internal PostgresServerSessionWriter Writer { get; }
    internal static byte[] Ticket() => [0, 255, 128, 7, 0, 9];
    internal const string Columns = "handle_digest,organization_id,binding_id,binding_version,login_epoch,issued_at,idle_expires_at,absolute_expires_at,version,revoked_at,policy_version,codec_version,protected_ticket";

    private PostgresServerSessionTestSupport(PostgresCase test, Guid organization, Guid otherOrganization,
        PrincipalId principal, ExternalIdentityBinding binding, ExternalIdentityBindingProvisioner identityOwner)
    {
        Case = test;
        Organization = organization;
        OtherOrganization = otherOrganization;
        Principal = principal;
        Binding = binding;
        IdentityOwner = identityOwner;
        ReaderDatabase = new(test.SessionReaderConnectionString, ServerSessionDatabaseProfile.Reader);
        WriterDatabase = new(test.SessionWriterConnectionString, ServerSessionDatabaseProfile.Writer);
        Reader = new(ReaderDatabase);
        Writer = new(WriterDatabase);
    }

    internal static async Task<PostgresServerSessionTestSupport> CreateAsync(PostgresFixture fixture)
    {
        var test = await fixture.CreateCaseAsync();
        var owner = new ExternalIdentityBindingProvisioner(test.MigrationConnectionString);
        try
        {
            var organization = Guid.NewGuid();
            var other = Guid.NewGuid();
            await test.ExecuteAsync("INSERT INTO braid.organizations(id,name) VALUES($1,'Session metadata A'),($2,'Session metadata B')", organization, other);
            var principal = new PrincipalId(Guid.NewGuid());
            Assert.Equal(ExternalIdentityPrincipalPreparationOutcome.Created, await owner.EnsurePrincipalAsync(principal));
            var result = await owner.BindAsync(new ExternalIdentityKey("session-test", "https://identity.example/tenant", Guid.NewGuid().ToString("N")), principal);
            Assert.Equal(ExternalIdentityBindingOwnerOutcome.Created, result.Outcome);
            return new(test, organization, other, principal, Assert.IsType<ExternalIdentityBinding>(result.Binding), owner);
        }
        catch
        {
            try { await owner.DisposeAsync(); }
            finally { await test.DisposeAsync(); }
            throw;
        }
    }

    internal ServerSessionKey Key(Guid? organization = null) =>
        new(organization ?? Organization, Sha256Digest.FromBytes(Guid.NewGuid().ToByteArray()));

    internal async Task<DateTimeOffset> DeadlineAsync(int minutes = 90)
    {
        var value = await Case.ScalarAsync<DateTime>("SELECT statement_timestamp()+$1*interval '1 minute'", minutes);
        return new DateTimeOffset(value);
    }

    internal async Task<ServerSessionCreateIntent> IntentAsync(ServerSessionKey? key = null,
        DateTimeOffset? deadline = null, PrincipalId? expectedPrincipal = null, long? bindingVersion = null,
        Guid? epoch = null, byte[]? ticket = null, Guid? bindingId = null) =>
        new(key ?? Key(), bindingId ?? Binding.BindingId, bindingVersion ?? Binding.Version,
            expectedPrincipal ?? Principal, epoch ?? Guid.NewGuid(), deadline ?? await DeadlineAsync(), ticket ?? Ticket());

    internal async Task<ServerSessionFacts> CreateAsync(ServerSessionCreateIntent? intent = null) =>
        Acknowledged(await Writer.CreateOnlyAsync(intent ?? await IntentAsync()), ServerSessionWriteOutcome.Created);

    internal static ServerSessionFacts Acknowledged(ServerSessionWriteResult result, ServerSessionWriteOutcome outcome)
    {
        Assert.Equal(outcome, result.Outcome);
        return Assert.IsType<ServerSessionFacts>(result.Facts);
    }

    internal static void WithoutFacts(ServerSessionWriteResult result, ServerSessionWriteOutcome outcome)
    {
        Assert.Equal(outcome, result.Outcome);
        Assert.Null(result.Facts);
    }

    internal static void SamePrivate(string expected, string actual) =>
        Assert.True(string.Equals(expected, actual, StringComparison.Ordinal), "A private session or authority snapshot changed.");

    internal static void SameImmutable(ServerSessionFacts expected, ServerSessionFacts actual)
    {
        Assert.True(expected.Key == actual.Key && expected.BindingId == actual.BindingId &&
            expected.BindingVersion == actual.BindingVersion && expected.LoginEpoch == actual.LoginEpoch &&
            expected.Lifetime.IssuedAtUtc == actual.Lifetime.IssuedAtUtc &&
            expected.Lifetime.AbsoluteExpiresAtUtc == actual.Lifetime.AbsoluteExpiresAtUtc &&
            expected.PolicyVersion == actual.PolicyVersion && expected.CodecVersion == actual.CodecVersion &&
            expected.CopyProtectedTicket().AsSpan().SequenceEqual(actual.CopyProtectedTicket()),
            "Immutable private session metadata changed.");
    }

    internal static void SameFacts(ServerSessionFacts expected, ServerSessionFacts actual)
    {
        SameImmutable(expected, actual);
        Assert.True(expected.Version == actual.Version && expected.RevokedAtUtc == actual.RevokedAtUtc &&
            expected.Lifetime.IdleExpiresAtUtc == actual.Lifetime.IdleExpiresAtUtc,
            "Private session state did not match its acknowledged facts.");
    }

    internal async Task<CurrentServerSession> CurrentAsync(ServerSessionFacts expected)
    {
        var current = Assert.IsType<CurrentServerSession>(await Reader.ObserveAsync(expected.Key));
        Assert.True(current.PrincipalId == Principal, "The private session observation returned a foreign principal.");
        SameFacts(expected, current.Facts);
        return current;
    }

    internal Task<string> RowsAsync() => Case.ScalarAsync<string>($"""
        SELECT COALESCE(jsonb_agg(to_jsonb(s) ORDER BY organization_id,handle_digest),'[]'::jsonb)::text
        FROM (SELECT {Columns} FROM braid.server_sessions) s
        """);

    internal Task<string> AuthorityAsync() => Case.ScalarAsync<string>("""
        SELECT jsonb_build_object(
            'organizations',(SELECT jsonb_agg(to_jsonb(r) ORDER BY id) FROM braid.organizations r),
            'principals',(SELECT jsonb_agg(to_jsonb(r) ORDER BY id) FROM braid.principals r),
            'bindings',(SELECT jsonb_agg(to_jsonb(r) ORDER BY binding_id) FROM braid.external_identity_bindings r),
            'admissions',(SELECT jsonb_agg(to_jsonb(r) ORDER BY organization_id,principal_id) FROM braid.organization_memberships r),
            'projects',(SELECT jsonb_agg(to_jsonb(r) ORDER BY organization_id,id) FROM braid.projects r),
            'memberships',(SELECT jsonb_agg(to_jsonb(r) ORDER BY organization_id,project_id,principal_id) FROM braid.project_memberships r),
            'capabilities',(SELECT jsonb_agg(to_jsonb(r) ORDER BY organization_id,principal_id) FROM braid.organization_project_creation_capabilities r),
            'payload',(SELECT jsonb_agg(to_jsonb(r) ORDER BY organization_id,project_id,principal_id) FROM braid.project_payload_read_grants r)
        )::text
        """);

    // Historical metadata uses the enabled original guard and the actual statement clock.
    // It does not pretend that the ordinary CreateOnly port was called in the past.
    internal async Task<ServerSessionFacts> HistoricalAsync(ServerSessionKey? key = null,
        int ageMinutes = 5, int absoluteMinutes = 60)
    {
        key ??= Key();
        await using var connection = Unpooled(Case.MigrationConnectionString);
        await connection.OpenAsync();
        await using var command = HistoricalInsert(connection, null, key, Binding, Guid.NewGuid(), Ticket(), ageMinutes, absoluteMinutes);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        return await StoredAsync(key);
    }

    internal static NpgsqlCommand HistoricalInsert(NpgsqlConnection connection, NpgsqlTransaction? transaction,
        ServerSessionKey key, ExternalIdentityBinding binding, Guid epoch, byte[] ticket,
        int ageMinutes = 5, int absoluteMinutes = 60)
    {
        var command = new NpgsqlCommand("""
            WITH lifetime AS (SELECT statement_timestamp()-$6*interval '1 minute' AS issued)
            INSERT INTO braid.server_sessions(handle_digest,organization_id,binding_id,binding_version,login_epoch,
                issued_at,idle_expires_at,absolute_expires_at,version,revoked_at,policy_version,codec_version,protected_ticket)
            SELECT $1,$2,$3,$4,$5,issued,LEAST(issued+interval '20 minutes',issued+$7*interval '1 minute'),
                issued+$7*interval '1 minute',1,NULL,1,1,$8 FROM lifetime
            """, connection, transaction);
        command.Parameters.AddWithValue(Bytes(key));
        command.Parameters.AddWithValue(key.OrganizationId);
        command.Parameters.AddWithValue(binding.BindingId);
        command.Parameters.AddWithValue(binding.Version);
        command.Parameters.AddWithValue(epoch);
        command.Parameters.AddWithValue(ageMinutes);
        command.Parameters.AddWithValue(absoluteMinutes);
        command.Parameters.AddWithValue(ticket);
        return command;
    }

    internal async Task<ServerSessionFacts> StoredAsync(ServerSessionKey key)
    {
        await using var command = Case.Owner.CreateCommand($"SELECT {Columns} FROM braid.server_sessions WHERE organization_id=$1 AND handle_digest=$2");
        command.Parameters.AddWithValue(key.OrganizationId);
        command.Parameters.AddWithValue(Bytes(key));
        await using var rows = await command.ExecuteReaderAsync();
        Assert.True(await rows.ReadAsync(), "The owned session fixture row is absent.");
        var actualKey = new ServerSessionKey(rows.GetGuid(1), Sha256Digest.Parse(Convert.ToHexStringLower(rows.GetFieldValue<byte[]>(0))));
        Assert.True(key == actualKey, "The owned session fixture returned a foreign key.");
        var result = new ServerSessionFacts(actualKey, rows.GetGuid(2), rows.GetInt64(3), rows.GetGuid(4),
            new(rows.GetFieldValue<DateTimeOffset>(5), rows.GetFieldValue<DateTimeOffset>(6), rows.GetFieldValue<DateTimeOffset>(7)),
            rows.GetInt64(8), rows.GetFieldValue<byte[]>(12), rows.IsDBNull(9) ? null : rows.GetFieldValue<DateTimeOffset>(9),
            rows.GetInt32(10), rows.GetInt32(11));
        Assert.False(await rows.ReadAsync(), "The owned session fixture returned duplicate rows.");
        return result;
    }

    internal static byte[] Bytes(ServerSessionKey key) => Convert.FromHexString(key.HandleDigest.Value);
    internal static NpgsqlConnection Unpooled(string value) => new(new NpgsqlConnectionStringBuilder(value) { Pooling = false }.ConnectionString);
    internal static string Application(string value, string name) => new NpgsqlConnectionStringBuilder(value) { ApplicationName = name }.ConnectionString;

    internal static async Task<int> ResetAsync(PostgresServerSessionDatabase database)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT pg_backend_pid(),COALESCE(current_setting('braid.organization_id',true),'')='',current_setting('transaction_read_only')='off'", connection);
        await using var rows = await command.ExecuteReaderAsync();
        Assert.True(await rows.ReadAsync());
        Assert.True(rows.GetBoolean(1) && rows.GetBoolean(2), "The returned session pool retained transaction state.");
        return rows.GetInt32(0);
    }

    internal static async Task<NpgsqlCommand> PrepareBlockedAsync(NpgsqlConnection observer, string application, int blocker)
    {
        var command = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_stat_activity a WHERE a.datname=current_database() AND a.application_name=$1 AND $2=ANY(pg_catalog.pg_blocking_pids(a.pid)))", observer);
        command.Parameters.AddWithValue(application);
        command.Parameters.AddWithValue(blocker);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await command.PrepareAsync(deadline.Token);
            Assert.False(Assert.IsType<bool>(await command.ExecuteScalarAsync(deadline.Token)));
            return command;
        }
        catch { await command.DisposeAsync(); throw; }
    }

    internal static async Task WaitBlockedAsync(NpgsqlCommand command, params Task[] operations)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            await RequirePendingAsync(operations, "An owned session operation settled before blocking was observed.");
            var blocked = Assert.IsType<bool>(await command.ExecuteScalarAsync(deadline.Token));
            await RequirePendingAsync(operations, "An owned session operation settled during blocking observation.");
            if (blocked) return;
            if (elapsed.Elapsed >= TimeSpan.FromSeconds(5)) throw new TimeoutException("The owned session blocking edge was not observed.");
            await Task.Delay(TimeSpan.FromMilliseconds(5), deadline.Token);
        }
    }

    private static async Task RequirePendingAsync(Task[] operations, string message)
    {
        foreach (var operation in operations)
        {
            if (!operation.IsCompleted) continue;
            // Preserve the original port's categorical failure/cancellation instead
            // of replacing an expired lock timeout with an observer assertion.
            await operation;
            throw new InvalidOperationException(message);
        }
    }

    // Cleanup only: successful/result proof always occurs before its finally path.
    internal static async Task SettleAsync(Task operation)
    {
        try { await operation; }
        catch (Exception) { }
    }

    internal static async Task ReleaseAsync(NpgsqlTransaction transaction, bool released, bool bodyCompleted)
    {
        if (released) return;
        try { await transaction.RollbackAsync(); }
        catch (Exception) when (!bodyCompleted)
        {
            // Preserve an existing observation/assertion failure. A rollback failure
            // following a successful body still fails the test normally.
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await ReaderDatabase.DisposeAsync(); }
        finally
        {
            try { await WriterDatabase.DisposeAsync(); }
            finally
            {
                try { await IdentityOwner.DisposeAsync(); }
                finally { await Case.DisposeAsync(); }
            }
        }
    }
}
