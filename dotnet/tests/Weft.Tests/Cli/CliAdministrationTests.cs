using System.Text;
using System.Text.Json.Nodes;
using Weft.Cli;
using Weft.Contracts.Administration;

namespace Weft.Tests.Cli;

public sealed class CliAdministrationTests : IAsyncLifetime
{
    private CliTestIssuer issuer = null!;
    private CliApiStub stub = null!;
    private CliHarness cli = null!;
    private string ETag => $"\"admin-{stub.OrganizationId}-7\"";

    public async Task InitializeAsync()
    {
        issuer = await CliTestIssuer.StartAsync(); stub = await CliApiStub.StartAsync(issuer);
        cli = new(issuer, stub.Handler); await cli.ConfigureAsync(); await cli.LoginAsync();
    }
    public async Task DisposeAsync() { stub.AssertClean(); cli.Dispose(); await stub.DisposeAsync(); await issuer.DisposeAsync(); }

    [Fact]
    public async Task Show_reports_the_observed_etag_and_json_preserves_the_response()
    {
        var principal = Guid.NewGuid();
        stub.Reply("GetOrganizationAdministration", _ => CliStubReply.Of(200,
            new OrganizationAdministrationV1(7, [new(principal, true, "administrator", 1, true, [])], [], []), ("ETag", ETag)));
        var human = await cli.RunAsync("administration", "show");
        Assert.True(human.Exit == 0, human.ToString()); Assert.Contains(ETag, human.Out); Assert.Contains(principal.ToString("D"), human.Out);
        var json = await cli.RunAsync("administration", "show", "--json");
        Assert.Equal(7, JsonNode.Parse(json.Out)!["version"]!.GetValue<long>());
    }

    [Fact]
    public async Task Member_payload_and_invocation_commands_use_the_exact_scope_and_condition()
    {
        foreach (var operation in new[] { "PutOrganizationMember", "PutProjectMember", "PutProjectPayloadRead", "PutAdministrationInvocationGrant", "RevokeAdministrationInvocationGrant" })
            stub.Reply(operation, _ => CliStubReply.Of(200, new AdministrationReceiptV1("updated")));
        var principal = CliStubData.Id(); var project = CliStubData.Id(); var grant = CliStubData.Id();
        Assert.Equal(0, (await cli.RunAsync("administration", "member", "--principal", principal, "--role", "member", "--active", "false", "--if-match", ETag)).Exit);
        Assert.Equal(0, (await cli.RunAsync("administration", "member", "--project", project, "--principal", principal, "--role", "runner", "--active", "true", "--if-match", ETag)).Exit);
        Assert.Equal(0, (await cli.RunAsync("administration", "payload", "--project", project, "--principal", principal, "--active", "true", "--if-match", ETag)).Exit);
        Assert.Equal(0, (await cli.RunAsync("administration", "invocation-grants", "grant", grant, "--project", project,
            "--target", CliStubData.Id(), "--tenant", CliStubData.Id(), "--object", CliStubData.Id(), "--if-match", ETag)).Exit);
        Assert.Equal(0, (await cli.RunAsync("administration", "invocation-grants", "revoke", grant, "--project", project, "--if-match", ETag)).Exit);
        foreach (var request in stub.Requests.Where(r => r.OperationId != "GetCatalogContext")) Assert.Equal(ETag, request.Headers["If-Match"]);
        Assert.False(stub.Calls("PutOrganizationMember")[0].Json!["active"]!.GetValue<bool>());
        Assert.Equal(project, stub.Calls("PutProjectMember")[0].Path["projectId"]);
        Assert.Equal(principal, stub.Calls("PutProjectPayloadRead")[0].Path["principalId"]);
    }

    [Fact]
    public async Task Every_bodyless_control_uses_its_declared_identifier_and_sends_no_json()
    {
        foreach (var (command, operation) in new[] { ("disable-principal", "DisableAdministrationPrincipal"),
            ("disable-binding", "DisableAdministrationBinding") })
        {
            stub.Reply(operation, _ => CliStubReply.Of(200, new AdministrationReceiptV1("updated")));
            Assert.Equal(0, (await cli.RunAsync("administration", command, CliStubData.Id(), "--if-match", ETag)).Exit);
            Assert.Empty(stub.Calls(operation)[0].Body);
        }
        stub.Reply("RevokeOrganizationInvitation", _ => CliStubReply.Of(200, new AdministrationReceiptV1("updated")));
        Assert.Equal(0, (await cli.RunAsync("administration", "invitations", "revoke", CliStubData.Id(), "--if-match", ETag)).Exit);
    }

    [Fact]
    public async Task An_invitation_is_explicit_input_and_its_private_link_is_only_deliberate_output()
    {
        var id = Guid.NewGuid(); var secret = new string('a', 64);
        stub.Reply("CreateOrganizationInvitation", _ => CliStubReply.Of(200, new AdministrationInvitationCreatedV1(id, "/invitations#" + secret)));
        var body = Encoding.UTF8.GetBytes(new JsonObject
        {
            ["invitationId"] = id.ToString("D"),
            ["tenantId"] = Guid.NewGuid().ToString("D"),
            ["expectedObjectId"] = null,
            ["projectRoles"] = new JsonArray(),
            ["expiresAt"] = DateTimeOffset.UtcNow.AddHours(1).ToString("O")
        }.ToJsonString());
        var result = await cli.RunWithInputAsync(body, "administration", "invitations", "create", "--file", "-", "--if-match", ETag);
        Assert.True(result.Exit == 0, result.ToString()); Assert.Contains("https://weft.example.test/invitations#" + secret, result.Out);
        Assert.DoesNotContain(secret, result.Error); Assert.Single(stub.Calls("CreateOrganizationInvitation"));
    }

    [Fact]
    public async Task Stale_conditions_and_uncertain_transport_results_never_retry_edits()
    {
        stub.Reply("PutOrganizationMember", _ => CliStubReply.Of(412, new AdministrationErrorV1("preconditionFailed")));
        var args = new[] { "administration", "member", "--principal", CliStubData.Id(), "--role", "member", "--active", "true", "--if-match", ETag };
        Assert.Equal(CliExitCode.PreconditionFailed, (await cli.RunAsync(args)).Code);
        Assert.Single(stub.Calls("PutOrganizationMember"));
        stub.Reply("PutOrganizationMember", _ => throw new HttpRequestException("synthetic lost acknowledgment"));
        var uncertain = await cli.RunAsync(args);
        Assert.Equal(CliExitCode.Transport, uncertain.Code); Assert.Contains("may have committed", uncertain.Error);
        Assert.Equal(2, stub.Calls("PutOrganizationMember").Count);
    }
}
