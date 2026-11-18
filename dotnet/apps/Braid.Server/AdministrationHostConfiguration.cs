using Microsoft.Extensions.DependencyInjection;
using Braid.Application;
using Braid.Application.Lifecycle;
using Braid.Application.Retention;
using Braid.Persistence.Postgres;

namespace Braid.Server;

internal static class AdministrationHostConfiguration
{
    internal static void Configure(IServiceCollection services, Func<string, string?> read)
    {
        const string name = "BRAID_ADMINISTRATION_CONNECTION_STRING";
        if (read(name + "_FILE") is null)
        {
            if (read(name) is not null) throw ServerHostOptions.Failure();
            return;
        }
        var connection = SessionHostResources.ReadProtectedConnection(read, name);
        services.AddSingleton(_ => new PostgresAdministrationStore(connection));
        services.AddSingleton<IAdministrationStore>(p => p.GetRequiredService<PostgresAdministrationStore>());
        services.AddSingleton<IAuditLogReader>(p => p.GetRequiredService<PostgresAdministrationStore>());
        services.AddSingleton<IAiUsageReader>(p => p.GetRequiredService<PostgresAdministrationStore>());
        services.AddSingleton<IAiQuotaStore>(p => p.GetRequiredService<PostgresAdministrationStore>());
        services.AddSingleton<IResourceLifecycleStore>(p => p.GetRequiredService<PostgresAdministrationStore>());
        services.AddSingleton<IResourceDeletionStore>(p => p.GetRequiredService<PostgresAdministrationStore>());
        services.AddSingleton<IRetentionPolicyStore>(p => p.GetRequiredService<PostgresAdministrationStore>());
        services.AddSingleton<IResourcePurgeStore>(p => p.GetRequiredService<PostgresAdministrationStore>());
        services.AddSingleton<ITargetRetirementStore>(p => p.GetRequiredService<PostgresAdministrationStore>());
        services.AddSingleton<IOrganizationInvitationRedeemer>(p => p.GetRequiredService<PostgresAdministrationStore>());
    }
}
