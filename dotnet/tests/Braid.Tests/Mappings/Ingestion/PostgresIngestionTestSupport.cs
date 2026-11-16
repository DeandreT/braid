using System.Text;
using System.Text.Json;
using Npgsql;
using Braid.Application;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

/// <summary>C1 exercises actual PostgreSQL with explicitly synthetic admission evidence; no storage/native qualification.</summary>
internal sealed class PostgresIngestionTestSupport : IAsyncDisposable
{
    internal PostgresCase Case { get; }
    internal PostgresSeedData Seed { get; }
    internal ProjectScope Project { get; }
    internal ActorContext Actor { get; }
    internal PostgresIngestionDatabase Database { get; }
    internal PostgresRevisionIngestionStore Store { get; }
    internal static IngestionBackendProfile Profile { get; } = new("ferrule", "synthetic.c1.engine.v1", "synthetic.c1.worker.v1", "synthetic.c1.adapter.v1");
    internal static IngestionAdmissionPolicy Policy { get; } = new("braid.native-admission.v1", 4 * 1024 * 1024);
    internal static byte[] SyntheticBody => Encoding.UTF8.GetBytes("C1 synthetic metadata fixture; no qualified native validator or object store was invoked.");

    private PostgresIngestionTestSupport(PostgresCase test, PostgresSeedData seed)
    {
        Case = test;
        Seed = seed;
        Project = new(seed.OrganizationA, seed.ProjectA);
        Actor = new(new PrincipalId(seed.Alice));
        Database = new(test.IngestionConnectionString);
        Store = new(Database);
    }

    internal static async Task<PostgresIngestionTestSupport> CreateAsync(PostgresFixture fixture)
    {
        var test = await fixture.CreateCaseAsync();
        try
        {
            var seed = await PostgresSeedData.CreateAsync(test);
            await test.ExecuteAsync("UPDATE braid.project_memberships SET role='publisher',version=version+1 WHERE principal_id=$1", seed.Alice);
            await seed.AddMembershipAsync(test, seed.OrganizationA, seed.ProjectA, seed.Admitted, "publisher");
            return new(test, seed);
        }
        catch { await test.DisposeAsync(); throw; }
    }

    internal static IngestionIntent CreateIntent(string name = "Synthetic C1 mapping", string description = "Synthetic admission evidence only", string? filename = null,
        byte[]? body = null, string backend = "ferrule")
    {
        var bytes = body ?? SyntheticBody;
        return IngestionIntent.ForCreate(new(name, description), backend, filename, Sha256Digest.FromBytes(bytes), bytes.LongLength);
    }

    internal static IngestionIntent AppendIntent(IngestionReceipt mapping, HeadCondition? head = null, byte[]? body = null)
    {
        var scope = mapping.Facts.FutureRevision;
        var bytes = body ?? SyntheticBody;
        return IngestionIntent.ForAppend(new(scope.OrganizationId, scope.ProjectId, scope.MappingId), mapping.Facts.BackendProfile.BackendId,
            null, head ?? new AnyHead(), Sha256Digest.FromBytes(bytes), bytes.LongLength);
    }

    internal async Task<ReservedIngestionFacts> ReserveAsync(Guid? key = null, IngestionIntent? intent = null)
    {
        var result = await Store.ReserveAsync(Actor, Project, key ?? Guid.NewGuid(), intent ?? CreateIntent(), Profile, Policy);
        Assert.Equal(IngestionStoreOutcome.Reserved, result.Outcome);
        return Assert.IsType<IngestionReceipt>(result.Receipt).Facts;
    }

    internal async Task<IngestionClaim> ClaimAsync(ReservedIngestionFacts facts)
    {
        var result = await Store.ClaimAsync(Actor, facts.Project, facts.OperationId, Guid.NewGuid());
        Assert.Equal(IngestionStoreOutcome.Claimed, result.Outcome);
        return Assert.IsType<IngestionClaim>(result.Claim);
    }

    internal async Task StageAsync(ReservedIngestionFacts facts, IngestionClaim claim)
    {
        var result = await Store.MarkStagedAsync(Actor, claim, facts.NativeObject);
        Assert.Equal(IngestionStoreOutcome.Applied, result.Outcome);
        Assert.Equal(claim.Generation, result.Receipt!.VerifiedStageGeneration);
    }

    internal static IngestionAttemptCompletion SyntheticCompletion(ReservedIngestionFacts facts, bool valid = true,
        IngestionProvenance? provenance = null, IngestionBackendStatus? status = null, JsonElement? diagnostics = null,
        int total = 0, int emitted = 0, bool truncated = false)
    {
        var observed = provenance ?? new(facts.FutureRevision, facts.NativeObject.Digest, facts.BackendProfile, true);
        var validation = new IngestionValidationFacts(status ?? (valid ? IngestionBackendStatus.Succeeded : IngestionBackendStatus.InvalidMapping),
            observed, valid, total, emitted, truncated);
        using var empty = JsonDocument.Parse("[]");
        return IngestionAttemptCompletion.FromValidation(IngestionEvidenceDocument.Create(validation, diagnostics ?? empty.RootElement));
    }

    internal async Task<IngestionReceipt> ReadyAsync(ReservedIngestionFacts facts, IngestionClaim claim, bool valid = true)
    {
        await StageAsync(facts, claim);
        var result = await Store.CompleteAttemptAsync(Actor, claim, SyntheticCompletion(facts, valid));
        Assert.Equal(IngestionStoreOutcome.Applied, result.Outcome);
        Assert.Equal(IngestionPhase.Ready, result.Receipt!.Phase);
        return result.Receipt;
    }

    internal async Task<IngestionReceipt> CommitCreateAsync()
    {
        var facts = await ReserveAsync();
        var claim = await ClaimAsync(facts);
        await ReadyAsync(facts, claim);
        var result = await Store.FinalizeAsync(Actor, claim);
        Assert.Equal(IngestionStoreOutcome.Applied, result.Outcome);
        Assert.Equal(IngestionPhase.Committed, result.Receipt!.Phase);
        return result.Receipt;
    }

    internal async Task ExpireLeaseAsync(ReservedIngestionFacts facts)
    {
        // Explicit synthetic clock control in this disposable database only. Production exposes
        // no clock override; disabling/re-enabling the guard is atomic and isolated to this row.
        await using var connection = await Case.Owner.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var disable = new NpgsqlCommand("ALTER TABLE braid.revision_ingestions DISABLE TRIGGER revision_ingestions_transition_guard", connection, transaction))
            await disable.ExecuteNonQueryAsync();
        await using (var expire = new NpgsqlCommand("UPDATE braid.revision_ingestions SET lease_expires_at=clock_timestamp()-interval '1 second' WHERE organization_id=$1 AND project_id=$2 AND operation_id=$3", connection, transaction))
        {
            expire.Parameters.AddWithValue(facts.Project.OrganizationId);
            expire.Parameters.AddWithValue(facts.Project.ProjectId);
            expire.Parameters.AddWithValue(facts.OperationId);
            Assert.Equal(1, await expire.ExecuteNonQueryAsync());
        }
        await using (var enable = new NpgsqlCommand("ALTER TABLE braid.revision_ingestions ENABLE TRIGGER revision_ingestions_transition_guard", connection, transaction))
            await enable.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Database.DisposeAsync();
        await Case.DisposeAsync();
    }
}
