using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;

namespace Weft.Server;

/// <summary>The Session host has only the separately admitted, privately composed key profile.</summary>
internal static class SessionHostDataProtectionPolicy
{
    internal static void Apply(IServiceCollection services)
    {
        // AddAuthentication adds this unused warmup through AddDataProtection. Match its actual
        // framework identity, not an arbitrary service's type name or all hosted services.
        var warmupType = typeof(IKeyManager).Assembly.GetType(
            "Microsoft.AspNetCore.DataProtection.Internal.DataProtectionHostedService", throwOnError: false);
        if (warmupType is null || !typeof(IHostedService).IsAssignableFrom(warmupType))
            throw ServerHostOptions.Failure();
        var warmups = services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService) &&
            !descriptor.IsKeyedService && descriptor.ImplementationType == warmupType).ToArray();
        var providers = services.Where(descriptor => descriptor.ServiceType == typeof(IDataProtectionProvider)).ToArray();
        // Fail before changing registrations if framework composition no longer has this shape.
        if (warmups.Length != 1 || warmups[0].Lifetime != ServiceLifetime.Singleton ||
            providers.Length != 1 || providers[0].IsKeyedService || providers[0].Lifetime != ServiceLifetime.Singleton)
            throw ServerHostOptions.Failure();
        services.Remove(warmups[0]);
        services.Remove(providers[0]);
        services.AddSingleton<IDataProtectionProvider>(new UnusedHostProvider());
    }

    private sealed class UnusedHostProvider : IDataProtectionProvider
    {
        // Named cookie/OIDC and private antiforgery compositions already select the admitted
        // profile. An accidental future main-container consumer must not select HOME storage.
        public IDataProtector CreateProtector(string purpose) => throw ServerHostOptions.Failure();
        public override string ToString() => nameof(UnusedHostProvider);
    }
}
