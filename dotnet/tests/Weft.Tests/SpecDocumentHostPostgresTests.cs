using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
public sealed class SpecDocumentHostPostgresTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task Actor_bound_admission_checks_actual_46_and_store_each_time()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var storeId = Guid.NewGuid();
        var binding = new SpecStoreBinding(storeId, SpecStoreKind.FileSystem, Sha256Digest.FromBytes(storeId.ToByteArray()));
        await test.Case.ExecuteAsync("INSERT INTO weft.spec_stores VALUES($1,'filesystem',$2,true)", storeId, binding.LocationDigest.Value);
        await using var database = new PostgresSpecBundleDatabase(test.Case.RegressionConnectionString("weft_spec_reader"), SpecDatabaseProfile.Reader);
        var session = database.ForActor(test.Actor, binding);
        Assert.True(await session.AdmitDocumentEvidenceAsync(default));
        await test.Case.ExecuteAsync("UPDATE weft.spec_stores SET active=false WHERE id=$1", storeId);
        await Assert.ThrowsAsync<SpecStorageException>(() => session.AdmitDocumentEvidenceAsync(default));
        await test.Case.ExecuteAsync("UPDATE weft.spec_stores SET active=true WHERE id=$1", storeId);
        Assert.True(await session.AdmitDocumentEvidenceAsync(default));
        await test.Case.ExecuteAsync("UPDATE weft.schema_migrations SET checksum=$1 WHERE version=46", new string('0', 64));
        await Assert.ThrowsAsync<SpecStorageException>(() => session.AdmitDocumentEvidenceAsync(default));
    }
}
