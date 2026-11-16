using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Braid.Application;
using Braid.Domain;
using Braid.Jobs;
using Braid.MappingBackends;
using Braid.MappingBackends.Ingestion;
using Braid.MappingBackends.Workers;
using Wolverine;
using static Braid.Tests.FerruleServiceTestSupport;

namespace Braid.Tests;

/// <summary>Actual persisted caller workflows, real child answers, and a lost answer at the service boundary.</summary>
[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class FerruleServiceDurableRecoveryTests(PostgresFixture fixture)
{
    [RecoveryPostgresFact]
    public async Task PreviewRetainsItsAttemptBudgetAndPublishesOnceAfterTheSameRunnerReopens()
    {
        await using var test = await PreviewRunTestSupport.CreateAsync(fixture);
        var root = TemporaryRoot();
        try
        {
            await using var service = await FerruleServiceEndToEndTests.ServiceHost.StartAsync(root);
            var clock = new MaintenanceServiceTestTimeProvider();
            var connector = Connector(service);
            var options = new FerruleServiceRunnerOptions(connector, "preview", service.Identity) { Clock = clock };
            await using var runner = new FerruleServiceRunner(options);
            var backend = new FerrulePreviewBackend(runner);
            var executor = test.Executor(backend);
            var api = test.Service();
            var submission = test.Submission();
            var org = test.Revision.OrganizationId;
            var run = (await api.SubmitAsync(test.Alice, submission)).RunId!.Value;
            Assert.Equal(PreviewExecutionOutcome.Released, await executor.ExecuteAsync(org, run, Guid.NewGuid()));
            Assert.False(runner.Available);
            var pending = (await api.ReadAsync(test.Alice, org, run))!;
            Assert.Equal(1, pending.Attempt);
            Assert.Empty(pending.Outputs);
            Assert.Null(pending.Provenance);
            await Assert.ThrowsAsync<PreviewAdmissionClosedException>(() => RunPreviewHandler.Handle(new(org, run), executor,
                new(Guid.NewGuid()), new(), null!, default));
            // The store, not the reopened transport, controls when a later attempt is eligible.
            await test.Case.ExecuteAsync("UPDATE braid.preview_runs SET not_before=statement_timestamp() WHERE run_id=$1", run);
            for (var index = 0; index < 10; index++)
                Assert.Equal(PreviewExecutionOutcome.Busy, await executor.ExecuteAsync(org, run, Guid.NewGuid()));
            Assert.Equal(1, (await api.ReadAsync(test.Alice, org, run))!.Attempt);
            Assert.Equal(PreviewExecutionOutcome.Busy, await executor.ExecuteAsync(org, run, Guid.NewGuid()));
            var observed = new ObservedPreviewBackend(backend);
            using (var host = Host.CreateDefaultBuilder().UseContentRoot(AppContext.BaseDirectory)
                .ConfigureLogging(logging => logging.ClearProviders()).ConfigureServices(services =>
            {
                services.AddSingleton(test.Executor(observed));
                services.AddSingleton(new PreviewWorkerInstance(Guid.NewGuid()));
                services.AddSingleton(new PreviewRunOptions());
                services.AddWolverine(configuration => PreviewJobs.Configure(configuration, test.Case.PreviewWriterConnectionString, PreviewJobRole.Worker));
            }).Build())
            {
                await host.StartAsync();
                try
                {
                    await host.Services.GetRequiredService<IMessageBus>().PublishAsync(new RunPreview(org, run));
                    await observed.Closed.Task.WaitAsync(TimeSpan.FromSeconds(20));
                    Assert.Equal(1, (await api.ReadAsync(test.Alice, org, run))!.Attempt);
                    await clock.TimerScheduled.WaitAsync(TimeSpan.FromSeconds(10));
                    clock.Advance(options.QuarantineBound);
                    await FerruleServiceRecoveryTests.Reopened(runner);
                    using var delivered = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                    while ((await api.ReadAsync(test.Alice, org, run))!.State != PreviewRunState.Completed)
                        await Task.Delay(50, delivered.Token);
                }
                finally { await host.StopAsync(); }
            }
            var completed = (await api.ReadAsync(test.Alice, org, run))!;
            Assert.Equal(2, completed.Attempt);
            Assert.Equal(PreviewRunOutcome.Succeeded, completed.Outcome);
            Assert.Equal(2, completed.Outputs.Count);
            Assert.Equal(PreviewExecutionOutcome.Finished, await executor.ExecuteAsync(org, run, Guid.NewGuid()));
            Assert.Equal(PreviewSubmitOutcome.Replayed, (await api.SubmitAsync(test.Alice, submission)).Outcome);
            Assert.Equal(2, connector.Deliveries);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [RecoveryPostgresFact]
    public async Task UploadResumeKeepsItsReceiptAndCompletesAfterServiceRecoveryWithoutHostRestart()
    {
        await using var test = await RevisionIngestionTestSupport.CreateAsync(fixture);
        var root = TemporaryRoot();
        try
        {
            await using var service = await FerruleServiceEndToEndTests.ServiceHost.StartAsync(root);
            var clock = new MaintenanceServiceTestTimeProvider();
            var connector = Connector(service);
            var options = new FerruleServiceRunnerOptions(connector, "inspection", service.Identity) { Clock = clock };
            await using var runner = new FerruleServiceRunner(options);
            var api = test.NewService(new IngestionBackendRegistry([FerruleIngestionAdmissionBackend.CreateRegistration(runner)]));
            var bytes = RevisionIngestionTestSupport.Fixture("valid-basic");
            var result = await api.CreateAsync(test.Actor, test.Project, Guid.NewGuid(), RevisionIngestionTestSupport.Request(), new MemoryStream(bytes));
            Assert.Equal(RevisionIngestionOutcome.RetryableFailure, result.Outcome);
            Assert.False(runner.Available);
            var operation = result.Receipt!.OperationId;
            for (var index = 0; index < 10; index++)
            {
                var held = await api.ResumeAsync(test.Actor, test.Project, operation);
                Assert.Equal(RevisionIngestionOutcome.RetryableFailure, held.Outcome);
                Assert.Equal(result.Receipt.ClaimCount, held.Receipt!.ClaimCount);
            }
            await clock.TimerScheduled.WaitAsync(TimeSpan.FromSeconds(10));
            clock.Advance(options.QuarantineBound);
            await FerruleServiceRecoveryTests.Reopened(runner);
            var resumed = await api.ResumeAsync(test.Actor, test.Project, operation);
            Assert.Equal(RevisionIngestionOutcome.CommittedValid, resumed.Outcome);
            Assert.Equal(operation, resumed.Receipt!.OperationId);
            Assert.True(connector.Deliveries >= 2);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static LostAnswerConnector Connector(FerruleServiceEndToEndTests.ServiceHost service) =>
        new(new PinnedServiceConnector(new(service.Endpoint, service.ServerPin, service.ClientCertificate)));

    private sealed class ObservedPreviewBackend(FerrulePreviewBackend backend) : IMappingPreviewBackend, IExecutionAdmission
    {
        internal TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CanExecute
        {
            get
            {
                var available = backend.CanExecute;
                if (!available) Closed.TrySetResult();
                return available;
            }
        }
        public Task<MappingPreviewResult> PreviewAsync(MappingPreviewRequest request, CancellationToken token = default) =>
            backend.PreviewAsync(request, token);
    }

    [RecoveryPostgresFact]
    public async Task IngestionQuarantinePreventsTheFirstProjectionAndValidationClaimsUntilTheSharedRunnerRecovers()
    {
        var root = TemporaryRoot();
        try
        {
            await using var service = await FerruleServiceEndToEndTests.ServiceHost.StartAsync(root);
            await using var setupRunner = new FerruleServiceRunner(new(new PinnedServiceConnector(new(service.Endpoint, service.ServerPin,
                service.ClientCertificate)), "inspection", service.Identity));
            await using var test = await RevisionReportTestSupport.CreateAsync(fixture,
                ingestionRegistry: new IngestionBackendRegistry([FerruleIngestionAdmissionBackend.CreateRegistration(setupRunner)]));
            var clock = new MaintenanceServiceTestTimeProvider();
            var connector = Connector(service);
            var options = new FerruleServiceRunnerOptions(connector, "inspection", service.Identity) { Clock = clock };
            await using var runner = new FerruleServiceRunner(options);
            var registry = FerruleRevisionReportBackendFactory.CreateRegistry(
                FerruleRevisionReportBackendFactory.CreateProfile(runner.BuildIdentity), new(), () => runner, runner);
            var api = test.NewService(registry);
            await new FerruleIngestionAdmissionBackend(runner).ValidateAsync(
                NativeArtifact.Admit(test.Revision, test.NativeBytes, InspectionPolicy.MaximumNativeBytes));
            Assert.False(runner.Available);
            Assert.Equal(1, connector.Deliveries);
            var before = await test.CountsAsync();
            foreach (var operation in Enum.GetValues<RevisionReportOperation>())
            {
                var held = await api.EnsureAsync(test.Actor, test.Revision, operation);
                Assert.Equal(RevisionReportEnsureOutcome.AdmissionBusy, held.Outcome);
                Assert.Null(held.NextAttemptAtUtc);
                Assert.Equal(before, await test.CountsAsync());
            }
            Assert.Equal(1, connector.Deliveries);
            await clock.TimerScheduled.WaitAsync(TimeSpan.FromSeconds(10));
            clock.Advance(options.QuarantineBound);
            await FerruleServiceRecoveryTests.Reopened(runner);
            foreach (var operation in Enum.GetValues<RevisionReportOperation>())
                Assert.NotNull(RevisionReportTestSupport.Available(await api.EnsureAsync(test.Actor, test.Revision, operation)));
            Assert.Equal(3, connector.Deliveries);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [RecoveryPostgresFact]
    public async Task DerivationEnsureRetainsItsBackoffAndCreatesAUsableAttemptAfterRunnerRecovery()
    {
        var root = TemporaryRoot();
        try
        {
            await using var service = await FerruleServiceEndToEndTests.ServiceHost.StartAsync(root);
            await using var setupRunner = new FerruleServiceRunner(new(new PinnedServiceConnector(new(service.Endpoint, service.ServerPin,
                service.ClientCertificate)), "inspection", service.Identity));
            await using var test = await RevisionReportTestSupport.CreateAsync(fixture,
                ingestionRegistry: new IngestionBackendRegistry([FerruleIngestionAdmissionBackend.CreateRegistration(setupRunner)]));
            var clock = new MaintenanceServiceTestTimeProvider();
            var connector = Connector(service);
            var options = new FerruleServiceRunnerOptions(connector, "inspection", service.Identity) { Clock = clock };
            await using var runner = new FerruleServiceRunner(options);
            var profile = FerruleRevisionReportBackendFactory.CreateProfile(runner.BuildIdentity);
            var registry = FerruleRevisionReportBackendFactory.CreateRegistry(profile, new(), () => runner);
            var api = test.NewService(registry);
            var first = await api.EnsureAsync(test.Actor, test.Revision, RevisionReportOperation.Projection);
            Assert.False(runner.Available);
            Assert.Equal(RevisionReportState.Failed, first.Read!.State);
            Assert.Null(first.Read.Report);
            var beforeValidation = await test.CountsAsync();
            var heldValidation = await api.EnsureAsync(test.Actor, test.Revision, RevisionReportOperation.Validation);
            Assert.Equal(RevisionReportEnsureOutcome.AdmissionBusy, heldValidation.Outcome);
            Assert.Equal(beforeValidation, await test.CountsAsync());
            Assert.Equal(1, connector.Deliveries);
            var early = await api.EnsureAsync(test.Actor, test.Revision, RevisionReportOperation.Projection);
            Assert.Equal(RevisionReportEnsureOutcome.RetryDelayed, early.Outcome);
            Assert.Equal(1, connector.Deliveries);
            var wait = first.NextAttemptAtUtc!.Value - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(100);
            Assert.InRange(wait, TimeSpan.Zero, TimeSpan.FromMinutes(2));
            await Task.Delay(wait);
            var before = await test.CountsAsync();
            var held = await api.EnsureAsync(test.Actor, test.Revision, RevisionReportOperation.Projection);
            Assert.Equal(RevisionReportEnsureOutcome.AdmissionBusy, held.Outcome);
            Assert.Equal(before, await test.CountsAsync());
            Assert.Equal(1, connector.Deliveries);
            await clock.TimerScheduled.WaitAsync(TimeSpan.FromSeconds(10));
            clock.Advance(options.QuarantineBound);
            await FerruleServiceRecoveryTests.Reopened(runner);
            var recovered = await api.EnsureAsync(test.Actor, test.Revision, RevisionReportOperation.Projection);
            Assert.NotNull(RevisionReportTestSupport.Available(recovered));
            Assert.Equal(2, connector.Deliveries);
            await api.EnsureAsync(test.Actor, test.Revision, RevisionReportOperation.Projection);
            Assert.Equal(2, connector.Deliveries); // The retained result, not another engine operation.
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class LostAnswerConnector(IFerruleServiceConnector inner) : IFerruleServiceConnector
    {
        private int lost;
        internal int Deliveries;
        public IFerruleServiceChannel Open() => new Channel(this, inner.Open());
        private sealed class Channel(LostAnswerConnector owner, IFerruleServiceChannel inner) : IFerruleServiceChannel
        {
            public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                var reply = await inner.SendAsync(request, token);
                if (request.Method == HttpMethod.Post && !request.RequestUri!.AbsolutePath.EndsWith("/cancel", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref owner.Deliveries);
                    if (Interlocked.Exchange(ref owner.lost, 1) == 0)
                    {
                        // The child really executed and settled, but the host has no authenticated terminal answer.
                        reply.Dispose();
                        throw new IOException("The settled answer was lost.");
                    }
                }
                return reply;
            }
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    public sealed class RecoveryPostgresFactAttribute : FactAttribute
    {
        public RecoveryPostgresFactAttribute()
        {
            if (!PostgresFixture.IsConfigured || string.IsNullOrEmpty(Executable) ||
                string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BRAID_FERRULE_WORKER_IMAGE")))
                Skip = "Set the PostgreSQL image, Ferrule worker image and native executable for durable service recovery.";
        }
    }
}
