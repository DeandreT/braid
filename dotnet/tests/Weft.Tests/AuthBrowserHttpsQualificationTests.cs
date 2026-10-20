using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Weft.BrowserQualification;
using Weft.Identity;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

/// <summary>Real-browser cases need an explicit admission record. CI always prepares one, so they never skip there.</summary>
public sealed class OwnedHttpsBrowserTheoryAttribute : TheoryAttribute
{
    public OwnedHttpsBrowserTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("WEFT_AUTH_BROWSER_ADMISSION_FILE") is null && Environment.GetEnvironmentVariable("CI") != "true")
            Skip = "Run tools/auth-browser-https/admit.mjs --approved with WEFT_AUTH_BROWSER_ADMISSION_ROOT/FILE to execute owned HTTPS browser qualification.";
    }
}

[Collection(PostgresCollection.Name)]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed partial class AuthBrowserHttpsQualificationTests
{
    [OwnedHttpsBrowserTheory]
    [InlineData("positive")]
    [InlineData("tokens")]
    [InlineData("state")]
    [InlineData("correlation")]
    [InlineData("nonce")]
    [InlineData("replay")]
    public async Task ActualOwnedHttpsBrowserFlow(string scenario)
    {
        // Deliberately not PostgresFact: an admitted run with missing tools/database fails, never skips.
        QualificationWire.Require(PostgresFixture.IsConfigured);
        await AuthBrowserHttpsCase.RunAsync(scenario);
    }

}

[System.Runtime.Versioning.SupportedOSPlatform("linux")]
internal sealed class AuthBrowserHttpsCase
{
    private static readonly List<AuthBrowserHttpsCase> Retained = [];
    private QualificationAdmission admission = null!;
    private BrowserQualificationConnectProxy? proxy;
    private readonly QualificationProbeLedger<QualificationChild> probeOwners = new();
    private readonly QualificationParentStopOwner stopOwner = new();
    private PostgresCatalogTestSupport? data;
    private ExternalIdentityBindingProvisioner? identityOwner;
    private PostgresFixture? postgres;
    private ServerHostFiles? material;
    private EntraProtocolCrypto? tlsMaterial;
    private QualificationHostInput input = null!;
    private string root = "", inputFile = "", ringBefore = "";
    private string? priorEpoch;
    private QualificationSocketServer? controls;
    private QualificationChild? browser;
    private Task? close;
    private readonly List<Task> dependencyCloses = [];

    internal static async Task RunAsync(string scenario)
    {
        var owner = new AuthBrowserHttpsCase(); bool primary = false, cleanup = false;
        try { await owner.StartAsync(scenario); }
        catch { primary = true; }
        try { await owner.CloseAsync().WaitAsync(TimeSpan.FromSeconds(90)); }
        catch { cleanup = true; lock (Retained) Retained.Add(owner); }
        var outcome = owner.stopOwner.Outcome(primary, cleanup);
        if (outcome.Failed) throw new ServerHostCaseFailure(outcome.CategoryMessage);
    }
    private async Task StartAsync(string scenario)
    {
        string parent = Environment.GetEnvironmentVariable("WEFT_AUTH_BROWSER_ADMISSION_ROOT") ?? throw new QualificationFailure();
        string admissionFile = Environment.GetEnvironmentVariable("WEFT_AUTH_BROWSER_ADMISSION_FILE") ?? throw new QualificationFailure();
        admission = PrivateQualificationFiles.Input<QualificationAdmission>(parent, admissionFile);
        QualificationWire.Require(admission.Version == 1 && admission.Approved && admission.Root == parent);
        CheckFile(admission.ProbeAssembly, admission.ProbeSha256); CheckFile(admission.ServerAssembly, admission.ServerSha256);
        CheckFile(admission.Node, admission.NodeSha256); CheckFile(admission.Npm, admission.NpmSha256);
        CheckFile(admission.Chromium, admission.ChromiumSha256); CheckFile(admission.Certutil, admission.CertutilSha256);
        QualificationWire.Require(Path.IsPathFullyQualified(admission.SourceRoot) && new DirectoryInfo(admission.SourceRoot).LinkTarget is null);
        root = Path.Combine(parent, "c-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root, ServerHostFiles.PrivateDirectory);
        QualificationWire.Require((root + "/p.sock").Length < 100);
        // This case owns its PostgresFixture directly. The collection's automatic
        // DisposeAsync must not force-remove this cluster after unproved child drain.
        postgres = new(); await postgres.InitializeAsync();
        material = new(); tlsMaterial = new(); data = await PostgresCatalogTestSupport.CreateAsync(postgres);
        identityOwner = new(data.Case.MigrationConnectionString);
        var binding = await identityOwner.BindAsync(new("entra", EntraProtocolFixturePolicy.Issuer, EntraProtocolFixturePolicy.Subject), data.Actor.PrincipalId);
        QualificationWire.Require(binding.Outcome == Weft.Application.ExternalIdentityBindingOwnerOutcome.Created);
        string keys = Path.Combine(root, "keys"); Directory.CreateDirectory(keys, ServerHostFiles.PrivateDirectory);
        const string application = "Weft owned HTTPS browser qualification";
        using (DurableSessionKeyProfile.BootstrapEmpty(new(data.Project.OrganizationId, application, keys), material.DataCertificate, [],
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2))) { }
        var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start(1);
        int port; try { port = ((IPEndPoint)reservation.LocalEndpoint).Port; } finally { reservation.Stop(); }
        string webRoot = Path.Combine(Path.GetDirectoryName(admission.ServerAssembly)!, "wwwroot");
        var assets = admission.Assets;
        QualificationWire.Require(assets.ContainsKey("index.html") && assets.Count <= 256);
        input = new()
        {
            Version = 1,
            Generation = 1,
            Root = root,
            SocketPath = Path.Combine(root, "p.sock"),
            Origin = "https://127.0.0.1:" + port,
            NetworkCase = scenario.StartsWith("network-", StringComparison.Ordinal) ? scenario[8..] : null,
            WebRoot = webRoot,
            Assets = assets,
            Organization = data.Project.OrganizationId,
            ApplicationName = application,
            KeyDirectory = keys,
            DataCertificateFile = Write("data.pem", material.DataCertificate.ExportCertificatePem()),
            DataKeyFile = Write("data.key", Key(material.DataCertificate)),
            TlsCertificateFile = Write("tls.pem", tlsMaterial.ApplicationTls.ExportCertificatePem() + "\n" + tlsMaterial.Root.ExportCertificatePem()),
            TlsKeyFile = Write("tls.key", Key(tlsMaterial.ApplicationTls)),
            ReaderConnection = data.Case.SessionReaderConnectionString,
            WriterConnection = data.Case.SessionWriterConnectionString,
            IdentityConnection = data.Case.IdentityConnectionString,
            CatalogConnection = data.Case.CatalogConnectionString
        };
        inputFile = Write("host.json", JsonSerializer.Serialize(input, QualificationWire.Json)); ringBefore = Ring();
        string home = Path.Combine(root, "home"); Directory.CreateDirectory(home, ServerHostFiles.PrivateDirectory);
        string pki = Path.Combine(home, ".pki"); Directory.CreateDirectory(pki, ServerHostFiles.PrivateDirectory);
        string nss = Path.Combine(pki, "nssdb"); Directory.CreateDirectory(nss, ServerHostFiles.PrivateDirectory);
        string ca = Write("browser-ca.pem", tlsMaterial.Root.ExportCertificatePem());
        await ToolAsync(admission.Certutil, ["-N", "-d", "sql:" + nss, "--empty-password"], home);
        await ToolAsync(admission.Certutil, ["-A", "-d", "sql:" + nss, "-n", "WeftOwnedQualificationRoot", "-t", "C,,", "-i", ca], home);
        proxy = new(port);
        await StartProbeAsync();
        controls = new(root, Path.Combine(root, "o.sock"), ControlAsync);
        string browserInput = Write("browser.json", JsonSerializer.Serialize(new
        {
            version = 1,
            root,
            socketPath = Path.Combine(root, "o.sock"),
            origin = input.Origin,
            scenario,
            proxy = proxy.Origin,
            organization = data.Project.OrganizationId.ToString("D"),
            assets = assets.Keys.ToArray(),
            home,
            node = admission.Node,
            nodeSha256 = admission.NodeSha256,
            chromium = admission.Chromium,
            chromiumSha256 = admission.ChromiumSha256
        }, QualificationWire.Json));
        var environment = ChildEnvironment(home);
        environment["WEFT_AUTH_BROWSER_INPUT"] = browserInput;
        environment["WEFT_AUTH_BROWSER_ROOT"] = root;
        environment["WEFT_CHROMIUM_EXECUTABLE"] = admission.Chromium;
        browser = QualificationChild.Start(admission.Npm, ["run", "test:auth-https-browser"], admission.SourceRoot, environment);
        await browser.RequireSuccessAsync(TimeSpan.FromMinutes(5));
    }
    private async Task StartProbeAsync()
    {
        CheckFile(admission.ProbeAssembly, admission.ProbeSha256);
        var launch = probeOwners.Allocate();
        input = input with { Generation = launch.Generation };
        inputFile = Write("host-" + launch.Generation + ".json", JsonSerializer.Serialize(input, QualificationWire.Json));
        var environment = ChildEnvironment(Path.Combine(root, "home"));
        environment["WEFT_HOST_PROBE_OPERATION"] = "browserQualification";
        environment["WEFT_AUTH_BROWSER_ROOT"] = root; environment["WEFT_AUTH_BROWSER_HOST_INPUT"] = inputFile;
        await launch.StartAsync(() => QualificationChild.Start(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            [admission.ProbeAssembly], Path.GetDirectoryName(admission.ProbeAssembly)!, environment),
            child => child.Ready.Task.WaitAsync(TimeSpan.FromSeconds(30)),
            async launchedGeneration =>
            {
                QualificationWire.Require(launchedGeneration == launch.Generation);
                _ = await QualificationWire.CallAsync(root, input.SocketPath, QualificationRequest.Control("counts"));
            });
    }
    private Task<QualificationReply?> StopProbeAsync()
    {
        var launch = probeOwners.Current;
        return launch is null ? Task.FromResult<QualificationReply?>(null) : launch.StopAsync(async child =>
        {
            if (!child.Exit.IsCompleted) _ = await QualificationWire.CallAsync(root, input.SocketPath, QualificationRequest.Control("stop"));
            await child.RequireSuccessAsync(TimeSpan.FromSeconds(50));
        }, (_, launchedGeneration) => PrivateQualificationFiles.Input<QualificationReply>(root, Path.Combine(root, "closed-" + launchedGeneration + ".json")));
    }
    private Task<QualificationParentStopResult> StopTransportAsync()
        => stopOwner.StopAsync(() => proxy?.CloseAsync() ?? Task.CompletedTask, () => proxy is null || proxy.Settled,
            StopProbeAsync, () => probeOwners.AllClosed && probeOwners.Children.All(child => child.Settled),
            () => QualificationBackstopPolicy.Validate(input?.NetworkCase, probeOwners.AdmissionRefusalsTotal, proxy?.Rejected ?? 0, proxy?.RejectedForbidden ?? 0));
    private async Task<QualificationReply> ControlAsync(QualificationRequest request)
    {
        if (request.Operation == "restart")
        {
            await StopProbeAsync(); QualificationWire.Require(Ring() == ringBefore); await StartProbeAsync();
        }
        else if (request.Operation == "stop")
        {
            var closed = await StopTransportAsync();
            QualificationWire.Require(closed.KnownSettled && closed.Report is not null);
            return closed.Report! with
            {
                AdmissionRefusalsTotal = probeOwners.AdmissionRefusalsTotal,
                ProxyRefused = proxy?.Rejected ?? 0,
                ProxyApproved = proxy?.Connected ?? 0,
                ClosedObservation = true,
                CounterValidation = closed.CounterValidation
            };
        }
        else if (request.Operation is "authorize" or "register" or "withdraw" or "seal" or "replay" or "networkBegin" or "revoke" or "barrier") return await QualificationWire.CallAsync(root, input.SocketPath, request);
        else QualificationWire.Require(request.Operation == "counts");
        QualificationWire.Require(probeOwners.Current is { Admitted: true });
        var counts = await QualificationWire.CallAsync(root, input.SocketPath, QualificationRequest.Control("counts"));
        long total = await data!.Case.ScalarAsync<long>("SELECT count(*) FROM ONLY weft.server_sessions");
        long active = await data.Case.ScalarAsync<long>("SELECT count(*) FROM ONLY weft.server_sessions WHERE revoked_at IS NULL");
        long revoked = await data.Case.ScalarAsync<long>("SELECT count(*) FROM ONLY weft.server_sessions WHERE revoked_at IS NOT NULL");
        string epoch = await data.Case.ScalarAsync<string>("SELECT coalesce(string_agg(login_epoch::text,',' ORDER BY login_epoch),'') FROM ONLY weft.server_sessions");
        bool uniqueEpochs = await data.Case.ScalarAsync<bool>("SELECT count(DISTINCT login_epoch)=count(*) FROM ONLY weft.server_sessions");
        bool changed = priorEpoch is not null && epoch.Length > 0 && priorEpoch != epoch && uniqueEpochs;
        if (epoch.Length > 0) priorEpoch = epoch;
        return counts with
        {
            SessionRows = total,
            ActiveRows = active,
            RevokedRows = revoked,
            EpochChanged = changed,
            ProxyRefused = proxy?.Rejected ?? 0,
            ProxyApproved = proxy?.Connected ?? 0,
            AdmissionRefusalsTotal = checked(probeOwners.AdmissionRefusalsTotal + counts.AdmissionRefusals),
            ProcessGeneration = probeOwners.AdmittedGeneration,
            RingUnchanged = Ring() == ringBefore
        };
    }

    private Dictionary<string, string> ChildEnvironment(string home)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string name in new[] { "PATH", "DOTNET_ROOT", "DOTNET_ROOT_X64", "LANG", "LC_ALL", "TZ" })
            if (Environment.GetEnvironmentVariable(name) is { } value) environment[name] = value;
        environment["PATH"] = Path.GetDirectoryName(admission.Node) + ":" + (environment.GetValueOrDefault("PATH") ?? "/usr/bin:/bin");
        environment["HOME"] = home; environment["TMPDIR"] = root; environment["XDG_DATA_HOME"] = Path.Combine(home, ".local/share");
        environment["DOTNET_CLI_HOME"] = home; environment["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "false";
        environment["DOTNET_ENVIRONMENT"] = "Production"; environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        environment["PLAYWRIGHT_NO_COPY_PROMPT"] = "1"; environment["CI"] = "1";
        // Optional fixed virtual display for shared hosts; digits only, otherwise the first free one.
        if (Environment.GetEnvironmentVariable("WEFT_XVFB_SERVER_NUM") is { } display)
        {
            QualificationWire.Require(display.Length is > 0 and <= 5 && display.All(char.IsAsciiDigit));
            environment["WEFT_XVFB_SERVER_NUM"] = display;
        }
        return environment;
    }
    private async Task ToolAsync(string executable, string[] arguments, string home)
    {
        var child = QualificationChild.Start(executable, arguments, root, ChildEnvironment(home));
        // Retain utility owners too: a timeout does not authorize deleting their NSS files.
        tools.Add(child); await child.RequireSuccessAsync(TimeSpan.FromSeconds(60));
    }
    private readonly List<QualificationChild> tools = [];
    private static string Key(X509Certificate2 certificate) { using var rsa = certificate.GetRSAPrivateKey(); return rsa!.ExportPkcs8PrivateKeyPem(); }
    private string Write(string name, string text)
    {
        string path = Path.Combine(root, name); PrivateQualificationFiles.RequireChild(root, path);
        using var file = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = ServerHostFiles.PrivateFile
        });
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
        try { QualificationWire.Require(bytes.Length <= QualificationWire.Maximum); file.Write(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        return path;
    }
    private string Ring() => string.Join("\n", Directory.GetFiles(input.KeyDirectory).Order(StringComparer.Ordinal).Select(path =>
        Path.GetFileName(path) + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))));
    private static void CheckFile(string path, string hash)
    {
        QualificationWire.Require(Path.IsPathFullyQualified(path) && new FileInfo(path).LinkTarget is null && hash.Length == 64
            && hash == Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
    }
    private Task CloseAsync() => close ??= stopOwner.ReleaseAsync(ConfirmOuterOwnersAsync, ReleaseDependenciesAsync);
    private async Task ConfirmOuterOwnersAsync()
    {
        // A failed/undelivered final counter receipt does not undo known closure.
        // Missing actual browser closure still retains all dependent resources.
        if (browser is not null) { await browser.RequireSettlementAsync(TimeSpan.FromSeconds(45)); QualificationWire.Require(stopOwner.KnownSettled); }
        foreach (var tool in tools) await tool.RequireSettlementAsync(TimeSpan.FromSeconds(5));
        await stopOwner.ConfirmControlAsync(controls);
        var closed = await StopTransportAsync();
        QualificationWire.Require(closed.KnownSettled && probeOwners.AllClosed && probeOwners.Children.All(child => child.Settled));
    }
    private async Task ReleaseDependenciesAsync()
    {
        foreach (var dependency in new IAsyncDisposable?[] { identityOwner, data })
        { if (dependency is null) continue; Task original = dependency.DisposeAsync().AsTask(); dependencyCloses.Add(original); await original; }
        if (postgres is not null) { Task original = postgres.DisposeAsync(); dependencyCloses.Add(original); await original; }
        tlsMaterial?.Dispose(); material?.Dispose();
        foreach (var child in probeOwners.Children.Concat(tools).Append(browser).OfType<QualificationChild>()) child.DisposeAfterSettlement();
        if (root.Length > 0) Directory.Delete(root, recursive: true);
    }
}

[System.Runtime.Versioning.SupportedOSPlatform("linux")]
internal sealed class QualificationChild
{
    private readonly Process process;
    private readonly Task stdout, stderr;
    private bool overflow;
    internal Task Exit { get; }
    internal TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal bool Settled => Exit.IsCompletedSuccessfully && stdout.IsCompletedSuccessfully && stderr.IsCompletedSuccessfully;
    private QualificationChild(Process process)
    {
        this.process = process; Exit = process.WaitForExitAsync();
        stdout = DrainAsync(process.StandardOutput, true); stderr = DrainAsync(process.StandardError, false);
        foreach (var original in new[] { Exit, stdout, stderr, Ready.Task })
            _ = original.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        _ = Exit.ContinueWith(_ => Ready.TrySetException(new QualificationFailure()), TaskScheduler.Default);
    }
    internal static QualificationChild Start(string executable, string[] arguments, string directory, IReadOnlyDictionary<string, string> environment)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.Environment.Clear(); foreach (var item in environment) start.Environment[item.Key] = item.Value;
        start.Environment["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "false";
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return new(Process.Start(start) ?? throw new QualificationFailure());
    }
    private async Task DrainAsync(StreamReader reader, bool readiness)
    {
        var buffer = new char[1024]; int total = 0; string tail = "";
        while (true)
        {
            int count = await reader.ReadAsync(buffer); if (count == 0) return;
            total += count; if (total > 131072) overflow = true;
            // No raw child output is retained or forwarded to xUnit/TRX.
            if (readiness)
            {
                string window = tail + new string(buffer, 0, count);
                if (window.Contains("WeftAuthBrowserReady\n", StringComparison.Ordinal)) Ready.TrySetResult();
                tail = window.Length > 32 ? window[^32..] : window;
            }
            Array.Clear(buffer);
        }
    }
    internal async Task RequireSuccessAsync(TimeSpan timeout)
    {
        await RequireSettlementAsync(timeout);
        QualificationWire.Require(process.ExitCode == 0 && !overflow);
    }
    internal async Task RequireSettlementAsync(TimeSpan timeout) => await Task.WhenAll(Exit, stdout, stderr).WaitAsync(timeout);
    internal void DisposeAfterSettlement() { QualificationWire.Require(Settled); process.Dispose(); }
}
