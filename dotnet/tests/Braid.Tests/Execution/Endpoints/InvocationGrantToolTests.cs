using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Braid.Deployment;

namespace Braid.Tests;

/// <summary>The real owner tool process, through the restricted grant-writer login, as a named administrator.</summary>
[Collection(PostgresCollection.Name)]
public sealed class InvocationGrantToolTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task Administrators_grant_list_and_revoke_through_the_tool_and_others_are_refused()
    {
        await using var test = await MachineInvocationPersistenceTests.Context.CreateAsync(fixture, activate: false);
        var connection = test.Case.RegressionConnectionString("braid_invocation_grant_writer");
        var grant = Guid.NewGuid(); var tenant = Guid.NewGuid(); var principal = Guid.NewGuid();
        string[] Scope(string command, Guid actor) =>
        [
            command, actor.ToString("D"), test.Target.Mapping.OrganizationId.ToString("D"), test.Target.Mapping.ProjectId.ToString("D"),
            test.Target.Mapping.MappingId.ToString("D"), test.Target.Environment.EnvironmentId.ToString("D"),
            test.Target.Environment.Version.ToString(CultureInfo.InvariantCulture), test.Target.TargetId.ToString("D")
        ];
        var admin = test.Actor.PrincipalId.Value;
        var created = await RunAsync(connection, [.. Scope("grant-machine-invocation", admin), grant.ToString("D"), tenant.ToString("D"), principal.ToString("D")]);
        Assert.Equal((0, "{\"outcome\":\"granted\"}"), (created.ExitCode, created.Output.Trim()));
        var replay = await RunAsync(connection, [.. Scope("grant-machine-invocation", admin), grant.ToString("D"), tenant.ToString("D"), principal.ToString("D")]);
        Assert.Equal((0, "{\"outcome\":\"replayed\"}"), (replay.ExitCode, replay.Output.Trim()));
        var stranger = await RunAsync(connection, [.. Scope("grant-machine-invocation", Guid.NewGuid()), Guid.NewGuid().ToString("D"), tenant.ToString("D"), Guid.NewGuid().ToString("D")]);
        Assert.Equal((3, "{\"outcome\":\"refused\"}"), (stranger.ExitCode, stranger.Output.Trim()));
        var listed = await RunAsync(connection, Scope("list-machine-invocation-grants", admin));
        Assert.Equal(0, listed.ExitCode);
        using (var document = JsonDocument.Parse(listed.Output))
        {
            var entries = document.RootElement.EnumerateArray().ToArray();
            Assert.Equal(2, entries.Length); // The context's own grant and the tool's grant.
            Assert.Contains(entries, entry => entry.GetProperty("grantId").GetString() == grant.ToString("D") &&
                entry.GetProperty("objectId").GetString() == principal.ToString("D") && entry.GetProperty("revokedAt").ValueKind == JsonValueKind.Null);
        }
        var revoked = await RunAsync(connection, [.. Scope("revoke-machine-invocation", admin), grant.ToString("D")]);
        Assert.Equal((0, "{\"outcome\":\"revoked\"}"), (revoked.ExitCode, revoked.Output.Trim()));
        Assert.False((await test.Grants.ReadAsync(test.Actor, test.Target)).Single(item => item.GrantId == grant).Active);
        var invalid = await RunAsync(connection, [.. Scope("revoke-machine-invocation", admin), "not-a-grant"]);
        Assert.Equal(2, invalid.ExitCode);
        var unconfigured = await RunAsync(null, Scope("list-machine-invocation-grants", admin));
        Assert.Equal(2, unconfigured.ExitCode);
        Assert.Equal(InvocationGrantOutcome.Replayed, await test.Grants.RevokeAsync(test.Actor, test.Target, grant));
    }

    private sealed record ToolResult(int ExitCode, string Output, string Error);

    private static async Task<ToolResult> RunAsync(string? connection, string[] arguments)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "DatabaseTool", "Braid.DatabaseTool.dll");
        Assert.True(File.Exists(dll), "The owner database tool must be copied into the test output.");
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrWhiteSpace(host)) host = Environment.ProcessPath is { } current && Path.GetFileName(current) == "dotnet" ? current : "dotnet";
        var start = new ProcessStartInfo(host) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(dll);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment.Remove("BRAID_MIGRATION_CONNECTION_STRING");
        start.Environment.Remove("BRAID_INVOCATION_GRANT_WRITER_CONNECTION_STRING");
        if (connection is not null) start.Environment["BRAID_INVOCATION_GRANT_WRITER_CONNECTION_STRING"] = connection;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The owner tool did not start.");
        var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var error = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            var result = new ToolResult(process.ExitCode, await output, await error);
            // No connection text or password on either stream.
            Assert.DoesNotContain("Password", result.Output + result.Error, StringComparison.OrdinalIgnoreCase);
            return result;
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }
}
