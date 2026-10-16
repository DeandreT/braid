using Npgsql;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresServerSessionBehaviorTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task AcknowledgedCreationUsesOneBoundedUtcLifetimeAndOpaqueCopiedMetadataWithoutGrantInference()
    {
        await using var test = await PostgresServerSessionTestSupport.CreateAsync(fixture);
        var before = await test.AuthorityAsync();
        var intent = await test.IntentAsync();
        var facts = await test.CreateAsync(intent);
        Assert.True(facts.Key == intent.Key && facts.BindingId == intent.BindingId &&
            facts.BindingVersion == intent.BindingVersion && facts.LoginEpoch == intent.LoginEpoch &&
            facts.CopyProtectedTicket().AsSpan().SequenceEqual(intent.CopyProtectedTicket()),
            "Creation did not preserve its private exact intent.");
        Assert.True(facts.Version == 1 && facts.RevokedAtUtc is null && facts.PolicyVersion == 1 && facts.CodecVersion == 1);
        var lifetime = facts.Lifetime;
        Assert.True(lifetime.IssuedAtUtc.Offset == TimeSpan.Zero && lifetime.IdleExpiresAtUtc.Offset == TimeSpan.Zero &&
            lifetime.AbsoluteExpiresAtUtc.Offset == TimeSpan.Zero && lifetime.IssuedAtUtc.Ticks % 10 == 0 &&
            lifetime.IdleExpiresAtUtc.Ticks % 10 == 0 && lifetime.AbsoluteExpiresAtUtc.Ticks % 10 == 0,
            "Creation returned noncanonical private timestamp metadata.");
        Assert.True(lifetime.IdleExpiresAtUtc - lifetime.IssuedAtUtc == TimeSpan.FromMinutes(20) &&
            lifetime.AbsoluteExpiresAtUtc - lifetime.IssuedAtUtc == TimeSpan.FromMinutes(60),
            "Creation did not apply the fixed statement-time lifetime.");
        var copy = facts.CopyProtectedTicket();
        copy[0] ^= 255;
        await test.CurrentAsync(facts);
        PostgresServerSessionTestSupport.SamePrivate(before, await test.AuthorityAsync());
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("""
            SELECT (SELECT count(*) FROM weft.organization_memberships)+(SELECT count(*) FROM weft.project_memberships)
                +(SELECT count(*) FROM weft.organization_project_creation_capabilities)+(SELECT count(*) FROM weft.project_payload_read_grants)
            """));
    }

    [PostgresFact]
    public async Task ReusedSeparatePoolsIsolateTheFullOrganizationDigestKeyAndReturnWithoutSessionContext()
    {
        await using var test = await PostgresServerSessionTestSupport.CreateAsync(fixture);
        var readerPid = await PostgresServerSessionTestSupport.ResetAsync(test.ReaderDatabase);
        var writerPid = await PostgresServerSessionTestSupport.ResetAsync(test.WriterDatabase);
        var first = await test.CreateAsync();
        var second = await test.CreateAsync(await test.IntentAsync(test.Key(test.OtherOrganization)));
        Assert.Null(await test.Reader.ObserveAsync(new(test.OtherOrganization, first.Key.HandleDigest)));
        Assert.Null(await test.Reader.ObserveAsync(new(test.Organization, second.Key.HandleDigest)));
        Assert.Null(await test.Reader.ObserveAsync(test.Key()));
        PostgresServerSessionTestSupport.WithoutFacts(await test.Writer.RenewAsync(test.Key(), 1), ServerSessionWriteOutcome.Denied);
        await test.CurrentAsync(first);
        await test.CurrentAsync(second);
        await test.CurrentAsync(first);
        Assert.Equal(readerPid, await PostgresServerSessionTestSupport.ResetAsync(test.ReaderDatabase));
        Assert.Equal(writerPid, await PostgresServerSessionTestSupport.ResetAsync(test.WriterDatabase));
        PostgresServerSessionTestSupport.WithoutFacts(await test.Writer.RevokeAsync(new(test.OtherOrganization, first.Key.HandleDigest)),
            ServerSessionWriteOutcome.Unchanged);
        await test.CurrentAsync(first);
    }

    [PostgresFact]
    public async Task IdenticalAndForeignOrganizationDigestCollisionsNeverUpsertReplayOrExposeAnotherRow()
    {
        await using var test = await PostgresServerSessionTestSupport.CreateAsync(fixture);
        var intent = await test.IntentAsync();
        var facts = await test.CreateAsync(intent);
        var before = await test.RowsAsync();
        foreach (var candidate in new[]
        {
            intent,
            await test.IntentAsync(intent.Key, epoch: Guid.NewGuid(), ticket: [9, 9, 9]),
            await test.IntentAsync(new ServerSessionKey(test.OtherOrganization, intent.Key.HandleDigest), ticket: [8, 8])
        }) PostgresServerSessionTestSupport.WithoutFacts(await test.Writer.CreateOnlyAsync(candidate), ServerSessionWriteOutcome.Conflict);
        Assert.Null(await test.Reader.ObserveAsync(new(test.OtherOrganization, intent.Key.HandleDigest)));
        PostgresServerSessionTestSupport.SamePrivate(before, await test.RowsAsync());
        await test.CurrentAsync(facts);
        Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.server_sessions"));
    }

    [PostgresFact]
    public async Task ReturnedOwnerMismatchIsIntegrityFailureBeforeMissingOrganizationExpiredDeadlineOrCollision()
    {
        await using var test = await PostgresServerSessionTestSupport.CreateAsync(fixture);
        var existing = await test.CreateAsync();
        var before = await test.RowsAsync();
        var other = new PrincipalId(Guid.NewGuid());
        foreach (var key in new[] { test.Key(), existing.Key, new ServerSessionKey(Guid.NewGuid(), existing.Key.HandleDigest) })
        {
            var intent = await test.IntentAsync(key, await test.DeadlineAsync(-1), other);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => test.Writer.CreateOnlyAsync(intent));
            Assert.True(string.Equals(error.Message, "The database contains inconsistent server session facts.", StringComparison.Ordinal),
                "A returned owner mismatch did not remain a fixed integrity failure.");
            Assert.Null(error.InnerException);
        }
        PostgresServerSessionTestSupport.SamePrivate(before, await test.RowsAsync());
    }

    [PostgresFact]
    public async Task OrdinaryMissingInactiveDisabledAndVersionMismatchedCreationDeniesWithoutSeedingAuthority()
    {
        await using var test = await PostgresServerSessionTestSupport.CreateAsync(fixture);
        var before = await test.AuthorityAsync();
        foreach (var candidate in new[]
        {
            await test.IntentAsync(new ServerSessionKey(Guid.NewGuid(), test.Key().HandleDigest)),
            await test.IntentAsync(bindingId: Guid.NewGuid()),
            await test.IntentAsync(bindingVersion: test.Binding.Version + 1),
            await test.IntentAsync(deadline: await test.DeadlineAsync(-1))
        }) PostgresServerSessionTestSupport.WithoutFacts(await test.Writer.CreateOnlyAsync(candidate), ServerSessionWriteOutcome.Denied);
        PostgresServerSessionTestSupport.SamePrivate(before, await test.AuthorityAsync());
        await test.Case.ExecuteAsync("UPDATE weft.principals SET active=false WHERE id=$1", test.Principal.Value);
        PostgresServerSessionTestSupport.WithoutFacts(await test.Writer.CreateOnlyAsync(await test.IntentAsync()), ServerSessionWriteOutcome.Denied);
        await test.Case.ExecuteAsync("UPDATE weft.principals SET active=true WHERE id=$1", test.Principal.Value);
        Assert.Equal(ExternalIdentityBindingOwnerOutcome.Disabled,
            (await test.IdentityOwner.DisableAsync(test.Binding.Key, test.Principal, test.Binding.Version)).Outcome);
        PostgresServerSessionTestSupport.WithoutFacts(await test.Writer.CreateOnlyAsync(await test.IntentAsync()), ServerSessionWriteOutcome.Denied);
        PostgresServerSessionTestSupport.SamePrivate("[]", await test.RowsAsync());
    }

    [PostgresFact]
    public async Task ReusedReaderAndRenewalObservePrincipalRestorationAndCurrentBindingDisableVersion()
    {
        await using var test = await PostgresServerSessionTestSupport.CreateAsync(fixture);
        var facts = await test.HistoricalAsync();
        var before = await test.RowsAsync();
        await test.CurrentAsync(facts);
        await test.Case.ExecuteAsync("UPDATE weft.principals SET active=false WHERE id=$1", test.Principal.Value);
        Assert.Null(await test.Reader.ObserveAsync(facts.Key));
        PostgresServerSessionTestSupport.WithoutFacts(await test.Writer.RenewAsync(facts.Key, facts.Version), ServerSessionWriteOutcome.Denied);
        await test.Case.ExecuteAsync("UPDATE weft.principals SET active=true WHERE id=$1", test.Principal.Value);
        await test.CurrentAsync(facts);
        PostgresServerSessionTestSupport.SamePrivate(before, await test.RowsAsync());
        var disabled = await test.IdentityOwner.DisableAsync(test.Binding.Key, test.Principal, test.Binding.Version);
        Assert.Equal(ExternalIdentityBindingOwnerOutcome.Disabled, disabled.Outcome);
        Assert.True(disabled.Binding is { Enabled: false, Version: 2 }, "The owned binding did not enter its disabled version.");
        Assert.Null(await test.Reader.ObserveAsync(facts.Key));
        PostgresServerSessionTestSupport.WithoutFacts(await test.Writer.RenewAsync(facts.Key, facts.Version), ServerSessionWriteOutcome.Denied);
        PostgresServerSessionTestSupport.SamePrivate(before, await test.RowsAsync());
    }

    [PostgresFact]
    public async Task ProviderClippingHasAnExactNoopButStaleCasStillConflictsBeforeNoop()
    {
        await using var test = await PostgresServerSessionTestSupport.CreateAsync(fixture);
        var deadline = await test.DeadlineAsync(5);
        var facts = await test.CreateAsync(await test.IntentAsync(deadline: deadline));
        Assert.True(facts.Lifetime.IdleExpiresAtUtc == deadline && facts.Lifetime.AbsoluteExpiresAtUtc == deadline,
            "The fixed initial lifetime was not clipped to its private verified deadline.");
        var before = await test.RowsAsync();
        PostgresServerSessionTestSupport.WithoutFacts(await test.Writer.RenewAsync(facts.Key, facts.Version + 1), ServerSessionWriteOutcome.Conflict);
        var unchanged = PostgresServerSessionTestSupport.Acknowledged(await test.Writer.RenewAsync(facts.Key, facts.Version), ServerSessionWriteOutcome.Unchanged);
        PostgresServerSessionTestSupport.SameFacts(facts, unchanged);
        PostgresServerSessionTestSupport.SamePrivate(before, await test.RowsAsync());
    }

    [PostgresFact]
    public async Task GenuineHistoricalRenewalAdvancesOnceAndPreservesAllImmutablePrivateMetadata()
    {
        await using var test = await PostgresServerSessionTestSupport.CreateAsync(fixture);
        var original = await test.HistoricalAsync();
        var authority = await test.AuthorityAsync();
        var renewed = PostgresServerSessionTestSupport.Acknowledged(await test.Writer.RenewAsync(original.Key, original.Version), ServerSessionWriteOutcome.Renewed);
        PostgresServerSessionTestSupport.SameImmutable(original, renewed);
        Assert.True(renewed.Version == 2 && renewed.Lifetime.IdleExpiresAtUtc > original.Lifetime.IdleExpiresAtUtc &&
            renewed.Lifetime.IdleExpiresAtUtc <= original.Lifetime.AbsoluteExpiresAtUtc && renewed.RevokedAtUtc is null,
            "Renewal did not make one bounded forward state transition.");
        PostgresServerSessionTestSupport.WithoutFacts(await test.Writer.RenewAsync(original.Key, original.Version), ServerSessionWriteOutcome.Conflict);
        await test.CurrentAsync(renewed);
        PostgresServerSessionTestSupport.SamePrivate(authority, await test.AuthorityAsync());
    }

    [PostgresFact]
    public async Task ExpiredAndInactiveSessionsCannotReviveButExplicitRevocationRemainsOneWayAndIdempotent()
    {
        foreach (var (ageMinutes, inactive) in new[] { (30, false), (90, false), (5, true) })
        {
            await using var test = await PostgresServerSessionTestSupport.CreateAsync(fixture);
            var facts = await test.HistoricalAsync(ageMinutes: ageMinutes);
            if (inactive) await test.Case.ExecuteAsync("UPDATE weft.principals SET active=false WHERE id=$1", test.Principal.Value);
            Assert.Null(await test.Reader.ObserveAsync(facts.Key));
            PostgresServerSessionTestSupport.WithoutFacts(await test.Writer.RenewAsync(facts.Key, facts.Version), ServerSessionWriteOutcome.Denied);
            var revoked = PostgresServerSessionTestSupport.Acknowledged(await test.Writer.RevokeAsync(facts.Key), ServerSessionWriteOutcome.Revoked);
            PostgresServerSessionTestSupport.SameImmutable(facts, revoked);
            Assert.True(revoked.Version == 2 && revoked.RevokedAtUtc is { } revokedAt && revokedAt >= facts.Lifetime.IssuedAtUtc);
            var before = await test.RowsAsync();
            PostgresServerSessionTestSupport.SameFacts(revoked, PostgresServerSessionTestSupport.Acknowledged(
                await test.Writer.RevokeAsync(facts.Key), ServerSessionWriteOutcome.Unchanged));
            if (inactive) await test.Case.ExecuteAsync("UPDATE weft.principals SET active=true WHERE id=$1", test.Principal.Value);
            Assert.Null(await test.Reader.ObserveAsync(facts.Key));
            PostgresServerSessionTestSupport.WithoutFacts(await test.Writer.RenewAsync(facts.Key, revoked.Version), ServerSessionWriteOutcome.Denied);
            PostgresServerSessionTestSupport.WithoutFacts(await test.Writer.CreateOnlyAsync(await test.IntentAsync(facts.Key)), ServerSessionWriteOutcome.Conflict);
            PostgresServerSessionTestSupport.SamePrivate(before, await test.RowsAsync());
        }
    }

    [PostgresFact]
    public async Task IsolatedOwnerCorruptionAtMaximumVersionPreservesNoopAndRefusesRealRenewalOrRevoke()
    {
        await using var test = await PostgresServerSessionTestSupport.CreateAsync(fixture);
        var clipped = await test.HistoricalAsync(absoluteMinutes: 7);
        var extendable = await test.HistoricalAsync();
        await using (var connection = PostgresServerSessionTestSupport.Unpooled(test.Case.MigrationConnectionString))
        {
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using (var disable = new NpgsqlCommand("ALTER TABLE weft.server_sessions DISABLE TRIGGER server_sessions_transition_guard", connection, transaction))
                await disable.ExecuteNonQueryAsync();
            foreach (var key in new[] { clipped.Key, extendable.Key })
            {
                await using var update = new NpgsqlCommand("UPDATE weft.server_sessions SET version=$1 WHERE organization_id=$2 AND handle_digest=$3", connection, transaction);
                update.Parameters.AddWithValue(long.MaxValue);
                update.Parameters.AddWithValue(key.OrganizationId);
                update.Parameters.AddWithValue(PostgresServerSessionTestSupport.Bytes(key));
                Assert.Equal(1, await update.ExecuteNonQueryAsync());
            }
            await using (var enable = new NpgsqlCommand("ALTER TABLE weft.server_sessions ENABLE TRIGGER server_sessions_transition_guard", connection, transaction))
                await enable.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }
        Assert.True(await test.Case.ScalarAsync<bool>("""
            SELECT count(*)=1 AND bool_and(tgenabled='O' AND tgtype=31 AND NOT tgisinternal
                AND tgfoid='weft.guard_server_sessions_transition()'::regprocedure
                AND tgconstraint=0 AND tgparentid=0 AND tgconstrrelid=0 AND NOT tgdeferrable AND NOT tginitdeferred
                AND tgnargs=0 AND octet_length(tgargs)=0 AND tgattr::text='' AND tgqual IS NULL
                AND tgoldtable IS NULL AND tgnewtable IS NULL)
            FROM pg_catalog.pg_trigger WHERE tgrelid='weft.server_sessions'::regclass AND tgname='server_sessions_transition_guard'
            """),
            "The isolated owner fixture did not restore the exact transition guard.");
        var maximum = await test.StoredAsync(clipped.Key);
        var otherMaximum = await test.StoredAsync(extendable.Key);
        PostgresServerSessionTestSupport.SameImmutable(clipped, maximum);
        PostgresServerSessionTestSupport.SameImmutable(extendable, otherMaximum);
        Assert.True(maximum.Version == long.MaxValue && otherMaximum.Version == long.MaxValue);
        Assert.True(maximum.Lifetime.IdleExpiresAtUtc == clipped.Lifetime.IdleExpiresAtUtc && maximum.RevokedAtUtc is null &&
            otherMaximum.Lifetime.IdleExpiresAtUtc == extendable.Lifetime.IdleExpiresAtUtc && otherMaximum.RevokedAtUtc is null,
            "The isolated owner fixture changed private facts beyond its maximum version.");
        var before = await test.RowsAsync();
        PostgresServerSessionTestSupport.SameFacts(maximum, PostgresServerSessionTestSupport.Acknowledged(
            await test.Writer.RenewAsync(clipped.Key, long.MaxValue), ServerSessionWriteOutcome.Unchanged));
        PostgresServerSessionTestSupport.WithoutFacts(await test.Writer.RenewAsync(clipped.Key, long.MaxValue - 1), ServerSessionWriteOutcome.Conflict);
        PostgresServerSessionTestSupport.WithoutFacts(await test.Writer.RenewAsync(extendable.Key, long.MaxValue), ServerSessionWriteOutcome.VersionExhausted);
        PostgresServerSessionTestSupport.WithoutFacts(await test.Writer.RevokeAsync(clipped.Key), ServerSessionWriteOutcome.VersionExhausted);
        PostgresServerSessionTestSupport.SamePrivate(before, await test.RowsAsync());
    }

    [PostgresFact]
    public async Task NewPoolsAndRestartOfTheSameClusterRetainLiveExpiredAndRevokedOpaqueMetadata()
    {
        await using var test = await PostgresServerSessionTestSupport.CreateAsync(fixture);
        var live = await test.HistoricalAsync();
        var expired = await test.HistoricalAsync(ageMinutes: 30);
        var revoked = await test.CreateAsync();
        revoked = PostgresServerSessionTestSupport.Acknowledged(await test.Writer.RevokeAsync(revoked.Key), ServerSessionWriteOutcome.Revoked);
        var before = await test.RowsAsync();
        var readerConnection = test.Case.SessionReaderConnectionString;
        var writerConnection = test.Case.SessionWriterConnectionString;
        var ownerConnection = test.Case.MigrationConnectionString;
        await test.ReaderDatabase.DisposeAsync();
        await test.WriterDatabase.DisposeAsync();
        await test.IdentityOwner.DisposeAsync();
        await test.Case.CloseOwnerPoolAsync();
        await fixture.RestartAsync();
        await using var readerDatabase = new PostgresServerSessionDatabase(readerConnection, ServerSessionDatabaseProfile.Reader);
        await using var writerDatabase = new PostgresServerSessionDatabase(writerConnection, ServerSessionDatabaseProfile.Writer);
        var reader = new PostgresServerSessionReader(readerDatabase);
        var writer = new PostgresServerSessionWriter(writerDatabase);
        PostgresServerSessionTestSupport.SameFacts(live, Assert.IsType<CurrentServerSession>(await reader.ObserveAsync(live.Key)).Facts);
        Assert.Null(await reader.ObserveAsync(expired.Key));
        Assert.Null(await reader.ObserveAsync(revoked.Key));
        PostgresServerSessionTestSupport.SameFacts(revoked, PostgresServerSessionTestSupport.Acknowledged(
            await writer.RevokeAsync(revoked.Key), ServerSessionWriteOutcome.Unchanged));
        await using var connection = PostgresServerSessionTestSupport.Unpooled(ownerConnection);
        await connection.OpenAsync();
        await using var rows = new NpgsqlCommand("SELECT COALESCE(jsonb_agg(to_jsonb(s) ORDER BY organization_id,handle_digest),'[]'::jsonb)::text FROM weft.server_sessions s", connection);
        PostgresServerSessionTestSupport.SamePrivate(before, Assert.IsType<string>(await rows.ExecuteScalarAsync()));
    }
}
