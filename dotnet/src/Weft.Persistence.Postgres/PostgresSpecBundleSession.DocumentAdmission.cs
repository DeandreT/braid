using Weft.Application;

namespace Weft.Persistence.Postgres;

public sealed partial class PostgresSpecBundleSession
{
    /// <summary>Fresh actor-bound schema/store admission. RunAsync validates SpecSchemaAdmission, including
    /// DocumentEvidenceSchemaAdmission's actual migration 46 checks, in this same transaction.</summary>
    internal Task<bool> AdmitDocumentEvidenceAsync(CancellationToken token)
    {
        if (actor is null) throw new SpecStorageException(SpecStorageError.NotAdmitted);
        return database.RunAsync(false, async (connection, transaction) =>
        {
            await ValidateBinding(connection, transaction, token).ConfigureAwait(false);
            return true;
        }, token);
    }
}
