using System.Data;
using System.Text;
using Npgsql;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

/// <summary>
/// Actual PostgreSQL only. C1 admission, report publication, clock movement,
/// cleanup dispositions and Linux file identities are explicitly authored metadata.
/// No native worker, private object, filesystem gate or deletion is exercised.
/// </summary>
internal sealed class PostgresPrivateObjectMaintenanceTestSupport : IAsyncDisposable
{
    private readonly PostgresRevisionDerivationTestSupport source;
    internal PostgresCase Case => source.Case;
    internal PostgresSeedData Seed => source.Seed;
    internal ActorContext Actor => source.Actor;
    internal ProjectScope Project => source.Project;
    internal RevisionScope Revision => source.Revision;
    internal RevisionReportSource Source => source.Source;
    internal ProjectionCacheKey Key => source.Key;
    internal PostgresRevisionReportDatabase ReportDatabase => source.ReportDatabase;
    internal PostgresRevisionDerivationDatabase DerivationDatabase => source.DerivationDatabase;
    internal PostgresRevisionReportMetadataReader ReportReader => source.Reader;
    internal PostgresRevisionDerivationStore Reports => source.Store;
    internal PostgresIngestionDatabase IngestionDatabase { get; }
    internal PostgresRevisionIngestionStore Ingestions { get; }
    internal PostgresPrivateObjectMaintenanceDatabase MaintenanceDatabase { get; }
    internal PostgresPrivateObjectMaintenanceStore Maintenance { get; }
    internal PostgresIngestionPublicationRegistry NativePublications { get; }
    internal PostgresRevisionReportPublicationRegistry ReportPublications { get; }
    internal static Sha256Digest AbsenceDigest => Sha256Digest.FromBytes("Synthetic sealed absence record; no file was inspected."u8);
    internal static Sha256Digest BoundDigest => Sha256Digest.FromBytes("Synthetic sealed bound record; no file was inspected."u8);

    private PostgresPrivateObjectMaintenanceTestSupport(PostgresRevisionDerivationTestSupport source)
    {
        this.source = source;
        IngestionDatabase = new(Case.IngestionConnectionString);
        Ingestions = new(IngestionDatabase);
        MaintenanceDatabase = new(Case.MaintenanceConnectionString);
        Maintenance = new(MaintenanceDatabase);
        NativePublications = new(IngestionDatabase);
        ReportPublications = new(DerivationDatabase);
    }

    internal static async Task<PostgresPrivateObjectMaintenanceTestSupport> CreateAsync(PostgresFixture fixture)
    {
        var source = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
        try { return new(source); }
        catch { await source.DisposeAsync(); throw; }
    }

    internal ProjectionCacheKey ReportKey(string discriminator) => source.ProjectionKey(algorithm: "synthetic.maintenance." + discriminator);
    internal ValidationCacheKey ValidationKey(string discriminator) => source.ValidationKey(algorithm: "synthetic.maintenance." + discriminator);

    internal async Task<ReservedIngestionFacts> ReserveNativeAsync(IngestionIntent? intent = null)
    {
        var result = await Ingestions.ReserveAsync(Actor, Project, Guid.NewGuid(),
            intent ?? PostgresIngestionTestSupport.CreateIntent(), PostgresIngestionTestSupport.Profile,
            PostgresIngestionTestSupport.Policy);
        Assert.Equal(IngestionStoreOutcome.Reserved, result.Outcome);
        return Assert.IsType<IngestionReceipt>(result.Receipt).Facts;
    }

    internal async Task<IngestionClaim> ClaimNativeAsync(ReservedIngestionFacts facts)
    {
        var result = await Ingestions.ClaimAsync(Actor, facts.Project, facts.OperationId, Guid.NewGuid());
        Assert.Equal(IngestionStoreOutcome.Claimed, result.Outcome);
        return Assert.IsType<IngestionClaim>(result.Claim);
    }

    internal Task<RevisionReportClaim> ReserveReportAsync(RevisionReportKey? key = null) => source.ClaimAsync(key);

    internal async Task ReadyNativeAsync(ReservedIngestionFacts facts, IngestionClaim claim)
    {
        Assert.Equal(IngestionStoreOutcome.Applied, (await Ingestions.MarkStagedAsync(Actor, claim, facts.NativeObject)).Outcome);
        var ready = await Ingestions.CompleteAttemptAsync(Actor, claim, PostgresIngestionTestSupport.SyntheticCompletion(facts));
        Assert.Equal(IngestionPhase.Ready, ready.Receipt!.Phase);
    }

    internal async Task<ReservedIngestionFacts> CreateRejectedNativeAsync(bool aged = true)
    {
        var facts = await ReserveNativeAsync();
        var claim = await ClaimNativeAsync(facts);
        var result = await Ingestions.CompleteAttemptAsync(Actor, claim,
            IngestionAttemptCompletion.FromOperationalFailure(IngestionFailureCode.EvidenceLimit));
        Assert.Equal(IngestionPhase.Rejected, result.Receipt!.Phase);
        if (aged) await SyntheticAgeNativeAsync(facts, TimeSpan.FromHours(25));
        return facts;
    }

    internal async Task<ReservedIngestionFacts> CreateHeadConflictNativeAsync(bool aged = true)
    {
        // A genuine ready append loses its expected head. Its eligible attempt/native
        // facts remain genuine metadata and permit the historical raw FK attachment probe.
        var append = IngestionIntent.ForAppend(new(Revision.OrganizationId, Revision.ProjectId, Revision.MappingId),
            Source.SourceBackendId, null, new EqualHead(Revision.RevisionId),
            Sha256Digest.FromBytes(PostgresIngestionTestSupport.SyntheticBody), PostgresIngestionTestSupport.SyntheticBody.LongLength);
        var losing = await ReserveNativeAsync(append);
        var loser = await ClaimNativeAsync(losing);
        await ReadyNativeAsync(losing, loser);
        var winning = await ReserveNativeAsync(IngestionIntent.ForAppend(
            new(Revision.OrganizationId, Revision.ProjectId, Revision.MappingId), Source.SourceBackendId, null,
            new AnyHead(), losing.NativeObject.Digest, losing.NativeObject.ByteCount));
        var winner = await ClaimNativeAsync(winning);
        await ReadyNativeAsync(winning, winner);
        Assert.Equal(IngestionPhase.Committed, (await Ingestions.FinalizeAsync(Actor, winner)).Receipt!.Phase);
        Assert.Equal(IngestionPhase.HeadConflict, (await Ingestions.FinalizeAsync(Actor, loser)).Receipt!.Phase);
        if (aged) await SyntheticAgeNativeAsync(losing, TimeSpan.FromHours(25));
        return losing;
    }

    internal async Task<RevisionReportClaim> CreateFailedReportAsync(string discriminator, bool aged = true)
    {
        var claim = await ReserveReportAsync(ReportKey(discriminator));
        Assert.Equal(RevisionReportStoreOutcome.Applied,
            (await Reports.FinishAsync(Actor, claim, RevisionReportCompletion.ForFailure(RevisionReportFailureCategory.BackendFailure))).Outcome);
        return aged ? await SyntheticAgeReportAsync(claim, TimeSpan.FromHours(25)) : claim;
    }

    internal Task<RevisionReportResult> PublishSyntheticReportAsync(RevisionReportKey? key = null) => source.PublishSyntheticAsync(key);

    internal Task<long[]> CountsAsync() => Case.ScalarAsync<long[]>("""
        SELECT ARRAY[(SELECT count(*) FROM weft.private_object_retirements),
                     (SELECT count(*) FROM weft.private_object_cleanup_attempts),
                     (SELECT count(*) FROM weft.private_object_publications)]
        """);

    internal Task<string> OriginSnapshotAsync() => Case.ScalarAsync<string>("""
        SELECT jsonb_build_object(
          'ingestions',(SELECT coalesce(jsonb_agg(to_jsonb(i) ORDER BY operation_id),'[]') FROM weft.revision_ingestions i),
          'attempts',(SELECT coalesce(jsonb_agg(to_jsonb(a) ORDER BY operation_id,generation),'[]') FROM weft.revision_ingestion_attempts a),
          'revisions',(SELECT coalesce(jsonb_agg(to_jsonb(r) ORDER BY mapping_id,id),'[]') FROM weft.mapping_revisions r),
          'keys',(SELECT coalesce(jsonb_agg(to_jsonb(k) ORDER BY cache_entry_id),'[]') FROM weft.revision_derivations k),
          'reports',(SELECT coalesce(jsonb_agg(to_jsonb(a) ORDER BY derivation_id),'[]') FROM weft.revision_derivation_attempts a))::text
        """);

    internal Task<string> CleanupSnapshotAsync(Guid retirementId) => Case.ScalarAsync<string>("""
        SELECT jsonb_build_object('retirement',(SELECT to_jsonb(r) FROM weft.private_object_retirements r WHERE retirement_id=$1),
          'attempts',(SELECT coalesce(jsonb_agg(to_jsonb(a) ORDER BY generation),'[]') FROM weft.private_object_cleanup_attempts a WHERE retirement_id=$1))::text
        """, retirementId);

    internal Task<string> PublicationSnapshotAsync(Guid publicationId) => Case.ScalarAsync<string>(
        "SELECT to_jsonb(p)::text FROM weft.private_object_publications p WHERE publication_id=$1", publicationId);

    internal async Task<ObjectCleanupClaim> ClaimSingleAsync(PrivateObjectId expected)
    {
        var batch = await Maintenance.ClaimAsync(Project, Guid.NewGuid());
        var claim = Assert.Single(batch.Claims);
        Assert.Equal(expected, claim.Retirement.Target.Object);
        return claim;
    }

    internal async Task SyntheticAgeNativeAsync(ReservedIngestionFacts facts, TimeSpan age)
    {
        await OwnerEditAsync([
            ("revision_ingestions", "revision_ingestions_transition_guard"),
            ("revision_ingestion_attempts", "revision_ingestion_attempts_transition_guard")],
            new("UPDATE weft.revision_ingestions SET created_at=created_at-$1,completed_at=completed_at-$1,lease_expires_at=lease_expires_at-$1,verified_stage_at=verified_stage_at-$1 WHERE organization_id=$2 AND project_id=$3 AND operation_id=$4",
                [age, Project.OrganizationId, Project.ProjectId, facts.OperationId], 1),
            new("UPDATE weft.revision_ingestion_attempts SET started_at=started_at-$1,finished_at=finished_at-$1,verified_stage_at=verified_stage_at-$1 WHERE organization_id=$2 AND project_id=$3 AND operation_id=$4",
                [age, Project.OrganizationId, Project.ProjectId, facts.OperationId], null));
    }

    internal async Task<RevisionReportClaim> SyntheticAgeReportAsync(RevisionReportClaim claim, TimeSpan age)
    {
        await SyntheticAgeReportAttemptAsync(claim.DerivationId, age);
        return ShiftReport(claim, age);
    }

    internal Task SyntheticAgeReportAttemptAsync(Guid derivationId, TimeSpan age) =>
        OwnerEditAsync([("revision_derivation_attempts", "revision_derivation_attempts_transition_guard")],
            new OwnerStatement("UPDATE weft.revision_derivation_attempts SET started_at=started_at-$1,lease_expires_at=lease_expires_at-$1,finished_at=finished_at-$1 WHERE derivation_id=$2",
                [age, derivationId], 1));

    internal async Task<ObjectCleanupClaim> SyntheticAgeCleanupAsync(ObjectCleanupClaim claim, TimeSpan age)
    {
        // Age the header and all retained attempts together, preserving temporal
        // ordering and every FK/CHECK. This is not elapsed-clock/deletion evidence.
        await OwnerEditAsync([
            ("private_object_retirements", "private_object_retirements_transition_guard"),
            ("private_object_cleanup_attempts", "private_object_cleanup_attempts_transition_guard")],
            new("UPDATE weft.private_object_retirements SET retired_at=retired_at-$1 WHERE retirement_id=$2", [age, claim.Retirement.RetirementId], 1),
            new("UPDATE weft.private_object_cleanup_attempts SET started_at=started_at-$1,lease_expires_at=lease_expires_at-$1,finished_at=finished_at-$1 WHERE retirement_id=$2", [age, claim.Retirement.RetirementId], null));
        return (await Maintenance.GetAttemptAsync(Project, claim.Retirement.RetirementId, claim.Generation))!.Claim;
    }

    internal async Task<PrivatePublicationReservation> SyntheticAgePublicationAsync(PrivatePublicationReservation reservation, TimeSpan age)
    {
        var owner = reservation.Owner;
        var edits = new List<OwnerStatement>();
        var triggers = new List<(string, string)> { ("private_object_publications", "private_object_publications_transition_guard") };
        PrivatePublicationOwner shifted;
        if (owner.IngestionClaim is { } native)
        {
            triggers.Add(("revision_ingestions", "revision_ingestions_transition_guard"));
            triggers.Add(("revision_ingestion_attempts", "revision_ingestion_attempts_transition_guard"));
            edits.Add(new("UPDATE weft.revision_ingestions SET created_at=created_at-$1,lease_expires_at=lease_expires_at-$1,verified_stage_at=verified_stage_at-$1 WHERE operation_id=$2", [age, native.OperationId], 1));
            edits.Add(new("UPDATE weft.revision_ingestion_attempts SET started_at=started_at-$1,finished_at=finished_at-$1,verified_stage_at=verified_stage_at-$1 WHERE operation_id=$2", [age, native.OperationId], null));
            shifted = PrivatePublicationOwner.ForIngestion(owner.Actor, new(native.Project, native.OperationId, native.OriginalActor,
                native.ServerInstanceId, native.LeaseToken, native.Generation, native.LeaseExpiresAtUtc - age));
        }
        else
        {
            var report = owner.ReportClaim!;
            triggers.Add(("revision_derivation_attempts", "revision_derivation_attempts_transition_guard"));
            edits.Add(new("UPDATE weft.revision_derivation_attempts SET started_at=started_at-$1,lease_expires_at=lease_expires_at-$1,finished_at=finished_at-$1 WHERE derivation_id=$2", [age, report.DerivationId], 1));
            shifted = PrivatePublicationOwner.ForReport(owner.Actor, ShiftReport(report, age));
        }
        edits.Add(new("UPDATE weft.private_object_publications SET registered_at=registered_at-$1,bound_at=bound_at-$1,owner_lease_expires_at=owner_lease_expires_at-$1 WHERE publication_id=$2", [age, reservation.PublicationId], 1));
        await OwnerEditAsync(triggers.ToArray(), edits.ToArray());
        var key = reservation.Key;
        var registry = owner.Kind == PrivateObjectOwnerKind.NativeIngestion ? (IPrivateObjectPublicationRegistry)NativePublications : ReportPublications;
        var observed = await registry.GetAsync(shifted, key.RootId, key.Object, key.TempId);
        Assert.NotNull(observed);
        Assert.False(observed.OwnerIsLive);
        return observed.Record.Reservation;
    }

    private static RevisionReportClaim ShiftReport(RevisionReportClaim claim, TimeSpan age) => new(
        claim.CacheEntryId, claim.DerivationId, claim.Key, claim.OriginalActor, claim.ServerInstanceId, claim.LeaseToken,
        claim.Generation, claim.StartedAtUtc - age, claim.LeaseExpiresAtUtc - age, claim.CandidateObjectId, claim.RepairTargetDerivationId);

    internal static PrivatePublicationFileIdentity SyntheticIdentity(Guid rootId, Guid? bootId = null) => new(
        rootId, Guid.NewGuid(), bootId ?? Guid.NewGuid(), ulong.MaxValue, ulong.MaxValue - 2, 0,
        new(ulong.MaxValue, uint.MaxValue, uint.MaxValue, ulong.MaxValue),
        new(ulong.MaxValue - 1, uint.MaxValue, uint.MaxValue, ulong.MaxValue));

    internal sealed record OwnerStatement(string Sql, object[] Values, int? ExpectedRows);

    internal async Task OwnerEditAsync((string Table, string Trigger)[] triggers, params OwnerStatement[] edits)
    {
        // Disposable owner-authored metadata setup only. Hard-coded named ordinary
        // transition guards are restored inside the same transaction. Never disable
        // ALL, FK/internal triggers, constraints or the new attachment guard.
        await using var owner = await OpenOwnerAsync();
        await using var transaction = await owner.BeginTransactionAsync();
        foreach (var (table, trigger) in triggers)
            await ExecuteAsync(owner, transaction, $"ALTER TABLE weft.{table} DISABLE TRIGGER {trigger}");
        foreach (var edit in edits)
        {
            var changed = await ExecuteAsync(owner, transaction, edit.Sql, edit.Values);
            if (edit.ExpectedRows is { } expected) Assert.Equal(expected, changed);
        }
        await ExecuteAsync(owner, transaction, "SET CONSTRAINTS ALL IMMEDIATE");
        foreach (var (table, trigger) in triggers)
            await ExecuteAsync(owner, transaction, $"ALTER TABLE weft.{table} ENABLE TRIGGER {trigger}");
        await transaction.CommitAsync();
    }

    internal static async Task<int> InsertHistoricalNativeReferenceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        ReservedIngestionFacts facts, CancellationToken cancellationToken = default) =>
        await ExecuteAsync(connection, transaction, """
            INSERT INTO weft.mapping_revisions
              (organization_id,project_id,mapping_id,id,sequence,native_object_id,native_class,native_digest,native_byte_count,
               logical_entry_document,dependency_manifest_id,backend_id,creator_principal_id,committed_at,ingestion_operation_id,
               initial_attempt_generation,initial_admission)
            SELECT i.organization_id,i.project_id,i.future_mapping_id,i.future_revision_id,
              (SELECT max(r.sequence)+1 FROM weft.mapping_revisions r WHERE r.organization_id=i.organization_id
                AND r.project_id=i.project_id AND r.mapping_id=i.future_mapping_id),i.native_object_id,i.native_class,
              i.native_digest,i.native_byte_count,i.logical_entry_document,i.dependency_manifest_id,i.backend_id,
              i.actor_principal_id,clock_timestamp(),i.operation_id,i.eligible_attempt_generation,i.eligible_admission
            FROM weft.revision_ingestions i WHERE i.organization_id=$1 AND i.project_id=$2 AND i.operation_id=$3
            """, [facts.Project.OrganizationId, facts.Project.ProjectId, facts.OperationId], cancellationToken);

    internal static async Task<int> ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string sql, object[]? values = null, CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var value in values ?? []) command.Parameters.AddWithValue(value);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal Task<NpgsqlConnection> OpenOwnerAsync() => ProjectCreationTestSupport.OpenOwnerAsync(Case);
    internal static Task<int> BackendAsync(NpgsqlDataSource pool) => ProjectCreationTestSupport.BackendAsync(pool);
    internal Task WaitForBlockedAsync(int contender, int blocker, Task operation, CancellationToken cancellationToken = default) =>
        ProjectCreationTestSupport.WaitForBlockedAsync(Case, contender, blocker, operation, cancellationToken);

    internal async Task<(NpgsqlConnection Connection, NpgsqlTransaction Transaction)> OpenRestrictedAsync(string connectionString,
        IsolationLevel isolation = IsolationLevel.ReadCommitted)
    {
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        try
        {
            await connection.OpenAsync();
            var transaction = await connection.BeginTransactionAsync(isolation);
            await ExecuteAsync(connection, transaction,
                "SELECT set_config('weft.organization_id',$1,true),set_config('weft.project_id',$2,true),set_config('lock_timeout','1s',true),set_config('statement_timeout','5s',true)",
                [Project.OrganizationId.ToString(), Project.ProjectId.ToString()]);
            return (connection, transaction);
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        try { await MaintenanceDatabase.DisposeAsync(); }
        finally { try { await IngestionDatabase.DisposeAsync(); } finally { await source.DisposeAsync(); } }
    }
}
