using Weft.Cli;
using Weft.Cli.Authentication;
using Weft.Cli.Http;
using Weft.Contracts.Chat;

namespace Weft.Tests;

public sealed class CliWorkspaceCommandTests
{
    private static CliEnvironment Environment(TextWriter? error = null) => new()
    {
        Out = TextWriter.Null,
        Error = error ?? TextWriter.Null,
        OpenStandardInput = () => Stream.Null,
        OpenStandardOutput = () => Stream.Null,
        ConfigurationDirectory = "/unconfigured-weft-cli-test",
        TokenCaches = new MsalProtectedTokenCacheStore("/unconfigured-weft-cli-test")
    };

    [Fact]
    public void Workspace_commands_parse_without_changing_legacy_revision_requirements()
    {
        var root = CliApplication.Build(Environment());
        Assert.Empty(root.Parse(["chat", "workspace", "ask", "Explain the workspace"]).Errors);
        Assert.NotEmpty(root.Parse(["chat", "ask", "Explain the revision"]).Errors);
        var id = Guid.NewGuid().ToString("D");
        Assert.Empty(root.Parse(["chat", "ask", "--project", id, "--mapping", id, "--revision", id, "Explain the revision"]).Errors);
        Assert.Empty(root.Parse(["chat", "workspace", "conversations", "list", "--after", id, "--revision", id]).Errors);
        Assert.Empty(root.Parse(["chat", "workspace", "follow", "--conversation", id, "--after", "42", id]).Errors);
    }

    [Theory]
    [InlineData("--focus-project")]
    [InlineData("--focus-mapping")]
    [InlineData("--focus-revision")]
    public async Task Partial_focus_is_refused_before_any_configuration_or_sign_in(string option)
    {
        using var error = new StringWriter();
        var result = await CliApplication.RunAsync(["chat", "workspace", "ask", option, Guid.NewGuid().ToString("D"), "question"], Environment(error));
        Assert.Equal((int)CliExitCode.Usage, result);
        Assert.Contains("Focus requires", error.ToString());
    }

    [Fact]
    public async Task Turn_recovery_requires_the_original_conversation_before_sign_in()
    {
        using var error = new StringWriter();
        var result = await CliApplication.RunAsync(["chat", "workspace", "ask", "--turn-id", Guid.NewGuid().ToString("D"), "question"], Environment(error));
        Assert.Equal((int)CliExitCode.Usage, result);
        Assert.Contains("--turn-id recovery requires --conversation", error.ToString());
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("4097")]
    public async Task Follow_refuses_an_out_of_range_cursor_before_configuration(string cursor)
    {
        var id = Guid.NewGuid().ToString("D");
        Assert.Equal((int)CliExitCode.Usage, await CliApplication.RunAsync(
            ["chat", "workspace", "follow", "--conversation", id, "--after", cursor, id], Environment()));
    }

    [Fact]
    public void Workspace_operation_inventory_is_closed_and_does_not_send_cookie_headers_or_mutate_focus()
    {
        var operations = ApiOperations.All.Where(item => item.Path.StartsWith("/api/v1/organizations/{organizationId}/chat", StringComparison.Ordinal)).ToArray();
        Assert.Equal(7, operations.Length);
        Assert.Equal(7, operations.Select(item => item.OperationId).Distinct(StringComparer.Ordinal).Count());
        foreach (var operation in operations)
        {
            Assert.StartsWith("/api/v1/organizations/{organizationId}/chat", operation.Path, StringComparison.Ordinal);
            Assert.DoesNotContain("{revisionId}", operation.Path);
            Assert.NotNull(operation.Query); Assert.NotNull(operation.Headers);
            Assert.DoesNotContain(operation.Headers, ApiOperations.CookieSessionHeaders.Contains);
        }
        Assert.Equal(typeof(ChatWorkspaceConversationCreateV1), ApiOperations.CreateWorkspaceChatConversation.RequestBody);
        Assert.Equal(new[] { "limit", "after", "revisionId" }, ApiOperations.GetWorkspaceChatConversations.Query);
        Assert.Equal("text/event-stream", ApiOperations.StreamWorkspaceChatTurnEvents.ResponseMediaType);
    }
}
