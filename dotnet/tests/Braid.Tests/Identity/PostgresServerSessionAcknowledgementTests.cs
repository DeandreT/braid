using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using Npgsql;
using NpgsqlTypes;
using Braid.Application;
using Braid.Domain;
using Braid.Persistence.Postgres;
using Xunit.Sdk;

namespace Braid.Tests;

/// <summary>
/// Real restricted Create/Revoke, actual server COMMIT frames withheld, and
/// independent unpooled durability observations. Ticket bytes are opaque fixture
/// metadata, not a protected ticket, successful login or delivered logout.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresServerSessionAcknowledgementTests(PostgresFixture fixture)
{
    [PostgresFact]
    public Task CreateCommitAcknowledgementLossIsFailureWithExactKnownKeyDurableReconciliation()
        => RunAsync(revoke: false);

    [PostgresFact]
    public Task RevokeCommitAcknowledgementLossIsFailureWithExactRetainedTombstoneReconciliation()
        => RunAsync(revoke: true);

    private async Task RunAsync(bool revoke)
    {
        PostgresCase? test = null;
        ExternalIdentityBindingProvisioner? identityOwner = null;
        PostgresServerSessionDatabase? writerDatabase = null;
        PostgresServerSessionDatabase? readerDatabase = null;
        PostgresServerSessionDatabase? routedDatabase = null;
        ServerSessionCommitAckLossRelay? relay = null;
        Task<ServerSessionWriteResult>? operation = null;
        Exception? primary = null;
        var secondary = new List<Exception>();
        using var caller = new CancellationTokenSource();
        var phase = "setup";
        try
        {
            test = await fixture.CreateCaseAsync();
            identityOwner = new(test.MigrationConnectionString);
            var organization = Guid.NewGuid();
            var principal = new PrincipalId(Guid.NewGuid());
            await using (var owner = OwnerConnection(test))
            {
                await owner.OpenAsync();
                await using var create = new NpgsqlCommand(
                    "INSERT INTO braid.organizations(id,name) VALUES($1,'Owned session acknowledgement fixture')", owner);
                create.Parameters.AddWithValue(organization);
                create.CommandTimeout = 5;
                await create.ExecuteNonQueryAsync();
            }
            var preparation = await identityOwner.EnsurePrincipalAsync(principal);
            Assert.True(preparation == ExternalIdentityPrincipalPreparationOutcome.Created,
                "The owned principal was not newly prepared.");
            var bound = await identityOwner.BindAsync(new ExternalIdentityKey("session-ack-fixture",
                "literal fixture authority", Guid.NewGuid().ToString("N")), principal);
            Assert.True(bound.Outcome == ExternalIdentityBindingOwnerOutcome.Created && bound.Binding is not null,
                "The owned binding was not newly committed.");
            var binding = bound.Binding ?? throw new InvalidOperationException("The owned binding is absent.");
            DateTimeOffset verifiedDeadline;
            await using (var owner = OwnerConnection(test))
            {
                await owner.OpenAsync();
                await using var clock = new NpgsqlCommand(
                    "SELECT pg_catalog.statement_timestamp()+INTERVAL '50 minutes'", owner)
                { CommandTimeout = 5 };
                var value = await clock.ExecuteScalarAsync();
                Assert.True(value is DateTime { Kind: DateTimeKind.Utc }, "The fixture deadline is not UTC.");
                verifiedDeadline = new((DateTime)value!);
            }
            var key = new ServerSessionKey(organization, Sha256Digest.FromBytes(RandomNumberGenerator.GetBytes(32)));
            var intent = new ServerSessionCreateIntent(key, binding.BindingId, binding.Version, principal,
                Guid.NewGuid(), verifiedDeadline, "opaque synthetic session acknowledgement bytes"u8.ToArray());
            writerDatabase = new(test.SessionWriterConnectionString, ServerSessionDatabaseProfile.Writer);
            readerDatabase = new(test.SessionReaderConnectionString, ServerSessionDatabaseProfile.Reader);
            var writer = new PostgresServerSessionWriter(writerDatabase);
            var reader = new PostgresServerSessionReader(readerDatabase);
            var parents = await ParentRowsAsync(test, caller.Token);
            Snapshot? beforeRevoke = null;
            caller.CancelAfter(TimeSpan.FromSeconds(25));
            if (revoke)
            {
                var created = await writer.CreateOnlyAsync(intent, caller.Token);
                Assert.True(created.Outcome == ServerSessionWriteOutcome.Created && created.Facts is not null,
                    "The ordinary revocation setup did not acknowledge real creation.");
                beforeRevoke = await ReadExactAsync(test, intent, caller.Token);
                Assert.True(beforeRevoke.MatchesIntent && beforeRevoke.Version == 1 && !beforeRevoke.Revoked,
                    "The ordinary creation setup has inconsistent durable facts.");
            }
            else
                Assert.True(await SessionCountAsync(test, caller.Token) == 0,
                    "The creation case does not start with an empty session table.");

            relay = new(test.SessionWriterConnectionString);
            // Equivalent UTC spellings force a real SET LOCAL restoration status at COMMIT.
            var routed = new NpgsqlConnectionStringBuilder(relay.RoutedConnectionString) { Timezone = "Etc/UTC" };
            routedDatabase = new(routed.ConnectionString, ServerSessionDatabaseProfile.Writer);
            var routedWriter = new PostgresServerSessionWriter(routedDatabase);
            phase = "awaiting-real-commit";
            operation = revoke ? routedWriter.RevokeAsync(key, caller.Token) : routedWriter.CreateOnlyAsync(intent, caller.Token);
            var gate = relay.CommitAcknowledgementWithheld;
            await Task.WhenAny(operation, gate).WaitAsync(TimeSpan.FromSeconds(10), caller.Token);
            Assert.True(gate.IsCompletedSuccessfully && !operation.IsCompleted,
                "The real commit gate was not reached while the original operation remained pending.");
            await gate;
            Assert.True(relay.FrontendCommitSeen && relay.BackendCommitCompleteSeen && relay.BackendIdleReadySeen,
                "The real transaction completion and idle acknowledgement were not both withheld.");
            Assert.True(relay.ParameterStatusFramesWithheld is > 0 and <= 16,
                "The real post-commit parameter status frames were not bounded and withheld.");

            phase = "independent-durability";
            // This new unpooled owner connection observes the actual committed row
            // while the original restricted operation has received neither ACK.
            var durable = await ReadExactAsync(test, intent, caller.Token);
            Assert.True(durable.Total == 1 && durable.MatchesIntent,
                "The independent committed row differs from its exact private intent.");
            Assert.True(string.Equals(parents, await ParentRowsAsync(test, caller.Token), StringComparison.Ordinal),
                "The session operation changed private parent or permission facts.");
            if (revoke)
                Assert.True(beforeRevoke is not null && durable.Version == 2 && durable.Revoked &&
                    string.Equals(beforeRevoke.ImmutableRow, durable.ImmutableRow, StringComparison.Ordinal),
                    "The real revocation changed more than its version and tombstone.");
            else
                Assert.True(durable.Version == 1 && !durable.Revoked,
                    "The real creation has inconsistent durable state.");
            Assert.True(!operation.IsCompleted && !relay.Completion.IsCompleted && !caller.IsCancellationRequested,
                "The original operation settled or expired before deliberate acknowledgement loss.");

            phase = "dropping-acknowledgement";
            relay.DropAcknowledgement();
            Exception? originalFailure = null;
            try { await operation.WaitAsync(TimeSpan.FromSeconds(5), caller.Token); }
            catch (Exception error) { originalFailure = error; }
            Assert.True(originalFailure is InvalidOperationException && originalFailure.InnerException is null,
                "Lost commit acknowledgement did not remain a fixed private-safe store failure.");
            Assert.True(!caller.IsCancellationRequested,
                "Caller-budget cancellation cannot satisfy acknowledgement-loss evidence.");
            await relay.Completion.WaitAsync(TimeSpan.FromSeconds(5), caller.Token);
            Assert.True(relay.RequestedDropAccepted,
                "The owned relay did not accept the explicit post-durability drop before its deadline.");

            phase = "explicit-known-key-reconciliation";
            // No Create/Revoke retry is dispatched. The exact known key is observed
            // separately after the original task has genuinely settled with failure.
            var observed = await reader.ObserveAsync(key, caller.Token);
            if (revoke)
                Assert.True(observed is null, "The independent reader accepted a revoked session.");
            else
            {
                Assert.True(observed is not null && observed.PrincipalId == principal,
                    "The explicit known-key observation did not retain its actual owner.");
                var facts = observed?.Facts ?? throw new InvalidOperationException("The known session is absent.");
                Assert.True(facts.Key == key && facts.BindingId == intent.BindingId &&
                    facts.BindingVersion == intent.BindingVersion && facts.LoginEpoch == intent.LoginEpoch &&
                    facts.Version == 1 && facts.RevokedAtUtc is null &&
                    facts.PolicyVersion == intent.PolicyVersion && facts.CodecVersion == intent.CodecVersion &&
                    facts.Lifetime == ServerSessionPolicy.InitialLifetime(facts.Lifetime.IssuedAtUtc, verifiedDeadline) &&
                    facts.CopyProtectedTicket().AsSpan().SequenceEqual(intent.CopyProtectedTicket()),
                    "The reconciled session differs from its exact immutable private facts.");
            }
            var reconciled = await ReadExactAsync(test, intent, caller.Token);
            Assert.True(reconciled.Total == 1 &&
                string.Equals(durable.FullRow, reconciled.FullRow, StringComparison.Ordinal) &&
                string.Equals(parents, await ParentRowsAsync(test, caller.Token), StringComparison.Ordinal),
                "Known-key reconciliation changed the committed row or unrelated private facts.");
        }
        catch (Exception error)
        {
            // Only fixed Boolean assertion diagnostics survive. Provider/transport
            // messages and inner exceptions never enter test output or aggregates.
            primary = error is XunitException ? error :
                new InvalidOperationException("The owned session acknowledgement fixture failed during " + phase + ".");
        }
        finally
        {
            relay?.DropAcknowledgement();
            try { caller.Cancel(); }
            catch (Exception) { secondary.Add(new InvalidOperationException("Owned caller cancellation failed.")); }
            if (relay is not null)
                await CleanupAsync(() => relay.DisposeAsync(), "Owned relay settlement failed.", secondary);
            if (operation is not null)
            {
                try { await operation; }
                catch (Exception) { /* Observe this exact owned call before any database/pool disposal. */ }
            }
            if (routedDatabase is not null)
                await CleanupAsync(() => routedDatabase.DisposeAsync(), "Owned routed pool disposal failed.", secondary);
            if (readerDatabase is not null)
                await CleanupAsync(() => readerDatabase.DisposeAsync(), "Owned reader pool disposal failed.", secondary);
            if (writerDatabase is not null)
                await CleanupAsync(() => writerDatabase.DisposeAsync(), "Owned writer pool disposal failed.", secondary);
            if (identityOwner is not null)
                await CleanupAsync(() => identityOwner.DisposeAsync(), "Owned identity pool disposal failed.", secondary);
            if (test is not null)
                await CleanupAsync(() => test.DisposeAsync(), "Owned case disposal failed.", secondary);
        }
        if (primary is not null)
        {
            if (secondary.Count != 0)
                throw new AggregateException("The session acknowledgement case and cleanup failed.", new[] { primary }.Concat(secondary));
            ExceptionDispatchInfo.Capture(primary).Throw();
        }
        if (secondary.Count != 0)
            throw new AggregateException("The session acknowledgement cleanup failed.", secondary);
    }

    private static NpgsqlConnection OwnerConnection(PostgresCase test) => new(
        new NpgsqlConnectionStringBuilder(test.MigrationConnectionString) { Pooling = false }.ConnectionString);

    private static async Task<long> SessionCountAsync(PostgresCase test, CancellationToken token)
    {
        await using var owner = OwnerConnection(test);
        await owner.OpenAsync(token);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM ONLY braid.server_sessions", owner) { CommandTimeout = 3 };
        return (long)(await command.ExecuteScalarAsync(token) ?? throw new InvalidOperationException("The private count is absent."));
    }

    private static async Task<Snapshot> ReadExactAsync(PostgresCase test, ServerSessionCreateIntent intent,
        CancellationToken token)
    {
        await using var owner = OwnerConnection(test);
        await owner.OpenAsync(token);
        await using var command = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM ONLY braid.server_sessions),to_jsonb(s)::text,
                (to_jsonb(s)-'version'-'revoked_at')::text,s.version,s.revoked_at IS NOT NULL,
                s.organization_id=@org AND s.handle_digest=@digest AND s.binding_id=@binding
                AND s.binding_version=@bindingVersion AND s.login_epoch=@epoch
                AND s.protected_ticket=@ticket AND s.policy_version=@policy AND s.codec_version=@codec
                AND pg_catalog.isfinite(s.issued_at) AND pg_catalog.isfinite(s.idle_expires_at)
                AND pg_catalog.isfinite(s.absolute_expires_at) AND s.issued_at<s.idle_expires_at
                AND s.absolute_expires_at=LEAST(s.issued_at+INTERVAL '60 minutes',@deadline)
                AND s.idle_expires_at=LEAST(s.issued_at+INTERVAL '20 minutes',s.absolute_expires_at)
                AND (s.revoked_at IS NULL OR (pg_catalog.isfinite(s.revoked_at) AND s.revoked_at>=s.issued_at))
            FROM ONLY braid.server_sessions s WHERE s.organization_id=@org AND s.handle_digest=@digest
            """, owner) { CommandTimeout = 3 };
        command.Parameters.AddWithValue("org", NpgsqlDbType.Uuid, intent.Key.OrganizationId);
        command.Parameters.AddWithValue("digest", NpgsqlDbType.Bytea, Convert.FromHexString(intent.Key.HandleDigest.Value));
        command.Parameters.AddWithValue("binding", NpgsqlDbType.Uuid, intent.BindingId);
        command.Parameters.AddWithValue("bindingVersion", NpgsqlDbType.Bigint, intent.BindingVersion);
        command.Parameters.AddWithValue("epoch", NpgsqlDbType.Uuid, intent.LoginEpoch);
        command.Parameters.AddWithValue("ticket", NpgsqlDbType.Bytea, intent.CopyProtectedTicket());
        command.Parameters.AddWithValue("policy", NpgsqlDbType.Integer, intent.PolicyVersion);
        command.Parameters.AddWithValue("codec", NpgsqlDbType.Integer, intent.CodecVersion);
        command.Parameters.AddWithValue("deadline", NpgsqlDbType.TimestampTz, intent.VerifiedLoginDeadlineUtc);
        await using var reader = await command.ExecuteReaderAsync(token);
        Assert.True(await reader.ReadAsync(token), "The independent exact committed session row is absent.");
        var result = new Snapshot(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
            reader.GetInt64(3), reader.GetBoolean(4), reader.GetBoolean(5));
        Assert.True(!await reader.ReadAsync(token), "The private exact session query returned duplicate facts.");
        return result;
    }

    private static async Task<string> ParentRowsAsync(PostgresCase test, CancellationToken token)
    {
        // Source-owned fixed names only; no generic SQL or private diagnostic seam.
        string[] names = ["organizations", "principals", "external_identity_bindings", "organization_memberships",
            "projects", "project_memberships", "organization_project_creation_capabilities", "project_payload_read_grants"];
        var fields = names.Select(name => "'" + name + "',(SELECT COALESCE(jsonb_agg(to_jsonb(r) ORDER BY to_jsonb(r)::text),'[]'::jsonb) FROM ONLY braid." + name + " r)");
        await using var owner = OwnerConnection(test);
        await owner.OpenAsync(token);
        await using var command = new NpgsqlCommand("SELECT jsonb_build_object(" + string.Join(",", fields) + ")::text", owner)
        { CommandTimeout = 3 };
        return (string)(await command.ExecuteScalarAsync(token) ?? throw new InvalidOperationException("The private parent snapshot is absent."));
    }

    private static async Task CleanupAsync(Func<ValueTask> dispose, string category, List<Exception> secondary)
    {
        try { await dispose(); }
        catch (Exception) { secondary.Add(new InvalidOperationException(category)); }
    }

    private sealed record Snapshot(long Total, string FullRow, string ImmutableRow, long Version, bool Revoked, bool MatchesIntent)
    {
        public override string ToString() => nameof(Snapshot);
    }
}
