using System.Security.Cryptography;
using System.Text;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using Weft.Cli.Configuration;

namespace Weft.Cli.Authentication;

/// <summary>Binds an MSAL token cache to durable storage, or refuses.</summary>
internal interface ITokenCacheStore
{
    /// <summary>
    /// Attaches the profile's protected cache to <paramref name="cache"/>. Throws a
    /// <see cref="CliExitCode.ProtectedStoreUnavailable"/> failure when no protected store is usable;
    /// it never falls back to an unprotected file.
    /// </summary>
    Task AttachAsync(CliProfile profile, ITokenCache cache, CancellationToken cancellationToken);
}

/// <summary>
/// One operating-system protected cache per profile: DPAPI (current user) on Windows, the Keychain on macOS and
/// the Secret Service keyring through libsecret on Linux. Persistence is verified before first use.
/// </summary>
internal sealed class MsalProtectedTokenCacheStore(string directory) : ITokenCacheStore
{
    private const string KeyringSchema = "io.weft.cli.tokens";
    private const string KeychainService = "io.weft.cli.tokens";

    public async Task AttachAsync(CliProfile profile, ITokenCache cache, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(cache);
        OwnerOnlyFiles.EnsureDirectory(directory);
        // Keychain and keyring entries are per user, so they also name the configuration directory they belong to.
        var store = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(directory))))[..16];
        var properties = new StorageCreationPropertiesBuilder("tokens-" + profile.Name + ".cache", directory)
            .WithMacKeyChain(KeychainService, "profile-" + profile.Name + "-" + store)
            .WithLinuxKeyring(KeyringSchema, MsalCacheHelper.LinuxKeyRingDefaultCollection, "Weft CLI sign-in (" + profile.Name + ")",
                new KeyValuePair<string, string>("Store", store), new KeyValuePair<string, string>("Profile", profile.Name))
            .Build();
        MsalCacheHelper helper;
        try
        {
            helper = await MsalCacheHelper.CreateAsync(properties).WaitAsync(cancellationToken);
            helper.VerifyPersistence();
        }
        catch (Exception error) when (error is MsalCachePersistenceException or PlatformNotSupportedException or
            DllNotFoundException or TypeInitializationException or EntryPointNotFoundException)
        {
            throw Unavailable();
        }
        helper.RegisterCache(cache);
    }

    internal static CliException Unavailable() => new(CliExitCode.ProtectedStoreUnavailable,
        "No operating-system protected token store is available (DPAPI, Keychain or a Secret Service keyring through libsecret). " +
        "The Weft CLI does not store sign-in tokens unprotected, so it cannot sign in here.", "protectedStoreUnavailable");
}
