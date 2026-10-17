using System.Net;
using System.Runtime.Versioning;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using Weft.Application;
using Weft.Identity;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class AuthHttpIntegrationTests(PostgresFixture postgres)
{
    [PostgresFact]
    public Task RealCallbackAndPairedBootstrapTokensRemainSeparateAndNonRenewing() => AuthHttpFixture.RunAsync(postgres, async test =>
    {
        var jar = new AuthHttpJar(); var anonymous = await test.StatusAsync(jar);
        AuthHttpFixture.Check(anonymous.State == "anonymous" && anonymous.Epoch is null && anonymous.Fingerprint is null && anonymous.Reply.Record.SessionReads == 0 && await test.RowsAsync() == 0, "Anonymous bootstrap acquired identity or session work.");
        var actionCookie = SetCookieHeaderValue.ParseList(anonymous.Reply.Headers["Set-Cookie"].ToList()).Single(cookie => cookie.Name == AuthHttpFixture.ActionCookie);
        AuthHttpFixture.Check(actionCookie.Secure && actionCookie.HttpOnly && !actionCookie.Domain.HasValue && actionCookie.Path == "/" && actionCookie.SameSite == Microsoft.Net.Http.Headers.SameSiteMode.Strict && actionCookie.Expires is null && actionCookie.MaxAge is null, "Anonymous auth-action cookie attributes changed.");
        jar = await test.LoginAsync(jar);
        var record = test.NewRecord(); record.PoisonAmbient = true;
        var status = await test.StatusAsync(jar, record: record);
        AuthHttpFixture.Check(status.State == "authenticated" && status.Epoch is not null && status.Fingerprint is { Length: 46 } && record.Renews == 0 && record.Identity is not null
            && status.Fingerprint == AuthHttpFixture.ExpectedFingerprint(record.Identity)
            && record.ActionCookieUsers.Count > 0 && record.ActionCookieUsers.All(user => user.Identity?.IsAuthenticated == true && user.Claims.Any(claim => claim.Type == "weft:login_epoch")),
            "Paired same-context bootstrap reused AF cache/ambient identity or renewed a session.");
        foreach (var pair in new[] { (Action: status.Epoch, Epoch: status.Action), (Action: status.Action, Epoch: (string?)null), (Action: (string?)null, Epoch: status.Epoch) })
        {
            int before = test.First.Transport.SnapshotAttempts().Count;
            var denied = await test.SendAsync(AuthHttpFixture.LoginPath, jar, "POST", pair.Action, pair.Epoch);
            AuthHttpFixture.Error(denied, HttpStatusCode.Forbidden, "requestRejected");
            AuthHttpFixture.Check(denied.Record.Creates == 0 && denied.Record.Revokes == 0 && denied.Record.BodyReads == 0 && !denied.Headers.ContainsKey("Set-Cookie") && test.First.Transport.SnapshotAttempts().Count == before, "A foreign/missing AF audience started a challenge or body/write.");
        }
        foreach (string cookie in new[] { AuthHttpFixture.ActionCookie, AuthHttpFixture.EpochCookie })
        {
            var missing = jar.Copy(); missing.Set(cookie, null);
            var denied = await test.SendAsync(AuthHttpFixture.LoginPath, missing, "POST", status.Action, status.Epoch);
            AuthHttpFixture.Error(denied, HttpStatusCode.Forbidden, "requestRejected"); AuthHttpFixture.Check(!denied.Headers.ContainsKey("Set-Cookie") && denied.Record.Creates == 0, "A missing AF cookie gained challenge authority.");
        }
        var direct = test.NewRecord(); direct.Direct = AuthHttpDirect.Read;
        _ = await test.SendAsync(AuthHttpFixture.StatusPath, jar, record: direct);
        AuthHttpFixture.Check(direct.SessionResult is not null && JsonSerializer.Serialize(direct.SessionResult) == "{}" && direct.SessionResult.ToString() == nameof(BrowserAuthenticationSession), "Private auth result serialized token/identity operands.");
        string metadata = CatalogHttpTestSupport.MappingPath(test.Data.Original.Facts.FutureRevision) + "/metadata";
        var catalogDenied = await test.SendAsync(metadata, jar, "PATCH", epoch: status.Action, mutate: message => message.Headers.TryAddWithoutValidation("Origin", test.Origin.GetLeftPart(UriPartial.Authority)));
        AuthHttpFixture.Check(catalogDenied.Status == HttpStatusCode.Forbidden && catalogDenied.Record.MetadataWrites == 0 && catalogDenied.Record.BodyReads == 0, "Auth-action AF became catalog authority.");
        AuthHttpFixture.Check(JsonSerializer.Serialize(record.Identity) == "{}", "Private112 identity serialized private facts.");
    });

    [PostgresFact]
    public Task EveryFixedRouteRefusesBodyAndWrongMethodBeforeProviderOrBusiness() => AuthHttpFixture.RunAsync(postgres, async test =>
    {
        var jar = new AuthHttpJar(); var status = await test.StatusAsync(jar); var flow = await test.FlowAsync(jar);
        foreach (var route in new[] {
            (Path: AuthHttpFixture.StatusPath, Method: "GET", Wrong: "HEAD", Allow: "GET"),
            (Path: AuthHttpFixture.LoginPath, Method: "POST", Wrong: "GET", Allow: "POST"),
            (Path: flow.Callback, Method: "GET", Wrong: "POST", Allow: "GET"),
            (Path: AuthHttpFixture.LogoutPath, Method: "POST", Wrong: "GET", Allow: "POST"),
            (Path: AuthHttpFixture.ForgetPath, Method: "POST", Wrong: "OPTIONS", Allow: "POST") })
        {
            int before = test.First.Transport.SnapshotAttempts().Count;
            var body = await test.SendAsync(route.Path, jar, route.Method, status.Action, mutate: message => { message.Content = new ByteArrayContent([]); message.Content.Headers.ContentType = new("application/x-www-form-urlencoded"); });
            AuthHttpFixture.SharedHeaders(body); AuthHttpFixture.Check((int)body.Status is >= 400 and < 500, "A fixed auth route accepted a body-shaped request."); AuthHttpFixture.NoLaterWork(body);
            var method = await test.SendAsync(route.Path, jar, route.Wrong, status.Action);
            AuthHttpFixture.SharedHeaders(method); AuthHttpFixture.Check(method.Status == HttpStatusCode.MethodNotAllowed && method.Headers.TryGetValue("Allow", out var allow) && allow.SequenceEqual(new[] { route.Allow }) && !method.Headers.ContainsKey("Access-Control-Allow-Origin") && !method.Headers.ContainsKey("Set-Cookie"), "A wrong auth method gained work, cookies or preflight authority."); AuthHttpFixture.NoLaterWork(method);
            AuthHttpFixture.Check(test.First.Transport.SnapshotAttempts().Count == before, "Early body/method rejection sent provider HTTP.");
        }
        foreach (bool chunked in new[] { false, true })
        { var reply = await test.HeadersOnlyAsync(AuthHttpFixture.LoginPath, jar, status, chunked); AuthHttpFixture.SharedHeaders(reply); AuthHttpFixture.Check((int)reply.Status is >= 400 and < 500, "A body-bearing headers-only request was admitted."); AuthHttpFixture.NoLaterWork(reply); }
        foreach (bool missing in new[] { true, false })
        {
            var record = test.NewRecord(); record.MissingBodyDetector = missing; record.PositiveBodyDetector = !missing;
            var reply = await test.SendAsync(AuthHttpFixture.LoginPath, jar, "POST", status.Action, record: record);
            AuthHttpFixture.Check((int)reply.Status >= 400 && !reply.Headers.ContainsKey("Set-Cookie"), "Absent/positive actual body-detection support was accepted."); AuthHttpFixture.NoLaterWork(reply);
        }
        var healthy = await test.CompleteAsync(flow); AuthHttpFixture.Check(healthy.Status == HttpStatusCode.SeeOther, "Early rejected callback bodies/methods consumed the real flow.");
    });

    [PostgresFact]
    public Task PublicFacadeMethodsEnforceTheirOwnActualRequestGate() => AuthHttpFixture.RunAsync(postgres, async test =>
    {
        foreach (var operation in new[] { AuthHttpDirect.Read, AuthHttpDirect.Begin, AuthHttpDirect.Complete, AuthHttpDirect.Logout, AuthHttpDirect.Forget })
        {
            var record = test.NewRecord(); record.Direct = operation; record.ExpectedDirectFailure = true;
            string path = operation == AuthHttpDirect.Read ? AuthHttpFixture.LoginPath : AuthHttpFixture.StatusPath;
            string method = operation == AuthHttpDirect.Read ? "POST" : "GET";
            var reply = await test.SendAsync(path, new(), method, record: record);
            AuthHttpFixture.Check((record.ModuleOperation is null or { IsFaulted: true }) && record.AuthenticationFailure is not null && record.ClosedError is { InnerException: null }
                && record.ClosedError.ToString() == record.ClosedError.GetType().Name && reply.Status != HttpStatusCode.NoContent && !reply.Headers.ContainsKey("Set-Cookie"), "A trusted public facade method bypassed its own route/method admission.");
            AuthHttpFixture.NoLaterWork(reply);
        }
    });

    [PostgresFact]
    public Task ExactHeadersOriginRawCookiesAndQueriesCannotBootstrapUnsafeAuthority() => AuthHttpFixture.RunAsync(postgres, async test =>
    {
        var jar = new AuthHttpJar(); var status = await test.StatusAsync(jar);
        foreach (var action in new[] { (Path: AuthHttpFixture.LoginPath, Header: "X-Weft-Login"), (Path: AuthHttpFixture.LogoutPath, Header: "X-Weft-Logout"), (Path: AuthHttpFixture.ForgetPath, Header: "X-Weft-Forget-Local") })
        {
            var reply = await test.SendAsync(action.Path, jar, "POST", status.Action, mutate: message => message.Headers.Remove(action.Header));
            AuthHttpFixture.Check((int)reply.Status is >= 400 and < 500, "A missing exact action header was accepted."); AuthHttpFixture.NoLaterWork(reply); AuthHttpFixture.NoLocalDeletion(reply);
        }
        Action<HttpRequestMessage>[] vectors = [
            message => message.Headers.Remove("Origin"),
            message => { message.Headers.Remove("Origin"); message.Headers.TryAddWithoutValidation("Origin", "https://foreign.invalid"); },
            message => message.Headers.TryAddWithoutValidation("Origin", test.Origin.GetLeftPart(UriPartial.Authority)),
            message => message.Headers.TryAddWithoutValidation("X-Weft-Login", "1"),
            message => { message.Headers.Remove("X-Weft-Login"); message.Headers.TryAddWithoutValidation("X-Weft-Login", "1, 1"); },
            message => message.Headers.TryAddWithoutValidation("X-Weft-Auth-Token", status.Action),
            message => { message.Headers.Remove("X-Weft-Auth-Token"); message.Headers.TryAddWithoutValidation("X-Weft-Auth-Token", "not+canonical"); },
            message => { message.Headers.Remove("X-Weft-Auth-Token"); message.Headers.TryAddWithoutValidation("X-Weft-Auth-Token", new string('A', 4097)); },
            message => message.Headers.TryAddWithoutValidation("Authorization", "Bearer untrusted-fixture"),
            message => { message.Headers.Remove("Cookie"); message.Headers.TryAddWithoutValidation("Cookie", jar.Header + "; " + AuthHttpFixture.ActionCookie + "=duplicate"); },
            message => { message.Headers.Remove("Cookie"); message.Headers.TryAddWithoutValidation("Cookie", jar.Header + "; __host-weft.authaction=alias"); },
            message => { message.Headers.Remove("Cookie"); message.Headers.TryAddWithoutValidation("Cookie", jar.Header + "; " + string.Join("; ", Enumerable.Range(0, 129).Select(index => "x" + index + "=1"))); },
            message => { message.Headers.Remove("Cookie"); message.Headers.TryAddWithoutValidation("Cookie", "x=" + new string('A', 16384)); }
        ];
        foreach (var mutate in vectors)
        {
            int before = test.First.Transport.SnapshotAttempts().Count;
            var reply = await test.SendAsync(AuthHttpFixture.LoginPath, jar, "POST", status.Action, mutate: mutate);
            AuthHttpFixture.Check((int)reply.Status is >= 400 and < 500 && !reply.Headers.ContainsKey("Set-Cookie"), "An exact-header/raw-cookie violation started challenge effects."); AuthHttpFixture.NoLaterWork(reply); AuthHttpFixture.Check(test.First.Transport.SnapshotAttempts().Count == before, "A refused header/cookie sent provider HTTP.");
        }
        var query = await test.SendAsync(AuthHttpFixture.LoginPath + "?returnUrl=%2F", jar, "POST", status.Action); AuthHttpFixture.Error(query, HttpStatusCode.BadRequest, "invalidRequest"); AuthHttpFixture.NoLaterWork(query);
        foreach (var mutate in new Action<HttpRequestMessage>[] { message => message.Headers.Remove("X-Weft-Session"), message => message.Headers.TryAddWithoutValidation("X-Weft-Session", "1"), message => message.Headers.TryAddWithoutValidation("Origin", "https://foreign.invalid"), message => message.Headers.TryAddWithoutValidation("Origin", new[] { test.Origin.GetLeftPart(UriPartial.Authority), test.Origin.GetLeftPart(UriPartial.Authority) }) })
        { var reply = await test.SendAsync(AuthHttpFixture.StatusPath, jar, mutate: mutate); AuthHttpFixture.Check((int)reply.Status is >= 400 and < 500, "A malformed safe status header/Origin was accepted."); AuthHttpFixture.NoLaterWork(reply); }
    });

    [PostgresFact]
    public Task CallbackQueryDialectAndLateNonceReplayFailuresNeverReachBindingOrIssue() => AuthHttpFixture.RunAsync(postgres, async test =>
    {
        var flow = await test.FlowAsync();
        string[] rejected = [
            flow.Callback + "&state=duplicate", flow.Callback + "&%73tate=duplicate", flow.Callback + "&State=alias", flow.Callback + "&id_token=forged", flow.Callback + "&error=access_denied",
            flow.Callback + "&trace_id=unsupported", flow.Callback + "&client_info=" + new string('A', 513), flow.Callback + "&client_info=%C3%28", flow.Callback + "&client_info=%00",
            flow.Callback + "&" + string.Join("&", Enumerable.Range(0, 17).Select(index => "unknown" + index + "=x")), AuthHttpFixture.CallbackPath + "?state=" + new string('A', 8193) + "&code=x", AuthHttpFixture.CallbackPath + "?code=" + new string('A', 8192) + "&state=" + new string('B', 8192)
        ];
        foreach (var callback in rejected)
        {
            int before = test.First.Transport.SnapshotAttempts().Count;
            var reply = await test.CompleteAsync(flow, callback: callback); AuthHttpFixture.CallbackError(reply, HttpStatusCode.Unauthorized); AuthHttpFixture.NoLaterWork(reply);
            AuthHttpFixture.Check(test.First.Transport.SnapshotAttempts().Count == before && !reply.Headers.ContainsKey("Set-Cookie"), "An early query rejection advanced provider/flow-cookie work.");
        }
        var percent = await test.RawCallbackAsync(flow.Callback + "&client_info=%ZZ", flow.Jar); AuthHttpFixture.SharedHeaders(percent); AuthHttpFixture.Check(percent.Status == HttpStatusCode.Unauthorized && percent.Record.Entered, "Invalid raw percent syntax was normalized away or escaped the callback gate."); AuthHttpFixture.NoLaterWork(percent);
        var good = await test.CompleteAsync(flow, callback: flow.Callback + "&session_state=opaque&client_info=" + new string('A', 512) + "&iss=" + Uri.EscapeDataString(EntraProtocolFixturePolicy.Issuer));
        AuthHttpFixture.Check(good.Status == HttpStatusCode.SeeOther, "The exact optional callback dialect was rejected.");
        var replay = await test.CompleteAsync(flow); AuthHttpFixture.CallbackError(replay, HttpStatusCode.Unauthorized); AuthHttpFixture.NoLaterWork(replay);
        test.Issuer.TokenCase = EntraProtocolTokenCase.BadNonce;
        var bad = await test.FlowAsync(); var denial = await test.CompleteAsync(bad); AuthHttpFixture.CallbackError(denial, HttpStatusCode.Unauthorized); AuthHttpFixture.NoLaterWork(denial);
        AuthHttpFixture.Check(test.Issuer.TokenResponses >= 2 && denial.Record.Resolves == 0, "The nonce denial did not pass through actual signed token-response verification.");
        test.Issuer.TokenCase = EntraProtocolTokenCase.Good;
        var shared = new AuthHttpJar(); var errorFlow = await test.FlowAsync(shared); var firstNames = shared.Names.Where(name => name != AuthHttpFixture.ActionCookie).ToArray();
        var remaining = await test.FlowAsync(shared);
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(errorFlow.Callback[(errorFlow.Callback.IndexOf('?'))..]);
        string errorCallback = AuthHttpFixture.CallbackPath + "?state=" + Uri.EscapeDataString(query["state"].Single()!) + "&error=access_denied&error_description=" + Uri.EscapeDataString(EntraProtocolFixturePolicy.PrivateMarker) + "&client_info=" + new string('A', 512);
        var error = await test.CompleteAsync(errorFlow, callback: errorCallback); AuthHttpFixture.CallbackError(error, HttpStatusCode.Unauthorized); AuthHttpFixture.NoLaterWork(error);
        AuthHttpFixture.Check(firstNames.All(name => !shared.Names.Contains(name)) && shared.Names.Count(name => name != AuthHttpFixture.ActionCookie) == 2, "Matched denial cleared another flow or left its own nonce/correlation cookies.");
        var independent = await test.CompleteAsync(remaining); AuthHttpFixture.Check(independent.Status == HttpStatusCode.SeeOther, "An unrelated outstanding flow was damaged by denial cleanup.");
    });

    [PostgresFact]
    public Task CallbackBindingAndPrincipalAdmissionAreFreshAndNeverProvision() => AuthHttpFixture.RunAsync(postgres, async test =>
    {
        var first = await test.FlowAsync(); long rows = await test.RowsAsync();
        var disabled = await test.Owner.DisableAsync(test.Binding.Key, test.Data.Actor.PrincipalId, test.Binding.Version);
        AuthHttpFixture.Check(disabled.Outcome == ExternalIdentityBindingOwnerOutcome.Disabled, "The actual binding was not disabled.");
        var reply = await test.CompleteAsync(first); AuthHttpFixture.CallbackError(reply, HttpStatusCode.Unauthorized);
        AuthHttpFixture.Check(reply.Record.Resolves == 1 && reply.Record.Creates == 0 && await test.RowsAsync() == rows, "A disabled fresh binding was cached or provisioned.");
    });

    [PostgresFact]
    public async Task UnknownInactiveAndOperationalBindingRemainDistinctFromSuccessfulSignIn()
    {
        foreach (string state in new[] { "unknown", "inactive", "unavailable" })
            await AuthHttpFixture.RunAsync(postgres, async test =>
            {
                var flow = await test.FlowAsync(); var record = test.NewRecord();
                if (state == "inactive") await test.Data.Case.ExecuteAsync("UPDATE weft.principals SET active=false WHERE id=$1", test.Data.Actor.PrincipalId.Value);
                if (state == "unavailable") { record.BindingUnavailable = true; await test.First.CloseIdentityPoolAsync(); }
                var reply = await test.CompleteAsync(flow, record: record); AuthHttpFixture.CallbackError(reply, state == "unavailable" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Unauthorized);
                AuthHttpFixture.Check(record.Resolves == 1 && record.Creates == 0 && record.Revokes == 0 && await test.RowsAsync() == 0, "A refused fresh binding reached session write/provisioning.");
            }, bindIssuer: state != "unknown");
    }

    [PostgresFact]
    public Task SameActorReauthenticationWaitsForOldRevokeAndChangesEpochAcrossIndependentInstances() => AuthHttpFixture.RunAsync(postgres, async test =>
    {
        // Keep the provider deadline beyond the ordinary idle window so this
        // control can actually enter renewal without changing production policy.
        test.Issuer.TokenLifetimeSeconds = 1800;
        var jar = await test.LoginAsync(); var oldStatus = await test.StatusAsync(jar); var oldCookie = jar.Get(AuthHttpFixture.SessionCookie); var oldAfCookie = jar.Get(AuthHttpFixture.EpochCookie);
        await test.AddSecondAsync(); var replica = await test.StatusAsync(jar, test.Second);
        AuthHttpFixture.Check(replica.Fingerprint == oldStatus.Fingerprint, "The same epoch changed across independent ring/profile instances.");
        var flow = await test.FlowAsync(jar, test.Second); var record = test.NewRecord(); record.RevokeGate = new();
        var pending = test.CompleteAsync(flow, test.Second, record); _ = test.Track(pending);
        try
        {
            await Task.WhenAll(record.RevokeGate.Entered.Task, record.RevokeGate.Original.Task).WaitAsync(TimeSpan.FromSeconds(5));
            var original = record.RevokeGate.Original.Task.Result;
            AuthHttpFixture.Check(!original.IsCompleted && record.Creates == 0 && !record.Order.Contains("create-enter"), "Fresh store began before the original old-revoke ACK.");
            record.RevokeGate.Release.TrySetResult(); var reply = await pending;
            AuthHttpFixture.Check(reply.Status == HttpStatusCode.SeeOther && original.IsCompletedSuccessfully && record.Revokes == 1 && record.Creates == 1 && record.Renews == 0, "Same-actor reauthentication skipped ACK-gated fresh issuance.");
            AuthHttpFixture.Check(record.CreatedFacts is { Lifetime: var lifetime } && lifetime.IdleExpiresAtUtc < lifetime.AbsoluteExpiresAtUtc,
                "The renewal control was capped at its absolute deadline.");
            var order = record.Order.ToArray(); AuthHttpFixture.Check(Array.IndexOf(order, "revoke-ack") < Array.IndexOf(order, "create-enter"), "New Store preceded old-revoke completion.");
        }
        finally { record.ReleaseAll(); }
        var current = await test.StatusAsync(jar, test.Second);
        AuthHttpFixture.Check(current.Fingerprint != oldStatus.Fingerprint && jar.Get(AuthHttpFixture.SessionCookie) != oldCookie && jar.Get(AuthHttpFixture.EpochCookie) == oldAfCookie, "Fresh same-actor issuance reused its old epoch or replaced its ordinary AF cookie.");
        var denied = await test.SendAsync(AuthHttpFixture.LoginPath, jar, "POST", current.Action, oldStatus.Epoch, host: test.Second); AuthHttpFixture.Error(denied, HttpStatusCode.Forbidden, "requestRejected");
        var context = await test.SendAsync(AuthHttpFixture.ContextPath, jar, host: test.Second);
        AuthHttpFixture.Check(context.Status == HttpStatusCode.OK && context.Record.Renews > 0 && context.Json().GetProperty("identityContext").GetString() == current.Fingerprint, "Ordinary due renewal changed the session fingerprint.");
    });

    [PostgresFact]
    public async Task RealCommittedAckLossNeverBecomesRedirectRetryStoreOrLocalForgetSuccess()
    {
        foreach (bool revoke in new[] { true, false })
            await AuthHttpFixture.RunAsync(postgres, async test =>
            {
                var jar = await test.LoginAsync(); var flow = await test.FlowAsync(jar); long before = await test.RowsAsync(); var record = test.NewRecord(); record.LoseRevokeAck = revoke; record.LoseCreateAck = !revoke;
                var reply = await test.CompleteAsync(flow, record: record); AuthHttpFixture.CallbackError(reply, HttpStatusCode.ServiceUnavailable); AuthHttpFixture.NoLocalDeletion(reply);
                AuthHttpFixture.Check(record.Revokes == 1 && record.Creates == (revoke ? 0 : 1) && await test.RowsAsync() == before + (revoke ? 0 : 1), "ACK loss retried/compensated or entered an unauthorized new Store.");
                var status = await test.StatusAsync(jar);
                var forgotten = await test.SendAsync(AuthHttpFixture.ForgetPath, jar, "POST", status.Action);
                AuthHttpFixture.Error(forgotten, HttpStatusCode.Conflict, "forgetNotAvailable"); AuthHttpFixture.NoLocalDeletion(forgotten);
            });
    }

    [PostgresFact]
    public Task LogoutAcknowledgesBeforeDeletingAndExactDeniedForgetNeverWrites() => AuthHttpFixture.RunAsync(postgres, async test =>
    {
        var jar = await test.LoginAsync(); var status = await test.StatusAsync(jar); var record = test.NewRecord(); record.RevokeGate = new();
        var pending = test.SendAsync(AuthHttpFixture.LogoutPath, jar, "POST", status.Action, status.Epoch, record); _ = test.Track(pending);
        try
        {
            await Task.WhenAll(record.RevokeGate.Entered.Task, record.RevokeGate.Original.Task).WaitAsync(TimeSpan.FromSeconds(5));
            AuthHttpFixture.Check(!pending.IsCompleted && record.Context is not null && (!record.Context.Response.Headers.TryGetValue("Set-Cookie", out var earlyCookies) || SetCookieHeaderValue.ParseList(earlyCookies.ToArray().Where(value => value is not null).Select(value => value!).ToList()).All(cookie => cookie.Value.ToString().Length != 0)), "Logout deleted cookies before actual revoke ACK.");
            record.RevokeGate.Release.TrySetResult(); var reply = await pending; AuthHttpFixture.SharedHeaders(reply);
            AuthHttpFixture.Check(reply.Status == HttpStatusCode.NoContent && record.Revokes == 1 && AuthHttpFixture.Deletes(reply, AuthHttpFixture.SessionCookie) && AuthHttpFixture.Deletes(reply, AuthHttpFixture.EpochCookie) && AuthHttpFixture.Deletes(reply, AuthHttpFixture.ActionCookie), "Acknowledged logout did not clear its exact audiences."); jar.Apply(reply);
        }
        finally { record.ReleaseAll(); }
        var empty = await test.StatusAsync(jar);
        var emptyLogout = await test.SendAsync(AuthHttpFixture.LogoutPath, jar, "POST", empty.Action); AuthHttpFixture.Check(emptyLogout.Status == HttpStatusCode.NoContent && emptyLogout.Record.Revokes == 0, "Absent local state did not complete acknowledged local absence."); jar.Apply(emptyLogout); empty = await test.StatusAsync(jar);
        var emptyForget = await test.SendAsync(AuthHttpFixture.ForgetPath, jar, "POST", empty.Action); AuthHttpFixture.Error(emptyForget, HttpStatusCode.Conflict, "forgetNotAvailable"); AuthHttpFixture.NoLocalDeletion(emptyForget);
        var second = await test.LoginAsync(); long rows = await test.RowsAsync(); second.Set(AuthHttpFixture.SessionCookie, "invalid-owned-carrier");
        var malformed = await test.StatusAsync(second); AuthHttpFixture.Check(malformed.State == "authenticationRequired", "Malformed carrier became authenticated.");
        var logout = await test.SendAsync(AuthHttpFixture.LogoutPath, second, "POST", malformed.Action); AuthHttpFixture.Check((int)logout.Status is >= 400 and < 500, "Malformed carrier became acknowledged logout."); AuthHttpFixture.NoLocalDeletion(logout);
        var forbidden = await test.SendAsync(AuthHttpFixture.ForgetPath, second, "POST", mutate: message => message.Headers.Remove("X-Weft-Forget-Local")); AuthHttpFixture.NoLocalDeletion(forbidden);
        var forget = await test.SendAsync(AuthHttpFixture.ForgetPath, second, "POST", malformed.Action); AuthHttpFixture.SharedHeaders(forget);
        AuthHttpFixture.Check(forget.Status == HttpStatusCode.OK && forget.Json().GetProperty("outcome").GetString() == "revocationUnconfirmed" && forget.Record.Creates == 0 && forget.Record.Revokes == 0 && await test.RowsAsync() == rows
            && AuthHttpFixture.Deletes(forget, AuthHttpFixture.SessionCookie) && AuthHttpFixture.Deletes(forget, AuthHttpFixture.EpochCookie) && AuthHttpFixture.Deletes(forget, AuthHttpFixture.ActionCookie), "Explicit exact-Denied forgetting mutated the server or claimed revocation.");
    });

    [PostgresFact]
    public Task StartedHeadersAfterCommittedStoreAndLogoutAckLossRemainAmbiguous() => AuthHttpFixture.RunAsync(postgres, async test =>
    {
        var flow = await test.FlowAsync(); var record = test.NewRecord(); record.Direct = AuthHttpDirect.Complete; record.StartHeadersAfterCreate = true;
        var reply = await test.SendAsync(flow.Callback, flow.Jar, record: record);
        AuthHttpFixture.Check(record.Creates == 1 && record.ModuleOperation is { IsFaulted: true } && (record.Failure == ServerSessionIdentityFailureKind.Unavailable || record.AuthenticationFailure == BrowserAuthenticationFailureKind.Unavailable)
            && record.ClosedError is { InnerException: null } && record.ClosedError.ToString() == record.ClosedError.GetType().Name
            && reply.Status != HttpStatusCode.SeeOther && !reply.Headers.ContainsKey("Location") && await test.RowsAsync() == 1, "Started headers hid a committed issue failure as a second successful callback result."); AuthHttpFixture.NoLocalCookieEffect(reply);
        var jar = await test.LoginAsync(); var status = await test.StatusAsync(jar); var loss = test.NewRecord(); loss.LoseRevokeAck = true;
        var logout = await test.SendAsync(AuthHttpFixture.LogoutPath, jar, "POST", status.Action, status.Epoch, loss);
        AuthHttpFixture.Error(logout, HttpStatusCode.ServiceUnavailable, "unavailable"); AuthHttpFixture.NoLocalDeletion(logout);
        AuthHttpFixture.Check(loss.Revokes == 1 && loss.Creates == 0, "Logout ACK loss retried or issued a session.");
        var stale = await test.StatusAsync(jar);
        AuthHttpFixture.Check(stale.State == "authenticationRequired", "Committed revoked carrier remained live.");
        var acknowledged = await test.SendAsync(AuthHttpFixture.LogoutPath, jar, "POST", stale.Action);
        AuthHttpFixture.Check(acknowledged.Status == HttpStatusCode.NoContent && acknowledged.Record.Revokes == 1, "A decodable nonlive carrier skipped its acknowledged revoke path.");
    });

    [PostgresFact]
    public Task PriorFailedTransitionAndOperationalObservationCannotBecomeForgetting() => AuthHttpFixture.RunAsync(postgres, async test =>
    {
        var valid = await test.LoginAsync(); var status = await test.StatusAsync(valid);
        foreach (bool integrity in new[] { false, true })
        {
            var record = test.NewRecord(); record.ReaderUnavailable = !integrity; record.ReaderIntegrity = integrity; record.PriorObserve = true;
            var reply = await test.SendAsync(AuthHttpFixture.ForgetPath, valid, "POST", status.Action, record: record);
            AuthHttpFixture.Check(reply.Status == HttpStatusCode.ServiceUnavailable && record.SessionReads == 1 &&
                record.PriorObservation is { Kind: ServerSessionRequestKind.Unavailable, Identity: null } &&
                record.InjectedReaderFailure == (integrity ? ServerSessionIdentityFailureKind.IntegrityFailure : ServerSessionIdentityFailureKind.Unavailable) &&
                record.Creates == 0 && record.Renews == 0 && record.Revokes == 0,
                "Retained operational/integrity state became local clearing success."); AuthHttpFixture.NoLocalDeletion(reply);
            var bootstrapRecord = test.NewRecord(); bootstrapRecord.ReaderUnavailable = !integrity; bootstrapRecord.ReaderIntegrity = integrity;
            var bootstrap = await test.SendAsync(AuthHttpFixture.StatusPath, valid, record: bootstrapRecord); AuthHttpFixture.Error(bootstrap, HttpStatusCode.ServiceUnavailable, "unavailable"); AuthHttpFixture.NoLocalDeletion(bootstrap);
        }
        var malformed = valid.Copy(); malformed.Set(AuthHttpFixture.SessionCookie, "invalid-owned-carrier"); var malformedStatus = await test.StatusAsync(malformed);
        var failed = test.NewRecord(); failed.PriorDeniedRevoke = true;
        var refusal = await test.SendAsync(AuthHttpFixture.ForgetPath, malformed, "POST", malformedStatus.Action, record: failed);
        AuthHttpFixture.Check(failed.PriorFailure == ServerSessionIdentityFailureKind.Denied && (int)refusal.Status >= 400 && refusal.Record.Revokes == 0, "Forgetting reset an already Failed112 transition."); AuthHttpFixture.NoLocalDeletion(refusal);
    });

    [PostgresFact]
    public Task AuthActionProfileFailureSurvivesStandardCookieFallback() => AuthHttpFixture.RunAsync(postgres, async test =>
    {
        var jar = new AuthHttpJar(); _ = await test.StatusAsync(jar); var record = test.NewRecord(); record.BreakProfileAtActionCookie = true;
        var reply = await test.SendAsync(AuthHttpFixture.StatusPath, jar, record: record);
        AuthHttpFixture.Check(record.ActionCookieUsers.Count > 0 && !record.BreakProfileAtActionCookie, "The sticky failure vector never reached actual standard auth-action cookie access.");
        AuthHttpFixture.Error(reply, HttpStatusCode.ServiceUnavailable, "unavailable"); AuthHttpFixture.NoLocalDeletion(reply);
        AuthHttpFixture.Check(!reply.Headers.ContainsKey("Set-Cookie") && reply.Record.Resolves == 0 && reply.Record.Creates == 0, "Standard AF fallback hid a profile failure as a new successful cookie/token.");
    });

    [PostgresFact]
    public Task CatalogFingerprintRetainsItsAdmittedEpochAcrossAnotherStatusAndAmbientIdentity() => AuthHttpFixture.RunAsync(postgres, async test =>
    {
        var a = await test.LoginAsync(); var statusA = await test.StatusAsync(a); var b = await test.LoginAsync(); var statusB = await test.StatusAsync(b);
        AuthHttpFixture.Check(statusA.Fingerprint != statusB.Fingerprint, "Independent real login epochs collided or were reused.");
        var record = test.NewRecord(); record.CaptureIdentity = true; record.PoisonAmbient = true; record.CatalogGate = new();
        var pending = test.SendAsync(AuthHttpFixture.ContextPath, a, record: record); _ = test.Track(pending);
        try
        {
            await Task.WhenAll(record.CatalogGate.Entered.Task, record.CatalogGate.Original.Task).WaitAsync(TimeSpan.FromSeconds(5));
            _ = await test.StatusAsync(b); record.CatalogGate.Release.TrySetResult(); var reply = await pending;
            AuthHttpFixture.Check(reply.Status == HttpStatusCode.OK && record.Identity is not null && reply.Json().GetProperty("identityContext").GetString() == statusA.Fingerprint
                && reply.Json().GetProperty("identityContext").GetString() == AuthHttpFixture.ExpectedFingerprint(record.Identity), "Catalog mapping used another observation/status/ambient epoch.");
        }
        finally { record.ReleaseAll(); }
        var absent = await test.SendAsync(AuthHttpFixture.ContextPath, new(), mutate: message => message.Headers.TryAddWithoutValidation("X-Weft-Identity-Context", statusB.Fingerprint));
        AuthHttpFixture.Check(absent.Status == HttpStatusCode.Unauthorized && absent.Record.CatalogReads == 0, "Presentation fingerprint became authority.");
    });

    [PostgresFact]
    public async Task CanceledOriginalReaderBindingAndStoreWorkKeepModuleDisposalPending()
    {
        foreach (string stage in new[] { "reader", "binding", "store" })
            await AuthHttpFixture.RunAsync(postgres, async test =>
            {
                var jar = await test.LoginAsync(); var record = test.NewRecord(); record.CallerCancellation = new();
                var gate = new AuthHttpGate { IgnoreCancellation = true };
                string path;
                if (stage == "reader") { record.Direct = AuthHttpDirect.Read; record.ReaderGate = gate; path = AuthHttpFixture.StatusPath; }
                else
                {
                    var flow = await test.FlowAsync(jar); jar = flow.Jar; path = flow.Callback; record.Direct = AuthHttpDirect.Complete;
                    if (stage == "binding") record.BindingGate = gate; else record.CreateGate = gate;
                }
                var pending = test.SendAsync(path, jar, record: record); _ = test.Track(pending);
                try
                {
                    await Task.WhenAll(gate.Entered.Task, gate.Original.Task, record.ModuleStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));
                    var original = gate.Original.Task.Result; var method = record.ModuleOperation!;
                    record.CallerCancellation.Cancel(); await gate.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    var disposal = test.First.BeginModuleDisposal();
                    AuthHttpFixture.Check(!original.IsCompleted && !method.IsCompleted && !disposal.IsCompleted, "Canceled public wait released the original method/resource lease before stored work settled.");
                    gate.Release.TrySetResult(); var reply = await pending;
                    await disposal.WaitAsync(TimeSpan.FromSeconds(10));
                    AuthHttpFixture.Check(original.IsCanceled && method.IsCanceled && record.Canceled && (int)reply.Status == 499 && !reply.Headers.ContainsKey("Location"), "Canceled original work became a result/redirect or did not drain."); AuthHttpFixture.NoLocalCookieEffect(reply);
                    var closed = test.NewRecord(); closed.Direct = AuthHttpDirect.Read; closed.ExpectedDirectFailure = true;
                    var refused = await test.SendAsync(AuthHttpFixture.StatusPath, jar, record: closed);
                    AuthHttpFixture.Check((closed.ModuleOperation is null or { IsFaulted: true }) && (closed.AuthenticationFailure == BrowserAuthenticationFailureKind.Unavailable || closed.Failure == ServerSessionIdentityFailureKind.Unavailable) && refused.Status != HttpStatusCode.NoContent, "Disposed auth module admitted a new method."); AuthHttpFixture.NoLaterWork(refused); AuthHttpFixture.NoLocalCookieEffect(refused);
                    var probe = test.NewRecord(); probe.Direct = AuthHttpDirect.BorrowedObserve;
                    var observed = await test.SendAsync(AuthHttpFixture.StatusPath, jar, record: probe);
                    AuthHttpFixture.Check(observed.Status == HttpStatusCode.NoContent && !probe.Unexpected, "Module disposal disposed its borrowed112 browser/profile/reader.");
                    var reader = test.NewRecord(); reader.Direct = AuthHttpDirect.BorrowedIdentity;
                    var binding = await test.SendAsync(AuthHttpFixture.StatusPath, jar, record: reader);
                    AuthHttpFixture.Check(binding.Status == HttpStatusCode.NoContent && reader.BorrowedIdentityResolved && !test.First.Identities.Disposed, "Module disposal disposed its borrowed external-identity reader.");
                    var provider = test.NewRecord(); provider.Direct = AuthHttpDirect.BorrowedBegin;
                    var challenge = await test.SendAsync(AuthHttpFixture.LoginPath, new(), "POST", record: provider);
                    AuthHttpFixture.Check(challenge.Status == HttpStatusCode.NoContent && provider.ChallengeResult is not null && test.Crypto.ClientCertificate.HasPrivateKey, "Module disposal disposed its borrowed116 client/certificate/profile.");
                }
                finally { gate.Release.TrySetResult(); }
            });
    }

    [PostgresFact]
    public Task CallerCancellationDuringFinalDrainDoesNotPublishCachedBootstrap() => FinalDrainCancellationAsync(cancelCaller: true);

    [PostgresFact]
    public Task ModuleDisposalDuringFinalDrainDoesNotPublishCachedBootstrap() => FinalDrainCancellationAsync(cancelCaller: false);

    private Task FinalDrainCancellationAsync(bool cancelCaller) => AuthHttpFixture.RunAsync(postgres, async test =>
    {
        var phase = AuthHttpFinalDrainPhase.Login;
        AuthHttpRecord? record = null; AuthHttpGate? gate = null; Task? pending = null, disposal = null;
        try
        {
            var jar = await test.LoginAsync(); phase = AuthHttpFinalDrainPhase.PrimeStatus; _ = await test.StatusAsync(jar);
            phase = AuthHttpFinalDrainPhase.Configure;
            record = test.NewRecord(); record.Direct = AuthHttpDirect.Read; record.PriorObserve = true;
            record.CallerCancellation = new(); record.PriorAdmissionCancellation = new(); record.ReaderGateAtRead = 2;
            gate = new AuthHttpGate(); record.ReaderGate = gate;
            phase = AuthHttpFinalDrainPhase.Send;
            var response = test.SendAsync(AuthHttpFixture.StatusPath, jar, record: record); pending = response;
            _ = test.Track(response, role: AuthHttpTaskRole.HttpSend);
            phase = AuthHttpFinalDrainPhase.BoundaryWait;
            await Task.WhenAll(gate.Entered.Task, gate.Original.Task, record.ModuleStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));
            var reader = gate.Original.Task.Result; var admission = record.PriorAdmissionOperation!; var method = record.ModuleOperation!;
            // Middleware completed ObserveAsync before starting the independent admission.
            // Both bootstrap AF operations are synchronous over that cached observation:
            // their cookie access followed by ReadSessionAsync returning a pending task
            // therefore locates this wait in its final drain, not its body or first read.
            phase = AuthHttpFinalDrainPhase.BoundaryAssert;
            AuthHttpFixture.Check(record.PriorObservation is { Kind: ServerSessionRequestKind.Authenticated, Identity: not null }
                && record.SessionReads == 2 && record.ActionCookieUsers.Count > 0 && record.SessionResult is null
                && !reader.IsCompleted && !admission.IsCompleted && !method.IsCompleted
                && record.Context is { Response.HasStarted: false } && !record.Context.Response.Headers.ContainsKey("Set-Cookie"),
                "The late-cancellation vector did not reach the independently owned final-drain boundary.");
            phase = AuthHttpFinalDrainPhase.CallerCancel;
            if (cancelCaller) record.CallerCancellation.Cancel();
            phase = AuthHttpFinalDrainPhase.CallerAssert;
            AuthHttpFixture.Check(!reader.IsCompleted && !admission.IsCompleted && !method.IsCompleted && !pending.IsCompleted
                && !gate.CancellationObserved.Task.IsCompleted && !gate.Release.Task.IsCompleted
                && !record.PriorAdmissionCancellation.IsCancellationRequested,
                "Caller cancellation released or canceled independently owned browser work during final drain.");
            // Keep the caller-only case independent of lifetime cancellation.
            if (!cancelCaller)
            {
                phase = AuthHttpFinalDrainPhase.DisposalBegin;
                disposal = test.First.BeginModuleDisposal();
                phase = AuthHttpFinalDrainPhase.DisposalAssert;
                AuthHttpFixture.Check(!reader.IsCompleted && !admission.IsCompleted && !method.IsCompleted && !disposal.IsCompleted
                    && !gate.CancellationObserved.Task.IsCompleted && !gate.Release.Task.IsCompleted
                    && !record.PriorAdmissionCancellation.IsCancellationRequested,
                    "Module disposal released its lease or canceled independently owned browser work during final drain.");
            }
            phase = AuthHttpFinalDrainPhase.AdmissionJoin;
            gate.Release.TrySetResult(); await admission.WaitAsync(TimeSpan.FromSeconds(5));
            phase = AuthHttpFinalDrainPhase.ResponseJoin;
            var reply = await response;
            phase = AuthHttpFinalDrainPhase.DisposalBegin;
            disposal ??= test.First.BeginModuleDisposal();
            phase = AuthHttpFinalDrainPhase.DisposalJoin;
            await disposal.WaitAsync(TimeSpan.FromSeconds(10));
            phase = AuthHttpFinalDrainPhase.FinalAssert;
            AuthHttpFixture.Check(reader.IsCompletedSuccessfully && admission.IsCompletedSuccessfully
                && admission.Result is { Kind: ServerSessionRequestKind.Authenticated, Identity: not null }
                && method.IsCanceled && record.Canceled && record.SessionResult is null && record.ClosedError is null
                && record.AuthenticationFailure is null && record.Failure is null && (int)reply.Status == 499
                && reply.Body.Length == 0 && !reply.Headers.ContainsKey("Location") && !reply.Headers.ContainsKey("Set-Cookie")
                && record.Creates == 0 && record.Revokes == 0 && record.Renews == 0 && record.BodyReads == 0 && record.FormReads == 0
                && !gate.CancellationObserved.Task.IsCompleted && !record.PriorAdmissionCancellation.IsCancellationRequested
                && record.AdmissionAtRequestEnd == TaskStatus.RanToCompletion && record.Ended.Task.IsCompleted
                && record.CallerCancellation.IsCancellationRequested == cancelCaller,
                "Cancellation during a successful independent final drain published bootstrap success or cookie/token delivery.");
            phase = AuthHttpFinalDrainPhase.CookieAssert;
            AuthHttpFixture.NoLocalCookieEffect(reply);
        }
        catch (Exception error)
        {
            var diagnostic = new AuthHttpFailure("Final-drain phase=" + phase + "; kind=" + AuthHttpFixture.FailureKind(error)
                + "; ActualReader=" + AuthHttpFixture.TaskState(record?.ActualReaderOperation)
                + "; GatedReader=" + AuthHttpFixture.TaskState(record?.GatedReaderOperation)
                + "; PublicAdmission=" + AuthHttpFixture.TaskState(record?.PriorAdmissionOperation)
                + "; Module=" + AuthHttpFixture.TaskState(record?.ModuleOperation)
                + "; HttpSend=" + AuthHttpFixture.TaskState(pending) + "; disposal=" + AuthHttpFixture.TaskState(disposal)
                + "; ended=" + (record?.Ended.Task.IsCompleted ?? false)
                + "; requestCompleted=" + (record?.RequestCompleted.Task.IsCompleted ?? false)
                + "; admissionAtRequestEnd=" + (record?.AdmissionAtRequestEnd?.ToString() ?? "absent") + ".");
            if (error is AuthHttpFailure)
                throw new AggregateException("Final-drain assertion and closed diagnostics.", error, diagnostic);
            throw diagnostic;
        }
        finally { gate?.Release.TrySetResult(); }
    });

    [PostgresFact]
    public Task OriginalProviderTokenProcessCancelsBeforeGateReleaseAndNeverPublishesBinding() => AuthHttpFixture.RunAsync(postgres, async test =>
    {
        var flow = await test.FlowAsync(); var record = test.NewRecord(); record.Direct = AuthHttpDirect.Complete; record.CallerCancellation = new();
        var gate = new EntraProtocolGate(); test.Issuer.TokenGate = gate; test.Issuer.NextTokenOperation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = test.SendAsync(flow.Callback, flow.Jar, record: record); _ = test.Track(pending);
        try
        {
            await Task.WhenAll(gate.Entered.Task, test.Issuer.NextTokenOperation.Task, record.ModuleStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));
            var original = test.Issuer.NextTokenOperation.Task.Result; var method = record.ModuleOperation!;
            record.CallerCancellation.Cancel();
            try { await Task.WhenAll(original, method, record.Ended.Task).WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
            AuthHttpFixture.Check(original.IsCanceled && method.IsCanceled && record.Canceled && record.Resolves == 0 && record.Creates == 0 && Volatile.Read(ref gate.Canceled) == 1, "Provider cancellation used a signal/proxy instead of settling the original token operation before binding.");
            var reply = await pending; AuthHttpFixture.NoLocalCookieEffect(reply); AuthHttpFixture.Check(!reply.Headers.ContainsKey("Location"), "Provider cancellation became a successful callback redirect.");
            await test.First.BeginModuleDisposal().WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { gate.Release.TrySetResult(); }
    });

    [PostgresFact]
    public Task RealClientDisconnectSettlesTheExactOriginalReaderBeforeAnySuccess() => AuthHttpFixture.RunAsync(postgres, async test =>
    {
        var jar = await test.LoginAsync(); var record = test.NewRecord(); record.ReaderGate = new(); using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = test.SendAsync(AuthHttpFixture.StatusPath, jar, record: record, token: cancel.Token); _ = test.Track(pending, cancellation: true);
        try
        {
            await Task.WhenAll(record.ReaderGate.Entered.Task, record.ReaderGate.Original.Task).WaitAsync(TimeSpan.FromSeconds(5)); var original = record.ReaderGate.Original.Task.Result;
            cancel.Cancel(); bool canceled = false; try { await pending; } catch (OperationCanceledException) { canceled = true; }
            try { await Task.WhenAll(record.Ended.Task, original).WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
            AuthHttpFixture.Check(canceled && original.IsCanceled && record.Canceled && record.Creates == 0 && record.Revokes == 0 && record.Renews == 0 && record.BodyReads == 0, "Actual disconnect did not settle its original auth observer before protected work.");
        }
        finally { record.ReleaseAll(); }
    });
}
