using System.Runtime.Versioning;
using Npgsql;
using Braid.Application;
using Braid.Domain;
using Braid.Persistence.Postgres;
using Braid.Storage.FileSystem;

namespace Braid.Tests;

/// <summary>
/// OWNER-AUTHORED AGE FIXTURE ONLY. This does not prove25h elapsed, manufacture
/// a registration, create a missing seal, verify an unfinished payload, or issue
/// cleanup authority. Call only after the real writer has settled or been reaped.
/// Actual Claim/Retire/Finish follows separately through production ports.
/// </summary>
[SupportedOSPlatform("linux")]
internal static class MaintenanceCommandTrackedTemporaryAging
{
    internal static async Task<PrivatePublicationRecord> AgeAsync(PostgresCase test, PrivateStorageTestRoot root,
        PrivatePublicationKey key, TimeSpan age)
    {
        Assert.True(age >= TimeSpan.FromHours(25));
        var leaf = PrivateStorageV2Data.TempLeaf(root, key);
        // An independently opened EX|NB flock refuses an actually live writer.
        // It also prevents a new publisher/retirer during this owned fixture edit.
        using var held = new PrivateStorageV2IndependentGate(Path.Combine(leaf, "temp.gate"));
        Assert.Empty(Directory.GetFiles(leaf, "*.stage"));
        var seals = new Dictionary<V2RecordKind, Seal>();
        foreach (var (kind, name) in new[] { (V2RecordKind.Absence, "absence"), (V2RecordKind.Reservation, "reservation"),
            (V2RecordKind.Bound, "bound"), (V2RecordKind.Prepared, "prepared") })
        {
            var path = Path.Combine(leaf, name);
            if (!File.Exists(path)) continue;
            var stat = PrivateStorageLinux.Metadata(path);
            Assert.Equal(PrivateStorageLinux.EffectiveUid, stat.Uid);
            Assert.Equal(1U, stat.Links);
            Assert.Equal((ushort)0x8100, stat.Mode); // ordinary0400, no symlink/special file
            var bytes = File.ReadAllBytes(path);
            var record = V2PublicationRecordCodec.Decode(bytes);
            Assert.Equal(kind, record.Kind);
            Assert.Equal(bytes, V2PublicationRecordCodec.Encode(record));
            seals.Add(kind, new(path, bytes, PrivateStorageV2Identity.Read(path), record));
        }
        var absence = Assert.IsType<V2AbsenceRecord>(seals[V2RecordKind.Absence].Record);
        var original = absence.Context;
        Assert.Equal(key.RootId, original.RootId);
        Assert.Equal(key.Object, original.Reference.Id);
        Assert.Equal(key.TempId, original.TempId);
        Assert.Equal(PrivateStorageV2Data.Marker(root), (original.RootId, original.ControlNamespaceId));
        Assert.Equal(PrivateStorageV2Identity.Read(root.RootPath), original.RootIdentity);
        Assert.Equal(PrivateStorageV2Identity.Read(PrivateStorageV2Data.ControlRoot(root)), original.ControlIdentity);
        Assert.All(seals.Values, seal => Assert.Equal(original, seal.Record.Context));
        await using var ingestion = new PostgresIngestionDatabase(test.IngestionConnectionString);
        await using var derivation = new PostgresRevisionDerivationDatabase(test.DerivationConnectionString);
        IPrivateObjectPublicationRegistry registry = original.Owner.Kind == PrivateObjectOwnerKind.NativeIngestion
            ? new PostgresIngestionPublicationRegistry(ingestion) : new PostgresRevisionReportPublicationRegistry(derivation);
        var before = Assert.IsType<PrivatePublicationSnapshot>(await registry.GetAsync(original.Owner, PrivatePublicationStoreKind.FileSystem,
            key.RootId, key.Object, key.TempId));
        var reservation = before.Record.Reservation;
        Assert.Equal(original.Owner, reservation.Owner);
        Assert.Equal(Sha256Digest.FromBytes(seals[V2RecordKind.Absence].Bytes), reservation.AbsenceRecordDigest);
        var reserved = seals.TryGetValue(V2RecordKind.Reservation, out var reservedSeal)
            ? Assert.IsType<V2ReservationRecord>(reservedSeal.Record) : null;
        var bound = seals.TryGetValue(V2RecordKind.Bound, out var boundSeal)
            ? Assert.IsType<V2BoundRecord>(boundSeal.Record) : null;
        var prepared = seals.TryGetValue(V2RecordKind.Prepared, out var preparedSeal)
            ? Assert.IsType<V2PreparedRecord>(preparedSeal.Record) : null;
        if (reserved is not null)
        {
            Assert.Equal(reservation.PublicationId, reserved.PublicationId);
            Assert.Equal(reservation.RegisteredAtUtc, reserved.RegisteredAtUtc);
            Assert.Equal(reservation.AbsenceRecordDigest, reserved.AbsenceRecordDigest);
            Assert.Equal(seals[V2RecordKind.Absence].Identity, reserved.AbsenceRecordIdentity);
        }
        if (bound is not null)
        {
            Assert.NotNull(reserved);
            Assert.Equal(reservation.PublicationId, bound.PublicationId);
            Assert.Equal(reservedSeal!.Identity, bound.ReservationRecordIdentity);
            Assert.Equal(Sha256Digest.FromBytes(reservedSeal.Bytes), bound.ReservationRecordDigest);
            if (before.Record.Binding is { } binding)
            {
                Assert.Equal(original.CreateFileIdentity(bound.DataFileIdentity), binding.ActualIdentity);
                Assert.Equal(Sha256Digest.FromBytes(boundSeal!.Bytes), binding.BoundRecordDigest);
            }
        }
        else Assert.Null(before.Record.Binding);
        if (prepared is not null)
        {
            Assert.NotNull(bound);
            Assert.NotNull(before.Record.Binding);
            Assert.Equal(reservation.PublicationId, prepared.PublicationId);
            Assert.Equal(boundSeal!.Identity, prepared.BoundRecordIdentity);
            Assert.Equal(Sha256Digest.FromBytes(boundSeal.Bytes), prepared.BoundRecordDigest);
            Assert.Equal(before.Record.Binding.BoundAtUtc, prepared.BoundAtUtc);
            Assert.Equal(bound.DataFileIdentity, prepared.DataFileIdentity);
            Assert.Equal(original.Reference.Digest, prepared.VerifiedDigest);
            Assert.Equal(original.Reference.ByteCount, prepared.VerifiedByteCount);
        }
        var temp = PrivateStorageV2Data.TempPath(root, key);
        var payload = File.Exists(temp) ? File.ReadAllBytes(temp) : null;
        var payloadIdentity = payload is null ? null : PrivateStorageV2Identity.Read(temp);
        var payloadMode = payload is null ? default : File.GetUnixFileMode(temp);
        if (payload is not null)
        {
            Assert.NotNull(reserved);
            var facts = PrivateStorageLinux.Metadata(temp);
            Assert.Equal(1U, facts.Links);
            Assert.Equal(PrivateStorageLinux.EffectiveUid, facts.Uid);
            Assert.True(payloadMode is PrivateStorageTestRoot.AttemptMode or PrivateStorageTestRoot.FinalMode);
            if (bound is not null) Assert.Equal(bound.DataFileIdentity, payloadIdentity);
            else Assert.Empty(payload);
        }
        var owner = ShiftOwner(original.Owner, age);
        var context = new V2PublicationContext(original.RootId, original.ControlNamespaceId, original.Reference,
            original.TempId, owner, original.BootId, original.MountNamespaceDevice, original.MountNamespaceInode,
            original.EffectiveUserId, original.RootIdentity, original.ControlIdentity, original.ObjectDirectoryIdentity,
            original.TempDirectoryIdentity, original.DataParentIdentity, original.TempGateIdentity);
        var replacements = new Dictionary<V2RecordKind, byte[]>
        {
            [V2RecordKind.Absence] = V2PublicationRecordCodec.Encode(new V2AbsenceRecord(context))
        };
        if (reserved is not null)
            replacements.Add(V2RecordKind.Reservation, V2PublicationRecordCodec.Encode(new V2ReservationRecord(context,
                Sha256Digest.FromBytes(replacements[V2RecordKind.Absence]), reserved.AbsenceRecordIdentity,
                reserved.PublicationId, reserved.RegisteredAtUtc - age)));
        if (bound is not null)
            replacements.Add(V2RecordKind.Bound, V2PublicationRecordCodec.Encode(new V2BoundRecord(context,
                Sha256Digest.FromBytes(replacements[V2RecordKind.Reservation]), bound.ReservationRecordIdentity,
                bound.PublicationId, bound.DataFileIdentity)));
        if (prepared is not null)
            replacements.Add(V2RecordKind.Prepared, V2PublicationRecordCodec.Encode(new V2PreparedRecord(context,
                Sha256Digest.FromBytes(replacements[V2RecordKind.Bound]), prepared.BoundRecordIdentity,
                prepared.PublicationId, prepared.BoundAtUtc - age, prepared.DataFileIdentity,
                prepared.VerifiedDigest, prepared.VerifiedByteCount)));
        Assert.Equal(seals.Keys.Order(), replacements.Keys.Order()); // preserve actual3/4-seal shape
        var aged = new PrivatePublicationRecord(new(reservation.PublicationId, key, reservation.StoreKind, owner,
            Sha256Digest.FromBytes(replacements[V2RecordKind.Absence]), reservation.RegisteredAtUtc - age),
            before.Record.Binding is { } oldBinding ? new(oldBinding.ActualIdentity,
                Sha256Digest.FromBytes(replacements[V2RecordKind.Bound]), oldBinding.BoundAtUtc - age) : null);

        await using var connection = await ProjectCreationTestSupport.OpenOwnerAsync(test);
        await using var transaction = await connection.BeginTransactionAsync();
        var originalNonTime = await NonTimeSnapshotAsync(connection, transaction, reservation);
        var guards = original.Owner.Kind == PrivateObjectOwnerKind.NativeIngestion
            ? new[] { "revision_ingestions", "revision_ingestion_attempts", "private_object_publications" }
            : new[] { "revision_derivation_attempts", "private_object_publications" };
        try
        {
            foreach (var table in guards)
                await ExecuteAsync(connection, transaction, $"ALTER TABLE braid.{table} DISABLE TRIGGER {table}_transition_guard");
            if (original.Owner.IngestionClaim is { } native)
            {
                await ExecuteAsync(connection, transaction, "UPDATE braid.revision_ingestions SET created_at=created_at-$1,completed_at=completed_at-$1,lease_expires_at=lease_expires_at-$1,verified_stage_at=verified_stage_at-$1 WHERE operation_id=$2", age, native.OperationId);
                await ExecuteAsync(connection, transaction, "UPDATE braid.revision_ingestion_attempts SET started_at=started_at-$1,finished_at=finished_at-$1,verified_stage_at=verified_stage_at-$1 WHERE operation_id=$2", age, native.OperationId);
            }
            else
                await ExecuteAsync(connection, transaction, "UPDATE braid.revision_derivation_attempts SET started_at=started_at-$1,lease_expires_at=lease_expires_at-$1,finished_at=finished_at-$1 WHERE derivation_id=$2", age, original.Owner.ReportClaim!.DerivationId);
            await ExecuteAsync(connection, transaction, """
                UPDATE braid.private_object_publications SET registered_at=registered_at-$1,bound_at=bound_at-$1,
                  owner_lease_expires_at=owner_lease_expires_at-$1,absence_record_digest=$2,bound_record_digest=$3
                WHERE publication_id=$4
                """, age, aged.Reservation.AbsenceRecordDigest.Value,
                (object?)aged.Binding?.BoundRecordDigest.Value ?? DBNull.Value, reservation.PublicationId);
            Assert.Equal(originalNonTime, await NonTimeSnapshotAsync(connection, transaction, reservation));
            await ExecuteAsync(connection, transaction, "SET CONSTRAINTS ALL IMMEDIATE");
            foreach (var table in guards)
                await ExecuteAsync(connection, transaction, $"ALTER TABLE braid.{table} ENABLE TRIGGER {table}_transition_guard");
            foreach (var (kind, bytes) in replacements)
            {
                Assert.Equal(seals[kind].Bytes.Length, bytes.Length);
                Rewrite(seals[kind], bytes);
                Assert.Equal(bytes, V2PublicationRecordCodec.Encode(V2PublicationRecordCodec.Decode(File.ReadAllBytes(seals[kind].Path))));
                Assert.Equal(context, V2PublicationRecordCodec.Decode(bytes).Context);
            }
            Assert.Equal(seals.Count, Directory.GetFiles(leaf).Count(path => Path.GetFileName(path) is "absence" or "reservation" or "bound" or "prepared"));
            if (payload is not null)
            {
                Assert.Equal(payload, File.ReadAllBytes(temp));
                Assert.Equal(payloadIdentity, PrivateStorageV2Identity.Read(temp));
                Assert.Equal(payloadMode, File.GetUnixFileMode(temp));
            }
            else Assert.False(File.Exists(temp));
            await transaction.CommitAsync();
        }
        catch (Exception first)
        {
            var failures = new List<Exception> { first };
            try { await transaction.RollbackAsync(); }
            catch (Exception rollback) { failures.Add(rollback); }
            foreach (var seal in seals.Values)
            {
                try { Rewrite(seal, seal.Bytes); }
                catch (Exception restore) { failures.Add(restore); }
            }
            if (failures.Count > 1)
                throw new AggregateException("Owned age fixture failed; rollback/restoration failures are retained after the original.", failures);
            throw;
        }
        var observed = Assert.IsType<PrivatePublicationSnapshot>(await registry.GetAsync(owner, PrivatePublicationStoreKind.FileSystem, key.RootId, key.Object, key.TempId));
        Assert.Equal(aged, observed.Record);
        Assert.False(observed.OwnerIsLive);
        Assert.False(observed.CanWrite);
        return observed.Record;
    }

    private static PrivatePublicationOwner ShiftOwner(PrivatePublicationOwner owner, TimeSpan age) => owner.IngestionClaim is { } native
        ? PrivatePublicationOwner.ForIngestion(owner.Actor, new(native.Project, native.OperationId, native.OriginalActor,
            native.ServerInstanceId, native.LeaseToken, native.Generation, native.LeaseExpiresAtUtc - age))
        : PrivatePublicationOwner.ForReport(owner.Actor, new(owner.ReportClaim!.CacheEntryId, owner.ReportClaim.DerivationId,
            owner.ReportClaim.Key, owner.ReportClaim.OriginalActor, owner.ReportClaim.ServerInstanceId, owner.ReportClaim.LeaseToken,
            owner.ReportClaim.Generation, owner.ReportClaim.StartedAtUtc - age, owner.ReportClaim.LeaseExpiresAtUtc - age,
            owner.ReportClaim.CandidateObjectId, owner.ReportClaim.RepairTargetDerivationId));

    private static void Rewrite(Seal seal, byte[] bytes)
    {
        Assert.Equal(seal.Identity, PrivateStorageV2Identity.Read(seal.Path));
        Assert.Equal(1U, PrivateStorageLinux.Metadata(seal.Path).Links);
        File.SetUnixFileMode(seal.Path, PrivateStorageTestRoot.AttemptMode);
        try
        {
            using var output = new FileStream(seal.Path, FileMode.Open, FileAccess.Write, FileShare.None);
            output.Write(bytes);
            output.SetLength(bytes.Length);
            output.Flush(flushToDisk: true);
        }
        finally { File.SetUnixFileMode(seal.Path, PrivateStorageTestRoot.FinalMode); }
        Assert.Equal(seal.Identity, PrivateStorageV2Identity.Read(seal.Path));
        Assert.Equal((ushort)0x8100, PrivateStorageLinux.Metadata(seal.Path).Mode);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var value in values) command.Parameters.AddWithValue(value);
        var changed = await command.ExecuteNonQueryAsync();
        if (sql.StartsWith("UPDATE ", StringComparison.Ordinal)) Assert.True(changed > 0);
    }

    private static async Task<string> NonTimeSnapshotAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        PrivatePublicationReservation reservation)
    {
        var native = reservation.Owner.IngestionClaim;
        var sql = native is not null ? """
            SELECT jsonb_build_object('owner',(SELECT to_jsonb(i)-ARRAY['created_at','completed_at','lease_expires_at','verified_stage_at'] FROM braid.revision_ingestions i WHERE operation_id=$1),
              'attempts',(SELECT jsonb_agg(to_jsonb(a)-ARRAY['started_at','finished_at','verified_stage_at'] ORDER BY generation) FROM braid.revision_ingestion_attempts a WHERE operation_id=$1),
              'publication',(SELECT to_jsonb(p)-ARRAY['registered_at','bound_at','owner_lease_expires_at','absence_record_digest','bound_record_digest'] FROM braid.private_object_publications p WHERE publication_id=$2))::text
            """ : """
            SELECT jsonb_build_object('owner',(SELECT to_jsonb(a)-ARRAY['started_at','lease_expires_at','finished_at'] FROM braid.revision_derivation_attempts a WHERE derivation_id=$1),
              'publication',(SELECT to_jsonb(p)-ARRAY['registered_at','bound_at','owner_lease_expires_at','absence_record_digest','bound_record_digest'] FROM braid.private_object_publications p WHERE publication_id=$2))::text
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(native?.OperationId ?? reservation.Owner.ReportClaim!.DerivationId);
        command.Parameters.AddWithValue(reservation.PublicationId);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private sealed record Seal(string Path, byte[] Bytes, PrivatePublicationInodeIdentity Identity, V2PublicationRecord Record);
}
