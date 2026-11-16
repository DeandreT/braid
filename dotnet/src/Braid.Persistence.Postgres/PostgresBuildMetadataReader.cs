using System.Data;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using NpgsqlTypes;
using Braid.Application;
using Braid.Deployment;
using Braid.Domain;

namespace Braid.Persistence.Postgres;

/// <summary>Retained own metadata only. It cannot authorize execution or read private artifact/native bytes.</summary>
public sealed class PostgresBuildMetadataReader(string connectionString) : IBuildMetadataReader, IAsyncDisposable
{
    private readonly NpgsqlDataSource source = NpgsqlDataSource.Create(PostgresConfiguration.Normalize(connectionString));
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 16
    };
    private sealed record Row(string Document, byte[]? Intent);
    private sealed record ReadResult(IReadOnlyList<Row> Rows, bool More);

    public async Task<RetainedBuildEnvironment?> ReadEnvironmentAsync(ActorContext actor, BuildEnvironmentReference environment, CancellationToken token = default)
    {
        var rows = await Read(actor, environment.Project, Guid.Empty, "environment", environment.EnvironmentId, environment.Version, 1, null, null, false, token);
        if (rows is null) return null;
        var value = Environment(rows.Rows.Single(), environment.Project);
        return value.Environment.Reference == environment ? value : throw Invalid();
    }
    public async Task<BuildEnvironmentPage?> ListEnvironmentsAsync(ActorContext actor, ProjectScope project, int maximum,
        BuildEnvironmentCursor? after = null, CancellationToken token = default)
    {
        Bound(maximum);
        if (after is not null && (after.EnvironmentId == Guid.Empty || after.Version < 1)) throw Invalid();
        var rows = await Read(actor, project, Guid.Empty, "environments", null, null, maximum, after?.EnvironmentId, after?.Version, true, token);
        if (rows is null) return null;
        var values = rows.Rows.Select(row => Environment(row, project)).ToArray();
        if (values.Select(value => value.Environment.Reference).Distinct().Count() != values.Length) throw Invalid();
        var selected = values.Take(maximum).ToArray();
        var last = selected.LastOrDefault()?.Environment.Reference;
        return new(Array.AsReadOnly(selected), rows.More && last is not null ? new(last.EnvironmentId, last.Version) : null);
    }
    public async Task<RetainedBuildApproval?> ReadApprovalAsync(ActorContext actor, MappingScope mapping, Guid approvalId, CancellationToken token = default)
    {
        if (approvalId == Guid.Empty) throw Invalid();
        var rows = await Read(actor, Project(mapping), mapping.MappingId, "approval", approvalId, null, 1, null, null, false, token);
        if (rows is null) return null;
        var value = Approval(rows.Rows.Single(), actor, mapping);
        return value.Intent.ApprovalId == approvalId ? value : throw Invalid();
    }
    public async Task<BuildApprovalPage?> ListApprovalsAsync(ActorContext actor, MappingScope mapping, int maximum,
        Guid? afterApprovalId = null, CancellationToken token = default)
    {
        Bound(maximum); Cursor(afterApprovalId);
        var rows = await Read(actor, Project(mapping), mapping.MappingId, "approvals", null, null, maximum, afterApprovalId, null, true, token);
        if (rows is null) return null;
        var values = rows.Rows.Select(row => Approval(row, actor, mapping)).ToArray();
        if (values.Select(value => value.Intent.ApprovalId).Distinct().Count() != values.Length) throw Invalid();
        var selected = values.Take(maximum).ToArray();
        return new(Array.AsReadOnly(selected), rows.More ? selected[^1].Intent.ApprovalId : null);
    }
    public async Task<BuildRunPage?> ListRunsAsync(ActorContext actor, MappingScope mapping, int maximum,
        Guid? afterBuildId = null, CancellationToken token = default)
    {
        Bound(maximum); Cursor(afterBuildId);
        var rows = await Read(actor, Project(mapping), mapping.MappingId, "runs", null, null, maximum, afterBuildId, null, true, token);
        if (rows is null) return null;
        var values = rows.Rows.Select(row => Run(row, actor, mapping)).ToArray();
        if (values.Select(value => value.Intent.BuildId).Distinct().Count() != values.Length) throw Invalid();
        var selected = values.Take(maximum).ToArray();
        return new(Array.AsReadOnly(selected), rows.More ? selected[^1].Intent.BuildId : null);
    }

    private async Task<ReadResult?> Read(ActorContext actor, ProjectScope project, Guid mapping, string operation,
        Guid? key, long? version, int maximum, Guid? after, long? afterVersion, bool page, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var connection = await source.OpenConnectionAsync(token);
        NpgsqlTransaction? transaction = null;
        try
        {
            transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
            await using (var settings = new NpgsqlCommand("SET TRANSACTION READ ONLY; SET LOCAL search_path=pg_catalog; SET LOCAL TimeZone='UTC'; SET LOCAL DateStyle='ISO, MDY'; SET LOCAL statement_timeout='15s'; SET LOCAL lock_timeout='5s'", connection, transaction))
                await settings.ExecuteNonQueryAsync(token);
            await BuildMetadataRoleGuard.ValidateAsync(connection, transaction, token);
            await using (var identity = new NpgsqlCommand("SELECT set_config('braid.build_principal_id',@actor,true)", connection, transaction))
            {
                identity.Parameters.AddWithValue("actor", actor.PrincipalId.Value.ToString("D"));
                await identity.ExecuteNonQueryAsync(token);
            }
            await using var tx = new PostgresTransaction(connection, transaction, project.OrganizationId);
            await tx.BindOrganizationAsync(project.OrganizationId, token);
            await using var command = tx.CreateCommand("SELECT document::text,intent FROM braid.read_build_metadata(@op,@org,@project,@mapping,@key,@version,@maximum,@after,@afterVersion)");
            command.Parameters.AddWithValue("op", operation); command.Parameters.AddWithValue("org", project.OrganizationId);
            command.Parameters.AddWithValue("project", project.ProjectId); command.Parameters.AddWithValue("mapping", mapping);
            command.Parameters.AddWithValue("key", NpgsqlDbType.Uuid, (object?)key ?? DBNull.Value);
            command.Parameters.AddWithValue("version", NpgsqlDbType.Bigint, (object?)version ?? DBNull.Value);
            command.Parameters.AddWithValue("maximum", maximum);
            command.Parameters.AddWithValue("after", NpgsqlDbType.Uuid, (object?)after ?? DBNull.Value);
            command.Parameters.AddWithValue("afterVersion", NpgsqlDbType.Bigint, (object?)afterVersion ?? DBNull.Value);
            var values = new List<Row>();
            var count = 0; var bytes = 0;
            var more = false;
            await using (var rows = await command.ExecuteReaderAsync(token))
            {
                while (await rows.ReadAsync(token))
                {
                    count++;
                    if (count > (page ? maximum + 2 : 1)) throw Invalid();
                    var document = rows.GetString(0);
                    var body = rows.IsDBNull(1) ? null : rows.GetFieldValue<byte[]>(1);
                    if (document.Length > 1048576 || body?.Length > 1048576) throw Invalid();
                    if (page && count == 1)
                    {
                        using var marker = JsonDocument.Parse(document);
                        if (body is not null || marker.RootElement.ValueKind != JsonValueKind.Object ||
                            marker.RootElement.EnumerateObject().Count() != 1 || marker.RootElement.GetProperty("page").ValueKind != JsonValueKind.True) throw Invalid();
                    }
                    else
                    {
                        var next = checked(bytes + Encoding.UTF8.GetByteCount(document) + (body?.Length ?? 0));
                        if (page && (values.Count >= maximum || next > 4 * 1024 * 1024))
                        {
                            if (values.Count == 0) throw Invalid();
                            more = true;
                            break;
                        }
                        if (next > 4 * 1024 * 1024) throw Invalid();
                        bytes = next;
                        values.Add(new(document, body));
                    }
                }
            }
            if (count == 0) { await tx.CommitAsync(token); return null; }
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM braid.read_build_metadata(@op,@org,@project,@mapping,@key,@version,@maximum,@after,@afterVersion))";
            var fresh = await command.ExecuteScalarAsync(token) is true;
            await tx.CommitAsync(token);
            return fresh ? new(values.AsReadOnly(), more) : null;
        }
        catch
        {
            if (transaction is not null) await transaction.DisposeAsync();
            await connection.DisposeAsync();
            throw;
        }
    }

    private static RetainedBuildEnvironment Environment(Row row, ProjectScope project)
    {
        if (row.Intent is not null) throw Invalid();
        using var doc = JsonDocument.Parse(row.Document);
        var root = doc.RootElement;
        if (Id(root, "organization_id") != project.OrganizationId || Id(root, "project_id") != project.ProjectId) throw Invalid();
        var reference = new BuildEnvironmentReference(project, Id(root, "environment_id"), root.GetProperty("version").GetInt64());
        var profile = JsonSerializer.Deserialize<BuildRuntimeProfile>(root.GetProperty("profile").GetRawText(), Json) ?? throw Invalid();
        return new(new(reference, profile, root.GetProperty("active").GetBoolean()), new(Id(root, "created_by")), Utc(root, "created_at"));
    }
    private static RetainedBuildApproval Approval(Row row, ActorContext actor, MappingScope mapping)
    {
        using var doc = JsonDocument.Parse(row.Document);
        var root = doc.RootElement;
        var intent = Intent(row, root, actor, mapping);
        if (Id(root, "mapping_id") != mapping.MappingId || Id(root, "revision_id") != intent.Revision.RevisionId ||
            Id(root, "environment_id") != intent.Environment.EnvironmentId || root.GetProperty("environment_version").GetInt64() != intent.Environment.Version ||
            Id(root, "evidence_id") != intent.RegressionEvidenceId || root.GetProperty("native_digest").GetString() != intent.NativeDigest.Value) throw Invalid();
        return new(actor.PrincipalId, intent, root.GetProperty("revoked").GetBoolean(), root.GetProperty("environment_active").GetBoolean(), Utc(root, "created_at"));
    }
    private static BuildRunSummary Run(Row row, ActorContext actor, MappingScope mapping)
    {
        using var doc = JsonDocument.Parse(row.Document);
        var root = doc.RootElement;
        var intent = Intent(row, root, actor, mapping);
        var state = root.GetProperty("state").GetString() switch
        {
            "queued" => BuildRunState.Queued,
            "running" => BuildRunState.Running,
            "succeeded" => BuildRunState.Succeeded,
            "failed" => BuildRunState.Failed,
            "canceled" => BuildRunState.Canceled,
            "exhausted" => BuildRunState.Exhausted,
            "revoked" => BuildRunState.Revoked,
            "uncertain" => BuildRunState.Uncertain,
            _ => throw Invalid()
        };
        var attempt = root.GetProperty("attempt").GetInt32();
        var outcome = root.GetProperty("outcome").ValueKind == JsonValueKind.Null ? (BuildExecutionOutcome?)null : (BuildExecutionOutcome)root.GetProperty("outcome").GetInt32();
        var digest = root.GetProperty("artifact_digest"); var length = root.GetProperty("artifact_bytes");
        BuildArtifactReference? artifact = digest.ValueKind == JsonValueKind.Null && length.ValueKind == JsonValueKind.Null ? null :
            BuildArtifactReference.Restore(intent, Sha256Digest.Parse(digest.GetString()!), length.GetInt64());
        if (attempt is < 0 or > BuildRunPolicy.MaximumAttempts ||
            (state == BuildRunState.Succeeded ? outcome is not (BuildExecutionOutcome.Created or BuildExecutionOutcome.Replayed) :
                state == BuildRunState.Failed ? outcome is not (BuildExecutionOutcome.Refused or BuildExecutionOutcome.Unsupported or BuildExecutionOutcome.Conflict or BuildExecutionOutcome.IntegrityFailure) : outcome is not null) ||
            (state is BuildRunState.Succeeded or BuildRunState.Uncertain) != (artifact is not null)) throw Invalid();
        return new(actor.PrincipalId, intent, state, attempt, Utc(root, "created_at"), outcome, artifact);
    }
    private static BuildIntent Intent(Row row, JsonElement root, ActorContext actor, MappingScope mapping)
    {
        if (row.Intent is null) throw Invalid();
        var value = BuildIntentCodec.Decode(row.Intent);
        if (value.Revision.OrganizationId != mapping.OrganizationId || value.Revision.ProjectId != mapping.ProjectId || value.Revision.MappingId != mapping.MappingId ||
            Id(root, "organization_id") != mapping.OrganizationId || Id(root, "project_id") != mapping.ProjectId || Id(root, "actor_id") != actor.PrincipalId.Value ||
            Id(root, "build_id") != value.BuildId || Id(root, "approval_id") != value.ApprovalId || root.GetProperty("intent_digest").GetString() != value.Fingerprint.Value) throw Invalid();
        return value;
    }
    private static Guid Id(JsonElement root, string name) => root.GetProperty(name).GetGuid() is var value && value != Guid.Empty ? value : throw Invalid();
    private static DateTimeOffset Utc(JsonElement root, string name) => root.GetProperty(name).GetDateTimeOffset() is var value && value.Offset == TimeSpan.Zero ? value : throw Invalid();
    private static ProjectScope Project(MappingScope mapping) => new(mapping.OrganizationId, mapping.ProjectId);
    private static void Bound(int value) { if (value is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(value)); }
    private static void Cursor(Guid? value) { if (value == Guid.Empty) throw Invalid(); }
    private static InvalidOperationException Invalid() => new("Build metadata contract refused.");
    public ValueTask DisposeAsync() => source.DisposeAsync();
}
