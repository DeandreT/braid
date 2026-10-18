using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using Weft.Application;
using Weft.Identity;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

[SupportedOSPlatform("linux")]
internal sealed class ServerHostFiles : IDisposable
{
    internal const UnixFileMode PrivateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    internal const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    internal string Root { get; }
    internal string Keys { get; }
    internal string ApplicationName => "Weft owned published host qualification";
    internal X509Certificate2 DataCertificate { get; }
    internal X509Certificate2 ClientCertificate { get; }
    internal X509Certificate2 TlsCertificate { get; }
    internal string DataPem { get; }
    internal string DataKey { get; }
    internal string ClientPem { get; }
    internal string ClientKey { get; }
    internal string TlsPem { get; }
    internal string TlsKey { get; }

    internal ServerHostFiles()
    {
        Root = Path.Combine(Path.GetTempPath(), "weft-host-case-" + Guid.NewGuid().ToString("N"));
        Keys = Path.Combine(Root, "keys");
        try
        {
            Directory.CreateDirectory(Root, PrivateDirectory); Directory.CreateDirectory(Keys, PrivateDirectory);
            DataCertificate = Create(false); ClientCertificate = Create(false); TlsCertificate = Create(true);
            (DataPem, DataKey) = Pair("data", DataCertificate);
            (ClientPem, ClientKey) = Pair("client", ClientCertificate);
            (TlsPem, TlsKey) = Pair("tls", TlsCertificate);
        }
        catch (Exception)
        {
            var errors = new List<Exception> { new ServerHostCaseFailure("Owned private host fixture construction failed.") };
            foreach (var value in new[] { DataCertificate, ClientCertificate, TlsCertificate })
                try { value?.Dispose(); } catch (Exception) { errors.Add(new ServerHostCaseFailure("An owned partial certificate did not settle.")); }
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
            catch (Exception) { errors.Add(new ServerHostCaseFailure("The owned partial private root did not settle.")); }
            throw new AggregateException("Owned private host fixture construction and cleanup failed.", errors);
        }
    }

    private static X509Certificate2 Create(bool server)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Weft owned host fixture", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        if (server)
        {
            var names = new SubjectAlternativeNameBuilder(); names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        }
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(3));
    }

    private (string Certificate, string Key) Pair(string label, X509Certificate2 value)
    {
        using var key = value.GetRSAPrivateKey();
        ServerHostCheck.That(key is not null, "The owned PEM fixture has no RSA private key.");
        return (Text(label + ".pem", value.ExportCertificatePem()), Text(label + ".key", key!.ExportPkcs8PrivateKeyPem()));
    }

    internal (string CertificateFile, string PrivateKeyFile) HistoricalPair(string label, bool expired)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Weft owned historical delivery", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(expired ? -1 : 2));
        var pair = Pair(label, certificate); return (pair.Certificate, pair.Key);
    }

    internal string Text(string label, string value) => Bytes(label, new UTF8Encoding(false, true).GetBytes(value));

    internal string Bytes(string label, byte[] value)
    {
        string path = Path.Combine(Root, label);
        using var output = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = PrivateFile
        });
        output.Write(value); return path;
    }

    internal ServerSessionKeyProfileOptions Options(Guid organization) => new(organization, ApplicationName, Keys);

    internal string RingSnapshot() => JsonSerializer.Serialize(Directory.GetFiles(Keys).Order(StringComparer.Ordinal)
        .Select(path => new
        {
            Name = Path.GetFileName(path),
            Mode = (int)File.GetUnixFileMode(path),
            Digest = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))
        }));

    public void Dispose()
    {
        DataCertificate.Dispose(); ClientCertificate.Dispose(); TlsCertificate.Dispose();
        Directory.Delete(Root, recursive: true);
    }

    public override string ToString() => nameof(ServerHostFiles);
}

internal sealed class ServerHostCookies
{
    private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);
    internal string Header => string.Join("; ", values.Select(pair => pair.Key + "=" + pair.Value));
    internal void Apply(ServerHostReply reply)
    {
        if (!reply.Headers.TryGetValue("Set-Cookie", out var headers)) return;
        foreach (var item in SetCookieHeaderValue.ParseList(headers.ToList()))
        {
            string name = item.Name.ToString(), value = item.Value.ToString();
            if (value.Length == 0 || item.MaxAge is { } age && age <= TimeSpan.Zero) values.Remove(name);
            else values[name] = value;
        }
    }
    internal bool ContainsSession => values.ContainsKey("__Host-Weft.Session");
    public override string ToString() => nameof(ServerHostCookies);
}

/// <summary>Trusted112 fixture issuance and real restricted PG; no provider protocol or native-worker claim.</summary>
[SupportedOSPlatform("linux")]
internal sealed class ServerHostSessionCase : IAsyncDisposable
{
    internal static readonly Guid Tenant = Guid.Parse("e6492421-de0b-4626-87a1-773d7f77e0aa");
    internal static readonly Guid Client = Guid.Parse("39ceba91-5e4d-4ead-9be2-ddbf843be1c6");
    internal PostgresMappingMetadataTestSupport Data { get; private set; } = null!;
    internal ServerHostFiles Files { get; private set; } = null!;
    internal ExternalIdentityBinding Binding { get; private set; } = null!;
    internal ServerHostCookies Cookies { get; } = new();
    internal Uri Origin { get; private set; } = null!;
    internal Guid Epoch { get; private set; }
    internal string ExpectedIdentityContext => "v1." + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(
        "weft:identity-context:v1\n" + Data.Project.OrganizationId.ToString("D") + "\n" + Data.Actor.PrincipalId.Value.ToString("D") + "\n" + Epoch.ToString("D"))))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private ExternalIdentityBindingProvisioner? identities;
    private DurableSessionKeyProfile? issuerProfile;
    private PostgresServerSessionDatabase? issuerReader, issuerWriter;
    private WebApplication? issuer;
    private Task? issuerStop, issuerDispose, disposal;
    private readonly List<ServerHostProcess> hosts = [];
    private readonly List<Func<bool>> externalSettlement = [];
    private readonly List<IAsyncDisposable> networkDependencies = [];
    private readonly List<Task> dependencyDisposalTasks = [];
    private bool issuerSettled;

    internal static async Task RunAsync(PostgresFixture postgres, Func<ServerHostSessionCase, Task> body)
    {
        var test = new ServerHostSessionCase(); Exception? primary = null; var cleanup = new List<Exception>();
        try { await test.InitializeAsync(postgres); await body(test); }
        catch (Exception error) { primary = ServerHostCheck.Safe(error, "The owned published-session case failed."); }
        finally
        {
            try { await test.DisposeAsync(); }
            catch (Exception error) { cleanup.Add(ServerHostCheck.Safe(error, "The owned published-session case did not settle.")); }
        }
        ServerHostCheck.Throw(primary, cleanup);
    }

    private async Task InitializeAsync(PostgresFixture postgres)
    {
        Data = await PostgresMappingMetadataTestSupport.CreateAsync(postgres);
        Files = new(); identities = new(Data.Case.MigrationConnectionString);
        var binding = await identities.BindAsync(new ExternalIdentityKey("entra", "https://login.microsoftonline.com/" + Tenant.ToString("D") + "/v2.0",
            "owned-private-host-subject"), Data.Actor.PrincipalId);
        ServerHostCheck.That(binding.Outcome == ExternalIdentityBindingOwnerOutcome.Created && binding.Binding is not null,
            "The trusted host fixture binding was not created.");
        Binding = binding.Binding!;
        using (DurableSessionKeyProfile.BootstrapEmpty(Files.Options(Data.Project.OrganizationId), Files.DataCertificate, [],
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2))) { }
        issuerProfile = DurableSessionKeyProfile.Open(Files.Options(Data.Project.OrganizationId), Files.DataCertificate, []);
        issuerReader = new(Data.Case.SessionReaderConnectionString, ServerSessionDatabaseProfile.Reader);
        issuerWriter = new(Data.Case.SessionWriterConnectionString, ServerSessionDatabaseProfile.Writer);
        var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start(1);
        try
        {
            Origin = new Uri("https://127.0.0.1:" + ((IPEndPoint)reservation.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture));
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Production" });
            builder.Configuration.Sources.Clear(); builder.Logging.ClearProviders();
            builder.Services.AddWeftBrowserSessions(new(Origin.GetLeftPart(UriPartial.Authority)), issuerProfile,
                new PostgresServerSessionReader(issuerReader), new PostgresServerSessionWriter(issuerWriter), TimeProvider.System);
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, Origin.Port,
                listener => { listener.Protocols = HttpProtocols.Http1; listener.UseHttps(Files.TlsCertificate); }));
            issuer = builder.Build();
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            issuer.Use(async (context, next) =>
            {
                context.Response.OnCompleted(() => { completed.TrySetResult(); return Task.CompletedTask; });
                await next(context);
            });
            issuer.MapGet("/__owned_host_issue", async (HttpContext context) =>
            {
                DateTime now = await Data.Case.ScalarAsync<DateTime>("SELECT statement_timestamp()+interval '90 minutes'");
                await context.RequestServices.GetRequiredService<ServerSessionBrowser>().IssueFreshAsync(context,
                    Binding.BindingId, Binding.Version, Data.Actor.PrincipalId, new DateTimeOffset(now), context.RequestAborted);
                context.Response.StatusCode = 204;
            });
            reservation.Stop(); using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await issuer.StartAsync(startup.Token);
            using var client = ServerHostHttp.Client(Origin, Files.TlsCertificate);
            var reply = await ServerHostHttp.SendAsync(client, "/__owned_host_issue");
            ServerHostCheck.That(reply.Status == 204, "The trusted112 issuer did not acknowledge its fixture session.");
            Cookies.Apply(reply); ServerHostCheck.That(Cookies.ContainsSession, "The trusted112 issuer emitted no session cookie.");
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Epoch = await Data.Case.ScalarAsync<Guid>("SELECT login_epoch FROM weft.server_sessions");
            await StopIssuerAsync();
        }
        finally { reservation.Stop(); }
    }

    internal Dictionary<string, string?> Environment(bool proxy = false, int? backendPort = null) => new(StringComparer.Ordinal)
    {
        ["WEFT_CATALOG_ENABLED"] = "true",
        ["WEFT_CATALOG_AUTH_MODE"] = "session",
        ["WEFT_PUBLIC_ORIGIN"] = Origin.GetLeftPart(UriPartial.Authority),
        ["WEFT_HOST_TRANSPORT"] = proxy ? "trustedProxyOneHop" : "directHttps",
        ["WEFT_LISTEN_ADDR"] = "127.0.0.1:" + (backendPort ?? Origin.Port).ToString(CultureInfo.InvariantCulture),
        ["WEFT_KNOWN_PROXY_IP"] = proxy ? "127.0.0.1" : null,
        ["WEFT_SESSION_ORGANIZATION_ID"] = Data.Project.OrganizationId.ToString("D"),
        ["WEFT_SESSION_APPLICATION_NAME"] = Files.ApplicationName,
        ["WEFT_SESSION_KEY_DIRECTORY"] = Files.Keys,
        ["WEFT_SESSION_READER_CONNECTION_STRING_FILE"] = Credential("session-reader.txt", Data.Case.SessionReaderConnectionString),
        ["WEFT_SESSION_WRITER_CONNECTION_STRING_FILE"] = Credential("session-writer.txt", Data.Case.SessionWriterConnectionString),
        ["WEFT_IDENTITY_READER_CONNECTION_STRING_FILE"] = Credential("identity-reader.txt", Data.Case.IdentityConnectionString),
        ["WEFT_SESSION_KEY_CERTIFICATE_FILE"] = Files.DataPem,
        ["WEFT_SESSION_KEY_PRIVATE_KEY_FILE"] = Files.DataKey,
        ["WEFT_ENTRA_TENANT_ID"] = Tenant.ToString("D"),
        ["WEFT_ENTRA_CLIENT_ID"] = Client.ToString("D"),
        ["WEFT_ENTRA_CLIENT_CERTIFICATE_FILE"] = Files.ClientPem,
        ["WEFT_ENTRA_CLIENT_PRIVATE_KEY_FILE"] = Files.ClientKey,
        ["WEFT_TLS_CERTIFICATE_FILE"] = proxy ? null : Files.TlsPem,
        ["WEFT_TLS_PRIVATE_KEY_FILE"] = proxy ? null : Files.TlsKey,
        ["WEFT_CATALOG_CONNECTION_STRING"] = Data.Case.CatalogConnectionString
    };

    private string Credential(string label, string value)
    {
        string existing = Path.Combine(Files.Root, label);
        if (File.Exists(existing)) return existing;
        return Files.Text(label, value + "\n");
    }

    internal ServerHostProcess Start(Dictionary<string, string?>? environment = null, bool privacyProbe = false)
    {
        var host = ServerHostProcess.Start(environment ?? Environment(), privacyProbe); hosts.Add(host); return host;
    }

    internal void RequireExternalSettlement(Func<bool> witness) => externalSettlement.Add(witness);
    internal T OwnNetwork<T>(T resource) where T : IAsyncDisposable { networkDependencies.Add(resource); return resource; }

    internal async Task<ServerHostReply> StatusAsync(HttpClient client)
    {
        var reply = await ServerHostHttp.SendAsync(client, "/api/auth/session", headers: new Dictionary<string, string>
        { ["X-Weft-Session"] = "1", ["Cookie"] = Cookies.Header });
        Cookies.Apply(reply); return reply;
    }

    internal async Task<ServerHostReply> ContextAsync(HttpClient client)
    {
        var reply = await ServerHostHttp.SendAsync(client, "/api/v1/catalog/context", headers: new Dictionary<string, string>
        { ["Cookie"] = Cookies.Header });
        Cookies.Apply(reply); return reply;
    }

    internal void PositiveStatus(ServerHostReply reply)
    {
        using var document = reply.Json(); var root = document.RootElement;
        ServerHostCheck.That(reply.Status == 200 && root.EnumerateObject().Count() == 6
            && root.GetProperty("version").GetInt32() == 1 && root.GetProperty("mode").GetString() == "session"
            && root.GetProperty("state").GetString() == "authenticated"
            && Token(root.GetProperty("authActionRequestToken")) && Token(root.GetProperty("requestToken"))
            && root.GetProperty("identityContext").GetString() == ExpectedIdentityContext,
            "The actual published host did not consume the trusted112 session exactly.");
        ServerHostHttp.PrivateHeaders(reply);
    }

    private static bool Token(JsonElement value) => value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 and <= 4096 } token
        && token.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    internal void PositiveContext(ServerHostReply reply)
    {
        using var document = reply.Json(); var root = document.RootElement;
        ServerHostCheck.That(reply.Status == 200 && root.GetProperty("mode").GetString() == "session"
            && root.GetProperty("actorPrincipalId").GetString() == Data.Actor.PrincipalId.Value.ToString("D")
            && root.GetProperty("allowedOrganizationId").GetString() == Data.Project.OrganizationId.ToString("D")
            && root.GetProperty("identityContext").GetString() == ExpectedIdentityContext && Token(root.GetProperty("requestToken")),
            "The actual published catalog did not retain current scoped session authority.");
    }

    private async Task StopIssuerAsync()
    {
        if (issuer is null) { issuerSettled = true; return; }
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        issuerStop ??= issuer.StopAsync(stop.Token);
        try { await issuerStop.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally
        {
            issuerDispose ??= issuer.DisposeAsync().AsTask();
            await issuerDispose.WaitAsync(TimeSpan.FromSeconds(10));
        }
        issuerSettled = issuerStop.IsCompleted && issuerDispose is { IsCompleted: true };
        ServerHostCheck.That(issuerSettled, "The trusted issuer did not settle before origin handoff.");
    }

    public ValueTask DisposeAsync() => new(disposal ??= DisposeCoreAsync());
    private async Task DisposeCoreAsync()
    {
        var errors = new List<Exception>();
        foreach (var host in hosts.AsEnumerable().Reverse())
            try { await host.DisposeAsync(); } catch (Exception error) { errors.Add(ServerHostCheck.Safe(error, "An owned published host failed cleanup.")); }
        try { await StopIssuerAsync(); } catch (Exception error) { errors.Add(ServerHostCheck.Safe(error, "The trusted issuer failed cleanup.")); }
        if (!issuerSettled || hosts.Any(host => !host.Settled) || externalSettlement.Any(witness => !witness()))
            throw new AggregateException("Session fixture dependencies retained for unsettled owned work.", errors.Append(new ServerHostCaseFailure("Owned work settlement is unproven.")));
        foreach (var resource in networkDependencies.AsEnumerable().Reverse())
            try { await resource.DisposeAsync(); } catch (Exception) { errors.Add(new ServerHostCaseFailure("An owned fixture network dependency did not settle.")); }
        if (errors.Count != 0) throw new AggregateException("Fixture dependencies retained after owned network cleanup failure.", errors);
        foreach (var action in new Func<Task>[] {
            () => issuerReader is null ? Task.CompletedTask : issuerReader.DisposeAsync().AsTask(),
            () => issuerWriter is null ? Task.CompletedTask : issuerWriter.DisposeAsync().AsTask(),
            () => identities is null ? Task.CompletedTask : identities.DisposeAsync().AsTask(),
            () => Data is null ? Task.CompletedTask : Data.DisposeAsync().AsTask() })
            try { Task original = action(); dependencyDisposalTasks.Add(original); await original.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception) { errors.Add(new ServerHostCaseFailure("An owned fixture pool did not settle.")); }
        if (errors.Count == 0) { issuerProfile?.Dispose(); Files?.Dispose(); }
        if (errors.Count != 0) throw new AggregateException("Published-session cleanup did not complete.", errors);
    }

    public override string ToString() => nameof(ServerHostSessionCase);
}
