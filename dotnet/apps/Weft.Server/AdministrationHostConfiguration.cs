using Microsoft.Extensions.DependencyInjection;
using Weft.Application;
using Weft.Persistence.Postgres;

namespace Weft.Server;

internal static class AdministrationHostConfiguration
{
    internal static void Configure(IServiceCollection services, Func<string, string?> read)
    {
        const string name = "WEFT_ADMINISTRATION_CONNECTION_STRING";
        if (read(name + "_FILE") is null)
        {
            if (read(name) is not null) throw ServerHostOptions.Failure();
            return;
        }
        var connection = SessionHostResources.ReadProtectedConnection(read, name);
        services.AddSingleton(_ => new PostgresAdministrationStore(connection));
        services.AddSingleton<IAdministrationStore>(p => p.GetRequiredService<PostgresAdministrationStore>());
        services.AddSingleton<IOrganizationInvitationRedeemer>(p => p.GetRequiredService<PostgresAdministrationStore>());
    }
}
