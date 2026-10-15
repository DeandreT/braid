using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Npgsql;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

/// <summary>Invokes the ordinary tool, optionally from root's ordinary published output.</summary>
[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class PostgresMaintenanceCommandToolProcessTests(PostgresFixture fixture)
{
    private const string Usage = "Usage: Weft.MaintenanceTool run-once ORGANIZATION_ID PROJECT_ID ROOT APPLICATION_ROOT [--repository-root REPOSITORY_ROOT] [--cleanup-after JSON] [--status-after JSON]";
    private static readonly JsonSerializerOptions WireOptions = CreateWireOptions();

    [Fact]
    public async Task ActualOrdinaryToolHelpAndUsageHaveFixedBoundedOutput()
    {
        var help = await InvokeAsync(["--help"]);
        Assert.Equal(0, help.ExitCode);
        Assert.Equal(Usage + Environment.NewLine, help.Output);
        Assert.Empty(help.Error);
        foreach (var arguments in new[] { Array.Empty<string>(), new[] { "run-once" }, new[] { "--help", "extra" }, new[] { "initialize" } })
        {
            var invalid = await InvokeAsync(arguments);
            Assert.Equal(2, invalid.ExitCode);
            Assert.Empty(invalid.Output);
            Assert.Equal("Usage" + Environment.NewLine + Usage + Environment.NewLine, invalid.Error);
        }
    }

    [Fact]
    public async Task ActualOrdinaryToolRequiresOnlyItsDedicatedEnvironmentCredentialBeforeOpeningOrCreatingStorage()
    {
        using var root = new PrivateStorageTestRoot();
        var project = new ProjectScope(Guid.NewGuid(), Guid.NewGuid());
        var arguments = Arguments(root, project);
        var secret = "owned-secret-" + Guid.NewGuid().ToString("N");
        foreach (var connection in new string?[] { null, " ", "UnsupportedCredentialKeyword=" + secret })
        {
            var result = await InvokeAsync(arguments, connection,
                privateValues: [root.BasePath, secret]);
            Assert.Equal(2, result.ExitCode);
            Assert.Empty(result.Output);
            Assert.Equal("InvalidConfiguration" + Environment.NewLine, result.Error);
            Assert.False(Directory.Exists(root.RootPath));
        }
    }

    [Fact]
    public async Task ActualOrdinaryToolRefusesMalformedForeignDuplicateAndOversizedIndependentCursorsBeforeStorage()
    {
        using var root = new PrivateStorageTestRoot();
        var project = new ProjectScope(Guid.NewGuid(), Guid.NewGuid());
        var foreign = new ProjectScope(project.OrganizationId, Guid.NewGuid());
        var status = JsonSerializer.Serialize(new ObjectCleanupStatusCursor(project, Guid.NewGuid()), WireOptions);
        var foreignStatus = JsonSerializer.Serialize(new ObjectCleanupStatusCursor(foreign, Guid.NewGuid()), WireOptions);
        var scope = new RevisionScope(project.OrganizationId, project.ProjectId, Guid.NewGuid(), Guid.NewGuid());
        var cleanup = JsonSerializer.Serialize(new ObjectCleanupScanCursor(project, ObjectCleanupScanStream.NativeStage,
            DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            new(PrivateObjectId.Create(scope, PrivateObjectClass.NativeArtifact), CleanupTargetKind.FinalObject)), WireOptions);
        var malformed = new (string Option, string Cursor)[]
        {
            ("--status-after", "{}"),
            ("--status-after", foreignStatus),
            ("--status-after", status.Replace("\"project\":", "\"unexpected\":0,\"project\":", StringComparison.Ordinal)),
            ("--status-after", status.Replace("\"afterRetirementId\":", "\"afterRetirementId\":null,\"afterRetirementId\":", StringComparison.Ordinal)),
            ("--status-after", new string(' ', 513)),
            ("--cleanup-after", "null"),
            ("--cleanup-after", status),
            ("--cleanup-after", cleanup.Replace("\"nativeStage\"", "\"trackedTemporary\"", StringComparison.Ordinal)),
            ("--cleanup-after", cleanup.Replace("\"publicationAttemptId\":null", "\"publicationAttemptId\":\"00000000-0000-0000-0000-000000000001\"", StringComparison.Ordinal)),
            ("--cleanup-after", new string(' ', 2049))
        };
        foreach (var (option, cursor) in malformed)
        {
            var result = await InvokeAsync([.. Arguments(root, project), option, cursor], privateValues: [root.BasePath]);
            Assert.Equal(2, result.ExitCode);
            Assert.Empty(result.Output);
            Assert.Equal("Usage" + Environment.NewLine + Usage + Environment.NewLine, result.Error);
            Assert.False(Directory.Exists(root.RootPath));
        }
        var duplicateOption = await InvokeAsync([.. Arguments(root, project), "--status-after", status, "--status-after", status]);
        Assert.Equal(2, duplicateOption.ExitCode);
        Assert.Empty(duplicateOption.Output);
        Assert.Equal("Usage" + Environment.NewLine + Usage + Environment.NewLine, duplicateOption.Error);
        Assert.False(Directory.Exists(root.RootPath));
    }

    [RevisionIngestionFact]
    public async Task ActualOrdinaryToolDeletesARealTerminalStageAndRoundTripsBothScopedCursorsWithoutPrivateOutput()
    {
        await using var test = await PostgresClaimAwareStorageIntegrationSupport.CreateAsync(fixture);
        var receipt = await MaintenanceCommandProcessTestSupport.RejectedNativeAsync(test);
        var marker = File.ReadAllBytes(test.Root.MarkerPath);
        var arguments = Arguments(test.Root, test.Project);
        var privateValues = new[] { test.Root.BasePath, test.Case.MaintenanceConnectionString,
            new NpgsqlConnectionStringBuilder(test.Case.MaintenanceConnectionString).Password!, test.Actor.PrincipalId.Value.ToString("D") };
        var result = await InvokeAsync(arguments, test.Case.MaintenanceConnectionString, privateValues);
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Error);
        using var document = JsonDocument.Parse(result.Output);
        var output = document.RootElement;
        Assert.Equal("finished", output.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, output.GetProperty("failure").ValueKind);
        Assert.Equal(JsonValueKind.Null, output.GetProperty("cleanupAfter").ValueKind);
        Assert.Equal(JsonValueKind.Null, output.GetProperty("statusAfter").ValueKind);
        Assert.Equal(1, output.GetProperty("claimedCount").GetInt32());
        Assert.Equal(1, output.GetProperty("physicalDeleted").GetInt32());
        Assert.Equal(1, output.GetProperty("acknowledgedDeleted").GetInt32());
        var target = Assert.Single(output.GetProperty("targets").EnumerateArray());
        Assert.Equal("deleted", target.GetProperty("physical").GetString());
        Assert.Equal("deleted", target.GetProperty("acknowledgedDisposition").GetString());
        Assert.Equal("finalObject", target.GetProperty("targetKind").GetString());
        Assert.Equal("nativeArtifact", target.GetProperty("objectClass").GetString());
        Assert.False(File.Exists(test.Root.FinalPath(receipt.Facts.NativeObject)));
        Assert.Equal(marker, File.ReadAllBytes(test.Root.MarkerPath));
        AssertNoPrivateProperties(output);

        await using var database = new PostgresPrivateObjectMaintenanceDatabase(test.Case.MaintenanceConnectionString);
        var metadata = new PostgresPrivateObjectMaintenanceStore(database);
        var claim = await MaintenanceCommandProcessTestSupport.SingleCleanupClaimAsync(test.Case, metadata, test.Project);
        var cleanupAfter = new ObjectCleanupScanCursor(test.Project, ObjectCleanupScanStream.DueRetirement,
            claim.Retirement.EligibilityOriginAtUtc, claim.Retirement.Target);
        var statusAfter = new ObjectCleanupStatusCursor(test.Project, claim.Retirement.RetirementId);
        var cleanupJson = JsonSerializer.Serialize(cleanupAfter, WireOptions);
        var statusJson = JsonSerializer.Serialize(statusAfter, WireOptions);
        var continued = await InvokeAsync([.. arguments, "--cleanup-after", cleanupJson, "--status-after", statusJson],
            test.Case.MaintenanceConnectionString, privateValues);
        Assert.Equal(0, continued.ExitCode);
        Assert.Empty(continued.Error);
        using var continuation = JsonDocument.Parse(continued.Output);
        var next = continuation.RootElement;
        Assert.Equal("finished", next.GetProperty("outcome").GetString());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(cleanupJson), JsonNode.Parse(next.GetProperty("cleanupAfter").GetRawText())));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(statusJson), JsonNode.Parse(next.GetProperty("statusAfter").GetRawText())));
        Assert.Equal(0, next.GetProperty("claimedCount").GetInt32());
        Assert.Equal(0, next.GetProperty("physicalDeleted").GetInt32());
        var observations = next.GetProperty("observations");
        Assert.Equal(0, observations.GetProperty("metadataRetirementsExamined").GetInt32());
        Assert.True(observations.GetProperty("metadataStatusPageComplete").GetBoolean());
        Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.private_object_cleanup_attempts"));
        Assert.Equal(marker, File.ReadAllBytes(test.Root.MarkerPath));
        AssertNoPrivateProperties(next);

        var wrongPassword = "owned-invalid-password-" + Guid.NewGuid().ToString("N");
        var unavailableConnection = new NpgsqlConnectionStringBuilder(test.Case.MaintenanceConnectionString)
        { Password = wrongPassword }.ConnectionString;
        var unavailable = await InvokeAsync(arguments, unavailableConnection, [.. privateValues, wrongPassword, unavailableConnection]);
        Assert.Equal(1, unavailable.ExitCode);
        Assert.Empty(unavailable.Error);
        using var refusal = JsonDocument.Parse(unavailable.Output);
        var refused = refusal.RootElement;
        Assert.Equal("failed", refused.GetProperty("outcome").GetString());
        Assert.Equal("metadataUnavailable", refused.GetProperty("failure").GetString());
        Assert.False(refused.GetProperty("claimAttempted").GetBoolean());
        Assert.Equal(JsonValueKind.Null, refused.GetProperty("claimedCount").ValueKind);
        Assert.Equal(JsonValueKind.Null, refused.GetProperty("candidatesExamined").ValueKind);
        Assert.Equal(JsonValueKind.Null, refused.GetProperty("observations").GetProperty("metadataRetirementsExamined").ValueKind);
        Assert.Equal(JsonValueKind.Null, refused.GetProperty("observations").GetProperty("filesystemEntriesExamined").ValueKind);
        Assert.Empty(refused.GetProperty("targets").EnumerateArray());
        AssertNoPrivateProperties(refused);
        Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.private_object_cleanup_attempts"));
    }

    [RevisionIngestionFact]
    public async Task ActualOrdinaryToolReportsBusyAsUnfinishedAndOnlyRetriesAfterGateReleaseAndTheSeparateAuthoredLeaseShift()
    {
        await using var test = await PostgresClaimAwareStorageIntegrationSupport.CreateAsync(fixture);
        var receipt = await MaintenanceCommandProcessTestSupport.RejectedNativeAsync(test);
        var arguments = Arguments(test.Root, test.Project);
        var privateValues = new[] { test.Root.BasePath, test.Case.MaintenanceConnectionString,
            new NpgsqlConnectionStringBuilder(test.Case.MaintenanceConnectionString).Password! };
        await using var database = new PostgresPrivateObjectMaintenanceDatabase(test.Case.MaintenanceConnectionString);
        var metadata = new PostgresPrivateObjectMaintenanceStore(database);
        ObjectCleanupClaim original;
        // This separately opened real exclusive descriptor models a current
        // holder of the production object gate; no test observer supplies Busy.
        using (new PrivateStorageV2IndependentGate(Path.Combine(PrivateStorageV2Data.ObjectLeaf(test.Root,
            receipt.Facts.NativeObject.Id), "object.gate")))
        {
            var busy = await InvokeAsync(arguments, test.Case.MaintenanceConnectionString, privateValues);
            Assert.Equal(3, busy.ExitCode);
            Assert.Empty(busy.Error);
            using var document = JsonDocument.Parse(busy.Output);
            var output = document.RootElement;
            Assert.Equal("finished", output.GetProperty("outcome").GetString());
            var target = Assert.Single(output.GetProperty("targets").EnumerateArray());
            Assert.Equal("busy", target.GetProperty("physical").GetString());
            Assert.Equal("notAttempted", target.GetProperty("acknowledgement").GetString());
            Assert.Equal(JsonValueKind.Null, target.GetProperty("acknowledgedDisposition").ValueKind);
            Assert.Equal(0, output.GetProperty("acknowledgedRetryable").GetInt32());
            Assert.Equal("{not native JSON"u8.ToArray(), File.ReadAllBytes(test.Root.FinalPath(receipt.Facts.NativeObject)));
            original = await MaintenanceCommandProcessTestSupport.SingleCleanupClaimAsync(test.Case, metadata, test.Project);
            Assert.Equal(1, original.Generation);
            Assert.Equal(TimeSpan.FromSeconds(60), original.LeaseExpiresAtUtc - original.StartedAtUtc);
            var attempt = (await metadata.GetAttemptAsync(test.Project, original.Retirement.RetirementId, 1))!;
            Assert.Null(attempt.Disposition);
            Assert.Null(attempt.FinishedAtUtc);
            Assert.False(File.Exists(Path.Combine(PrivateStorageV2Data.ObjectLeaf(test.Root, receipt.Facts.NativeObject.Id), "object.retired")));
        }

        // Releasing a gate alone does not waive the original durable lease.
        var waiting = await InvokeAsync(arguments, test.Case.MaintenanceConnectionString, privateValues);
        Assert.Equal(0, waiting.ExitCode);
        using (var document = JsonDocument.Parse(waiting.Output))
        {
            Assert.Equal(0, document.RootElement.GetProperty("claimedCount").GetInt32());
            Assert.True(document.RootElement.GetProperty("notDueCandidatesObserved").GetInt32() >= 1);
            Assert.Empty(document.RootElement.GetProperty("targets").EnumerateArray());
        }
        Assert.True(File.Exists(test.Root.FinalPath(receipt.Facts.NativeObject)));
        await MaintenanceCommandProcessTestSupport.AgeCleanupAsync(test.Case, original);
        var retry = await InvokeAsync(arguments, test.Case.MaintenanceConnectionString, privateValues);
        Assert.Equal(0, retry.ExitCode);
        using (var document = JsonDocument.Parse(retry.Output))
        {
            Assert.Equal(1, document.RootElement.GetProperty("physicalDeleted").GetInt32());
            Assert.Equal(1, document.RootElement.GetProperty("acknowledgedDeleted").GetInt32());
            Assert.Equal(2, Assert.Single(document.RootElement.GetProperty("targets").EnumerateArray()).GetProperty("generation").GetInt64());
        }
        Assert.False(File.Exists(test.Root.FinalPath(receipt.Facts.NativeObject)));
        Assert.Null((await metadata.GetAttemptAsync(test.Project, original.Retirement.RetirementId, 1))!.Disposition);
        Assert.Equal(ObjectCleanupDisposition.Deleted,
            (await metadata.GetAttemptAsync(test.Project, original.Retirement.RetirementId, 2))!.Disposition);
        Assert.Equal(2L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.private_object_cleanup_attempts"));
    }

    private static string[] Arguments(PrivateStorageTestRoot root, ProjectScope project) =>
        ["run-once", project.OrganizationId.ToString("D"), project.ProjectId.ToString("D"), root.RootPath, root.ApplicationRoot];

    private static async Task<ToolResult> InvokeAsync(string[] arguments, string? connection = null, string[]? privateValues = null)
    {
        var dll = Environment.GetEnvironmentVariable("WEFT_MAINTENANCE_TOOL_PATH") ??
            Path.Combine(AppContext.BaseDirectory, "MaintenanceTool", "Weft.MaintenanceTool.dll");
        if (!File.Exists(dll)) throw new InvalidOperationException("The ordinary maintenance tool must be copied into the test output.");
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrWhiteSpace(host))
            host = Environment.ProcessPath is { } current && Path.GetFileName(current) == "dotnet" ? current : "dotnet";
        var start = new ProcessStartInfo(host)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(dll);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment.Remove("WEFT_MAINTENANCE_CONNECTION_STRING");
        // Wrong-purpose credentials cannot serve as a fallback.
        start.Environment["WEFT_CONNECTION_STRING"] = "UnsupportedCredentialKeyword=wrong-purpose-owned-sentinel";
        start.Environment["WEFT_INGESTION_CONNECTION_STRING"] = "UnsupportedCredentialKeyword=wrong-purpose-owned-sentinel";
        if (connection is not null) start.Environment["WEFT_MAINTENANCE_CONNECTION_STRING"] = connection;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The owned maintenance tool did not start.");
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var output = ReadBoundedAsync(process.StandardOutput, lifetime.Token);
        var error = ReadBoundedAsync(process.StandardError, lifetime.Token);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(lifetime.Token), output, error);
            var result = new ToolResult(process.ExitCode, await output, await error);
            foreach (var value in privateValues ?? [])
            {
                Assert.DoesNotContain(value, result.Output, StringComparison.Ordinal);
                Assert.DoesNotContain(value, result.Error, StringComparison.Ordinal);
            }
            Assert.DoesNotContain("wrong-purpose-owned-sentinel", result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("wrong-purpose-owned-sentinel", result.Error, StringComparison.Ordinal);
            return result;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: false);
                using var reap = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(reap.Token);
            }
            await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var output = new System.Text.StringBuilder();
        while (true)
        {
            var count = await reader.ReadAsync(buffer, cancellationToken);
            if (count == 0) return output.ToString();
            if (output.Length + count > 32768) throw new InvalidOperationException("The owned maintenance tool exceeded its output bound.");
            output.Append(buffer, 0, count);
        }
    }

    private static void AssertNoPrivateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
            {
                Assert.DoesNotContain(property.Name, new[] { "leaseToken", "serverInstanceId", "attemptId", "retirementId", "rawName", "path", "actor", "claim", "origin", "publication" });
                AssertNoPrivateProperties(property.Value);
            }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) AssertNoPrivateProperties(child);
    }

    private static JsonSerializerOptions CreateWireOptions()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }

    private sealed record ToolResult(int ExitCode, string Output, string Error);
}
