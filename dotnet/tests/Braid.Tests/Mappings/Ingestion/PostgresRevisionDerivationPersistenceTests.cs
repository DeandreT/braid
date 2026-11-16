using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using Braid.Application;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

/// <summary>Actual pool/cluster/process metadata recovery; usable publication facts are explicitly synthetic.</summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresRevisionDerivationPersistenceTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task FreshDataSourcesAndSameOwnedClusterRestartPreserveUsableFailedAndOriginalNativeMetadata()
    {
        await using var test = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
        var source = await test.ImmutableSourceSnapshotAsync();
        var usable = await test.PublishSyntheticAsync();
        var failedKey = test.ValidationKey();
        var failedClaim = await test.ClaimAsync(failedKey);
        var observation = new RevisionReportObservation(RevisionReportOperation.Validation, RevisionReportBackendStatus.InvalidMapping,
            PostgresRevisionDerivationTestSupport.SyntheticProvenance(failedKey), null, new(null, 0, 0, false, 1));
        var failure = RevisionReportCompletion.ForFailure(RevisionReportFailureCategory.UnsupportedArtifact, observation);
        Assert.Equal(RevisionReportStoreOutcome.Applied, (await test.Store.FinishAsync(test.Actor, failedClaim, failure)).Outcome);
        var usableRow = await test.AttemptSnapshotAsync(usable.DerivationId);
        var failedRow = await test.AttemptSnapshotAsync(failedClaim.DerivationId);
        var counts = await test.CountsAsync();
        await test.ReportDatabase.DisposeAsync();
        await test.DerivationDatabase.DisposeAsync();
        await AssertReopenedAsync();
        await test.Case.CloseOwnerPoolAsync();
        await fixture.RestartAsync();
        await using var restartedOwner = await PostgresRevisionDerivationTestSupport.OpenOwnerAsync(test.Case);
        await AssertReopenedAsync(restartedOwner);
        Assert.Equal(source, await test.ImmutableSourceSnapshotAsync(restartedOwner));
        Assert.Equal(usableRow, await test.AttemptSnapshotAsync(usable.DerivationId, restartedOwner));
        Assert.Equal(failedRow, await test.AttemptSnapshotAsync(failedClaim.DerivationId, restartedOwner));
        Assert.Equal(counts, await test.CountsAsync(restartedOwner));

        async Task AssertReopenedAsync(NpgsqlConnection? readbackOwner = null)
        {
            await using var database = new PostgresRevisionReportDatabase(test.Case.ReportConnectionString);
            await using var writes = new PostgresRevisionDerivationDatabase(test.Case.DerivationConnectionString);
            var reader = new PostgresRevisionReportMetadataReader(database);
            var store = new PostgresRevisionDerivationStore(writes);
            Assert.Equal(test.Source, (await reader.GetSourceAsync(test.AdmittedActor, test.Revision))!.Source);
            var current = Assert.IsType<RevisionReportReadSnapshot>(await reader.GetCurrentAsync(test.AdmittedActor, test.Key));
            Assert.Equal(RevisionReportState.Available, current.State);
            Assert.Equal(usable, current.SelectedResult);
            Assert.Equal(usable.Key, current.Key);
            var failed = Assert.IsType<AuthorizedRevisionReportAttempt>(await reader.GetRetainedAsync(
                test.AdmittedActor, test.Revision, failedKey.Operation, failedClaim.DerivationId));
            Assert.Null(failed.Result);
            Assert.Equal(failure, failed.Attempt.Completion);
            Assert.Null(failed.Attempt.Completion!.Observation!.Validation!.IsValid);
            Assert.Equal(RevisionReportBackendStatus.InvalidMapping, failed.Attempt.Completion.Observation.Status);
            Assert.Equal(RevisionReportStoreOutcome.Available,
                (await store.TryClaimAsync(test.AdmittedActor, test.Key, Guid.NewGuid())).Outcome);
            Assert.Equal(counts, readbackOwner is null ? await test.CountsAsync() : await test.CountsAsync(readbackOwner));
        }
    }

    [PostgresFact]
    public async Task IndependentPublicReaderProcessesRecoverExactSyntheticUsableAndFailedAttemptsAfterClusterRestart()
    {
        await using var test = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
        var source = await test.ImmutableSourceSnapshotAsync();
        var usable = await test.PublishSyntheticAsync(test.ValidationKey(), valid: false);
        var failedKey = test.ProjectionKey();
        var claim = await test.ClaimAsync(failedKey);
        var failed = RevisionReportCompletion.ForFailure(RevisionReportFailureCategory.StorageIo,
            new(RevisionReportOperation.Projection, null, null, null, null));
        Assert.Equal(RevisionReportStoreOutcome.Applied, (await test.Store.FinishAsync(test.Actor, claim, failed)).Outcome);
        var usableRow = await test.AttemptSnapshotAsync(usable.DerivationId);
        var failedRow = await test.AttemptSnapshotAsync(claim.DerivationId);
        await AssertProcessesAsync();
        await test.ReportDatabase.DisposeAsync();
        await test.DerivationDatabase.DisposeAsync();
        await test.Case.CloseOwnerPoolAsync();
        await fixture.RestartAsync();
        await AssertProcessesAsync();
        await using var restartedOwner = await PostgresRevisionDerivationTestSupport.OpenOwnerAsync(test.Case);
        Assert.Equal(source, await test.ImmutableSourceSnapshotAsync(restartedOwner));
        Assert.Equal(usableRow, await test.AttemptSnapshotAsync(usable.DerivationId, restartedOwner));
        Assert.Equal(failedRow, await test.AttemptSnapshotAsync(claim.DerivationId, restartedOwner));
        Assert.Equal(new long[] { 2, 2, 1, 1 }, await test.CountsAsync(restartedOwner));

        async Task AssertProcessesAsync()
        {
            var current = await ProbeAsync(test.Case.ReportConnectionString, ProbeInput(test, usable.Key));
            AssertFacts(current, usable.Key, usable.CacheEntryId);
            Assert.Equal("available", current.GetProperty("state").GetString());
            Assert.Equal(Decimal(usable.Generation), current.GetProperty("generation").GetString());
            Assert.Equal(usable.DerivationId.ToString("D"), current.GetProperty("selectedDerivationId").GetString());
            Assert.Equal(usable.DerivationId.ToString("D"), current.GetProperty("currentDerivationId").GetString());
            var retained = await ProbeAsync(test.Case.ReportConnectionString, ProbeInput(test, usable.Key, usable.DerivationId));
            AssertFacts(retained, usable.Key, usable.CacheEntryId);
            Assert.Equal("usable", retained.GetProperty("completion").GetString());
            Assert.True(retained.GetProperty("hasUsableResult").GetBoolean());
            Assert.Equal(JsonValueKind.Null, retained.GetProperty("failure").ValueKind);
            Assert.Equal(usable.DerivedObject.Id.ObjectId.ToString("D"), retained.GetProperty("derivedObjectId").GetString());
            Assert.Equal(usable.DerivedObject.Digest.Value, retained.GetProperty("derivedDigest").GetString());
            Assert.Equal(Decimal(usable.DerivedObject.ByteCount), retained.GetProperty("derivedByteCount").GetString());
            var failedCurrent = await ProbeAsync(test.Case.ReportConnectionString, ProbeInput(test, failedKey));
            AssertFacts(failedCurrent, failedKey, claim.CacheEntryId);
            Assert.Equal("failed", failedCurrent.GetProperty("state").GetString());
            Assert.Equal(JsonValueKind.Null, failedCurrent.GetProperty("selectedDerivationId").ValueKind);
            Assert.Equal(claim.DerivationId.ToString("D"), failedCurrent.GetProperty("currentDerivationId").GetString());
            var failedRetained = await ProbeAsync(test.Case.ReportConnectionString, ProbeInput(test, failedKey, claim.DerivationId));
            AssertFacts(failedRetained, failedKey, claim.CacheEntryId);
            Assert.Equal("failed", failedRetained.GetProperty("completion").GetString());
            Assert.Equal("StorageIo", failedRetained.GetProperty("failure").GetString());
            Assert.False(failedRetained.GetProperty("hasUsableResult").GetBoolean());
            foreach (var field in new[] { "derivedObjectId", "derivedDigest", "derivedByteCount" })
                Assert.Equal(JsonValueKind.Null, failedRetained.GetProperty(field).ValueKind);
            Assert.Equal(Decimal(claim.Generation), failedRetained.GetProperty("generation").GetString());
            Assert.Equal(claim.DerivationId.ToString("D"), failedRetained.GetProperty("derivationId").GetString());
        }
    }

    [PostgresFact]
    public async Task IndependentRetainedReaderStillRequiresCurrentAuthorityAndNeverTurnsFailureIntoNotFound()
    {
        await using var test = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
        var claim = await test.ClaimAsync();
        Assert.Equal(RevisionReportStoreOutcome.Applied, (await test.Store.FinishAsync(test.Actor, claim,
            RevisionReportCompletion.ForFailure(RevisionReportFailureCategory.ProfileUnavailable))).Outcome);
        var original = await test.AttemptSnapshotAsync(claim.DerivationId);
        var found = await ProbeAsync(test.Case.ReportConnectionString, ProbeInput(test, test.Key, claim.DerivationId));
        Assert.Equal("found", found.GetProperty("status").GetString());
        Assert.Equal("failed", found.GetProperty("completion").GetString());
        await test.Case.ExecuteAsync("UPDATE braid.project_memberships SET active=false,version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3",
            test.Project.OrganizationId, test.Project.ProjectId, test.Seed.Admitted);
        var denied = await ProbeAsync(test.Case.ReportConnectionString, ProbeInput(test, test.Key, claim.DerivationId));
        Assert.Equal("notFound", denied.GetProperty("status").GetString());
        Assert.Equal("status", Assert.Single(denied.EnumerateObject()).Name);
        Assert.Equal(original, await test.AttemptSnapshotAsync(claim.DerivationId));
        Assert.Equal(new long[] { 1, 1, 0, 1 }, await test.CountsAsync());
    }

    private static JsonObject ProbeInput(PostgresRevisionDerivationTestSupport test, RevisionReportKey key,
        Guid? derivationId = null)
    {
        var input = new JsonObject
        {
            ["mode"] = derivationId is null ? "current" : "retained",
            ["organizationId"] = key.Scope.OrganizationId.ToString("D"),
            ["projectId"] = key.Scope.ProjectId.ToString("D"),
            ["mappingId"] = key.Scope.MappingId.ToString("D"),
            ["revisionId"] = key.Scope.RevisionId.ToString("D"),
            ["actorId"] = test.AdmittedActor.PrincipalId.Value.ToString("D"),
            ["operation"] = key.Operation == RevisionReportOperation.Projection ? "projection" : "validation"
        };
        if (derivationId is { } id)
            input["derivationId"] = id.ToString("D");
        else
        {
            input["profile"] = new JsonObject
            {
                ["backendId"] = key.BackendId,
                ["backendRevision"] = key.BackendRevision,
                ["backendBuild"] = key.BackendBuild,
                ["adapterBuild"] = key.AdapterBuild
            };
            input["reportSchemaVersion"] = key.SchemaVersion;
            input["algorithmVersion"] = key.AlgorithmVersion;
            input["codecVersion"] = key.CodecVersion;
            input["policy"] = JsonNode.Parse(key.Policy.CopyCanonicalUtf8());
        }
        return input;
    }

    private static void AssertFacts(JsonElement output, RevisionReportKey key, Guid cacheId)
    {
        Assert.Equal("found", output.GetProperty("status").GetString());
        Assert.Equal(key.Scope.OrganizationId.ToString("D"), output.GetProperty("organizationId").GetString());
        Assert.Equal(key.Scope.ProjectId.ToString("D"), output.GetProperty("projectId").GetString());
        Assert.Equal(key.Scope.MappingId.ToString("D"), output.GetProperty("mappingId").GetString());
        Assert.Equal(key.Scope.RevisionId.ToString("D"), output.GetProperty("revisionId").GetString());
        Assert.Equal(key.Operation.ToString().ToLowerInvariant(), output.GetProperty("operation").GetString());
        Assert.Equal(key.Fingerprint.Value, output.GetProperty("keyFingerprint").GetString());
        Assert.Equal(key.Policy.Fingerprint.Value, output.GetProperty("policyFingerprint").GetString());
        Assert.Equal(key.Source.NativeObject.Id.ObjectId.ToString("D"), output.GetProperty("nativeObjectId").GetString());
        Assert.Equal(key.NativeDigest.Value, output.GetProperty("nativeDigest").GetString());
        Assert.Equal(Decimal(key.Source.NativeObject.ByteCount), output.GetProperty("nativeByteCount").GetString());
        Assert.Equal(cacheId.ToString("D"), output.GetProperty("cacheEntryId").GetString());
    }

    private static async Task<JsonElement> ProbeAsync(string connectionString, JsonObject input)
    {
        var dll = Environment.GetEnvironmentVariable("BRAID_REPORT_METADATA_PROBE_PATH") ??
            Path.Combine(AppContext.BaseDirectory, "RevisionReportMetadataProbe", "Braid.RevisionReportMetadataProbe.dll");
        if (!File.Exists(dll)) throw new InvalidOperationException("The independent metadata probe must be copied to the test output.");
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrWhiteSpace(host))
            host = Environment.ProcessPath is { } current && Path.GetFileName(current) == "dotnet" ? current : "dotnet";
        var start = new ProcessStartInfo(host)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false, true),
            StandardOutputEncoding = new UTF8Encoding(false, true),
            StandardErrorEncoding = new UTF8Encoding(false, true)
        };
        start.ArgumentList.Add(dll);
        start.Environment["BRAID_REPORT_METADATA_PROBE_CONNECTION_STRING"] = connectionString;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The independent metadata probe did not start.");
        var output = CaptureAsync(process.StandardOutput, 32768, deadline.Token);
        var error = CaptureAsync(process.StandardError, 4096, deadline.Token);
        try
        {
            var command = input.ToJsonString();
            Assert.InRange(Encoding.UTF8.GetByteCount(command), 1, 32768);
            await process.StandardInput.WriteAsync(command.AsMemory(), deadline.Token);
            process.StandardInput.Close();
            await Task.WhenAll(process.WaitForExitAsync(deadline.Token), output, error);
            Assert.Equal(0, process.ExitCode);
            Assert.True((await error).Length == 0, "The independent metadata probe emitted unexpected diagnostic output.");
            using var document = JsonDocument.Parse(await output, new JsonDocumentOptions { MaxDepth = 16 });
            return document.RootElement.Clone();
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
            }
            using var reap = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await process.WaitForExitAsync(reap.Token);
            deadline.Cancel();
            try { await Task.WhenAll(output, error); }
            catch (OperationCanceledException) { }
        }
    }

    private static async Task<string> CaptureAsync(StreamReader stream, int maximumCharacters, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var buffer = new char[2048];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) return text.ToString();
            if (read > maximumCharacters - text.Length)
                throw new InvalidOperationException("The independent metadata probe exceeded its output budget.");
            text.Append(buffer, 0, read);
        }
    }

    private static string Decimal(long value) => value.ToString(CultureInfo.InvariantCulture);
}
