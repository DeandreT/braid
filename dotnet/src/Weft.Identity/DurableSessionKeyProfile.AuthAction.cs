using Microsoft.AspNetCore.DataProtection;

namespace Weft.Identity;

public sealed partial class DurableSessionKeyProfile
{
    internal IDataProtectionProvider CreateAuthActionAntiforgeryProvider() => CreateBrowserProtector(
        "Weft.BrowserAuthAction", "csrf:1", FrameworkAntiforgeryPurpose);
}
