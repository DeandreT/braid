using System.Net;
using System.Runtime.Versioning;
using Braid.Application;
using Braid.Domain;
using H = Braid.Tests.ChatHttpTestSupport;

namespace Braid.Tests;

/// <summary>Actual HTTP and PostgreSQL admission checks that precede any report, ledger or provider work.</summary>
[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class ChatHttpBoundaryTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task Unsafe_requests_refuse_csrf_and_malformed_input_before_authority_or_provider_work()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        await using var provider = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("text/event-stream", [], ct));
        var revision = new RevisionScope(seed.OrganizationA, seed.ProjectA, Guid.NewGuid(), Guid.NewGuid());
        await using var api = await H.StartAsync(test, new ActorContext(new PrincipalId(seed.Alice)), revision, H.Options(provider));
        var token = await api.TokenAsync();
        var id = Guid.NewGuid();
        using (var missing = await api.PostAsync(api.ConversationsPath, H.Conversation(id), null))
            await H.ErrorAsync(missing, HttpStatusCode.Forbidden, "identityRejected");
        using (var foreign = await api.PostAsync(api.ConversationsPath, H.Conversation(id), token, origin: "http://127.0.0.1:1"))
            await H.ErrorAsync(foreign, HttpStatusCode.Forbidden, "identityRejected");
        using (var turnCsrf = await api.PostAsync(api.TurnsPath(id), H.Turn(Guid.NewGuid(), "question"), null))
            await H.ErrorAsync(turnCsrf, HttpStatusCode.Forbidden, "identityRejected");
        using (var cancelCsrf = await api.PostAsync(api.TurnPath(id, Guid.NewGuid()) + "/cancel", null, null))
            await H.ErrorAsync(cancelCsrf, HttpStatusCode.Forbidden, "identityRejected");

        foreach (var (body, type, status, code) in new[]
        {
            ("{\"conversationId\":\"" + id.ToString("D") + "\",\"extra\":1}", "application/json", HttpStatusCode.BadRequest, "invalidRequest"),
            ("{\"conversationId\":\"" + id.ToString("D").ToUpperInvariant() + "\"}", "application/json", HttpStatusCode.BadRequest, "invalidRequest"),
            ("[\"" + id.ToString("D") + "\"]", "application/json", HttpStatusCode.BadRequest, "invalidRequest"),
            (H.Conversation(id), "text/plain", HttpStatusCode.UnsupportedMediaType, "unsupportedMediaType"),
            (H.Conversation(id), "application/json; charset=latin1", HttpStatusCode.UnsupportedMediaType, "unsupportedMediaType"),
            ("{\"conversationId\":\"" + new string('a', 2048) + "\"}", "application/json", HttpStatusCode.RequestEntityTooLarge, "bodyLimit")
        })
        {
            using var refused = await api.PostAsync(api.ConversationsPath, body, token, contentType: type);
            await H.ErrorAsync(refused, status, code);
        }
        foreach (var body in new[]
        {
            H.Turn(Guid.NewGuid(), "   "),
            H.Turn(Guid.NewGuid(), "nul\0text"),
            H.Turn(Guid.NewGuid(), new string('x', 32_769)),
            H.Turn(Guid.NewGuid(), "question").Replace(",\"selectors\":[]", "", StringComparison.Ordinal),
            H.Turn(Guid.NewGuid(), "question", selectors: "[{\"kind\":\"field\",\"resourceId\":null,\"diagnosticOrdinal\":null}]"),
            H.Turn(Guid.NewGuid(), "question", selectors: "[{\"kind\":\"field\",\"resourceId\":\"f\",\"diagnosticOrdinal\":null,\"x\":1}]"),
            H.Turn(Guid.NewGuid(), "question", selectors: "[{\"kind\":\"field\",\"resourceId\":\"f\",\"diagnosticOrdinal\":null},{\"kind\":\"field\",\"resourceId\":\"f\",\"diagnosticOrdinal\":null}]"),
            H.Turn(Guid.NewGuid(), "question", selectors: "[{\"kind\":\"validationDiagnostic\",\"resourceId\":null,\"diagnosticOrdinal\":-1}]"),
            H.Turn(Guid.NewGuid(), "question").Replace("\"profileVersion\":1", "\"profileVersion\":1.0", StringComparison.Ordinal),
            H.Turn(Guid.NewGuid(), "question").Replace("\"profileVersion\":1", "\"profileVersion\":0", StringComparison.Ordinal)
        })
        {
            using var refused = await api.PostAsync(api.TurnsPath(id), body, token);
            await H.ErrorAsync(refused, HttpStatusCode.BadRequest, "invalidRequest");
        }
        using (var cancelBody = await api.PostAsync(api.TurnPath(id, Guid.NewGuid()) + "/cancel", "{}", token))
            await H.ErrorAsync(cancelBody, HttpStatusCode.BadRequest, "invalidRequest");
        foreach (var query in new[] { "?limit=0", "?limit=101", "?limit=01", "?cursor=x", "?limit=1&limit=2" })
        {
            using var refused = await api.Client.GetAsync(api.ConversationsPath + query);
            await H.ErrorAsync(refused, HttpStatusCode.BadRequest, "invalidRequest");
        }
        using (var after = await api.Client.GetAsync(api.TurnPath(id, Guid.NewGuid()) + "/events?after=4097"))
            await H.ErrorAsync(after, HttpStatusCode.BadRequest, "invalidRequest");

        // Well-formed requests for an unreadable revision disclose nothing.
        using (var availability = await api.Client.GetAsync(api.ChatPath))
            await H.ErrorAsync(availability, HttpStatusCode.NotFound, "notFound");
        using (var list = await api.Client.GetAsync(api.ConversationsPath))
            await H.ErrorAsync(list, HttpStatusCode.NotFound, "notFound");
        using (var create = await api.PostAsync(api.ConversationsPath, H.Conversation(id), token))
            await H.ErrorAsync(create, HttpStatusCode.NotFound, "notFound");
        using (var turn = await api.PostAsync(api.TurnsPath(id), H.Turn(Guid.NewGuid(), "question"), token))
            await H.ErrorAsync(turn, HttpStatusCode.NotFound, "notFound");
        using (var events = await api.Client.GetAsync(api.TurnPath(id, Guid.NewGuid()) + "/events"))
            await H.ErrorAsync(events, HttpStatusCode.NotFound, "notFound");
        using (var cancel = await api.PostAsync(api.TurnPath(id, Guid.NewGuid()) + "/cancel", null, token))
            await H.ErrorAsync(cancel, HttpStatusCode.NotFound, "notFound");
        var foreignOrganization = api.ChatPath.Replace(seed.OrganizationA.ToString("D"), seed.OrganizationB.ToString("D"), StringComparison.Ordinal);
        using (var other = await api.Client.GetAsync(foreignOrganization))
            await H.ErrorAsync(other, HttpStatusCode.NotFound, "notFound");
        Assert.Equal(0, provider.RequestCount);
        Assert.Equal(0L, await test.ScalarAsync<long>("SELECT count(*) FROM braid.chat_identities"));
    }

    [PostgresFact]
    public async Task Unconfigured_chat_is_unavailable_and_leaves_the_catalog_usable()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        var revision = new RevisionScope(seed.OrganizationA, seed.ProjectA, Guid.NewGuid(), Guid.NewGuid());
        await using var api = await H.StartAsync(test, new ActorContext(new PrincipalId(seed.Alice)), revision, new());
        using (var context = await api.Client.GetAsync("/api/v1/catalog/context"))
            Assert.False((await CatalogHttpTestSupport.JsonAsync(context)).GetProperty("features").GetProperty("chat").GetBoolean());
        var token = await api.TokenAsync();
        using (var availability = await api.Client.GetAsync(api.ChatPath))
            await H.ErrorAsync(availability, HttpStatusCode.NotFound, "unavailable");
        using (var create = await api.PostAsync(api.ConversationsPath, H.Conversation(Guid.NewGuid()), token))
            await H.ErrorAsync(create, HttpStatusCode.NotFound, "unavailable");
        using (var projects = await api.Client.GetAsync($"/api/v1/organizations/{seed.OrganizationA:D}/projects"))
            Assert.NotEmpty((await CatalogHttpTestSupport.JsonAsync(projects)).GetProperty("projects").EnumerateArray());
    }
}
