using Braid.Application;
using Braid.Application.Regression;
using Braid.Contracts.Operations;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

internal sealed class RegressionRunPostgresTestSupport : IAsyncDisposable
{
    internal RegressionCasePostgresTestSupport Cases { get; }
    internal RegressionCaseVersion Value { get; }
    internal RegressionCaseReference Reference { get; }
    internal ActorContext Actor => Cases.Actor;
    internal Guid Organization => Value.Revision.OrganizationId;
    internal Guid RunId { get; } = Guid.NewGuid();
    internal string ReaderConnection { get; }
    internal string WriterConnection { get; }
    internal PostgresRegressionRunStore Reader { get; }
    internal PostgresRegressionRunStore Writer { get; }
    internal PostgresRegressionRunQueue Queue { get; }

    private RegressionRunPostgresTestSupport(PostgresFixture fixture, RegressionCasePostgresTestSupport cases, RegressionCaseVersion value)
    {
        Cases = cases; Value = value;
        Reference = new RegressionCaseEnvelopeCodec().Encode(value).Reference;
        ReaderConnection = fixture.RegressionConnectionString(cases.Case, "braid_run_reader");
        WriterConnection = fixture.RegressionConnectionString(cases.Case, "braid_run_writer");
        Reader = new(ReaderConnection, writer: false);
        Writer = new(WriterConnection, writer: true);
        Queue = new(WriterConnection);
    }

    internal static async Task<RegressionRunPostgresTestSupport> CreateAsync(PostgresFixture fixture, int? migrations = null,
        Func<RegressionCaseVersion, RegressionCaseVersion>? configure = null)
    {
        var cases = await RegressionCasePostgresTestSupport.CreateAsync(fixture, migrations);
        try
        {
            var value = cases.Version(1);
            if (configure is not null) value = configure(value);
            await cases.SaveAsync(cases.Actor, value);
            return new(fixture, cases, value);
        }
        catch { await cases.DisposeAsync(); throw; }
    }

    internal async Task<RegressionRunSubmitOutcome> SubmitAsync() =>
        await HistoricalRegressionAccess.AppliesAsync(Cases)
            ? await HistoricalRegressionAccess.SubmitAsync(this) : await Writer.SubmitAsync(new(Actor, RunId, Reference));
    internal async Task<RegressionRunClaim> ClaimAsync() => await HistoricalRegressionAccess.AppliesAsync(Cases)
        ? await HistoricalRegressionAccess.ClaimAsync(this)
        : (await Writer.ClaimAsync(Organization, RunId, Guid.NewGuid())).Claim ?? throw new InvalidOperationException("Expected a claim.");
    internal async Task<bool> CompleteAsync(RegressionRunClaim claim, RegressionCaseExecution result) =>
        await HistoricalRegressionAccess.AppliesAsync(Cases)
            ? await HistoricalRegressionAccess.CompleteAsync(this, claim, result) : await Writer.CompleteAsync(claim, result);
    internal async Task<RegressionRunSnapshot?> ReadAsync(ActorContext actor, Guid organization, Guid id) =>
        await HistoricalRegressionAccess.AppliesAsync(Cases)
            ? await HistoricalRegressionAccess.ReadRunAsync(this, actor, organization, id) : await Reader.ReadAsync(actor, organization, id);
    internal RegressionCaseExecution Success()
    {
        var outputs = Value.Expected;
        return RegressionCaseExecution.Complete(MappingPreviewStatus.Succeeded, outputs,
            new RegressionComparisonService().Compare(Value.Expected, new(OperationStatusV1.Succeeded, outputs)));
    }
    public async ValueTask DisposeAsync()
    {
        await Queue.DisposeAsync(); await Reader.DisposeAsync(); await Writer.DisposeAsync(); await Cases.DisposeAsync();
    }
}
