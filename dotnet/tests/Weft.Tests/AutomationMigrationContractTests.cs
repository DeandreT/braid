using Weft.Persistence.Postgres;

namespace Weft.Tests;

public sealed class AutomationMigrationContractTests
{
    [Fact]
    public void Automation_migration_retains_its_exact_frozen_sql_checksum()
    {
        var migration = Assert.Single(MigrationRunner.EmbeddedMigrations, script => script.Version == 30);
        Assert.Equal(RegressionSchemaTestContract.AutomationChecksum, migration.Checksum);
    }
}
