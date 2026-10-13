using Npgsql;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

public sealed class PostgresConfigurationTests
{
    [Fact]
    public void AbsentUnsafeOptionsNormalizeToExplicitSafePoolingSettings()
    {
        var normalized = new NpgsqlConnectionStringBuilder(PostgresConfiguration.Normalize("Host=localhost;Database=synthetic;Username=weft_runtime"));
        Assert.False(normalized.Enlist);
        Assert.False(normalized.Multiplexing);
        Assert.False(normalized.NoResetOnClose);
    }

    [Theory]
    [InlineData("Enlist=true")]
    [InlineData("Multiplexing=true")]
    [InlineData("No Reset On Close=true")]
    public void ExplicitUnsafeOptionsCannotConfigureRuntimeOrMigrationPools(string option)
    {
        var connection = $"Host=localhost;Database=synthetic;Username=weft_runtime;{option}";
        Assert.Throws<ArgumentException>(() => new PostgresDatabase(connection));
        Assert.Throws<ArgumentException>(() => new MigrationRunner(connection));
    }
}
