using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Identity.Client;
using Weft.Cli.Configuration;

namespace Weft.Cli.Authentication;

/// <summary>Supplies the signed-in user's access token for API requests.</summary>
internal interface IAccessTokenSource
{
    /// <summary>A current access token from the protected cache, refreshed silently when needed. Never logged or printed.</summary>
    Task<string> GetAccessTokenAsync(bool forceRefresh, CancellationToken cancellationToken);
}

/// <summary>The signed-in account as the local cache knows it. Holds no token.</summary>
internal sealed record CliAccount(string Username, string TenantId);

/// <summary>
/// MSAL public-client sign-in for one profile. The authority is always the Microsoft public cloud for the profile's
/// tenant; only the delegated Weft API scope is requested. Tokens stay inside MSAL and its protected cache.
/// </summary>
internal sealed partial class CliSignIn(CliProfile profile, CliEnvironment environment) : IAccessTokenSource
{
    private IPublicClientApplication? _application;

    private string[] Scopes => [profile.Scope];

    public async Task<CliAccount> LoginWithDeviceCodeAsync(CancellationToken cancellationToken)
    {
        var application = await ApplicationAsync(cancellationToken);
        var result = await Run(() => application.AcquireTokenWithDeviceCode(Scopes, code =>
        {
            // Only the verification address and user code are shown; the device code itself stays with MSAL.
            environment.Error.WriteLine($"To sign in, open {code.VerificationUrl} and enter the code {code.UserCode}");
            environment.Error.Flush();
            return Task.CompletedTask;
        }).ExecuteAsync(cancellationToken), cancellationToken);
        return await KeepOnlyAsync(application, result.Account);
    }

    public async Task<CliAccount> LoginWithBrowserAsync(CancellationToken cancellationToken)
    {
        var application = await ApplicationAsync(cancellationToken);
        var request = application.AcquireTokenInteractive(Scopes)
            .WithUseEmbeddedWebView(false)
            .WithPrompt(Prompt.SelectAccount);
        if (environment.OpenBrowser is { } open)
            request = request.WithSystemWebViewOptions(new SystemWebViewOptions { OpenBrowserAsync = open });
        var result = await Run(() => request.ExecuteAsync(cancellationToken), cancellationToken);
        return await KeepOnlyAsync(application, result.Account);
    }

    public async Task<string> GetAccessTokenAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        var application = await ApplicationAsync(cancellationToken);
        var account = await SingleAccountAsync(application);
        try
        {
            var result = await application.AcquireTokenSilent(Scopes, account).WithForceRefresh(forceRefresh)
                .ExecuteAsync(cancellationToken);
            return result.AccessToken;
        }
        catch (MsalUiRequiredException)
        {
            throw SignInRequired();
        }
        catch (Exception error) when (error is MsalException)
        {
            throw Map(error, cancellationToken);
        }
    }

    public async Task<CliAccount?> AccountAsync(CancellationToken cancellationToken)
    {
        var application = await ApplicationAsync(cancellationToken);
        var accounts = (await application.GetAccountsAsync()).ToArray();
        return accounts.Length == 1 ? Describe(accounts[0]) : null;
    }

    /// <summary>Removes every account of this profile, and with it each account's tokens, from the protected cache.</summary>
    public async Task<int> LogoutAsync(CancellationToken cancellationToken)
    {
        var application = await ApplicationAsync(cancellationToken);
        var accounts = (await application.GetAccountsAsync()).ToArray();
        foreach (var account in accounts)
            await application.RemoveAsync(account);
        return accounts.Length;
    }

    private async Task<IPublicClientApplication> ApplicationAsync(CancellationToken cancellationToken)
    {
        if (_application is not null)
            return _application;
        var builder = PublicClientApplicationBuilder.Create(profile.ClientId.ToString("D"))
            .WithAuthority(AzureCloudInstance.AzurePublic, profile.TenantId)
            .WithRedirectUri("http://localhost")
            .WithClientName("weft-cli")
            .WithClientVersion(Version);
        if (environment.IdentityHttp is { } http)
            builder = builder.WithHttpClientFactory(http);
        if (environment.DisableInstanceDiscovery)
            builder = builder.WithInstanceDiscovery(false);
        var application = builder.Build();
        await environment.TokenCaches.AttachAsync(profile, application.UserTokenCache, cancellationToken);
        return _application = application;
    }

    private static async Task<IAccount> SingleAccountAsync(IPublicClientApplication application)
    {
        var accounts = (await application.GetAccountsAsync()).ToArray();
        return accounts.Length == 1 ? accounts[0] : throw SignInRequired();
    }

    // One account per profile: a new sign-in replaces any earlier account of the profile.
    private static async Task<CliAccount> KeepOnlyAsync(IPublicClientApplication application, IAccount signedIn)
    {
        foreach (var account in await application.GetAccountsAsync())
            if (account.HomeAccountId?.Identifier != signedIn.HomeAccountId?.Identifier)
                await application.RemoveAsync(account);
        return Describe(signedIn);
    }

    private static CliAccount Describe(IAccount account) =>
        new(account.Username ?? "", account.HomeAccountId?.TenantId ?? "");

    private static async Task<AuthenticationResult> Run(Func<Task<AuthenticationResult>> action, CancellationToken cancellationToken)
    {
        try
        {
            return await action();
        }
        catch (Exception error) when (error is MsalException)
        {
            throw Map(error, cancellationToken);
        }
    }

    // MSAL messages can carry correlation details; only a closed protocol error code is ever shown.
    private static CliException Map(Exception error, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || error is MsalClientException { ErrorCode: MsalError.AuthenticationCanceledError })
            return new CliException(CliExitCode.Canceled, "Sign-in was canceled.", "canceled");
        var code = error is MsalException { ErrorCode: { } value } && ErrorCodePattern().IsMatch(value) ? value : "unknown";
        if (error.InnerException is HttpRequestException or TaskCanceledException ||
            code is MsalError.RequestTimeout or MsalError.ServiceNotAvailable)
            return new CliException(CliExitCode.Transport, $"The identity provider could not be reached ({code}).", "transport");
        return error is MsalServiceException
            ? new CliException(CliExitCode.SignInFailed, $"The identity provider refused the sign-in ({code}).", "signInFailed")
            : new CliException(CliExitCode.SignInFailed, $"Sign-in failed ({code}).", "signInFailed");
    }

    internal static CliException SignInRequired() =>
        new(CliExitCode.SignInRequired, "You are not signed in for this profile. Run: weft login", "signInRequired");

    private static string Version { get; } =
        typeof(CliSignIn).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    [GeneratedRegex("^[a-z][a-z0-9_]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ErrorCodePattern();
}
