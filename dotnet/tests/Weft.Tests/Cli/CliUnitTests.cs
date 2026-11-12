using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;
using Weft.Cli;
using Weft.Cli.Commands;
using Weft.Cli.Configuration;
using Weft.Cli.Http;
using Weft.Cli.Output;
using Weft.Contracts.Catalog;
using Weft.Contracts.Chat;

namespace Weft.Tests.Cli;

public sealed class CliUnitTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("weft-cli-unit-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
    }

    private static CliProfile Profile(string name = "dev", string server = "https://weft.example.test") => new(name, CliProfile.ParseServer(server),
        Guid.NewGuid(), Guid.NewGuid(), "api://" + Guid.NewGuid().ToString("D") + "/weft.api");

    [Fact]
    public async Task ParseErrorsAndUnknownCommandsExitWithTheUsageCode()
    {
        var environment = new CliEnvironment
        {
            Out = new StringWriter(),
            Error = new StringWriter(),
            OpenStandardInput = () => new MemoryStream(),
            OpenStandardOutput = () => new MemoryStream(),
            ConfigurationDirectory = directory,
            TokenCaches = new CliMemoryTokenCaches()
        };
        Assert.Equal((int)CliExitCode.Usage, await CliApplication.RunAsync(["projects", "frobnicate"], environment));
        Assert.Equal((int)CliExitCode.Usage, await CliApplication.RunAsync(["reports", "projection", "ensure", "--timeout", "soon"], environment));
        Assert.Equal((int)CliExitCode.Usage, await CliApplication.RunAsync(["projects", "list"], environment));
        Assert.Contains("No profile is configured", environment.Error.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, await CliApplication.RunAsync(["--help"], environment));
        Assert.Contains("chat", environment.Out.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("90s", 90)]
    [InlineData("10m", 600)]
    [InlineData("2h", 7200)]
    public void DurationsParse(string text, int seconds) => Assert.Equal(TimeSpan.FromSeconds(seconds), CommandSupport.ParseDuration(text));

    [Theory]
    [InlineData("")]
    [InlineData("5")]
    [InlineData("0s")]
    [InlineData("-5s")]
    [InlineData("25h")]
    [InlineData("1d")]
    public void InvalidDurationsAreRefused(string text) => Assert.Null(CommandSupport.ParseDuration(text));

    [Fact]
    public void IdentifiersAreCanonicalizedAndInvalidOnesRefused()
    {
        Assert.Equal("0a1b2c3d-0000-4000-8000-00000000000f", CliInvocation.Id("0A1B2C3D-0000-4000-8000-00000000000F", "--project"));
        Assert.Equal(CliExitCode.Usage, Assert.Throws<CliException>(() => CliInvocation.Id("00000000-0000-0000-0000-000000000000", "--project")).Code);
        Assert.Equal(CliExitCode.Usage, Assert.Throws<CliException>(() => CliInvocation.Id("{0a1b2c3d-0000-4000-8000-00000000000f}", "--project")).Code);
        Assert.Equal(CliExitCode.Usage, Assert.Throws<CliException>(() => CliInvocation.Id(null, "--project")).Code);
    }

    [Fact]
    public void DefaultBuildIdentityIsAStableDomainSeparatedUuid()
    {
        Assert.Equal("c786658f-7fa6-8fea-b6eb-362659ec2785", BuildCommands.BuildForApproval("11111111-1111-4111-8111-111111111111"));
    }

    [Fact]
    public void FullPaginationRefusesCyclesAndTruncationButAllowsTheLastPageAtTheLimit()
    {
        var cycle = new PageTraversal("initial");
        Assert.True(cycle.Continue(true, "second"));
        Assert.Equal("invalidPagination", Assert.Throws<CliException>(() => cycle.Continue(true, "initial")).Category);
        var tooMany = new PageTraversal();
        var complete = new PageTraversal();
        for (var page = 1; page < PageTraversal.MaximumPages; page++)
        {
            Assert.True(tooMany.Continue(true, page.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            Assert.True(complete.Continue(true, page.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        Assert.Equal(CliExitCode.Transport, Assert.Throws<CliException>(() => tooMany.Continue(true, "next")).Code);
        Assert.False(complete.Continue(true, null));
        Assert.False(new PageTraversal("same").Continue(false, "same"));
    }

    [Fact]
    public void PreviewParametersAndChatSelectorsParse()
    {
        Assert.Equal(new CatalogPreviewParameterV1("limit", "integer", "5"), PreviewCommands.Parameter("limit:integer=5"));
        Assert.Equal(new CatalogPreviewParameterV1("region", "string", "a=b"), PreviewCommands.Parameter("region=a=b"));
        Assert.Throws<CliException>(() => PreviewCommands.Parameter("=x"));
        Assert.Throws<CliException>(() => PreviewCommands.Parameter("x:date=1"));
        Assert.Equal(new ChatSelectorV1(ChatSelectorKindV1.Field, "f1", null), ChatCommands.Selector("field=f1"));
        Assert.Equal(new ChatSelectorV1(ChatSelectorKindV1.ValidationDiagnostic, null, 3), ChatCommands.Selector("validationDiagnostic=3"));
        Assert.Equal(new ChatSelectorV1(ChatSelectorKindV1.ProjectionReport, null, null), ChatCommands.Selector("projectionReport"));
        Assert.Throws<CliException>(() => ChatCommands.Selector("field"));
        Assert.Throws<CliException>(() => ChatCommands.Selector("projectionDiagnostic=-1"));
        Assert.Throws<CliException>(() => ChatCommands.Selector("7=x"));
        Assert.Throws<CliException>(() => ChatCommands.Selector("unknown=x"));
    }

    [Fact]
    public void OutputNeutralizesTerminalControlAndWithholdsBrowserRequestTokens()
    {
        Assert.Equal("a�[31mb�c�d", CliOutput.Safe("a\u001b[31mb\u0007c‮d"));
        Assert.Equal("line\nnext\ttab�", ChatCommands.SafeAnswer("line\nnext\ttab\r"));
        var writer = new StringWriter();
        var output = new CliOutput(writer, json: true);
        output.WriteJson(new CatalogContextV1("development", Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), "Org",
            CatalogFeaturesV1.ReadOnly, CatalogOrganizationActionsV1.None, "browser-request-token"));
        var json = JsonNode.Parse(writer.ToString())!.AsObject();
        Assert.False(json.ContainsKey("requestToken"));
        Assert.DoesNotContain("browser-request-token", writer.ToString(), StringComparison.Ordinal);
        var table = new StringWriter();
        new CliOutput(table, json: false).Table(["A", "LONGER"], [["x", "1"], ["yyyy", null]]);
        Assert.Equal("A     LONGER\nx     1\nyyyy  -\n", table.ToString().ReplaceLineEndings("\n"));
    }

    [Fact]
    public void DiagnosticTextIsRedacted()
    {
        var jwt = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJ4In0.c2lnbmF0dXJl";
        Assert.DoesNotContain(jwt, CliRedaction.Text("token " + jwt + " leaked"), StringComparison.Ordinal);
        Assert.Equal("Authorization: Bearer [redacted]", CliRedaction.Text("Authorization: Bearer abcdefghijklmnop"));
        Assert.Equal("refresh_token=[redacted]&scope=x", CliRedaction.Text("refresh_token=0.AAAA-secret-value&scope=x"));
        Assert.Equal("code=[redacted]", CliRedaction.Text("code=abc123"));
        Assert.Equal("plain message", CliRedaction.Text("plain message"));
    }

    [Theory]
    [InlineData(400, null, 9)]
    [InlineData(401, "identityRejected", 4)]
    [InlineData(403, "identityRejected", 4)]
    [InlineData(403, "permissionDenied", 5)]
    [InlineData(404, "notFound", 6)]
    [InlineData(404, "unavailable", 11)]
    [InlineData(409, "idempotencyConflict", 7)]
    [InlineData(412, "preconditionFailed", 8)]
    [InlineData(413, "bodyLimit", 9)]
    [InlineData(415, "unsupportedMediaType", 9)]
    [InlineData(422, null, 9)]
    [InlineData(428, "preconditionRequired", 8)]
    [InlineData(429, "rateLimited", 10)]
    [InlineData(500, "internalFailure", 12)]
    [InlineData(503, "unavailable", 11)]
    [InlineData(302, null, 13)]
    public void ResponseStatusesMapToStableExitCodes(int status, string? code, int expected)
    {
        var failure = ApiResponse.Failure(status, code);
        Assert.Equal(expected, (int)failure.Code);
        Assert.Contains($"HTTP {status}", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExitCodesAreStable()
    {
        int[] expected = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 130];
        Assert.Equal(expected, Enum.GetValues<CliExitCode>().Select(code => (int)code).ToArray());
    }

    [Fact]
    public async Task ErrorBodiesAreNeverEchoedAndUnknownCategoriesAreNotTrusted()
    {
        using var response = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.Conflict)
        {
            Content = new System.Net.Http.StringContent("{\"code\":\"secret-body-marker\"}", Encoding.UTF8, "application/json")
        };
        var failure = await new ApiResponse(response, ApiOperations.GetCatalogContext).FailureAsync(CancellationToken.None);
        Assert.Equal(CliExitCode.Conflict, failure.Code);
        Assert.DoesNotContain("secret-body-marker", failure.Message, StringComparison.Ordinal);
        using var chat = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.Conflict)
        {
            Content = new System.Net.Http.StringContent("{\"code\":\"turnActive\"}", Encoding.UTF8, "application/json")
        };
        Assert.Equal("turnActive", (await new ApiResponse(chat, ApiOperations.CreateChatTurn).FailureAsync(CancellationToken.None)).Category);
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public void ProfilesAreStoredOwnerOnlyAndStrictlyValidated()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var store = new CliConfigurationStore(Path.Combine(directory, "config"));
        var profile = Profile();
        store.Save(new CliConfiguration("dev", new SortedDictionary<string, CliProfile>(StringComparer.Ordinal) { ["dev"] = profile }));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(store.Path));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(store.Directory));
        var loaded = store.Load();
        Assert.Equal(profile, loaded.Profiles["dev"]);
        Assert.Equal(new Uri("https://login.microsoftonline.com/" + profile.TenantId.ToString("D")), loaded.Profiles["dev"].Authority);
        Assert.Equal(profile, CliConfigurationStore.Select(loaded, null));
        Assert.Throws<CliException>(() => CliConfigurationStore.Select(loaded, "missing"));
        Assert.DoesNotContain("token", File.ReadAllText(store.Path), StringComparison.OrdinalIgnoreCase);

        File.SetUnixFileMode(store.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        Assert.Contains("accessible to other users", Assert.Throws<CliException>(store.Load).Message, StringComparison.Ordinal);
        File.SetUnixFileMode(store.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var text = File.ReadAllText(store.Path);
        File.WriteAllText(store.Path, text.Replace("\"scope\"", "\"accessToken\": \"x\", \"scope\"", StringComparison.Ordinal));
        Assert.Equal(CliExitCode.Usage, Assert.Throws<CliException>(store.Load).Code);
        File.WriteAllText(store.Path, text.Replace("\"version\": 1", "\"version\": 2", StringComparison.Ordinal));
        Assert.Throws<CliException>(store.Load);
        File.SetUnixFileMode(store.Directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherExecute);
        Assert.Throws<CliException>(() => OwnerOnlyFiles.EnsureDirectory(store.Directory));
    }

    [Theory]
    [InlineData("http://weft.example.test")]
    [InlineData("https://weft.example.test/api")]
    [InlineData("https://user@weft.example.test")]
    [InlineData("https://weft.example.test/?x=1")]
    [InlineData("ftp://weft.example.test")]
    [InlineData("weft.example.test")]
    public void ProfilesRefuseServersThatAreNotHttpsOrigins(string server) => Assert.Throws<CliException>(() => CliProfile.ParseServer(server));

    [Fact]
    public void ProfilesAcceptLoopbackHttpAndRefuseDefaultScopesAndBadNames()
    {
        Assert.Equal("http://127.0.0.1:4100/", CliProfile.ParseServer("http://127.0.0.1:4100").AbsoluteUri);
        Assert.Equal("http://localhost:4100/", CliProfile.ParseServer("http://localhost:4100").AbsoluteUri);
        Assert.Throws<CliException>(() => new CliProfile("dev", new Uri("https://a.test/"), Guid.NewGuid(), Guid.NewGuid(), "api://x/.default"));
        Assert.Throws<CliException>(() => new CliProfile("dev", new Uri("https://a.test/"), Guid.NewGuid(), Guid.NewGuid(), "two scopes"));
        Assert.Throws<CliException>(() => new CliProfile("Dev", new Uri("https://a.test/"), Guid.NewGuid(), Guid.NewGuid(), "api://x/weft.api"));
        Assert.Throws<CliException>(() => new CliProfile("dev", new Uri("https://a.test/"), Guid.Empty, Guid.NewGuid(), "api://x/weft.api"));
    }

    [Fact]
    public async Task ServerSentEventsAreFramedByTheEventStreamRules()
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(": comment\r\nid: 1\r\nevent: a\r\ndata: one\r\ndata: two\r\n\r\nid: 2\ndata:three\n\nevent: x\n\ndata: tail"));
        var reader = new ServerSentEventReader(stream);
        Assert.Equal(new ServerSentEvent("1", "a", "one\ntwo"), await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(new ServerSentEvent("2", "message", "three"), await reader.ReadAsync(CancellationToken.None));
        Assert.Null(await reader.ReadAsync(CancellationToken.None));
        var huge = new ServerSentEventReader(new MemoryStream(Encoding.UTF8.GetBytes("data: " + new string('x', ServerSentEventReader.MaximumLineBytes + 1))));
        Assert.Equal(CliExitCode.Transport, (await Assert.ThrowsAsync<CliException>(() => huge.ReadAsync(CancellationToken.None))).Code);
    }

    [Fact]
    public async Task TheProfileSelectionPrefersTheOptionThenTheVariableThenTheDefault()
    {
        // Let the real owner-only helper create this missing child with its
        // private ACL; the general temporary directory can inherit wider access.
        var configurationDirectory = Path.Combine(directory, "profile-selection");
        var store = new CliConfigurationStore(configurationDirectory);
        var profiles = new SortedDictionary<string, CliProfile>(StringComparer.Ordinal) { ["a"] = Profile("a"), ["b"] = Profile("b"), ["c"] = Profile("c") };
        store.Save(new CliConfiguration("a", profiles));
        async Task<string> Selected(string? variable, params string[] args)
        {
            var output = new StringWriter();
            var environment = new CliEnvironment
            {
                Out = output,
                Error = new StringWriter(),
                OpenStandardInput = () => new MemoryStream(),
                OpenStandardOutput = () => new MemoryStream(),
                ConfigurationDirectory = configurationDirectory,
                TokenCaches = new CliMemoryTokenCaches(),
                ProfileVariable = variable
            };
            Assert.Equal(0, await CliApplication.RunAsync(["profile", "show", "--json", .. args], environment));
            return JsonNode.Parse(output.ToString())!["name"]!.GetValue<string>();
        }
        Assert.Equal("a", await Selected(null));
        Assert.Equal("b", await Selected("b"));
        Assert.Equal("c", await Selected("b", "--profile", "c"));
    }

    /// <summary>
    /// The published CLI as a separate process with no Secret Service session bus: it refuses to sign in before any network
    /// request and leaves no token file behind.
    /// </summary>
    [Fact]
    public async Task TheBuiltCliRefusesToSignInWithoutAnOperatingSystemProtectedStore()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var configuration = Path.Combine(directory, "process");
        async Task<(int Exit, string Error)> Run(params string[] args)
        {
            var start = new ProcessStartInfo("dotnet") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "weft.dll"));
            foreach (var arg in args)
                start.ArgumentList.Add(arg);
            start.Environment["WEFT_CONFIG_DIR"] = configuration;
            start.Environment["DBUS_SESSION_BUS_ADDRESS"] = "unix:path=" + Path.Combine(directory, "no-bus");
            start.Environment.Remove("XDG_RUNTIME_DIR");
            start.Environment.Remove("WEFT_PROFILE");
            using var process = Process.Start(start)!;
            var error = process.StandardError.ReadToEndAsync();
            _ = await process.StandardOutput.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, await error);
        }
        // A server and tenant that do not exist: nothing may be contacted.
        var saved = await Run("profile", "set", "dev", "--server", "https://weft.invalid", "--tenant", Guid.NewGuid().ToString("D"),
            "--client-id", Guid.NewGuid().ToString("D"), "--scope", "api://" + Guid.NewGuid().ToString("D") + "/weft.api");
        Assert.True(saved.Exit == 0, saved.Error);
        var login = await Run("login");
        Assert.True(login.Exit == (int)CliExitCode.ProtectedStoreUnavailable, $"exit {login.Exit}: {login.Error}");
        Assert.Contains("does not store sign-in tokens unprotected", login.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("To sign in", login.Error, StringComparison.Ordinal);
        Assert.All(Directory.GetFiles(configuration), file => Assert.Equal("config.json", Path.GetFileName(file)));
    }
}
