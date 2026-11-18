using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Braid.Application.Regression;
using Braid.Application;
using Xunit.Abstractions;
using Braid.As2;
using Braid.Deployment;
using Braid.Deployment.Triggers;
using Braid.Persistence.Postgres;
using Braid.Triggers.As2;
using Braid.Triggers.Sftp;
using Npgsql;

namespace Braid.Tests;

public sealed class SftpLoopbackFactAttribute : FactAttribute
{
    public SftpLoopbackFactAttribute()
    {
        if (!OperatingSystem.IsLinux() || !PostgresFixture.IsConfigured || Environment.GetEnvironmentVariable("BRAID_SFTP_TEST_PYTHON") is not { Length: > 0 })
            Skip = "Requires owned PostgreSQL and Python with Paramiko and cryptography for the independent local receiver.";
    }
}

[Collection(PostgresCollection.Name)]
public sealed class SftpAs2RouteTests(PostgresFixture fixture, ITestOutputHelper output)
{
    [SftpLoopbackFact]
    public async Task Sftp_to_pinned_document_runtime_to_durable_outbox_and_signed_https_receipt()
    {
        await using var test = await MachineInvocationPersistenceTests.Context.CreateAsync(fixture, documents: true);
        await RunRoute(test, test.Target, test.Caller, test.Application, test.Release, [2, 3]);
    }

    [SftpLoopbackFact]
    public async Task Lost_A_receipt_is_not_resent_while_B_completes_and_A_rolls_back()
    {
        await using var test = await MachineInvocationPersistenceTests.Context.CreateAsync(fixture, migrations: 65, shipment: true);
        Assert.Equal(65, await test.Case.ScalarAsync<int>("SELECT max(version) FROM braid.schema_migrations"));
        var baseline = test.Release;
        var store = test.Releases.Store;
        var b = new LocalReleaseTarget(new(test.Target.Mapping, test.Target.Environment, Guid.NewGuid()), test.Releases.Target.Profile, test.Releases.Target.RuntimeImage);
        var callerB = new MachinePrincipal(MachinePrincipal.Entra, Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(ReleaseWriteOutcome.Created, await store.RegisterAsync(test.Actor, b));
        Assert.Equal(InvocationGrantOutcome.Applied, await test.Grants.GrantAsync(test.Actor, new(Guid.NewGuid(), b.Reference, callerB)));
        var bRelease = new ReleaseIntent(Guid.NewGuid(), test.Actor.PrincipalId, b.Reference, test.Releases.First, ReleaseAction.Activate, null);
        Assert.Equal(ReleaseWriteOutcome.Created, await store.RequestAsync(test.Actor, bRelease));
        Assert.Equal(ReleaseTransitionOutcome.Activated, await store.ApplyAsync(test.Actor, b.Reference, bRelease.ReleaseId));
        var candidate = await test.Releases.Request(await test.Releases.Build([4, 5]), baseline.ReleaseId);
        Assert.Equal(ReleaseTransitionOutcome.Activated, await test.Releases.Apply(candidate));
        await RunRoute(test, test.Target, test.Caller, test.Application, candidate, [4, 5], async delivery =>
        {
            var elapsed = Stopwatch.StartNew();
            var rollback = await test.Releases.Request(test.Releases.First, candidate.ReleaseId, ReleaseAction.Rollback, baseline.ReleaseId);
            Assert.Equal(ReleaseTransitionOutcome.Activated, await test.Releases.Apply(rollback));
            output.WriteLine($"A rollback ms={elapsed.ElapsedMilliseconds}");
            Assert.Equal(TriggerResolutionOutcome.Refused, await store.ResolveDeliveryAsync(test.Actor, b.Reference, delivery, Guid.NewGuid(), 1, TriggerDeliveryResolution.Accepted));
            await RunRoute(test, b.Reference, callerB, Guid.NewGuid(), bRelease, [2, 3]);
            await RunRoute(test, test.Target, test.Caller, test.Application, rollback, [2, 3]);
            Assert.Equal("unknown", await test.Case.ScalarAsync<string>("SELECT phase FROM braid.trigger_deliveries WHERE delivery_id=$1", delivery));
            await RestoreAndReplay(test, delivery, candidate);
        });
    }

    [SftpLoopbackFact]
    public async Task Delayed_signed_receipt_cannot_settle_a_rebound_send()
    {
        await using var test = await MachineInvocationPersistenceTests.Context.CreateAsync(fixture, migrations: 65, shipment: true);
        await RunRoute(test, test.Target, test.Caller, test.Application, test.Release, [2, 3], _ => Task.CompletedTask, rebind: true);
    }

    private Task RunRoute(MachineInvocationPersistenceTests.Context test, ReleaseTargetReference target, MachinePrincipal caller, Guid app,
        ReleaseIntent release, byte[] expectedArtifact, Func<Guid, Task>? whileUnknown = null, bool rebind = false)
        => RunRoute(test.Releases, target, caller, app, release, expectedArtifact, whileUnknown, rebind);

    internal Task RunNativeRoute(LocalReleasePersistenceTests.Context test, MachinePrincipal caller, Guid app, ReleaseIntent release,
        Braid.MappingBackends.FerruleCompiledBuildRuntime runtime, Func<Guid, Task>? whileUnknown = null)
        => RunRoute(test, release.Target, caller, app, release, null, whileUnknown, false, runtime);

    private async Task RunRoute(LocalReleasePersistenceTests.Context test, ReleaseTargetReference target, MachinePrincipal caller, Guid app,
        ReleaseIntent release, byte[]? expectedArtifact, Func<Guid, Task>? whileUnknown, bool rebind, Braid.MappingBackends.FerruleCompiledBuildRuntime? native = null)
    {
        var directory = Directory.CreateTempSubdirectory("braid-route-");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("BRAID_SFTP_TEST_PYTHON")!) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sftp_as2_loopback.py")); start.ArgumentList.Add(directory.FullName);
        if (whileUnknown is not null) start.ArgumentList.Add("lose-mdn");
        using var child = Process.Start(start)!;
        try
        {
            var shipment = release.Qualification.Artifact.Intent.Boundaries.Any(b => b.Kind == BuildBoundaryKind.PrimaryInput && b.Format == "json");
            await child.StandardInput.WriteLineAsync(Convert.ToBase64String(shipment ? Shipment945Fixture.Input : DocumentPreviewTestData.Input("x12")));
            var expectedWire = native is not null ? Shipment945Fixture.NativeExpected : shipment ? Shipment945Fixture.Expected : DocumentPreviewTestData.Input("x12");
            await child.StandardInput.WriteLineAsync(Convert.ToBase64String(expectedWire));
            var line = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
            using var json = JsonDocument.Parse(line ?? throw new IOException("Fixture failed to initialize."));
            var conn = test.Case.RegressionConnectionString("braid_trigger_worker");
            await using var store = new PostgresTriggerWorkStore(conn);
            await using var fences = NpgsqlDataSource.Create(conn);
            await using var admin = new PostgresTriggerAdministrationStore(test.Case.RegressionConnectionString("braid_release_writer"));
            var source = new SftpIntakeSource(new($"inbox-{release.ReleaseId:N}", 1), "/inbox", "127.0.0.1", json.RootElement[0].GetInt32(), "fixture", json.RootElement[1].GetString()!,
                Path.Combine(directory.FullName, "client.pem"), (path, _) => File.ReadAllText(path));
            using var cert = X509Certificate2.CreateFromPemFile(Path.Combine(directory.FullName, "peer.pem"), Path.Combine(directory.FullName, "peer-key.pem"));
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false,
                ServerCertificateCustomValidationCallback = (_, presented, _, _) => presented?.GetCertHashString(HashAlgorithmName.SHA256) == cert.GetCertHashString(HashAlgorithmName.SHA256) });
            var route = new TriggerRoute(null, Guid.NewGuid(), TriggerDestinationKind.As2, new("partner", 1));
            var partner = new As2Partner(As2OutputDestination.BindingVersion(route.Connector, route.DestinationBindingId),
                new Uri($"https://127.0.0.1:{json.RootElement[2].GetInt32()}/receive"), "LOCAL", "PARTNER", cert, cert, cert);
            var dispatcher = new TriggerDeliveryDispatcher(store, [new As2OutputDestination(route.Connector, route.DestinationBindingId, partner, client, new PostgresAs2ClaimFenceStore(fences))], TimeProvider.System);
            var epoch = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60 * 60 - 60);
            var schedule = new TriggerSchedule(new("* * * * *"), "UTC", "2025b", epoch, ScheduleGap.Skip, ScheduleFold.First, ScheduleOverlap.SkipIfRunning, ScheduleCatchUp.Bounded, TimeSpan.FromHours(1), 2);
            var config = new TriggerConfiguration(Guid.NewGuid(), target, TriggerKind.Schedule, Guid.NewGuid(), source.Connector, caller, app,
                shipment ? "application/json" : "application/edi-x12", [], [route], new(new(3, [TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(20)], TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(2)), TimeSpan.FromMinutes(1), 65536, TimeSpan.FromHours(1), 32, 1024 * 1024), TriggerEnvelope.SingleOrder, schedule);
            Assert.Equal(TriggerOperationOutcome.Applied, await admin.OperateAsync(test.Actor, target, new(Guid.NewGuid(), config.TriggerId, TriggerAction.Configure, 0, config)));
            Assert.Equal(TriggerOperationOutcome.Applied, await admin.OperateAsync(test.Actor, target, new(Guid.NewGuid(), config.TriggerId, TriggerAction.Enable, 1, null)));
            await using var runs = new PostgresBuildRunStore(test.Case.RegressionConnectionString("braid_build_run_reader"), BuildRunDatabaseProfile.Reader);
            await using var builds = new PostgresBuildStore(test.Case.RegressionConnectionString("braid_build_reader"), false);
            await using var access = new PostgresDatabase(test.Case.RuntimeConnectionString);
            await using var reports = new PostgresRevisionReportDatabase(test.Case.ReportConnectionString);
            await using var evidence = new PostgresRegressionRunStore(test.Case.RegressionConnectionString("braid_run_reader"), false);
            await using var cases = new PostgresRegressionCaseStore(test.Case.RegressionConnectionString("braid_case_reader"), RegressionCaseDatabaseProfile.Reader);
            var synthetic = new Shipment945Fixture(test.Target.RuntimeImage);
            var recording = new LocalReleasePersistenceTests.RecordingRuntime(test);
            var observed = native is null ? null : new ObservedNative(native, release.Qualification.Artifact);
            ICompiledBuildRuntime selected = observed is not null ? observed : shipment ? synthetic : recording;
            var execution = new CompiledBuildExecution(runs, new BuildControlService(builds, new PostgresProjectReader(access), new PostgresRevisionReportMetadataReader(reports),
                new RegressionRunReadService(evidence, cases)), builds, new PostgresBuildObjectStore(builds, new LocalReleasePersistenceTests.NoNative(), test.Actor), selected);
            await using var ledger = new PostgresInvocationLedger(test.Case.RegressionConnectionString("braid_invocation_writer"));
            var service = new MachineInvocationService(ledger, test.Store, _ => new LocalReleaseInvocation(test.Store, execution), TimeProvider.System, Braid.MachineEndpoint.MappingEndpointResources.Limits);
            var runner = new ScheduledTriggerRunner(store, store, source, (target, id, caller, token) => service.InvokeTriggeredAsync(store, target, id, caller, token), dispatcher, () => "2025b", TimeProvider.System);
            var timer = Stopwatch.StartNew();
            var result = await runner.TickAsync(target, config.TriggerId, new(caller, app, DateTimeOffset.UtcNow.AddMinutes(5)), default);
            Assert.Equal((1, 1), (result.Admitted, result.Executed));
            if (observed is null) Assert.Equal(expectedArtifact, shipment ? synthetic.Last : recording.Last);
            else Assert.Equal(1, observed.Calls);
            Assert.Equal(release.ReleaseId, await test.Case.ScalarAsync<Guid>("SELECT release_id FROM braid.trigger_work WHERE trigger_id=$1 AND phase='completed'", config.TriggerId));
            var quarantine = await test.Store.ReadTriggerOperationsAsync(test.Actor, target.Mapping, target.TargetId, "quarantine", 0, 10);
            Assert.Equal("digestMismatch", Assert.Single(quarantine!.Value.EnumerateArray(), row => row.GetProperty("trigger_id").GetGuid() == config.TriggerId).GetProperty("reason").GetString());
            Assert.DoesNotContain("source_identity", quarantine.Value.GetRawText());
            var id = await test.Case.ScalarAsync<Guid>("SELECT delivery_id FROM braid.trigger_deliveries WHERE target_id=$1 AND destination_binding_id=$2 AND phase='pending'", target.TargetId, route.DestinationBindingId);
            var mappedMs = timer.ElapsedMilliseconds;
            Task<bool> Dispatch() => dispatcher.DispatchAsync(target, route.DestinationBindingId, default);
            Task<string> Phase() => test.Case.ScalarAsync<string>("SELECT phase FROM braid.trigger_deliveries WHERE delivery_id=$1", id);
            Assert.True(await Dispatch());
            var totalMs = timer.ElapsedMilliseconds;
            Assert.Equal(whileUnknown is null ? "accepted" : "unknown", await Phase());
            var witnessed = File.ReadAllLines(Path.Combine(directory.FullName, "accepted.log"));
            var expectedEvidence = await test.Case.ScalarAsync<string>("SELECT message_id || ' ' || payload_sha256 FROM braid.as2_send_fences WHERE delivery_id=$1", id);
            Assert.Equal(expectedEvidence, Assert.Single(witnessed));
            Assert.Equal(expectedWire, await test.Case.ScalarAsync<byte[]>("SELECT p.body FROM braid.trigger_payloads p JOIN braid.trigger_deliveries d USING(organization_id,receipt_id) WHERE d.delivery_id=$1", id));
            output.WriteLine($"{(whileUnknown is null ? "signed receipt" : "lost MDN")} SFTP-to-mapped ms={mappedMs}; SFTP-to-outcome ms={totalMs}");
            Assert.InRange(totalMs, 0L, 30_000L);
            if (whileUnknown is not null)
            {
                Assert.False(await Dispatch());
                await whileUnknown(id);
                Assert.False(await Dispatch());
                var recover = Stopwatch.StartNew();
                var writer = test.Store;
                var body = await File.ReadAllBytesAsync(Path.Combine(directory.FullName, "delayed.mdn"));
                var claim = await test.Case.ScalarAsync<Guid>("SELECT claim_id FROM braid.as2_send_fences WHERE delivery_id=$1", id);
                await using var evidenceConnection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(test.Case.MigrationConnectionString) { Pooling = false }.ConnectionString);
                await evidenceConnection.OpenAsync();
                Task<TriggerResolutionOutcome> Accept(ReleaseTargetReference scope, Guid attempt, byte[] bytes) =>
                    Braid.ReleaseTool.LocalAs2ReceiptRecovery.AcceptAsync(evidenceConnection, test.Case.RegressionConnectionString("braid_release_writer"), test.Actor, scope, id, attempt, "PARTNER", "LOCAL",
                        "multipart/signed; protocol=\"application/pkcs7-signature\"; micalg=sha-256; boundary=outer", bytes, default);
                Assert.Equal(TriggerResolutionOutcome.Refused, await Accept(new(target.Mapping, target.Environment, Guid.NewGuid()), claim, body));
                Assert.Equal(TriggerResolutionOutcome.Refused, await Accept(target, Guid.NewGuid(), body));
                var conflicting = await File.ReadAllBytesAsync(Path.Combine(directory.FullName, "conflicting.mdn"));
                Assert.Equal(TriggerResolutionOutcome.Refused, await Accept(target, claim, conflicting));
                Assert.Equal("unknown", await Phase()); Assert.False(await Dispatch());
                if (rebind)
                {
                    Assert.Equal(TriggerResolutionOutcome.Applied, await writer.ResolveDeliveryAsync(test.Actor, target, id, Guid.NewGuid(), 1, TriggerDeliveryResolution.Resend));
                    var later = await store.ClaimAsync(target, route.DestinationBindingId);
                    Assert.Equal(2, later.Send); Assert.NotEqual(claim, later.ClaimId);
                    Assert.Equal(TriggerObservationStatus.Recorded, await store.RecordObservationAsync(target, id, later.ClaimId, DeliveryObservationKind.Unknown));
                    Assert.Equal(TriggerResolutionOutcome.Refused, await Accept(target, claim, body));
                    Assert.Equal(TriggerResolutionOutcome.Refused, await Accept(target, later.ClaimId, body));
                    Assert.Equal("unknown", await Phase()); Assert.False(await Dispatch());
                    Assert.Single(File.ReadAllLines(Path.Combine(directory.FullName, "accepted.log")));
                    return;
                }
                Assert.Equal(TriggerResolutionOutcome.Applied, await Accept(target, claim, body));
                Assert.Equal(TriggerResolutionOutcome.Applied, await Accept(target, claim, body));
                Assert.Equal(TriggerResolutionOutcome.Refused, await Accept(target, claim, conflicting));
                Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.trigger_delivery_resolutions WHERE delivery_id=$1 AND outcome=0", id));
                output.WriteLine($"A scoped reconciliation including replay ms={recover.ElapsedMilliseconds}");
            }
            Assert.Equal("accepted", await Phase());
            Assert.False(await Dispatch());
            Assert.Equal(1, await test.Case.ScalarAsync<int>("SELECT sends FROM braid.trigger_deliveries WHERE delivery_id=$1", id));
            Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.as2_send_fences WHERE delivery_id=$1", id));
            Assert.Single(File.ReadAllLines(Path.Combine(directory.FullName, "accepted.log")));
        }
        finally
        {
            child.StandardInput.Close();
            if (!child.WaitForExit(5000)) { child.Kill(); await child.WaitForExitAsync(); }
            directory.Delete(recursive: true);
        }
    }

    // Observe the admitted package at the dispatch boundary without supplying a result or loading an assembly here.
    private sealed class ObservedNative(Braid.MappingBackends.FerruleCompiledBuildRuntime runtime, BuildArtifactReference expected) : ICompiledBuildRuntime
    {
        public string RuntimeImage => runtime.RuntimeImage;
        internal int Calls { get; private set; }
        public Task<CompiledBuildResult> ExecuteAsync(BuildArtifactReference artifact, BuildBytes bytes, RegressionReplay replay, CancellationToken token)
        {
            Assert.Equal(expected.Intent.BuildId, artifact.Intent.BuildId);
            Assert.Equal(expected.ContentDigest, bytes.Digest);
            Calls++;
            return runtime.ExecuteAsync(artifact, bytes, replay, token);
        }
    }

    private async Task RestoreAndReplay(MachineInvocationPersistenceTests.Context test, Guid delivery, ReleaseIntent original)
    {
        // No connector or worker is running against the destination while restoring. Never replace the source.
        var timer = Stopwatch.StartNew();
        await using var restored = await fixture.RestoreFullBackupAsync(test.Case);
        var restoreMs = timer.ElapsedMilliseconds;
        Assert.NotEqual(test.Case.Name, restored.Name);
        Assert.Equal(65L, await restored.ScalarAsync<long>("SELECT count(*) FROM braid.schema_migrations"));
        const string state = """
            SELECT jsonb_agg(jsonb_build_array(d.delivery_id,d.destination_binding_id,d.phase,d.sends,f.message_id,f.payload_sha256,
                w.request_id,w.request_digest,w.release_id,h.release_id,w.build_id,w.artifact_digest,r.digest,encode(sha256(p.body),'hex')) ORDER BY d.delivery_id)::text
            FROM braid.trigger_deliveries d JOIN braid.trigger_work w USING(organization_id,work_id)
            JOIN braid.trigger_receipts r ON (r.organization_id,r.receipt_id)=(d.organization_id,d.receipt_id)
            JOIN braid.trigger_payloads p ON (p.organization_id,p.receipt_id)=(r.organization_id,r.receipt_id)
            JOIN braid.host_order_pins h ON (w.organization_id,w.target_id,w.provider,w.tenant_id,w.object_id,w.request_id)=
                (h.organization_id,h.target_id,h.provider,h.tenant_id,h.object_id,h.request_id)
            JOIN braid.as2_send_fences f ON (f.organization_id,f.delivery_id)=(d.organization_id,d.delivery_id)
            """;
        var snapshot = await test.Case.ScalarAsync<string>(state);
        Assert.Equal(snapshot, await restored.ScalarAsync<string>(state));
        Assert.Equal(3L, await restored.ScalarAsync<long>("SELECT count(*) FROM braid.trigger_deliveries WHERE sends=1"));
        Assert.Equal(2L, await restored.ScalarAsync<long>("SELECT count(*) FROM braid.trigger_deliveries WHERE phase='accepted'"));
        Assert.Equal("unknown", await restored.ScalarAsync<string>("SELECT phase FROM braid.trigger_deliveries WHERE delivery_id=$1", delivery));
        Assert.Equal(Shipment945Fixture.Expected, await restored.ScalarAsync<byte[]>("SELECT p.body FROM braid.trigger_payloads p JOIN braid.trigger_deliveries d USING(organization_id,receipt_id) WHERE d.delivery_id=$1", delivery));
        var request = await restored.ScalarAsync<Guid>("SELECT w.request_id FROM braid.trigger_work w JOIN braid.trigger_deliveries d USING(organization_id,work_id) WHERE d.delivery_id=$1", delivery);
        var digest = await restored.ScalarAsync<string>("SELECT request_digest FROM braid.trigger_work WHERE request_id=$1", request);
        await using var ledger = new PostgresInvocationLedger(restored.RegressionConnectionString("braid_invocation_writer"));
        var replay = await ledger.ClaimAsync(test.Key(request), test.Application, Braid.Domain.Sha256Digest.Parse(digest));
        Assert.Equal(MachineInvocationClaimStatus.Recorded, replay.Status);
        Assert.Equal(original.ReleaseId, replay.ReleaseId);
        Assert.Equal(MachineInvocationOutcome.Completed, replay.Outcome);
        var expected = await test.Case.ScalarAsync<byte[]>("SELECT body FROM braid.machine_invocation_results WHERE request_id=$1", request);
        Assert.Equal(expected, replay.RetainedResponse);
        await using var work = new PostgresTriggerWorkStore(restored.RegressionConnectionString("braid_trigger_worker"));
        var binding = await restored.ScalarAsync<Guid>("SELECT destination_binding_id FROM braid.trigger_deliveries WHERE delivery_id=$1", delivery);
        Assert.NotEqual(TriggerDeliveryClaimStatus.Claimed, (await work.ClaimAsync(test.Target, binding, default)).Status);
        Assert.Equal(snapshot, await restored.ScalarAsync<string>(state));
        // Recover *in the new database* from the same independently witnessed acceptance. A lost acknowledgement
        // of this operator command must also be safe: replay changes neither the send count nor the original order.
        await using var writer = new PostgresLocalReleaseStore(restored.RegressionConnectionString("braid_release_writer"), true);
        var operation = Guid.NewGuid();
        for (var i = 0; i < 2; i++)
            Assert.Equal(TriggerResolutionOutcome.Applied, await writer.ResolveDeliveryAsync(test.Actor, test.Target, delivery, operation, 1, TriggerDeliveryResolution.Accepted));
        Assert.NotEqual(TriggerDeliveryClaimStatus.Claimed, (await work.ClaimAsync(test.Target, binding, default)).Status);
        Assert.Equal(1L, await restored.ScalarAsync<long>("SELECT count(*) FROM braid.trigger_deliveries WHERE delivery_id=$1 AND phase='accepted' AND sends=1", delivery));
        Assert.Equal(1L, await restored.ScalarAsync<long>("SELECT count(*) FROM braid.as2_send_fences WHERE delivery_id=$1", delivery));
        Assert.Equal(1L, await restored.ScalarAsync<long>("SELECT count(*) FROM braid.trigger_delivery_resolutions WHERE delivery_id=$1", delivery));
        Assert.Equal("unknown", await test.Case.ScalarAsync<string>("SELECT phase FROM braid.trigger_deliveries WHERE delivery_id=$1", delivery));
        output.WriteLine($"Full logical database backup + fresh restore ms={restoreMs}; verified replay and 3 deliveries, 3 sends, 1 unknown; verification ms={timer.ElapsedMilliseconds - restoreMs}");
    }
}
