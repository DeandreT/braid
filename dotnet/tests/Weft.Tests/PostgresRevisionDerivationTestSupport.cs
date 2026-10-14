using System.Text;
using Npgsql;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

/// <summary>
/// Actual PostgreSQL metadata fixtures with explicitly synthetic C1 admission
/// and trusted publication facts. No native worker, object store or report codec runs here.
/// </summary>
internal sealed class PostgresRevisionDerivationTestSupport : IAsyncDisposable
{
    private readonly PostgresCatalogTestSupport catalog;
    internal PostgresCase Case => catalog.Case;
    internal PostgresSeedData Seed => catalog.Seed;
    internal ActorContext Actor => catalog.Actor;
    internal ActorContext AdmittedActor => new(new PrincipalId(Seed.Admitted));
    internal ProjectScope Project => catalog.Project;
    internal IngestionReceipt SourceReceipt { get; }
    internal RevisionScope Revision => SourceReceipt.Facts.FutureRevision;
    internal RevisionReportSource Source { get; }
    internal ProjectionCacheKey Key => ProjectionKey();
    internal PostgresRevisionReportDatabase ReportDatabase { get; }
    internal PostgresRevisionDerivationDatabase DerivationDatabase { get; }
    internal PostgresRevisionReportMetadataReader Reader { get; }
    internal PostgresRevisionDerivationStore Store { get; }
    internal static RevisionReportBackendProfile Profile { get; } =
        new("ferrule", "synthetic.report.engine.v2", "synthetic.report.worker.v2", "synthetic.report.adapter.v2");

    private PostgresRevisionDerivationTestSupport(PostgresCatalogTestSupport catalog, IngestionReceipt receipt)
    {
        this.catalog = catalog;
        SourceReceipt = receipt;
        var facts = receipt.Facts;
        Source = new(facts.FutureRevision, facts.NativeObject, facts.Intent.LogicalDocument,
            facts.Intent.DependencyIdentity, facts.BackendProfile.BackendId);
        ReportDatabase = new(Case.ReportConnectionString);
        DerivationDatabase = new(Case.DerivationConnectionString);
        Reader = new(ReportDatabase);
        Store = new(DerivationDatabase);
    }

    internal static async Task<PostgresRevisionDerivationTestSupport> CreateAsync(PostgresFixture fixture)
    {
        var catalog = await PostgresCatalogTestSupport.CreateAsync(fixture);
        PostgresRevisionDerivationTestSupport? support = null;
        try
        {
            await catalog.Seed.AddMembershipAsync(catalog.Case, catalog.Seed.OrganizationA,
                catalog.Seed.ProjectA, catalog.Seed.Admitted, "viewer");
            var receipt = await catalog.CommitCreateAsync("Synthetic report source", "C1 metadata only; no native validation or storage");
            support = new(catalog, receipt);
            var observed = Assert.IsType<AuthorizedRevisionReportSource>(await support.Reader.GetSourceAsync(support.Actor, support.Revision));
            Assert.Equal(support.Source, observed.Source);
            Assert.Equal(support.Actor.PrincipalId, observed.Actor);
            Assert.Equal(TimeSpan.Zero, observed.ObservedAtUtc.Offset);
            Assert.NotEqual(receipt.Facts.BackendProfile.EngineRevision, Profile.BackendRevision);
            return support;
        }
        catch
        {
            if (support is not null) await support.DisposeAsync();
            else await catalog.DisposeAsync();
            throw;
        }
    }

    internal ProjectionCacheKey ProjectionKey(RevisionReportPolicy? policy = null,
        RevisionReportBackendProfile? profile = null, string schema = "weft.mapping-projection.v1",
        string algorithm = "synthetic.report.projection.v1", string codec = "synthetic.report.codec.v1",
        RevisionReportSource? source = null) => new(source ?? Source, profile ?? Profile, schema, algorithm, codec, policy ?? new());

    internal ValidationCacheKey ValidationKey(RevisionReportPolicy? policy = null,
        RevisionReportBackendProfile? profile = null, string schema = "weft.mapping-validation.v1",
        string algorithm = "synthetic.report.validation.v1", string codec = "synthetic.report.codec.v1",
        RevisionReportSource? source = null) => new(source ?? Source, profile ?? Profile, schema, algorithm, codec, policy ?? new());

    internal async Task<RevisionReportClaim> ClaimAsync(RevisionReportKey? key = null, ActorContext? actor = null,
        Guid? instance = null, CancellationToken cancellationToken = default)
    {
        var result = await Store.TryClaimAsync(actor ?? Actor, key ?? Key, instance ?? Guid.NewGuid(), cancellationToken);
        Assert.Equal(RevisionReportStoreOutcome.Claimed, result.Outcome);
        var claim = Assert.IsType<RevisionReportClaim>(result.Claim);
        Assert.Equal(claim.DerivationId, result.Snapshot!.CurrentAttempt!.DerivationId);
        Assert.True(result.Snapshot.IsClaimLive);
        return claim;
    }

    internal async Task<RevisionReportResult> PublishSyntheticAsync(RevisionReportKey? key = null,
        ActorContext? actor = null, bool valid = true, RevisionReportCoverage coverage = RevisionReportCoverage.Complete)
    {
        var claim = await ClaimAsync(key, actor);
        var completion = claim.Key.Operation == RevisionReportOperation.Projection
            ? SyntheticProjectionCompletion(claim, coverage)
            : SyntheticValidationCompletion(claim, valid, total: valid ? 0 : 1, emitted: valid ? 0 : 1);
        var result = await Store.FinishAsync(actor ?? Actor, claim, completion);
        Assert.Equal(RevisionReportStoreOutcome.Applied, result.Outcome);
        return Assert.IsType<RevisionReportResult>(result.Snapshot!.SelectedResult);
    }

    internal static RevisionReportCompletion SyntheticProjectionCompletion(RevisionReportClaim claim,
        RevisionReportCoverage coverage = RevisionReportCoverage.Complete)
    {
        var gaps = coverage == RevisionReportCoverage.Complete ? 0 : 1;
        var observation = new RevisionReportObservation(RevisionReportOperation.Projection,
            RevisionReportBackendStatus.Succeeded, SyntheticProvenance(claim.Key),
            new(coverage, 3 + gaps, 0, gaps, 0), null);
        return RevisionReportCompletion.ForUsable(claim.Key, observation, SyntheticDerivedReference(claim));
    }

    internal static RevisionReportCompletion SyntheticValidationCompletion(RevisionReportClaim claim,
        bool valid = true, int total = 0, int emitted = 0, bool truncated = false) =>
        RevisionReportCompletion.ForUsable(claim.Key,
            new(RevisionReportOperation.Validation, valid ? RevisionReportBackendStatus.Succeeded : RevisionReportBackendStatus.InvalidMapping,
                SyntheticProvenance(claim.Key), null, new(valid, total, emitted, truncated, emitted)), SyntheticDerivedReference(claim));

    internal static RevisionReportProvenance SyntheticProvenance(RevisionReportKey key, bool confirmed = true) =>
        new(key.Scope, key.NativeDigest, key.Profile, confirmed);

    internal static PrivateObjectReference SyntheticDerivedReference(RevisionReportClaim claim)
    {
        // This reference is an explicitly synthetic trusted publication assertion,
        // not a claim that these bytes are a valid report or exist in private storage.
        var bytes = Encoding.UTF8.GetBytes("Synthetic #49 metadata publication; no private report object was written.");
        return new(claim.CandidateObjectId, Sha256Digest.FromBytes(bytes), bytes.LongLength);
    }

    internal Task<long[]> CountsAsync() => Case.ScalarAsync<long[]>("""
        SELECT ARRAY[(SELECT count(*) FROM weft.revision_derivations),
                     (SELECT count(*) FROM weft.revision_derivation_attempts),
                     (SELECT count(*) FROM weft.revision_derivation_attempts WHERE result_usable),
                     (SELECT count(*) FROM weft.revision_derivation_attempts WHERE completion_kind='failed')]
        """);

    internal Task<string> ImmutableSourceSnapshotAsync() => Case.ScalarAsync<string>("""
        SELECT jsonb_build_object('mapping',(SELECT to_jsonb(m) FROM weft.mappings m
            WHERE organization_id=$1 AND project_id=$2 AND id=$3),
            'revision',(SELECT to_jsonb(r) FROM weft.mapping_revisions r
            WHERE organization_id=$1 AND project_id=$2 AND mapping_id=$3 AND id=$4),
            'ingestion',(SELECT to_jsonb(i) FROM weft.revision_ingestions i
            WHERE organization_id=$1 AND project_id=$2 AND operation_id=$5))::text
        """, Revision.OrganizationId, Revision.ProjectId, Revision.MappingId, Revision.RevisionId, SourceReceipt.Facts.OperationId);

    internal Task<string> AttemptSnapshotAsync(Guid derivationId) => Case.ScalarAsync<string>("""
        SELECT to_jsonb(a)::text FROM weft.revision_derivation_attempts a
        WHERE organization_id=$1 AND project_id=$2 AND mapping_id=$3 AND revision_id=$4 AND derivation_id=$5
        """, Revision.OrganizationId, Revision.ProjectId, Revision.MappingId, Revision.RevisionId, derivationId);

    // Borrow an explicitly owned fresh connection for restart readbacks without reopening Case.Owner.
    internal Task<long[]> CountsAsync(NpgsqlConnection owner) => OwnerScalarAsync<long[]>(owner, """
        SELECT ARRAY[(SELECT count(*) FROM weft.revision_derivations),
                     (SELECT count(*) FROM weft.revision_derivation_attempts),
                     (SELECT count(*) FROM weft.revision_derivation_attempts WHERE result_usable),
                     (SELECT count(*) FROM weft.revision_derivation_attempts WHERE completion_kind='failed')]
        """);

    internal Task<string> ImmutableSourceSnapshotAsync(NpgsqlConnection owner) => OwnerScalarAsync<string>(owner, """
        SELECT jsonb_build_object('mapping',(SELECT to_jsonb(m) FROM weft.mappings m
            WHERE organization_id=$1 AND project_id=$2 AND id=$3),
            'revision',(SELECT to_jsonb(r) FROM weft.mapping_revisions r
            WHERE organization_id=$1 AND project_id=$2 AND mapping_id=$3 AND id=$4),
            'ingestion',(SELECT to_jsonb(i) FROM weft.revision_ingestions i
            WHERE organization_id=$1 AND project_id=$2 AND operation_id=$5))::text
        """, Revision.OrganizationId, Revision.ProjectId, Revision.MappingId, Revision.RevisionId, SourceReceipt.Facts.OperationId);

    internal Task<string> AttemptSnapshotAsync(Guid derivationId, NpgsqlConnection owner) => OwnerScalarAsync<string>(owner, """
        SELECT to_jsonb(a)::text FROM weft.revision_derivation_attempts a
        WHERE organization_id=$1 AND project_id=$2 AND mapping_id=$3 AND revision_id=$4 AND derivation_id=$5
        """, Revision.OrganizationId, Revision.ProjectId, Revision.MappingId, Revision.RevisionId, derivationId);

    private static async Task<T> OwnerScalarAsync<T>(NpgsqlConnection owner, string sql, params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, owner);
        foreach (var value in values)
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        return (T)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Expected fixture scalar."));
    }

    internal async Task<Guid> InsertSyntheticEmptyKeyAsync(RevisionReportKey key)
    {
        // Explicit structural generation-zero fixture, not a production reservation.
        // The ordinary INSERT guard, every CHECK and every scoped FK remain enabled.
        await using var connection = await OpenOwnerAsync(Case);
        await using var command = new NpgsqlCommand("""
            INSERT INTO weft.revision_derivations
              (organization_id,project_id,mapping_id,revision_id,operation_kind,native_object_id,native_class,
               native_digest,native_byte_count,logical_entry_document,dependency_manifest_id,backend_id,
               engine_revision,backend_build,adapter_build,schema_version,algorithm_version,codec_version,
               policy_schema_version,policy_json,policy_fingerprint,native_byte_limit,worker_response_byte_limit,
               item_limit,response_item_limit,json_depth_limit,detail_depth_limit,diagnostic_limit,
               validation_issue_limit,text_byte_limit,header_byte_limit,stored_object_byte_limit,response_byte_limit,key_fingerprint)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,$20,$21,
                    $22,$23,$24,$25,$26,$27,$28,$29,$30,$31,$32,$33,$34)
            RETURNING cache_entry_id
            """, connection);
        AddScope(command, key.Scope);
        object[] values =
        [
            key.Operation == RevisionReportOperation.Projection ? "projection" : "validation",
            key.Source.NativeObject.Id.ObjectId, "native", key.NativeDigest.Value, key.Source.NativeObject.ByteCount,
            key.Source.LogicalDocument, key.Source.DependencyIdentity, key.BackendId,
            key.BackendRevision, key.BackendBuild, key.AdapterBuild, key.SchemaVersion, key.AlgorithmVersion, key.CodecVersion,
            key.Policy.SchemaVersion, Encoding.UTF8.GetString(key.Policy.CopyCanonicalUtf8()), key.Policy.Fingerprint.Value,
            key.Policy.NativeByteLimit, key.Policy.WorkerResponseByteLimit, key.Policy.ItemLimit, key.Policy.ResponseItemLimit,
            key.Policy.JsonDepthLimit, key.Policy.DetailDepthLimit, key.Policy.DiagnosticLimit, key.Policy.ValidationIssueLimit,
            key.Policy.TextByteLimit, key.Policy.HeaderByteLimit, key.Policy.StoredObjectByteLimit, key.Policy.ResponseByteLimit,
            key.Fingerprint.Value
        ];
        foreach (var value in values) command.Parameters.AddWithValue(value);
        return (Guid)(await command.ExecuteScalarAsync())!;
    }

    internal Task SyntheticExpireClaimAsync(RevisionReportClaim claim) => SyntheticClockAsync(claim, """
        WITH synthetic_clock AS (SELECT clock_timestamp() AS value)
        UPDATE weft.revision_derivation_attempts a
        SET started_at=synthetic_clock.value-interval '61 seconds',lease_expires_at=synthetic_clock.value-interval '1 second'
        FROM synthetic_clock WHERE organization_id=$1 AND project_id=$2 AND mapping_id=$3 AND revision_id=$4
          AND derivation_id=$5 AND finished_at IS NULL
        """);

    internal Task SyntheticMakeRetryEligibleAsync(RevisionReportClaim claim) => SyntheticClockAsync(claim, """
        UPDATE weft.revision_derivation_attempts
        SET started_at=started_at-interval '31 seconds',lease_expires_at=lease_expires_at-interval '31 seconds',
            finished_at=finished_at-interval '31 seconds'
        WHERE organization_id=$1 AND project_id=$2 AND mapping_id=$3 AND revision_id=$4
          AND derivation_id=$5 AND completion_kind='failed'
        """);

    private async Task SyntheticClockAsync(RevisionReportClaim claim, string statement)
    {
        // Synthetic owner clock adjustment only. Every FK/CHECK remains enabled,
        // the exact intended transition guard is restored before atomic commit.
        await using var connection = await OpenOwnerAsync(Case);
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var disable = new NpgsqlCommand("ALTER TABLE weft.revision_derivation_attempts DISABLE TRIGGER revision_derivation_attempts_transition_guard", connection, transaction))
            await disable.ExecuteNonQueryAsync();
        await using (var update = new NpgsqlCommand(statement, connection, transaction))
        {
            AddScope(update, claim.Key.Scope);
            update.Parameters.AddWithValue(claim.DerivationId);
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }
        await using (var flush = new NpgsqlCommand("SET CONSTRAINTS ALL IMMEDIATE", connection, transaction))
            await flush.ExecuteNonQueryAsync();
        await using (var enable = new NpgsqlCommand("ALTER TABLE weft.revision_derivation_attempts ENABLE TRIGGER revision_derivation_attempts_transition_guard", connection, transaction))
            await enable.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    internal static void AddScope(NpgsqlCommand command, RevisionScope scope)
    {
        command.Parameters.AddWithValue(scope.OrganizationId);
        command.Parameters.AddWithValue(scope.ProjectId);
        command.Parameters.AddWithValue(scope.MappingId);
        command.Parameters.AddWithValue(scope.RevisionId);
    }

    internal static Task<NpgsqlConnection> OpenOwnerAsync(PostgresCase test) => ProjectCreationTestSupport.OpenOwnerAsync(test);
    internal static Task<int> BackendAsync(NpgsqlDataSource pool) => ProjectCreationTestSupport.BackendAsync(pool);
    internal static Task WaitForBlockedAsync(PostgresCase test, int contenderPid, int blockerPid, Task operation,
        CancellationToken cancellationToken = default) =>
        ProjectCreationTestSupport.WaitForBlockedAsync(test, contenderPid, blockerPid, operation, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        try { await ReportDatabase.DisposeAsync(); }
        finally
        {
            try { await DerivationDatabase.DisposeAsync(); }
            finally { await catalog.DisposeAsync(); }
        }
    }
}
