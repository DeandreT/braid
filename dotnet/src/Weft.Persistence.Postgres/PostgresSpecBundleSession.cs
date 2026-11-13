using Npgsql;
using NpgsqlTypes;
using Weft.Application;
using Weft.Domain;

namespace Weft.Persistence.Postgres;

/// <summary>Constructed only by the validated database profile; physical callbacks execute inside its database transaction.</summary>
public sealed partial class PostgresSpecBundleSession : ISpecBundleLifecycle, ISpecObjectRegistry, ISpecTemporaryRegistry, ISpecBundleInventory
{
    private readonly PostgresSpecBundleDatabase database;
    private readonly ActorContext? actor;
    private readonly SpecStoreBinding binding;
    public Guid StoreId { get; }
    internal PostgresSpecBundleSession(PostgresSpecBundleDatabase database, ActorContext? actor, SpecStoreBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        this.database = database; this.actor = actor; this.binding = binding; StoreId = binding.StoreId;
    }

    /// <summary>Ensures production composition hands the physical adapter THIS durable registry, never a caller-supplied grant.</summary>
    public async Task<SpecBundleService> CreateServiceAsync(Func<ISpecObjectRegistry, CancellationToken, Task<ISpecObjectStore>> openStore,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(openStore);
        if (!MigrationRunner.EmbeddedMigrations.Any(migration => migration.Version == 44))
            throw new SpecStorageException(SpecStorageError.Unavailable);
        await database.RunAsync(false, async (c, t) => { await ValidateBinding(c, t, token).ConfigureAwait(false); return true; }, token).ConfigureAwait(false);
        return new(this, await openStore(this, token).ConfigureAwait(false));
    }

    public Task<SpecBundleStatus> CreateAsync(SpecBundleScope scope, Guid creationKey, CancellationToken token) =>
        BundleCall("spec_bundle_create", scope, true, c => Id(c, creationKey), token);

    public Task<SpecBundleStatus> AuthorizeAsync(SpecBundleScope scope, bool write, CancellationToken token) =>
        BundleCall("spec_bundle_authorize", scope, write, c => c.Parameters.AddWithValue(write), token);

    private Task<SpecBundleStatus> BundleCall(string function, SpecBundleScope scope, bool mutation, Action<NpgsqlCommand> append, CancellationToken token) =>
        database.RunAsync(mutation, async (connection, transaction) =>
        {
            using var command = Command(function, scope, connection, transaction);
            append(command); Finish(command);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) throw Denied();
            return new SpecBundleStatus(scope, reader.GetInt64(reader.GetOrdinal("version")), reader.GetBoolean(reader.GetOrdinal("tombstoned")));
        }, token);

    public Task<SpecObjectReservation> ReserveAsync(SpecObjectReference reference, SpecSourceFormat format, Guid attemptId,
        long expectedBundleVersion, SpecExtractionIdentity? extraction, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!Enum.IsDefined(format) || expectedBundleVersion < 1 || (reference.Id.Kind == SpecObjectKind.Extraction) != (extraction is not null)
            || extraction is { } e && (e.Source != reference.Id.Source || e.Format != format || e.EvidenceDigest != reference.Digest))
            throw new ArgumentException("Reservation provenance is inconsistent.");
        return database.RunAsync(true, async (connection, transaction) =>
        {
            await ValidateBinding(connection, transaction, token).ConfigureAwait(false);
            using var command = Command("spec_reserve", reference.Id.Source.Bundle, connection, transaction);
            Id(command, reference.Id.Source.SourceId); command.Parameters.AddWithValue(reference.Id.Source.Version);
            command.Parameters.AddWithValue((int)format); Id(command, reference.Id.ObjectId); command.Parameters.AddWithValue((int)reference.Id.Kind);
            command.Parameters.AddWithValue(reference.Digest.Value); command.Parameters.AddWithValue(reference.ByteCount);
            Id(command, StoreId); Id(command, attemptId); command.Parameters.AddWithValue(expectedBundleVersion);
            Optional(command, NpgsqlDbType.Integer, extraction?.ExtractorVersion); Optional(command, NpgsqlDbType.Text, extraction?.ProfileDigest.Value);
            Optional(command, NpgsqlDbType.Integer, extraction is null ? null : (int)extraction.Outcome); Optional(command, NpgsqlDbType.Text, extraction?.SourceDigest.Value);
            Finish(command);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) throw Denied();
            var stored = ReadObject(reader);
            Require(reference, stored.Reference);
            if (stored.AttemptId != attemptId || stored.StoreId != StoreId) throw Denied();
            return new SpecObjectReservation(new(StoreId, attemptId, stored.Reference), checked(reader.GetInt64(reader.GetOrdinal("reservation_version")) + 1));
        }, token);
    }

    public Task<SpecObjectReference> FindAsync(SpecBundleScope scope, Guid objectId, CancellationToken token) =>
        database.RunAsync(false, async (c, t) => (await Gate(c, t, scope, objectId, null, "read", token).ConfigureAwait(false)).Reference, token);

    public Task<SpecBundleInventory> InventoryAsync(SpecBundleScope scope, CancellationToken token) =>
        database.RunAsync(false, async (c, t) =>
        {
            await ValidateBinding(c, t, token).ConfigureAwait(false);
            SpecBundleStatus status;
            using (var command = Command("spec_bundle_authorize", scope, c, t))
            {
                command.Parameters.AddWithValue(false); Finish(command);
                await using var row = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                if (!await row.ReadAsync(token).ConfigureAwait(false)) throw Denied();
                status = new(scope, row.GetInt64(row.GetOrdinal("version")), false);
            }
            using var list = Command("spec_inventory", scope, c, t);
            Id(list, StoreId); Finish(list);
            var items = new List<SpecInventoryItem>();
            await using (var rows = await list.ExecuteReaderAsync(token).ConfigureAwait(false))
            {
                while (await rows.ReadAsync(token).ConfigureAwait(false))
                {
                    if (items.Count >= 128) throw Denied();
                    var reference = new SpecObjectReference(new(new(scope, rows.GetGuid(1), rows.GetInt64(2)),
                        (SpecObjectKind)rows.GetInt32(3), rows.GetGuid(0)), Sha256Digest.Parse(rows.GetString(4)), rows.GetInt32(5));
                    items.Add(new(reference, (SpecSourceFormat)rows.GetInt32(6), rows.GetString(7),
                        rows.IsDBNull(8) ? null : (SpecExtractionOutcome)rows.GetInt32(8)));
                }
            }
            // The existing admitted object gate returns the stored extraction pins; do not synthesize today's profile.
            // Pending/retired items retain their inventory status but cannot supply executable evidence.
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item.State != "published" || item.Reference.Id.Kind != SpecObjectKind.Extraction) continue;
                var row = await Gate(c, t, scope, item.Reference.Id.ObjectId, null, "read", token).ConfigureAwait(false);
                Require(item.Reference, row.Reference);
                var pin = row.Extraction ?? throw new SpecStorageException(SpecStorageError.Integrity);
                if (pin.Outcome != item.Outcome) throw new SpecStorageException(SpecStorageError.Integrity);
                items[i] = item with
                {
                    Extraction = new(item.Reference.Id.Source, pin.SourceDigest, item.Format,
                    pin.Version, pin.ProfileDigest, pin.Outcome, item.Reference.Digest)
                };
            }
            return new SpecBundleInventory(status, items.AsReadOnly());
        }, token);

    public Task<SpecBundleStatus> TombstoneAsync(SpecBundleScope scope, SpecSourceIdentity? source, long expectedVersion, Guid key, CancellationToken token)
    {
        if (expectedVersion < 1 || source is not null && source.Bundle != scope) throw new ArgumentException("Invalid tombstone scope/version.");
        return BundleCall("spec_tombstone", scope, true, command =>
        {
            Optional(command, NpgsqlDbType.Uuid, source?.SourceId); Optional(command, NpgsqlDbType.Bigint, source?.Version);
            command.Parameters.AddWithValue(expectedVersion); Id(command, key);
        }, token);
    }

    public Task<IReadOnlyList<SpecRetirementClaim>> RetirementsAsync(SpecBundleScope scope, CancellationToken token) =>
        database.RunAsync<IReadOnlyList<SpecRetirementClaim>>(false, async (c, t) =>
        {
            await ValidateBinding(c, t, token).ConfigureAwait(false);
            using var command = Command("spec_retirements", scope, c, t); Id(command, StoreId); Finish(command);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            var result = new List<SpecRetirementClaim>();
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                if (result.Count == 100) throw Denied();
                var row = ReadObject(reader);
                if (row.Reference.Id.Source.Bundle != scope || row.StoreId != StoreId || row.RetirementId is not { } retirement) throw Denied();
                result.Add(new(StoreId, retirement, row.Reference));
            }
            return result.AsReadOnly();
        }, token);

    public Task AdmitPublicationAsync(SpecPublicationClaim claim, CancellationToken token) =>
        database.RunAsync(true, async (c, t) => { await PublicationGate(c, t, claim, token).ConfigureAwait(false); return true; }, token);

    public Task<SpecPublishResult> ExecutePublicationAsync(SpecPublicationClaim claim, Func<CancellationToken, Task<SpecPublishResult>> publish, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(publish);
        return database.RunAsync(true, async (c, t) =>
        {
            await PublicationGate(c, t, claim, token).ConfigureAwait(false);
            var result = await publish(token).ConfigureAwait(false);
            if (!Enum.IsDefined(result)) throw new SpecStorageException(SpecStorageError.Integrity);
            await Complete(c, t, claim.Reference, claim.AttemptId, "publish", token).ConfigureAwait(false);
            return result;
        }, token);
    }

    public Task<byte[]> ExecuteReadAsync(Guid storeId, SpecObjectReference reference, Func<CancellationToken, Task<byte[]>> read, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (storeId != StoreId) throw Denied();
        return database.RunAsync(false, async (c, t) =>
        {
            var row = await Gate(c, t, reference.Id.Source.Bundle, reference.Id.ObjectId, null, "read", token).ConfigureAwait(false);
            Require(reference, row.Reference);
            var bytes = await read(token).ConfigureAwait(false);
            if (bytes.Length != reference.ByteCount || Sha256Digest.FromBytes(bytes) != reference.Digest) throw new SpecStorageException(SpecStorageError.Integrity);
            return bytes;
        }, token);
    }

    public Task<SpecRetireResult> ExecuteRetirementAsync(SpecRetirementClaim claim, Func<CancellationToken, Task<SpecRetireResult>> retire, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(retire);
        return database.RunAsync(true, async (c, t) =>
        {
            await RetirementGate(c, t, claim, token).ConfigureAwait(false);
            var result = await retire(token).ConfigureAwait(false);
            if (!Enum.IsDefined(result)) throw new SpecStorageException(SpecStorageError.Integrity);
            await Complete(c, t, claim.Reference, claim.RetirementId, "retire", token).ConfigureAwait(false);
            return result;
        }, token);
    }

    public Task<SpecTemporarySnapshot> AdvanceTemporaryAsync(SpecPublicationClaim claim, Guid temporaryId, SpecTemporaryPhase phase,
        SpecTemporaryEvidence evidence, Sha256Digest? previousDigest, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (!Enum.IsDefined(phase) || (phase == SpecTemporaryPhase.Reserved) != (previousDigest is null)
            || (phase == SpecTemporaryPhase.Reserved) != (evidence.File is null)) throw new ArgumentException("Invalid temporary phase evidence.");
        var bytes = evidence.Encode();
        return database.RunAsync(true, async (c, t) =>
        {
            await PublicationGate(c, t, claim, token).ConfigureAwait(false);
            using var command = Command("spec_temp_advance", claim.Reference.Id.Source.Bundle, c, t);
            Id(command, claim.Reference.Id.ObjectId); Id(command, claim.AttemptId); Id(command, temporaryId);
            command.Parameters.AddWithValue((int)phase); Optional(command, NpgsqlDbType.Text, previousDigest?.Value);
            command.Parameters.AddWithValue(bytes); Finish(command);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) throw Denied();
            return ReadTemporary(reader, claim);
        }, token);
    }

    public Task<IReadOnlyList<SpecTemporarySnapshot>> RecoverTemporariesAsync(SpecPublicationClaim claim, CancellationToken token = default) =>
        database.RunAsync(true, async (c, t) =>
        {
            await PublicationGate(c, t, claim, token).ConfigureAwait(false);
            return await ReadTemporaries(c, t, claim, claim.AttemptId, false, token).ConfigureAwait(false);
        }, token);

    public Task<IReadOnlyList<SpecTemporarySnapshot>> RetiringTemporariesAsync(SpecRetirementClaim claim, CancellationToken token = default) =>
        database.RunAsync(false, async (c, t) =>
        {
            var row = await RetirementGate(c, t, claim, token).ConfigureAwait(false);
            return await ReadTemporaries(c, t, new(StoreId, row.AttemptId, row.Reference), claim.RetirementId, true, token).ConfigureAwait(false);
        }, token);

    public Task ExecuteTemporaryRetirementAsync(SpecRetirementClaim claim, SpecTemporarySnapshot temporary,
        Func<CancellationToken, Task> retire, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(temporary); ArgumentNullException.ThrowIfNull(retire);
        return database.RunAsync(true, async (c, t) =>
        {
            var row = await RetirementGate(c, t, claim, token).ConfigureAwait(false);
            var all = await ReadTemporaries(c, t, new(StoreId, row.AttemptId, row.Reference), claim.RetirementId, true, token).ConfigureAwait(false);
            var current = all.SingleOrDefault(s => s.TemporaryId == temporary.TemporaryId);
            if (current is null || current.Publication != temporary.Publication || current.SealDigest != temporary.SealDigest || current.Phase != temporary.Phase
                || current.RetirementId != temporary.RetirementId || current.RetirementId is null || !current.Evidence.Encode().SequenceEqual(temporary.Evidence.Encode())) throw Denied();
            if (current.Retired) return true;
            await retire(token).ConfigureAwait(false);
            using var command = Command("spec_temp_complete", claim.Reference.Id.Source.Bundle, c, t);
            Id(command, claim.Reference.Id.ObjectId); Id(command, claim.RetirementId); Id(command, temporary.TemporaryId);
            Id(command, current.RetirementId.Value); command.Parameters.AddWithValue(current.SealDigest.Value); Finish(command);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            return true;
        }, token);
    }

    private async Task<IReadOnlyList<SpecTemporarySnapshot>> ReadTemporaries(NpgsqlConnection c, NpgsqlTransaction t,
        SpecPublicationClaim publication, Guid claimId, bool retiring, CancellationToken token)
    {
        using var command = Command("spec_temp_read", publication.Reference.Id.Source.Bundle, c, t);
        Id(command, publication.Reference.Id.ObjectId); Id(command, claimId); command.Parameters.AddWithValue(retiring); Finish(command);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var result = new List<SpecTemporarySnapshot>();
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            if (result.Count == 32) throw Denied();
            result.Add(ReadTemporary(reader, publication));
        }
        return result.AsReadOnly();
    }

    private static SpecTemporarySnapshot ReadTemporary(NpgsqlDataReader reader, SpecPublicationClaim publication)
    {
        int At(string name) => reader.GetOrdinal(name);
        var source = publication.Reference.Id.Source;
        if (reader.GetGuid(At("organization_id")) != source.Bundle.Project.OrganizationId || reader.GetGuid(At("project_id")) != source.Bundle.Project.ProjectId
            || reader.GetGuid(At("bundle_id")) != source.Bundle.BundleId || reader.GetGuid(At("object_id")) != publication.Reference.Id.ObjectId
            || reader.GetGuid(At("attempt_id")) != publication.AttemptId) throw Denied();
        var phase = (SpecTemporaryPhase)reader.GetInt32(At("phase"));
        var evidence = SpecTemporaryEvidence.Decode(reader.GetFieldValue<byte[]>(At("evidence")));
        if (!Enum.IsDefined(phase) || (phase == SpecTemporaryPhase.Reserved) != (evidence.File is null)) throw Denied();
        var tempId = reader.GetGuid(At("id"));
        var digest = Sha256Digest.Parse(reader.GetString(At("seal_digest")));
        var previous = reader.IsDBNull(At("previous_digest")) ? null : Sha256Digest.Parse(reader.GetString(At("previous_digest")));
        if (SpecTemporarySeal.Digest(publication, tempId, phase, previous, evidence) != digest) throw new SpecStorageException(SpecStorageError.Integrity);
        return new(publication, tempId, phase, evidence, digest, previous,
            reader.IsDBNull(At("retirement_id")) ? null : reader.GetGuid(At("retirement_id")), reader.GetBoolean(At("retired")));
    }

    private async Task<ObjectRow> PublicationGate(NpgsqlConnection c, NpgsqlTransaction t, SpecPublicationClaim claim, CancellationToken token)
    {
        if (claim.StoreId != StoreId) throw Denied();
        var row = await Gate(c, t, claim.Reference.Id.Source.Bundle, claim.Reference.Id.ObjectId, claim.AttemptId, "publish", token).ConfigureAwait(false);
        Require(claim.Reference, row.Reference); return row;
    }
    private async Task<ObjectRow> RetirementGate(NpgsqlConnection c, NpgsqlTransaction t, SpecRetirementClaim claim, CancellationToken token)
    {
        if (claim.StoreId != StoreId) throw Denied();
        var row = await Gate(c, t, claim.Reference.Id.Source.Bundle, claim.Reference.Id.ObjectId, claim.RetirementId, "retire", token).ConfigureAwait(false);
        Require(claim.Reference, row.Reference); return row;
    }
    private async Task<ObjectRow> Gate(NpgsqlConnection c, NpgsqlTransaction t, SpecBundleScope scope, Guid objectId, Guid? claimId, string mode, CancellationToken token)
    {
        await ValidateBinding(c, t, token).ConfigureAwait(false);
        using var command = Command("spec_object_gate", scope, c, t); Id(command, objectId);
        Optional(command, NpgsqlDbType.Uuid, claimId); command.Parameters.AddWithValue(mode); Finish(command);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) throw Denied();
        var row = ReadObject(reader);
        if (row.StoreId != StoreId || row.Reference.Id.Source.Bundle != scope || row.Reference.Id.ObjectId != objectId) throw Denied();
        return row;
    }
    private async Task ValidateBinding(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        using var command = new NpgsqlCommand("SELECT weft.spec_store_admit($1,$2,$3)", connection, transaction);
        command.Parameters.AddWithValue(binding.StoreId);
        command.Parameters.AddWithValue(binding.Kind == SpecStoreKind.Blob ? "blob" : "filesystem");
        command.Parameters.AddWithValue(binding.LocationDigest.Value);
        if (await command.ExecuteScalarAsync(token).ConfigureAwait(false) is not true) throw Denied();
    }
    private async Task Complete(NpgsqlConnection c, NpgsqlTransaction t, SpecObjectReference reference, Guid claim, string mode, CancellationToken token)
    {
        using var command = Command("spec_object_complete", reference.Id.Source.Bundle, c, t);
        Id(command, reference.Id.ObjectId); Id(command, claim); command.Parameters.AddWithValue(mode); Finish(command);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
    private sealed record StoredExtraction(int Version, Sha256Digest ProfileDigest, SpecExtractionOutcome Outcome, Sha256Digest SourceDigest);
    private sealed record ObjectRow(SpecObjectReference Reference, Guid StoreId, Guid AttemptId, Guid? RetirementId, StoredExtraction? Extraction);
    private static ObjectRow ReadObject(NpgsqlDataReader reader)
    {
        int At(string name) => reader.GetOrdinal(name);
        var scope = new SpecBundleScope(new(reader.GetGuid(At("organization_id")), reader.GetGuid(At("project_id"))), reader.GetGuid(At("bundle_id")));
        var source = new SpecSourceIdentity(scope, reader.GetGuid(At("source_id")), reader.GetInt64(At("source_version")));
        var reference = new SpecObjectReference(new(source, (SpecObjectKind)reader.GetInt32(At("kind")), reader.GetGuid(At("id"))),
            Sha256Digest.Parse(reader.GetString(At("digest"))), reader.GetInt32(At("byte_count")));
        var extraction = reference.Id.Kind == SpecObjectKind.Extraction ? new StoredExtraction(reader.GetInt32(At("extractor_version")),
            Sha256Digest.Parse(reader.GetString(At("profile_digest"))), (SpecExtractionOutcome)reader.GetInt32(At("extraction_outcome")),
            Sha256Digest.Parse(reader.GetString(At("source_digest")))) : null;
        return new(reference, reader.GetGuid(At("store_id")), reader.GetGuid(At("attempt_id")), reader.IsDBNull(At("retirement_id")) ? null : reader.GetGuid(At("retirement_id")), extraction);
    }
    private NpgsqlCommand Command(string function, SpecBundleScope scope, NpgsqlConnection c, NpgsqlTransaction t)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var command = new NpgsqlCommand(function, c, t);
        Optional(command, NpgsqlDbType.Uuid, actor?.PrincipalId.Value);
        Id(command, scope.Project.OrganizationId); Id(command, scope.Project.ProjectId); Id(command, scope.BundleId);
        return command;
    }
    private static void Finish(NpgsqlCommand command) => command.CommandText = $"SELECT * FROM weft.{command.CommandText}({string.Join(',', Enumerable.Range(1, command.Parameters.Count).Select(i => "$" + i))})";
    private static void Id(NpgsqlCommand command, Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("An identifier is required.");
        command.Parameters.AddWithValue(id);
    }
    private static void Optional(NpgsqlCommand command, NpgsqlDbType type, object? value) => command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = type, Value = value ?? DBNull.Value });
    private static void Require(SpecObjectReference expected, SpecObjectReference actual) { if (expected != actual) throw Denied(); }
    private static SpecStorageException Denied() => new(SpecStorageError.NotAdmitted);
}
