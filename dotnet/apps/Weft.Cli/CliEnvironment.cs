using Microsoft.Identity.Client;
using Weft.Cli.Authentication;

namespace Weft.Cli;

/// <summary>
/// Everything a command touches outside the process: streams, the configuration directory, the protected token
/// store and the network. Production uses <see cref="CreateDefault"/>; tests replace the transports in process.
/// Nothing here can supply a token or change a profile's authority.
/// </summary>
internal sealed class CliEnvironment
{
    public required TextWriter Out { get; init; }
    public required TextWriter Error { get; init; }
    public required Func<Stream> OpenStandardInput { get; init; }
    public required Func<Stream> OpenStandardOutput { get; init; }
    public Func<string, Stream> OpenInputFile { get; init; } = path =>
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
    public required string ConfigurationDirectory { get; init; }
    public required ITokenCacheStore TokenCaches { get; init; }

    /// <summary>The handler for Weft API requests; null uses a default handler with system trust.</summary>
    public HttpMessageHandler? ApiHandler { get; init; }

    /// <summary>The HTTP client factory MSAL uses; null keeps MSAL's own.</summary>
    public IMsalHttpClientFactory? IdentityHttp { get; init; }

    /// <summary>Opens the system browser for <c>weft login --browser</c>; null keeps MSAL's platform default.</summary>
    public Func<Uri, Task>? OpenBrowser { get; init; }

    /// <summary>Set only by test configurations whose issuer does not serve Entra instance discovery.</summary>
    public bool DisableInstanceDiscovery { get; init; }

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Selects a profile when no <c>--profile</c> is given. Never a source of tokens.</summary>
    public string? ProfileVariable { get; init; }

    public static CliEnvironment CreateDefault()
    {
        var directory = DefaultDirectory();
        return new CliEnvironment
        {
            Out = Console.Out,
            Error = Console.Error,
            OpenStandardInput = Console.OpenStandardInput,
            OpenStandardOutput = Console.OpenStandardOutput,
            ConfigurationDirectory = directory,
            TokenCaches = new MsalProtectedTokenCacheStore(directory),
            ProfileVariable = Environment.GetEnvironmentVariable("WEFT_PROFILE") is { Length: > 0 } name ? name : null
        };
    }

    /// <summary><c>WEFT_CONFIG_DIR</c>, or the per-user application configuration directory.</summary>
    internal static string DefaultDirectory()
    {
        if (Environment.GetEnvironmentVariable("WEFT_CONFIG_DIR") is { Length: > 0 } configured)
            return Path.GetFullPath(configured);
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData,
                Environment.SpecialFolderOption.DoNotVerify), "Weft");
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var root = xdg is { Length: > 0 } && Path.IsPathRooted(xdg)
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(root, "weft");
    }
}
