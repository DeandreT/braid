using System.Globalization;
using System.Net;
using System.Text.Json;
using Npgsql;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

/// <summary>Assertions over real HTTP replies and independently observed owned PostgreSQL facts.</summary>
internal static class ProjectCreationHttpTransactionSupport
{
    internal static Task<HttpResponseMessage> PostAsync(ProjectCreationHttpTestSupport api, string token, Guid key,
        ProjectCreationMetadata? metadata = null, CancellationToken cancellationToken = default) => api.PostAsync(
            ProjectCreationHttpTestSupport.CreatePath(api.OrganizationId), token, key,
            ProjectCreationHttpTestSupport.Body((metadata ?? ProjectCreationTestSupport.DefaultMetadata).Name,
                (metadata ?? ProjectCreationTestSupport.DefaultMetadata).Description), cancellationToken: cancellationToken);

    internal static async Task<JsonElement> SuccessAsync(HttpResponseMessage response, HttpStatusCode status,
        string outcome, Guid organization, Guid actor, ProjectCreationMetadata metadata)
    {
        var value = await ProjectCreationHttpTestSupport.JsonAsync(response, status);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(new[] { "outcome", "projectUrl", "receipt" },
            value.EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal));
        Assert.Equal(outcome, value.GetProperty("outcome").GetString());
        var receipt = value.GetProperty("receipt");
        Assert.Equal(new[] { "createdAtUtc", "creatorPrincipalId", "description", "initialMembershipActive",
            "initialMembershipRole", "initialMembershipVersion", "initialProjectVersion", "name", "project" },
            receipt.EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal));
        var scope = Scope(receipt);
        Assert.Equal(organization, scope.OrganizationId);
        Assert.NotEqual(Guid.Empty, scope.ProjectId);
        Assert.Equal(actor.ToString("D"), receipt.GetProperty("creatorPrincipalId").GetString());
        Assert.Equal(metadata.Name, receipt.GetProperty("name").GetString());
        Assert.Equal(metadata.Description, receipt.GetProperty("description").GetString());
        Assert.Equal(TimeSpan.Zero, DateTimeOffset.Parse(receipt.GetProperty("createdAtUtc").GetString()!,
            CultureInfo.InvariantCulture).Offset);
        Assert.Equal("1", receipt.GetProperty("initialProjectVersion").GetString());
        Assert.Equal("administrator", receipt.GetProperty("initialMembershipRole").GetString());
        Assert.True(receipt.GetProperty("initialMembershipActive").GetBoolean());
        Assert.Equal("1", receipt.GetProperty("initialMembershipVersion").GetString());
        var path = CatalogHttpTestSupport.ProjectPath(scope);
        Assert.Equal(path, value.GetProperty("projectUrl").GetString());
        Assert.Equal(path, response.Headers.Location?.OriginalString);
        Assert.False(response.Headers.Location!.IsAbsoluteUri);
        Assert.Null(response.Headers.ETag);
        return receipt;
    }

    internal static async Task ErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var value = await ProjectCreationHttpTestSupport.JsonAsync(response, status);
        Assert.Equal(new[] { "code" }, value.EnumerateObject().Select(x => x.Name));
        Assert.Equal(code, value.GetProperty("code").GetString());
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Null(response.Headers.Location);
        Assert.Null(response.Headers.ETag);
    }

    internal static ProjectScope Scope(JsonElement receipt) => new(
        receipt.GetProperty("project").GetProperty("organizationId").GetGuid(),
        receipt.GetProperty("project").GetProperty("projectId").GetGuid());

    internal static async Task AssertCreatedRowsAsync(ProjectCreationTestSupport test, JsonElement receipt,
        Guid key, Guid actor, ProjectCreationMetadata metadata, long[] before)
    {
        Assert.Equal(before.Select(x => x + 1), await test.CountsAsync());
        var scope = Scope(receipt);
        var createdAt = DateTimeOffset.Parse(receipt.GetProperty("createdAtUtc").GetString()!, CultureInfo.InvariantCulture);
        Assert.True(await test.Case.ScalarAsync<bool>("""
            SELECT project.name=$3 AND project.description=$4 AND project.creator_principal_id=$5
              AND project.version=1 AND project.created_at=project.updated_at AND project.created_at=$6
              AND member.principal_id=$5 AND member.role='administrator' AND member.active AND member.version=1
              AND fact.original_actor_id=$5 AND fact.idempotency_key=$7 AND fact.project_id=project.id
              AND fact.name=$3 AND fact.description=$4 AND fact.created_at=$6
              AND fact.initial_project_version=1 AND fact.initial_membership_role='administrator'
              AND fact.initial_membership_active AND fact.initial_membership_version=1
              AND fact.fingerprint_version=1 AND fact.fingerprint=$8
            FROM braid.projects project JOIN braid.project_memberships member
              ON member.organization_id=project.organization_id AND member.project_id=project.id
            JOIN braid.project_creations fact
              ON fact.organization_id=project.organization_id AND fact.project_id=project.id
            WHERE project.organization_id=$1 AND project.id=$2
            """, scope.OrganizationId, scope.ProjectId, metadata.Name, metadata.Description, actor, createdAt, key,
            ProjectCreationTestSupport.IndependentFingerprint(scope.OrganizationId, actor, key, metadata.Name, metadata.Description)));
    }

    internal static async Task AssertUnboundAsync(PostgresProjectCreationDatabase database, int expectedBackend)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var connection = await database.DataSource.OpenConnectionAsync(deadline.Token);
        await using (var identity = new NpgsqlCommand("""
            SELECT pg_backend_pid(),NULLIF(current_setting('braid.organization_id',true),''),
                current_user,session_user,pg_catalog.txid_current_if_assigned()
            """, connection))
        await using (var reader = await identity.ExecuteReaderAsync(deadline.Token))
        {
            Assert.True(await reader.ReadAsync(deadline.Token));
            Assert.Equal(expectedBackend, reader.GetInt32(0));
            Assert.True(reader.IsDBNull(1));
            Assert.Equal("braid_project_creator", reader.GetString(2));
            Assert.Equal("braid_project_creator", reader.GetString(3));
            Assert.True(reader.IsDBNull(4));
        }
        foreach (var (setting, expected) in new[]
        {
            ("transaction_read_only", "off"), ("transaction_isolation", "read committed"),
            ("lock_timeout", "0"), ("statement_timeout", "0")
        })
        {
            await using var command = new NpgsqlCommand($"SHOW {setting}", connection);
            Assert.Equal(expected, await command.ExecuteScalarAsync(deadline.Token));
        }
        await using var locks = new NpgsqlCommand("""
            SELECT count(*) FROM pg_catalog.pg_locks held
            LEFT JOIN pg_catalog.pg_class relation ON relation.oid=held.relation
            LEFT JOIN pg_catalog.pg_namespace namespace ON namespace.oid=relation.relnamespace
            WHERE held.pid=pg_backend_pid()
              AND (held.locktype IN ('transactionid','tuple','advisory') OR namespace.nspname='braid')
            """, connection);
        Assert.Equal(0L, await locks.ExecuteScalarAsync(deadline.Token));
        await using var fresh = await connection.BeginTransactionAsync(deadline.Token);
        await fresh.RollbackAsync(deadline.Token);
    }

    internal static async Task<int> BackendAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand("SELECT pg_backend_pid()", connection);
        return (int)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Missing owned backend."));
    }

    internal static async Task LockFactsAsync(NpgsqlConnection owner, NpgsqlTransaction transaction)
    {
        await using var command = new NpgsqlCommand("LOCK TABLE ONLY braid.project_creations IN SHARE MODE", owner, transaction);
        await command.ExecuteNonQueryAsync();
    }

    internal static async Task AssertLateFactInsertLocksAsync(PostgresCase test, int creatorPid)
    {
        await using var owner = await ProjectCreationTestSupport.OpenOwnerAsync(test);
        await using var command = new NpgsqlCommand("""
            SELECT
                EXISTS (SELECT 1 FROM pg_catalog.pg_locks WHERE pid=$1 AND locktype='relation'
                    AND relation='braid.project_creations'::regclass AND mode='RowExclusiveLock' AND NOT granted),
                EXISTS (SELECT 1 FROM pg_catalog.pg_locks WHERE pid=$1 AND locktype='relation'
                    AND relation='braid.projects'::regclass AND mode='RowExclusiveLock' AND granted),
                EXISTS (SELECT 1 FROM pg_catalog.pg_locks WHERE pid=$1 AND locktype='relation'
                    AND relation='braid.project_memberships'::regclass AND mode='RowExclusiveLock' AND granted)
            """, owner);
        command.Parameters.AddWithValue(creatorPid);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0), "Actual HTTP creator waits for its late creation-fact INSERT.");
        Assert.True(reader.GetBoolean(1), "Actual HTTP creator already holds its project write lock.");
        Assert.True(reader.GetBoolean(2), "Actual HTTP creator already holds its first-member write lock.");
    }

    internal static async Task DrainAsync(Task<HttpResponseMessage> operation)
    {
        try { using var response = await operation.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (OperationCanceledException) { }
        catch (HttpRequestException) { }
    }
}
