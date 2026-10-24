using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Weft.Tests;

/// <summary>One owned HTTPS server, fixed Microsoft dialect, standard token crypto, no live provider I/O.</summary>
internal sealed class EntraProtocolHttpsIssuer(EntraProtocolCrypto crypto) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, EntraProtocolCode> codes = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<EntraProtocolIssuerSend> sends = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource> work = new();
    private readonly ConcurrentDictionary<int, Task> originalOperations = new();
    internal TaskCompletionSource<Task>? NextTokenOperation = null;
    internal IReadOnlyList<Task> SnapshotOperations() => originalOperations.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToArray();
    private WebApplication? app;
    private Task? disposal, applicationStopOperation, applicationDisposeOperation;
    private int sequence, codeCount;
    internal int Port { get; private set; }
    internal string Callback { get; set; } = "";
    internal EntraProtocolMetadataCase Metadata = default;
    internal EntraProtocolTokenCase TokenCase;
    internal int TokenLifetimeSeconds = 300;
    internal EntraProtocolKeyCase KeyCase = default;
    internal bool PublishSecond = false, SignSecond = false, BreakPkce, CommonKeys = false, OnlySecond = false, MalformedTokenResponse;
    internal TaskCompletionSource? NextKeysResponse = null, NextMetadataResponse = null;
    internal EntraProtocolGate? MetadataGate = null, TokenGate;
    internal EntraProtocolGate? MetadataBodyGate = null, KeysBodyGate = null, TokenBodyGate = null;
    internal HttpStatusCode? TokenFailureStatus = null;
    internal string? TokenFailureDescription = null;
    internal int AssertionAccepted, AssertionRejected, PkceAccepted, TokenResponses, UnexpectedRequests, UnexpectedFailures;
    internal EntraProtocolFixtureFailure? AssertionFailure;
    private string? lastAssertion, lastToken;
    internal DateTimeOffset LastExpiry { get; private set; }
    internal bool Settled { get; private set; }
    internal IReadOnlyList<EntraProtocolIssuerSend> Snapshot() => sends.ToArray();
    internal IReadOnlyList<string> PrivateOperands() => codes.SelectMany(pair => new[] { pair.Key, pair.Value.Nonce, pair.Value.State })
        .Concat(new[] { lastAssertion, lastToken, EntraProtocolFixturePolicy.Subject, EntraProtocolFixturePolicy.ObjectId.ToString("D"), EntraProtocolFixturePolicy.PrivateMarker })
        .Where(value => !string.IsNullOrEmpty(value)).Select(value => value!).ToArray();

    internal async Task StartAsync()
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start(1);
        try
        {
            Port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], EnvironmentName = Environments.Production });
            builder.Configuration.Sources.Clear(); builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Limits.MaxRequestBodySize = 65_536; options.Limits.MaxRequestHeadersTotalSize = 32_768; options.Limits.MaxRequestLineSize = 24_576; options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
                options.Listen(IPAddress.Loopback, Port, listen => { listen.Protocols = HttpProtocols.Http1AndHttp2; listen.UseHttps(crypto.IssuerTls); });
            });
            app = builder.Build();
            app.Run(async context =>
            {
                int id = Interlocked.Increment(ref sequence); var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                work.TryAdd(id, ended);
                try
                {
                    EntraProtocolFixturePolicy.Check(id <= 128, "The owned issuer request budget was exceeded.");
                    string path = context.Request.Path.Value ?? ""; sends.Enqueue(new(context.Request.Method, path, context.Connection.Id));
                    EntraProtocolFixturePolicy.Check(context.Request.IsHttps && context.Request.Host.Host == "login.microsoftonline.com",
                        "An owned issuer request lost its logical HTTPS name.");
                    Task original = ProcessAsync(context, path);
                    originalOperations.TryAdd(id, original);
                    if (path == EntraProtocolFixturePolicy.Token) NextTokenOperation?.TrySetResult(original);
                    await original;
                }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
                catch (Exception error)
                {
                    if (error is EntraProtocolFixtureFailure failure) AssertionFailure = failure;
                    Interlocked.Increment(ref UnexpectedFailures);
                    if (!context.Response.HasStarted) { context.Response.StatusCode = 500; await context.Response.WriteAsync("{}", context.RequestAborted); }
                }
                finally { ended.TrySetResult(); }
            });
            reservation.Stop(); using var start = new CancellationTokenSource(TimeSpan.FromSeconds(15)); await app.StartAsync(start.Token);
        }
        finally { reservation.Stop(); }
    }

    private async Task ProcessAsync(HttpContext context, string path)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (context.Request.QueryString.HasValue && path != EntraProtocolFixturePolicy.Authorize)
        {
            Interlocked.Increment(ref UnexpectedRequests); context.Response.StatusCode = 400;
            await context.Response.WriteAsync("{}", context.RequestAborted); return;
        }
        if (context.Request.Method == "GET" && path == EntraProtocolFixturePolicy.Discovery)
        {
            await HoldAsync(MetadataGate, context.RequestAborted);
            if (Metadata == EntraProtocolMetadataCase.Redirect)
            { context.Response.StatusCode = 302; context.Response.Headers.Location = "https://unadmitted.invalid/private"; return; }
            var value = new Dictionary<string, object?>
            {
                ["issuer"] = Metadata == EntraProtocolMetadataCase.WrongIssuer ? "https://login.microsoftonline.com/df07c377-bdb9-4d8a-b15f-d1dfc3537e99/v2.0"
                    : Metadata == EntraProtocolMetadataCase.TemplateIssuer ? "https://login.microsoftonline.com/{tenantid}/v2.0" : EntraProtocolFixturePolicy.Issuer,
                ["authorization_endpoint"] = Metadata == EntraProtocolMetadataCase.ForeignAuthorize ? "https://unadmitted.invalid/authorize" : "https://login.microsoftonline.com" + EntraProtocolFixturePolicy.Authorize,
                ["token_endpoint"] = Metadata == EntraProtocolMetadataCase.ForeignToken ? "https://unadmitted.invalid/token" : EntraProtocolFixturePolicy.TokenAddress,
                ["jwks_uri"] = Metadata switch
                {
                    EntraProtocolMetadataCase.ForeignKeys => "https://unadmitted.invalid" + EntraProtocolFixturePolicy.Keys,
                    EntraProtocolMetadataCase.WrongKeysPath => "https://login.microsoftonline.com/unadmitted/keys",
                    EntraProtocolMetadataCase.KeysQuery => "https://login.microsoftonline.com" + EntraProtocolFixturePolicy.Keys + "?appid=unadmitted",
                    EntraProtocolMetadataCase.KeysPort => "https://login.microsoftonline.com:444" + EntraProtocolFixturePolicy.Keys,
                    _ => "https://login.microsoftonline.com" + (CommonKeys ? EntraProtocolFixturePolicy.CommonKeys : EntraProtocolFixturePolicy.Keys)
                },
                ["response_types_supported"] = new[] { "code" },
                ["response_modes_supported"] = new[] { "query" },
                ["subject_types_supported"] = new[] { "pairwise" },
                ["scopes_supported"] = new[] { "openid", "profile" },
                ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
                ["token_endpoint_auth_methods_supported"] = new[] { "private_key_jwt" },
                ["code_challenge_methods_supported"] = new[] { "S256" }
            };
            if (Metadata == EntraProtocolMetadataCase.ParEndpoint) value["pushed_authorization_request_endpoint"] = "https://login.microsoftonline.com/unadmitted/par";
            if (Metadata is EntraProtocolMetadataCase.RequirePar or EntraProtocolMetadataCase.ExplicitFalsePar)
                value["require_pushed_authorization_requests"] = Metadata == EntraProtocolMetadataCase.RequirePar;
            if (Metadata == EntraProtocolMetadataCase.MalformedPar) value["require_pushed_authorization_requests"] = "false";
            await WriteJsonAsync(context, value, MetadataBodyGate); NextMetadataResponse?.TrySetResult(); return;
        }
        if (context.Request.Method == "GET" && (path == EntraProtocolFixturePolicy.Keys || path == EntraProtocolFixturePolicy.CommonKeys))
        { await WriteJsonAsync(context, crypto.Jwks(KeyCase, PublishSecond, OnlySecond), KeysBodyGate); NextKeysResponse?.TrySetResult(); return; }
        if (context.Request.Method == "GET" && path == EntraProtocolFixturePolicy.Authorize)
        { Authorize(context); return; }
        if (context.Request.Method == "POST" && path == EntraProtocolFixturePolicy.Token)
        { await RedeemAsync(context); return; }
        Interlocked.Increment(ref UnexpectedRequests); context.Response.StatusCode = 400;
        await context.Response.WriteAsJsonAsync(new { error = "unexpected_fixture_path" }, context.RequestAborted);
    }

    private void Authorize(HttpContext context)
    {
        var query = QueryHelpers.ParseQuery(context.Request.QueryString.Value);
        string One(string key)
        {
            EntraProtocolFixturePolicy.Check(query.TryGetValue(key, out var value) && value.Count == 1 && value[0] is { Length: > 0 and <= 16_384 },
                "The owned authorization request lost the required bounded singleton field: " + key + "."); return value[0]!;
        }
        EntraProtocolFixturePolicy.Check(One("client_id") == EntraProtocolFixturePolicy.Client.ToString("D") && One("redirect_uri") == Callback
            && One("response_type") == "code" && One("scope") == "openid profile"
            && One("prompt") == "select_account" && One("code_challenge_method") == "S256",
            "The standard challenge departed from the fixed code/PKCE/OpenID policy.");
        // The standard handler omits the default query response mode for code flow.
        EntraProtocolFixturePolicy.Check(!query.ContainsKey("response_mode") || One("response_mode") == "query",
            "The code flow requested a non-query response mode.");
        string challenge = One("code_challenge");
        EntraProtocolFixturePolicy.Check(challenge.Length == 43 && !query.ContainsKey("code_verifier") && !query.ContainsKey("client_secret")
            && !query.ContainsKey("request_uri") && !query.ContainsKey("access_token") && !query.ContainsKey("id_token")
            && !query.Keys.Any(key => key.StartsWith("x-client-", StringComparison.OrdinalIgnoreCase))
            && !query.ContainsKey("login_hint") && !query.ContainsKey("domain_hint") && !query.ContainsKey("returnUrl"),
            "A standard challenge leaked a verifier/token or used unadmitted PAR.");
        EntraProtocolFixturePolicy.Check(Interlocked.Increment(ref codeCount) <= 64, "The owned issuer code budget was exceeded.");
        string code = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var record = new EntraProtocolCode(One("nonce"), One("state"), BreakPkce ? "wrong-owned-challenge" : challenge, Callback);
        EntraProtocolFixturePolicy.Check(codes.TryAdd(code, record), "An owned authorization code collided.");
        context.Response.StatusCode = 303;
        context.Response.Headers.Location = QueryHelpers.AddQueryString(Callback, new Dictionary<string, string?> { ["code"] = code, ["state"] = record.State });
    }

    private async Task RedeemAsync(HttpContext context)
    {
        await HoldAsync(TokenGate, context.RequestAborted);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        EntraProtocolFixturePolicy.Check(context.Request.ContentLength is >= 1 and <= 16_384,
            "The owned standard token request did not have a bounded form body.");
        using var body = new MemoryStream(); await context.Request.Body.CopyToAsync(body, timeout.Token);
        EntraProtocolFixturePolicy.Check(body.Length <= 16_384, "The owned token form exceeded its bound.");
        var form = QueryHelpers.ParseQuery("?" + new UTF8Encoding(false, true).GetString(body.ToArray()));
        string? One(string key) => form.TryGetValue(key, out var value) && value.Count == 1 ? value[0] : null;
        if (One("grant_type") != "authorization_code" || One("client_id") != EntraProtocolFixturePolicy.Client.ToString("D")
            || One("client_assertion_type") != "urn:ietf:params:oauth:client-assertion-type:jwt-bearer"
            || form.ContainsKey("client_secret") || One("client_assertion") is not { } assertion
            || !await crypto.ValidateAssertionAsync(assertion))
        { Interlocked.Increment(ref AssertionRejected); await RefuseAsync(context, "invalid_client"); return; }
        lastAssertion = assertion; Interlocked.Increment(ref AssertionAccepted);
        if (One("code") is not { } code || !codes.TryGetValue(code, out var record)
            || Interlocked.Exchange(ref record.Consumed, 1) != 0 || DateTimeOffset.UtcNow >= record.ExpiresUtc || One("redirect_uri") != record.Redirect
            || One("code_verifier") is not { Length: >= 43 and <= 128 } verifier
            || Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))) != record.Challenge)
        { await RefuseAsync(context, "invalid_grant"); return; }
        Interlocked.Increment(ref PkceAccepted);
        if (TokenFailureStatus is { } status)
        {
            context.Response.StatusCode = (int)status;
            if ((int)status is >= 300 and < 400) context.Response.Headers.Location = "https://unadmitted.invalid/private";
            // HTTP forbids a 304 body; the unavailable classification must not depend
            // on receiving JSON (or even an error description) with that status.
            if (status == HttpStatusCode.NotModified) return;
            var failure = new Dictionary<string, object?> { ["error"] = "invalid_grant" };
            if (TokenFailureDescription is { } description) failure.Add("error_description", description);
            await context.Response.WriteAsJsonAsync(failure, context.RequestAborted); return;
        }
        if (MalformedTokenResponse)
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"error_description\":\"" + EntraProtocolFixturePolicy.PrivateMarker + "\"", context.RequestAborted); return;
        }
        string token = crypto.IdToken(record, TokenCase, SignSecond, out var expiry, TokenLifetimeSeconds); lastToken = token; LastExpiry = expiry;
        if (TokenCase == EntraProtocolTokenCase.UnapprovedRsaAlgorithm) await crypto.AssertValidUnsupportedRsaVectorAsync(token, SignSecond);
        Interlocked.Increment(ref TokenResponses);
        await WriteJsonAsync(context, new
        {
            token_type = "Bearer",
            expires_in = 300,
            access_token = "owned-opaque-access-token-not-retained",
            id_token = token
        }, TokenBodyGate);
    }

    private static async Task WriteJsonAsync(HttpContext context, object value, EntraProtocolGate? gate)
    {
        if (gate is null) { await context.Response.WriteAsJsonAsync(value, context.RequestAborted); return; }
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        int prefix = Math.Min(32, bytes.Length - 1);
        EntraProtocolFixturePolicy.Check(prefix > 0 && prefix < bytes.Length, "The partial response control did not retain an unread body tail.");
        context.Response.ContentType = "application/json"; context.Response.ContentLength = bytes.Length;
        await context.Response.Body.WriteAsync(bytes.AsMemory(0, prefix), context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
        gate.PrefixBytes = prefix; gate.DeclaredBytes = bytes.Length;
        await HoldAsync(gate, context.RequestAborted);
        await context.Response.Body.WriteAsync(bytes.AsMemory(prefix), context.RequestAborted);
    }

    private static Task RefuseAsync(HttpContext context, string code)
    {
        context.Response.StatusCode = 400;
        return context.Response.WriteAsJsonAsync(new { error = code, error_description = EntraProtocolFixturePolicy.PrivateMarker }, context.RequestAborted);
    }
    private static async Task HoldAsync(EntraProtocolGate? gate, CancellationToken token)
    {
        if (gate is null) return;
        gate.Entered.TrySetResult();
        try { await gate.Release.Task.WaitAsync(TimeSpan.FromSeconds(10), token); }
        catch (OperationCanceledException) { Interlocked.Exchange(ref gate.Canceled, 1); throw; }
        finally { gate.Ended.TrySetResult(); }
    }
    public ValueTask DisposeAsync() => new(disposal ??= DisposeCoreAsync());
    private async Task DisposeCoreAsync()
    {
        MetadataGate?.Release.TrySetResult(); TokenGate?.Release.TrySetResult();
        MetadataBodyGate?.Release.TrySetResult(); KeysBodyGate?.Release.TrySetResult(); TokenBodyGate?.Release.TrySetResult();
        if (app is not null)
        {
            Exception? stopFailure = null;
            try
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                applicationStopOperation ??= app.StopAsync(stop.Token); await applicationStopOperation.WaitAsync(TimeSpan.FromSeconds(15));
            }
            catch (Exception) { stopFailure = new EntraProtocolFixtureFailure("Owned issuer stop did not settle."); }
            applicationDisposeOperation ??= app.DisposeAsync().AsTask(); await applicationDisposeOperation.WaitAsync(TimeSpan.FromSeconds(15));
            EntraProtocolFixturePolicy.Check(applicationStopOperation is { IsCompleted: true } && applicationDisposeOperation.IsCompleted
                && work.Values.All(value => value.Task.IsCompleted), "Owned issuer original stop/disposal/work remained unsettled.");
            if (stopFailure is not null) throw stopFailure;
        }
        Settled = true;
    }
    public override string ToString() => nameof(EntraProtocolHttpsIssuer);
}
