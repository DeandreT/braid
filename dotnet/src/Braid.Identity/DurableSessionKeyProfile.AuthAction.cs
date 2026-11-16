using Microsoft.AspNetCore.DataProtection;

namespace Braid.Identity;

public sealed partial class DurableSessionKeyProfile
{
    internal IDataProtectionProvider CreateAuthActionAntiforgeryProvider() => CreateBrowserProtector(
        "Braid.BrowserAuthAction", "csrf:1", FrameworkAntiforgeryPurpose);
}
