using Microsoft.Extensions.DependencyInjection;
using Weft.Application;

namespace Weft.Identity;

public static class EntraBrowserAuthenticationRegistration
{
    public static IServiceCollection AddWeftEntraBrowserAuthentication(this IServiceCollection services,
        BrowserAuthenticationOptions options, DurableSessionKeyProfile profile, IExternalIdentityReader identities)
    {
        if (services is null || options is null || profile is null || identities is null ||
            services.Any(item => item.ServiceType == typeof(EntraBrowserAuthentication) ||
                item.ServiceType == typeof(BrowserAuthenticationOptions)))
            throw BrowserAuthenticationRequest.Error(BrowserAuthenticationFailureKind.Unavailable);
        RequireSingleton<ServerSessionBrowser>(services);
        RequireSingleton<EntraBrowserClient>(services);
        services.AddSingleton(options);
        services.AddSingleton(provider => new EntraBrowserAuthentication(options, profile, identities,
            provider.GetRequiredService<ServerSessionBrowser>(), provider.GetRequiredService<EntraBrowserClient>()));
        return services;
    }

    private static void RequireSingleton<T>(IServiceCollection services)
    {
        var entries = services.Where(item => item.ServiceType == typeof(T)).ToArray();
        if (entries.Length != 1 || entries[0].IsKeyedService || entries[0].Lifetime != ServiceLifetime.Singleton)
            throw BrowserAuthenticationRequest.Error(BrowserAuthenticationFailureKind.Unavailable);
    }
}
