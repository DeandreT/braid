using Microsoft.Extensions.DependencyInjection;
using Braid.Api.Catalog;
using Braid.Deployment;
using Braid.Persistence.Postgres;

namespace Braid.Server;

internal static class ReleaseOperationsHostConfiguration
{
    internal static bool ReadEnabled(Func<string, string?> read, bool catalogEnabled)
    {
        var enabled = read("BRAID_RELEASE_OPERATIONS_ENABLED") switch
        {
            null or "false" => false,
            "true" => true,
            _ => throw new ArgumentException("Invalid release operations enablement.")
        };
        if (enabled && !catalogEnabled) throw new ArgumentException("Release operations requires an enabled catalog.");
        return enabled;
    }
    internal static void Configure(IServiceCollection services, Func<string, string?> read)
    {
        if (!ReadEnabled(read, true)) return;
        string Connection(string name) => read("BRAID_CATALOG_AUTH_MODE") == "session"
            ? SessionHostResources.ReadConnection(read, name + "_FILE")
            : read(name) is { Length: > 0 } value ? value : throw new ArgumentException($"Release operations requires {name}.");
        var reader = Connection("BRAID_RELEASE_READER_CONNECTION_STRING");
        var writer = Connection("BRAID_RELEASE_WRITER_CONNECTION_STRING");
        var qualification = Connection("BRAID_QUALIFICATION_READER_CONNECTION_STRING");
        services.AddSingleton(new ReleaseOperationsOptions(true));
        services.AddKeyedSingleton<PostgresLocalReleaseStore>("operations-reader", (_, _) => new(reader, false));
        services.AddKeyedSingleton<PostgresLocalReleaseStore>("operations-writer", (_, _) => new(writer, true));
        services.AddKeyedSingleton<PostgresBuildQualificationStore>("operations", (_, _) => new(qualification, false));
        services.AddSingleton<IReleaseOperationsStore>(provider => provider.GetRequiredKeyedService<PostgresLocalReleaseStore>("operations-reader"));
        services.AddSingleton<Braid.Deployment.Triggers.ITriggerOperationsStore>(provider => provider.GetRequiredKeyedService<PostgresLocalReleaseStore>("operations-writer"));
        services.AddSingleton<Braid.Deployment.Triggers.ITriggerAdministrationStore>(_ => new PostgresTriggerAdministrationStore(writer));
        services.AddSingleton(provider => new ReleaseOperationsService(provider.GetRequiredKeyedService<PostgresLocalReleaseStore>("operations-writer"),
            provider.GetRequiredKeyedService<PostgresLocalReleaseStore>("operations-reader"), provider.GetRequiredKeyedService<PostgresBuildQualificationStore>("operations")));
    }
}
