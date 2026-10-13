using System.Data.Common;
using Npgsql;

namespace Weft.Persistence.Postgres;

internal static class PostgresConfiguration
{
    internal static string Normalize(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var configuration = new NpgsqlConnectionStringBuilder(connectionString);
        // Npgsql's ContainsKey reports supported keywords, including absent defaults.
        var supplied = new DbConnectionStringBuilder { ConnectionString = connectionString };
        if (configuration.NoResetOnClose || configuration.Multiplexing ||
            (supplied.ContainsKey("Enlist") && configuration.Enlist))
            throw new ArgumentException(
                "Weft connections require pooled-state reset, explicit transactions, and disabled multiplexing and ambient enlistment.",
                nameof(connectionString));

        configuration.NoResetOnClose = false;
        configuration.Multiplexing = false;
        configuration.Enlist = false;
        return configuration.ConnectionString;
    }
}
