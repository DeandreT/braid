using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Weft.Api;
using Weft.Api.Chat;

namespace Weft.Tests;

public sealed class WorkspaceChatOpenApiTests
{
    [Fact]
    public async Task Workspace_routes_declare_paging_resume_closed_requests_and_stream_variants()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddWeftApi();
        builder.Services.AddWeftChat(new());
        await using var app = builder.Build();
        app.MapWeftChat();
        app.MapOpenApi();
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");
        const string root = "/api/v1/organizations/{organizationId}/chat";
        var listing = paths.GetProperty(root + "/conversations").GetProperty("get");
        var queries = listing.GetProperty("parameters").EnumerateArray().Where(parameter => parameter.GetProperty("in").GetString() == "query")
            .ToDictionary(parameter => parameter.GetProperty("name").GetString()!);
        Assert.Equal(new[] { "after", "limit", "revisionId" }, queries.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(1, queries["limit"].GetProperty("schema").GetProperty("minimum").GetInt32());
        Assert.Equal(100, queries["limit"].GetProperty("schema").GetProperty("maximum").GetInt32());
        Assert.Equal("uuid", queries["after"].GetProperty("schema").GetProperty("format").GetString());
        var events = paths.GetProperty(root + "/conversations/{conversationId}/turns/{turnId}/events").GetProperty("get");
        var after = Assert.Single(events.GetProperty("parameters").EnumerateArray(), parameter => parameter.GetProperty("in").GetString() == "query");
        Assert.Equal("after", after.GetProperty("name").GetString());
        Assert.Equal(4096, after.GetProperty("schema").GetProperty("maximum").GetInt32());
        Assert.True(events.GetProperty("responses").GetProperty("200").GetProperty("content").TryGetProperty("text/event-stream", out _));
        Assert.True(events.GetProperty("responses").GetProperty("200").GetProperty("headers").TryGetProperty("X-Accel-Buffering", out _));
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        const string receiptPath = root + "/conversations/{conversationId}/turns/{turnId}/mapping-writes";
        Assert.True(paths.GetProperty(receiptPath).TryGetProperty("get", out _));
        Assert.True(paths.GetProperty(receiptPath + "/recover").TryGetProperty("post", out _));
        const string proposalsPath = root + "/conversations/{conversationId}/turns/{turnId}/mapping-edit-proposals";
        Assert.True(paths.GetProperty(proposalsPath).TryGetProperty("get", out _));
        Assert.True(paths.GetProperty(proposalsPath + "/{proposalId}/approve").TryGetProperty("post", out _));
        Assert.True(paths.GetProperty(proposalsPath + "/{proposalId}/recover").TryGetProperty("post", out _));
        var operations = paths.GetProperty(proposalsPath + "/{proposalId}/operations").GetProperty("get");
        var fingerprint = Assert.Single(operations.GetProperty("parameters").EnumerateArray(), p => p.GetProperty("in").GetString() == "query");
        Assert.Equal("expectedFingerprint", fingerprint.GetProperty("name").GetString());
        Assert.True(fingerprint.GetProperty("required").GetBoolean());
        var approval = schemas.GetProperty("ChatMappingEditApprovalV1");
        Assert.False(approval.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal("expectedFingerprint", Assert.Single(approval.GetProperty("required").EnumerateArray()).GetString());
        Assert.Equal(64, approval.GetProperty("properties").GetProperty("expectedFingerprint").GetProperty("maxLength").GetInt32());
        Assert.Equal(8, schemas.GetProperty("ChatMappingEditProposalPageV1").GetProperty("properties").GetProperty("proposals").GetProperty("maxItems").GetInt32());
        var receipt = schemas.GetProperty("ChatMappingWriteReceiptV1").GetProperty("properties");
        Assert.Equal(new[] { "pending", "saved", "notSaved" }, receipt.GetProperty("status").GetProperty("enum").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains(receipt.GetProperty("write").GetProperty("oneOf").EnumerateArray(), item => item.TryGetProperty("type", out var type) && type.GetString() == "null");
        var savedReference = Assert.Single(receipt.GetProperty("write").GetProperty("oneOf").EnumerateArray(), item => item.TryGetProperty("$ref", out _)).GetProperty("$ref").GetString()!;
        Assert.True(schemas.TryGetProperty(savedReference.Split('/')[^1], out _));
        Assert.False(schemas.GetProperty("ChatWorkspaceConversationCreateV1").GetProperty("additionalProperties").GetBoolean());
        Assert.False(schemas.GetProperty("ChatFocusV1").GetProperty("additionalProperties").GetBoolean());
        Assert.DoesNotContain(schemas.GetProperty("ChatWorkspaceConversationCreateV1").GetProperty("required").EnumerateArray(), field => field.GetString() == "focus");
        foreach (var (owner, property) in new[] { ("ChatOrganizationConversationV1", "focus"), ("ChatOrganizationConversationV1", "revision"),
            ("ChatWorkspaceConversationCreateV1", "focus"), ("ChatTurnFocusV1", "revision"), ("ChatWorkspaceCitationV1", "target") })
            Assert.Contains(schemas.GetProperty(owner).GetProperty("properties").GetProperty(property).GetProperty("oneOf").EnumerateArray(),
                variant => variant.TryGetProperty("type", out var type) && type.GetString() == "null");
        Assert.Equal("weft.chat.v1", Assert.Single(schemas.GetProperty("ChatWorkspaceAvailabilityV1").GetProperty("properties")
            .GetProperty("schemaVersion").GetProperty("enum").EnumerateArray()).GetString());
        var stream = schemas.GetProperty("ChatTurnEventV1").GetRawText();
        Assert.Contains("workspace.citation", stream, StringComparison.Ordinal);
        Assert.Contains("data.shared", stream, StringComparison.Ordinal);
        foreach (var path in paths.EnumerateObject().Where(path => path.Name.StartsWith(root, StringComparison.Ordinal)))
            foreach (var method in path.Value.EnumerateObject().Where(method => method.Name is "get" or "post"))
            {
                if (method.Name == "post")
                    foreach (var header in new[] { "Origin", "X-XSRF-TOKEN" })
                        Assert.Contains(method.Value.GetProperty("parameters").EnumerateArray(), parameter =>
                            parameter.GetProperty("in").GetString() == "header" && parameter.GetProperty("name").GetString() == header &&
                            (!parameter.TryGetProperty("required", out var required) || !required.GetBoolean()));
                var security = method.Value.GetProperty("security").EnumerateArray().ToArray();
                Assert.Contains(security, item => item.TryGetProperty("entraBearer", out _));
                Assert.Contains(security, item => item.TryGetProperty("weftSession", out _) &&
                    (method.Name != "post" || item.TryGetProperty("weftAntiforgery", out _)));
                foreach (var code in new[] { "400", "401", "403", "404", "409", "429", "500", "503" })
                {
                    Assert.True(method.Value.GetProperty("responses").TryGetProperty(code, out _), path.Name + " " + code);
                    Assert.True(method.Value.GetProperty("responses").GetProperty(code).GetProperty("headers").TryGetProperty("Cache-Control", out _));
                }
            }
    }
}
