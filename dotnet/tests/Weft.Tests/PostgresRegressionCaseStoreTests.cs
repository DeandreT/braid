using Npgsql;
using Weft.Application;
using Weft.Application.Regression;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

/// <summary>Isolated new-migration checks; full ordered P7 upgrade qualification remains separate.</summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresRegressionCaseStoreTests(PostgresFixture fixture)
{

    [PostgresFact]
    public async Task The_schema_only_fingerprint_matches_the_qualified_layout()
    {
        await using var test = await PrepareAsync();
        await using var connection = await test.Case.Owner.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var settings = new NpgsqlCommand("SET LOCAL search_path=pg_catalog; SET LOCAL TimeZone='UTC'; SET LOCAL DateStyle='ISO, MDY'", connection, transaction))
            await settings.ExecuteNonQueryAsync();
        var actual = await RegressionCaseSchemaContract.CaptureAsync(connection, transaction, CancellationToken.None);
        Assert.Equal(RegressionCaseSchemaContract.Fingerprint, actual);
    }

    public static TheoryData<string> SchemaChanges =>
    [
        "ALTER TABLE weft.regression_cases DISABLE ROW LEVEL SECURITY",
        "ALTER TABLE weft.regression_case_versions DISABLE TRIGGER regression_version_guard",
        "ALTER TABLE weft.regression_case_versions ALTER COLUMN envelope DROP NOT NULL",
        "ALTER TABLE weft.regression_case_versions SET (fillfactor=80)",
        "CREATE INDEX unexpected_case_index ON weft.regression_cases(case_id)",
        "DROP POLICY organization_scope ON weft.regression_cases",
        "CREATE POLICY unexpected_case_policy ON weft.regression_cases USING(true) WITH CHECK(true)",
        "ALTER TABLE weft.regression_cases ALTER CONSTRAINT regression_case_head NOT DEFERRABLE",
        "DROP TRIGGER regression_case_guard ON weft.regression_cases",
        "GRANT SELECT ON weft.regression_case_versions TO weft_runtime",
        "GRANT SELECT (envelope) ON weft.regression_case_versions TO weft_runtime",
        """
        DO $change$ DECLARE target text; BEGIN
            SELECT conname INTO STRICT target FROM pg_constraint
            WHERE conrelid='weft.regression_case_versions'::regclass AND contype='c'
                AND pg_get_constraintdef(oid) LIKE '%octet_length(envelope)%';
            EXECUTE format('ALTER TABLE weft.regression_case_versions DROP CONSTRAINT %I',target);
        END $change$;
        """
    ];

    [RegressionPostgresTheory]
    [MemberData(nameof(SchemaChanges))]
    public async Task Schema_drift_is_refused_before_reading_or_publishing_private_cases(string ddl)
    {
        await using var test = await PrepareAsync();
        Assert.Equal(RegressionCaseSaveOutcome.Created, (await test.Writer.SaveAsync(test.Actor, test.Version(1))).Outcome);
        await test.Case.ExecuteAsync(ddl);
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Reader.ReadAsync(test.Actor, test.Mapping, test.CaseId, 1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Reader.ListAsync(test.Actor, test.Mapping, 10));
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Writer.SaveAsync(test.Actor, test.Version(2)));
    }

    [PostgresFact]
    public async Task Inactive_principal_and_organization_admission_do_not_preserve_sample_access()
    {
        await using var test = await PrepareAsync();
        Assert.Equal(RegressionCaseSaveOutcome.Created, (await test.Writer.SaveAsync(test.Actor, test.Version(1))).Outcome);
        foreach (var table in new[] { "principals", "organization_memberships" })
        {
            var key = table == "principals" ? "id" : "principal_id";
            await test.Case.ExecuteAsync($"UPDATE weft.{table} SET active=false WHERE {key}=$1", test.Seed.Alice);
            Assert.Null(await test.Reader.ReadAsync(test.Actor, test.Mapping, test.CaseId, 1));
            Assert.Empty((await test.Reader.ListAsync(test.Actor, test.Mapping, 10)).Cases);
            Assert.Equal(RegressionCaseSaveOutcome.Unavailable, (await test.Writer.SaveAsync(test.Actor, test.Version(2))).Outcome);
            await test.Case.ExecuteAsync($"UPDATE weft.{table} SET active=true WHERE {key}=$1", test.Seed.Alice);
        }
    }

    [PostgresFact]
    public async Task Another_organization_and_saved_samples_do_not_grant_production_payload_access()
    {
        await using var test = await PrepareAsync();
        Assert.Equal(RegressionCaseSaveOutcome.Created, (await test.Writer.SaveAsync(test.Actor, test.Version(1))).Outcome);
        // Alice is also an admitted runner in organization B, but that is not the case's scope.
        var foreign = new MappingScope(test.Seed.OrganizationB, test.Seed.ProjectB, test.Mapping.MappingId);
        Assert.Null(await test.Reader.ReadAsync(test.Actor, foreign, test.CaseId, 1));
        Assert.Empty((await test.Reader.ListAsync(test.Actor, foreign, 10)).Cases);
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.project_payload_read_grants"));
    }

    [PostgresFact]
    public async Task Maximum_input_and_expected_bytes_round_trip_through_the_restricted_store()
    {
        await using var test = await PrepareAsync();
        var basis = test.Version(1);
        var primary = new byte[PreviewPolicy.MaximumInputBytes];
        var expected = new byte[PreviewPolicy.MaximumOutputBytes];
        primary[^1] = 123;
        expected[^1] = 255;
        var replay = new RegressionReplay(basis.Replay.Target, basis.Replay.CurrentDateTime, basis.Replay.Limits,
            new(null, primary), [], []);
        var large = new RegressionCaseVersion(basis.CaseId, 1, basis.Owner, basis.Name, basis.Revision,
            basis.NativeDigest, basis.Runtime, replay, basis.ComparisonPolicy,
            RegressionOutputSet.Capture([RegressionOutput.Capture(RegressionOutputKey.Primary, expected)]));
        Assert.Equal(RegressionCaseSaveOutcome.Created, (await test.Writer.SaveAsync(test.Actor, large)).Outcome);
        var restored = await test.Reader.ReadAsync(test.Actor, test.Mapping, test.CaseId, 1);
        Assert.NotNull(restored);
        Assert.Equal(primary, restored.Replay.Primary.CopyBytes());
        Assert.Equal(expected, restored.Expected.Outputs[0].CopyBytes());
    }

    [PostgresFact]
    public async Task Corrupt_stored_frames_fail_integrity_without_blocking_an_unrelated_case()
    {
        await using var test = await PrepareAsync();
        Assert.Equal(RegressionCaseSaveOutcome.Created, (await test.Writer.SaveAsync(test.Actor, test.Version(1))).Outcome);
        var healthy = test.Version(1, caseId: Guid.NewGuid());
        Assert.Equal(RegressionCaseSaveOutcome.Created, (await test.Writer.SaveAsync(test.Actor, healthy)).Outcome);
        // Explicit owner-only damage simulation. Restore the structure before testing the content reader.
        await test.Case.ExecuteAsync("ALTER TABLE weft.regression_case_versions DISABLE TRIGGER regression_version_guard");
        await test.Case.ExecuteAsync("""
            UPDATE weft.regression_case_versions SET
                envelope=set_byte(envelope,octet_length(envelope)-1,get_byte(envelope,octet_length(envelope)-1)#1),
                content_digest=encode(sha256(set_byte(envelope,octet_length(envelope)-1,get_byte(envelope,octet_length(envelope)-1)#1)),'hex')
            WHERE case_id=$1
            """, test.CaseId);
        await test.Case.ExecuteAsync("ALTER TABLE weft.regression_case_versions ENABLE TRIGGER regression_version_guard");
        var failure = await Assert.ThrowsAsync<RegressionCaseException>(() => test.Reader.ReadAsync(test.Actor, test.Mapping, test.CaseId, 1));
        Assert.Equal(RegressionCaseFailure.StorageIntegrity, failure.Failure);
        Assert.NotNull(await test.Reader.ReadAsync(test.Actor, test.Mapping, healthy.CaseId, 1));
        Assert.Equal(RegressionCaseSaveOutcome.Replayed, (await test.Writer.SaveAsync(test.Actor, healthy)).Outcome);
    }

    [PostgresFact]
    public async Task Revocation_that_wins_an_authority_lock_prevents_the_waiting_publication()
    {
        await using var test = await PrepareAsync();
        Assert.Equal(RegressionCaseSaveOutcome.Created, (await test.Writer.SaveAsync(test.Actor, test.Version(1))).Outcome);
        await using var blocker = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(test.Case.MigrationConnectionString) { Pooling = false }.ConnectionString);
        await blocker.OpenAsync();
        await using var change = await blocker.BeginTransactionAsync();
        await using (var revoke = new NpgsqlCommand("UPDATE weft.project_memberships SET active=false,version=version+1 WHERE principal_id=$1 AND project_id=$2", blocker, change))
        {
            revoke.Parameters.AddWithValue(test.Seed.Alice);
            revoke.Parameters.AddWithValue(test.Seed.ProjectA);
            await revoke.ExecuteNonQueryAsync();
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var pending = test.Writer.SaveAsync(test.Actor, test.Version(2), deadline.Token);
        var committed = false;
        try
        {
            while (!await test.Case.ScalarAsync<bool>("""
                SELECT EXISTS(SELECT 1 FROM pg_locks l JOIN pg_stat_activity a ON a.pid=l.pid
                    WHERE a.datname=current_database() AND a.usename='weft_case_writer' AND NOT l.granted)
                """))
            {
                if (pending.IsCompleted) throw new InvalidOperationException("The writer did not wait on the owned authority lock.");
                await Task.Delay(25, deadline.Token);
            }
            await change.CommitAsync(deadline.Token);
            committed = true;
            Assert.Equal(RegressionCaseSaveOutcome.Unavailable, (await pending).Outcome);
            Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.regression_case_versions"));
        }
        finally
        {
            if (!committed) await change.RollbackAsync();
            deadline.Cancel();
            try { await pending; } catch (OperationCanceledException) { }
        }
    }

    [PostgresFact]
    public async Task Lost_commit_acknowledgement_recovers_the_same_saved_version_without_duplication()
    {
        await using var test = await PrepareAsync();
        var version = test.Version(1);
        await using var relay = new ServerSessionCommitAckLossRelay(test.WriterConnection);
        await using var routed = new PostgresRegressionCaseStore(relay.RoutedConnectionString, RegressionCaseDatabaseProfile.Writer);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var pending = routed.SaveAsync(test.Actor, version, deadline.Token);
        try
        {
            await Task.WhenAny(pending, relay.CommitAcknowledgementWithheld).WaitAsync(TimeSpan.FromSeconds(10), deadline.Token);
            Assert.True(relay.CommitAcknowledgementWithheld.IsCompletedSuccessfully && !pending.IsCompleted);
            Assert.True(relay.FrontendCommitSeen && relay.BackendCommitCompleteSeen && relay.BackendIdleReadySeen);
            Assert.NotNull(await test.Reader.ReadAsync(test.Actor, test.Mapping, version.CaseId, 1, deadline.Token));
            relay.DropAcknowledgement();
            await Assert.ThrowsAnyAsync<NpgsqlException>(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5), deadline.Token));
            Assert.False(deadline.IsCancellationRequested);
            Assert.Equal(RegressionCaseSaveOutcome.Replayed, (await test.Writer.SaveAsync(test.Actor, version, deadline.Token)).Outcome);
            Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.regression_case_versions"));
        }
        finally
        {
            relay.DropAcknowledgement();
            try { await pending; } catch (NpgsqlException) { }
        }
    }

    [PostgresFact]
    public async Task Database_restart_and_new_pools_preserve_the_exact_saved_case()
    {
        await using var test = await PrepareAsync();
        var version = test.Version(1);
        var saved = await test.Writer.SaveAsync(test.Actor, version);
        await test.Reader.DisposeAsync();
        await test.Writer.DisposeAsync();
        await test.Case.CloseOwnerPoolAsync();
        await fixture.RestartAsync();
        await using var reader = new PostgresRegressionCaseStore(test.ReaderConnection, RegressionCaseDatabaseProfile.Reader);
        var restored = await reader.ReadAsync(test.Actor, test.Mapping, version.CaseId, 1);
        Assert.NotNull(restored);
        Assert.Equal(saved.Reference, new RegressionCaseEnvelopeCodec().Encode(restored).Reference);
    }

    [PostgresFact]
    public async Task Exact_versions_and_original_retries_survive_fresh_pools()
    {
        await using var test = await PrepareAsync();
        var version1 = test.Version(1);
        var created = await test.Writer.SaveAsync(test.Actor, version1);
        Assert.Equal(RegressionCaseSaveOutcome.Created, created.Outcome);
        Assert.Equal(new RegressionCaseEnvelopeCodec().Encode(version1).Reference, created.Reference);
        var version2 = test.Version(2);
        Assert.Equal(RegressionCaseSaveOutcome.Appended, (await test.Writer.SaveAsync(test.Actor, version2)).Outcome);
        Assert.Equal(RegressionCaseSaveOutcome.Replayed, (await test.Writer.SaveAsync(test.Actor, version1)).Outcome);
        Assert.Equal(RegressionCaseSaveOutcome.Conflict, (await test.Writer.SaveAsync(test.Actor, test.Version(1, "changed"))).Outcome);
        await using var reopened = new PostgresRegressionCaseStore(test.ReaderConnection, RegressionCaseDatabaseProfile.Reader);
        var old = await reopened.ReadAsync(test.Actor, test.Mapping, version1.CaseId, 1);
        Assert.NotNull(old);
        Assert.Equal(new RegressionCaseEnvelopeCodec().Encode(version1).Reference,
            new RegressionCaseEnvelopeCodec().Encode(old).Reference);
        var page = await reopened.ListAsync(test.Actor, test.Mapping, 10);
        Assert.Equal(2, Assert.Single(page.Cases).Version);
        Assert.Null(page.NextCaseId);
        Assert.Equal(2L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.regression_case_versions"));
    }

    [PostgresFact]
    public async Task Fresh_roles_separate_metadata_sample_read_and_case_publication()
    {
        await using var test = await PrepareAsync();
        var version = test.Version(1);
        Assert.Equal(RegressionCaseSaveOutcome.Created, (await test.Writer.SaveAsync(test.Actor, version)).Outcome);
        await test.RoleAsync("viewer");
        Assert.Single((await test.Reader.ListAsync(test.Actor, test.Mapping, 10)).Cases);
        Assert.Null(await test.Reader.ReadAsync(test.Actor, test.Mapping, version.CaseId, 1));
        Assert.Equal(RegressionCaseSaveOutcome.Unavailable, (await test.Writer.SaveAsync(test.Actor, test.Version(2))).Outcome);
        await test.RoleAsync("runner");
        Assert.NotNull(await test.Reader.ReadAsync(test.Actor, test.Mapping, version.CaseId, 1));
        Assert.Equal(RegressionCaseSaveOutcome.Unavailable, (await test.Writer.SaveAsync(test.Actor, test.Version(2))).Outcome);
        await test.RoleAsync("publisher");
        Assert.Equal(RegressionCaseSaveOutcome.Appended, (await test.Writer.SaveAsync(test.Actor, test.Version(2))).Outcome);
        await test.Case.ExecuteAsync("UPDATE weft.project_memberships SET active=false,version=version+1 WHERE principal_id=$1 AND project_id=$2",
            test.Seed.Alice, test.Seed.ProjectA);
        Assert.Null(await test.Reader.ReadAsync(test.Actor, test.Mapping, version.CaseId, 1));
        Assert.Empty((await test.Reader.ListAsync(test.Actor, test.Mapping, 10)).Cases);
        Assert.Equal(RegressionCaseSaveOutcome.Unavailable, (await test.Writer.SaveAsync(test.Actor, test.Version(3))).Outcome);
    }

    [PostgresFact]
    public async Task Foreign_project_and_creator_substitution_cannot_publish_or_read_samples()
    {
        await using var test = await PrepareAsync();
        var version = test.Version(1);
        Assert.Equal(RegressionCaseSaveOutcome.Created, (await test.Writer.SaveAsync(test.Actor, version)).Outcome);
        var foreign = new ActorContext(new(test.Seed.OtherProjectAdmin));
        Assert.Null(await test.Reader.ReadAsync(foreign, test.Mapping, version.CaseId, 1));
        Assert.Empty((await test.Reader.ListAsync(foreign, test.Mapping, 10)).Cases);
        Assert.Equal(RegressionCaseSaveOutcome.Unavailable, (await test.Writer.SaveAsync(foreign, test.Version(2))).Outcome);
        var otherMapping = new MappingScope(test.Mapping.OrganizationId, test.Seed.HiddenProjectA, test.Mapping.MappingId);
        Assert.Null(await test.Reader.ReadAsync(test.Actor, otherMapping, version.CaseId, 1));
        Assert.Equal(RegressionCaseSaveOutcome.Conflict, (await test.Writer.SaveAsync(test.Actor,
            test.Version(2, owner: new(test.Seed.Bob)))).Outcome);
        Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.regression_case_versions"));
    }

    [PostgresFact]
    public async Task Competing_appends_keep_one_winner_and_other_cases_continue()
    {
        await using var test = await PrepareAsync();
        Assert.Equal(RegressionCaseSaveOutcome.Created, (await test.Writer.SaveAsync(test.Actor, test.Version(1))).Outcome);
        var before = test.Version(1, "before", caseId: Guid.NewGuid());
        Assert.Equal(RegressionCaseSaveOutcome.Created, (await test.Writer.SaveAsync(test.Actor, before)).Outcome);
        await using var secondWriter = new PostgresRegressionCaseStore(test.WriterConnection, RegressionCaseDatabaseProfile.Writer);
        var results = await Task.WhenAll(test.Writer.SaveAsync(test.Actor, test.Version(2, "left")),
            secondWriter.SaveAsync(test.Actor, test.Version(2, "right")));
        Assert.Single(results, result => result.Outcome == RegressionCaseSaveOutcome.Appended);
        Assert.Single(results, result => result.Outcome == RegressionCaseSaveOutcome.Conflict);
        var after = test.Version(1, "after", caseId: Guid.NewGuid());
        Assert.Equal(RegressionCaseSaveOutcome.Created, (await test.Writer.SaveAsync(test.Actor, after)).Outcome);
        Assert.Equal(3, (await test.Reader.ListAsync(test.Actor, test.Mapping, 10)).Cases.Count);
        Assert.Equal(4L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.regression_case_versions"));
        Assert.Equal(RegressionCaseSaveOutcome.Replayed, (await test.Writer.SaveAsync(test.Actor, before)).Outcome);
    }

    [PostgresFact]
    public async Task Narrow_roles_cannot_bypass_the_store_or_mutate_retained_versions()
    {
        await using var test = await PrepareAsync();
        Assert.Equal(RegressionCaseSaveOutcome.Created, (await test.Writer.SaveAsync(test.Actor, test.Version(1))).Outcome);
        foreach (var connectionString in new[] { test.ReaderConnection, test.WriterConnection })
        {
            await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
            await connection.OpenAsync();
            await using var query = new NpgsqlCommand("SELECT envelope FROM weft.regression_case_versions", connection);
            var denied = await Assert.ThrowsAsync<PostgresException>(() => query.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        }
        var immutable = await Assert.ThrowsAsync<PostgresException>(() => test.Case.ExecuteAsync("DELETE FROM weft.regression_case_versions"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, immutable.SqlState);
        await test.Case.ExecuteAsync("GRANT SELECT ON weft.regression_case_versions TO weft_case_reader");
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Reader.ReadAsync(test.Actor, test.Mapping, test.CaseId, 1));
    }

    [PostgresFact]
    public async Task Pagination_has_no_payload_and_uses_stable_uuid_seeks()
    {
        await using var test = await PrepareAsync();
        var first = Guid.Parse("10000000-0000-4000-8000-000000000001");
        var second = Guid.Parse("20000000-0000-4000-8000-000000000001");
        foreach (var id in new[] { second, first })
            Assert.Equal(RegressionCaseSaveOutcome.Created, (await test.Writer.SaveAsync(test.Actor, test.Version(1, caseId: id))).Outcome);
        var page = await test.Reader.ListAsync(test.Actor, test.Mapping, 1);
        Assert.Equal(first, Assert.Single(page.Cases).CaseId);
        Assert.Equal(first, page.NextCaseId);
        var next = await test.Reader.ListAsync(test.Actor, test.Mapping, 1, page.NextCaseId);
        Assert.Equal(second, Assert.Single(next.Cases).CaseId);
        Assert.Null(next.NextCaseId);
        Assert.DoesNotContain("private-input", page.ToString());
    }

    private Task<RegressionCasePostgresTestSupport> PrepareAsync() => RegressionCasePostgresTestSupport.CreateAsync(fixture);
}

public sealed class RegressionPostgresTheoryAttribute : TheoryAttribute
{
    public RegressionPostgresTheoryAttribute()
    {
        if (!PostgresFixture.IsConfigured) Skip = "An explicit PostgreSQL test fixture is required.";
    }
}
