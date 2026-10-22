using System.Security.Cryptography.X509Certificates;
using Weft.FerruleService;
using Weft.WorkerTransport;
using static Weft.Tests.InspectionTransportTests;

namespace Weft.Tests;

/// <summary>The packaged service's environment admission: complete, canonical and fail-closed.</summary>
public sealed class FerruleServiceSettingsTests
{
    [Fact]
    public void PinnedServiceAdmitsOneCertificateSourceAndCanonicalLimits()
    {
        using var certificate = TransportCase.Certificate("CN=Ferrule service");
        var environment = Pinned(certificate);
        var settings = FerruleServiceSettings.Read(Read(environment));
        Assert.False(settings.PlatformSandbox);
        Assert.Equal(3, settings.ConcurrencyLimit);
        Assert.Equal(5, settings.QueueLimit);
        Assert.Equal(TimeSpan.FromSeconds(2), settings.Wait);
        Assert.Equal("/ferrule-host", settings.WorkerExecutable);
        Assert.Equal("/tmp", settings.TemporaryRoot);
        var transport = settings.Transport(() => true, "engine");
        Assert.Equal(WorkerTransportAuthentication.PinnedMutualTls, transport.Authentication);
        Assert.True(transport.ServiceRoutes);
        Assert.Equal(8, transport.Capacity);
        Assert.Equal(2, transport.ClientCertificateSha256.Count);
        Assert.Equal(certificate.Thumbprint, transport.ServerCertificate!.Thumbprint);
        Assert.Equal("FerruleServiceSettings", settings.ToString());

        // Defaults: concurrency from the processor count, an equal queue and a five-second admission wait.
        environment.Remove("WEFT_FERRULE_SERVICE_CONCURRENCY");
        environment.Remove("WEFT_FERRULE_SERVICE_QUEUE");
        environment.Remove("WEFT_FERRULE_SERVICE_ADMISSION_WAIT_SECONDS");
        var defaults = FerruleServiceSettings.Read(Read(environment));
        Assert.Equal(Math.Clamp(Environment.ProcessorCount, 1, 64), defaults.ConcurrencyLimit);
        Assert.Equal(defaults.ConcurrencyLimit, defaults.QueueLimit);
        Assert.Equal(TimeSpan.FromSeconds(5), defaults.Wait);
    }

    [Fact]
    public void PlatformSandboxRefusesPinnedMaterialBesideIt()
    {
        var platform = new Dictionary<string, string>
        {
            ["WEFT_FERRULE_SERVICE_LISTEN_ADDR"] = "0.0.0.0:8080",
            ["WEFT_FERRULE_SERVICE_AUTHENTICATION"] = "platformSandbox",
            ["WEFT_FERRULE_SERVICE_CONCURRENCY"] = "1",
            ["WEFT_FERRULE_SERVICE_QUEUE"] = "0"
        };
        var settings = FerruleServiceSettings.Read(Read(platform));
        Assert.True(settings.PlatformSandbox);
        var transport = settings.Transport(() => true, "engine");
        Assert.Equal(WorkerTransportAuthentication.PlatformSandbox, transport.Authentication);
        Assert.Equal(1, transport.Capacity);
        foreach (var extra in new[] { "WEFT_FERRULE_SERVICE_CLIENT_CERTIFICATE_SHA256", "WEFT_FERRULE_SERVICE_PEM", "WEFT_FERRULE_SERVICE_CERTIFICATE_FILE",
                     "WEFT_FERRULE_SERVICE_PRIVATE_KEY_FILE", "WEFT_FERRULE_SERVICE_HEALTH_ADDR" })
            Assert.Throws<InvalidOperationException>(() => FerruleServiceSettings.Read(Read(new Dictionary<string, string>(platform) { [extra] = "x" })));
    }

    [Fact]
    public void InvalidOrIncompleteSettingsStopTheService()
    {
        using var certificate = TransportCase.Certificate("CN=Ferrule service");
        foreach (var (name, value) in new (string, string?)[]
        {
            ("WEFT_FERRULE_SERVICE_LISTEN_ADDR", null),
            ("WEFT_FERRULE_SERVICE_LISTEN_ADDR", "0.0.0.0:0"),
            ("WEFT_FERRULE_SERVICE_LISTEN_ADDR", "localhost:8443"),
            ("WEFT_FERRULE_SERVICE_LISTEN_ADDR", "0.0.0.0:08443"),
            ("WEFT_FERRULE_SERVICE_AUTHENTICATION", "none"),
            ("WEFT_FERRULE_SERVICE_CLIENT_CERTIFICATE_SHA256", null),
            ("WEFT_FERRULE_SERVICE_CLIENT_CERTIFICATE_SHA256", new string('A', 64)),
            ("WEFT_FERRULE_SERVICE_CLIENT_CERTIFICATE_SHA256", new string('a', 64) + "," + new string('a', 64)),
            ("WEFT_FERRULE_SERVICE_PEM", null),
            ("WEFT_FERRULE_SERVICE_PEM", "-----BEGIN CERTIFICATE-----\nAA==\n-----END CERTIFICATE-----\n"),
            ("WEFT_FERRULE_SERVICE_CERTIFICATE_FILE", "/run/secrets/service.crt"),
            ("WEFT_FERRULE_SERVICE_HEALTH_ADDR", "0.0.0.0:8443"),
            ("WEFT_FERRULE_SERVICE_CONCURRENCY", "0"),
            ("WEFT_FERRULE_SERVICE_CONCURRENCY", "65"),
            ("WEFT_FERRULE_SERVICE_CONCURRENCY", "03"),
            ("WEFT_FERRULE_SERVICE_QUEUE", "-1"),
            ("WEFT_FERRULE_SERVICE_QUEUE", "510"),
            ("WEFT_FERRULE_SERVICE_ADMISSION_WAIT_SECONDS", "21")
        })
        {
            var environment = Pinned(certificate);
            if (value is null) environment.Remove(name);
            else environment[name] = value;
            Assert.Throws<InvalidOperationException>(() => FerruleServiceSettings.Read(Read(environment)));
        }

        // An expired certificate is refused.
        using var key = System.Security.Cryptography.RSA.Create(2048);
        var request = new CertificateRequest("CN=Expired", key, System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var expired = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow.AddDays(-1));
        Assert.ThrowsAny<Exception>(() => FerruleServiceSettings.Read(Read(Pinned(expired))));
    }

    [Fact]
    public void CompilationIsExplicitAndItsInstallationRequiresIt()
    {
        var platform = new Dictionary<string, string>
        {
            ["WEFT_FERRULE_SERVICE_LISTEN_ADDR"] = "0.0.0.0:8080",
            ["WEFT_FERRULE_SERVICE_AUTHENTICATION"] = "platformSandbox",
            ["WEFT_FERRULE_SERVICE_CONCURRENCY"] = "1",
            ["WEFT_FERRULE_SERVICE_QUEUE"] = "0"
        };
        Assert.Null(FerruleServiceSettings.Read(Read(platform)).Compile);
        Assert.Null(FerruleServiceSettings.Read(Read(new Dictionary<string, string>(platform) { ["WEFT_FERRULE_SERVICE_COMPILE_ENABLED"] = "false" })).Compile);
        var enabled = FerruleServiceSettings.Read(Read(new Dictionary<string, string>(platform) { ["WEFT_FERRULE_SERVICE_COMPILE_ENABLED"] = "true" }));
        Assert.Equal(new FerruleServiceSettings.CompileSettings("/weft-compile-sandbox", "/usr/share/dotnet"), enabled.Compile);
        var installed = FerruleServiceSettings.Read(Read(new Dictionary<string, string>(platform)
        {
            ["WEFT_FERRULE_SERVICE_COMPILE_ENABLED"] = "true",
            ["WEFT_FERRULE_SERVICE_COMPILE_LAUNCHER"] = "/opt/weft/launcher",
            ["WEFT_FERRULE_SERVICE_COMPILE_TOOLCHAIN"] = "/opt/dotnet"
        }));
        Assert.Equal(new FerruleServiceSettings.CompileSettings("/opt/weft/launcher", "/opt/dotnet"), installed.Compile);
        var toolchain = "sha256:" + new string('3', 64);
        Assert.Equal(toolchain, installed.Transport(() => true, "engine", toolchain).CompileToolchain);
        Assert.Null(enabled.Transport(() => true, "engine").CompileToolchain);
        foreach (var extra in new Dictionary<string, string>[]
        {
            new() { ["WEFT_FERRULE_SERVICE_COMPILE_ENABLED"] = "TRUE" },
            new() { ["WEFT_FERRULE_SERVICE_COMPILE_ENABLED"] = "1" },
            new() { ["WEFT_FERRULE_SERVICE_COMPILE_LAUNCHER"] = "/weft-compile-sandbox" },
            new() { ["WEFT_FERRULE_SERVICE_COMPILE_ENABLED"] = "false", ["WEFT_FERRULE_SERVICE_COMPILE_TOOLCHAIN"] = "/usr/share/dotnet" }
        })
        {
            var environment = new Dictionary<string, string>(platform);
            foreach (var (name, value) in extra) environment[name] = value;
            Assert.Throws<InvalidOperationException>(() => FerruleServiceSettings.Read(Read(environment)));
        }
    }

    private static Dictionary<string, string> Pinned(X509Certificate2 certificate)
    {
        using var key = certificate.GetRSAPrivateKey()!;
        return new Dictionary<string, string>
        {
            ["WEFT_FERRULE_SERVICE_LISTEN_ADDR"] = "0.0.0.0:8443",
            ["WEFT_FERRULE_SERVICE_HEALTH_ADDR"] = "0.0.0.0:8080",
            ["WEFT_FERRULE_SERVICE_CLIENT_CERTIFICATE_SHA256"] = new string('a', 64) + "," + new string('b', 64),
            ["WEFT_FERRULE_SERVICE_PEM"] = certificate.ExportCertificatePem() + "\n" + key.ExportPkcs8PrivateKeyPem() + "\n",
            ["WEFT_FERRULE_SERVICE_CONCURRENCY"] = "3",
            ["WEFT_FERRULE_SERVICE_QUEUE"] = "5",
            ["WEFT_FERRULE_SERVICE_ADMISSION_WAIT_SECONDS"] = "2"
        };
    }

    private static Func<string, string?> Read(IReadOnlyDictionary<string, string> environment) =>
        name => environment.TryGetValue(name, out var value) ? value : null;
}
