using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Braid.Identity;

public static class EntraClientRegistration
{
    public static IServiceCollection AddBraidEntraClient(this IServiceCollection services, EntraClientOptions options,
        DurableSessionKeyProfile profile, X509Certificate2 clientCertificate) =>
        Register(services, options, profile, clientCertificate, fixture: null);

    internal static IServiceCollection AddForHttpsFixture(IServiceCollection services, EntraClientOptions options,
        DurableSessionKeyProfile profile, X509Certificate2 clientCertificate, EntraHttpsFixtureTransport transport) =>
        Register(services, options, profile, clientCertificate, transport ??
            throw EntraErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure));

    private static IServiceCollection Register(IServiceCollection services, EntraClientOptions options,
        DurableSessionKeyProfile profile, X509Certificate2 certificate, EntraHttpsFixtureTransport? fixture)
    {
        if (services is null || options is null || profile is null || certificate is null ||
            services.Any(item => item.ServiceType == typeof(EntraBrowserClient)))
            throw EntraErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure);
        services.AddSingleton(_ => new EntraBrowserClient(options, profile, certificate, fixture));
        services.Configure<AuthenticationOptions>(value =>
        {
            if (value.Schemes.Any(scheme => scheme.Name is EntraBrowserClient.Scheme or EntraBrowserClient.Sink))
                throw EntraErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure);
        });
        services.AddAuthentication()
            .AddRemoteScheme<OpenIdConnectOptions, PrivateEntraHandler>(EntraBrowserClient.Scheme,
                displayName: null, configureOptions: null)
            .AddScheme<AuthenticationSchemeOptions, EntraRefusingSignInHandler>(EntraBrowserClient.Sink,
                displayName: null, configureOptions: null);
        services.AddOptions<OpenIdConnectOptions>(EntraBrowserClient.Scheme)
            .Configure<EntraBrowserClient>((value, client) => client.ConfigureNamedOptions(value));
        services.AddSingleton<IPostConfigureOptions<OpenIdConnectOptions>, EntraNamedPostConfigure>();
        return services;
    }
}
