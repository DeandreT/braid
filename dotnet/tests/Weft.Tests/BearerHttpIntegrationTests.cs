using System.Globalization;
using System.Net;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Weft.Application;
using Weft.Domain;

namespace Weft.Tests;

/// <summary>
/// Bearer requests on the real catalog routes beside real browser sessions: the owned HTTPS issuer signs delegated
/// tokens, restricted PostgreSQL resolves bindings and records audits, and authority is the same fresh catalog check.
/// </summary>
[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class BearerHttpIntegrationTests(PostgresFixture postgres)
{
    private const string Context = AuthHttpFixture.ContextPath;

    [PostgresFact]
    public Task Browser_sign_in_and_a_bearer_token_resolve_the_same_principal_and_the_same_fresh_authority() => AuthHttpFixture.RunAsync(postgres, async test =>
    {
        var jar = await test.LoginAsync();
        var token = Token(test);
        // The same person: the browser's ID token and this access token share tid and oid, not sub or client.
        var session = await test.SendAsync(Context, jar);
        var bearer = await BearerAsync(test, Context, token);
        AuthHttpFixture.Check(session.Status == HttpStatusCode.OK && bearer.Status == HttpStatusCode.OK, "A context read was refused.");
        var cookieContext = session.Json(); var bearerContext = bearer.Json();
        AuthHttpFixture.Check(bearerContext.GetProperty("mode").GetString() == "bearer" && cookieContext.GetProperty("mode").GetString() == "session"
            && !bearerContext.TryGetProperty("requestToken", out _) && !bearerContext.TryGetProperty("identityContext", out _)
            && bearerContext.GetProperty("actorPrincipalId").GetString() == test.Data.Actor.PrincipalId.Value.ToString("D")
            && new[] { "actorPrincipalId", "allowedOrganizationId", "organizationName", "features", "organizationActions" }.All(name =>
                bearerContext.GetProperty(name).GetRawText() == cookieContext.GetProperty(name).GetRawText()),
            "Bearer context differed from the session context for the same person.");
        NoSession(bearer); AuthHttpFixture.Check(bearer.Record.Resolves == 1 && bearer.Record.CatalogReads > 0, "Bearer context skipped fresh identity or authority.");
        // Representative reads: the same fresh authority answers both credentials identically.
        foreach (var path in new[] { Projects(test), Mapping(test), Mapping(test) + "/metadata", "/api/v1/help/sections", "/api/v1/help/sections/overview" })
        {
            var byCookie = await test.SendAsync(path, jar); var byBearer = await BearerAsync(test, path, token);
            AuthHttpFixture.Check(byCookie.Status == HttpStatusCode.OK && byBearer.Status == HttpStatusCode.OK
                && Encoding.UTF8.GetString(byCookie.Body) == Encoding.UTF8.GetString(byBearer.Body)
                && byBearer.Record.CatalogReads > 0, "A bearer read differed from the same principal's session read.");
            NoSession(byBearer);
        }
        // A mutation needs no antiforgery with a bearer token, runs exactly once and is audited before it runs.
        var status = await test.StatusAsync(jar);
        var cookieEdit = await EditAsync(test, "Session edit", jar: jar, epoch: status.Epoch);
        AuthHttpFixture.Check(cookieEdit.Status == HttpStatusCode.OK && cookieEdit.Record.MetadataWrites == 1 && cookieEdit.Record.Audits == 0,
            "The session edit did not run exactly once without an API audit.");
        var bearerEdit = await EditAsync(test, "Bearer edit", token: token);
        AuthHttpFixture.Check(bearerEdit.Status == HttpStatusCode.OK && bearerEdit.Record.MetadataWrites == 1 && bearerEdit.Record.Audits == 1
            && bearerEdit.Json().GetProperty("name").GetString() == "Bearer edit", "The bearer edit did not run exactly once after its audit.");
        NoSession(bearerEdit);
        var audit = await AuditsAsync(test);
        AuthHttpFixture.Check(audit.GetArrayLength() == 1, "An API audit row was missing or duplicated.");
        var row = audit[0];
        AuthHttpFixture.Check(row.GetProperty("principal_id").GetString() == test.Data.Actor.PrincipalId.Value.ToString("D")
            && row.GetProperty("binding_id").GetString() == test.Binding.BindingId.ToString("D")
            && row.GetProperty("client_application_id").GetString() == UserTokenValidationTests.Cli.ToString("D")
            && row.GetProperty("operation").GetString() == "EditCatalogMappingMetadata" && row.GetProperty("method").GetString() == "PATCH"
            && string.Join(',', row.GetProperty("resource_names").EnumerateArray().Select(item => item.GetString())) == "mappingId,organizationId,projectId"
            && string.Join(',', row.GetProperty("resource_values").EnumerateArray().Select(item => item.GetString())) ==
                string.Join(',', test.Data.Scope.MappingId.ToString("D"), test.Data.Scope.OrganizationId.ToString("D"), test.Data.Scope.ProjectId.ToString("D")),
            "The audit row did not record exactly the principal, binding, client, operation and route resources.");
        AuthHttpFixture.Check(!audit.GetRawText().Contains(token, StringComparison.Ordinal) && !audit.GetRawText().Contains("Bearer edit", StringComparison.Ordinal),
            "The audit row retained the token or the request payload.");
        // Another client application of the allowlist, with another pairwise sub, is the same principal.
        var other = Token(test, claims => { claims["azp"] = UserTokenValidationTests.SecondCli.ToString("D"); claims["sub"] = "another-pairwise-subject"; });
        AuthHttpFixture.Check((await BearerAsync(test, Context, other)).Json().GetProperty("actorPrincipalId").GetString() ==
            test.Data.Actor.PrincipalId.Value.ToString("D"), "Another allowlisted client resolved a different principal.");
    }, bearerBurst: 100);

    [PostgresFact]
    public Task A_principal_without_project_authority_is_answered_the_same_by_its_session_and_its_bearer_token() => AuthHttpFixture.RunAsync(postgres, async test =>
    {
        // Admitted to the organization but holding no project role.
        var bound = await test.Owner.BindAsync(EntraProtocolFixturePolicy.UserKey, new PrincipalId(test.Data.Seed.Admitted));
        AuthHttpFixture.Check(bound.Outcome == ExternalIdentityBindingOwnerOutcome.Created, "The outsider binding was not created.");
        var jar = await test.LoginAsync(); var token = Token(test);
        foreach (var path in new[] { Mapping(test), Mapping(test) + "/metadata" })
        {
            var byCookie = await test.SendAsync(path, jar); var byBearer = await BearerAsync(test, path, token);
            Closed(byCookie, HttpStatusCode.NotFound, "notFound"); Closed(byBearer, HttpStatusCode.NotFound, "notFound");
            AuthHttpFixture.Check(Encoding.UTF8.GetString(byCookie.Body) == Encoding.UTF8.GetString(byBearer.Body), "The hidden answers differed.");
        }
        var projects = await BearerAsync(test, Projects(test), token);
        AuthHttpFixture.Check(projects.Status == HttpStatusCode.OK && projects.Json().GetProperty("projects").GetArrayLength() ==
            (await test.SendAsync(Projects(test), jar)).Json().GetProperty("projects").GetArrayLength(), "Bearer and session saw different projects.");
        // The audit records the admitted attempt; the fresh catalog authority then hides the mapping.
        var edit = await EditAsync(test, "Outsider edit", token: token, tag: "\"0\"");
        Closed(edit, HttpStatusCode.NotFound, "notFound");
        AuthHttpFixture.Check(edit.Record.Audits == 1, "An admitted bearer mutation attempt was not audited.");
        AuthHttpFixture.Check(await test.Data.HeaderAsync() is { } header && header.Name != "Outsider edit", "An unauthorized bearer edit was stored.");
    }, bindIssuer: false, bearerBurst: 100);

    [PostgresFact]
    public Task Every_bearer_refusal_is_closed_redacted_and_reaches_no_business() => AuthHttpFixture.RunAsync(postgres, async test =>
    {
        var valid = Token(test);
        Assert.Equal(HttpStatusCode.OK, (await BearerAsync(test, Context, valid)).Status);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var invalidTokens = new Dictionary<string, string>
        {
            ["malformed"] = "not-a-jwt",
            ["expired"] = Token(test, claims => { claims["exp"] = now - 1; claims["nbf"] = now - 600; claims["iat"] = now - 600; }),
            ["wrongAudience"] = Token(test, claims => claims["aud"] = Guid.NewGuid().ToString("D")),
            ["wrongTenant"] = Token(test, claims => claims["tid"] = Guid.NewGuid().ToString("D")),
            ["appOnly"] = test.Crypto.SignClaims(MachineTokenValidationTests.Claims(EntraProtocolFixturePolicy.ObjectId, UserTokenValidationTests.Cli).ToJsonString()),
            ["missingScope"] = Token(test, claims => claims.Remove("scp")),
            ["otherScope"] = Token(test, claims => claims["scp"] = "other.scope"),
            ["unknownClient"] = Token(test, claims => claims["azp"] = Guid.NewGuid().ToString("D")),
            ["badSignature"] = Token(test, signing: EntraProtocolTokenCase.BadSignature),
            ["unsigned"] = Token(test, signing: EntraProtocolTokenCase.Unsigned)
        };
        foreach (var (name, token) in invalidTokens)
        {
            var reply = await BearerAsync(test, Mapping(test) + "/metadata", token);
            Refused(test, reply, HttpStatusCode.Unauthorized, "identityRejected", "Bearer error=\"invalid_token\"", token, name);
            AuthHttpFixture.Check(reply.Record.Resolves == 0, "A refused token reached binding resolution: " + name);
        }
        foreach (var (name, header) in new[] { ("basic", "Basic " + valid), ("empty", "Bearer "), ("bare", "Bearer"), ("scheme", valid) })
        {
            var reply = await test.SendAsync(Mapping(test), mutate: message => message.Headers.TryAddWithoutValidation("Authorization", header));
            Refused(test, reply, HttpStatusCode.BadRequest, "invalidRequest", "Bearer error=\"invalid_request\"", valid, name);
            AuthHttpFixture.Check(reply.Record.Resolves == 0, "A malformed Authorization header reached binding resolution: " + name);
        }
        // Never mixed with cookies: a signed-in browser's cookies, or any cookie at all.
        var jar = await test.LoginAsync();
        var mixed = await BearerAsync(test, Context, valid, jar: jar);
        Refused(test, mixed, HttpStatusCode.BadRequest, "invalidRequest", "Bearer error=\"invalid_request\"", valid, "mixed");
        var cookie = await BearerAsync(test, Context, valid, mutate: message => message.Headers.TryAddWithoutValidation("Cookie", "unrelated=1"));
        Refused(test, cookie, HttpStatusCode.BadRequest, "invalidRequest", "Bearer error=\"invalid_request\"", valid, "cookie");
        // A valid token whose (tid, oid) has no binding is forbidden after one fresh read.
        var unbound = Token(test, claims => claims["oid"] = Guid.NewGuid().ToString("D"));
        var forbidden = await BearerAsync(test, Context, unbound);
        Refused(test, forbidden, HttpStatusCode.Forbidden, "identityRejected", null, unbound, "unbound");
        AuthHttpFixture.Check(forbidden.Record.Resolves == 1, "The unbound token was not resolved freshly.");
        // Authentication routes never accept a bearer token, enabled or not.
        var status = await test.SendAsync(AuthHttpFixture.StatusPath, mutate: message => message.Headers.TryAddWithoutValidation("Authorization", "Bearer " + valid));
        AuthHttpFixture.Error(status, HttpStatusCode.BadRequest, "invalidRequest");
        AuthHttpFixture.Check(!Encoding.UTF8.GetString(status.Body).Contains(valid, StringComparison.Ordinal), "An auth route echoed the token.");
    }, bearerBurst: 100);

    [PostgresFact]
    public Task Disabling_the_binding_or_the_principal_refuses_the_next_bearer_request() => AuthHttpFixture.RunAsync(postgres, async test =>
    {
        var token = Token(test);
        Assert.Equal(HttpStatusCode.OK, (await BearerAsync(test, Mapping(test), token)).Status);
        await test.Data.Case.ExecuteAsync("UPDATE weft.principals SET active=false WHERE id=$1", test.Data.Actor.PrincipalId.Value);
        Refused(test, await BearerAsync(test, Mapping(test), token), HttpStatusCode.Forbidden, "identityRejected", null, token, "inactive");
        await test.Data.Case.ExecuteAsync("UPDATE weft.principals SET active=true WHERE id=$1", test.Data.Actor.PrincipalId.Value);
        Assert.Equal(HttpStatusCode.OK, (await BearerAsync(test, Mapping(test), token)).Status);
        var disabled = await test.Owner.DisableAsync(test.Binding.Key, test.Data.Actor.PrincipalId, test.Binding.Version);
        AuthHttpFixture.Check(disabled.Outcome == ExternalIdentityBindingOwnerOutcome.Disabled, "The binding was not disabled.");
        Refused(test, await BearerAsync(test, Mapping(test), token), HttpStatusCode.Forbidden, "identityRejected", null, token, "disabled");
    }, bearerBurst: 100);

    [PostgresFact]
    public Task A_mutation_runs_only_after_its_audit_is_durable_and_the_identity_is_still_current() => AuthHttpFixture.RunAsync(postgres, async test =>
    {
        var token = Token(test);
        var unavailable = test.NewRecord(); unavailable.AuditUnavailable = true;
        var refused = await EditAsync(test, "Unaudited edit", token: token, record: unavailable);
        Closed(refused, HttpStatusCode.ServiceUnavailable, "unavailable");
        AuthHttpFixture.Check(refused.Record.Audits == 1 && refused.Record.MetadataWrites == 0 && refused.Record.BodyReads == 0,
            "A mutation ran or read its body without a durable audit.");
        var changed = test.NewRecord(); changed.AuditIdentityChanged = true;
        var forbidden = await EditAsync(test, "Changed identity edit", token: token, record: changed);
        Closed(forbidden, HttpStatusCode.Forbidden, "identityRejected");
        AuthHttpFixture.Check(forbidden.Record.MetadataWrites == 0 && forbidden.Record.BodyReads == 0, "A mutation ran after its identity changed.");
        AuthHttpFixture.Check((await AuditsAsync(test)).GetArrayLength() == 0 && (await test.Data.HeaderAsync()).Name is not ("Unaudited edit" or "Changed identity edit"),
            "A refused mutation left an audit row or stored metadata.");
        // Reads are not audited.
        var read = await BearerAsync(test, Mapping(test), token);
        AuthHttpFixture.Check(read.Status == HttpStatusCode.OK && read.Record.Audits == 0, "A read was audited.");
    }, bearerBurst: 100);

    [PostgresFact]
    public Task Each_principal_has_its_own_bounded_rate_with_a_fixed_429_answer() => AuthHttpFixture.RunAsync(postgres, async test =>
    {
        var token = Token(test);
        for (var request = 0; request < 3; request++)
            Assert.Equal(HttpStatusCode.OK, (await BearerAsync(test, Context, token)).Status);
        var limited = await BearerAsync(test, Context, token);
        Refused(test, limited, HttpStatusCode.TooManyRequests, "rateLimited", null, token, "limited");
        AuthHttpFixture.Check(limited.Headers.TryGetValue("Retry-After", out var after) && after.Length == 1
            && long.TryParse(after[0], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds >= 1
            && after[0] == seconds.ToString(CultureInfo.InvariantCulture), "The 429 answer had no whole-second Retry-After.");
        // A mutation over the limit is refused before its audit.
        var edit = await EditAsync(test, "Limited edit", token: token);
        Closed(edit, HttpStatusCode.TooManyRequests, "rateLimited");
        AuthHttpFixture.Check(edit.Record.Audits == 0 && edit.Record.MetadataWrites == 0, "A limited mutation reached its audit or writer.");
        // Another principal keeps its own allowance; the browser session is not rate limited.
        var other = await test.Owner.BindAsync(EntraUserIdentity.Key(EntraProtocolFixturePolicy.Tenant, Guid.NewGuid()), new PrincipalId(test.Data.Seed.Admitted));
        var otherToken = Token(test, claims => claims["oid"] = other.Binding!.Key.Subject);
        Assert.Equal(HttpStatusCode.OK, (await BearerAsync(test, Context, otherToken)).Status);
        var jar = await test.LoginAsync();
        Assert.Equal(HttpStatusCode.OK, (await test.SendAsync(Context, jar)).Status);
    }, bearerBurst: 3);

    [PostgresFact]
    public Task Unavailable_signing_metadata_is_unavailable_not_a_denial() => AuthHttpFixture.RunAsync(postgres, async test =>
    {
        test.Issuer.Metadata = EntraProtocolMetadataCase.Redirect;
        var token = Token(test);
        var reply = await BearerAsync(test, Context, token);
        Refused(test, reply, HttpStatusCode.ServiceUnavailable, "unavailable", null, token, "unavailable");
        AuthHttpFixture.Check(reply.Record.Resolves == 0, "An unvalidated token reached binding resolution.");
    }, bearerBurst: 100);

    [PostgresFact]
    public Task Chat_routes_admit_a_bearer_token_like_the_session_and_keep_the_chat_error_shape() => AuthHttpFixture.RunAsync(postgres, async test =>
    {
        var token = Token(test); var jar = await test.LoginAsync();
        // A revision the person cannot read: the same fresh catalog authority hides it from both credentials.
        var chat = Mapping(test) + $"/revisions/{Guid.NewGuid():D}/chat";
        var conversation = Guid.NewGuid();
        foreach (var path in new[] { chat, chat + "/conversations?limit=10", chat + $"/conversations/{conversation:D}/turns/{Guid.NewGuid():D}/events?after=0" })
        {
            var byCookie = await test.SendAsync(path, jar); var byBearer = await BearerAsync(test, path, token);
            Closed(byCookie, HttpStatusCode.NotFound, "notFound"); Closed(byBearer, HttpStatusCode.NotFound, "notFound");
            AuthHttpFixture.Check(Encoding.UTF8.GetString(byCookie.Body) == Encoding.UTF8.GetString(byBearer.Body)
                && byBearer.Record.Resolves == 1 && byBearer.Record.CatalogReads > 0, "A bearer chat read skipped fresh identity or authority.");
            NoSession(byBearer);
        }
        // A chat mutation needs no antiforgery with a bearer token and is audited before the handler checks authority.
        var create = await BearerAsync(test, chat + "/conversations", token, "POST", mutate: message =>
        {
            message.Content = new StringContent("{\"conversationId\":\"" + conversation.ToString("D") + "\"}", Encoding.UTF8);
            message.Content.Headers.ContentType = new("application/json");
        });
        Closed(create, HttpStatusCode.NotFound, "notFound");
        var audit = await AuditsAsync(test);
        AuthHttpFixture.Check(create.Record.Audits == 1 && audit.GetArrayLength() == 1
            && audit[0].GetProperty("operation").GetString() == "CreateChatConversation" && audit[0].GetProperty("method").GetString() == "POST"
            && string.Join(',', audit[0].GetProperty("resource_names").EnumerateArray().Select(item => item.GetString())) ==
                "mappingId,organizationId,projectId,revisionId", "The bearer chat mutation was not audited exactly once before it ran.");
        // Refusals answer in the chat error shape with the same bearer challenges.
        var malformed = await test.SendAsync(chat, mutate: message => message.Headers.TryAddWithoutValidation("Authorization", "Basic " + token));
        Refused(test, malformed, HttpStatusCode.BadRequest, "invalidRequest", "Bearer error=\"invalid_request\"", token, "chatMalformed");
        var foreign = Token(test, claims => claims["azp"] = Guid.NewGuid().ToString("D"));
        Refused(test, await BearerAsync(test, chat, foreign), HttpStatusCode.Unauthorized, "identityRejected", "Bearer error=\"invalid_token\"", foreign, "chatInvalid");
        Refused(test, await BearerAsync(test, chat, token, jar: jar), HttpStatusCode.BadRequest, "invalidRequest", "Bearer error=\"invalid_request\"", token, "chatMixed");
        // The burst of five is spent by the four admitted requests above and one more; the next is rate limited.
        Closed(await BearerAsync(test, chat, token), HttpStatusCode.NotFound, "notFound");
        var limited = await BearerAsync(test, chat + $"/conversations/{conversation:D}/turns/{Guid.NewGuid():D}/events", token);
        Refused(test, limited, HttpStatusCode.TooManyRequests, "rateLimited", null, token, "chatLimited");
        AuthHttpFixture.Check(limited.Headers.TryGetValue("Retry-After", out var after) && after.Length == 1, "The chat 429 had no Retry-After.");
    }, bearerBurst: 5, chat: true);

    [PostgresFact]
    public Task Without_bearer_access_an_Authorization_header_never_authenticates_or_joins_a_session() => AuthHttpFixture.RunAsync(postgres, async test =>
    {
        var token = Token(test);
        var alone = await BearerAsync(test, Context, token);
        Refused(test, alone, HttpStatusCode.Unauthorized, "identityRejected", null, token, "disabled");
        AuthHttpFixture.Check(!alone.Headers.ContainsKey("WWW-Authenticate") && alone.Record.Resolves == 0, "Disabled bearer access advertised or resolved a token.");
        var jar = await test.LoginAsync();
        Assert.Equal(HttpStatusCode.OK, (await test.SendAsync(Context, jar)).Status);
        var mixed = await BearerAsync(test, Context, token, jar: jar);
        Refused(test, mixed, HttpStatusCode.Unauthorized, "identityRejected", null, token, "disabledMixed");
        AuthHttpFixture.Check(mixed.Record.SessionReads == 0, "A refused Authorization header still read the session.");
    });

    private static string Token(AuthHttpFixture test, Action<JsonObject>? change = null, EntraProtocolTokenCase signing = EntraProtocolTokenCase.Good)
    {
        var claims = UserTokenValidationTests.Claims();
        change?.Invoke(claims);
        return test.Crypto.SignClaims(claims.ToJsonString(), signing);
    }

    private static Task<AuthHttpReply> BearerAsync(AuthHttpFixture test, string path, string token, string method = "GET",
        AuthHttpJar? jar = null, Action<HttpRequestMessage>? mutate = null, AuthHttpRecord? record = null) =>
        test.SendAsync(path, jar, method, record: record, mutate: message =>
        {
            message.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            mutate?.Invoke(message);
        });

    private static async Task<AuthHttpReply> EditAsync(AuthHttpFixture test, string name, string? token = null, AuthHttpJar? jar = null,
        string? epoch = null, string? tag = null, AuthHttpRecord? record = null)
    {
        tag ??= (await (token is null ? test.SendAsync(Mapping(test) + "/metadata", jar) : BearerAsync(test, Mapping(test) + "/metadata", token)))
            .Headers.TryGetValue("ETag", out var etag) ? etag[0] : "\"0\"";
        return await test.SendAsync(Mapping(test) + "/metadata", jar, "PATCH", epoch: epoch, record: record, mutate: message =>
        {
            if (token is not null) message.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            if (jar is not null) message.Headers.TryAddWithoutValidation("Origin", test.Origin.GetLeftPart(UriPartial.Authority));
            message.Headers.TryAddWithoutValidation("If-Match", tag);
            message.Content = new ByteArrayContent(MetadataHttpTestSupport.Edit(name, "Owned edit"));
            message.Content.Headers.ContentType = new(MetadataHttpTestSupport.MediaType);
        });
    }

    private static void Closed(AuthHttpReply reply, HttpStatusCode status, string code) =>
        AuthHttpFixture.Check(reply.Status == status && reply.Json().GetProperty("code").GetString() == code && !reply.Headers.ContainsKey("ETag"),
            $"A closed catalog answer changed: expected {(int)status} {code}, actual {(int)reply.Status}.");

    private static void NoSession(AuthHttpReply reply) =>
        AuthHttpFixture.Check(reply.Record.SessionReads == 0 && reply.Record.Creates == 0 && reply.Record.Renews == 0 && reply.Record.Revokes == 0
            && !reply.Headers.ContainsKey("Set-Cookie"), "A bearer request read, created or renewed a session or set a cookie.");

    private static void Refused(AuthHttpFixture test, AuthHttpReply reply, HttpStatusCode status, string code, string? challenge, string token, string name)
    {
        var json = reply.Json();
        AuthHttpFixture.Check(reply.Status == status && json.GetProperty("code").GetString() == code, $"Bearer refusal {name} changed: {(int)reply.Status}.");
        AuthHttpFixture.Check(challenge is null ? !reply.Headers.ContainsKey("WWW-Authenticate")
            : reply.Headers.TryGetValue("WWW-Authenticate", out var values) && values.SequenceEqual([challenge]), "Bearer challenge changed: " + name);
        AuthHttpFixture.Check(reply.Record.CatalogReads == 0 && reply.Record.MetadataWrites == 0 && reply.Record.BodyReads == 0
            && reply.Record.Audits == 0 && reply.Record.Creates == 0 && reply.Record.Renews == 0 && !reply.Headers.ContainsKey("Set-Cookie"),
            "A refused bearer request reached business, audit or session work: " + name);
        var text = Encoding.UTF8.GetString(reply.Body) + string.Join('\n', reply.Headers.SelectMany(pair => pair.Value));
        AuthHttpFixture.Check(!text.Contains(token, StringComparison.Ordinal) && test.Issuer.PrivateOperands().All(value => !text.Contains(value, StringComparison.Ordinal))
            && !text.Contains(EntraProtocolFixturePolicy.ObjectId.ToString("D"), StringComparison.Ordinal), "A bearer refusal exposed the token or a claim: " + name);
    }

    private static Task<JsonElement> AuditsAsync(AuthHttpFixture test) => test.Data.Case.ScalarAsync<string>("""
        SELECT coalesce(jsonb_agg(to_jsonb(a) ORDER BY a.recorded_at,a.audit_id),'[]'::jsonb)::text FROM weft.api_request_audit a
        """).ContinueWith(task => JsonDocument.Parse(task.Result).RootElement.Clone(), TaskScheduler.Default);

    private static string Projects(AuthHttpFixture test) => $"/api/v1/organizations/{test.Data.Scope.OrganizationId:D}/projects";
    private static string Mapping(AuthHttpFixture test) =>
        $"/api/v1/organizations/{test.Data.Scope.OrganizationId:D}/projects/{test.Data.Scope.ProjectId:D}/mappings/{test.Data.Scope.MappingId:D}";
}
