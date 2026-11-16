using System.Runtime.Versioning;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Braid.Application;
using Braid.Domain;
using Braid.Identity;
using Braid.Persistence.Postgres;

namespace Braid.SessionTicketProcessProbe;

/// <summary>Test-only, one-shot private pipe protocol. No HTTP, listener, arguments or secret environment.</summary>
[SupportedOSPlatform("linux")]
internal static class Program
{
    private const int MaximumInput = 65536;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };

    private static async Task<int> Main(string[] arguments)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        X509Certificate2? current = null;
        var overlaps = new List<X509Certificate2>();
        DurableSessionKeyProfile? profile = null;
        PostgresServerSessionDatabase? readerDatabase = null;
        PostgresServerSessionDatabase? writerDatabase = null;
        var readerCalls = 0; var createCalls = 0; var renewCalls = 0; var revokeCalls = 0;
        var preliminaryRetrieve = false;
        var outcome = "failed"; string? failureKind = null;
        var cleanupFailed = false;
        byte[]? wire = null;
        try
        {
            if (arguments.Length != 0) throw new ProbeFailure();
            wire = await ReadInputAsync(lifetime.Token);
            using (var document = JsonDocument.Parse(wire, new JsonDocumentOptions { MaxDepth = 8 }))
            {
                string[] names = ["schemaVersion", "action", "privateRoot", "outputFile", "keyDirectory", "applicationName",
                    "organizationId", "readerConnectionString", "writerConnectionString", "currentCertificate",
                    "overlapCertificates", "certificatePassword", "key", "bindingId", "bindingVersion", "principalId", "verifiedDeadlineUtc"];
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)
                        .SequenceEqual(names.Order(StringComparer.Ordinal))) throw new ProbeFailure();
            }
            var input = JsonSerializer.Deserialize<Input>(wire, Json) ?? throw new ProbeFailure();
            Validate(input);
            current = LoadCertificate(input.PrivateRoot, input.CurrentCertificate, input.CertificatePassword);
            foreach (var file in input.OverlapCertificates)
                overlaps.Add(LoadCertificate(input.PrivateRoot, file, input.CertificatePassword));
            profile = DurableSessionKeyProfile.Open(new(input.OrganizationId, input.ApplicationName, input.KeyDirectory),
                current, overlaps);
            readerDatabase = new(input.ReaderConnectionString, ServerSessionDatabaseProfile.Reader);
            writerDatabase = new(input.WriterConnectionString, ServerSessionDatabaseProfile.Writer);
            var reader = new CountingReader(new PostgresServerSessionReader(readerDatabase), () => readerCalls++);
            var writer = new CountingWriter(new PostgresServerSessionWriter(writerDatabase),
                () => createCalls++, () => renewCalls++, () => revokeCalls++);
            var store = new ServerSessionTicketStore(profile, reader, writer);
            ITicketStore adapter = store;
            var context = new DefaultHttpContext();
            var result = new PrivateResult();
            if (input.Action == "issue")
            {
                using var issuance = store.BeginTrustedIssuance(context, input.BindingId, input.BindingVersion,
                    new PrincipalId(input.PrincipalId), input.VerifiedDeadlineUtc, lifetime.Token);
                var ticket = issuance.CopySignInTicket();
                result.Key = await adapter.StoreAsync(ticket, context, lifetime.Token);
                SetTicket(result, ticket);
            }
            else if (input.Action == "retrieve")
                SetTicket(result, await adapter.RetrieveAsync(input.Key!, context, lifetime.Token));
            else if (input.Action == "renew")
            {
                // This preliminary read is explicitly counted separately from
                // the adapter's own fresh Renew observation. No ticket is fabricated.
                preliminaryRetrieve = true;
                var ticket = await adapter.RetrieveAsync(input.Key!, context, lifetime.Token);
                if (ticket is null) throw new ProbeNotLive();
                await adapter.RenewAsync(input.Key!, ticket, context, lifetime.Token);
                result.Found = true;
            }
            else
                await adapter.RemoveAsync(input.Key!, context, lifetime.Token);
            lifetime.Token.ThrowIfCancellationRequested();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(result, Json);
            try
            {
                if (bytes.Length > 8192) throw new ProbeFailure();
                using var output = new FileStream(input.OutputFile, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
                });
                await output.WriteAsync(bytes, lifetime.Token);
                await output.FlushAsync(lifetime.Token);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            outcome = "succeeded";
        }
        catch (ServerSessionIdentityException error)
        {
            outcome = "refused";
            failureKind = Enum.IsDefined(error.Kind) ? error.Kind.ToString() : "Unavailable";
        }
        catch (ProbeNotLive) { outcome = "refused"; failureKind = "Denied"; }
        catch (OperationCanceledException) { failureKind = "Canceled"; }
        catch (Exception) { failureKind = "ProbeFailure"; }
        finally
        {
            if (wire is not null) CryptographicOperations.ZeroMemory(wire);
            // Every owned operation has actually settled before these pools.
            if (writerDatabase is not null)
                try { await writerDatabase.DisposeAsync(); } catch (Exception) { cleanupFailed = true; outcome = "failed"; failureKind ??= "CleanupFailure"; }
            if (readerDatabase is not null)
                try { await readerDatabase.DisposeAsync(); } catch (Exception) { cleanupFailed = true; outcome = "failed"; failureKind ??= "CleanupFailure"; }
            try { profile?.Dispose(); } catch (Exception) { cleanupFailed = true; outcome = "failed"; failureKind ??= "CleanupFailure"; }
            foreach (var certificate in overlaps)
                try { certificate.Dispose(); } catch (Exception) { cleanupFailed = true; outcome = "failed"; failureKind ??= "CleanupFailure"; }
            try { current?.Dispose(); } catch (Exception) { cleanupFailed = true; outcome = "failed"; failureKind ??= "CleanupFailure"; }
        }
        // Only closed categories and numeric counts leave the private channel.
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            schemaVersion = "braid.session-ticket-process-result.v1",
            outcome,
            failureKind,
            preliminaryRetrievePerformed = preliminaryRetrieve,
            readerCalls,
            createCalls,
            renewCalls,
            revokeCalls,
            cleanupFailed
        }));
        return outcome == "succeeded" ? 0 : 2;
    }

    private static async Task<byte[]> ReadInputAsync(CancellationToken token)
    {
        using var input = Console.OpenStandardInput();
        var bytes = new byte[MaximumInput + 1];
        var count = 0;
        try
        {
            while (count < bytes.Length)
            {
                var read = await input.ReadAsync(bytes.AsMemory(count), token);
                if (read == 0) break;
                count += read;
            }
            if (count == 0 || count > MaximumInput) throw new ProbeFailure();
            return bytes.AsSpan(0, count).ToArray();
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static void Validate(Input input)
    {
        if (input.SchemaVersion != "braid.session-ticket-process-input.v1" ||
            input.Action is not ("issue" or "retrieve" or "renew" or "revoke") ||
            input.OrganizationId == Guid.Empty || input.OverlapCertificates.Length > 3 ||
            string.IsNullOrEmpty(input.ReaderConnectionString) || string.IsNullOrEmpty(input.WriterConnectionString) ||
            string.IsNullOrEmpty(input.CertificatePassword) ||
            !DirectPrivateRoot(input.PrivateRoot) ||
            !DirectChild(input.PrivateRoot, input.KeyDirectory) ||
            !DirectChild(input.PrivateRoot, input.OutputFile) || File.Exists(input.OutputFile) ||
            Directory.Exists(input.OutputFile)) throw new ProbeFailure();
        if (input.Action == "issue")
        {
            if (input.Key is not null || input.BindingId == Guid.Empty || input.BindingVersion < 1 ||
                input.PrincipalId == Guid.Empty || input.VerifiedDeadlineUtc.Offset != TimeSpan.Zero ||
                input.VerifiedDeadlineUtc.Ticks % 10 != 0) throw new ProbeFailure();
        }
        else if (string.IsNullOrEmpty(input.Key) || input.BindingId != Guid.Empty || input.BindingVersion != 0 ||
                 input.PrincipalId != Guid.Empty || input.VerifiedDeadlineUtc != default) throw new ProbeFailure();
    }

    private static bool DirectPrivateRoot(string root) => Path.IsPathFullyQualified(root) &&
        Path.GetFullPath(root) == root && new DirectoryInfo(root).Exists &&
        new DirectoryInfo(root).LinkTarget is null && File.GetUnixFileMode(root) ==
            (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

    private static bool DirectChild(string root, string path) => Path.IsPathFullyQualified(path) &&
        Path.GetFullPath(path) == path && string.Equals(Path.GetDirectoryName(path), root, StringComparison.Ordinal);

    private static X509Certificate2 LoadCertificate(string root, string file, string password)
    {
        if (!DirectChild(root, file)) throw new ProbeFailure();
        var info = new FileInfo(file);
        if (!info.Exists || info.LinkTarget is not null || info.Length is < 1 or > MaximumInput ||
            File.GetUnixFileMode(file) != (UnixFileMode.UserRead | UnixFileMode.UserWrite)) throw new ProbeFailure();
        return X509CertificateLoader.LoadPkcs12FromFile(file, password, X509KeyStorageFlags.EphemeralKeySet);
    }

    private static void SetTicket(PrivateResult result, AuthenticationTicket? ticket)
    {
        result.Found = ticket is not null;
        if (ticket is null) return;
        var claims = ticket.Principal.Claims.ToArray();
        if (claims.Length != 3) throw new ProbeFailure();
        result.Principal = claims.Single(c => c.Type == ClaimTypes.NameIdentifier).Value;
        result.Organization = claims.Single(c => c.Type == "braid:organization_id").Value;
        result.Epoch = claims.Single(c => c.Type == "braid:login_epoch").Value;
        result.IssuedUtc = ticket.Properties.IssuedUtc;
        result.ExpiresUtc = ticket.Properties.ExpiresUtc;
        // Issue deliberately retains the Issue scheme; retrieval returns Session.
        result.Canonical = ticket.AuthenticationScheme == (result.Key is null ? ServerSessionSchemes.Session : ServerSessionSchemes.Issue) &&
            ticket.Principal.Identities.Count() == 1 &&
            ticket.Principal.Identity?.AuthenticationType == ServerSessionSchemes.Session &&
            claims.All(c => c.Issuer == ServerSessionSchemes.Session && c.OriginalIssuer == ServerSessionSchemes.Session && c.Properties.Count == 0) &&
            !ticket.Properties.IsPersistent && ticket.Properties.AllowRefresh == false &&
            ticket.Properties.Parameters.Count == 0;
    }

    private sealed class Input
    {
        public Input() { }
        public string SchemaVersion { get; set; } = "";
        public string Action { get; set; } = "";
        public string PrivateRoot { get; set; } = "";
        public string OutputFile { get; set; } = "";
        public string KeyDirectory { get; set; } = "";
        public string ApplicationName { get; set; } = "";
        public Guid OrganizationId { get; set; }
        public string ReaderConnectionString { get; set; } = "";
        public string WriterConnectionString { get; set; } = "";
        public string CurrentCertificate { get; set; } = "";
        public string[] OverlapCertificates { get; set; } = [];
        public string CertificatePassword { get; set; } = "";
        public string? Key { get; set; }
        public Guid BindingId { get; set; }
        public long BindingVersion { get; set; }
        public Guid PrincipalId { get; set; }
        public DateTimeOffset VerifiedDeadlineUtc { get; set; }
    }

    private sealed class PrivateResult
    {
        public string SchemaVersion { get; } = "braid.session-ticket-process-private.v1";
        public string? Key { get; set; }
        public bool Found { get; set; }
        public bool Canonical { get; set; }
        public string? Principal { get; set; }
        public string? Organization { get; set; }
        public string? Epoch { get; set; }
        public DateTimeOffset? IssuedUtc { get; set; }
        public DateTimeOffset? ExpiresUtc { get; set; }
        public override string ToString() => nameof(PrivateResult);
    }

    private sealed class CountingReader(IServerSessionReader actual, Action count) : IServerSessionReader
    {
        public Task<CurrentServerSession?> ObserveAsync(ServerSessionKey key, CancellationToken cancellationToken = default)
        { count(); return actual.ObserveAsync(key, cancellationToken); }
    }

    private sealed class CountingWriter(IServerSessionWriter actual, Action create, Action renew, Action revoke) : IServerSessionWriter
    {
        public Task<ServerSessionWriteResult> CreateOnlyAsync(ServerSessionCreateIntent intent, CancellationToken cancellationToken = default)
        { create(); return actual.CreateOnlyAsync(intent, cancellationToken); }
        public Task<ServerSessionWriteResult> RenewAsync(ServerSessionKey key, long expectedVersion, CancellationToken cancellationToken = default)
        { renew(); return actual.RenewAsync(key, expectedVersion, cancellationToken); }
        public Task<ServerSessionWriteResult> RevokeAsync(ServerSessionKey key, CancellationToken cancellationToken = default)
        { revoke(); return actual.RevokeAsync(key, cancellationToken); }
    }

    private sealed class ProbeFailure : Exception { }
    private sealed class ProbeNotLive : Exception { }
}
