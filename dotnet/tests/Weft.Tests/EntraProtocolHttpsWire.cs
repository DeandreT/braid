using System.Net;
using System.Runtime.Versioning;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.Net.Http.Headers;
using Weft.Identity;

namespace Weft.Tests;

[SupportedOSPlatform("linux")]
internal sealed partial class EntraProtocolHttpsFixture
{
    internal OpenIdConnectOptions NamedOptions => Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get("Weft.Entra");
    internal EntraProtocolRequest NewRequest(EntraProtocolOperation operation = EntraProtocolOperation.Callback, bool ownCancellation = false)
    {
        int id = Interlocked.Increment(ref sequence);
        EntraProtocolFixturePolicy.Check(id <= 256, "The owned application request budget was exceeded.");
        var record = new EntraProtocolRequest
        {
            Id = id,
            Operation = operation,
            OriginalCancellation = ownCancellation ? new CancellationTokenSource() : null
        };
        EntraProtocolFixturePolicy.Check(observations.TryAdd(id, record), "An owned application observation was reused."); return record;
    }
    internal async Task<EntraProtocolReply> SendAsync(string path, EntraProtocolBrowser? browser = null,
        EntraProtocolOperation operation = EntraProtocolOperation.Callback, EntraProtocolRequest? record = null,
        CancellationToken cancellationToken = default)
    {
        record ??= NewRequest(operation);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); budget.CancelAfter(TimeSpan.FromSeconds(10));
        bool beginning = record.Operation is EntraProtocolOperation.Begin or EntraProtocolOperation.BeginTwice;
        using var message = new HttpRequestMessage(beginning ? HttpMethod.Post : HttpMethod.Get, path);
        if (beginning) message.Content = new ByteArrayContent([]);
        message.Headers.Add(EntraProtocolFixturePolicy.TraceHeader, record.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (browser is not null && browser.Header.Length != 0) message.Headers.TryAddWithoutValidation("Cookie", browser.Header);
        using var response = await browserClient!.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, budget.Token);
        byte[] body = await ReadBoundedAsync(response, budget.Token);
        var headers = response.Headers.Concat(response.Content.Headers).ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
        EntraProtocolFixturePolicy.Check(headers.Sum(pair => pair.Key.Length + pair.Value.Sum(value => value.Length)) <= 32_768
            && headers.All(pair => pair.Value.All(value => value.Length <= 8192)),
            "The owned application response headers exceeded their bound.");
        await record.Ended.Task.WaitAsync(TimeSpan.FromSeconds(10), budget.Token);
        EntraProtocolFixturePolicy.Check(!record.UnexpectedFailure, "An owned protocol operation escaped its closed facade boundary.");
        return new(response.StatusCode, headers, body, record);
    }
    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, CancellationToken token)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(token); using var output = new MemoryStream(); var bytes = new byte[1024];
        while (true)
        {
            int count = await stream.ReadAsync(bytes, token); if (count == 0) return output.ToArray();
            EntraProtocolFixturePolicy.Check(output.Length + count <= 65_536, "An owned HTTP response exceeded its bound."); output.Write(bytes, 0, count);
        }
    }
    internal async Task<EntraProtocolReply> BeginAsync(EntraProtocolBrowser browser)
    {
        var reply = await SendAsync("/api/auth/login", browser, EntraProtocolOperation.Begin);
        EntraProtocolFixturePolicy.Check(reply.Status == HttpStatusCode.OK && reply.Request.Challenge is not null && reply.Request.Failure is null,
            "The healthy ordinary challenge did not complete.");
        ValidateAuthorization(reply.Request.Challenge!.AuthorizationUrl); browser.Apply(reply); return reply;
    }
    internal static Uri ValidateAuthorization(string value)
    {
        EntraProtocolFixturePolicy.Check(value.Length <= 16_384 && Uri.TryCreate(value, UriKind.Absolute, out _),
            "The authorization URL was absent or outside its fixed bound.");
        var uri = new Uri(value);
        EntraProtocolFixturePolicy.Check(uri.Scheme == "https" && uri.Host == "login.microsoftonline.com" && uri.IsDefaultPort
            && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 && uri.AbsolutePath == EntraProtocolFixturePolicy.Authorize,
            "The challenge exposed an unadmitted authorization destination."); return uri;
    }
    internal async Task<EntraProtocolFlow> AuthorizeAsync(EntraProtocolBrowser browser, EntraProtocolReply begin)
    {
        Uri logical = ValidateAuthorization(begin.Request.Challenge!.AuthorizationUrl);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // This client has fixed loopback ConnectCallback/custom-root trust. No live navigation.
        using var response = await issuerClient!.GetAsync(logical, HttpCompletionOption.ResponseHeadersRead, budget.Token);
        _ = await ReadBoundedAsync(response, budget.Token);
        if (Issuer.AssertionFailure is { } failure) throw failure;
        Uri? callback = response.Headers.Location;
        EntraProtocolFixturePolicy.Check(response.StatusCode == HttpStatusCode.SeeOther && callback is not null
            && callback.GetLeftPart(UriPartial.Authority) == Origin.GetLeftPart(UriPartial.Authority)
            && callback.AbsolutePath == EntraProtocolFixturePolicy.Callback && callback.Query.Length <= 16_384,
            "The owned issuer did not return the exact fixed callback.");
        return new(browser, begin, callback!.PathAndQuery);
    }
    internal async Task<EntraProtocolFlow> FlowAsync(EntraProtocolBrowser? browser = null)
    { browser ??= new(); return await AuthorizeAsync(browser, await BeginAsync(browser)); }
    internal async Task<EntraProtocolReply> CompleteAsync(EntraProtocolFlow flow,
        EntraProtocolOperation operation = EntraProtocolOperation.Callback, EntraProtocolRequest? record = null)
    {
        var reply = await SendAsync(flow.Callback, flow.Browser, operation, record); flow.Browser.Apply(reply); return reply;
    }
    internal void Success(EntraProtocolReply reply)
    {
        var value = reply.Request.Verified;
        EntraProtocolFixturePolicy.Check(reply.Status == HttpStatusCode.OK && reply.Request.Failure is null && value is not null
            && reply.Request.EvidencePublished == 1 && value.OrganizationId == EntraProtocolFixturePolicy.Organization
            && value.ExternalIdentity == EntraProtocolFixturePolicy.UserKey && value.VerifiedDeadlineUtc == Issuer.LastExpiry
            && value.VerifiedDeadlineUtc.Offset == TimeSpan.Zero && value.VerifiedDeadlineUtc.Ticks % TimeSpan.TicksPerSecond == 0,
            "The final private evidence was absent, early, widened or not the exact verified tuple/deadline.");
        EntraProtocolFixturePolicy.Check(!reply.Request.UserChanged && Ports.Calls == 0 && Issuer.AssertionAccepted > 0
            && Issuer.AssertionRejected == 0 && Issuer.PkceAccepted > 0 && Issuer.UnexpectedRequests == 0 && Issuer.UnexpectedFailures == 0,
            "The healthy protocol skipped crypto/PKCE, projected authority or touched an unrelated binding/session path.");
        NoApplicationEffects(reply);
    }
    internal void Denied(EntraProtocolReply reply, ServerSessionIdentityFailureKind kind = ServerSessionIdentityFailureKind.Denied)
    {
        EntraProtocolFixturePolicy.Check(reply.Request.Failure == kind && reply.Request.Verified is null
            && reply.Request.EvidencePublished == 0 && Ports.Calls == 0 && !reply.Request.UserChanged,
            "A refused protocol published evidence, authority or an unexpected closed category.");
        NoApplicationEffects(reply);
    }
    internal static void NoApplicationEffects(EntraProtocolReply reply)
    {
        EntraProtocolFixturePolicy.Check(!reply.Headers.ContainsKey("Location"), "A dormant protocol operation left a redirect response.");
        if (reply.Headers.TryGetValue("Set-Cookie", out var cookies))
            EntraProtocolFixturePolicy.Check(SetCookieHeaderValue.ParseList(cookies.ToList()).All(cookie =>
                cookie.Name.ToString() is not ("__Host-Weft.Session" or "__Host-Weft.Antiforgery")),
                "The dormant protocol adapter emitted an application/AF cookie.");
    }
    internal void ChallengeCookies(EntraProtocolReply reply)
    {
        EntraProtocolFixturePolicy.Check(reply.Headers.TryGetValue("Set-Cookie", out var headers), "The real challenge emitted no transient cookies.");
        var cookies = SetCookieHeaderValue.ParseList(headers!.ToList());
        EntraProtocolFixturePolicy.Check(cookies.Count == 2 && cookies.All(cookie => cookie.Secure && cookie.HttpOnly
            && !cookie.Domain.HasValue && cookie.Path == "/" && cookie.SameSite == Microsoft.Net.Http.Headers.SameSiteMode.None
            && cookie.Expires is { } expiry && expiry > DateTimeOffset.UtcNow.AddMinutes(9)
            && expiry <= DateTimeOffset.UtcNow.AddMinutes(10).AddSeconds(5)),
            "Nonce/correlation cookies did not retain the fixed finite host-only HTTPS attributes.");
        NoApplicationEffects(reply);
    }
    internal static void MatchingFlowCookiesCleared(EntraProtocolFlow flow)
    {
        var headers = flow.Begin.Headers["Set-Cookie"];
        var owned = SetCookieHeaderValue.ParseList(headers.ToList()).Select(cookie => cookie.Name.ToString()).ToArray();
        EntraProtocolFixturePolicy.Check(!flow.Browser.Names.Intersect(owned, StringComparer.Ordinal).Any(),
            "Matching nonce/correlation cookies survived completed or refused unstarted protocol work.");
    }
    internal async Task<OpenIdConnectConfiguration> ReadFreshCurrentConfigurationAsync(EntraProtocolRequest record, CancellationToken token)
    {
        var manager = record.Policy?.Manager as ConfigurationManager<OpenIdConnectConfiguration>;
        EntraProtocolFixturePolicy.Check(manager is not null && manager.AutomaticRefreshInterval >= TimeSpan.FromMinutes(5),
            "The initialized handler did not own a standard configuration manager with a fresh-cache interval.");
        var observation = manager!.GetConfigurationAsync(token); record.ConfigurationObservation = observation;
        // Known fresh current configuration: pinned standard GetConfigurationAsync returns it at its
        // first branch; this is observation, not RequestRefresh or a manager-continuation join.
        EntraProtocolFixturePolicy.Check(observation.IsCompletedSuccessfully, "A fresh configuration observation unexpectedly entered asynchronous retrieval.");
        return await observation;
    }
    internal async Task WaitForPublishedSecondKeyAsync(EntraProtocolRequest seeded)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        int before = Ledger().Count;
        for (int probe = 0; probe < 1024; probe++)
        {
            var configuration = await ReadFreshCurrentConfigurationAsync(seeded, budget.Token);
            EntraProtocolFixturePolicy.Check(Ledger().Count == before, "A current-configuration publication observation introduced another HTTP retrieval.");
            if (configuration.SigningKeys.Any(key => key.KeyId == "weft-k2") && configuration.SigningKeys.All(key => key.KeyId != "weft-k1")) return;
            await Task.Delay(TimeSpan.FromMilliseconds(10), budget.Token);
        }
        throw new EntraProtocolFixtureFailure("The admitted K2-only configuration did not publish within its bounded observation budget.");
    }
    internal IReadOnlyList<EntraFixtureSend> Ledger() => Transport.SnapshotAttempts();
    internal void CheckPrivacy(EntraProtocolReply reply)
    {
        string body = System.Text.Encoding.UTF8.GetString(reply.Body);
        var operands = Issuer.PrivateOperands(); var logs = Logs.Snapshot();
        EntraProtocolFixturePolicy.Check(!Logs.InspectionIncomplete && operands.All(secret => !body.Contains(secret, StringComparison.Ordinal)
            && !reply.Headers.Values.SelectMany(value => value).Any(value => value.Contains(secret, StringComparison.Ordinal))
            && !logs.Any(value => value.Contains(secret, StringComparison.Ordinal)))
            && reply.Request.ClosedFailure?.InnerException is null
            && (reply.Request.ClosedFailure is null || reply.Request.ClosedFailure.Message == "The server session operation failed."),
            "A private operand escaped response/exception or dedicated authentication-log boundaries.");
        // HTTP hosting/edge diagnostics and process-wide IdentityModel EventSource are outside d1.

    }
}
