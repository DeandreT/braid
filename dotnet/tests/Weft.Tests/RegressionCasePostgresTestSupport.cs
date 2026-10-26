using System.Text;
using Weft.Application;
using Weft.Application.Regression;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

/// <summary>A real historical revision upgraded through the complete ordered migration manifest.</summary>
internal sealed class RegressionCasePostgresTestSupport : IAsyncDisposable
{
    private readonly IngestionReceipt receipt;

    private RegressionCasePostgresTestSupport(PostgresFixture fixture, PostgresCase test, PostgresSeedData seed, IngestionReceipt receipt, int? historicalPrefix)
    {
        Case = test;
        Seed = seed;
        this.receipt = receipt;
        HistoricalPrefix = historicalPrefix;
        Actor = new(new(seed.Alice));
        Mapping = new(seed.OrganizationA, seed.ProjectA, receipt.Facts.FutureRevision.MappingId);
        ReaderConnection = fixture.RegressionConnectionString(test, "weft_case_reader");
        WriterConnection = fixture.RegressionConnectionString(test, "weft_case_writer");
        Reader = new(ReaderConnection, RegressionCaseDatabaseProfile.Reader);
        Writer = new(WriterConnection, RegressionCaseDatabaseProfile.Writer);
        Runtime = new(receipt.Facts.BackendProfile.BackendId, "engine", "build", "adapter", PreviewPolicy.Profile);
    }

    /// <param name="migrations">Optional ordered migration prefix for upgrade tests; the default is the current schema.</param>
    internal static async Task<RegressionCasePostgresTestSupport> CreateAsync(PostgresFixture fixture, int? migrations = null)
    {
        var test = await fixture.CreateCaseAsync(migrate: false);
        try
        {
            var scripts = MigrationRunner.EmbeddedMigrations;
            await using (var old = new MigrationRunner(test.MigrationConnectionString, scripts.Take(2).ToArray())) await old.MigrateAsync();
            var seed = await PostgresSeedData.CreateAsync(test);
            await test.ExecuteAsync("UPDATE weft.project_memberships SET role='publisher',version=version+1 WHERE principal_id=$1", seed.Alice);
            var actor = new ActorContext(new(seed.Alice));
            var receipt = await HistoricalIngestionSeed.CommitCreateAsync(test, actor, new(seed.OrganizationA, seed.ProjectA));
            await using (var baseline = new MigrationRunner(test.MigrationConnectionString, scripts.Take(migrations ?? scripts.Count).ToArray())) await baseline.MigrateAsync();
            return new(fixture, test, seed, receipt, migrations);
        }
        catch { await test.DisposeAsync(); throw; }
    }

    internal PostgresCase Case { get; }
    internal int? HistoricalPrefix { get; }
    internal PostgresSeedData Seed { get; }
    internal Guid CaseId { get; } = Guid.NewGuid();
    internal ActorContext Actor { get; }
    internal MappingScope Mapping { get; }
    internal RegressionRuntimeIdentity Runtime { get; }
    internal string ReaderConnection { get; }
    internal string WriterConnection { get; }
    internal PostgresRegressionCaseStore Reader { get; }
    internal PostgresRegressionCaseStore Writer { get; }
    internal async Task<RegressionCaseSaveResult> SaveAsync(ActorContext actor, RegressionCaseVersion value) =>
        await HistoricalRegressionAccess.AppliesAsync(this)
            ? await HistoricalRegressionAccess.SaveAsync(this, actor, value) : await Writer.SaveAsync(actor, value);
    internal async Task<RegressionCaseVersion?> ReadAsync(ActorContext actor, MappingScope mapping, Guid id, long version) =>
        await HistoricalRegressionAccess.AppliesAsync(this)
            ? await HistoricalRegressionAccess.ReadAsync(this, actor, mapping, id, version) : await Reader.ReadAsync(actor, mapping, id, version);
    internal RegressionCaseVersion Version(long version, string name = "case", Guid? caseId = null, PrincipalId? owner = null) =>
        new(caseId ?? CaseId, version, owner ?? Actor.PrincipalId, name, receipt.Facts.FutureRevision, receipt.Facts.NativeObject.Digest,
            Runtime, new(PreviewTargetSelection.All, "2026-10-20T12:00:00Z", PreviewLimits.Default,
                new(null, Encoding.UTF8.GetBytes("private-input")), [], []), RegressionComparisonService.ExactBytesPolicy,
            RegressionOutputSet.Capture([RegressionOutput.Capture(RegressionOutputKey.Primary, Encoding.UTF8.GetBytes($"expected-{version}"))]));
    internal Task RoleAsync(string role) => Case.ExecuteAsync("UPDATE weft.project_memberships SET role=$1,version=version+1 WHERE principal_id=$2 AND project_id=$3",
        role, Seed.Alice, Seed.ProjectA);
    public async ValueTask DisposeAsync()
    {
        await Reader.DisposeAsync();
        await Writer.DisposeAsync();
        await Case.DisposeAsync();
    }
}
