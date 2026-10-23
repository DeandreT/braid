using Azure.Identity;
using Weft.MappingBackends.Workers;

namespace Weft.MappingBackends.AzureSessions;

/// <summary>
/// The dynamic-sessions runner scalars: the pool management endpoint, the expected worker build identity
/// (<c>WEFT_FERRULE_WORKER_IMAGE</c>) and an optional user-assigned managed identity. No secret is configured; the host's
/// managed identity needs the Azure ContainerApps Session Executor role on the pool.
/// </summary>
public sealed record AzureSessionsRunnerSettings(Uri PoolManagementEndpoint, string BuildIdentity, Guid? ManagedIdentityClientId,
    string? CompileToolchainIdentity = null)
{
    public string ProfileIdentity => CompileToolchainIdentity is { } toolchain ? CompileWorkerIdentity.Combine(BuildIdentity, toolchain) : BuildIdentity;
    public const string PoolEndpoint = "WEFT_FERRULE_SESSIONS_POOL_ENDPOINT";
    public const string ClientId = "WEFT_FERRULE_SESSIONS_MANAGED_IDENTITY_CLIENT_ID";
    public const string Identity = "WEFT_FERRULE_WORKER_IMAGE";

    public static AzureSessionsRunnerSettings Read(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        try
        {
            string Required(string name) => read(name) is { Length: > 0 } value ? value : throw Failure();
            var endpoint = AzureSessionsConnectorOptions.RequireEndpoint(new Uri(Required(PoolEndpoint), UriKind.Absolute));
            Guid? clientId = read(ClientId) switch
            {
                null => null,
                var text when Guid.TryParseExact(text, "D", out var id) && id != Guid.Empty && text == id.ToString("D") => id,
                _ => throw Failure()
            };
            return new(endpoint, InspectionTransport.RequireImage(Required(Identity)), clientId,
                FerruleRunnerSelection.ReadCompileToolchain(read, FerruleRunnerKind.AzureContainerAppsSessions));
        }
        catch (Exception error) when (error is ArgumentException or UriFormatException) { throw Failure(); }
    }

    /// <summary>A sessions scalar beside another runner selection is an ambiguous configuration.</summary>
    public static bool Configured(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        return read(PoolEndpoint) is not null || read(ClientId) is not null;
    }

    /// <summary>The connector with the host's managed identity; its owner disposes it after the runner has stopped.</summary>
    public AzureSessionsConnector CreateConnector() => new(new(PoolManagementEndpoint, ManagedIdentityClientId is { } id
        ? new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(id.ToString("D")))
        : new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned)));

    private static InvalidOperationException Failure() => new("The dynamic-sessions runner configuration is invalid.");

    public override string ToString() => nameof(AzureSessionsRunnerSettings);
}
