using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace Braid.Identity;

public sealed partial class DurableSessionKeyProfile
{
    internal const string FrameworkAntiforgeryPurpose = "Microsoft.AspNetCore.Antiforgery.AntiforgeryToken.v1";

    internal IDataProtector CreateCookieCarrierProtector() => CreateBrowserProtector(
        "Braid.ServerSessionCookie", "carrier:1", nextPurpose: null);

    internal IDataProtectionProvider CreateAntiforgeryProvider() => CreateBrowserProtector(
        "Braid.ServerSessionAntiforgery", "csrf:1", FrameworkAntiforgeryPurpose);

    private BrowserProtector CreateBrowserProtector(string component, string version, string? nextPurpose)
    {
        try
        {
            if (Volatile.Read(ref disposed) != 0)
                throw ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.Unavailable);
            directory.Revalidate();
            var selected = services.GetRequiredService<IDataProtectionProvider>().CreateProtector(component,
                version, "policy:1", "configured-organization:" + OrganizationId.ToString("D"));
            directory.Revalidate();
            return new(this, selected, nextPurpose);
        }
        catch (Exception error) when (IsConfigurationError(error))
        {
            throw ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.Unavailable);
        }
    }

    // Only framework antiforgery's one fixed child purpose is admitted. Cookie carriers have none.
    private sealed class BrowserProtector(DurableSessionKeyProfile owner, IDataProtector selected,
        string? nextPurpose) : IDataProtector
    {
        public IDataProtector CreateProtector(string purpose)
        {
            if (nextPurpose is null || !string.Equals(nextPurpose, purpose, StringComparison.Ordinal))
                throw ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.UnsupportedFormat);
            return new BrowserProtector(owner, selected.CreateProtector(nextPurpose), nextPurpose: null);
        }

        public byte[] Protect(byte[] plaintext) => nextPurpose is null
            ? owner.Transform(plaintext, unprotect: false, selected)
            : throw ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.UnsupportedFormat);

        public byte[] Unprotect(byte[] protectedData) => nextPurpose is null
            ? owner.Transform(protectedData, unprotect: true, selected)
            : throw ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.UnsupportedFormat);
    }
}
