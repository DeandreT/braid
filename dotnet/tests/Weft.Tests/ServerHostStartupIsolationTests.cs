using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Authentication;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Weft.Identity;
using Weft.Server;

namespace Weft.Tests;

[SupportedOSPlatform("linux")]
public sealed class ServerHostStartupIsolationTests
{
    [Fact]
    public void SessionRemovesOnlyTheTrustedFrameworkWarmupAndRefusesTheUnusedProvider()
    {
        IServiceCollection services = new ServiceCollection();
        services.AddAuthentication(); services.AddAuthentication();
        var other = ServiceDescriptor.Singleton<IHostedService>(new StartupWitness()); services.Add(other);
        var warmup = FrameworkWarmup(services);
        var provider = services.Single(item => item.ServiceType == typeof(IDataProtectionProvider));
        var retained = services.Where(item => !ReferenceEquals(item, warmup) && !ReferenceEquals(item, provider)).ToArray();
        ServerHostCheck.That(ReferenceEquals(warmup.ImplementationType!.Assembly, typeof(IKeyManager).Assembly),
            "The warmup identity control did not use the framework Data Protection assembly.");
        SessionHostDataProtectionPolicy.Apply(services);
        ServerHostCheck.That(services.Count == retained.Length + 1 && retained.All(services.Contains)
            && services.Contains(other) && !services.Contains(warmup) && !services.Contains(provider),
            "Session removed an unrelated descriptor or retained the default warmup/provider.");
        using var composition = services.BuildServiceProvider();
        var unused = composition.GetServices<IDataProtectionProvider>().Single();
        var error = Assert.Throws<InvalidOperationException>(() => unused.CreateProtector("owned-unused-purpose"));
        ServerHostCheck.That(error.InnerException is null && error.Message == "The server host profile is invalid.",
            "The unused provider did not return the closed host-profile failure.");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("lifetime")]
    [InlineData("factory")]
    [InlineData("provider")]
    public void ChangedFrameworkRegistrationShapeIsRefusedWithoutPartialRemoval(string kind)
    {
        IServiceCollection services = new ServiceCollection(); services.AddAuthentication();
        var warmup = FrameworkWarmup(services);
        switch (kind)
        {
            case "missing": services.Remove(warmup); break;
            case "duplicate": services.Add(warmup); break;
            case "lifetime":
                services.Remove(warmup);
                services.Add(ServiceDescriptor.Transient(typeof(IHostedService), warmup.ImplementationType!));
                break;
            case "factory":
                services.Remove(warmup);
                services.Add(ServiceDescriptor.Singleton<IHostedService>(_ => throw new ServerHostCaseFailure("Unexpected warmup factory entered.")));
                break;
            case "provider":
                services.AddSingleton<IDataProtectionProvider>(_ => throw new ServerHostCaseFailure("Unexpected default provider entered."));
                break;
        }
        var before = services.ToArray();
        _ = Assert.Throws<InvalidOperationException>(() => SessionHostDataProtectionPolicy.Apply(services));
        ServerHostCheck.That(before.SequenceEqual(services), "A refused startup policy partially changed service ownership.");
    }

    [Fact]
    public async Task HandshakeBorrowsOnlyThePrebuiltContextAndPreservesKestrelDefaults()
    {
        using var files = new ServerHostFiles();
        var certificate = SslStreamCertificateContext.Create(files.TlsCertificate, additionalCertificates: null, offline: true);
        var callback = SessionHostTransport.HandshakeOptions(certificate);
        var context = new TlsHandshakeCallbackContext();
        var first = await callback.OnConnection(context); var second = await callback.OnConnection(context);
        ServerHostCheck.That(ReferenceEquals(first.ServerCertificateContext, certificate)
            && ReferenceEquals(second.ServerCertificateContext, certificate) && !ReferenceEquals(first, second)
            && first.ServerCertificate is null && first.ServerCertificateSelectionCallback is null
            && first.EnabledSslProtocols == SslProtocols.None && first.CertificateRevocationCheckMode == X509RevocationMode.NoCheck
            && !first.ClientCertificateRequired && first.RemoteCertificateValidationCallback is null
            && first.CertificateChainPolicy is null && first.ApplicationProtocols is null
            && !context.AllowDelayedClientCertificateNegotation
            && callback.HandshakeTimeout == new HttpsConnectionAdapterOptions().HandshakeTimeout,
            "The Session callback rebuilt its certificate, broadened trust or changed TLS/timeout defaults.");
    }

    [Fact]
    public async Task CallbackTlsStillRefusesACurrentSanMatchingCertificateWithOnlyClientAuthEku()
    {
        var files = new ServerHostFiles(); var resources = new SessionHostResources();
        try
        {
            var organization = Guid.NewGuid();
            using (DurableSessionKeyProfile.BootstrapEmpty(files.Options(organization), files.DataCertificate, [],
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30))) { }
            string ring = files.RingSnapshot(); var values = Configuration(files, organization);
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=Weft owned wrong TLS purpose", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder(); names.AddIpAddress(IPAddress.Loopback); request.CertificateExtensions.Add(names.Build());
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.2") }, true));
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
            values["WEFT_TLS_CERTIFICATE_FILE"] = files.Text("client-only-tls.pem", certificate.ExportCertificatePem());
            values["WEFT_TLS_PRIVATE_KEY_FILE"] = files.Text("client-only-tls.key", key.ExportPkcs8PrivateKeyPem());
            _ = Assert.Throws<InvalidOperationException>(() => resources.PrepareAndRegister(new ServiceCollection(), new StartupEnvironment(),
                ServerHostOptions.Read(values.GetValueOrDefault), values.GetValueOrDefault));
            ServerHostCheck.That(resources.TlsCertificate is not null && resources.TlsCertificateContext is null && ring == files.RingSnapshot(),
                "Callback TLS lost Kestrel's server-auth purpose admission or changed the named profile on refusal.");
        }
        finally { await resources.DisposeAfterSettlementAsync(); files.Dispose(); }
    }

    [Fact]
    public async Task ActualSessionCompositionStartsOtherHostedServicesAndKeepsNamedProfileCrypto()
    {
        var files = new ServerHostFiles(); var resources = new SessionHostResources(); SessionHostLifetime? ownership = null;
        var witness = new StartupWitness(); int defaultManagerCalls = 0;
        try
        {
            var organization = Guid.NewGuid();
            using (DurableSessionKeyProfile.BootstrapEmpty(files.Options(organization), files.DataCertificate, [],
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30))) { }
            string ring = files.RingSnapshot(); var values = Configuration(files, organization);
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true, Args = [] });
            builder.Logging.ClearProviders(); builder.Services.AddSingleton<IHostedService>(witness);
            builder.Services.AddSingleton<IKeyManager>(_ =>
            {
                Interlocked.Increment(ref defaultManagerCalls);
                throw new ServerHostCaseFailure("The unused main-container key manager was activated.");
            });
            resources.PrepareAndRegister(builder.Services, new StartupEnvironment(),
                ServerHostOptions.Read(values.GetValueOrDefault), values.GetValueOrDefault);
            var host = builder.Build(); ownership = new(host, new SessionRequestGate(), resources);
            ownership.ResolveBeforeStart(); await host.StartAsync();
            var unused = host.Services.GetRequiredService<IDataProtectionProvider>();
            _ = Assert.Throws<InvalidOperationException>(() => unused.CreateProtector("owned-unused-purpose"));
            var cookies = host.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>();
            string handle = ServerSessionHandle.Create().ExportKey();
            var claim = new Claim(ServerSessionCarrierFormat.SessionIdClaim, handle, ClaimValueTypes.String,
                ServerSessionSchemes.Session, ServerSessionSchemes.Session);
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([claim], ServerSessionSchemes.Session)),
                new AuthenticationProperties(), ServerSessionSchemes.Session);
            foreach (string scheme in new[] { ServerSessionSchemes.Session, ServerSessionSchemes.Issue })
            {
                var format = cookies.Get(scheme).TicketDataFormat;
                var recovered = format.Unprotect(format.Protect(ticket));
                ServerHostCheck.That(recovered?.Principal.FindFirst(ServerSessionCarrierFormat.SessionIdClaim)?.Value == handle,
                    "An actual named cookie format lost the admitted profile when the default provider was refused.");
            }
            var entra = host.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(EntraBrowserClient.Scheme);
            ServerHostCheck.That(entra.DataProtectionProvider is not null && !ReferenceEquals(entra.DataProtectionProvider, unused)
                && entra.DataProtectionProvider.CreateProtector(typeof(OpenIdConnectHandler).FullName!) is not null
                && entra.StateDataFormat is not null && entra.StringDataFormat is not null
                && witness.Starts == 1 && defaultManagerCalls == 0 && ring == files.RingSnapshot()
                && ReferenceEquals(resources.TlsCertificateContext?.TargetCertificate, resources.TlsCertificate),
                "Actual startup activated default key storage, removed another hosted service or changed the private profile.");
        }
        finally
        {
            // Keep the fixture if original host/provider/resource settlement fails.
            if (ownership is not null) await ownership.CloseAsync();
            else await resources.DisposeAfterSettlementAsync();
            files.Dispose();
        }
        ServerHostCheck.That(witness.Stops == 1 && defaultManagerCalls == 0,
            "The preserved hosted service did not stop exactly once or default storage was activated at shutdown.");
    }

    private static ServiceDescriptor FrameworkWarmup(IServiceCollection services) => services.Single(item =>
        item.ServiceType == typeof(IHostedService) && !item.IsKeyedService && item.ImplementationType ==
        typeof(IKeyManager).Assembly.GetType("Microsoft.AspNetCore.DataProtection.Internal.DataProtectionHostedService", throwOnError: true));

    private static Dictionary<string, string?> Configuration(ServerHostFiles files, Guid organization)
    {
        const string connection = "Host=127.0.0.1;Port=1;Database=owned;Username=owned;Password=owned;SSL Mode=Disable";
        var values = new Dictionary<string, string?>
        {
            ["WEFT_CATALOG_ENABLED"] = "true",
            ["WEFT_CATALOG_AUTH_MODE"] = "session",
            ["WEFT_PUBLIC_ORIGIN"] = "https://127.0.0.1:58413",
            ["WEFT_LISTEN_ADDR"] = "127.0.0.1:58413",
            ["WEFT_HOST_TRANSPORT"] = "directHttps",
            ["WEFT_SESSION_ORGANIZATION_ID"] = organization.ToString("D"),
            ["WEFT_SESSION_APPLICATION_NAME"] = files.ApplicationName,
            ["WEFT_SESSION_KEY_DIRECTORY"] = files.Keys,
            ["WEFT_ENTRA_TENANT_ID"] = ServerHostSessionCase.Tenant.ToString("D"),
            ["WEFT_ENTRA_CLIENT_ID"] = ServerHostSessionCase.Client.ToString("D"),
            ["WEFT_SESSION_KEY_CERTIFICATE_FILE"] = files.DataPem,
            ["WEFT_SESSION_KEY_PRIVATE_KEY_FILE"] = files.DataKey,
            ["WEFT_ENTRA_CLIENT_CERTIFICATE_FILE"] = files.ClientPem,
            ["WEFT_ENTRA_CLIENT_PRIVATE_KEY_FILE"] = files.ClientKey,
            ["WEFT_TLS_CERTIFICATE_FILE"] = files.TlsPem,
            ["WEFT_TLS_PRIVATE_KEY_FILE"] = files.TlsKey,
            ["WEFT_CATALOG_CONNECTION_STRING"] = connection
        };
        foreach (string role in new[] { "SESSION_READER", "SESSION_WRITER", "IDENTITY_READER" })
            values["WEFT_" + role + "_CONNECTION_STRING_FILE"] = files.Text(role + ".txt", connection);
        return values;
    }

    private sealed class StartupWitness : IHostedService
    {
        internal int Starts, Stops;
        public Task StartAsync(CancellationToken cancellationToken) { Starts++; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken) { Stops++; return Task.CompletedTask; }
    }

    private sealed class StartupEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Weft.Server";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}

[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class ServerHostStartupIsolationPublishedTests(PostgresFixture postgres)
{
    [PostgresFact]
    public Task PublishedStartupAndHandshakesKeepOwnedAiaOcspAndIsolatedHomeClosed() =>
        ServerHostSessionCase.RunAsync(postgres, async test =>
        {
            var aia = test.OwnNetwork(new ServerHostConnectionWitness()); var ocsp = test.OwnNetwork(new ServerHostConnectionWitness());
            using var certificates = new StartupTlsChain(aia.Port, ocsp.Port);
            string ring = test.Files.RingSnapshot();
            foreach (bool suppliedChain in new[] { false, true })
            {
                string label = suppliedChain ? "provided-chain" : "missing-chain";
                string home = Path.Combine(test.Files.Root, "startup-home-" + label);
                Directory.CreateDirectory(home, ServerHostFiles.PrivateDirectory);
                string guard = test.Files.Text("startup-home-" + label + "/owned-guard.txt", "owned-home-guard");
                var environment = test.Environment(); environment["HOME"] = home;
                environment["LOCALAPPDATA"] = null; environment["XDG_DATA_HOME"] = null;
                environment["WEFT_TLS_CERTIFICATE_FILE"] = test.Files.Text(label + ".pem", certificates.Pem(suppliedChain));
                environment["WEFT_TLS_PRIVATE_KEY_FILE"] = test.Files.Text(label + ".key", certificates.PrivateKeyPem());
                var host = test.Start(environment);
                using (var client = certificates.Client(test.Origin, suppliedChain))
                {
                    // This is the real named profile's persisted carrier and restricted reader,
                    // not merely a successful anonymous endpoint or a synthetic auth handler.
                    test.PositiveStatus(await ServerHostHttp.StartupStatusAsync(client, test.Cookies.Header));
                    test.PositiveContext(await test.ContextAsync(client));
                }
                // Exercise a fresh handshake too; the client always disables issuer downloads.
                using (var client = certificates.Client(test.Origin, suppliedChain))
                    test.PositiveStatus(await test.StatusAsync(client));
                await Task.Delay(TimeSpan.FromMilliseconds(500)); await host.StopAsync();
                ServerHostCheck.That(aia.Count == 0 && ocsp.Count == 0,
                    "Published startup or TLS handshakes contacted an owned AIA/OCSP endpoint.");
                ServerHostCheck.That(Directory.GetFileSystemEntries(home, "*", SearchOption.AllDirectories).SequenceEqual([guard])
                    && File.ReadAllText(guard) == "owned-home-guard" && ring == test.Files.RingSnapshot(),
                    "The published host touched default HOME storage or changed its admitted named ring.");
            }
            // Independent positive controls prove both negative socket witnesses were live.
            foreach (var witness in new[] { aia, ocsp })
            {
                using var sensor = new TcpClient(); await sensor.ConnectAsync(IPAddress.Loopback, witness.Port);
                await witness.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                ServerHostCheck.That(witness.Count == 1, "An owned issuer/OCSP witness lacked its exact positive control.");
            }
        });

    private sealed class StartupTlsChain : IDisposable
    {
        private X509Certificate2 Root { get; } = null!;
        private X509Certificate2 Intermediate { get; } = null!;
        private X509Certificate2 Leaf { get; } = null!;

        internal StartupTlsChain(int aiaPort, int ocspPort)
        {
            string realm = Guid.NewGuid().ToString("N"); var now = DateTimeOffset.UtcNow;
            try
            {
                using var rootKey = RSA.Create(2048); using var intermediateKey = RSA.Create(2048); using var leafKey = RSA.Create(2048);
                var root = Authority("CN=Weft startup root " + realm, rootKey, 1);
                Root = root.CreateSelfSigned(now.AddDays(-2), now.AddDays(10));
                var intermediate = Authority("CN=Weft startup intermediate " + realm, intermediateKey, 0);
                using var issuedIntermediate = intermediate.Create(Root, now.AddDays(-1), now.AddDays(5), RandomNumberGenerator.GetBytes(16));
                Intermediate = issuedIntermediate.CopyWithPrivateKey(intermediateKey);
                var leaf = new CertificateRequest("CN=Weft startup leaf " + realm, leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                leaf.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
                leaf.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
                leaf.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
                var names = new SubjectAlternativeNameBuilder(); names.AddIpAddress(IPAddress.Loopback); leaf.CertificateExtensions.Add(names.Build());
                leaf.CertificateExtensions.Add(new X509AuthorityInformationAccessExtension(
                    ["http://127.0.0.1:" + ocspPort.ToString(CultureInfo.InvariantCulture) + "/ocsp"],
                    ["http://127.0.0.1:" + aiaPort.ToString(CultureInfo.InvariantCulture) + "/issuer.der"]));
                using var issuedLeaf = leaf.Create(Intermediate, now.AddHours(-1), now.AddDays(2), RandomNumberGenerator.GetBytes(16));
                Leaf = issuedLeaf.CopyWithPrivateKey(leafKey);
            }
            catch { Dispose(); throw; }
        }

        private static CertificateRequest Authority(string subject, RSA key, int pathLength)
        {
            var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, pathLength, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            return request;
        }

        internal string Pem(bool suppliedChain) => Leaf.ExportCertificatePem() + (suppliedChain
            ? "\n" + Intermediate.ExportCertificatePem() + "\n" + Root.ExportCertificatePem() : "");

        internal string PrivateKeyPem()
        {
            using var key = Leaf.GetRSAPrivateKey(); return key!.ExportPkcs8PrivateKeyPem();
        }

        internal HttpClient Client(Uri origin, bool suppliedChain)
        {
            var policy = new X509ChainPolicy
            {
                TrustMode = X509ChainTrustMode.CustomRootTrust,
                RevocationMode = X509RevocationMode.NoCheck,
                DisableCertificateDownloads = true
            };
            policy.CustomTrustStore.Add(Root);
            // With a supplied server chain the client deliberately has no intermediate fallback.
            if (!suppliedChain) policy.ExtraStore.Add(Intermediate);
            var handler = new SocketsHttpHandler
            {
                UseProxy = false,
                UseCookies = false,
                AllowAutoRedirect = false,
                SslOptions = new SslClientAuthenticationOptions { CertificateChainPolicy = policy }
            };
            return new HttpClient(handler)
            {
                BaseAddress = origin,
                Timeout = TimeSpan.FromSeconds(15),
                DefaultRequestVersion = HttpVersion.Version11,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
        }

        public void Dispose() { Leaf?.Dispose(); Intermediate?.Dispose(); Root?.Dispose(); }
    }
}
