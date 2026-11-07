using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Weft.Contracts.Administration;
using Weft.Domain;

namespace Weft.Application;

public enum AdministrationOperation
{
    OrganizationMember, ProjectMember, PayloadRead, PrincipalDisable, BindingDisable, Invite, InviteRevoke,
    InvocationGrant, InvocationRevoke
}

public interface IAdministrationStore
{
    Task<OrganizationAdministrationV1?> ReadOrganizationAsync(ActorContext actor, Guid organizationId, CancellationToken token);
    Task<ProjectAdministrationV1?> ReadProjectAsync(ActorContext actor, ProjectScope project, CancellationToken token);
    Task<string> WriteAsync(ActorContext actor, Guid organizationId, Guid? projectId, long expectedVersion,
        AdministrationOperation operation, Guid targetId, JsonElement data, CancellationToken token);
}

/// <summary>Used only with an already verified, fixed-tenant Entra login, before resolving its binding.</summary>
public interface IOrganizationInvitationRedeemer
{
    Task<bool> RedeemAsync(Guid organizationId, string secret, ExternalIdentityKey verifiedIdentity, CancellationToken token);
}

public static class OrganizationInvitationSecret
{
    public static string Create() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    public static bool IsValid(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static string Hash(string value)
    {
        if (!IsValid(value)) throw new ArgumentException("Invalid invitation credential.", nameof(value));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(value)));
    }
}
