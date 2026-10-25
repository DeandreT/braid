using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Weft.Cli.Http;
using Weft.Contracts.Administration;
using static Weft.Cli.Commands.CommandSupport;

namespace Weft.Cli.Commands;

internal static class AdministrationCommands
{
    public static Command Administration(CliGlobals globals)
    {
        var project = Optional("--project", "An exact project ID; omit for organization administration");
        var match = Required("--if-match", "The strong ETag from administration show (quote the complete value)");
        var show = new Command("show", "Read current members, invitations, grants and administrative version").With(project);
        show.Handle(globals, async invocation =>
        {
            var projectId = invocation.OptionalId(project);
            var request = await invocation.RequestAsync(projectId is null ? AdministrationApiOperations.GetOrganization : AdministrationApiOperations.GetProject);
            if (projectId is not null) request.Path("projectId", projectId);
            using var response = await invocation.Api.SendAsync(request, invocation.Cancellation);
            await response.RequireSuccessAsync(invocation.Cancellation);
            var etag = response.Message.Headers.ETag?.Tag;
            if (projectId is null)
            {
                var value = await response.ReadJsonAsync<OrganizationAdministrationV1>(invocation.Cancellation);
                RequireETag(etag, await invocation.OrganizationAsync(), value.Version);
                invocation.Output.Write(value, output =>
                {
                    output.Fields(("ETag", etag));
                    output.Table(["PRINCIPAL", "ROLE", "ADMITTED", "PRINCIPAL ACTIVE"], value.Members.Select(m => (IReadOnlyList<object?>)[m.PrincipalId, m.Role, m.Active, m.PrincipalActive]));
                    output.Line("Use --json to include identity bindings, invitation states and recent audit changes.");
                });
            }
            else
            {
                var value = await response.ReadJsonAsync<ProjectAdministrationV1>(invocation.Cancellation);
                RequireETag(etag, await invocation.OrganizationAsync(), value.Version);
                invocation.Output.Write(value, output =>
                {
                    output.Fields(("ETag", etag));
                    output.Table(["PRINCIPAL", "ROLE", "ACTIVE", "PAYLOAD READ"], value.Members.Select(m => (IReadOnlyList<object?>)[m.PrincipalId, m.Role, m.Active, m.PayloadRead]));
                    output.Table(["MACHINE OBJECT", "TARGET", "REVOKED"], value.Grants.Select(g => (IReadOnlyList<object?>)[g.ObjectId, g.TargetId, g.RevokedAt is not null]));
                });
            }
        });

        var principal = Required("--principal", "The admitted principal ID");
        var role = Required("--role", "member/administrator for organization; viewer/runner/publisher/administrator for project");
        var active = new Option<bool>("--active") { Required = true, Arity = ArgumentArity.ExactlyOne, Description = "true to admit/restore; false to suspend/remove" };
        var member = new Command("member", "Admit, suspend, restore or change one member's role").With(project, principal, role, active, match);
        member.Handle(globals, async invocation =>
        {
            var projectId = invocation.OptionalId(project); var selectedRole = invocation.Value(role)!;
            if (projectId is null ? selectedRole is not ("member" or "administrator") : selectedRole is not ("viewer" or "runner" or "publisher" or "administrator"))
                throw CliException.Usage("Choose a supported role for this administrative scope.");
            var operation = projectId is null ? AdministrationApiOperations.OrganizationMember : AdministrationApiOperations.ProjectMember;
            var request = await Conditional(invocation, operation, match, projectId);
            request.Path("principalId", invocation.Id(principal)).Body(CatalogCommands.JsonBody(new AdministrationMemberPutV1(invocation.Value(active), selectedRole), operation));
            await Receipt(invocation, request);
        });

        var payload = new Command("payload", "Grant or revoke a member's explicit run.payload.read permission").With(project, principal, active, match);
        payload.Handle(globals, async invocation =>
        {
            var request = await Conditional(invocation, AdministrationApiOperations.PayloadRead, match, ProjectId(invocation, project));
            request.Path("principalId", invocation.Id(principal)).Body(CatalogCommands.JsonBody(new AdministrationPayloadPutV1(invocation.Value(active)), AdministrationApiOperations.PayloadRead));
            await Receipt(invocation, request);
        });

        return new Command("administration", "Organization and project administration; edits require an observed ETag and are never retried after an uncertain result")
            .With(show, member, payload, Disable(globals, match, "principal", AdministrationApiOperations.DisablePrincipal, "principalId"),
                Disable(globals, match, "binding", AdministrationApiOperations.DisableBinding, "bindingId"), Invitations(globals, match), InvocationGrants(globals, project, match));
    }

    private static Command Disable(CliGlobals globals, Option<string> match, string noun, ApiOperation operation, string parameter)
    {
        var id = Argument("id", "The " + noun + " ID");
        var command = new Command("disable-" + noun, "Permanently disable a " + noun + " without deleting its history").With(id, match);
        command.Handle(globals, async invocation => await Receipt(invocation, (await Conditional(invocation, operation, match)).Path(parameter, invocation.Id(id))));
        return command;
    }

    private static Command Invitations(CliGlobals globals, Option<string> match)
    {
        var file = Required("--file", "Invitation JSON: invitationId, tenantId, expectedObjectId (or null), projectRoles, expiresAt; '-' reads stdin");
        var create = new Command("create", "Create an expiring invitation; the private redemption link is returned only once").With(file, match);
        create.Handle(globals, async invocation =>
        {
            var bytes = await ReadInputAsync(invocation, invocation.Value(file)!, 8192, "invitation");
            AdministrationInvitationPostV1 body;
            try
            {
                var options = new JsonSerializerOptions(WeftApiClient.Json) { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true };
                body = JsonSerializer.Deserialize<AdministrationInvitationPostV1>(bytes, options) ?? throw new JsonException();
            }
            catch (JsonException) { throw CliException.Usage("The invitation file is not a valid invitation request."); }
            var request = await Conditional(invocation, AdministrationApiOperations.Invite, match);
            request.Body(CatalogCommands.JsonBody(body, AdministrationApiOperations.Invite));
            var saved = await invocation.Api.ReadAsync<AdministrationInvitationCreatedV1>(request, invocation.Cancellation);
            if (!saved.RedemptionPath.StartsWith("/invitations#", StringComparison.Ordinal) || saved.RedemptionPath.Length != 77 ||
                saved.RedemptionPath[13..].Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
                throw new CliException(CliExitCode.Transport, "The server returned an invalid invitation link.", "invalidResponse");
            invocation.Output.Write(saved, output => output.Fields(("Invitation", saved.InvitationId),
                ("Private link (shown once)", new Uri(invocation.Profile.Server, saved.RedemptionPath).AbsoluteUri)));
        });
        var id = Argument("invitationId", "The invitation ID");
        var revoke = new Command("revoke", "Revoke an unused invitation").With(id, match);
        revoke.Handle(globals, async invocation => await Receipt(invocation, (await Conditional(invocation, AdministrationApiOperations.RevokeInvitation, match)).Path("invitationId", invocation.Id(id))));
        return new Command("invitations", "Create and revoke organization invitations").With(create, revoke);
    }

    private static Command InvocationGrants(CliGlobals globals, Option<string?> project, Option<string> match)
    {
        var grantId = Argument("grantId", "A client-chosen grant ID");
        var target = Required("--target", "The registered release target ID"); var tenant = Required("--tenant", "The fixed Entra tenant ID");
        var machine = Required("--object", "The machine service-principal object ID, not the application/client ID");
        var grant = new Command("grant", "Grant one machine caller invocation of one exact target").With(project, match, grantId, target, tenant, machine);
        grant.Handle(globals, async invocation =>
        {
            var request = await Conditional(invocation, AdministrationApiOperations.GrantInvocation, match, ProjectId(invocation, project));
            request.Path("grantId", invocation.Id(grantId)).Body(CatalogCommands.JsonBody(new AdministrationInvocationGrantPutV1(
                Guid.Parse(invocation.Id(target)), Guid.Parse(invocation.Id(tenant)), Guid.Parse(invocation.Id(machine))), AdministrationApiOperations.GrantInvocation));
            await Receipt(invocation, request);
        });
        var revoke = new Command("revoke", "Revoke a retained invocation grant").With(project, match, grantId);
        revoke.Handle(globals, async invocation => await Receipt(invocation, (await Conditional(invocation, AdministrationApiOperations.RevokeInvocation, match, ProjectId(invocation, project))).Path("grantId", invocation.Id(grantId))));
        return new Command("invocation-grants", "Manage machine callers' per-target invocation grants").With(grant, revoke);
    }

    private static string ProjectId(CliInvocation invocation, Option<string?> project) => invocation.OptionalId(project) ?? throw CliException.Usage("--project is required for this operation.");
    private static async Task<ApiRequest> Conditional(CliInvocation invocation, ApiOperation operation, Option<string> match, string? project = null)
    {
        var org = await invocation.OrganizationAsync(); var etag = invocation.Value(match)!;
        var prefix = $"\"admin-{org}-";
        if (!etag.StartsWith(prefix, StringComparison.Ordinal) || !etag.EndsWith('"') ||
            !long.TryParse(etag.AsSpan(prefix.Length, etag.Length - prefix.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version < 1 ||
            etag != prefix + version.ToString(CultureInfo.InvariantCulture) + "\"") throw CliException.Usage("--if-match must be the exact strong administration ETag from a fresh show command.");
        var request = (await invocation.RequestAsync(operation)).Header("If-Match", etag);
        return project is null ? request : request.Path("projectId", project);
    }
    private static void RequireETag(string? etag, string org, long version)
    {
        if (version < 1 || etag != $"\"admin-{org}-{version.ToString(CultureInfo.InvariantCulture)}\"")
            throw new CliException(CliExitCode.Transport, "The server returned no valid administration ETag.", "invalidResponse");
    }
    private static async Task Receipt(CliInvocation invocation, ApiRequest request)
    {
        var receipt = await invocation.Api.ReadAsync<AdministrationReceiptV1>(request, invocation.Cancellation);
        if (receipt.Outcome is not ("updated" or "unchanged")) throw new CliException(CliExitCode.Transport, "The server returned an invalid administration receipt.", "invalidResponse");
        invocation.Output.Write(receipt, output => output.Fields(("Outcome", receipt.Outcome)));
    }
}
