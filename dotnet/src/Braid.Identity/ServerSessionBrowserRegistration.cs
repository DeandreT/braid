using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Braid.Application;

namespace Braid.Identity;

public static class ServerSessionBrowserRegistration
{
    public static IServiceCollection AddBraidBrowserSessions(this IServiceCollection services,
        ServerSessionBrowserOptions options, DurableSessionKeyProfile profile, IServerSessionReader reader,
        IServerSessionWriter writer, TimeProvider renewalSchedulingClock)
    {
        if (services is null || options is null || profile is null || reader is null || writer is null ||
            renewalSchedulingClock is null || services.Any(item => item.ServiceType == typeof(ServerSessionBrowser)))
            throw ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure);
        services.AddSingleton(_ => new ServerSessionBrowser(options, profile, reader, writer, renewalSchedulingClock));
        services.AddSingleton(provider => provider.GetRequiredService<ServerSessionBrowser>().TicketStore);
        services.AddAuthentication();
        services.AddTransient<ServerSessionCookieHandler>();
        services.AddTransient<ServerSessionIssueCookieHandler>();
        services.Configure<AuthenticationOptions>(value =>
        {
            try
            {
                if (value.Schemes.Any(scheme => scheme.Name is ServerSessionSchemes.Session or ServerSessionSchemes.Issue))
                    throw ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure);
                value.AddScheme(ServerSessionSchemes.Session, scheme => scheme.HandlerType = typeof(ServerSessionCookieHandler));
                value.AddScheme(ServerSessionSchemes.Issue, scheme => scheme.HandlerType = typeof(ServerSessionIssueCookieHandler));
            }
            catch (ServerSessionIdentityException) { throw; }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
            {
                throw ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure);
            }
        });
        services.AddOptions<CookieAuthenticationOptions>(ServerSessionSchemes.Session)
            .Configure<ServerSessionBrowser>((value, browser) => browser.ConfigureCookie(value, issue: false));
        services.AddOptions<CookieAuthenticationOptions>(ServerSessionSchemes.Issue)
            .Configure<ServerSessionBrowser>((value, browser) => browser.ConfigureCookie(value, issue: true));
        return services;
    }
}

public sealed partial class ServerSessionBrowser
{
    internal void ConfigureCookie(CookieAuthenticationOptions value, bool issue)
    {
        value.Cookie.Name = ServerSessionBrowserCookies.SessionName;
        value.Cookie.HttpOnly = true;
        value.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        value.Cookie.SameSite = SameSiteMode.Lax;
        value.Cookie.Domain = null;
        value.Cookie.Path = "/";
        value.Cookie.Expiration = null;
        value.Cookie.MaxAge = null;
        value.Cookie.IsEssential = true;
        value.ClaimsIssuer = ServerSessionSchemes.Session;
        value.SessionStore = TicketStore;
        value.TicketDataFormat = CarrierFormat;
        value.CookieManager = issue ? IssueCookieManager : MainCookieManager;
        value.Events = CookieEvents;
        value.EventsType = null;
        value.SlidingExpiration = false;
        value.ExpireTimeSpan = Braid.Application.ServerSessionPolicy.AbsoluteWindow;
        value.TimeProvider = TimeProvider.System;
        value.ForwardDefault = null;
        value.ForwardDefaultSelector = null;
        value.ForwardAuthenticate = null;
        value.ForwardChallenge = null;
        value.ForwardForbid = null;
        value.ForwardSignIn = null;
        value.ForwardSignOut = null;
    }

    internal void ValidateCookieConfiguration(CookieAuthenticationOptions value, bool issue)
    {
        if (value.Cookie.Name != ServerSessionBrowserCookies.SessionName || !value.Cookie.HttpOnly ||
            value.Cookie.SecurePolicy != CookieSecurePolicy.Always || value.Cookie.SameSite != SameSiteMode.Lax ||
            value.Cookie.Domain is not null || value.Cookie.Path != "/" || value.Cookie.Expiration is not null ||
            value.Cookie.MaxAge is not null || !value.Cookie.IsEssential || value.ClaimsIssuer != ServerSessionSchemes.Session ||
            !ReferenceEquals(value.SessionStore, TicketStore) || !ReferenceEquals(value.TicketDataFormat, CarrierFormat) ||
            !ReferenceEquals(value.CookieManager, issue ? IssueCookieManager : MainCookieManager) ||
            !ReferenceEquals(value.Events, CookieEvents) || value.EventsType is not null || value.SlidingExpiration ||
            value.ExpireTimeSpan != Braid.Application.ServerSessionPolicy.AbsoluteWindow ||
            !ReferenceEquals(value.TimeProvider, TimeProvider.System) ||
            value.ForwardDefault is not null || value.ForwardDefaultSelector is not null ||
            value.ForwardAuthenticate is not null || value.ForwardChallenge is not null ||
            value.ForwardForbid is not null || value.ForwardSignIn is not null || value.ForwardSignOut is not null)
            throw ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure);
    }
}
