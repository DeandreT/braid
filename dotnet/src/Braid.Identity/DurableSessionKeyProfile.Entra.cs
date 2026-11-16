using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace Braid.Identity;

public sealed partial class DurableSessionKeyProfile
{
    internal IDataProtectionProvider CreateEntraFlowProvider(Guid tenantId, Guid clientId)
    {
        try
        {
            if (tenantId == Guid.Empty || clientId == Guid.Empty || Volatile.Read(ref disposed) != 0)
                throw ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.Unavailable);
            directory.Revalidate();
            var selected = services.GetRequiredService<IDataProtectionProvider>().CreateProtector(
                "Braid.EntraLoginFlow", "flow:1", "policy:1",
                "configured-organization:" + OrganizationId.ToString("D"),
                "tenant:" + tenantId.ToString("D"), "client:" + clientId.ToString("D"));
            directory.Revalidate();
            return new EntraFlowProtector(this, selected, 0);
        }
        catch (Exception error) when (IsConfigurationError(error))
        { throw ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.Unavailable); }
    }

    // Only public framework OIDC's exact state and nonce child purpose sequences.
    // No existing profile body/startup latch/browser purpose is changed.
    private sealed class EntraFlowProtector(DurableSessionKeyProfile owner, IDataProtector selected,
        int position) : IDataProtector
    {
        public IDataProtector CreateProtector(string purpose)
        {
            var next = (position, purpose) switch
            {
                (0, "Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectHandler") => 1,
                (1, "Braid.Entra") => 2,
                (1, "System.String") => 3,
                (3, "Braid.Entra") => 2,
                (2, "v1") => 4,
                _ => throw ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.UnsupportedFormat)
            };
            return new EntraFlowProtector(owner, selected.CreateProtector(purpose), next);
        }

        public byte[] Protect(byte[] plaintext) => position == 4
            ? owner.Transform(plaintext, unprotect: false, selected)
            : throw ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.UnsupportedFormat);

        public byte[] Unprotect(byte[] protectedData) => position == 4
            ? owner.Transform(protectedData, unprotect: true, selected)
            : throw ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.UnsupportedFormat);
    }
}
