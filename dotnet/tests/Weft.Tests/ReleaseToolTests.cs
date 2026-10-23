using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Weft.Deployment;
using Weft.MappingBackends;
using Weft.ReleaseTool;

namespace Weft.Tests;

/// <summary>The release operator tool: closed argument and login admission, then the real process through restricted logins.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ReleaseToolTests(PostgresFixture fixture)
{
    private static readonly string[] Ids = Enumerable.Range(0, 9).Select(_ => Guid.NewGuid().ToString("D")).ToArray();

    [Fact]
    public void Commands_admit_only_complete_argument_lists_with_canonical_identifiers()
    {
        var qualify = ReleaseCommands.Parse(["qualify-build", Ids[0], Ids[1], Ids[2], Ids[3], Ids[4], Ids[5]]);
        Assert.Null(qualify.Target);
        Assert.Equal([Guid.Parse(Ids[4]), Guid.Parse(Ids[5])], qualify.Values);
        var activate = ReleaseCommands.Parse(["activate-release", .. Target(), Ids[5], Ids[6], "none"]);
        Assert.Equal(2L, activate.Target!.Environment.Version);
        Assert.Null(activate.Expected);
        var rollback = ReleaseCommands.Parse(["rollback-release", .. Target(), Ids[5], Ids[6], Ids[7]]);
        Assert.Equal(Guid.Parse(Ids[7]), rollback.Expected);
        Assert.Empty(ReleaseCommands.Parse(["register-release-target", .. Target()]).Values);
        foreach (var arguments in new[]
        {
            Array.Empty<string>(), ["deploy-release", .. Target()], ["register-release-target", .. Target(), Ids[5]],
            ["qualify-build", Ids[0], Ids[1], Ids[2], Ids[3], Ids[4]], ["qualify-build", Ids[0], Ids[1], Ids[2], Ids[3], Ids[4], Ids[5].ToUpperInvariant()],
            ["register-release-target", Ids[0], Ids[1], Ids[2], Ids[3], Ids[4], "02", Ids[5]],
            ["register-release-target", Ids[0], Ids[1], Ids[2], Ids[3], Ids[4], "0", Ids[5]],
            ["register-release-target", Guid.Empty.ToString("D"), Ids[1], Ids[2], Ids[3], Ids[4], "1", Ids[5]],
            // A rollback names an expected current release other than its source, and never "none".
            ["rollback-release", .. Target(), Ids[5], Ids[6], "none"], ["rollback-release", .. Target(), Ids[5], Ids[6], Ids[6]],
            ["rollback-release", .. Target(), Ids[5], Ids[5], Ids[7]], ["activate-release", .. Target(), Ids[5], Ids[6], "None"]
        })
            Assert.Throws<ArgumentException>(() => ReleaseCommands.Parse(arguments));
    }

    [Fact]
    public void Each_login_must_name_its_restricted_role_with_an_explicit_password()
    {
        const string Setting = ReleaseCommands.ReleaseWriter;
        const string Valid = "Host=db;Username=weft_release_writer;Password=p";
        Assert.Equal(Valid, ReleaseCommands.Login(_ => Valid, Setting, "weft_release_writer"));
        foreach (var value in new string?[]
        {
            null, "", "Host=db;Username=weft_release_reader;Password=p", "Host=db;Username=weft_release_writer",
            "Host=db;Username=weft_release_writer;Password=p;Passfile=/tmp/pass", "Host=db;Username=weft_release_writer;Password=p;Unknown=1"
        })
            Assert.Throws<ArgumentException>(() => ReleaseCommands.Login(_ => value, Setting, "weft_release_writer"));
    }

    [Fact]
    public async Task Invalid_arguments_and_missing_configuration_stop_before_any_database_access()
    {
        var usage = await RunAsync(new Dictionary<string, string>(), ["activate-release", .. Target()]);
        Assert.Equal(2, usage.ExitCode);
        Assert.Contains("Usage: Weft.ReleaseTool", usage.Error, StringComparison.Ordinal);
        var unconfigured = await RunAsync(new Dictionary<string, string>(), ["register-release-target", .. Target()]);
        Assert.Equal((2, "", "{\"error\":\"configurationInvalid\"}"), (unconfigured.ExitCode, unconfigured.Output, unconfigured.Error.Trim()));
        var mutableImage = await RunAsync(new Dictionary<string, string>
        {
            [ReleaseCommands.RuntimeImageSetting] = "weft-compiled-runtime:latest",
            [ReleaseCommands.ReleaseWriter] = "Host=127.0.0.1;Port=1;Username=weft_release_writer;Password=p"
        }, ["register-release-target", .. Target()]);
        Assert.Equal(2, mutableImage.ExitCode);
        // Qualification reads the endpoint's runtime selection first: an incomplete service runtime, or settings of two
        // runtimes at once, stop it before any login is used.
        var qualify = new Dictionary<string, string>(ReleaseCommands.QualificationLogins.ToDictionary(login => login.Setting,
            login => $"Host=127.0.0.1;Port=1;Username={login.Role};Password=p"))
        {
            [ReleaseCommands.RuntimeImageSetting] = "sha256:" + new string('a', 64),
            [ReleaseCommands.CompilerImageSetting] = "sha256:" + new string('b', 64)
        };
        string[] Qualify() => ["qualify-build", Ids[0], Ids[1], Ids[2], Ids[3], Ids[4], Ids[5]];
        foreach (var runtime in new Dictionary<string, string>[]
        {
            new() { ["WEFT_FERRULE_RUNNER"] = "service" },
            new() { ["WEFT_FERRULE_RUNNER"] = "service", ["WEFT_FERRULE_COMPILE_TOOLCHAIN"] = "sha256:" + new string('c', 64),
                ["WEFT_FERRULE_SERVICE_URL"] = "https://ferrule:8443" },
            new() { ["WEFT_FERRULE_SERVICE_URL"] = "https://ferrule:8443" },
            new() { ["WEFT_FERRULE_RUNNER"] = "docker", ["WEFT_FERRULE_COMPILE_TOOLCHAIN"] = "sha256:" + new string('c', 64) },
            new() { ["WEFT_FERRULE_RUNNER"] = "worker" }
        })
        {
            var refused = await RunAsync(new Dictionary<string, string>(qualify.Concat(runtime)), Qualify());
            Assert.Equal((2, "", "{\"error\":\"configurationInvalid\"}"), (refused.ExitCode, refused.Output, refused.Error.Trim()));
        }
    }

    [PostgresFact]
    public async Task A_named_principal_registers_activates_rolls_back_and_disables_through_the_tool()
    {
        await using var test = await LocalReleasePersistenceTests.Context.CreateAsync(fixture);
        var reference = test.Target.Reference;
        var settings = new Dictionary<string, string>
        {
            [ReleaseCommands.RuntimeImageSetting] = test.Target.RuntimeImage,
            [ReleaseCommands.ReleaseWriter] = test.Case.RegressionConnectionString("weft_release_writer"),
            [ReleaseCommands.QualificationReader] = test.Case.RegressionConnectionString("weft_qualification_reader")
        };
        string[] Scope(string command, Guid actor, ReleaseTargetReference? target = null)
        {
            var selected = target ?? reference;
            return
            [
                command, actor.ToString("D"), selected.Mapping.OrganizationId.ToString("D"), selected.Mapping.ProjectId.ToString("D"),
                selected.Mapping.MappingId.ToString("D"), selected.Environment.EnvironmentId.ToString("D"),
                selected.Environment.Version.ToString(CultureInfo.InvariantCulture), selected.TargetId.ToString("D")
            ];
        }
        var actor = test.Actor.PrincipalId.Value;
        var qualification = test.First.Id.ToString("D");

        // Registration names the Ferrule artifact profile, which the target's build environment must use, and the
        // configured runtime image; a replay returns the original registration.
        var environment = new BuildEnvironmentReference(new(reference.Mapping.OrganizationId, reference.Mapping.ProjectId), Guid.NewGuid(), 1);
        Assert.Equal(BuildControlOutcome.Created, await test.Control.Store.SaveEnvironmentAsync(test.Actor, new(environment, FerruleBuildCompiler.Profile, true)));
        var target = new ReleaseTargetReference(reference.Mapping, environment, Guid.NewGuid());
        Assert.Equal((0, "registered"), Outcome(await RunAsync(settings, Scope("register-release-target", actor, target))));
        Assert.Equal((0, "replayed"), Outcome(await RunAsync(settings, Scope("register-release-target", actor, target))));
        var registered = (await test.Store.ReadTargetAsync(test.Actor, target))!;
        Assert.Equal(FerruleBuildCompiler.Profile, registered.Target.Profile);
        Assert.Equal(test.Target.RuntimeImage, registered.Target.RuntimeImage);
        Assert.Equal((3, "refused"), Outcome(await RunAsync(settings,
            Scope("register-release-target", actor, new(reference.Mapping, reference.Environment, Guid.NewGuid())))));
        // A qualification from another environment and artifact profile cannot activate there.
        Assert.Equal((3, "refused"), Outcome(await RunAsync(settings, [.. Scope("activate-release", actor, target), Guid.NewGuid().ToString("D"), qualification, "none"])));

        var first = Guid.NewGuid();
        Assert.Equal((0, "activated"), Outcome(await RunAsync(settings, [.. Scope("activate-release", actor), first.ToString("D"), qualification, "none"])));
        Assert.Equal((0, "replayed"), Outcome(await RunAsync(settings, [.. Scope("activate-release", actor), first.ToString("D"), qualification, "none"])));
        // Another principal without the publisher's authority, and a stale expected pointer, change nothing.
        Assert.Equal(3, (await RunAsync(settings, [.. Scope("activate-release", Guid.NewGuid()), Guid.NewGuid().ToString("D"), qualification, first.ToString("D")])).ExitCode);
        var stale = Outcome(await RunAsync(settings, [.. Scope("activate-release", actor), Guid.NewGuid().ToString("D"), qualification, "none"]));
        Assert.Equal(3, stale.ExitCode);
        Assert.Equal(first, (await test.Store.ReadTargetAsync(test.Actor, reference))!.ActiveReleaseId);

        var second = Guid.NewGuid();
        Assert.Equal((0, "activated"), Outcome(await RunAsync(settings, [.. Scope("activate-release", actor), second.ToString("D"), qualification, first.ToString("D")])));
        var rollback = Guid.NewGuid();
        var rolledBack = await RunAsync(settings, [.. Scope("rollback-release", actor), rollback.ToString("D"), first.ToString("D"), second.ToString("D")]);
        Assert.Equal((0, "activated"), Outcome(rolledBack));
        using (var line = JsonDocument.Parse(rolledBack.Output))
            Assert.Equal(qualification, line.RootElement.GetProperty("qualificationId").GetString());
        var current = (await test.Store.ReadAsync(test.Actor, reference, rollback))!;
        Assert.Equal((ReleaseAction.Rollback, first), (current.Intent.Action, current.Intent.RollbackOfReleaseId));
        Assert.Equal(rollback, (await test.Store.ReadTargetAsync(test.Actor, reference))!.ActiveReleaseId);
        // A rollback source must be an existing release of this target.
        Assert.Equal((3, "refused"), Outcome(await RunAsync(settings, [.. Scope("rollback-release", actor), Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), rollback.ToString("D")])));

        Assert.Equal((0, "disabled"), Outcome(await RunAsync(settings, Scope("disable-release-target", actor))));
        Assert.Equal((3, "refused"), Outcome(await RunAsync(settings, [.. Scope("activate-release", actor), Guid.NewGuid().ToString("D"), qualification, rollback.ToString("D")])));
        Assert.False((await test.Store.ReadTargetAsync(test.Actor, reference))!.Enabled);
    }

    private static string[] Target() => [Ids[0], Ids[1], Ids[2], Ids[3], Ids[4], "2", Ids[8]];

    private static (int ExitCode, string Outcome) Outcome(ToolResult result)
    {
        using var line = JsonDocument.Parse(result.Output);
        return (result.ExitCode, line.RootElement.GetProperty("outcome").GetString()!);
    }

    private sealed record ToolResult(int ExitCode, string Output, string Error);

    private static async Task<ToolResult> RunAsync(IReadOnlyDictionary<string, string> settings, string[] arguments)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "ReleaseTool", "Weft.ReleaseTool.dll");
        Assert.True(File.Exists(dll), "The release tool must be copied into the test output.");
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrWhiteSpace(host)) host = Environment.ProcessPath is { } current && Path.GetFileName(current) == "dotnet" ? current : "dotnet";
        var start = new ProcessStartInfo(host) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(dll);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("WEFT_", StringComparison.Ordinal)).ToArray())
            start.Environment.Remove(name);
        foreach (var (name, value) in settings) start.Environment[name] = value;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The release tool did not start.");
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
