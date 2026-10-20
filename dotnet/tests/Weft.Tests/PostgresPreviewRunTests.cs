using System.Text;
using System.Text.Json.Nodes;
using Npgsql;
using Weft.Application;
using Weft.Domain;
using Weft.MappingBackends;
using Weft.MappingBackends.Workers;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class PostgresPreviewRunTests(PostgresFixture fixture)
{
    private static readonly byte[] Order = PreviewRunTestSupport.Order;
    private static readonly byte[] Rates = PreviewRunTestSupport.Rates;

    [PostgresFact]
    public async Task SubmissionReplaysOneKeyRefusesChangedContentAndRequiresFreshRunnerAuthority()
    {
        await using var test = await PreviewRunTestSupport.CreateAsync(fixture);
        var submission = test.Submission();
        var created = await test.Service().SubmitAsync(test.Alice, submission);
        Assert.Equal(PreviewSubmitOutcome.Created, created.Outcome);
        var run = created.RunId!.Value;

        var replayed = await test.Service().SubmitAsync(test.Alice, submission with
        {
            Primary = new(null, Order),
            NamedInputs = [new("rates", Rates)]
        });
        Assert.Equal(new PreviewSubmitResult(PreviewSubmitOutcome.Replayed, run), replayed);
        Assert.Equal(new PreviewSubmitResult(PreviewSubmitOutcome.Conflict, null), await test.Service().SubmitAsync(test.Alice,
            submission with { Parameters = [new("region", PreviewParameterKind.String, "east"), new("express", PreviewParameterKind.Boolean, "true")] }));
        Assert.Equal(new PreviewSubmitResult(PreviewSubmitOutcome.Conflict, null), await test.Service().SubmitAsync(test.Alice,
            submission with { NamedInputs = [new("rates", "{\"currency\":\"USD\"}"u8)] }));

        await test.Catalog.Seed.AddMembershipAsync(test.Case, test.Catalog.Seed.OrganizationA, test.Catalog.Seed.ProjectA,
            test.Catalog.Seed.Admitted, "viewer");
        var viewer = new ActorContext(new PrincipalId(test.Catalog.Seed.Admitted));
        Assert.Equal(PreviewSubmitOutcome.NotAuthorizedOrNotFound,
            (await test.Service().SubmitAsync(viewer, submission with { RequestKey = Guid.NewGuid() })).Outcome);
        Assert.Equal(PreviewSubmitOutcome.NotAuthorizedOrNotFound,
            (await test.Service().SubmitAsync(new ActorContext(new PrincipalId(test.Catalog.Seed.Bob)), submission)).Outcome);
        Assert.Equal(PreviewSubmitOutcome.NotAuthorizedOrNotFound, (await test.Service().SubmitAsync(test.Alice,
            submission with { Revision = new(test.Revision.OrganizationId, test.Revision.ProjectId, test.Revision.MappingId, Guid.NewGuid()), RequestKey = Guid.NewGuid() })).Outcome);

        var snapshot = Assert.IsType<PreviewRunSnapshot>(await test.Service().ReadAsync(test.Alice, test.Revision.OrganizationId, run));
        Assert.Equal(PreviewRunState.Queued, snapshot.State);
        Assert.Equal(test.Revision, snapshot.Revision);
        Assert.Equal(0, snapshot.Attempt);
        Assert.Equal(3, snapshot.MaxAttempts);
        Assert.Null(snapshot.Outcome);
        Assert.Empty(snapshot.Outputs);
        Assert.Equal("2026-10-20T08:30:00.0000000Z", snapshot.CurrentDateTime);
        Assert.Equal(PreviewRequestDocument.Fingerprint(PreviewRequestDocument.Encode(submission, PreviewLimits.Default)), snapshot.RequestFingerprint);
        Assert.InRange(snapshot.ExpiresAtUtc - snapshot.CreatedAtUtc, TimeSpan.FromHours(24) - TimeSpan.FromSeconds(1), TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1));
        Assert.Equal(new long[] { 2, Order.Length + Rates.Length }, await test.Case.ScalarAsync<long[]>(
            "SELECT ARRAY[count(*), sum(byte_count)] FROM weft.preview_documents WHERE run_id=$1 AND direction=1", run));

        // Reads are submitter-only, and authority is checked again on every read.
        await test.Case.ExecuteAsync("UPDATE weft.project_memberships SET role='runner',version=version+1 WHERE principal_id=$1", test.Catalog.Seed.Admitted);
        Assert.Null(await test.Service().ReadAsync(viewer, test.Revision.OrganizationId, run));
        await test.Case.ExecuteAsync("UPDATE weft.project_memberships SET role='viewer',version=version+1 WHERE principal_id=$1", test.Catalog.Seed.Alice);
        Assert.Null(await test.Service().ReadAsync(test.Alice, test.Revision.OrganizationId, run));
        Assert.Equal(PreviewSubmitOutcome.NotAuthorizedOrNotFound, (await test.Service().SubmitAsync(test.Alice, submission)).Outcome);
    }

    [PostgresFact]
    public async Task LeasesFenceEveryTransitionAndOnlyTheCurrentAttemptPublishesOneResultSet()
    {
        await using var test = await PreviewRunTestSupport.CreateAsync(fixture);
        var run = (await test.Service().SubmitAsync(test.Alice, test.Submission())).RunId!.Value;
        var org = test.Revision.OrganizationId;
        var first = Assert.IsType<PreviewRunClaim>((await test.Store.ClaimAsync(org, run, Guid.NewGuid(), TimeSpan.FromSeconds(60))).Claim);
        Assert.Equal(1, first.Attempt);
        Assert.Equal(test.Revision, first.Revision);
        Assert.Equal(test.Native, first.NativeObject);
        Assert.Equal(test.Alice.PrincipalId, first.Submitter);
        Assert.Equal(PreviewClaimOutcome.Busy, (await test.Store.ClaimAsync(org, run, Guid.NewGuid(), TimeSpan.FromSeconds(60))).Outcome);
        var inputs = await test.Store.ReadInputsAsync(first);
        Assert.Equal([null, "rates"], inputs.Select(input => input.Name));
        Assert.Equal(Order, inputs[0].Content);
        Assert.Equal(Sha256Digest.FromBytes(Rates), inputs[1].Digest);

        await test.Case.ExecuteAsync("UPDATE weft.preview_runs SET lease_expires_at=statement_timestamp()-interval '1 second' WHERE run_id=$1", run);
        var second = Assert.IsType<PreviewRunClaim>((await test.Store.ClaimAsync(org, run, Guid.NewGuid(), TimeSpan.FromSeconds(60))).Claim);
        Assert.Equal(2, second.Attempt);
        Assert.NotEqual(first.LeaseToken, second.LeaseToken);
        Assert.Empty(await test.Store.ReadInputsAsync(first));
        Assert.Equal(new PreviewClaimObservation(false, false), await test.Store.ObserveAsync(first));
        Assert.Equal(new PreviewClaimObservation(true, false), await test.Store.ObserveAsync(second));

        var stale = Success("stale");
        Assert.False(await test.Store.CompleteAsync(first, stale));
        Assert.False(await test.Store.ReleaseAsync(first, TimeSpan.Zero));
        Assert.True(await test.Store.CompleteAsync(second, Success("current")));
        Assert.False(await test.Store.CompleteAsync(second, Success("again")));
        Assert.Equal(PreviewClaimOutcome.Finished, (await test.Store.ClaimAsync(org, run, Guid.NewGuid(), TimeSpan.FromSeconds(60))).Outcome);

        var snapshot = Assert.IsType<PreviewRunSnapshot>(await test.Service().ReadAsync(test.Alice, org, run));
        Assert.Equal(PreviewRunState.Completed, snapshot.State);
        Assert.Equal(PreviewRunOutcome.Succeeded, snapshot.Outcome);
        Assert.Equal(2, snapshot.Attempt);
        Assert.Equal(new PreviewRunProvenance(true, "engine", "image", "adapter", "profile"), snapshot.Provenance);
        Assert.Equal(7, snapshot.RecordsWritten);
        Assert.Equal(["primary-output", "named-output:0"], snapshot.Outputs.Select(output => output.BoundaryId));
        var output = Assert.IsType<PreviewOutputContent>(await test.Service().ReadOutputAsync(test.Alice, org, run, 1));
        Assert.Equal("{\"current\":1}"u8.ToArray(), output.Content);
        Assert.Equal(snapshot.Outputs[1].Digest, output.Digest);
        Assert.Null(await test.Service().ReadOutputAsync(test.Alice, org, run, 2));
        Assert.Equal(2L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.preview_documents WHERE run_id=$1 AND direction=2", run));
    }

    [PostgresFact]
    public async Task CancellationRevocationAndExhaustedAttemptsFinishWithoutPublishingOutputs()
    {
        await using var test = await PreviewRunTestSupport.CreateAsync(fixture);
        var org = test.Revision.OrganizationId;
        var queued = (await test.Service().SubmitAsync(test.Alice, test.Submission())).RunId!.Value;
        Assert.Equal(PreviewCancelOutcome.NotAuthorizedOrNotFound,
            await test.Service().CancelAsync(new ActorContext(new PrincipalId(test.Catalog.Seed.Bob)), org, queued));
        Assert.Equal(PreviewCancelOutcome.Canceled, await test.Service().CancelAsync(test.Alice, org, queued));
        Assert.Equal(PreviewCancelOutcome.Finished, await test.Service().CancelAsync(test.Alice, org, queued));
        Assert.Equal(PreviewClaimOutcome.Finished, (await test.Store.ClaimAsync(org, queued, Guid.NewGuid(), TimeSpan.FromSeconds(60))).Outcome);
        var canceled = Assert.IsType<PreviewRunSnapshot>(await test.Service().ReadAsync(test.Alice, org, queued));
        Assert.Equal((PreviewRunState.Canceled, PreviewRunOutcome.Canceled, 0), (canceled.State, canceled.Outcome!.Value, canceled.Attempt));

        var running = (await test.Service().SubmitAsync(test.Alice, test.Submission())).RunId!.Value;
        var claim = Assert.IsType<PreviewRunClaim>((await test.Store.ClaimAsync(org, running, Guid.NewGuid(), TimeSpan.FromSeconds(60))).Claim);
        Assert.Equal(PreviewCancelOutcome.Requested, await test.Service().CancelAsync(test.Alice, org, running));
        Assert.Equal(new PreviewClaimObservation(true, true), await test.Store.ObserveAsync(claim));
        Assert.True(await test.Store.ReleaseAsync(claim, TimeSpan.Zero));
        Assert.Equal(PreviewClaimOutcome.Finished, (await test.Store.ClaimAsync(org, running, Guid.NewGuid(), TimeSpan.FromSeconds(60))).Outcome);
        Assert.Equal(PreviewRunOutcome.Canceled, (await test.Service().ReadAsync(test.Alice, org, running))!.Outcome);

        var exhausted = (await test.Service(new() { MaxAttempts = 1 }).SubmitAsync(test.Alice, test.Submission())).RunId!.Value;
        claim = Assert.IsType<PreviewRunClaim>((await test.Store.ClaimAsync(org, exhausted, Guid.NewGuid(), TimeSpan.FromSeconds(60))).Claim);
        Assert.True(await test.Store.ReleaseAsync(claim, TimeSpan.FromSeconds(3600)));
        Assert.Equal(PreviewClaimOutcome.Busy, (await test.Store.ClaimAsync(org, exhausted, Guid.NewGuid(), TimeSpan.FromSeconds(60))).Outcome);
        await test.Case.ExecuteAsync("UPDATE weft.preview_runs SET not_before=statement_timestamp()-interval '1 second' WHERE run_id=$1", exhausted);
        Assert.Equal(PreviewClaimOutcome.Finished, (await test.Store.ClaimAsync(org, exhausted, Guid.NewGuid(), TimeSpan.FromSeconds(60))).Outcome);
        Assert.Equal(PreviewRunOutcome.AttemptsExhausted, (await test.Service().ReadAsync(test.Alice, org, exhausted))!.Outcome);

        var revoked = (await test.Service().SubmitAsync(test.Alice, test.Submission())).RunId!.Value;
        await test.Case.ExecuteAsync("UPDATE weft.project_memberships SET active=false,version=version+1 WHERE principal_id=$1", test.Catalog.Seed.Alice);
        Assert.Equal(PreviewClaimOutcome.Finished, (await test.Store.ClaimAsync(org, revoked, Guid.NewGuid(), TimeSpan.FromSeconds(60))).Outcome);
        Assert.Null(await test.Service().ReadAsync(test.Alice, org, revoked));
        Assert.Equal("authorityRevoked", await test.Case.ScalarAsync<string>("SELECT outcome FROM weft.preview_runs WHERE run_id=$1", revoked));
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.preview_documents WHERE direction=2"));
    }

    [PostgresFact]
    public async Task RestrictedProfilesReachOnlyTheirOwnFunctionsAndNoTables()
    {
        await using var test = await PreviewRunTestSupport.CreateAsync(fixture);
        var run = (await test.Service().SubmitAsync(test.Alice, test.Submission())).RunId!.Value;
        foreach (var (connection, sql) in new[]
        {
            (test.Case.PreviewReaderConnectionString, "SELECT count(*) FROM weft.preview_runs"),
            (test.Case.PreviewReaderConnectionString, "SELECT count(*) FROM weft.preview_documents"),
            (test.Case.PreviewReaderConnectionString, $"SELECT weft.preview_cancel('{test.Revision.OrganizationId}','{run}','{test.Catalog.Seed.Alice}')"),
            (test.Case.PreviewWriterConnectionString, "SELECT count(*) FROM weft.preview_runs"),
            (test.Case.PreviewWriterConnectionString, $"SELECT * FROM weft.preview_read('{test.Revision.OrganizationId}','{run}','{test.Catalog.Seed.Alice}')"),
            (test.Case.PreviewWriterConnectionString, "UPDATE weft.preview_runs SET state=3"),
            (test.Case.CatalogConnectionString, $"SELECT * FROM weft.preview_read('{test.Revision.OrganizationId}','{run}','{test.Catalog.Seed.Alice}')"),
            (test.Case.ChatWriterConnectionString, $"SELECT weft.preview_cancel('{test.Revision.OrganizationId}','{run}','{test.Catalog.Seed.Alice}')")
        })
        {
            await using var source = NpgsqlDataSource.Create(connection);
            await using var command = source.CreateCommand(sql);
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteScalarAsync());
            Assert.Equal("42501", error.SqlState);
        }
        Assert.Equal(PreviewRunState.Queued, (await test.Service().ReadAsync(test.Alice, test.Revision.OrganizationId, run))!.State);
    }

    [PostgresFact]
    public async Task InfrastructureFailuresReleaseForBoundedRetryAndStoredInputsAreVerified()
    {
        await using var test = await PreviewRunTestSupport.CreateAsync(fixture);
        var org = test.Revision.OrganizationId;
        var backend = new PreviewRunTestSupport.ScriptedBackend(MappingPreviewStatus.BackendUnavailable);
        var options = new PreviewRunOptions { MaxAttempts = 2, RetryDelay = TimeSpan.FromSeconds(30) };
        var service = test.Service(options);
        var executor = test.Executor(backend, options);
        var run = (await service.SubmitAsync(test.Alice, test.Submission())).RunId!.Value;
        Assert.Equal(PreviewExecutionOutcome.Released, await executor.ExecuteAsync(org, run, Guid.NewGuid()));
        Assert.Equal(PreviewExecutionOutcome.Busy, await executor.ExecuteAsync(org, run, Guid.NewGuid()));
        await test.Case.ExecuteAsync("UPDATE weft.preview_runs SET not_before=statement_timestamp() WHERE run_id=$1", run);
        Assert.Equal(PreviewExecutionOutcome.Released, await executor.ExecuteAsync(org, run, Guid.NewGuid()));
        await test.Case.ExecuteAsync("UPDATE weft.preview_runs SET not_before=statement_timestamp() WHERE run_id=$1", run);
        Assert.Equal(PreviewExecutionOutcome.Finished, await executor.ExecuteAsync(org, run, Guid.NewGuid()));
        Assert.Equal(2, backend.Calls);
        Assert.Equal(PreviewRunOutcome.AttemptsExhausted, (await service.ReadAsync(test.Alice, org, run))!.Outcome);
        var request = Assert.IsType<MappingPreviewRequest>(backend.LastRequest);
        Assert.Equal("2026-10-20T08:30:00.0000000Z", request.CurrentDateTime);
        Assert.Equal(test.Native.Digest, request.Artifact.Digest);
        Assert.Equal(Order, request.Primary.CopyBytes());
        Assert.Equal("rates", Assert.Single(request.NamedInputs).Name);

        var tampered = (await service.SubmitAsync(test.Alice, test.Submission())).RunId!.Value;
        await test.Case.ExecuteAsync("UPDATE weft.preview_runs SET request_document=replace(request_document,'\"west\"','\"east\"'),"
            + "request_fingerprint=encode(sha256(convert_to(replace(request_document,'\"west\"','\"east\"'),'UTF8')),'hex') WHERE run_id=$1", tampered);
        await test.Case.ExecuteAsync("UPDATE weft.preview_runs SET request_document=replace(request_document,'\"byteLength\":18','\"byteLength\":19'),"
            + "request_fingerprint=encode(sha256(convert_to(replace(request_document,'\"byteLength\":18','\"byteLength\":19'),'UTF8')),'hex') WHERE run_id=$1", tampered);
        Assert.Equal(PreviewExecutionOutcome.Completed, await executor.ExecuteAsync(org, tampered, Guid.NewGuid()));
        var refused = (await service.ReadAsync(test.Alice, org, tampered))!;
        Assert.Equal(PreviewRunOutcome.ProtocolFailure, refused.Outcome);
        Assert.Equal("preview.storedRequest", refused.Failure!.Code);
        Assert.Equal(2, backend.Calls);
    }

    [PostgresFact]
    public async Task ARequestedCancellationStopsTheRunningBackendAndCompletesTheRunAsCanceled()
    {
        await using var test = await PreviewRunTestSupport.CreateAsync(fixture);
        var backend = new PreviewRunTestSupport.ScriptedBackend(MappingPreviewStatus.Succeeded, blockUntilCanceled: true);
        var options = new PreviewRunOptions { CancellationPollInterval = TimeSpan.FromMilliseconds(100) };
        var service = test.Service(options);
        var executor = test.Executor(backend, options);
        var org = test.Revision.OrganizationId;
        var run = (await service.SubmitAsync(test.Alice, test.Submission())).RunId!.Value;
        var execution = executor.ExecuteAsync(org, run, Guid.NewGuid());
        await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(PreviewRunState.Running, (await service.ReadAsync(test.Alice, org, run))!.State);
        Assert.Equal(PreviewCancelOutcome.Requested, await service.CancelAsync(test.Alice, org, run));
        Assert.Equal(PreviewExecutionOutcome.Completed, await execution.WaitAsync(TimeSpan.FromSeconds(30)));
        var snapshot = (await service.ReadAsync(test.Alice, org, run))!;
        Assert.Equal((PreviewRunState.Canceled, PreviewRunOutcome.Canceled), (snapshot.State, snapshot.Outcome!.Value));
        Assert.Equal("preview.canceled", snapshot.Failure!.Code);
        Assert.Null(snapshot.Provenance);
        Assert.Empty(snapshot.Outputs);

        // Host shutdown releases the attempt for another worker instead of finishing it.
        var released = (await service.SubmitAsync(test.Alice, test.Submission())).RunId!.Value;
        var shutdownBackend = new PreviewRunTestSupport.ScriptedBackend(MappingPreviewStatus.Succeeded, blockUntilCanceled: true);
        using var shutdown = new CancellationTokenSource();
        var stopping = test.Executor(shutdownBackend).ExecuteAsync(org, released, Guid.NewGuid(), shutdown.Token);
        await shutdownBackend.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await shutdown.CancelAsync();
        Assert.Equal(PreviewExecutionOutcome.Released, await stopping.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal((PreviewRunState.Queued, 1), ((await service.ReadAsync(test.Alice, org, released))!.State, (await service.ReadAsync(test.Alice, org, released))!.Attempt));
    }

    [PreviewWorkerFact]
    public async Task ActualWorkerExecutionPublishesExactOutputsOnceAndReplaysTheFinishedRun()
    {
        await using var test = await PreviewRunTestSupport.CreateAsync(fixture);
        var backend = new FerrulePreviewBackend(new DockerInspectionWorkerRunner(new DockerInspectionWorkerOptions("/usr/bin/docker",
            Environment.GetEnvironmentVariable("WEFT_FERRULE_WORKER_IMAGE")!, profile: DockerWorkerProfile.Preview)));
        var service = test.Service();
        var executor = test.Executor(backend);
        var org = test.Revision.OrganizationId;
        var submission = test.Submission();
        var run = (await service.SubmitAsync(test.Alice, submission)).RunId!.Value;
        Assert.Equal(PreviewExecutionOutcome.Completed, await executor.ExecuteAsync(org, run, Guid.NewGuid()));
        Assert.Equal(PreviewExecutionOutcome.Finished, await executor.ExecuteAsync(org, run, Guid.NewGuid()));
        Assert.Equal(new PreviewSubmitResult(PreviewSubmitOutcome.Replayed, run), await service.SubmitAsync(test.Alice, submission));

        var snapshot = Assert.IsType<PreviewRunSnapshot>(await service.ReadAsync(test.Alice, org, run));
        Assert.True(snapshot.Outcome == PreviewRunOutcome.Succeeded, snapshot.Failure?.Message);
        Assert.Equal(1, snapshot.Attempt);
        Assert.Equal(Environment.GetEnvironmentVariable("WEFT_FERRULE_WORKER_IMAGE"), snapshot.Provenance!.BackendBuild);
        Assert.Equal(FerruleInspectionBackend.EngineRevision, snapshot.Provenance.EngineRevision);
        Assert.Equal(FerrulePreviewBackend.AdapterBuild, snapshot.Provenance.AdapterBuild);
        Assert.Equal(["primary-output", "named-output:0"], snapshot.Outputs.Select(output => output.BoundaryId));
        var shipment = (await service.ReadOutputAsync(test.Alice, org, run, 0))!.Content;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""
            {"orderId":"SO-1","region":"west","generatedAt":"2026-10-20T08:30:00.0000000Z","currency":"EUR","express":true,
             "items":[{"sku":"A","quantity":2},{"sku":"B","quantity":9007199254740993}]}
            """), JsonNode.Parse(shipment)));
        Assert.Contains("9007199254740993", Encoding.UTF8.GetString(shipment), StringComparison.Ordinal);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"lineCount":2,"region":"west"}"""),
            JsonNode.Parse((await service.ReadOutputAsync(test.Alice, org, run, 1))!.Content)));
        Assert.Equal(2L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.preview_documents WHERE run_id=$1 AND direction=2", run));
    }

    private static PreviewRunCompletion Success(string marker) => new(PreviewRunOutcome.Succeeded, null,
        new PreviewRunProvenance(true, "engine", "image", "adapter", "profile"), 7,
        [
            new("primary-output", "Shipment", false, "application/json", 1, Encoding.UTF8.GetBytes($"{{\"{marker}\":0}}")),
            new("named-output:0", "summary", false, "application/json", 1, Encoding.UTF8.GetBytes($"{{\"{marker}\":1}}"))
        ]);

    public sealed class PreviewWorkerFactAttribute : FactAttribute
    {
        public PreviewWorkerFactAttribute()
        {
            if (!PostgresFixture.IsConfigured || !OperatingSystem.IsLinux()
                || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEFT_FERRULE_WORKER_IMAGE")))
                Skip = "Set WEFT_POSTGRES_IMAGE and WEFT_FERRULE_WORKER_IMAGE for actual preview execution.";
        }
    }
}
