using Npgsql;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
public sealed class AiQuotaPersistenceTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task Quota_schema_matches_the_explicit_source_and_fingerprint()
    {
        await using var test = await fixture.CreateCaseAsync();
        await using var owner = PostgresServerSessionTestSupport.Unpooled(test.MigrationConnectionString);
        await owner.OpenAsync();
        await using var transaction = await owner.BeginTransactionAsync();
        var fingerprint = await RegressionCaseSchemaContract.CaptureAsync(owner, transaction,
            AiQuotaSchemaAdmission.Tables, default, validateAutomation: false, validateAudit: false, validateUsage: false, validateQuota: false);
        Assert.True(fingerprint == AiQuotaSchemaAdmission.Fingerprint, "Quota schema fingerprint: " + fingerprint);
        await AiQuotaSchemaAdmission.ValidateAsync(owner, transaction, default);
    }
}
