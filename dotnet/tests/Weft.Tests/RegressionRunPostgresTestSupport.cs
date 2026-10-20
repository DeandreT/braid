using Weft.Application;
using Weft.Application.Regression;
using Weft.Contracts.Operations;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

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
        ReaderConnection = fixture.RegressionConnectionString(cases.Case, "weft_run_reader");
        WriterConnection = fixture.RegressionConnectionString(cases.Case, "weft_run_writer");
        Reader = new(ReaderConnection, writer: false);
        Writer = new(WriterConnection, writer: true);
        Queue = new(WriterConnection);
    }

    internal static async Task<RegressionRunPostgresTestSupport> CreateAsync(PostgresFixture fixture)
    {
        var cases = await RegressionCasePostgresTestSupport.CreateAsync(fixture);
        try
        {
            var value = cases.Version(1);
            await cases.Writer.SaveAsync(cases.Actor, value);
            return new(fixture, cases, value);
        }
        catch { await cases.DisposeAsync(); throw; }
    }

    internal Task<RegressionRunSubmitOutcome> SubmitAsync() => Writer.SubmitAsync(new(Actor, RunId, Reference));
    internal async Task<RegressionRunClaim> ClaimAsync() => (await Writer.ClaimAsync(Organization, RunId, Guid.NewGuid())).Claim
        ?? throw new InvalidOperationException("Expected a claim.");
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
