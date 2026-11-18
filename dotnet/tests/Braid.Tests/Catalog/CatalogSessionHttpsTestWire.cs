using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Net.Http.Headers;

namespace Braid.Tests;

[SupportedOSPlatform("linux")]
internal sealed class CatalogSessionCookies
{
    internal string? Session, Antiforgery;
    internal string Header => string.Join("; ", new[] {
        Session is null ? null : CatalogSessionHttpsFixture.SessionCookie + "=" + Session,
        Antiforgery is null ? null : CatalogSessionHttpsFixture.AntiforgeryCookie + "=" + Antiforgery
    }.Where(value => value is not null));
    internal CatalogSessionCookies Copy() => new() { Session = Session, Antiforgery = Antiforgery };
    internal void Apply(CatalogSessionReply response)
    {
        if (!response.Headers.TryGetValue("Set-Cookie", out var headers)) return;
        foreach (var value in SetCookieHeaderValue.ParseList(headers.ToList()))
        {
            string name = value.Name.ToString(), cookie = value.Value.ToString();
            if (name == CatalogSessionHttpsFixture.SessionCookie) Session = cookie.Length == 0 ? null : cookie;
            else if (name == CatalogSessionHttpsFixture.AntiforgeryCookie) Antiforgery = cookie.Length == 0 ? null : cookie;
            else CatalogSessionHttpsFixture.Check(false, "The session catalog fixture emitted an unexpected cookie name.");
        }
    }
    public override string ToString() => nameof(CatalogSessionCookies);
}
internal sealed class CatalogSessionReply(HttpStatusCode status, IReadOnlyDictionary<string, string[]> headers, byte[] body, CatalogSessionRequestRecord record)
{
    internal HttpStatusCode Status { get; } = status;
    internal IReadOnlyDictionary<string, string[]> Headers { get; } = headers;
    internal byte[] Body { get; } = body;
    internal CatalogSessionRequestRecord Record { get; } = record;
    internal JsonElement Json()
    { using var document = JsonDocument.Parse(Body); return document.RootElement.Clone(); }
    internal bool NoStore => Headers.TryGetValue("Cache-Control", out var values) && values.Any(value => value.Split(',').Any(part => part.Trim().Equals("no-store", StringComparison.OrdinalIgnoreCase)));
    public override string ToString() => nameof(CatalogSessionReply);
}
internal sealed class CatalogSessionUnsafeRoute(string family, string method, string path, string mediaType, bool key = false, bool tag = false)
{
    internal string Family { get; } = family;
    internal string Method { get; } = method;
    internal string Path { get; } = path;
    internal string MediaType { get; } = mediaType;
    internal bool Key { get; } = key;
    internal bool Tag { get; } = tag;
    public override string ToString() => Family;
}

[SupportedOSPlatform("linux")]
internal sealed partial class CatalogSessionHttpsFixture
{
    internal string ProjectPath => CatalogHttpTestSupport.ProjectPath(new(Data.Scope.OrganizationId, Data.Scope.ProjectId));
    internal string MappingPath => CatalogHttpTestSupport.MappingPath(Data.Original.Facts.FutureRevision);
    internal string MetadataPath => MappingPath + "/metadata";
    internal CatalogSessionRequestRecord NewObservation(CatalogSessionBodyGate? bodyGate = null, bool seedEtag = false)
    {
        int id = Interlocked.Increment(ref requestSequence);
        Check(id <= 256, "The owned HTTPS fixture exceeded its fixed request bound.");
        var record = new CatalogSessionRequestRecord { Id = id, BodyGate = bodyGate, SeedEtag = seedEtag };
        Check(records.TryAdd(id, record), "The owned HTTPS request identity was reused."); return record;
    }
    internal CatalogSessionUnsafeRoute[] UnsafeRoutes() => [
        new("project-create", "POST", $"/api/v1/organizations/{Data.Scope.OrganizationId:D}/projects", "application/vnd.braid.project-create.v1+json", key: true),
        new("metadata-patch", "PATCH", MetadataPath, MetadataHttpTestSupport.MediaType, tag: true),
        new("native-create", "POST", ProjectPath + "/mappings?name=Owned&backendId=ferrule", "application/octet-stream", key: true),
        new("native-append", "POST", MappingPath + "/revisions?head=any", "application/octet-stream", key: true),
        new("native-resume", "POST", ProjectPath + "/revision-ingestions/" + Guid.NewGuid().ToString("D") + "/resume", "application/json"),
        new("projection-ensure", "POST", CatalogHttpTestSupport.RevisionPath(Data.Original.Facts.FutureRevision) + "/reports/projection/ensure", "application/json"),
        new("validation-ensure", "POST", CatalogHttpTestSupport.RevisionPath(Data.Original.Facts.FutureRevision) + "/reports/validation/ensure", "application/json")
    ];
    internal async Task<CatalogSessionReply> SendAsync(string path, CatalogSessionCookies? cookies = null,
        string method = "GET", string? token = null, byte[]? body = null, string? tag = null,
        string? mediaType = null, bool forgedHeaders = false, CancellationToken cancellationToken = default,
        CatalogSessionRequestRecord? observation = null, bool idempotencyKey = false, string? authorization = null)
    {
        var record = observation ?? NewObservation();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); budget.CancelAfter(TimeSpan.FromSeconds(15));
        using var message = new HttpRequestMessage(new HttpMethod(method), path);
        message.Headers.Add(FixtureHeader, record.Id.ToString(CultureInfo.InvariantCulture));
        message.Headers.TryAddWithoutValidation("Origin", Origin.GetLeftPart(UriPartial.Authority));
        if (cookies is not null && cookies.Header.Length > 0) message.Headers.TryAddWithoutValidation("Cookie", cookies.Header);
        if (token is not null) message.Headers.TryAddWithoutValidation(TokenHeader, token);
        if (tag is not null) message.Headers.TryAddWithoutValidation("If-Match", tag);
        if (idempotencyKey) message.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString("D"));
        if (authorization is not null) message.Headers.TryAddWithoutValidation("Authorization", authorization);
        if (forgedHeaders)
        {
            message.Headers.TryAddWithoutValidation("X-Braid-Principal-Id", Data.Seed.OtherProjectAdmin.ToString("D"));
            message.Headers.TryAddWithoutValidation("X-Braid-Organization-Id", Data.Seed.OrganizationB.ToString("D"));
            message.Headers.TryAddWithoutValidation("X-Braid-Role", "administrator");
        }
        if (body is not null)
        {
            Check(body.Length <= 65_536, "The owned request body exceeded its bound.");
            message.Content = new ByteArrayContent(body);
            string type = mediaType ?? MetadataHttpTestSupport.MediaType;
            if (type.Length != 0) message.Content.Headers.ContentType = new(type);
        }
        using var response = await client!.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, budget.Token);
        await using var input = await response.Content.ReadAsStreamAsync(budget.Token);
        byte[] bytes = await ReadBodyAsync(input, budget.Token);
        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers.Concat(response.Content.Headers)) headers[header.Key] = header.Value.ToArray();
        await record.Ended.Task.WaitAsync(TimeSpan.FromSeconds(5), budget.Token);
        Check(!record.UnexpectedFailure, "An owned catalog request escaped its closed response boundary.");
        return new(response.StatusCode, headers, bytes, record);
    }
    internal async Task<CatalogSessionCookies> IssueAsync(CatalogSessionCookies? previous = null)
    {
        var cookies = previous?.Copy() ?? new CatalogSessionCookies();
        var reply = await SendAsync("/__braid114/issue", cookies);
        Check(reply.Status == HttpStatusCode.NoContent && !reply.Headers.ContainsKey("Location"), "Trusted fixture issuance did not finish without redirect.");
        cookies.Apply(reply); Check(cookies.Session is not null, "Trusted fixture issuance returned no main session cookie."); return cookies;
    }
    internal async Task<(CatalogSessionReply Reply, string Token)> ContextAsync(CatalogSessionCookies cookies)
    {
        var reply = await SendAsync(ContextPath, cookies);
        Check(reply.Status == HttpStatusCode.OK && reply.NoStore, "The healthy session context did not return a private success.");
        var document = reply.Json();
        Check(document.GetProperty("mode").GetString() == "session"
            && document.GetProperty("actorPrincipalId").GetString() == Data.Seed.Alice.ToString("D")
            && document.GetProperty("allowedOrganizationId").GetString() == Data.Seed.OrganizationA.ToString("D"),
            "The context did not bind its mode and scope to the captured session principal.");
        string? token = document.GetProperty("requestToken").GetString();
        Check(token is { Length: > 0 and <= 4096 }, "The catalog request token was absent or outside its bound.");
        cookies.Apply(reply); return (reply, token!);
    }
    internal async Task<string> MetadataTagAsync(CatalogSessionCookies cookies)
    {
        var reply = await SendAsync(MetadataPath, cookies);
        Check(reply.Status == HttpStatusCode.OK && reply.NoStore && reply.Headers.TryGetValue("ETag", out var tags) && tags.Length == 1,
            "The actual catalog metadata read did not return one private strong validator.");
        return reply.Headers["ETag"][0];
    }
    internal async Task<CatalogSessionReply> HeadersOnlyAsync(CatalogSessionUnsafeRoute route, CatalogSessionCookies cookies,
        string? token, string? tag = null, string[]? origins = null)
    {
        var record = NewObservation(seedEtag: true);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var tcp = new TcpClient(); await tcp.ConnectAsync(IPAddress.Loopback, Origin.Port, budget.Token);
        byte[] pin = Certificate.GetCertHash(HashAlgorithmName.SHA256);
        await using var tls = new SslStream(tcp.GetStream(), false, (_, certificate, _, errors) => certificate is not null
            && (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) == 0
            && CryptographicOperations.FixedTimeEquals(pin, certificate.GetCertHash(HashAlgorithmName.SHA256)));
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        { TargetHost = "127.0.0.1", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, budget.Token);
        var text = new StringBuilder().Append(route.Method).Append(' ').Append(route.Path).Append(" HTTP/1.1\r\nHost: ")
            .Append(Origin.Authority).Append("\r\nConnection: close\r\nContent-Length: 128\r\nContent-Type: ").Append(route.MediaType)
            .Append("\r\n").Append(FixtureHeader).Append(": ").Append(record.Id.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        if (cookies.Header.Length > 0) text.Append("Cookie: ").Append(cookies.Header).Append("\r\n");
        foreach (string origin in origins ?? [Origin.GetLeftPart(UriPartial.Authority)]) text.Append("Origin: ").Append(origin).Append("\r\n");
        if (token is not null) text.Append(TokenHeader).Append(": ").Append(token).Append("\r\n");
        if (route.Key) text.Append("Idempotency-Key: ").Append(Guid.NewGuid().ToString("D")).Append("\r\n");
        if (route.Tag && tag is not null) text.Append("If-Match: ").Append(tag).Append("\r\n");
        text.Append("\r\n");
        Check(text.Length <= 32_768 && text.ToString().All(character => character <= 127), "The owned TLS headers exceeded their ASCII bound.");
        await tls.WriteAsync(Encoding.ASCII.GetBytes(text.ToString()), budget.Token);
        // Deliberately send NO declared body bytes and schedule NO body producer.
        CatalogSessionReply reply = await ReadRawAsync(tls, record, budget.Token);
        await record.Ended.Task.WaitAsync(TimeSpan.FromSeconds(5), budget.Token);
        Check(!record.UnexpectedFailure, "A headers-only catalog refusal escaped its closed boundary."); return reply;
    }
    private static async Task<byte[]> ReadBodyAsync(Stream stream, CancellationToken token)
    {
        using var output = new MemoryStream(); var buffer = new byte[1024];
        while (true)
        {
            int count = await stream.ReadAsync(buffer, token); if (count == 0) return output.ToArray();
            Check(output.Length + count <= 32_768, "The owned HTTPS response exceeded its body bound."); output.Write(buffer, 0, count);
        }
    }
    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken token)
    {
        using var output = new MemoryStream(); var one = new byte[1];
        while (await stream.ReadAsync(one, token) != 0)
        {
            output.WriteByte(one[0]); Check(output.Length <= 8192, "An owned TLS response line exceeded its bound.");
            if (one[0] == 10 && output.Length >= 2 && output.GetBuffer()[(int)output.Length - 2] == 13)
                return Encoding.ASCII.GetString(output.ToArray(), 0, (int)output.Length - 2);
        }
        throw new InvalidOperationException("An owned TLS response ended before a complete line.");
    }
    private static async Task<CatalogSessionReply> ReadRawAsync(Stream stream, CatalogSessionRequestRecord record, CancellationToken token)
    {
        string[] first = (await ReadLineAsync(stream, token)).Split(' ', 3);
        Check(first.Length == 3 && first[0].StartsWith("HTTP/1.", StringComparison.Ordinal) && int.TryParse(first[1], out _), "The owned TLS response status was malformed.");
        var status = (HttpStatusCode)int.Parse(first[1], CultureInfo.InvariantCulture);
        var values = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase); int size = 0;
        while (true)
        {
            string line = await ReadLineAsync(stream, token); if (line.Length == 0) break;
            size += line.Length; Check(size <= 16_384 && values.Count <= 64, "The owned TLS response headers exceeded their bound.");
            int colon = line.IndexOf(':'); Check(colon > 0, "The owned TLS response header was malformed.");
            string name = line[..colon], value = line[(colon + 1)..].Trim();
            if (!values.TryGetValue(name, out var entries)) values.Add(name, entries = []); entries.Add(value);
        }
        using var body = new MemoryStream();
        if (values.TryGetValue("Content-Length", out var lengths))
        {
            Check(lengths.Count == 1 && int.TryParse(lengths[0], out int _) && int.Parse(lengths[0], CultureInfo.InvariantCulture) is >= 0 and <= 32_768,
                "The owned TLS response length was malformed.");
            int count = int.Parse(lengths[0], CultureInfo.InvariantCulture); var bytes = new byte[count];
            await stream.ReadExactlyAsync(bytes, token); body.Write(bytes);
        }
        else if (values.TryGetValue("Transfer-Encoding", out var transfers))
        {
            Check(transfers.Count == 1 && transfers[0].Equals("chunked", StringComparison.OrdinalIgnoreCase), "The owned TLS response transfer coding was unsupported.");
            while (true)
            {
                string line = (await ReadLineAsync(stream, token)).Split(';', 2)[0];
                Check(int.TryParse(line, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int count) && count >= 0 && body.Length + count <= 32_768,
                    "The owned TLS response chunk exceeded its bound.");
                if (count == 0) { Check((await ReadLineAsync(stream, token)).Length == 0, "The owned TLS response had unexpected trailers."); break; }
                var bytes = new byte[count]; await stream.ReadExactlyAsync(bytes, token); body.Write(bytes);
                Check((await ReadLineAsync(stream, token)).Length == 0, "The owned TLS chunk ended without CRLF.");
            }
        }
        else if (status != HttpStatusCode.NoContent)
        {
            // This client explicitly requested Connection: close; bound EOF-framed bodies too.
            byte[] bytes = await ReadBodyAsync(stream, token); body.Write(bytes);
        }
        return new(status, values.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase), body.ToArray(), record);
    }
    internal async Task ProbeDeferredWitnessesAsync(CatalogSessionCookies cookies, string token)
    {
        // Only after rejection measures: actual admitted HTTP routes must enter each
        // witness method. These canned closed outcomes perform no native/report I/O.
        AllowDeferredWitness = true;
        try
        {
            foreach (var route in UnsafeRoutes().Where(value => value.Family is not ("project-create" or "metadata-patch")))
            {
                byte[] bytes = route.Key ? [1] : [];
                string path = route.Family == "native-resume"
                    ? ProjectPath + "/revision-ingestions/" + Data.Original.Facts.OperationId.ToString("D") + "/resume"
                    : route.Path;
                var reply = await SendAsync(path, cookies, route.Method, token, body: bytes,
                    mediaType: route.Key ? route.MediaType : "", idempotencyKey: route.Key);
                Check(reply.Status is HttpStatusCode.NotFound or HttpStatusCode.ServiceUnavailable
                    && reply.NoStore && reply.Record.DeferredCalls > 0
                    && reply.Record.DownstreamPaths == reply.Record.DeferredCalls && reply.Record.SessionRenews > 0
                    && reply.Record.Order.Contains(route.Family.EndsWith("-ensure", StringComparison.Ordinal) ? "report-ensure" : route.Family),
                    "An enabled admitted unsafe route did not reach its deferred method witness.");
            }
            string revision = CatalogHttpTestSupport.RevisionPath(Data.Original.Facts.FutureRevision);
            foreach (var route in new[] {
                (Path: ProjectPath + "/revision-ingestions/" + Guid.NewGuid().ToString("D"), Method: "native-receipt"),
                (Path: revision + "/reports/projection", Method: "report-current"),
                (Path: revision + "/reports/validation", Method: "report-current"),
                (Path: revision + "/reports/projection/" + Guid.NewGuid().ToString("D"), Method: "report-retained"),
                (Path: revision + "/reports/validation/" + Guid.NewGuid().ToString("D"), Method: "report-retained") })
            {
                var reply = await SendAsync(route.Path, cookies);
                Check(reply.Status is HttpStatusCode.NotFound or HttpStatusCode.ServiceUnavailable
                    && reply.NoStore && reply.Record.DeferredCalls > 0 && reply.Record.Order.Contains(route.Method),
                    "An enabled admitted read route did not reach its deferred reader witness.");
            }
        }
        finally { AllowDeferredWitness = false; }
    }
    internal static void Error(CatalogSessionReply reply, HttpStatusCode status, string code)
    {
        Check(reply.Status == status && reply.NoStore && !reply.Headers.ContainsKey("ETag") && !reply.Headers.ContainsKey("Location"),
            "The catalog-session failure status or private response headers were incorrect.");
        var json = reply.Json();
        Check(json.ValueKind == JsonValueKind.Object && json.EnumerateObject().Count() == 1 && json.TryGetProperty("code", out var value) && value.GetString() == code,
            "The catalog-session failure disclosed an unexpected response shape.");
    }
    internal static void NoBodyAfterAdmission(CatalogSessionReply reply, Braid.Application.ActorContext actor)
    {
        var value = reply.Record;
        Check(value.Actor?.PrincipalId == actor.PrincipalId && value.CatalogReads == 1
            && value.Order.SequenceEqual(new[] { "catalog", "organization-completed" })
            && value.BodyReads == 0 && value.BodyBytes == 0 && value.CapabilityReads == 0
            && value.MetadataWrites == 0 && value.CreatorCalls == 0 && value.DeferredResolutions == 0
            && value.DeferredCalls == 0 && value.DownstreamPaths == 0 && value.SessionRenews == 0,
            "Cross-organization token reached work beyond its single admission read.");
    }

    internal static void NoBodyOrBusiness(CatalogSessionReply reply, bool noRenew = true)
    {
        var value = reply.Record;
        Check(value.BodyReads == 0 && value.BodyBytes == 0 && value.CatalogReads == 0 && value.CapabilityReads == 0
            && value.MetadataWrites == 0 && value.CreatorCalls == 0 && value.DeferredResolutions == 0 && value.DeferredCalls == 0 && value.DownstreamPaths == 0
            && (!noRenew || value.SessionRenews == 0), "A refused session entered body, business, downstream or renewal work.");
    }
}
