using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Braid.Tests;

[SupportedOSPlatform("linux")]
internal sealed partial class AuthHttpFixture
{
    internal async Task<AuthHttpReply> SendAsync(string path, AuthHttpJar? jar = null, string method = "GET",
        string? action = null, string? epoch = null, AuthHttpRecord? record = null, AuthHttpHost? host = null,
        Action<HttpRequestMessage>? mutate = null, CancellationToken token = default)
    {
        record ??= NewRecord(); host ??= First;
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add(FixtureHeader, record.Id.ToString(CultureInfo.InvariantCulture));
        if (jar is not null && jar.Header.Length != 0) request.Headers.TryAddWithoutValidation("Cookie", jar.Header);
        string route = path.Split('?', 2)[0];
        if (route == StatusPath) request.Headers.TryAddWithoutValidation("X-Braid-Session", "1");
        if (method == "POST")
        {
            request.Headers.TryAddWithoutValidation("Origin", Origin.GetLeftPart(UriPartial.Authority));
            string? header = route switch { LoginPath => "X-Braid-Login", LogoutPath => "X-Braid-Logout", ForgetPath => "X-Braid-Forget-Local", _ => null };
            if (header is not null) request.Headers.TryAddWithoutValidation(header, "1");
        }
        if (action is not null) request.Headers.TryAddWithoutValidation("X-Braid-Auth-Token", action);
        if (epoch is not null) request.Headers.TryAddWithoutValidation("X-XSRF-TOKEN", epoch);
        mutate?.Invoke(request);
        using var response = await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        byte[] body = await ReadBoundedAsync(response, token);
        var headers = response.Headers.Concat(response.Content.Headers).ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
        Check(headers.Sum(pair => pair.Key.Length + pair.Value.Sum(value => value.Length)) <= 32768, "Auth response headers exceeded their bound.");
        await record.Ended.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
        Check(!record.Unexpected, "An auth request escaped its closed response boundary.");
        return new(response.StatusCode, headers, body, record);
    }
    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, CancellationToken token)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(token); using var output = new MemoryStream(); var block = new byte[2048];
        while (true) { int count = await stream.ReadAsync(block, token); if (count == 0) return output.ToArray(); Check(output.Length + count <= 65536, "Auth response body exceeded its bound."); output.Write(block, 0, count); }
    }
    internal async Task<AuthHttpStatus> StatusAsync(AuthHttpJar jar, AuthHttpHost? host = null, AuthHttpRecord? record = null)
    {
        record ??= NewRecord(); record.CaptureIdentity = true;
        var reply = await SendAsync(StatusPath, jar, record: record, host: host); SharedHeaders(reply);
        Check(reply.Status == HttpStatusCode.OK, "Healthy auth bootstrap did not complete.");
        var json = reply.Json();
        Check(json.EnumerateObject().Count() == 7 && json.GetProperty("version").GetInt32() == 1 && json.GetProperty("mode").GetString() == "session", "Auth session union shape changed.");
        string? state = json.GetProperty("state").GetString(); bool positive = state == "authenticated";
        Check(state is "anonymous" or "authenticationRequired" or "authenticated"
            && positive == (json.GetProperty("requestToken").ValueKind == System.Text.Json.JsonValueKind.String)
            && positive == (json.GetProperty("identityContext").ValueKind == System.Text.Json.JsonValueKind.String), "Auth status coherence changed.");
        Check(json.GetProperty("authActionRequestToken").GetString() is { Length: > 0 and <= 4096 }, "Auth action token is missing or oversized.");
        jar.Apply(reply); PrivateResponse(reply); return new(reply);
    }
    internal async Task<AuthHttpFlow> FlowAsync(AuthHttpJar? jar = null, AuthHttpHost? host = null, string? invitation = null, string? returnTo = null)
    {
        jar ??= new(); var status = await StatusAsync(jar, host);
        var begin = await SendAsync(LoginPath, jar, "POST", status.Action, status.Epoch, host: host,
            mutate: message =>
            {
                if (invitation is not null) message.Headers.Add("X-Braid-Invitation", invitation);
                if (returnTo is not null) message.Headers.Add("X-Braid-Return-To", returnTo);
            }); SharedHeaders(begin);
        Check(begin.Status == HttpStatusCode.OK && !begin.Headers.ContainsKey("Location"), "Login fetch did not return its bounded navigation DTO.");
        var json = begin.Json(); Check(json.EnumerateObject().Count() == 2 && json.GetProperty("version").GetInt32() == 1, "Login navigation shape changed.");
        var value = json.GetProperty("authorizationUrl").GetString(); Check(value is { Length: > 0 and <= 16384 }, "Authorization navigation is absent or oversized.");
        var logical = EntraProtocolHttpsFixture.ValidateAuthorization(value!); jar.Apply(begin);
        var transient = SetCookieHeaderValue.ParseList(begin.Headers["Set-Cookie"].ToList());
        Check(transient.Count == 2 && transient.All(cookie => cookie.Secure && cookie.HttpOnly && !cookie.Domain.HasValue && cookie.Path == "/" && cookie.SameSite == Microsoft.Net.Http.Headers.SameSiteMode.None), "Protocol transient cookie attributes changed.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await issuerBrowser!.GetAsync(logical, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        _ = await ReadBoundedAsync(response, timeout.Token);
        var location = response.Headers.Location;
        Check(response.StatusCode == HttpStatusCode.SeeOther && location is not null && location.GetLeftPart(UriPartial.Authority) == Origin.GetLeftPart(UriPartial.Authority) && location.AbsolutePath == CallbackPath, "The owned issuer did not return the fixed callback.");
        return new(jar, location!.PathAndQuery);
    }
    internal async Task<AuthHttpReply> CompleteAsync(AuthHttpFlow flow, AuthHttpHost? host = null, AuthHttpRecord? record = null, string? callback = null)
    {
        var reply = await SendAsync(callback ?? flow.Callback, flow.Jar, record: record, host: host); flow.Jar.Apply(reply); SharedHeaders(reply); PrivateResponse(reply); return reply;
    }
    internal async Task<AuthHttpJar> LoginAsync(AuthHttpJar? jar = null, AuthHttpHost? host = null)
    {
        var flow = await FlowAsync(jar, host); var reply = await CompleteAsync(flow, host);
        Check(reply.Status == HttpStatusCode.SeeOther && reply.Body.Length == 0 && reply.Headers.TryGetValue("Location", out var location) && location.SequenceEqual(new[] { "/" }), "Real verified callback did not finish with fixed empty303.");
        Check(reply.Record.Resolves == 1 && reply.Record.ResolvedKey == EntraProtocolFixturePolicy.UserKey
            && reply.Record.Creates == 1 && reply.Record.Renews == 0 && reply.Record.SessionReads == 0
            && Issuer.AssertionAccepted > 0 && Issuer.PkceAccepted > 0 && Issuer.AssertionRejected == 0,
            "Real OIDC verification/fresh binding/session issue ordering was skipped.");
        Check(flow.Jar.Get(SessionCookie) is not null, "Real session issuance returned no carrier."); return flow.Jar;
    }
    internal static void SharedHeaders(AuthHttpReply reply)
    {
        Check(reply.Headers.TryGetValue("Cache-Control", out var cache) && cache.Any(value => value.Split(',').Any(part => part.Trim() == "no-store"))
            && reply.Headers.TryGetValue("Pragma", out var pragma) && pragma.Contains("no-cache")
            && reply.Headers.TryGetValue("Referrer-Policy", out var referrer) && referrer.SequenceEqual(new[] { "no-referrer" })
            && !reply.Headers.ContainsKey("ETag") && !reply.Headers.ContainsKey("Last-Modified") && reply.Status != HttpStatusCode.NotModified,
            "An auth response exposed a cache validator or lost its private response headers.");
    }
    internal static void Error(AuthHttpReply reply, HttpStatusCode status, string code)
    {
        SharedHeaders(reply); Check(reply.Status == status && !reply.Headers.ContainsKey("Location"),
            $"Auth error status or redirect changed (expected {(int)status}, actual {(int)reply.Status}).");
        var json = reply.Json(); Check(json.EnumerateObject().Count() == 2 && json.GetProperty("version").GetInt32() == 1 && json.GetProperty("code").GetString() == code, "Auth error schema changed.");
    }
    internal static void CallbackError(AuthHttpReply reply, HttpStatusCode status)
    {
        SharedHeaders(reply); string body = Encoding.UTF8.GetString(reply.Body);
        string heading = status == HttpStatusCode.Unauthorized ? "Sign-in could not be completed" : "Sign-in is temporarily unavailable";
        Check(reply.Status == status && reply.Body.Length <= 1024 && body.Contains(heading, StringComparison.Ordinal) && body.Contains("href=\"/\"", StringComparison.Ordinal)
            && !reply.Headers.ContainsKey("Location") && reply.Headers.TryGetValue("Content-Security-Policy", out var csp)
            && csp.SequenceEqual(new[] { "default-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'" }), "Callback failure was not its fixed bounded HTML/status/CSP.");
    }
    internal void PrivateResponse(AuthHttpReply reply)
    {
        string body = Encoding.UTF8.GetString(reply.Body);
        Check(Issuer.PrivateOperands().All(value => !body.Contains(value, StringComparison.Ordinal)
            && !reply.Headers.Where(pair => !pair.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)).SelectMany(pair => pair.Value).Any(value2 => value2.Contains(value, StringComparison.Ordinal))), "A callback/status/error exposed private provider operands.");
    }
    internal static void NoLaterWork(AuthHttpReply reply, bool sessionRead = true)
    {
        var r = reply.Record;
        Check(r.BodyReads == 0 && r.FormReads == 0 && r.Resolves == 0 && r.BindingReads == 0 && r.Creates == 0 && r.Revokes == 0 && r.Renews == 0 && r.CatalogReads == 0 && r.MetadataWrites == 0 && (!sessionRead || r.SessionReads == 0), "An early auth refusal entered later body/authority/provider business work.");
    }
    internal static bool Deletes(AuthHttpReply reply, string name) => reply.Headers.TryGetValue("Set-Cookie", out var values)
        && SetCookieHeaderValue.ParseList(values.ToList()).Any(cookie => cookie.Name == name && (cookie.Value.ToString().Length == 0 || cookie.MaxAge is { } age && age <= TimeSpan.Zero));
    internal static void NoLocalCookieEffect(AuthHttpReply reply) => Check(!reply.Headers.TryGetValue("Set-Cookie", out var values) || SetCookieHeaderValue.ParseList(values.ToList()).All(cookie => cookie.Name.ToString() is not (SessionCookie or EpochCookie or ActionCookie)), "A canceled operation emitted an application cookie.");
    internal static void NoLocalDeletion(AuthHttpReply reply) => Check(!Deletes(reply, SessionCookie) && !Deletes(reply, EpochCookie) && !Deletes(reply, ActionCookie), "A failed/unconfirmed operation cleared local cookie audiences.");
    internal static string ExpectedFingerprint(Braid.Identity.ServerSessionRequestIdentity identity)
    {
        string input = "braid:identity-context:v1\n" + identity.OrganizationId.ToString("D").ToLowerInvariant() + "\n" + identity.PrincipalId.Value.ToString("D").ToLowerInvariant() + "\n" + identity.LoginEpoch.ToString("D").ToLowerInvariant();
        return "v1." + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(input))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
    internal Task<AuthHttpReply> HeadersOnlyAsync(string path, AuthHttpJar jar, AuthHttpStatus status, bool chunked) =>
        RawAsync("POST", path, jar, "Origin: " + Origin.GetLeftPart(UriPartial.Authority) + "\r\nX-Braid-Login: 1\r\nX-Braid-Auth-Token: " + status.Action + "\r\n" + (chunked ? "Transfer-Encoding: chunked\r\n" : "Content-Length: 8\r\n"));
    internal Task<AuthHttpReply> RawCallbackAsync(string path, AuthHttpJar jar) => RawAsync("GET", path, jar, "");
    private async Task<AuthHttpReply> RawAsync(string method, string path, AuthHttpJar jar, string extra)
    {
        var record = NewRecord(); using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var tcp = new TcpClient(); await tcp.ConnectAsync(IPAddress.Loopback, First.PhysicalPort, budget.Token);
        await using var tls = new SslStream(tcp.GetStream(), false);
        var chain = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck }; chain.CustomTrustStore.Add(Crypto.Root);
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "127.0.0.1", CertificateChainPolicy = chain }, budget.Token);
        string head = method + " " + path + " HTTP/1.1\r\nHost: " + Origin.Authority + "\r\nConnection: close\r\n" + FixtureHeader + ": " + record.Id.ToString(CultureInfo.InvariantCulture)
            + "\r\nCookie: " + jar.Header + "\r\n" + extra + "\r\n";
        await tls.WriteAsync(Encoding.ASCII.GetBytes(head), budget.Token); await tls.FlushAsync(budget.Token);
        using var output = new MemoryStream(); var one = new byte[1]; string text;
        while (true)
        {
            int count = await tls.ReadAsync(one, budget.Token); Check(count == 1 && output.Length < 32768, "The headers-only auth probe response did not settle within its bound."); output.WriteByte(one[0]);
            text = Encoding.ASCII.GetString(output.ToArray()); if (text.EndsWith("\r\n\r\n", StringComparison.Ordinal)) break;
        }
        var lines = text.Split("\r\n", StringSplitOptions.None); Check(lines[0].StartsWith("HTTP/1.1 ", StringComparison.Ordinal) && int.TryParse(lines[0].Split(' ')[1], out _), "The headers-only auth response status was malformed.");
        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1).Where(line => line.Length != 0)) { int colon = line.IndexOf(':'); Check(colon > 0, "The headers-only auth response header was malformed."); string key = line[..colon], value = line[(colon + 1)..].Trim(); headers[key] = headers.TryGetValue(key, out var prior) ? prior.Append(value).ToArray() : [value]; }
        await record.Ended.Task.WaitAsync(TimeSpan.FromSeconds(5), budget.Token);
        return new((HttpStatusCode)int.Parse(lines[0].Split(' ')[1], CultureInfo.InvariantCulture), headers, [], record);
    }
}
