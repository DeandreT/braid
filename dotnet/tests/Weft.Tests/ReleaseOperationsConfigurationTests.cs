using Weft.Persistence.Postgres;
using Weft.Server;

namespace Weft.Tests;

public sealed class ReleaseOperationsConfigurationTests
{
    [Fact]
    public void Operations_follow_the_exact_preceding_migration_prefix()
    {
        var migrations = MigrationRunner.EmbeddedMigrations;
        Assert.Equal(33, migrations.Count);
        Assert.Equal(Enumerable.Range(1, 33), migrations.Select(migration => migration.Version));
        Assert.Equal(RegressionSchemaTestContract.AccountDisplayChecksum, migrations[25].Checksum);
        Assert.Equal(RegressionSchemaTestContract.AdministrationChecksum, migrations[26].Checksum);
        Assert.Equal(InterchangeSchemaTestContract.MigrationChecksum, migrations[27].Checksum);
        Assert.Equal(CatalogSearchSchemaAdmission.MigrationChecksum, migrations[28].Checksum);
        Assert.Equal(RegressionSchemaTestContract.AutomationChecksum, migrations[29].Checksum);
        Assert.Equal(RegressionSchemaTestContract.ReleaseOperationsChecksum, migrations[30].Checksum);
        Assert.Equal(ChatSchemaContract.WorkspaceMigrationChecksum, migrations[31].Checksum);
        Assert.Equal(PreviewSchemaTestContract.ReadsMigrationChecksum, migrations[32].Checksum);
        Assert.Contains("CREATE TABLE weft.account_display_metadata", migrations[25].Sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE weft.release_target_operations", migrations[30].Sql, StringComparison.Ordinal);
        Assert.Equal(ReleaseOperationsRoleGuard.Names.Order(StringComparer.Ordinal),
            BuildRoleGuard.LoadFunctions("0031_release_operations.sql", ReleaseOperationsRoleGuard.Names.Length).Keys.Order(StringComparer.Ordinal));
        Assert.Equal(AutomationSchemaAdmission.Functions.Order(StringComparer.Ordinal),
            BuildRoleGuard.LoadFunctions("0030_regression_automation.sql", AutomationSchemaAdmission.Functions.Length).Keys.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("false", false)]
    [InlineData("true", true)]
    public void Enablement_is_explicit(string? setting, bool expected) =>
        Assert.Equal(expected, ReleaseOperationsHostConfiguration.ReadEnabled(_ => setting, true));

    [Theory]
    [InlineData("TRUE")]
    [InlineData("1")]
    [InlineData("")]
    public void Invalid_enablement_is_refused(string setting) =>
        Assert.Throws<ArgumentException>(() => ReleaseOperationsHostConfiguration.ReadEnabled(_ => setting, true));

    [Fact]
    public void Operations_cannot_bypass_disabled_catalog() =>
        Assert.Throws<ArgumentException>(() => ReleaseOperationsHostConfiguration.ReadEnabled(_ => "true", false));
}
