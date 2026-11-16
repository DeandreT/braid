using System.Security.Cryptography;
using System.Text;

namespace Braid.Persistence.Postgres;

// Internal injection is for migration failure and future-version qualification, never client SQL.
internal sealed record MigrationScript
{
    internal MigrationScript(int version, string sql)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        Version = version;
        Sql = sql;
        Checksum = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));
    }

    internal int Version { get; }
    internal string Sql { get; }
    internal string Checksum { get; }
}
