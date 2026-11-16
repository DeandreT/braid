using Npgsql;
using NpgsqlTypes;
using Braid.Application;
using Braid.Domain;

namespace Braid.Persistence.Postgres;

/// <summary>Durable cross-turn discovery through the restricted spec reader. Requires the 0046 owner functions and
/// updated exact SpecSchemaAdmission. No fallback when the functions/schema/role are unavailable.</summary>
public sealed class PostgresCreationDiscovery : ICreationDiscovery
{
    private readonly PostgresSpecBundleDatabase database;
    private readonly SpecStoreBinding store;
    public PostgresCreationDiscovery(PostgresSpecBundleDatabase database, SpecStoreBinding store)
    {
        if (database.Profile != SpecDatabaseProfile.Reader) throw new ArgumentException("Discovery requires the restricted reader profile.");
        this.database = database; this.store = store;
    }

    public Task<IReadOnlyList<CreationSelectionReference>> SelectionsAsync(ActorContext actor, ProjectScope project, Guid? afterBundle,
        Guid? afterObject, int limit, CancellationToken ct)
    {
        Bounds(limit); if ((afterBundle is null) != (afterObject is null)) throw new ArgumentException("Incomplete cursor.");
        return database.RunAsync<IReadOnlyList<CreationSelectionReference>>(false, async (connection, transaction) =>
        {
            await Store(connection, transaction, ct).ConfigureAwait(false);
            using var command = new NpgsqlCommand("SELECT bundle_id,extraction_id FROM braid.spec_selection_discover_v1($1,$2,$3,$4,$5,$6,$7)", connection, transaction);
            Actor(command, actor, project); Id(command, store.StoreId); Id(command, afterBundle); Id(command, afterObject); command.Parameters.AddWithValue(limit);
            await using var rows = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var result = new List<CreationSelectionReference>(); var previous = afterBundle is null ? null : Key(afterBundle.Value, afterObject!.Value);
            while (await rows.ReadAsync(ct).ConfigureAwait(false))
            {
                var bundle = rows.GetGuid(0); var extraction = rows.GetGuid(1); var key = Key(bundle, extraction);
                if (bundle == Guid.Empty || extraction == Guid.Empty || result.Count == limit || previous is not null && string.CompareOrdinal(key, previous) <= 0) throw Invalid();
                result.Add(new(new(project, bundle), extraction)); previous = key;
            }
            return result.AsReadOnly();
        }, ct);
    }

    public Task<IReadOnlyList<CreationProposalReference>> ProposalsAsync(ActorContext actor, ProjectScope project, Guid? afterProposal,
        int limit, CancellationToken ct)
    {
        Bounds(limit);
        return database.RunAsync<IReadOnlyList<CreationProposalReference>>(false, async (connection, transaction) =>
        {
            // Retained review survives source-store retirement or relocation. The owner function
            // still authorizes current actor/project content access and filters by creator.
            using var command = new NpgsqlCommand("SELECT proposal_id,fingerprint FROM braid.creation_proposals_discover_v1($1,$2,$3,$4,$5)", connection, transaction);
            Actor(command, actor, project); Id(command, afterProposal); command.Parameters.AddWithValue(limit);
            await using var rows = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var result = new List<CreationProposalReference>(); var previous = afterProposal?.ToString("D");
            while (await rows.ReadAsync(ct).ConfigureAwait(false))
            {
                var id = rows.GetGuid(0); var fingerprint = rows.GetString(1); var digest = Sha256Digest.Parse(fingerprint);
                if (id == Guid.Empty || digest.Value != fingerprint || result.Count == limit || previous is not null && string.CompareOrdinal(id.ToString("D"), previous) <= 0) throw Invalid();
                result.Add(new(project, id, digest)); previous = id.ToString("D");
            }
            return result.AsReadOnly();
        }, ct);
    }
    private async Task Store(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        using var command = new NpgsqlCommand("SELECT braid.spec_store_admit($1,$2,$3)", connection, transaction);
        command.Parameters.AddWithValue(store.StoreId); command.Parameters.AddWithValue(store.Kind == SpecStoreKind.Blob ? "blob" : "filesystem");
        command.Parameters.AddWithValue(store.LocationDigest.Value);
        if (await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not true) throw Invalid();
    }
    private static void Actor(NpgsqlCommand command, ActorContext actor, ProjectScope project)
    { Id(command, actor.PrincipalId.Value); Id(command, project.OrganizationId); Id(command, project.ProjectId); }
    private static void Id(NpgsqlCommand command, Guid? value)
    {
        if (value == Guid.Empty) throw new ArgumentException("A nonzero cursor is required.");
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = (object?)value ?? DBNull.Value });
    }
    private static string Key(Guid bundle, Guid extraction) => bundle.ToString("D") + "/" + extraction.ToString("D");
    private static void Bounds(int limit) { if (limit is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(limit)); }
    private static SpecStorageException Invalid() => new(SpecStorageError.NotAdmitted);
}
