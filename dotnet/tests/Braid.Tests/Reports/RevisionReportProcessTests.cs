using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using Braid.Application;
using Braid.Domain;

namespace Braid.Tests;

/// <summary>Actual process loss over a committed C2 source, PostgreSQL, private objects and the pinned worker.</summary>
[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class RevisionReportProcessTests(PostgresFixture fixture)
{
    [RevisionIngestionFact]
    public async Task DeathAfterVerifiedPublicationLeavesAnOrphanAndRealLeaseExpiryAllocatesAnotherObject()
    {
        await using var test = await RevisionReportTestSupport.CreateAsync(fixture);
        var key = test.Key(RevisionReportOperation.Projection);
        var sourceBefore = await SourceFactsAsync(test);
        var nativeBefore = await test.PhysicalStore.ReadVerifiedAsync(test.Revision, key.Source.NativeObject);
        var directory = test.PhysicalRoot.NewDirectory("report-before-finish");
        await using var interrupted = await RevisionReportProcessProbe.StartAsync(test, "ensure", gateDirectory: directory,
            gateStage: "beforeFinish");
        var ready = await interrupted.WaitReadyAsync(directory, "beforeFinish");
        AssertSettledPublication(ready, key, metadataWrites: 1);
        var pending = Assert.IsType<RevisionReportReadSnapshot>(await test.Reader.GetCurrentAsync(test.Actor, key));
        Assert.Equal(RevisionReportState.Pending, pending.State);
        Assert.True(pending.IsClaimLive);
        Assert.Null(pending.SelectedResult);
        Assert.Equal(ready.CacheEntryId, pending.CacheEntryId);
        Assert.Equal(ready.DerivationId, pending.CurrentAttempt!.DerivationId);
        Assert.Equal(ready.Generation, pending.Generation);
        Assert.Equal(ready.StartedAtUtc, pending.CurrentAttempt.StartedAtUtc);
        Assert.Equal(ready.LeaseExpiresAtUtc, pending.CurrentAttempt.LeaseExpiresAtUtc);
        Assert.Null(pending.CurrentAttempt.Completion);
        Assert.Equal(ready.DerivedObjectId, await test.Case.ScalarAsync<Guid>(
            "SELECT candidate_object_id FROM braid.revision_derivation_attempts WHERE derivation_id=$1", ready.DerivationId));
        var orphan = Candidate(key.Scope, ready);
        var orphanBytes = await test.PhysicalStore.ReadVerifiedAsync(test.Revision, orphan);
        Assert.Equal(orphan.Digest, Sha256Digest.FromBytes(orphanBytes));
        Assert.Equal(orphan.ByteCount, orphanBytes.LongLength);
        var beforeDeath = await ReportFactsAsync(test);
        await interrupted.KillAtSettledCheckpointAsync();
        Assert.Equal(beforeDeath, await ReportFactsAsync(test));

        await using (var readOnly = await RevisionReportProcessProbe.StartAsync(test, "current"))
        {
            var observed = await readOnly.CompleteAsync();
            Assert.Equal(0, observed.ExitCode);
            Assert.Null(observed.Error);
            Assert.Equal("Observed", observed.Outcome);
            Assert.Equal("Pending", observed.State);
            Assert.False(observed.HasVerifiedReport);
            AssertReadOnly(observed, derivedReads: 0);
        }

        // This observes the unchanged original sixty-second lease against fresh
        // production DB-clock snapshots. No owner backdating or synthetic clock is used.
        var expired = await WaitForRealDatabaseExpiryAsync(test, key, pending);
        Assert.False(expired.IsClaimLive);
        Assert.Equal(pending.CurrentAttempt, expired.CurrentAttempt);
        Assert.Equal(beforeDeath, await ReportFactsAsync(test));

        await using (var retry = await RevisionReportProcessProbe.StartAsync(test, "ensure"))
        {
            var completed = await retry.CompleteAsync();
            Assert.Equal(0, completed.ExitCode);
            Assert.Null(completed.Error);
            Assert.Equal("Observed", completed.Outcome);
            Assert.Equal("Available", completed.State);
            Assert.True(completed.HasVerifiedReport);
            Assert.Equal(1, completed.WorkerCalls);
            Assert.Equal(1, completed.WorkerCompletions);
            Assert.Equal("None", completed.LastWorkerFailure);
            Assert.Equal(1, completed.RunnerResolutions);
            Assert.Equal(1, completed.BackendResolutions);
            Assert.Equal(1, completed.NativeReads);
            // Candidate readback plus the exact committed object's final verified read.
            Assert.Equal(2, completed.DerivedReads);
            Assert.Equal(1, completed.PublishCalls);
            Assert.Equal(1, completed.CreatedPublications);
            Assert.Equal(2, completed.MetadataWrites);
            Assert.Equal(key.Fingerprint.Value, completed.KeyFingerprint);
            Assert.Equal(ready.CacheEntryId, completed.CacheEntryId);
            Assert.Equal(2L, completed.Generation);
            Assert.NotEqual(ready.DerivationId, completed.DerivationId);
            Assert.NotEqual(ready.DerivedObjectId, completed.DerivedObjectId);
        }

        var available = Assert.IsType<RevisionReportReadSnapshot>(await test.Reader.GetCurrentAsync(test.Actor, key));
        Assert.Equal(RevisionReportState.Available, available.State);
        Assert.Equal(key, available.Key);
        Assert.Equal(ready.CacheEntryId, available.CacheEntryId);
        Assert.Equal(2L, available.Generation);
        Assert.NotEqual(orphan.Id, available.SelectedResult!.DerivedObject.Id);
        var abandoned = Assert.IsType<AuthorizedRevisionReportAttempt>(await test.Reader.GetRetainedAsync(test.Actor,
            test.Revision, RevisionReportOperation.Projection, ready.DerivationId));
        Assert.Equal(pending.CurrentAttempt, abandoned.Attempt);
        Assert.Null(abandoned.Result);
        Assert.Null(abandoned.Attempt.Completion);
        Assert.Equal(2L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.revision_derivation_attempts"));
        Assert.Equal(orphanBytes, await test.PhysicalStore.ReadVerifiedAsync(test.Revision, orphan));
        Assert.Equal(nativeBefore, await test.PhysicalStore.ReadVerifiedAsync(test.Revision, key.Source.NativeObject));
        Assert.Equal(sourceBefore, await SourceFactsAsync(test));
        Assert.Empty(test.PhysicalRoot.AttemptFiles());
    }

    [RevisionIngestionFact]
    public async Task DeathAfterAcknowledgedFinishRecoversExactReportInReadOnlyProcessesWithoutTheNativeBlob()
    {
        await using var test = await RevisionReportTestSupport.CreateAsync(fixture);
        var key = test.Key(RevisionReportOperation.Projection);
        var sourceBefore = await SourceFactsAsync(test);
        var nativeBefore = await test.PhysicalStore.ReadVerifiedAsync(test.Revision, key.Source.NativeObject);
        var directory = test.PhysicalRoot.NewDirectory("report-after-finish");
        await using var interrupted = await RevisionReportProcessProbe.StartAsync(test, "ensure", gateDirectory: directory,
            gateStage: "afterFinish");
        var ready = await interrupted.WaitReadyAsync(directory, "afterFinish");
        AssertSettledPublication(ready, key, metadataWrites: 2);
        var current = Assert.IsType<RevisionReportReadSnapshot>(await test.Reader.GetCurrentAsync(test.Actor, key));
        Assert.Equal(RevisionReportState.Available, current.State);
        var result = Assert.IsType<RevisionReportResult>(current.SelectedResult);
        Assert.Equal(key, result.Key);
        Assert.Equal(ready.DerivationId, result.DerivationId);
        Assert.Equal(ready.CacheEntryId, result.CacheEntryId);
        Assert.Equal(ready.Generation, result.Generation);
        Assert.Equal(ready.StartedAtUtc, result.StartedAtUtc);
        Assert.Equal(Candidate(key.Scope, ready), result.DerivedObject);
        Assert.Equal(RevisionReportBackendStatus.Succeeded, result.Observation.Status);
        Assert.True(result.Observation.Provenance!.Confirmed);
        var retainedBefore = Assert.IsType<AuthorizedRevisionReportAttempt>(await test.Reader.GetRetainedAsync(test.Actor,
            test.Revision, RevisionReportOperation.Projection, ready.DerivationId));
        var storedBefore = await test.PhysicalStore.ReadVerifiedAsync(test.Revision, result.DerivedObject);
        var metadataBefore = await ReportFactsAsync(test);
        await interrupted.KillAtSettledCheckpointAsync();
        Assert.Equal(metadataBefore, await ReportFactsAsync(test));

        var nativePath = test.PhysicalRoot.FinalPath(key.Source.NativeObject);
        var unavailablePath = test.PhysicalRoot.NewPath("unavailable-owned-native.blob");
        File.Move(nativePath, unavailablePath, overwrite: false);
        try
        {
            Assert.False(File.Exists(nativePath));
            foreach (var mode in new[] { "retained", "current" })
            {
                await using var reopened = await RevisionReportProcessProbe.StartAsync(test, mode,
                    derivationId: mode == "retained" ? result.DerivationId : Guid.Empty);
                var recovered = await reopened.CompleteAsync();
                Assert.Equal(0, recovered.ExitCode);
                Assert.Null(recovered.Error);
                Assert.Equal("Observed", recovered.Outcome);
                Assert.Equal("Available", recovered.State);
                Assert.True(recovered.HasVerifiedReport);
                Assert.Equal(result.DerivationId, recovered.DerivationId);
                Assert.Equal(result.CacheEntryId, recovered.CacheEntryId);
                Assert.Equal(result.Generation, recovered.Generation);
                Assert.Equal(result.Key.Fingerprint.Value, recovered.KeyFingerprint);
                Assert.Equal(result.DerivedObject.Id.ObjectId, recovered.DerivedObjectId);
                Assert.Equal(result.DerivedObject.Digest.Value, recovered.DerivedDigest);
                Assert.Equal(result.DerivedObject.ByteCount, recovered.DerivedByteCount);
                Assert.Equal(result.Observation.Status.ToString(), recovered.BackendStatus);
                Assert.True(recovered.ProvenanceConfirmed);
                AssertReadOnly(recovered, derivedReads: 1);
            }
            Assert.Equal(metadataBefore, await ReportFactsAsync(test));
        }
        finally { File.Move(unavailablePath, nativePath, overwrite: false); }

        var retainedAfter = Assert.IsType<AuthorizedRevisionReportAttempt>(await test.Reader.GetRetainedAsync(test.Actor,
            test.Revision, RevisionReportOperation.Projection, result.DerivationId));
        Assert.Equal(retainedBefore.Key, retainedAfter.Key);
        Assert.Equal(retainedBefore.CacheEntryId, retainedAfter.CacheEntryId);
        Assert.Equal(retainedBefore.Attempt, retainedAfter.Attempt);
        Assert.Equal(retainedBefore.Result, retainedAfter.Result);
        Assert.Equal(storedBefore, await test.PhysicalStore.ReadVerifiedAsync(test.Revision, result.DerivedObject));
        Assert.Equal(nativeBefore, await test.PhysicalStore.ReadVerifiedAsync(test.Revision, key.Source.NativeObject));
        Assert.Equal(sourceBefore, await SourceFactsAsync(test));
        Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.revision_derivation_attempts"));
        Assert.Empty(test.PhysicalRoot.AttemptFiles());
    }

    private static void AssertSettledPublication(RevisionReportProcessCheckpoint ready, RevisionReportKey key, int metadataWrites)
    {
        Assert.Equal(key.Fingerprint.Value, ready.KeyFingerprint);
        Assert.Equal(1L, ready.Generation);
        Assert.Equal(ready.StartedAtUtc.AddSeconds(60), ready.LeaseExpiresAtUtc);
        Assert.Equal("Succeeded", ready.BackendStatus);
        Assert.True(ready.ProvenanceConfirmed);
        Assert.Equal(1, ready.WorkerCalls);
        Assert.Equal(1, ready.WorkerCompletions);
        Assert.Equal(1, ready.NativeReads);
        Assert.Equal(1, ready.DerivedReads);
        Assert.Equal(1, ready.PublishCalls);
        Assert.Equal(1, ready.CreatedPublications);
        Assert.Equal(metadataWrites, ready.MetadataWrites);
    }

    private static void AssertReadOnly(RevisionReportProcessResponse response, int derivedReads)
    {
        Assert.Equal(0, response.WorkerCalls);
        Assert.Equal(0, response.WorkerCompletions);
        Assert.Equal(0, response.RunnerResolutions);
        Assert.Equal(0, response.BackendResolutions);
        Assert.Null(response.LastWorkerFailure);
        Assert.Equal(0, response.NativeReads);
        Assert.Equal(derivedReads, response.DerivedReads);
        Assert.Equal(0, response.PublishCalls);
        Assert.Equal(0, response.CreatedPublications);
        Assert.Equal(0, response.MetadataWrites);
    }

    private static PrivateObjectReference Candidate(RevisionScope scope, RevisionReportProcessCheckpoint ready) => new(
        PrivateObjectId.Restore(scope, ready.DerivedObjectId, PrivateObjectClass.DerivedArtifact),
        Sha256Digest.Parse(ready.DerivedDigest), ready.DerivedByteCount);

    private static Task<string> SourceFactsAsync(RevisionReportTestSupport test) => test.SourceSnapshotAsync();

    private static Task<string> ReportFactsAsync(RevisionReportTestSupport test) => test.Case.ScalarAsync<string>("""
        SELECT jsonb_build_object(
            'entries',(SELECT jsonb_agg(to_jsonb(d) ORDER BY d.cache_entry_id) FROM braid.revision_derivations d),
            'attempts',(SELECT jsonb_agg(to_jsonb(a) ORDER BY a.derivation_id)
                FROM braid.revision_derivation_attempts a))::text
        """);

    private static async Task<RevisionReportReadSnapshot> WaitForRealDatabaseExpiryAsync(RevisionReportTestSupport test,
        RevisionReportKey key, RevisionReportReadSnapshot original)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(75));
        while (true)
        {
            var observed = Assert.IsType<RevisionReportReadSnapshot>(
                await test.Reader.GetCurrentAsync(test.Actor, key, deadline.Token));
            Assert.Equal(original.CurrentAttempt, observed.CurrentAttempt);
            Assert.Null(observed.SelectedResult);
            if (!observed.IsClaimLive)
            {
                Assert.True(observed.Source.ObservedAtUtc >= original.CurrentAttempt!.LeaseExpiresAtUtc);
                return observed;
            }
            await Task.Delay(TimeSpan.FromSeconds(1), deadline.Token);
        }
    }
}

[SupportedOSPlatform("linux")]
internal sealed class RevisionReportProcessProbe : IAsyncDisposable
{
    private readonly Process process;
    private readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(100));
    private readonly Task<string> output;
    private readonly Task<string> error;
    private readonly string[] privateValues;
    private readonly string? workerStateDirectory;
    private readonly string dockerExecutable;
    private bool settledCheckpoint;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private RevisionReportProcessProbe(Process process, string? workerStateDirectory, string dockerExecutable,
        params string[] privateValues)
    {
        this.process = process;
        this.workerStateDirectory = workerStateDirectory;
        this.dockerExecutable = dockerExecutable;
        this.privateValues = privateValues.Where(value => value.Length != 0).ToArray();
        output = CaptureAsync(process.StandardOutput, 16 * 1024, deadline.Token);
        error = CaptureAsync(process.StandardError, 16 * 1024, deadline.Token);
    }

    internal static async Task<RevisionReportProcessProbe> StartAsync(RevisionReportTestSupport test, string mode,
        Guid derivationId = default, string? gateDirectory = null, string? gateStage = null)
    {
        var dll = Environment.GetEnvironmentVariable("BRAID_REPORT_WORKFLOW_PROBE_PATH")
            ?? Path.Combine(AppContext.BaseDirectory, "RevisionReportWorkflowProbe", "Braid.RevisionReportWorkflowProbe.dll");
        if (!File.Exists(dll))
            throw new InvalidOperationException("The workflow probe ordinary runtime closure must be copied to the test output.");
        var image = Environment.GetEnvironmentVariable("BRAID_FERRULE_WORKER_IMAGE");
        if (string.IsNullOrWhiteSpace(image))
            throw new InvalidOperationException("The actual process test requires the configured immutable worker image.");
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
        // No inherited application configuration or deriver login enters the read-only process.
        start.Environment.Clear();
        foreach (var name in new[] { "DOTNET_ROOT", "DOTNET_ROOT_X64", "PATH" })
            if (Environment.GetEnvironmentVariable(name) is { } value)
                start.Environment[name] = value;
        start.Environment["LC_ALL"] = "C.UTF-8";
        start.Environment["BRAID_REPORT_WORKFLOW_READER_CONNECTION_STRING"] = test.Case.ReportConnectionString;
        if (mode == "ensure")
            start.Environment["BRAID_REPORT_WORKFLOW_DERIVER_CONNECTION_STRING"] = test.Case.DerivationConnectionString;
        var workerState = mode == "ensure" ? test.PhysicalRoot.NewDirectory($"report-probe-workers-{Guid.NewGuid():N}") : null;
        var docker = Environment.GetEnvironmentVariable("BRAID_DOCKER_EXECUTABLE") ?? "/usr/bin/docker";
        var process = Process.Start(start) ?? throw new InvalidOperationException("The owned workflow process did not start.");
        var probe = new RevisionReportProcessProbe(process, workerState, docker, test.PhysicalRoot.BasePath,
            test.PhysicalRoot.RootPath, test.Case.ReportConnectionString, test.Case.DerivationConnectionString,
            new NpgsqlConnectionStringBuilder(test.Case.ReportConnectionString).Password ?? string.Empty,
            new NpgsqlConnectionStringBuilder(test.Case.DerivationConnectionString).Password ?? string.Empty);
        try
        {
            var command = new
            {
                Mode = mode,
                Operation = "projection",
                RootPath = test.PhysicalRoot.RootPath,
                ApplicationContentRoot = test.PhysicalRoot.ApplicationRoot,
                WorkerImage = image,
                test.Revision.OrganizationId,
                test.Revision.ProjectId,
                test.Revision.MappingId,
                test.Revision.RevisionId,
                ActorId = test.Actor.PrincipalId.Value,
                DerivationId = derivationId,
                GateDirectory = gateDirectory,
                GateStage = gateStage,
                WorkerStateDirectory = workerState,
                DockerExecutable = docker
            };
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command, JsonOptions).AsMemory(), probe.deadline.Token);
            process.StandardInput.Close();
            return probe;
        }
        catch { await probe.DisposeAsync(); throw; }
    }

    internal async Task<RevisionReportProcessCheckpoint> WaitReadyAsync(string directory, string stage)
    {
        using var gateDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var path = Path.Combine(directory, "ready");
        while (!File.Exists(path))
        {
            if (process.HasExited)
            {
                var response = await CompleteAsync();
                throw new InvalidOperationException($"The workflow probe exited before its checkpoint ({response.Outcome}/{response.Error}).");
            }
            await Task.Delay(20, gateDeadline.Token);
        }
        var bytes = await File.ReadAllBytesAsync(path, gateDeadline.Token);
        Assert.InRange(bytes.Length, 1, 16 * 1024);
        var ready = JsonSerializer.Deserialize<RevisionReportProcessCheckpoint>(bytes, JsonOptions)
            ?? throw new InvalidOperationException("The owned workflow checkpoint was invalid.");
        Assert.Equal(stage, ready.Stage);
        Assert.NotEqual(Guid.Empty, ready.DerivationId);
        Assert.NotEqual(Guid.Empty, ready.CacheEntryId);
        Assert.NotEqual(Guid.Empty, ready.DerivedObjectId);
        Assert.False(process.HasExited);
        Assert.Equal(1, ready.WorkerCalls);
        Assert.Equal(1, ready.WorkerCompletions);
        await AssertOwnedWorkersAbsentAsync(expectedCount: 1);
        settledCheckpoint = true;
        return ready;
    }

    internal async Task<RevisionReportProcessResponse> CompleteAsync()
    {
        await Task.WhenAll(process.WaitForExitAsync(deadline.Token), output, error);
        var stdout = await output;
        var stderr = await error;
        AssertNoPrivateText(stdout, stderr);
        Assert.Empty(stderr);
        var response = JsonSerializer.Deserialize<RevisionReportProcessResponse>(stdout, JsonOptions)
            ?? throw new InvalidOperationException("The workflow probe response was invalid.");
        await AssertOwnedWorkersAbsentAsync(expectedCount: response.WorkerCalls);
        return response with { ExitCode = process.ExitCode };
    }

    internal async Task KillAtSettledCheckpointAsync()
    {
        Assert.True(settledCheckpoint);
        Assert.False(process.HasExited);
        // Absence is independently observed before termination, not inferred from a counter.
        await AssertOwnedWorkersAbsentAsync(expectedCount: 1);
        process.Kill(entireProcessTree: true);
        using var reap = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Task.WhenAll(process.WaitForExitAsync(reap.Token), output, error);
        Assert.NotEqual(0, process.ExitCode);
        AssertNoPrivateText(await output, await error);
        Assert.Empty(await output);
        Assert.Empty(await error);
        await AssertOwnedWorkersAbsentAsync(expectedCount: 1);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!process.HasExited)
            {
                if (settledCheckpoint)
                    await KillAtSettledCheckpointAsync();
                else
                    // The request's fixed30s plus separate5s cleanup settle a failed
                    // checkpoint first. Do not terminate an unobserved live worker.
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
            }
            await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(5));
            AssertNoPrivateText(await output, await error);
            await AssertOwnedWorkersAbsentAsync();
        }
        finally { deadline.Dispose(); process.Dispose(); }
    }

    private void AssertNoPrivateText(string stdout, string stderr)
    {
        foreach (var value in privateValues)
        {
            Assert.DoesNotContain(value, stdout, StringComparison.Ordinal);
            Assert.DoesNotContain(value, stderr, StringComparison.Ordinal);
        }
    }

    private async Task AssertOwnedWorkersAbsentAsync(int? expectedCount = null)
    {
        if (workerStateDirectory is null)
            return;
        var records = Directory.EnumerateFiles(workerStateDirectory).ToArray();
        if (expectedCount is { } count)
            Assert.Equal(count, records.Length);
        foreach (var record in records)
        {
            var name = Path.GetFileName(record);
            if (name.Length != 45 || !name.StartsWith("braid-inspect-", StringComparison.Ordinal) ||
                name.AsSpan(13).ContainsAnyExcept("0123456789abcdef") || await File.ReadAllTextAsync(record) != "owned")
                throw new InvalidOperationException("The owned workflow worker record was invalid.");
            var inspected = await InspectOwnedWorkerAsync(name);
            var message = inspected.Error.Trim();
            if (inspected.ExitCode != 1 || !string.IsNullOrWhiteSpace(inspected.Output) ||
                message != $"Error response from daemon: No such container: {name}" &&
                message != $"Error: No such container: {name}" && message != $"Error: No such object: {name}")
                throw new InvalidOperationException("The actual workflow worker absence was not confirmed.");
        }
    }

    private async Task<(int ExitCode, string Output, string Error)> InspectOwnedWorkerAsync(string name)
    {
        var config = Path.Combine(workerStateDirectory!, "docker-config");
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
        foreach (var argument in new[] { "--config", config, "--host", "unix:///var/run/docker.sock", "container",
            "inspect", "--format", "{{json .}}", name })
            child.StartInfo.ArgumentList.Add(argument);
        if (!child.Start())
            throw new InvalidOperationException("The owned workflow worker inspection did not start.");
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
                throw new InvalidOperationException("The owned workflow probe exceeded its output bound.");
            content.Append(buffer, 0, count);
        }
        return content.ToString();
    }
}

internal sealed record RevisionReportProcessCheckpoint(string Stage, Guid DerivationId, Guid CacheEntryId, long Generation,
    DateTimeOffset StartedAtUtc, DateTimeOffset LeaseExpiresAtUtc, string KeyFingerprint, Guid DerivedObjectId,
    string DerivedDigest, long DerivedByteCount, string BackendStatus, bool ProvenanceConfirmed,
    int WorkerCalls, int WorkerCompletions, int NativeReads, int DerivedReads, int PublishCalls,
    int CreatedPublications, int MetadataWrites);

internal sealed record RevisionReportProcessResponse(string Outcome, string? State = null, bool HasVerifiedReport = false,
    Guid? DerivationId = null, Guid? CacheEntryId = null, long? Generation = null, string? KeyFingerprint = null,
    Guid? DerivedObjectId = null, string? DerivedDigest = null, long? DerivedByteCount = null,
    string? BackendStatus = null, bool? ProvenanceConfirmed = null, int WorkerCalls = 0, int WorkerCompletions = 0,
    int RunnerResolutions = 0, int BackendResolutions = 0, int NativeReads = 0, int DerivedReads = 0, int PublishCalls = 0,
    int CreatedPublications = 0, int MetadataWrites = 0, string? LastWorkerFailure = null, string? Error = null,
    int ExitCode = 0);
