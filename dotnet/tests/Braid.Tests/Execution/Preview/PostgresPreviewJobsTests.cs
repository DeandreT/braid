using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Braid.Application;
using Braid.Jobs;
using Braid.Storage.FileSystem;
using Wolverine;
using Wolverine.RDBMS;
using Wolverine.Runtime;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class PostgresPreviewJobsTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task TheOutboxRecordsOneCommandOnlyWithItsCommittedRun()
    {
        await using var test = await PreviewRunTestSupport.CreateAsync(fixture);
        using var host = await SubmitterAsync(test);
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        var service = test.Service(store: new OutboxPreviewRunStore(test.Store, runtime));
        var submission = test.Submission();
        var created = await service.SubmitAsync(test.Alice, submission);
        Assert.Equal(PreviewSubmitOutcome.Created, created.Outcome);
        Assert.Equal(1L, await QueuedCommandsAsync(test, created.RunId!.Value));
        Assert.Equal(PreviewSubmitOutcome.Replayed, (await service.SubmitAsync(test.Alice, submission)).Outcome);
        Assert.Equal(PreviewSubmitOutcome.Conflict, (await service.SubmitAsync(test.Alice,
            submission with { NamedInputs = [new("rates", "{\"currency\":\"USD\"}"u8)] })).Outcome);
        Assert.Equal(1L, await QueuedCommandsAsync(test, null));

        // A transaction that fails after publishing keeps neither the run nor its command.
        var abandoned = test.Submission();
        var record = new PreviewRunRecord(abandoned, PreviewRequestDocument.Encode(abandoned, PreviewLimits.Default),
            "2026-10-20T08:30:00Z", 3, TimeSpan.FromHours(1));
        var context = new MessageContext(runtime);
        Assert.True(context.TryFindMessageDatabase(out var database));
        Guid? attempted = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Store.SubmitAsync(test.Alice, record, async (transaction, result, _) =>
        {
            attempted = result.RunId;
            await context.EnlistInOutboxAsync(new DatabaseEnvelopeTransaction(database!, transaction));
            await context.PublishAsync(new RunPreview(test.Revision.OrganizationId, result.RunId!.Value));
            throw new InvalidOperationException("Abandon after publishing.");
        }));
        Assert.NotNull(attempted);
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.preview_runs WHERE run_id=$1", attempted!.Value));
        Assert.Equal(1L, await QueuedCommandsAsync(test, null));

        var documentSubmission = test.Submission() with { Profile = PreviewExecutionProfile.Document };
        var documentRun = (await service.SubmitAsync(test.Alice, documentSubmission)).RunId!.Value;
        Assert.Equal(1L, await QueuedCommandsAsync(test, documentRun));
        var claim = (await test.Store.ClaimAsync(test.Revision.OrganizationId, documentRun, Guid.NewGuid(), TimeSpan.FromSeconds(90))).Claim!;
        Assert.Equal(PreviewExecutionProfile.Document, PreviewRequestDocument.Decode(claim.RequestDocument).Profile);
        Assert.True(await test.Store.ReleaseAsync(claim, TimeSpan.Zero));
        var reclaimed = (await test.Store.ClaimAsync(test.Revision.OrganizationId, documentRun, Guid.NewGuid(), TimeSpan.FromSeconds(90))).Claim!;
        Assert.Equal(claim.RequestDocument, reclaimed.RequestDocument);
        Assert.Equal(claim.CurrentDateTime, reclaimed.CurrentDateTime);
        Assert.Equal(PreviewSubmitOutcome.Replayed, (await service.SubmitAsync(test.Alice, documentSubmission)).Outcome);
        Assert.Equal(1L, await QueuedCommandsAsync(test, documentRun));
    }

    [PreviewWorkerProcessFact]
    public async Task TheWorkerProcessExecutesDeliveredRunsAndRecoversAKilledAttemptWithOneResultSet()
    {
        await using var test = await PreviewRunTestSupport.CreateAsync(fixture);
        using var storage = new PrivateStorageTestRoot();
        var registry = new PrivateStorageV2PhysicalRegistry();
        using (var physical = FileSystemV2PrivateObjectStore.Initialize(storage.Options, () => registry))
        using (var input = new MemoryStream(PreviewRunTestSupport.NativeBytes))
            Assert.Equal(PrivateObjectPublishResult.Created,
                await physical.PublishCreateOnlyAsync(PrivateStorageV2Data.Owner(test.Native), test.Revision, test.Native, input));
        using var host = await SubmitterAsync(test);
        var service = test.Service(store: new OutboxPreviewRunStore(test.Store, host.Services.GetRequiredService<IWolverineRuntime>()));
        var org = test.Revision.OrganizationId;

        var first = PreviewWorkerProcess.Start(test, storage);
        try
        {
            var delivered = (await service.SubmitAsync(test.Alice, test.Submission())).RunId!.Value;
            var snapshot = await FinishedAsync(service, test, delivered, first);
            Assert.True(snapshot.Outcome == PreviewRunOutcome.Succeeded, snapshot.Failure?.Message);
            Assert.Equal(1, snapshot.Attempt);
            Assert.Equal(["primary-output", "named-output:0"], snapshot.Outputs.Select(output => output.BoundaryId));

            // Kill the worker while its attempt holds the lease; a restarted worker reclaims the delivery.
            var interrupted = (await service.SubmitAsync(test.Alice, test.Submission())).RunId!.Value;
            await UntilAsync(async () => (await service.ReadAsync(test.Alice, org, interrupted))!.State == PreviewRunState.Running,
                TimeSpan.FromMinutes(3), first);
            first.Kill();
            var orphaned = (await service.ReadAsync(test.Alice, org, interrupted))!;
            Assert.Equal((PreviewRunState.Running, 1), (orphaned.State, orphaned.Attempt));
        }
        finally { first.Kill(); }

        var second = PreviewWorkerProcess.Start(test, storage);
        try
        {
            var runs = await test.Case.ScalarAsync<Guid[]>("SELECT array_agg(run_id ORDER BY created_at) FROM braid.preview_runs");
            var recovered = await FinishedAsync(service, test, runs[1], second);
            Assert.True(recovered.Outcome == PreviewRunOutcome.Succeeded, recovered.Failure?.Message);
            Assert.Equal(2, recovered.Attempt);
            Assert.Equal(new long[] { 2, 2 }, await test.Case.ScalarAsync<long[]>(
                "SELECT array_agg(c ORDER BY r) FROM (SELECT run_id r, count(*) c FROM braid.preview_documents WHERE direction=2 GROUP BY run_id) s"));
            Assert.Equal(new[] { 1, 2 }, await test.Case.ScalarAsync<int[]>("SELECT array_agg(attempt ORDER BY created_at) FROM braid.preview_runs"));
        }
        finally { second.Kill(); }
    }

    private static async Task<IHost> SubmitterAsync(PreviewRunTestSupport test)
    {
        var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services => services.AddWolverine(options =>
                PreviewJobs.Configure(options, test.Case.PreviewWriterConnectionString, PreviewJobRole.Submitter)))
            .Build();
        await host.StartAsync();
        return host;
    }

    /// <summary>Counts queued commands through the writer login that owns the message tables.</summary>
    private static async Task<long> QueuedCommandsAsync(PreviewRunTestSupport test, Guid? run)
    {
        await using var source = NpgsqlDataSource.Create(test.Case.PreviewWriterConnectionString);
        await using var command = source.CreateCommand(
            $"SELECT body FROM {PreviewJobs.Schema}.wolverine_queue_{PreviewJobs.Queue} WHERE message_type LIKE '%RunPreview%'");
        await using var reader = await command.ExecuteReaderAsync();
        long count = 0;
        while (await reader.ReadAsync())
        {
            var body = Encoding.UTF8.GetString(reader.GetFieldValue<byte[]>(0));
            if (run is null || body.Contains(run.Value.ToString("D"), StringComparison.OrdinalIgnoreCase))
                count++;
        }
        return count;
    }

    private static async Task<PreviewRunSnapshot> FinishedAsync(PreviewRunService service, PreviewRunTestSupport test, Guid run, PreviewWorkerProcess worker)
    {
        PreviewRunSnapshot? snapshot = null;
        await UntilAsync(async () =>
        {
            snapshot = await service.ReadAsync(test.Alice, test.Revision.OrganizationId, run);
            return snapshot!.State is PreviewRunState.Completed or PreviewRunState.Canceled;
        }, TimeSpan.FromMinutes(4), worker);
        return snapshot!;
    }

    private static async Task UntilAsync(Func<Task<bool>> condition, TimeSpan limit, PreviewWorkerProcess worker)
    {
        var deadline = DateTime.UtcNow + limit;
        while (!await condition())
        {
            if (worker.HasExited)
                throw new InvalidOperationException($"The preview worker exited early: {worker.Diagnostics()}");
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"The preview run did not reach the expected state: {worker.Diagnostics()}");
            await Task.Delay(50);
        }
    }

    public sealed class PreviewWorkerProcessFactAttribute : FactAttribute
    {
        public PreviewWorkerProcessFactAttribute()
        {
            if (!PostgresFixture.IsConfigured || !OperatingSystem.IsLinux()
                || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BRAID_FERRULE_WORKER_IMAGE")))
                Skip = "Set BRAID_POSTGRES_IMAGE and BRAID_FERRULE_WORKER_IMAGE for the actual preview worker process.";
        }
    }
}
