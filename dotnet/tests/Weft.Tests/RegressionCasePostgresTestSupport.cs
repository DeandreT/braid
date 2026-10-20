using System.Text;
using Npgsql;
using Weft.Application;
using Weft.Application.Regression;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

/// <summary>Shared isolated-schema fixture; never claims the pending P7 ordered upgrade.</summary>
internal sealed class RegressionCasePostgresTestSupport : IAsyncDisposable
{
    private static readonly string ReaderPassword = Guid.NewGuid().ToString("N");
    private static readonly string WriterPassword = Guid.NewGuid().ToString("N");
    private readonly IngestionReceipt receipt;

    private RegressionCasePostgresTestSupport(PostgresCase test, PostgresSeedData seed, IngestionReceipt receipt)
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
        Runtime = new(receipt.Facts.BackendProfile.BackendId, "engine", "build", "adapter", PreviewPolicy.Profile);
    }

    internal static async Task<RegressionCasePostgresTestSupport> CreateAsync(PostgresFixture fixture)
    {
        var test = await fixture.CreateCaseAsync(migrate: false);
        try
        {
            var scripts = typeof(MigrationRunner).Assembly.GetManifestResourceNames()
                .Where(name => name.StartsWith("Weft.Persistence.Postgres.Migrations.", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal).Take(11).Select((name, index) => new MigrationScript(index + 1, Resource(name))).ToArray();
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
            await test.ExecuteAsync(Resource("Weft.Persistence.Postgres.Regression.0014_regression_cases.sql"));
            return new(test, seed, receipt);
        }
        catch { await test.DisposeAsync(); throw; }
    }

    internal PostgresCase Case { get; }
    internal PostgresSeedData Seed { get; }
    internal Guid CaseId { get; } = Guid.NewGuid();
    internal ActorContext Actor { get; }
    internal MappingScope Mapping { get; }
    internal RegressionRuntimeIdentity Runtime { get; }
    internal string ReaderConnection { get; }
    internal string WriterConnection { get; }
    internal PostgresRegressionCaseStore Reader { get; }
    internal PostgresRegressionCaseStore Writer { get; }
    internal RegressionCaseVersion Version(long version, string name = "case", Guid? caseId = null, PrincipalId? owner = null) =>
        new(caseId ?? CaseId, version, owner ?? Actor.PrincipalId, name, receipt.Facts.FutureRevision, receipt.Facts.NativeObject.Digest,
            Runtime, new(PreviewTargetSelection.All, "2026-10-20T12:00:00Z", PreviewLimits.Default,
                new(null, Encoding.UTF8.GetBytes("private-input")), [], []), RegressionComparisonService.ExactBytesPolicy,
            RegressionOutputSet.Capture([RegressionOutput.Capture(RegressionOutputKey.Primary, Encoding.UTF8.GetBytes($"expected-{version}"))]));
    internal Task RoleAsync(string role) => Case.ExecuteAsync("UPDATE weft.project_memberships SET role=$1,version=version+1 WHERE principal_id=$2 AND project_id=$3",
        role, Seed.Alice, Seed.ProjectA);
    private static string Connection(string baseline, string user, string password) =>
        new NpgsqlConnectionStringBuilder(baseline) { Username = user, Password = password }.ConnectionString;
    private static string Resource(string name)
    {
        using var stream = typeof(MigrationRunner).Assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    public async ValueTask DisposeAsync()
    {
        await Reader.DisposeAsync();
        await Writer.DisposeAsync();
        await Case.DisposeAsync();
    }
}
