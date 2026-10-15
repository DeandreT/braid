using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using Weft.Application;
using Weft.Domain;

namespace Weft.Tests;

/// <summary>
/// Owner-authored aging changes only the disposable case's eligibility/lease
/// timestamps. It proves no elapsed grace; workflow, process and unlink facts
/// remain actual and are checked separately by the process tests.
/// </summary>
[SupportedOSPlatform("linux")]
internal static class MaintenanceCommandProcessTestSupport
{
    internal static async Task<IngestionReceipt> RejectedNativeAsync(PostgresClaimAwareStorageIntegrationSupport test)
    {
        var body = "{not native JSON"u8.ToArray();
        var result = await test.CreateNativeAsync(body);
        Assert.Equal(RevisionIngestionOutcome.Rejected, result.Outcome);
        Assert.Equal(RevisionIngestionRejection.NativeDecode, result.Rejection);
        var receipt = await test.ReceiptAsync(result);
        Assert.Equal(IngestionPhase.Rejected, receipt.Phase);
        Assert.Equal(body, await test.Physical.ReadVerifiedAsync(receipt.Facts.FutureRevision, receipt.Facts.NativeObject));
        Assert.Single(test.Native.Runner.NativeBodies);
        Assert.Equal(body, test.Native.Runner.NativeBodies[0]);
        await AgeNativeAsync(test.Case, receipt.Facts.OperationId, TimeSpan.FromHours(25));
        return receipt;
    }

    internal static async Task<ObjectCleanupClaim> SingleCleanupClaimAsync(PostgresCase test,
        IPrivateObjectMaintenanceMetadata metadata, ProjectScope project)
    {
        var retirement = await test.ScalarAsync<Guid>("SELECT retirement_id FROM weft.private_object_retirements");
        return Assert.IsType<ObjectCleanupAttempt>(await metadata.GetAttemptAsync(project, retirement, 1)).Claim;
    }

    internal static async Task<IngestionClaim> NativeClaimAsync(PostgresClaimAwareStorageIntegrationSupport test, Guid operation)
    {
        await using var connection = await test.Case.Owner.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT lease_owner_id,lease_token,lease_generation,lease_expires_at
            FROM weft.revision_ingestions WHERE organization_id=$1 AND project_id=$2 AND operation_id=$3
            """, connection);
        command.Parameters.AddWithValue(test.Project.OrganizationId);
        command.Parameters.AddWithValue(test.Project.ProjectId);
        command.Parameters.AddWithValue(operation);
        await using var rows = await command.ExecuteReaderAsync();
        Assert.True(await rows.ReadAsync());
        var claim = new IngestionClaim(test.Project, operation, test.Actor.PrincipalId,
            rows.GetGuid(0), rows.GetGuid(1), rows.GetInt64(2), rows.GetFieldValue<DateTimeOffset>(3));
        Assert.False(await rows.ReadAsync());
        return claim;
    }

    internal static Task AgeCleanupAsync(PostgresCase test, ObjectCleanupClaim claim) =>
        EditAsync(test,
            [("private_object_retirements", "private_object_retirements_transition_guard"),
             ("private_object_cleanup_attempts", "private_object_cleanup_attempts_transition_guard")],
            new("UPDATE weft.private_object_retirements SET retired_at=retired_at-$1 WHERE retirement_id=$2",
                [TimeSpan.FromMinutes(2), claim.Retirement.RetirementId], 1),
            new("UPDATE weft.private_object_cleanup_attempts SET started_at=started_at-$1,lease_expires_at=lease_expires_at-$1,finished_at=finished_at-$1 WHERE retirement_id=$2",
                [TimeSpan.FromMinutes(2), claim.Retirement.RetirementId], 1));

    internal static Task AgeNativeAsync(PostgresCase test, Guid operation, TimeSpan age) =>
        EditAsync(test,
            [("revision_ingestions", "revision_ingestions_transition_guard"),
             ("revision_ingestion_attempts", "revision_ingestion_attempts_transition_guard")],
            new("UPDATE weft.revision_ingestions SET created_at=created_at-$1,completed_at=completed_at-$1,lease_expires_at=lease_expires_at-$1,verified_stage_at=verified_stage_at-$1 WHERE operation_id=$2",
                [age, operation], 1),
            new("UPDATE weft.revision_ingestion_attempts SET started_at=started_at-$1,finished_at=finished_at-$1,verified_stage_at=verified_stage_at-$1 WHERE operation_id=$2",
                [age, operation], 1));

    private static async Task EditAsync(PostgresCase test, (string Table, string Trigger)[] triggers, params Edit[] edits)
    {
        await using var owner = await test.Owner.OpenConnectionAsync();
        await using var transaction = await owner.BeginTransactionAsync();
        foreach (var (table, trigger) in triggers)
            await ExecuteAsync($"ALTER TABLE weft.{table} DISABLE TRIGGER {trigger}");
        foreach (var edit in edits) Assert.Equal(edit.ExpectedRows, await ExecuteAsync(edit.Sql, edit.Values));
        await ExecuteAsync("SET CONSTRAINTS ALL IMMEDIATE");
        foreach (var (table, trigger) in triggers)
            await ExecuteAsync($"ALTER TABLE weft.{table} ENABLE TRIGGER {trigger}");
        await transaction.CommitAsync();

        async Task<int> ExecuteAsync(string sql, object[]? values = null)
        {
            await using var command = new NpgsqlCommand(sql, owner, transaction);
            foreach (var value in values ?? []) command.Parameters.AddWithValue(value);
            return await command.ExecuteNonQueryAsync();
        }
    }

    private sealed record Edit(string Sql, object[] Values, int ExpectedRows);
}

/// <summary>One directly owned child; it starts no worker or other child process.</summary>
[SupportedOSPlatform("linux")]
internal sealed class MaintenanceCommandProcessProbe : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };
    private readonly Process process;
    private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(100));
    private readonly Task<string> output;
    private readonly Task<string> error;
    private readonly string[] privateValues;

    private MaintenanceCommandProcessProbe(Process process, string[] privateValues)
    {
        this.process = process;
        this.privateValues = privateValues.Where(value => value.Length != 0).ToArray();
        output = CaptureAsync(process.StandardOutput, lifetime.Token);
        error = CaptureAsync(process.StandardError, lifetime.Token);
    }

    internal static async Task<MaintenanceCommandProcessProbe> StartAsync(PostgresClaimAwareStorageIntegrationSupport test,
        string operation = "run", string? gateDirectory = null, string? gateStage = null,
        Guid operationId = default, byte[]? content = null)
    {
        var dll = Environment.GetEnvironmentVariable("WEFT_MAINTENANCE_PROBE_PATH") ??
            Path.Combine(AppContext.BaseDirectory, "MaintenanceProbe", "Weft.MaintenanceProbe.dll");
        if (!File.Exists(dll)) throw new InvalidOperationException("The test-only maintenance probe must be copied into the test output.");
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrWhiteSpace(host))
            host = Environment.ProcessPath is { } current && Path.GetFileName(current) == "dotnet" ? current : "dotnet";
        var start = new ProcessStartInfo(host)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(dll);
        start.Environment["WEFT_MAINTENANCE_PROBE_CONNECTION"] = test.Case.MaintenanceConnectionString;
        start.Environment["WEFT_MAINTENANCE_PROBE_INGESTION_CONNECTION"] = test.Case.IngestionConnectionString;
        var process = Process.Start(start) ?? throw new InvalidOperationException("The owned maintenance probe did not start.");
        var child = new MaintenanceCommandProcessProbe(process,
            [test.Root.BasePath, test.Root.RootPath, test.Case.MaintenanceConnectionString, test.Case.IngestionConnectionString,
             new NpgsqlConnectionStringBuilder(test.Case.MaintenanceConnectionString).Password ?? string.Empty,
             new NpgsqlConnectionStringBuilder(test.Case.IngestionConnectionString).Password ?? string.Empty]);
        try
        {
            var command = new
            {
                Operation = operation,
                test.Root.RootPath,
                ApplicationContentRoot = test.Root.ApplicationRoot,
                test.Project.OrganizationId,
                test.Project.ProjectId,
                ServerInstanceId = Guid.NewGuid(),
                GateDirectory = gateDirectory,
                GateStage = gateStage,
                ActorId = test.Actor.PrincipalId.Value,
                OperationId = operationId,
                Base64Content = content is null ? null : Convert.ToBase64String(content)
            };
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command, JsonOptions).AsMemory(), child.lifetime.Token);
            process.StandardInput.Close();
            return child;
        }
        catch { await child.DisposeAsync(); throw; }
    }

    internal async Task<MaintenanceCommandProcessSignal> WaitReadyAsync(string directory, string stage)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var path = Path.Combine(directory, "ready");
        while (!File.Exists(path))
        {
            if (process.HasExited)
            {
                var result = await CompleteAsync();
                throw new InvalidOperationException($"The owned maintenance probe exited before readiness ({result.Kind}/{result.Error}).");
            }
            await Task.Delay(10, timeout.Token);
        }
        Assert.InRange(new FileInfo(path).Length, 1, 512);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        var signal = JsonSerializer.Deserialize<MaintenanceCommandProcessSignal>(await File.ReadAllTextAsync(path, timeout.Token), JsonOptions)
            ?? throw new InvalidOperationException("The owned maintenance probe signal was invalid.");
        Assert.Equal(stage, signal.Stage);
        Assert.False(process.HasExited);
        return signal;
    }

    internal async Task KillAsync()
    {
        Assert.False(process.HasExited);
        // This exact Process is owned here and the test probe creates no children.
        process.Kill(entireProcessTree: false);
        using var reap = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await process.WaitForExitAsync(reap.Token);
        Assert.NotEqual(0, process.ExitCode);
        await AssertBoundedPrivateOutputAsync();
    }

    internal async Task<MaintenanceCommandProcessResponse> CompleteAsync()
    {
        await process.WaitForExitAsync(lifetime.Token);
        await AssertBoundedPrivateOutputAsync();
        Assert.Empty(await error);
        var response = JsonSerializer.Deserialize<MaintenanceCommandProcessResponse>(await output, JsonOptions)
            ?? throw new InvalidOperationException("The owned maintenance probe response was invalid.");
        return response with { ExitCode = process.ExitCode };
    }

    private async Task AssertBoundedPrivateOutputAsync()
    {
        await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(5));
        foreach (var value in privateValues)
        {
            Assert.DoesNotContain(value, await output, StringComparison.Ordinal);
            Assert.DoesNotContain(value, await error, StringComparison.Ordinal);
        }
    }

    private static async Task<string> CaptureAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var bytes = new char[1024];
        var result = new System.Text.StringBuilder();
        while (true)
        {
            var count = await reader.ReadAsync(bytes, cancellationToken);
            if (count == 0) return result.ToString();
            if (result.Length + count > 8192) throw new InvalidOperationException("The owned maintenance probe exceeded its output bound.");
            result.Append(bytes, 0, count);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!process.HasExited) await KillAsync();
            else await AssertBoundedPrivateOutputAsync();
        }
        finally { lifetime.Cancel(); lifetime.Dispose(); process.Dispose(); }
    }
}

internal sealed record MaintenanceCommandProcessSignal(string Stage, int ClaimCount, string? Physical);
internal sealed record MaintenanceCommandProcessResponse(string Kind, string? Outcome, string? Failure, string? Error,
    int? ClaimedCount, int PhysicalDeleted, int PhysicalMissing, int AcknowledgedDeleted, int AcknowledgedMissing,
    long[]? Generations, string[]? Physical, string[]? Acknowledgements)
{
    internal int ExitCode { get; init; }
}
