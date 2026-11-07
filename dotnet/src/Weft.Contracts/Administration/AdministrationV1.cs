using System.Text.Json;

namespace Weft.Contracts.Administration;

public sealed record OrganizationAdministrationV1(long Version, IReadOnlyList<OrganizationMemberV1> Members,
    IReadOnlyList<OrganizationInvitationV1> Invitations, IReadOnlyList<AdministrationAuditV1> Audit);
public sealed record OrganizationMemberV1(Guid PrincipalId, bool Active, string Role, long Version,
    bool PrincipalActive, IReadOnlyList<AdministrationBindingV1> Bindings);
public sealed record AdministrationBindingV1(Guid BindingId, string Provider, string Authority, string Subject, bool Enabled, long Version);
public sealed record OrganizationInvitationV1(Guid InvitationId, Guid TenantId, Guid? ExpectedObjectId,
    IReadOnlyList<InvitationProjectRoleV1> ProjectRoles, DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt, DateTimeOffset? RedeemedAt);
public sealed record AdministrationAuditV1(long Sequence, Guid ActorId, string Operation, Guid TargetId, Guid? ProjectId,
    JsonElement? Before, JsonElement? After, DateTimeOffset RecordedAt);
public sealed record ProjectAdministrationV1(long Version, IReadOnlyList<ProjectMemberV1> Members, IReadOnlyList<AdministrationInvocationGrantV1> Grants);
public sealed record ProjectMemberV1(Guid PrincipalId, bool Active, string Role, long Version, bool PayloadRead, long PayloadVersion);
public sealed record AdministrationInvocationGrantV1(Guid GrantId, Guid TargetId, Guid MappingId, Guid TenantId, Guid ObjectId,
    DateTimeOffset GrantedAt, DateTimeOffset? RevokedAt);
public sealed record AdministrationMemberPutV1(bool Active, string Role);
public sealed record AdministrationPayloadPutV1(bool Active);
public sealed record InvitationProjectRoleV1(Guid ProjectId, string Role);
public sealed record AdministrationInvitationPostV1(Guid InvitationId, Guid TenantId, Guid? ExpectedObjectId,
    IReadOnlyList<InvitationProjectRoleV1> ProjectRoles, DateTimeOffset ExpiresAt);
public sealed record AdministrationInvocationGrantPutV1(Guid TargetId, Guid TenantId, Guid ObjectId);
public sealed record AdministrationReceiptV1(string Outcome);
public sealed record AdministrationErrorV1(string Code);

/// <summary>The link is returned once. It must never enter diagnostics or an audit event.</summary>
public sealed class AdministrationInvitationCreatedV1(Guid invitationId, string redemptionPath)
{
    public Guid InvitationId { get; } = invitationId;
    public string RedemptionPath { get; } = redemptionPath;
    public override string ToString() => nameof(AdministrationInvitationCreatedV1);
}
