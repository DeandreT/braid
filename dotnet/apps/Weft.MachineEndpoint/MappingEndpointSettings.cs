using System.Globalization;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using Npgsql;
using Weft.Deployment;
using Weft.Identity;
using Weft.Storage.FileSystem;

namespace Weft.MachineEndpoint;

/// <summary>
/// Explicit environment configuration, admitted completely before the host starts. One host serves one
/// registered target. Credentials arrive only as protected private files; nothing is inferred from a request.
/// </summary>
internal sealed class MappingEndpointSettings
{
    internal const string Listen = "WEFT_ENDPOINT_LISTEN_ADDR";
    internal const string Transport = "WEFT_ENDPOINT_TRANSPORT";
    internal const string TlsCertificateFile = "WEFT_ENDPOINT_TLS_CERTIFICATE_FILE";
    internal const string TlsPrivateKeyFile = "WEFT_ENDPOINT_TLS_PRIVATE_KEY_FILE";
    internal const string Concurrency = "WEFT_ENDPOINT_CONCURRENCY";
    internal const string TenantSetting = "WEFT_ENDPOINT_ENTRA_TENANT_ID";
    internal const string AudienceSetting = "WEFT_ENDPOINT_ENTRA_AUDIENCE";
    internal const string RuntimeImageSetting = "WEFT_COMPILED_RUNTIME_IMAGE";
    internal const string CompilerImageSetting = "WEFT_FERRULE_WORKER_IMAGE";
    internal const string DockerSetting = "WEFT_DOCKER_EXECUTABLE";
    internal const int MaximumConcurrency = CompiledBuildExecution.MaximumConcurrentInvocations;

    /// <summary>Each restricted login, delivered by its own <c>NAME_FILE</c>.</summary>
    internal static readonly (string Setting, string Role)[] Logins =
    [
        ("WEFT_ENDPOINT_INVOCATION_WRITER_CONNECTION_STRING", "weft_invocation_writer"),
        ("WEFT_ENDPOINT_RELEASE_READER_CONNECTION_STRING", "weft_release_reader"),
        ("WEFT_ENDPOINT_BUILD_READER_CONNECTION_STRING", "weft_build_reader"),
        ("WEFT_ENDPOINT_BUILD_RUN_READER_CONNECTION_STRING", "weft_build_run_reader"),
        ("WEFT_ENDPOINT_ACCESS_CONNECTION_STRING", "weft_runtime"),
        ("WEFT_ENDPOINT_REPORT_READER_CONNECTION_STRING", "weft_report_reader"),
        ("WEFT_ENDPOINT_RUN_READER_CONNECTION_STRING", "weft_run_reader"),
        ("WEFT_ENDPOINT_CASE_READER_CONNECTION_STRING", "weft_case_reader")
    ];

    private static readonly string[] Target =
        ["WEFT_ENDPOINT_ORGANIZATION_ID", "WEFT_ENDPOINT_PROJECT_ID", "WEFT_ENDPOINT_MAPPING_ID", "WEFT_ENDPOINT_ENVIRONMENT_ID", "WEFT_ENDPOINT_TARGET_ID"];
    private const string EnvironmentVersion = "WEFT_ENDPOINT_ENVIRONMENT_VERSION";

    private MappingEndpointSettings(IPEndPoint listen, X509Certificate2? certificate, X509Certificate2Collection chain, int concurrency,
        ReleaseTargetReference target, Guid tenant, Guid audience, string runtimeImage, string compilerImage, string docker,
        IReadOnlyDictionary<string, string> connections, MappingRuntimeSelection runner)
    {
        ListenEndpoint = listen; Certificate = certificate; Chain = chain; ConcurrencyLimit = concurrency; TargetReference = target;
        TenantId = tenant; Audience = audience; RuntimeImage = runtimeImage; CompilerImage = compilerImage; DockerExecutable = docker;
        Connections = connections;
        Runner = runner;
    }

    internal IPEndPoint ListenEndpoint { get; }
    /// <summary>Null behind a platform ingress that terminates TLS; otherwise the direct HTTPS leaf.</summary>
    internal X509Certificate2? Certificate { get; }
    internal X509Certificate2Collection Chain { get; }
    internal int ConcurrencyLimit { get; }
    internal ReleaseTargetReference TargetReference { get; }
    internal Guid TenantId { get; }
    internal Guid Audience { get; }
    internal string RuntimeImage { get; }
    internal string CompilerImage { get; }
    internal string DockerExecutable { get; }
    internal IReadOnlyDictionary<string, string> Connections { get; }
    internal MappingRuntimeSelection Runner { get; }

    internal static MappingEndpointSettings Read(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        string? Optional(string name) => environment(name) is { Length: > 0 } value ? value : null;
        string Required(string name) => Optional(name) ?? throw Failure();

        // Ambient credentials, URL bindings and injected code compete with the explicit profile.
        if (new[] { "PGUSER", "PGPASSWORD", "PGPASSFILE", "PGSSLCERT", "PGSSLKEY", "PGREQUIREAUTH", "PGGSSENCMODE", "ASPNETCORE_URLS", "DOTNET_URLS",
                "ASPNETCORE_HTTP_PORTS", "ASPNETCORE_HTTPS_PORTS", "DOTNET_HTTP_PORTS", "DOTNET_HTTPS_PORTS", "DOTNET_STARTUP_HOOKS",
                "ASPNETCORE_HOSTINGSTARTUPASSEMBLIES", "CORECLR_PROFILER", "COR_PROFILER", "OTEL_DOTNET_AUTO_HOME" }
            .Any(name => environment(name) is not null) || environment("CORECLR_ENABLE_PROFILING") is not (null or "0"))
            throw Failure();
        var listen = IPEndPoint.TryParse(Required(Listen), out var endpoint) && endpoint.Port != 0 && Required(Listen) == endpoint.ToString()
            ? endpoint : throw Failure();
        X509Certificate2? certificate = null;
        var chain = new X509Certificate2Collection();
        switch (Required(Transport))
        {
            case "platformIngress":
                if (Optional(TlsCertificateFile) is not null || Optional(TlsPrivateKeyFile) is not null) throw Failure();
                break;
            case "directHttps":
                var pem = ProtectedHostFiles.ReadText(Required(TlsCertificateFile), 65_536);
                certificate = X509Certificate2.CreateFromPem(pem, ProtectedHostFiles.ReadText(Required(TlsPrivateKeyFile), 65_536));
                if (!certificate.HasPrivateKey || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow) throw Failure();
                chain.ImportFromPem(pem);
                break;
            default: throw Failure();
        }
        var concurrency = Optional(Concurrency) is { } text
            ? text.Length == 1 && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count is >= 1 and <= MaximumConcurrency
                ? count : throw Failure()
            : MaximumConcurrency;
        var ids = Target.Select(name => Id(Required(name))).ToArray();
        var versionText = Required(EnvironmentVersion);
        if (!long.TryParse(versionText, NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version < 1 ||
            versionText != version.ToString(CultureInfo.InvariantCulture))
            throw Failure();
        var target = new ReleaseTargetReference(new(ids[0], ids[1], ids[2]), new(new(ids[0], ids[1]), ids[3], version), ids[4]);
        var tenant = Id(Required(TenantSetting));
        var audience = Id(Required(AudienceSetting));
        // The token options' own admission (organizational tenant, exact audience) applies at startup.
        try { _ = new EntraMachineTokenOptions(tenant, audience); }
        catch (ArgumentException) { throw Failure(); }
        var runtimeImage = Image(Required(RuntimeImageSetting));
        var compilerImage = Image(Required(CompilerImageSetting));
        var selection = MappingRuntimeSelection.Read(environment, runtimeImage, compilerImage);
        var connections = new Dictionary<string, string>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (setting, role) in Logins)
        {
            if (environment(setting) is not null || !paths.Add(Required(setting + "_FILE"))) throw Failure();
            connections[role] = Connection(Required(setting + "_FILE"), role, environment);
        }
        return new(listen, certificate, chain, concurrency, target, tenant, audience, runtimeImage,
            selection.CompilerIdentity, selection.Docker, connections, selection);
    }

    private static string Connection(string path, string role, Func<string, string?> environment)
    {
        var delivered = ProtectedHostFiles.ReadText(path, 16_384, removeTerminalLf: true);
        var connection = new NpgsqlConnectionStringBuilder(delivered);
        // Exactly the expected restricted login with an explicit password; no client identity, passfile or GSS fallback.
        if (connection.Username != role || string.IsNullOrEmpty(connection.Password) || connection.Passfile is not null ||
            connection.SslCertificate is not null || connection.SslKey is not null || connection.SslPassword is not null ||
            new System.Data.Common.DbConnectionStringBuilder { ConnectionString = delivered }.ContainsKey("GSS Encryption Mode") &&
                connection.GssEncryptionMode != GssEncryptionMode.Disable)
            throw Failure();
        if (connection.RequireAuth is { } methods && methods.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Any(method => method is not ("Password" or "MD5" or "ScramSHA256")))
            throw Failure();
        connection.GssEncryptionMode = GssEncryptionMode.Disable;
        connection.RequireAuth ??= "Password,MD5,ScramSHA256";
        if (connection.SslMode != SslMode.Disable && environment("HOME") is { } home && File.Exists(Path.Combine(home, ".postgresql", "postgresql.crt")))
            throw Failure();
        return connection.ConnectionString;
    }

    private static Guid Id(string value) =>
        Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty && value == id.ToString("D") ? id : throw Failure();

    private static string Image(string value) =>
        value.Length == 71 && value.StartsWith("sha256:", StringComparison.Ordinal) && value[7..].All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'))
            ? value : throw Failure();

    internal static InvalidOperationException Failure() => new("The mapping endpoint configuration is invalid.");

    public override string ToString() => nameof(MappingEndpointSettings);
}
