using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Weft.Api.Auth;
using Weft.Api.Catalog;
using Weft.Application;
using Weft.Identity;
using Weft.MappingBackends.Workers;
using Weft.Persistence.Postgres;

namespace Weft.Server;

/// <summary>Manual owner of borrowed certificates, the existing key profile and three separate lazy pools.</summary>
internal sealed class SessionHostResources
{
    private readonly List<X509Certificate2> certificates = [];
    private readonly List<PostgresServerSessionDatabase> sessionDatabases = [];
    private PostgresExternalIdentityDatabase? identityDatabase;
    private PostgresApiRequestAuditWriter? auditWriter;
    private EntraUserTokenValidator? userTokens;
    private DurableSessionKeyProfile? profile;
    internal X509Certificate2? TlsCertificate { get; private set; }
    internal SslStreamCertificateContext? TlsCertificateContext { get; private set; }

    internal void PrepareAndRegister(IServiceCollection services, IWebHostEnvironment environment,
        ServerHostOptions options, Func<string, string?> read)
    {
        // Keep every successful acquisition in this owner immediately, including partial startup.
        var current = Certificate(read, "WEFT_SESSION_KEY_CERTIFICATE_FILE", "WEFT_SESSION_KEY_PRIVATE_KEY_FILE", true, true);
        var overlap = new List<X509Certificate2>();
        if (read("WEFT_SESSION_DECRYPT_CERTIFICATES_FILE") is { } overlapFile)
        {
            using var document = JsonDocument.Parse(HostPrivateFiles.ReadText(overlapFile, 16_384),
                new JsonDocumentOptions { MaxDepth = 4, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() > 8)
                throw ServerHostOptions.Failure();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) throw ServerHostOptions.Failure();
                string? cert = null, key = null;
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in item.EnumerateObject())
                {
                    if (!names.Add(property.Name) || property.Value.ValueKind != JsonValueKind.String)
                        throw ServerHostOptions.Failure();
                    if (property.Name == "certificateFile") cert = property.Value.GetString();
                    else if (property.Name == "privateKeyFile") key = property.Value.GetString();
                    else throw ServerHostOptions.Failure();
                }
                if (names.Count != 2 || string.IsNullOrEmpty(cert) || string.IsNullOrEmpty(key)) throw ServerHostOptions.Failure();
                overlap.Add(CertificateFiles(cert, key, currentRequired: false, requireRsa: true));
            }
        }
        profile = DurableSessionKeyProfile.Open(new(options.OrganizationId, options.ApplicationName!, options.KeyDirectory!), current, overlap);
        var clientCertificate = Certificate(read, "WEFT_ENTRA_CLIENT_CERTIFICATE_FILE", "WEFT_ENTRA_CLIENT_PRIVATE_KEY_FILE", true, true);
        if (!options.Proxy)
        {
            var pem = HostPrivateFiles.ReadText(ServerHostOptions.Required(read, "WEFT_TLS_CERTIFICATE_FILE"), 65_536);
            var tls = CertificatePem(pem, HostPrivateFiles.ReadText(
                ServerHostOptions.Required(read, "WEFT_TLS_PRIVATE_KEY_FILE"), 65_536), true, false);
            TlsCertificate = tls;
            if (!tls.MatchesHostname(new Uri(options.Origin).Host.Trim('[', ']'),
                allowWildcards: true, allowCommonName: false)) throw ServerHostOptions.Failure();
            // Preserve Kestrel's server-auth EKU admission when using its callback TLS API.
            var usages = tls.Extensions.OfType<X509EnhancedKeyUsageExtension>().ToArray();
            if (usages.Length != 0 && !usages.Any(extension => extension.EnhancedKeyUsages
                .Cast<Oid>().Any(oid => oid.Value == "1.3.6.1.5.5.7.3.1"))) throw ServerHostOptions.Failure();
            // The same protected PEM may append the public issuer chain after its leaf. Capture
            // partial imports too; neither AIA chain completion nor background OCSP is allowed.
            var chain = new X509Certificate2Collection();
            try { chain.ImportFromPem(pem); }
            finally { certificates.AddRange(chain.Cast<X509Certificate2>()); }
            TlsCertificateContext = SslStreamCertificateContext.Create(tls, chain, offline: true);
        }
        // Native admission and derivation forward exact frames to the separately hosted worker.
        X509Certificate2? inspectionClient = null;
        if (options.InspectionWorker is not null)
            inspectionClient = Certificate(read, SessionInspectionWorker.ClientCertificateFile, SessionInspectionWorker.ClientPrivateKeyFile, true, true);
        if (options.FerruleService is not null)
            inspectionClient = Certificate(read, FerruleServiceEndpoint.ClientCertificateFile, FerruleServiceEndpoint.ClientPrivateKeyFile, true, true);
        // Admit all three deliveries before constructing even a lazy pool owner.
        var readerConnection = ReadConnection(read, "WEFT_SESSION_READER_CONNECTION_STRING_FILE");
        var writerConnection = ReadConnection(read, "WEFT_SESSION_WRITER_CONNECTION_STRING_FILE");
        var identityConnection = ReadConnection(read, "WEFT_IDENTITY_READER_CONNECTION_STRING_FILE");
        var auditConnection = options.Bearer is null ? null : ReadProtectedConnection(read, ServerHostOptions.BearerAuditSetting);
        var readerDatabase = new PostgresServerSessionDatabase(readerConnection,
            ServerSessionDatabaseProfile.Reader);
        sessionDatabases.Add(readerDatabase);
        var writerDatabase = new PostgresServerSessionDatabase(writerConnection,
            ServerSessionDatabaseProfile.Writer);
        sessionDatabases.Add(writerDatabase);
        identityDatabase = new(identityConnection);
        var identities = new PostgresExternalIdentityReader(identityDatabase);
        var reader = new PostgresServerSessionReader(readerDatabase);
        var writer = new PostgresServerSessionWriter(writerDatabase);
        // Implementation instances are borrowed; the DI container must not own these pools/profile/certificates.
        services.AddSingleton<IExternalIdentityReader>(identities);
        services.AddWeftBrowserSessions(new(options.Origin), profile, reader, writer, TimeProvider.System);
        services.AddWeftEntraClient(new(options.TenantId, options.ClientId, options.Origin), profile, clientCertificate);
        services.AddWeftEntraBrowserAuthentication(new(options.OrganizationId, options.Origin), profile, identities);
        services.AddWeftAuthenticationHttp(new(AuthenticationHttpMode.Session, options.Origin), environment);
        services.AddWeftSessionCatalog(new(options.OrganizationId), options.Metadata, options.Ingestion, options.Creation, options.Reports);
        if (options.Bearer is { } bearer)
        {
            // Borrowed by the admission: the validator only fetches the fixed tenant's discovery and keys on first use.
            auditWriter = new(auditConnection!);
            userTokens = new(bearer);
            services.AddWeftBearerAccess(new(options.OrganizationId), userTokens, identities, auditWriter);
        }
        if (options.InspectionWorker is { } inspection)
            InspectionWorkerHostConfiguration.ConfigureRemote(services, inspection.Options(inspectionClient!));
        if (options.FerruleService is { } service)
            InspectionWorkerHostConfiguration.ConfigureService(services, service, inspectionClient!);
        if (options.FerruleSessions is { } sessions)
            InspectionWorkerHostConfiguration.ConfigureConnector(services, _ => sessions.CreateConnector(), sessions.BuildIdentity, sessions.CompileToolchainIdentity);
        CatalogBusinessServices.Configure(services, environment, options.Metadata, options.Ingestion, options.Creation, options.Reports, read,
            options.NativeByteLimit, options.DerivedReadByteLimit, options.Builds);
        ChatHostConfiguration.Configure(services, read, options.Chat, true, options.Reports, session: true);
        CatalogPreviewHostConfiguration.Configure(services, options.Previews, read, session: true);
        CatalogMappingEditingHostConfiguration.Configure(services, read, true, options.Ingestion.Enabled, options.Reports.Enabled,
            options.Reports.DerivationEnabled, session: true);
        SessionHostDataProtectionPolicy.Apply(services);
    }

    /// <summary>
    /// Admits a feature credential only through its protected <c>NAME_FILE</c> delivery. A plain
    /// <c>NAME</c> environment credential is a second, unprotected delivery and refuses startup
    /// instead of being ignored or preferred.
    /// </summary>
    internal static string ReadProtectedConnection(Func<string, string?> read, string name)
    {
        if (read(name) is not null) throw ServerHostOptions.Failure();
        return ReadConnection(read, name + "_FILE");
    }

    internal static string ReadConnection(Func<string, string?> read, string name)
    {
        try
        {
            // Explicit passwords must not coexist with another credential-delivery mechanism.
            if (new[] { "PGUSER", "PGPASSWORD", "PGPASSFILE", "PGSSLCERT", "PGSSLKEY", "PGREQUIREAUTH", "PGGSSENCMODE" }
                .Any(variable => read(variable) is not null)) throw ServerHostOptions.Failure();
            var delivered = HostPrivateFiles.ReadText(
                ServerHostOptions.Required(read, name), 16_384, removeTerminalLf: true);
            var selectors = new ConnectionDeliverySelectors { ConnectionString = delivered };
            if (selectors.ForbiddenCredentialProvided || selectors.EmptyAuthenticationProvided)
                throw ServerHostOptions.Failure();
            var connection = new NpgsqlConnectionStringBuilder(delivered);
            if (string.IsNullOrEmpty(connection.Username) || string.IsNullOrEmpty(connection.Password) ||
                connection.Passfile is not null || connection.SslCertificate is not null || connection.SslKey is not null ||
                connection.SslPassword is not null)
                throw ServerHostOptions.Failure();
            // Npgsql's omitted GSS mode prefers ambient Kerberos. Reject an explicit competing mode,
            // then pin the absent default off; RequireAuth also excludes server-selected GSS/SSPI/trust.
            var supplied = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = connection.ConnectionString };
            if (supplied.ContainsKey("GSS Encryption Mode") && connection.GssEncryptionMode != GssEncryptionMode.Disable)
                throw ServerHostOptions.Failure();
            if (connection.RequireAuth is { } methods)
            {
                var modes = methods.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (modes.Length == 0 || modes.Any(method => method is not ("Password" or "MD5" or "ScramSHA256")))
                    throw ServerHostOptions.Failure();
            }
            connection.GssEncryptionMode = GssEncryptionMode.Disable;
            connection.RequireAuth ??= "Password,MD5,ScramSHA256";
            // TLS must not auto-load a default client identity from HOME. A default pgpass is
            // unreachable because the admitted nonempty password is retained verbatim.
            if (connection.SslMode != SslMode.Disable && read("HOME") is { } home &&
                File.Exists(Path.Combine(home, ".postgresql", "postgresql.crt"))) throw ServerHostOptions.Failure();
            return connection.ConnectionString;
        }
        catch (Exception error) when (Nonfatal(error)) { throw ServerHostOptions.Failure(); }
    }

    // The framework parser calls Remove for unquoted empty values and the indexer for quoted
    // empties/nonempty values. Observe every parsed field before normalization or deduplication;
    // selector-like text inside quoted credentials never becomes a keyword here.
    private sealed class ConnectionDeliverySelectors : DbConnectionStringBuilder
    {
        internal bool ForbiddenCredentialProvided { get; private set; }
        internal bool EmptyAuthenticationProvided { get; private set; }

        [AllowNull]
        public override object this[string keyword]
        {
            get => base[keyword];
            set
            {
                Observe(keyword, value);
                base[keyword] = value;
            }
        }

        public override bool Remove(string keyword)
        {
            Observe(keyword, null);
            return base.Remove(keyword);
        }

        private void Observe(string keyword, object? value)
        {
            // Npgsql 10.0.3 accepts each display name and its CLR-property-name alias.
            switch (keyword.ToUpperInvariant())
            {
                case "PASSFILE":
                case "SSL CERTIFICATE":
                case "SSLCERTIFICATE":
                case "SSL KEY":
                case "SSLKEY":
                case "SSL PASSWORD":
                case "SSLPASSWORD":
                    ForbiddenCredentialProvided = true;
                    break;
                case "REQUIRE AUTH":
                case "REQUIREAUTH":
                case "GSS ENCRYPTION MODE":
                case "GSSENCRYPTIONMODE":
                    if (string.IsNullOrWhiteSpace(value as string)) EmptyAuthenticationProvided = true;
                    break;
            }
        }

        public override string ToString() => nameof(ConnectionDeliverySelectors);
    }

    private X509Certificate2 Certificate(Func<string, string?> read, string certName, string keyName,
        bool currentRequired, bool requireRsa) => CertificateFiles(ServerHostOptions.Required(read, certName),
            ServerHostOptions.Required(read, keyName), currentRequired, requireRsa);

    private X509Certificate2 CertificateFiles(string certPath, string keyPath, bool currentRequired, bool requireRsa)
        => CertificatePem(HostPrivateFiles.ReadText(certPath, 65_536), HostPrivateFiles.ReadText(keyPath, 65_536),
            currentRequired, requireRsa);

    private X509Certificate2 CertificatePem(string certificatePem, string keyPem, bool currentRequired, bool requireRsa)
    {
        var certificate = X509Certificate2.CreateFromPem(certificatePem, keyPem);
        certificates.Add(certificate);
        if (!certificate.HasPrivateKey || currentRequired &&
            (DateTime.UtcNow < certificate.NotBefore.ToUniversalTime() || DateTime.UtcNow >= certificate.NotAfter.ToUniversalTime()))
            throw ServerHostOptions.Failure();
        if (requireRsa)
        {
            using var privateKey = certificate.GetRSAPrivateKey();
            using var publicKey = certificate.GetRSAPublicKey();
            if (privateKey is null || publicKey is null || privateKey.KeySize < 2048) throw ServerHostOptions.Failure();
            var probe = RandomNumberGenerator.GetBytes(32);
            byte[]? signature = null;
            try
            {
                signature = privateKey.SignData(probe, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                if (!publicKey.VerifyData(probe, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                    throw ServerHostOptions.Failure();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(probe);
                if (signature is not null) CryptographicOperations.ZeroMemory(signature);
            }
        }
        return certificate;
    }

    // Call only after the exact host/request/module/provider tasks have proved settlement, or before a provider existed.
    internal async Task DisposeAfterSettlementAsync()
    {
        Exception? first = null;
        if (userTokens is not null)
            try { await userTokens.DisposeAsync(); }
            catch (Exception error) when (Nonfatal(error)) { first ??= error; }
        if (auditWriter is not null)
            try { await auditWriter.DisposeAsync(); }
            catch (Exception error) when (Nonfatal(error)) { first ??= error; }
        foreach (var database in sessionDatabases)
            try { await database.DisposeAsync(); }
            catch (Exception error) when (Nonfatal(error)) { first ??= error; }
        if (identityDatabase is not null)
            try { await identityDatabase.DisposeAsync(); }
            catch (Exception error) when (Nonfatal(error)) { first ??= error; }
        try { profile?.Dispose(); }
        catch (Exception error) when (Nonfatal(error)) { first ??= error; }
        foreach (var certificate in certificates)
            try { certificate.Dispose(); }
            catch (Exception error) when (Nonfatal(error)) { first ??= error; }
        if (first is not null) throw new InvalidOperationException("The host resources did not close cleanly.");
    }

    internal static bool Nonfatal(Exception error) => error is not (OutOfMemoryException or StackOverflowException or AccessViolationException);
    public override string ToString() => nameof(SessionHostResources);
}
