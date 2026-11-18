using System.Net;
using System.Text;
using System.Text.Json;
using Braid.Application;
using Braid.Domain;

namespace Braid.Tests;

public sealed partial class BearerHttpIntegrationTests
{
    [PostgresFact]
    public Task Workspace_conversations_are_shared_between_bearer_and_cookie_and_mutations_are_audited() => AuthHttpFixture.RunAsync(postgres, async test =>
    {
        var token = Token(test);
        var jar = await test.LoginAsync();
        // Business mutations take the organization-bound context token, never the auth-status token.
        var scoped = await test.SendAsync($"/api/v1/organizations/{test.Data.Seed.OrganizationA:D}/context", jar); jar.Apply(scoped);
        var epoch = scoped.Json().GetProperty("catalog").GetProperty("requestToken").GetString();
        var path = $"/api/v1/organizations/{test.Data.Seed.OrganizationA:D}/chat";
        var available = await BearerAsync(test, path, token);
        Assert.Equal(HttpStatusCode.OK, available.Status);
        Assert.Equal((await test.SendAsync(path, jar)).Json().GetRawText(), available.Json().GetRawText());
        NoSession(available);
        var conversation = Guid.NewGuid();
        var body = JsonSerializer.Serialize(new { conversationId = conversation.ToString("D") });
        void Content(HttpRequestMessage message)
        {
            message.Content = new StringContent(body, Encoding.UTF8);
            message.Content.Headers.ContentType = new("application/json");
        }
        var created = await BearerAsync(test, path + "/conversations", token, "POST", mutate: Content);
        Assert.Equal(HttpStatusCode.Created, created.Status);
        Assert.Equal("workspace", created.Json().GetProperty("kind").GetString());
        Assert.Equal(1, created.Record.Audits);
        NoSession(created);
        var byCookie = await test.SendAsync(path + $"/conversations/{conversation:D}", jar);
        var byBearer = await BearerAsync(test, path + $"/conversations/{conversation:D}", token);
        Assert.Equal(HttpStatusCode.OK, byCookie.Status);
        Assert.Equal(byCookie.Json().GetRawText(), byBearer.Json().GetRawText());
        var replay = await test.SendAsync(path + "/conversations", jar, "POST", epoch: epoch, mutate: Content);
        Assert.Equal(HttpStatusCode.OK, replay.Status);
        Assert.Equal(created.Json().GetRawText(), replay.Json().GetRawText());
        var listed = await BearerAsync(test, path + "/conversations?limit=1", token);
        Assert.Equal(conversation.ToString("D"), Assert.Single(listed.Json().GetProperty("conversations").EnumerateArray()).GetProperty("conversationId").GetString());
        var audit = Assert.Single((await AuditsAsync(test)).EnumerateArray());
        Assert.Equal("CreateWorkspaceChatConversation", audit.GetProperty("operation").GetString());
        Assert.Equal("organizationId", Assert.Single(audit.GetProperty("resource_names").EnumerateArray()).GetString());
        Assert.DoesNotContain(token, audit.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(body, audit.GetRawText(), StringComparison.Ordinal);
        var mixed = await BearerAsync(test, path, token, jar: jar);
        Refused(test, mixed, HttpStatusCode.BadRequest, "invalidRequest", "Bearer error=\"invalid_request\"", token, "workspaceMixed");
        var missingCsrf = await test.SendAsync(path + "/conversations", jar, "POST", mutate: Content);
        Assert.Equal(HttpStatusCode.Forbidden, missingCsrf.Status);
        var unavailable = test.NewRecord();
        unavailable.AuditUnavailable = true;
        var unaudited = await BearerAsync(test, path + "/conversations", token, "POST", record: unavailable, mutate: Content);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unaudited.Status);
        Assert.Equal(0, unaudited.Record.BodyReads);
    }, bearerBurst: 100, workspaceChat: true);
}

/// <summary>The identity fixture has no derived reports. These ports must not run for conversation-only admission.</summary>
internal sealed class AuthHttpWorkspaceNoReports : IRevisionReportReader, IRevisionReportMetadataReader
{
    public Task<RevisionReportReadResult> GetCurrentAsync(ActorContext actor, RevisionScope scope, RevisionReportOperation operation,
        Guid? projectionDerivationId = null, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected report read.");
    public Task<RevisionReportReadResult> GetRetainedAsync(ActorContext actor, RevisionScope scope, RevisionReportOperation operation,
        Guid derivationId, Guid? projectionDerivationId = null, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected report read.");
    public Task<AuthorizedRevisionReportSource?> GetSourceAsync(ActorContext actor, RevisionScope scope,
        CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected report read.");
    public Task<RevisionReportReadSnapshot?> GetCurrentAsync(ActorContext actor, RevisionReportKey key,
        CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected report read.");
    public Task<AuthorizedRevisionReportAttempt?> GetRetainedAsync(ActorContext actor, RevisionScope scope, RevisionReportOperation operation,
        Guid derivationId, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected report read.");
}
