using Microsoft.AspNetCore.Http;

namespace Braid.Identity;

/// <summary>
/// The request-local business scope bound once after fresh admission: the route-selected organization,
/// its retained admission incarnation and the observed session identity it was admitted under.
/// A later inconsistent binding is an integrity failure, never a silent switch.
/// </summary>
public sealed class ServerSessionBusinessScope
{
    private ServerSessionBusinessScope(Guid organizationId, long admissionVersion, Guid principalId, Guid loginEpoch)
    {
        OrganizationId = organizationId;
        AdmissionVersion = admissionVersion;
        PrincipalId = principalId;
        LoginEpoch = loginEpoch;
    }

    public Guid OrganizationId { get; }
    public long AdmissionVersion { get; }
    public Guid PrincipalId { get; }
    public Guid LoginEpoch { get; }
    public override string ToString() => nameof(ServerSessionBusinessScope);

    public static ServerSessionBusinessScope Bind(HttpContext context, Guid organizationId, long admissionVersion,
        ServerSessionRequestIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(identity);
        if (organizationId == Guid.Empty || admissionVersion < 1)
            throw ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.IntegrityFailure);
        var bound = context.Features.Get<ServerSessionBusinessScope>();
        if (bound is not null)
        {
            if (bound.OrganizationId != organizationId || bound.AdmissionVersion != admissionVersion ||
                bound.PrincipalId != identity.PrincipalId.Value || bound.LoginEpoch != identity.LoginEpoch)
                throw ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.IntegrityFailure);
            return bound;
        }
        bound = new(organizationId, admissionVersion, identity.PrincipalId.Value, identity.LoginEpoch);
        context.Features.Set(bound);
        return bound;
    }
}
