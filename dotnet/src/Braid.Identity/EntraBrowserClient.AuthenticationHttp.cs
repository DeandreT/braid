namespace Braid.Identity;

public sealed partial class EntraBrowserClient
{
    internal bool UsesAuthenticationProfile(DurableSessionKeyProfile profile, Guid expectedOrganization,
        string expectedOrigin) => ReferenceEquals(profile, borrowedProfile) &&
        organization == expectedOrganization && string.Equals(options.PublicOrigin, expectedOrigin, StringComparison.Ordinal);
}
