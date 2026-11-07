using Weft.Contracts.Administration;

namespace Weft.Cli.Http;

internal static class AdministrationApiOperations
{
    private const string Organization = "/api/v1/organizations/{organizationId}/administration";
    private const string Project = "/api/v1/organizations/{organizationId}/projects/{projectId}/administration";
    public static readonly ApiOperation GetOrganization = Entry("GetOrganizationAdministration", "GET", Organization, null, typeof(OrganizationAdministrationV1));
    public static readonly ApiOperation GetProject = Entry("GetProjectAdministration", "GET", Project, null, typeof(ProjectAdministrationV1));
    public static readonly ApiOperation OrganizationMember = Entry("PutOrganizationMember", "PUT", Organization + "/members/{principalId}", typeof(AdministrationMemberPutV1));
    public static readonly ApiOperation ProjectMember = Entry("PutProjectMember", "PUT", Project + "/members/{principalId}", typeof(AdministrationMemberPutV1));
    public static readonly ApiOperation PayloadRead = Entry("PutProjectPayloadRead", "PUT", Project + "/members/{principalId}/payload-read", typeof(AdministrationPayloadPutV1));
    public static readonly ApiOperation DisablePrincipal = Entry("DisableAdministrationPrincipal", "POST", Organization + "/principals/{principalId}/disable");
    public static readonly ApiOperation DisableBinding = Entry("DisableAdministrationBinding", "POST", Organization + "/bindings/{bindingId}/disable");
    public static readonly ApiOperation Invite = Entry("CreateOrganizationInvitation", "POST", Organization + "/invitations", typeof(AdministrationInvitationPostV1), typeof(AdministrationInvitationCreatedV1));
    public static readonly ApiOperation RevokeInvitation = Entry("RevokeOrganizationInvitation", "POST", Organization + "/invitations/{invitationId}/revoke");
    public static readonly ApiOperation GrantInvocation = Entry("PutAdministrationInvocationGrant", "PUT", Project + "/invocation-grants/{grantId}", typeof(AdministrationInvocationGrantPutV1));
    public static readonly ApiOperation RevokeInvocation = Entry("RevokeAdministrationInvocationGrant", "POST", Project + "/invocation-grants/{grantId}/revoke");
    public static readonly ApiOperation[] All = [GetOrganization, GetProject, OrganizationMember, ProjectMember, PayloadRead,
        DisablePrincipal, DisableBinding, Invite, RevokeInvitation, GrantInvocation, RevokeInvocation];

    private static ApiOperation Entry(string name, string method, string path, Type? body = null, Type? response = null) =>
        new(name, method, path, [], method == "GET" ? [] : ["If-Match"], body is null ? null : "application/json", body,
            new Dictionary<int, Type?> { [200] = response ?? typeof(AdministrationReceiptV1) }, typeof(AdministrationErrorV1),
            RetryOnFailure: method == "GET");
}
