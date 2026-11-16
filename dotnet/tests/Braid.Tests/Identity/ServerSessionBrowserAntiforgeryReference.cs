using System.Runtime.Versioning;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Braid.Tests;

/// <summary>
/// Counterfactual STANDARD FRAMEWORK validation, not Braid admission. Same cookie,
/// token, admitted key ring, application and canonical owner isolate the epoch
/// AdditionalData gate. No token serializer/parser or product-internal bridge.
/// </summary>
[SupportedOSPlatform("linux")]
internal static class ServerSessionBrowserAntiforgeryReference
{
    internal static async Task AcceptsSameCookieAndOwnerBeforeObservingOldEpochAsync(
        BrowserHttpsFixture test, BrowserHttpsHost host, BrowserCookies cookies,
        string oldToken, ClaimsPrincipal freshCanonicalUser, Guid oldEpoch,
        X509Certificate2? currentCertificate = null, IReadOnlyCollection<X509Certificate2>? overlaps = null)
    {
        ServiceProvider? keys = null, reference = null;
        Exception? primary = null;
        var cleanup = new List<Exception>();
        try
        {
            var additional = new ObservingAdditionalData("braid:csrf:1:" + test.Data.Organization.ToString("D") + ":" + oldEpoch.ToString("D"));
            var keyServices = new ServiceCollection();
            keyServices.AddLogging(logging => logging.ClearProviders());
            var current = currentCertificate ?? test.Files.Certificate;
            keyServices.AddDataProtection().SetApplicationName(test.Files.ApplicationName)
                .PersistKeysToFileSystem(new DirectoryInfo(test.Files.Keys))
                .ProtectKeysWithCertificate(current)
                .UnprotectKeysWithAnyCertificate((overlaps ?? []).Prepend(current).ToArray())
                .DisableAutomaticKeyGeneration();
            keys = keyServices.BuildServiceProvider(validateScopes: true);
            var standard = keys.GetRequiredService<IDataProtectionProvider>();
            var services = new ServiceCollection();
            services.AddLogging(logging => logging.ClearProviders());
            services.AddAntiforgery(options =>
            {
                options.Cookie.Name = BrowserHttpsFixture.AntiforgeryCookie;
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.Path = "/";
                options.Cookie.Domain = null;
                options.HeaderName = BrowserHttpsFixture.TokenHeader;
                options.SuppressReadingTokenFromFormBody = true;
            });
            services.AddSingleton<IDataProtectionProvider>(new FixedFrameworkPurposeProvider(standard, test.Data.Organization));
            services.AddSingleton<IAntiforgeryAdditionalDataProvider>(additional);
            reference = services.BuildServiceProvider(validateScopes: true);
            var context = new DefaultHttpContext
            {
                RequestServices = reference,
                User = new ClaimsPrincipal(freshCanonicalUser.Identities.Select(identity => new ClaimsIdentity(identity)))
            };
            context.Request.Method = "POST";
            context.Request.Scheme = "https";
            context.Request.Host = new HostString(host.Origin.Host, host.Origin.Port);
            context.Request.Headers.Origin = host.Origin.GetLeftPart(UriPartial.Authority);
            context.Request.Headers.Cookie = BrowserHttpsFixture.AntiforgeryCookie + "=" + cookies.Antiforgery;
            context.Request.Headers[BrowserHttpsFixture.TokenHeader] = oldToken;
            context.Response.Body = Stream.Null;
            // Pinned ASP.NET Core10.0.12 DefaultAntiforgeryTokenGenerator validates
            // security token and ClaimUid BEFORE calling ValidateAdditionalData.
            // Successful reference validation plus this observed exact OLD data
            // isolates the product's epoch comparison; reference always permits it.
            await reference.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context);
            Assert.True(additional.Calls == 1 && additional.MatchedOldData,
                "The standard framework reference did not reach the exact old-epoch AdditionalData check.");
        }
        catch (Exception error) { primary = BrowserHttpsFixture.Safe(error, "The independent standard AF reference failed."); }
        finally
        {
            if (reference is not null)
                await BrowserHttpsFixture.CleanupAsync(reference.DisposeAsync, "The owned AF reference provider did not settle.", cleanup);
            if (keys is not null)
                await BrowserHttpsFixture.CleanupAsync(keys.DisposeAsync, "The owned AF reference key provider did not settle.", cleanup);
        }
        BrowserHttpsFixture.Throw(primary, cleanup);
    }

    private sealed class FixedFrameworkPurposeProvider(IDataProtectionProvider standard, Guid organization) : IDataProtectionProvider
    {
        public IDataProtector CreateProtector(string purpose)
        {
            Assert.True(string.Equals(purpose, "Microsoft.AspNetCore.Antiforgery.AntiforgeryToken.v1", StringComparison.Ordinal),
                "The standard AF reference requested an unexpected purpose.");
            return standard.CreateProtector("Braid.ServerSessionAntiforgery", "csrf:1", "policy:1",
                "configured-organization:" + organization.ToString("D"), purpose);
        }
    }

    private sealed class ObservingAdditionalData(string oldData) : IAntiforgeryAdditionalDataProvider
    {
        internal int Calls { get; private set; }
        internal bool MatchedOldData { get; private set; }
        public string GetAdditionalData(HttpContext context) => throw new InvalidOperationException("The reference must not issue a token.");
        public bool ValidateAdditionalData(HttpContext context, string additionalData)
        {
            Calls++;
            MatchedOldData = string.Equals(oldData, additionalData, StringComparison.Ordinal);
            return true; // Deliberate reference acceptance, never product authority.
        }
    }
}
