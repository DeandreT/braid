using Weft.Persistence.Postgres;

namespace Weft.Tests;

internal static class CatalogSearchUpgradeTestSupport
{
    // A conservation snapshot may exclude the additive search objects only after
    // their complete installed definitions have passed the runtime admission.
    internal static async Task<bool> AdmitAsync(PostgresCase test)
    {
        await using var connection = await test.Owner.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        return await CatalogSearchSchemaAdmission.ValidateAsync(connection, transaction, default);
    }
}
