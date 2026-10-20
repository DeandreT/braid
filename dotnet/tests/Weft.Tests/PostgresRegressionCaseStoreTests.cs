using System.Reflection;
using System.Text;
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
    private static readonly string ReaderPassword = Guid.NewGuid().ToString("N");
    private static readonly string WriterPassword = Guid.NewGuid().ToString("N");

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
            await using var connection = new NpgsqlConnection(connectionString);
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

    private async Task<CaseContext> PrepareAsync()
    {
        var test = await fixture.CreateCaseAsync(migrate: false);
        try
        {
            var scripts = typeof(MigrationRunner).Assembly.GetManifestResourceNames()
                .Where(name => name.StartsWith("Weft.Persistence.Postgres.Migrations.", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal).Take(11).Select((name, index) => new MigrationScript(index + 1, Resource(name))).ToArray();
            // Establish a real qualified old revision first. No P7 migration or fake replacement is applied here.
            await using (var old = new MigrationRunner(test.MigrationConnectionString, scripts.Take(2).ToArray())) await old.MigrateAsync();
            var seed = await PostgresSeedData.CreateAsync(test);
            await test.ExecuteAsync("UPDATE weft.project_memberships SET role='publisher',version=version+1 WHERE principal_id=$1", seed.Alice);
            var actor = new ActorContext(new(seed.Alice));
            var receipt = await HistoricalIngestionSeed.CommitCreateAsync(test, actor, new(seed.OrganizationA, seed.ProjectA));
            await using (var baseline = new MigrationRunner(test.MigrationConnectionString, scripts)) await baseline.MigrateAsync();
            await test.BootstrapAsync($"""
                DO $roles$ BEGIN
                    IF NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname='weft_case_reader') THEN
                        CREATE ROLE weft_case_reader LOGIN PASSWORD '{ReaderPassword}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;
                        CREATE ROLE weft_case_writer LOGIN PASSWORD '{WriterPassword}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;
                    END IF;
                END $roles$;
                GRANT CONNECT ON DATABASE "{test.Name}" TO weft_case_reader,weft_case_writer;
                """);
            // This is isolated migration-contract evidence, not a complete ordered 0011 -> 0014 upgrade.
            await test.ExecuteAsync(Resource("Weft.Persistence.Postgres.Regression.0014_regression_cases.sql"));
            return new(test, seed, receipt);
        }
        catch { await test.DisposeAsync(); throw; }
    }

    private static string Resource(string name)
    {
        using var stream = typeof(MigrationRunner).Assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed class CaseContext : IAsyncDisposable
    {
        private readonly IngestionReceipt receipt;
        internal CaseContext(PostgresCase test, PostgresSeedData seed, IngestionReceipt receipt)
        {
            Case = test;
            Seed = seed;
            this.receipt = receipt;
            Actor = new(new(seed.Alice));
            Mapping = new(seed.OrganizationA, seed.ProjectA, receipt.Facts.FutureRevision.MappingId);
            ReaderConnection = Connection(test.RuntimeConnectionString, "weft_case_reader", ReaderPassword);
            WriterConnection = Connection(test.RuntimeConnectionString, "weft_case_writer", WriterPassword);
            Reader = new(ReaderConnection, RegressionCaseDatabaseProfile.Reader);
            Writer = new(WriterConnection, RegressionCaseDatabaseProfile.Writer);
        }
        internal PostgresCase Case { get; }
        internal PostgresSeedData Seed { get; }
        internal Guid CaseId { get; } = Guid.NewGuid();
        internal ActorContext Actor { get; }
        internal MappingScope Mapping { get; }
        internal string ReaderConnection { get; }
        internal string WriterConnection { get; }
        internal PostgresRegressionCaseStore Reader { get; }
        internal PostgresRegressionCaseStore Writer { get; }
        internal RegressionCaseVersion Version(long version, string name = "case", Guid? caseId = null, PrincipalId? owner = null) =>
            new(caseId ?? CaseId, version, owner ?? Actor.PrincipalId, name, receipt.Facts.FutureRevision, receipt.Facts.NativeObject.Digest,
                new("fixture", "engine", "build", "adapter", PreviewPolicy.Profile),
                new(PreviewTargetSelection.All, "2026-10-20T12:00:00Z", PreviewLimits.Default,
                    new(null, Encoding.UTF8.GetBytes("private-input")), [], []), RegressionComparisonService.ExactBytesPolicy,
                RegressionOutputSet.Capture([RegressionOutput.Capture(RegressionOutputKey.Primary, Encoding.UTF8.GetBytes($"expected-{version}"))]));
        internal Task RoleAsync(string role) => Case.ExecuteAsync("UPDATE weft.project_memberships SET role=$1,version=version+1 WHERE principal_id=$2 AND project_id=$3",
            role, Seed.Alice, Seed.ProjectA);
        private static string Connection(string baseline, string user, string password) =>
            new NpgsqlConnectionStringBuilder(baseline) { Username = user, Password = password }.ConnectionString;
        public async ValueTask DisposeAsync()
        {
            await Reader.DisposeAsync();
            await Writer.DisposeAsync();
            await Case.DisposeAsync();
        }
    }
}
