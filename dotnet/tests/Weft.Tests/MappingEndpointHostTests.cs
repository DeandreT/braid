using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Weft.Application;
using Weft.MachineEndpoint;

namespace Weft.Tests;

public sealed class MappingEndpointLinuxFactAttribute : FactAttribute
{
    public MappingEndpointLinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "Protected endpoint credential delivery requires Linux qualification.";
    }
}

/// <summary>The published endpoint process: configuration admission, readiness, refusal without a token and clean stop.</summary>
[SupportedOSPlatform("linux")]
public sealed class MappingEndpointHostTests
{
    [Fact]
    public void The_request_document_admits_one_unnamed_primary_named_inputs_and_typed_parameters_only()
    {
        var request = MappingEndpointRoutes.Parse(Encoding.UTF8.GetBytes("""
            {"inputs":[{"content":"e30="},{"name":"lookup","content":"WzFd"}],"parameters":[{"name":"mode","kind":"string","value":"full"}]}
            """));
        Assert.Null(request.Primary.Name);
        Assert.Equal("lookup", Assert.Single(request.NamedInputs).Name);
        Assert.Equal(PreviewParameterKind.String, Assert.Single(request.Parameters).Kind);
        foreach (var body in new[]
        {
            """{"inputs":[]}""", """{"inputs":[{"name":"first","content":"e30="}]}""", """{"inputs":[{"content":"e30="},{"content":"e30="}]}""",
            """{"inputs":[{"content":""}]}""", """{"inputs":[{"content":"e30="}],"extra":1}""", """{"inputs":[{"content":"e30=","content":"e30="}]}""",
            """{"inputs":[{"content":"not base64"}]}""", """{"inputs":[{"content":"e30="}],"parameters":[{"name":"mode","kind":"date","value":"x"}]}""",
            """{"inputs":[{"content":"e30="},{"name":"a","content":"e30="},{"name":"a","content":"e30="}]}""", "[]"
        })
        {
            // Each refusal is one the route answers as 400, never an unexpected failure.
            var error = Record.Exception(() => MappingEndpointRoutes.Parse(Encoding.UTF8.GetBytes(body)));
            Assert.True(error is System.Text.Json.JsonException or FormatException or ArgumentException or InvalidOperationException, body);
        }
    }

    [MappingEndpointLinuxFact]
    public async Task Invalid_configuration_stops_the_process_with_a_fixed_line_and_no_values()
    {
        var root = Directory.CreateTempSubdirectory("weft-endpoint-");
        try
        {
            using var files = new MappingEndpointLiveTests.PrivateFiles(root.FullName);
            var read = files.Environment(Settings(MappingEndpointLiveTests.FreePort()), Connections());
            Assert.NotNull(MappingEndpointSettings.Read(read));
            foreach (var (name, value) in new (string, string?)[]
            {
                (MappingEndpointSettings.Transport, "anywhere"), (MappingEndpointSettings.Concurrency, "5"), (MappingEndpointSettings.Listen, "0.0.0.0:0"),
                (MappingEndpointSettings.TenantSetting, "9188040d-6c67-4c5b-b112-36a304b66dad"), (MappingEndpointSettings.RuntimeImageSetting, "latest"),
                ("WEFT_ENDPOINT_TARGET_ID", Guid.Empty.ToString("D")), ("WEFT_ENDPOINT_RELEASE_READER_CONNECTION_STRING", "Username=weft_release_reader;Password=x"),
                ("PGPASSWORD", "x"), ("DOTNET_STARTUP_HOOKS", "/hook.dll"), ("WEFT_ENDPOINT_ENVIRONMENT_VERSION", "01")
            })
                Assert.Throws<InvalidOperationException>(() => MappingEndpointSettings.Read(key => key == name ? value : read(key)));
            // A credential file naming another login, or shared between two settings, is refused.
            var swapped = files.Environment(Settings(MappingEndpointLiveTests.FreePort()), Connections(("weft_release_reader", "weft_build_reader")));
            Assert.Throws<InvalidOperationException>(() => MappingEndpointSettings.Read(swapped));
            Assert.Throws<InvalidOperationException>(() => MappingEndpointSettings.Read(key =>
                key == "WEFT_ENDPOINT_CASE_READER_CONNECTION_STRING_FILE" ? read("WEFT_ENDPOINT_RUN_READER_CONNECTION_STRING_FILE") : read(key)));
            File.SetUnixFileMode(read("WEFT_ENDPOINT_CASE_READER_CONNECTION_STRING_FILE")!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            Assert.ThrowsAny<Exception>(() => MappingEndpointSettings.Read(read));

            using var process = Start(new Dictionary<string, string>());
            Assert.True(process.WaitForExit(TimeSpan.FromMinutes(2)));
            Assert.Equal(1, process.ExitCode);
            Assert.Equal("", await process.StandardOutput.ReadToEndAsync());
            Assert.Equal("WeftMappingEndpointConfigurationFailed", (await process.StandardError.ReadToEndAsync()).Trim());
        }
        finally { root.Delete(recursive: true); }
    }

    [MappingEndpointLinuxFact]
    public async Task The_published_host_reports_readiness_refuses_unauthenticated_calls_and_stops_cleanly()
    {
        var root = Directory.CreateTempSubdirectory("weft-endpoint-");
        try
        {
            using var files = new MappingEndpointLiveTests.PrivateFiles(root.FullName);
            var port = MappingEndpointLiveTests.FreePort();
            var settings = Settings(port);
            var read = files.Environment(settings, Connections());
            var environment = new Dictionary<string, string>(settings);
            foreach (var (setting, _) in MappingEndpointSettings.Logins) environment[setting + "_FILE"] = read(setting + "_FILE")!;
            using var process = Start(environment);
            try
            {
                var ready = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromMinutes(2));
                Assert.Equal("WeftMappingEndpointReady", ready);
                // The container health probe reaches both routes on the loopback listener, also from a wildcard
                // setting, and ignores the base image's ambient listener port.
                var probe = new Dictionary<string, string>
                {
                    [MappingEndpointSettings.Listen] = "127.0.0.1:" + port,
                    [MappingEndpointSettings.Transport] = "platformIngress",
                    ["ASPNETCORE_HTTP_PORTS"] = "8080"
                };
                Assert.Equal(0, await ProbeAsync(probe, "probe", "live"));
                Assert.Equal(0, await ProbeAsync(probe, "probe", "ready"));
                Assert.Equal(0, await ProbeAsync(new Dictionary<string, string>(probe) { [MappingEndpointSettings.Listen] = "0.0.0.0:" + port }, "probe", "ready"));
                using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + port), Timeout = TimeSpan.FromSeconds(30) };
                Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync(MappingEndpointRoutes.LivePath)).StatusCode);
                Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync(MappingEndpointRoutes.ReadyPath)).StatusCode);
                Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/")).StatusCode);
                Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.GetAsync(MappingEndpointRoutes.InvocationsPath)).StatusCode);
                foreach (var authorization in new[] { null, "Basic dXNlcjpwYXNz", "Bearer not.a.jwt.token", "Bearer " })
                {
                    using var message = new HttpRequestMessage(HttpMethod.Post, MappingEndpointRoutes.InvocationsPath)
                    { Content = new ByteArrayContent(MappingEndpointLiveTests.Body("{}"u8.ToArray())) };
                    message.Content.Headers.ContentType = new("application/json");
                    if (authorization is not null) message.Headers.TryAddWithoutValidation("Authorization", authorization);
                    message.Headers.Add(MappingEndpointRoutes.RequestIdHeader, Guid.NewGuid().ToString("D"));
                    var response = await client.SendAsync(message);
                    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                    Assert.Equal("{\"outcome\":\"unauthorized\"}", await response.Content.ReadAsStringAsync());
                    Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
                }
                Assert.Equal(0, Signal(process.Id, 15));
                Assert.True(process.WaitForExit(TimeSpan.FromMinutes(2)));
                Assert.Equal(0, process.ExitCode);
                Assert.Equal(1, await ProbeAsync(probe, "probe", "ready"));
                Assert.Equal("", (await process.StandardOutput.ReadToEndAsync()).Trim());
                Assert.Equal("", (await process.StandardError.ReadToEndAsync()).Trim());
            }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        }
        finally { root.Delete(recursive: true); }
    }

    [MappingEndpointLinuxFact]
    public async Task The_probe_refuses_other_arguments_and_incomplete_listener_settings()
    {
        var listener = new Dictionary<string, string>
        {
            [MappingEndpointSettings.Listen] = "127.0.0.1:" + MappingEndpointLiveTests.FreePort(),
            [MappingEndpointSettings.Transport] = "platformIngress"
        };
        // Nothing listens: the probe fails without a configuration failure.
        Assert.Equal(1, await ProbeAsync(listener, "probe", "live"));
        Assert.Equal(2, await ProbeAsync(listener, "probe", "started"));
        Assert.Equal(2, await ProbeAsync(listener, "serve"));
        Assert.Equal(2, await ProbeAsync(listener, "probe", "live", "again"));
        Assert.Equal(2, await ProbeAsync(new Dictionary<string, string>(), "probe", "live"));
        Assert.Equal(2, await ProbeAsync(new Dictionary<string, string>(listener) { [MappingEndpointSettings.Listen] = "127.0.0.1:0" }, "probe", "live"));
        Assert.Equal(2, await ProbeAsync(new Dictionary<string, string>(listener) { [MappingEndpointSettings.Listen] = "localhost:8080" }, "probe", "live"));
        // A direct HTTPS probe pins the host's own certificate, which it must be able to read.
        Assert.Equal(2, await ProbeAsync(new Dictionary<string, string>(listener) { [MappingEndpointSettings.Transport] = "directHttps" }, "probe", "live"));
    }

    private static async Task<int> ProbeAsync(IReadOnlyDictionary<string, string> environment, params string[] arguments)
    {
        using var process = Start(environment, arguments);
        try
        {
            Assert.True(process.WaitForExit(TimeSpan.FromMinutes(1)));
            // The probe prints no value; only a usage or configuration line on refusal.
            Assert.Equal("", await process.StandardOutput.ReadToEndAsync());
            var error = (await process.StandardError.ReadToEndAsync()).Trim();
            Assert.True(process.ExitCode == 2 ? error.Length > 0 : error.Length == 0, error);
            return process.ExitCode;
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }

    private static Dictionary<string, string> Settings(int port) => new()
    {
        [MappingEndpointSettings.Listen] = "127.0.0.1:" + port,
        [MappingEndpointSettings.Transport] = "platformIngress",
        ["WEFT_ENDPOINT_ORGANIZATION_ID"] = Guid.NewGuid().ToString("D"),
        ["WEFT_ENDPOINT_PROJECT_ID"] = Guid.NewGuid().ToString("D"),
        ["WEFT_ENDPOINT_MAPPING_ID"] = Guid.NewGuid().ToString("D"),
        ["WEFT_ENDPOINT_ENVIRONMENT_ID"] = Guid.NewGuid().ToString("D"),
        ["WEFT_ENDPOINT_ENVIRONMENT_VERSION"] = "1",
        ["WEFT_ENDPOINT_TARGET_ID"] = Guid.NewGuid().ToString("D"),
        [MappingEndpointSettings.TenantSetting] = Guid.NewGuid().ToString("D"),
        [MappingEndpointSettings.AudienceSetting] = Guid.NewGuid().ToString("D"),
        [MappingEndpointSettings.RuntimeImageSetting] = "sha256:" + new string('a', 64),
        [MappingEndpointSettings.CompilerImageSetting] = "sha256:" + new string('b', 64)
    };

    // Unreachable database addresses: startup opens no connection, and the purge fails closed.
    private static Dictionary<string, string> Connections(params (string Role, string Username)[] overrides) =>
        MappingEndpointSettings.Logins.ToDictionary(login => login.Setting, login =>
            $"Host=127.0.0.1;Port=1;Database=weft;Username={overrides.FirstOrDefault(item => item.Role == login.Role).Username ?? login.Role};Password=p-{Guid.NewGuid():N}");

    private static Process Start(IReadOnlyDictionary<string, string> environment, params string[] arguments)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "MappingEndpoint", "weft-mapping-endpoint.dll");
        Assert.True(File.Exists(dll), "The mapping endpoint must be copied into the test output.");
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrWhiteSpace(host)) host = Environment.ProcessPath is { } current && Path.GetFileName(current) == "dotnet" ? current : "dotnet";
        var start = new ProcessStartInfo(host) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
        start.ArgumentList.Add(dll);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        var inherited = new[] { "PATH", "DOTNET_ROOT", "HOME", "LANG" }.Where(start.Environment.ContainsKey).ToDictionary(key => key, key => start.Environment[key]);
        start.Environment.Clear();
        foreach (var (key, value) in inherited) start.Environment[key] = value;
        start.Environment["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "false";
        start.Environment["DOTNET_NOLOGO"] = "1";
        foreach (var (key, value) in environment) start.Environment[key] = value;
        return Process.Start(start) ?? throw new InvalidOperationException("The endpoint process did not start.");
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)] private static extern int Signal(int pid, int signal);
}
