using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using Braid.Application;
using Braid.Domain;

namespace Braid.Tests;

/// <summary>Positive recovery uses actual PostgreSQL, private filesystem, service, mapper, and qualified worker.</summary>
[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class RevisionIngestionProcessTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task ActualProcessLossWaveAndStalePhysicalWriterRecoverAfterRealDatabaseLeaseExpiry()
    {
        await using var metadata = await PostgresIngestionTestSupport.CreateAsync(fixture);
        using var root = new PrivateStorageTestRoot();
        await using var physical = root.Initialize();
        var body = Body();
        var interrupted = new List<InterruptedProcess>();
        try
        {
            foreach (var stage in new[] { "beforeObject", "afterObject", "afterValidation", "afterReady" })
            {
                var key = Guid.NewGuid();
                var directory = root.NewDirectory(stage);
                var child = await IngestionProcessProbe.StartAsync(metadata, root, "create", key, body: body,
                    gateDirectory: directory, gateStage: stage);
                interrupted.Add(new(stage, key, directory, child));
                var ready = await child.WaitReadyAsync(directory, stage);
                var receipt = await ReceiptByKeyAsync(metadata, key);
                Assert.Equal(receipt.Facts.OperationId, ready.OperationId);
                Assert.Equal(1, receipt.ClaimCount);
                Assert.Null(receipt.CommittedRevision);
                Assert.Equal(stage is "afterValidation" or "afterReady" ? 1 : 0, ready.WorkerCalls);
                Assert.Equal(stage == "beforeObject" ? 0 : 1, ready.PublishCalls);
                Assert.Equal(stage is "afterValidation" or "afterReady" ? 1 : 0, ready.ReadCalls);
                Assert.Equal(stage switch
                {
                    "afterValidation" => IngestionPhase.Staged,
                    "afterReady" => IngestionPhase.Ready,
                    _ => IngestionPhase.Reserved
                }, receipt.Phase);
                Assert.Equal(stage is "afterValidation" or "afterReady" ? (long?)1 : null, receipt.VerifiedStageGeneration);
                Assert.Equal(stage == "afterReady" ? (long?)1 : null, receipt.EligibleAttemptGeneration);
                Assert.Equal(stage == "afterReady", receipt.Attempts.Single().Completion is not null);
                if (stage == "beforeObject")
                    await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Missing,
                        async () => { await physical.ReadVerifiedAsync(receipt.Facts.FutureRevision, receipt.Facts.NativeObject); });
                else
                    Assert.Equal(body, await physical.ReadVerifiedAsync(receipt.Facts.FutureRevision, receipt.Facts.NativeObject));
                await child.KillAsync();
                interrupted[^1] = interrupted[^1] with { Original = receipt };
            }

            // This independent live process has already completed a physical publish, but
            // has not staged it. Its lease expires alongside the killed processes above.
            var staleKey = Guid.NewGuid();
            var staleDirectory = root.NewDirectory("stale-physical-writer");
            await using var stale = await IngestionProcessProbe.StartAsync(metadata, root, "create", staleKey,
                body: body, gateDirectory: staleDirectory, gateStage: "afterObject");
            var staleGate = await stale.WaitReadyAsync(staleDirectory, "afterObject");
            var staleReceipt = await ReceiptByKeyAsync(metadata, staleKey);
            var oldClaim = await ReadOwnedClaimAsync(metadata, staleReceipt);
            Assert.Equal(TimeSpan.FromSeconds(IngestionWorkflowLimits.ClaimLeaseSeconds),
                oldClaim.LeaseExpiresAtUtc - staleReceipt.Attempts.Single().StartedAtUtc);
            Assert.Equal(staleReceipt.Facts.OperationId, staleGate.OperationId);
            Assert.Equal(0, staleGate.WorkerCalls);
            Assert.Equal(body, await physical.ReadVerifiedAsync(staleReceipt.Facts.FutureRevision, staleReceipt.Facts.NativeObject));
            Assert.Equal(0L, await metadata.Case.ScalarAsync<long>("SELECT count(*) FROM braid.mapping_revisions"));

            // Observe database time only. No lease updates, trigger bypass, test clock,
            // configurable lease, or synthetic admission evidence is used in this wave.
            var operations = interrupted.Select(item => item.Original!.Facts.OperationId)
                .Append(staleReceipt.Facts.OperationId).ToArray();
            Assert.InRange(await RemainingLeaseSecondsAsync(metadata, [staleReceipt.Facts.OperationId]), 100, 120);
            await WaitForRealExpiryAsync(metadata, operations);

            foreach (var item in interrupted)
            {
                var original = item.Original!;
                await using var recovery = await IngestionProcessProbe.StartAsync(metadata, root, "resume",
                    operationId: original.Facts.OperationId, emptyRegistry: item.Stage == "afterReady");
                var response = await recovery.CompleteAsync();
                Assert.Equal(0, response.ExitCode);
                Assert.Equal(0, response.PublishCalls);
                Assert.Equal(1, response.ReadCalls);
                Assert.Equal(original.Facts.OperationId, response.OperationId);
                Assert.Equal(2, response.ClaimCount);
                if (item.Stage == "beforeObject")
                {
                    Assert.Equal("NeedsSource", response.Outcome);
                    Assert.Equal(0, response.WorkerCalls);
                    var missing = await metadata.Store.GetReceiptAsync(metadata.Actor, metadata.Project, original.Facts.OperationId);
                    Assert.Equal(IngestionFailureCode.NeedsSource, missing!.Attempts[^1].Completion!.Failure);
                    await using var resubmit = await IngestionProcessProbe.StartAsync(metadata, root, "create", item.Key, body: body);
                    var submitted = await resubmit.CompleteAsync();
                    AssertCommitted(submitted, original.Facts.OperationId, 3, workerCalls: 1, publishCalls: 1, readCalls: 1);
                }
                else
                {
                    AssertCommitted(response, original.Facts.OperationId, 2,
                        workerCalls: item.Stage == "afterReady" ? 0 : 1, publishCalls: 0, readCalls: 1);
                }
                var committed = (await metadata.Store.GetReceiptAsync(metadata.Actor, metadata.Project, original.Facts.OperationId))!;
                Assert.Equal(original.Facts, committed.Facts);
                Assert.Equal(IngestionPhase.Committed, committed.Phase);
                Assert.Equal(item.Stage == "afterReady" ? 1L : committed.CurrentGeneration, committed.EligibleAttemptGeneration);
                if (item.Stage == "afterReady")
                {
                    Assert.Equal(IngestionAttemptCompletionKind.ReusedEvidence, committed.Attempts[^1].Completion!.Kind);
                    Assert.Equal(1, committed.Attempts[^1].Completion!.ReusedAttemptGeneration);
                    Assert.Equal(original.Attempts.Single().Completion!.Evidence!.Facts, committed.Attempts[0].Completion!.Evidence!.Facts);
                    Assert.Equal(original.Attempts.Single().Completion!.Evidence!.CopyDocument().GetRawText(),
                        committed.Attempts[0].Completion!.Evidence!.CopyDocument().GetRawText());
                }
                Assert.Equal(body, await physical.ReadVerifiedAsync(committed.Facts.FutureRevision, committed.Facts.NativeObject));
                Assert.Equal(Sha256Digest.FromBytes(body), committed.Facts.NativeObject.Digest);
                Assert.Equal(body.LongLength, committed.Facts.NativeObject.ByteCount);
            }

            var currentDirectory = root.NewDirectory("current-writer-validation");
            await using var current = await IngestionProcessProbe.StartAsync(metadata, root, "resume",
                operationId: staleReceipt.Facts.OperationId, gateDirectory: currentDirectory, gateStage: "afterValidation");
            var currentGate = await current.WaitReadyAsync(currentDirectory, "afterValidation");
            Assert.Equal(1, currentGate.WorkerCalls);
            Assert.Equal(0, currentGate.PublishCalls);
            var fenced = (await metadata.Store.GetReceiptAsync(metadata.Actor, metadata.Project, staleReceipt.Facts.OperationId))!;
            Assert.Equal(2, fenced.ClaimCount);
            Assert.Equal(2, fenced.VerifiedStageGeneration);
            Assert.Null(fenced.EligibleAttemptGeneration);
            Assert.Null(fenced.Attempts[^1].Completion);

            // The obsolete process now returns from its real publish and attempts the
            // ordinary stage call. All stale metadata mutations must retain this snapshot.
            await IngestionProcessProbe.ReleaseAsync(staleDirectory);
            var oldResponse = await stale.CompleteAsync();
            Assert.Equal(0, oldResponse.ExitCode);
            Assert.Equal("ClaimLost", oldResponse.Outcome);
            Assert.Equal(0, oldResponse.WorkerCalls);
            Assert.Equal(IngestionStoreOutcome.ClaimLost,
                (await metadata.Store.MarkStagedAsync(metadata.Actor, oldClaim, staleReceipt.Facts.NativeObject)).Outcome);
            Assert.Equal(IngestionStoreOutcome.ClaimLost,
                (await metadata.Store.CompleteAttemptAsync(metadata.Actor, oldClaim,
                    IngestionAttemptCompletion.FromOperationalFailure(IngestionFailureCode.StorageIo))).Outcome);
            Assert.Equal(IngestionStoreOutcome.ClaimLost, (await metadata.Store.FinalizeAsync(metadata.Actor, oldClaim)).Outcome);
            var afterStale = (await metadata.Store.GetReceiptAsync(metadata.Actor, metadata.Project, staleReceipt.Facts.OperationId))!;
            AssertUnchanged(fenced, afterStale);
            Assert.Equal(0L, await metadata.Case.ScalarAsync<long>(
                "SELECT count(*) FROM braid.mapping_revisions WHERE ingestion_operation_id=$1", staleReceipt.Facts.OperationId));
            Assert.Equal(body, await physical.ReadVerifiedAsync(staleReceipt.Facts.FutureRevision, staleReceipt.Facts.NativeObject));
            await IngestionProcessProbe.ReleaseAsync(currentDirectory);
            AssertCommitted(await current.CompleteAsync(), staleReceipt.Facts.OperationId, 2,
                workerCalls: 1, publishCalls: 0, readCalls: 1);
            var staleCommitted = (await metadata.Store.GetReceiptAsync(metadata.Actor, metadata.Project, staleReceipt.Facts.OperationId))!;
            Assert.Equal(staleReceipt.Facts, staleCommitted.Facts);
            Assert.Equal(2, staleCommitted.CommittedRevision!.InitialAdmissionAttemptGeneration);
            Assert.Equal(5L, await metadata.Case.ScalarAsync<long>("SELECT count(*) FROM braid.mappings"));
            Assert.Equal(5L, await metadata.Case.ScalarAsync<long>("SELECT count(*) FROM braid.mapping_revisions"));
            Assert.Empty(root.AttemptFiles());
        }
        finally
        {
            await Task.WhenAll(interrupted.Select(async item => await item.Probe.DisposeAsync()));
        }
    }

    [PostgresFact]
    public async Task ActualCommitBeforeProcessReplyReplaysOneRevisionWithoutStorageOrWorkerCalls()
    {
        await using var metadata = await PostgresIngestionTestSupport.CreateAsync(fixture);
        using var root = new PrivateStorageTestRoot();
        await using var physical = root.Initialize();
        var body = Body();
        var key = Guid.NewGuid();
        var directory = root.NewDirectory("after-commit");
        await using var original = await IngestionProcessProbe.StartAsync(metadata, root, "create", key, body: body,
            gateDirectory: directory, gateStage: "afterCommit");
        var gate = await original.WaitReadyAsync(directory, "afterCommit");
        Assert.Equal(1, gate.WorkerCalls);
        Assert.Equal(1, gate.PublishCalls);
        Assert.Equal(1, gate.ReadCalls);
        var committed = await ReceiptByKeyAsync(metadata, key);
        Assert.Equal(IngestionPhase.Committed, committed.Phase);
        Assert.Equal(gate.OperationId, committed.Facts.OperationId);
        await original.KillAsync();
        await using var retry = await IngestionProcessProbe.StartAsync(metadata, root, "create", key,
            body: body, emptyRegistry: true);
        AssertCommitted(await retry.CompleteAsync(), committed.Facts.OperationId, 1,
            workerCalls: 0, publishCalls: 0, readCalls: 0);
        var replayed = await ReceiptByKeyAsync(metadata, key);
        AssertUnchanged(committed, replayed);
        Assert.Equal(committed.CommittedRevision, replayed.CommittedRevision);
        Assert.Equal(1L, await metadata.Case.ScalarAsync<long>("SELECT count(*) FROM braid.mappings"));
        Assert.Equal(1L, await metadata.Case.ScalarAsync<long>("SELECT count(*) FROM braid.mapping_revisions"));
        Assert.Equal(body, await physical.ReadVerifiedAsync(committed.Facts.FutureRevision, committed.Facts.NativeObject));
        Assert.Empty(root.AttemptFiles());
    }

    private static byte[] Body() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "FerruleFixtures", "versioned-exact.json"));

    private static async Task<IngestionReceipt> ReceiptByKeyAsync(PostgresIngestionTestSupport metadata, Guid key) =>
        Assert.IsType<IngestionReceipt>(await metadata.Store.FindOwnReceiptAsync(metadata.Actor, metadata.Project,
            IngestionOperationKind.Create, null, key));

    private static void AssertCommitted(IngestionProcessResponse response, Guid operationId, int claimCount,
        int workerCalls, int publishCalls, int readCalls)
    {
        Assert.Equal(0, response.ExitCode);
        Assert.Null(response.Error);
        Assert.Equal("CommittedValid", response.Outcome);
        Assert.Equal("Committed", response.Phase);
        Assert.Equal(operationId, response.OperationId);
        Assert.Equal(claimCount, response.ClaimCount);
        Assert.Equal(workerCalls, response.WorkerCalls);
        Assert.Equal(publishCalls, response.PublishCalls);
        Assert.Equal(readCalls, response.ReadCalls);
    }

    private static void AssertUnchanged(IngestionReceipt expected, IngestionReceipt actual)
    {
        Assert.Equal(expected.Facts, actual.Facts);
        Assert.Equal(expected.Phase, actual.Phase);
        Assert.Equal(expected.ClaimCount, actual.ClaimCount);
        Assert.Equal(expected.CurrentGeneration, actual.CurrentGeneration);
        Assert.Equal(expected.VerifiedStageGeneration, actual.VerifiedStageGeneration);
        Assert.Equal(expected.VerifiedStageAtUtc, actual.VerifiedStageAtUtc);
        Assert.Equal(expected.EligibleAttemptGeneration, actual.EligibleAttemptGeneration);
        Assert.Equal(expected.CompletedAtUtc, actual.CompletedAtUtc);
        Assert.Equal(expected.Attempts.Count, actual.Attempts.Count);
        for (var index = 0; index < expected.Attempts.Count; index++)
        {
            Assert.Equal(expected.Attempts[index].Generation, actual.Attempts[index].Generation);
            Assert.Equal(expected.Attempts[index].StartedAtUtc, actual.Attempts[index].StartedAtUtc);
            Assert.Equal(expected.Attempts[index].FinishedAtUtc, actual.Attempts[index].FinishedAtUtc);
            Assert.Equal(expected.Attempts[index].Classification, actual.Attempts[index].Classification);
            Assert.Equal(expected.Attempts[index].Completion?.Kind, actual.Attempts[index].Completion?.Kind);
            Assert.Equal(expected.Attempts[index].Completion?.Failure, actual.Attempts[index].Completion?.Failure);
            Assert.Equal(expected.Attempts[index].Completion?.ReusedAttemptGeneration, actual.Attempts[index].Completion?.ReusedAttemptGeneration);
            Assert.Equal(expected.Attempts[index].Completion?.Evidence?.CopyDocument().GetRawText(),
                actual.Attempts[index].Completion?.Evidence?.CopyDocument().GetRawText());
        }
    }

    private static async Task<IngestionClaim> ReadOwnedClaimAsync(PostgresIngestionTestSupport metadata, IngestionReceipt receipt)
    {
        await using var connection = await metadata.Case.Owner.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT lease_owner_id, lease_token, lease_generation, lease_expires_at
            FROM braid.revision_ingestions WHERE organization_id=$1 AND project_id=$2 AND operation_id=$3
            """, connection);
        command.Parameters.AddWithValue(metadata.Project.OrganizationId);
        command.Parameters.AddWithValue(metadata.Project.ProjectId);
        command.Parameters.AddWithValue(receipt.Facts.OperationId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var claim = new IngestionClaim(metadata.Project, receipt.Facts.OperationId, metadata.Actor.PrincipalId,
            reader.GetGuid(0), reader.GetGuid(1), reader.GetInt64(2), reader.GetFieldValue<DateTimeOffset>(3));
        Assert.False(await reader.ReadAsync());
        return claim;
    }

    private static Task<double> RemainingLeaseSecondsAsync(PostgresIngestionTestSupport metadata, Guid[] operationIds) =>
        metadata.Case.ScalarAsync<double>("""
            SELECT coalesce(max(greatest(extract(epoch FROM lease_expires_at-clock_timestamp()),0)),0)::double precision
            FROM braid.revision_ingestions WHERE operation_id=ANY($1)
            """, operationIds);

    private static async Task WaitForRealExpiryAsync(PostgresIngestionTestSupport metadata, Guid[] operationIds)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(135));
        while (await RemainingLeaseSecondsAsync(metadata, operationIds) > 0)
            await Task.Delay(TimeSpan.FromSeconds(1), deadline.Token);
        Assert.Equal(0, await RemainingLeaseSecondsAsync(metadata, operationIds));
    }

    private sealed record InterruptedProcess(string Stage, Guid Key, string Directory,
        IngestionProcessProbe Probe, IngestionReceipt? Original = null);
}

[SupportedOSPlatform("linux")]
internal sealed class IngestionProcessProbe : IAsyncDisposable
{
    private readonly Process process;
    private readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(250));
    private readonly Task<string> output;
    private readonly Task<string> error;
    private readonly string[] privateValues;
    private readonly string workerStateDirectory;
    private readonly string dockerExecutable;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private IngestionProcessProbe(Process process, string workerStateDirectory, string dockerExecutable,
        params string[] privateValues)
    {
        this.process = process;
        this.privateValues = privateValues.Where(value => value.Length != 0).ToArray();
        this.workerStateDirectory = workerStateDirectory;
        this.dockerExecutable = dockerExecutable;
        output = CaptureAsync(process.StandardOutput, 32 * 1024, deadline.Token);
        error = CaptureAsync(process.StandardError, 32 * 1024, deadline.Token);
    }

    internal static async Task<IngestionProcessProbe> StartAsync(PostgresIngestionTestSupport metadata,
        PrivateStorageTestRoot root, string operation, Guid key = default, Guid operationId = default,
        byte[]? body = null, string? gateDirectory = null, string? gateStage = null, bool emptyRegistry = false)
    {
        var dll = Environment.GetEnvironmentVariable("BRAID_INGESTION_PROBE_PATH")
            ?? Path.Combine(AppContext.BaseDirectory, "IngestionProbe", "Braid.IngestionProbe.dll");
        if (!File.Exists(dll))
            throw new InvalidOperationException("The test-only ingestion probe and dependencies must be copied to the test output.");
        var image = Environment.GetEnvironmentVariable("BRAID_FERRULE_WORKER_IMAGE");
        if (string.IsNullOrWhiteSpace(image))
            throw new InvalidOperationException("Actual ingestion process qualification requires the qualified immutable Ferrule image.");
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrWhiteSpace(host))
        {
            var current = Environment.ProcessPath;
            host = current is not null && Path.GetFileName(current) == "dotnet" ? current : "dotnet";
        }
        var start = new ProcessStartInfo(host)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(dll);
        var workerState = root.NewDirectory($"probe-workers-{Guid.NewGuid():N}");
        var docker = Environment.GetEnvironmentVariable("BRAID_DOCKER_EXECUTABLE") ?? "/usr/bin/docker";
        var process = Process.Start(start) ?? throw new InvalidOperationException("The owned ingestion probe did not start.");
        var probe = new IngestionProcessProbe(process, workerState, docker, root.BasePath, root.RootPath,
            metadata.Case.IngestionConnectionString, metadata.Case.RuntimeConnectionString,
            new NpgsqlConnectionStringBuilder(metadata.Case.IngestionConnectionString).Password ?? string.Empty,
            new NpgsqlConnectionStringBuilder(metadata.Case.RuntimeConnectionString).Password ?? string.Empty);
        try
        {
            var command = new
            {
                Operation = operation,
                IngestionConnectionString = metadata.Case.IngestionConnectionString,
                ReaderConnectionString = metadata.Case.RuntimeConnectionString,
                RootPath = root.RootPath,
                ApplicationContentRoot = root.ApplicationRoot,
                WorkerImage = image,
                WorkerStateDirectory = workerState,
                DockerExecutable = docker,
                OrganizationId = metadata.Project.OrganizationId,
                ProjectId = metadata.Project.ProjectId,
                ActorId = metadata.Actor.PrincipalId.Value,
                IdempotencyKey = key,
                OperationId = operationId,
                Base64Content = body is null ? null : Convert.ToBase64String(body),
                GateDirectory = gateDirectory,
                GateStage = gateStage,
                EmptyRegistry = emptyRegistry
            };
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command, JsonOptions).AsMemory(), probe.deadline.Token);
            process.StandardInput.Close();
            return probe;
        }
        catch { await probe.DisposeAsync(); throw; }
    }

    internal async Task<IngestionProcessGate> WaitReadyAsync(string directory, string stage)
    {
        using var gateDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var readyPath = Path.Combine(directory, "ready");
        while (!File.Exists(readyPath))
        {
            if (process.HasExited)
            {
                var response = await CompleteAsync();
                throw new InvalidOperationException($"The owned ingestion probe exited before its gate ({response.Outcome}/{response.Error}).");
            }
            await Task.Delay(20, gateDeadline.Token);
        }
        var ready = JsonSerializer.Deserialize<IngestionProcessGate>(await File.ReadAllTextAsync(readyPath, gateDeadline.Token), JsonOptions)
            ?? throw new InvalidOperationException("The owned ingestion probe gate was invalid.");
        Assert.Equal(stage, ready.Stage);
        Assert.NotEqual(Guid.Empty, ready.OperationId);
        Assert.False(process.HasExited);
        return ready;
    }

    internal static Task ReleaseAsync(string directory) => File.WriteAllTextAsync(Path.Combine(directory, "release"), "release");

    internal async Task<IngestionProcessResponse> CompleteAsync()
    {
        await Task.WhenAll(process.WaitForExitAsync(deadline.Token), output, error);
        var stdout = await output;
        var stderr = await error;
        foreach (var value in privateValues)
        {
            Assert.DoesNotContain(value, stdout, StringComparison.Ordinal);
            Assert.DoesNotContain(value, stderr, StringComparison.Ordinal);
        }
        Assert.Empty(stderr);
        await EnsureOwnedWorkersRemovedAsync(remove: false);
        var response = JsonSerializer.Deserialize<IngestionProcessResponse>(stdout, JsonOptions)
            ?? throw new InvalidOperationException("The owned ingestion probe returned an invalid response.");
        return response with { ExitCode = process.ExitCode };
    }

    internal async Task KillAsync()
    {
        Assert.False(process.HasExited);
        // Gates are either before worker entry or after actual runner cleanup, so
        // process-tree termination cannot orphan an active worker container.
        process.Kill(entireProcessTree: true);
        using var reap = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await process.WaitForExitAsync(reap.Token);
        Assert.NotEqual(0, process.ExitCode);
        await EnsureOwnedWorkersRemovedAsync(remove: false);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var reap = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(reap.Token);
            }
            try { await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { await EnsureOwnedWorkersRemovedAsync(remove: true); }
        }
        finally { deadline.Dispose(); process.Dispose(); }
    }

    private async Task EnsureOwnedWorkersRemovedAsync(bool remove)
    {
        foreach (var record in Directory.EnumerateFiles(workerStateDirectory))
        {
            var name = Path.GetFileName(record);
            if (!Braid.WorkerProbeSupport.InspectionWorkerName.IsExact(name) || await File.ReadAllTextAsync(record) != "owned")
                throw new InvalidOperationException("The owned ingestion worker record was invalid.");
            // This probe's legacy "owned" record contains only a random name, not the
            // runner's independent operation marker or an acknowledged immutable ID.
            // It therefore authorizes read-only absence observation, never removal.
            // An orphan must remain a failed gate for the attributed qualification owner.
            var inspected = await OwnedWorkerCommandAsync("inspect", "--format", "{{json .}}", name);
            if (inspected.ExitCode != 1 || !string.IsNullOrWhiteSpace(inspected.Output) ||
                !IsExactMissingContainer(inspected.Error, name))
                throw new InvalidOperationException(remove
                    ? "Worker cleanup was refused: the name-only probe record cannot establish operation ownership."
                    : "The owned ingestion worker absence was not confirmed.");
        }
    }

    private static bool IsExactMissingContainer(string error, string name)
    {
        var message = error.Trim();
        return message == $"Error response from daemon: No such container: {name}" ||
            message == $"Error: No such container: {name}" || message == $"Error: No such object: {name}";
    }

    private async Task<(int ExitCode, string Output, string Error)> OwnedWorkerCommandAsync(params string[] arguments)
    {
        var config = Path.Combine(workerStateDirectory, "docker-config");
        Directory.CreateDirectory(config);
        File.SetUnixFileMode(config, PrivateStorageTestRoot.DirectoryMode);
        using var commandDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var child = new Process
        {
            StartInfo = new ProcessStartInfo(dockerExecutable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = config
            }
        };
        child.StartInfo.Environment.Clear();
        child.StartInfo.Environment["DOCKER_CONFIG"] = config;
        child.StartInfo.Environment["LC_ALL"] = "C.UTF-8";
        foreach (var argument in new[] { "--config", config, "--host", "unix:///var/run/docker.sock", "container" }.Concat(arguments))
            child.StartInfo.ArgumentList.Add(argument);
        if (!child.Start())
            throw new InvalidOperationException("The owned ingestion worker cleanup did not start.");
        var stdout = CaptureAsync(child.StandardOutput, 64 * 1024, commandDeadline.Token);
        var stderr = CaptureAsync(child.StandardError, 64 * 1024, commandDeadline.Token);
        try
        {
            await Task.WhenAll(child.WaitForExitAsync(commandDeadline.Token), stdout, stderr);
            return (child.ExitCode, await stdout, await stderr);
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill();
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1));
            }
        }
    }

    private static async Task<string> CaptureAsync(StreamReader reader, int ceiling, CancellationToken cancellationToken)
    {
        var content = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) != 0)
        {
            if (content.Length + count > ceiling)
                throw new InvalidOperationException("The owned ingestion probe exceeded its output bound.");
            content.Append(buffer, 0, count);
        }
        return content.ToString();
    }
}

internal sealed record IngestionProcessGate(string Stage, Guid OperationId, int WorkerCalls, int PublishCalls, int ReadCalls);
internal sealed record IngestionProcessResponse(string Outcome, string? Phase, Guid? OperationId,
    int? ClaimCount, int WorkerCalls, int PublishCalls, int ReadCalls, string? Error, int ExitCode = 0);
