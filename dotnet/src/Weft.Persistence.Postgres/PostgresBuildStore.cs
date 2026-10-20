using System.Data;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Weft.Application;
using Weft.Deployment;
using Weft.Domain;

namespace Weft.Persistence.Postgres;

/// <summary>Production build controls and durable first-writer artifact staging through narrow owner functions.</summary>
public sealed class PostgresBuildStore(string connectionString, bool writer) : IBuildControlStore, IBuildPublicationCatalog, IAsyncDisposable
{
    public const int MaximumArtifactBytes = 32 * 1024 * 1024;
    private readonly NpgsqlDataSource source = NpgsqlDataSource.Create(PostgresConfiguration.Normalize(connectionString));
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<BuildControlOutcome> SaveEnvironmentAsync(ActorContext actor, BuildEnvironment environment, CancellationToken token = default)
    {
        if (!environment.Active || environment.Profile.MaximumArtifactBytes > MaximumArtifactBytes) throw Invalid();
        return (BuildControlOutcome)await WriteAsync(actor, "environment", environment.Reference.Project.OrganizationId,
            environment.Reference.Project.ProjectId, environment.Reference.EnvironmentId, environment.Reference.Version,
            JsonSerializer.Serialize(environment.Profile, Json), null, null, token);
    }

    public async Task<BuildEnvironment?> ReadEnvironmentAsync(ActorContext actor, BuildEnvironmentReference environment, CancellationToken token = default)
    {
        var row = await ReadAsync(actor, "environment", environment.Project.OrganizationId, environment.Project.ProjectId,
            environment.EnvironmentId, environment.Version, token);
        if (row is null) return null;
        using var doc = JsonDocument.Parse(row.Document);
        var root = doc.RootElement;
        var reference = new BuildEnvironmentReference(new(Id(root, "organization_id"), Id(root, "project_id")), Id(root, "environment_id"), root.GetProperty("version").GetInt64());
        var profile = JsonSerializer.Deserialize<BuildRuntimeProfile>(root.GetProperty("profile").GetRawText(), Json) ?? throw Invalid();
        if (reference != environment || !root.GetProperty("active").GetBoolean()) throw Invalid();
        return new(reference, profile, true);
    }

    public async Task<BuildControlOutcome> ApproveAsync(ActorContext actor, BuildApproval approval, CancellationToken token = default)
    {
        if (approval.Actor != actor.PrincipalId || approval.Native.Id.Scope != approval.Intent.Revision ||
            approval.Native.Id.Class != PrivateObjectClass.NativeArtifact || approval.Native.Digest != approval.Intent.NativeDigest) throw Invalid();
        return (BuildControlOutcome)await WriteAsync(actor, "approve", approval.Intent.Revision.OrganizationId,
            approval.Intent.Revision.ProjectId, approval.Intent.ApprovalId, 0, JsonSerializer.Serialize(new { evidenceDigest = approval.EvidenceDigest.Value }),
            approval.Intent.CopyCanonicalBytes(), null, token);
    }

    public async Task<bool> RevokeAsync(ActorContext actor, Guid organizationId, Guid projectId, Guid approvalId, CancellationToken token = default) =>
        await WriteAsync(actor, "revoke", organizationId, projectId, approvalId, 0, "{}", null, null, token) == 0;

    public async Task<BuildApproval?> ReadApprovalAsync(ActorContext actor, BuildIntent intent, CancellationToken token = default)
    {
        var row = await ReadAsync(actor, "approval", intent.Revision.OrganizationId, intent.Revision.ProjectId, intent.ApprovalId, 0, token);
        if (row is null) return null;
        using var doc = JsonDocument.Parse(row.Document);
        var result = Approval(doc.RootElement, row.Intent!);
        return result.Actor == actor.PrincipalId && result.Intent.Fingerprint == intent.Fingerprint ? result : null;
    }

    public async Task<BuildPublication?> ReadAsync(ActorContext actor, Guid organizationId, Guid buildId, CancellationToken token = default) =>
        (await ReadStageAsync(actor, "published", organizationId, buildId, token))?.Publication;

    public Task<StagedBuild?> ReadStagedAsync(ActorContext actor, Guid organizationId, Guid buildId, CancellationToken token = default) =>
        ReadStageAsync(actor, "staged", organizationId, buildId, token);

    internal Task<StagedBuild?> ReadObjectAsync(ActorContext actor, Guid organizationId, Guid buildId, CancellationToken token = default) =>
        ReadStageAsync(actor, "object", organizationId, buildId, token);

    public async Task<StagedBuild?> StageAsync(ActorContext actor, StagedBuild build, CancellationToken token = default)
    {
        Check(actor, build.Publication);
        if (build.Bytes.Digest != build.Publication.Artifact.ContentDigest || build.Bytes.Length != build.Publication.Artifact.ByteCount) throw Invalid();
        var intent = build.Publication.Artifact.Intent;
        var outcome = await WriteAsync(actor, "stage", intent.Revision.OrganizationId, intent.Revision.ProjectId, intent.BuildId, 0,
            JsonSerializer.Serialize(new { artifactDigest = build.Bytes.Digest.Value }), intent.CopyCanonicalBytes(), build.Bytes.Copy(), token);
        return outcome is 0 or 1 ? await ReadStagedAsync(actor, intent.Revision.OrganizationId, intent.BuildId, token) : null;
    }

    public async Task<BuildPublicationOutcome> PublishAsync(ActorContext actor, BuildPublication publication, CancellationToken token = default)
    {
        Check(actor, publication);
        var intent = publication.Artifact.Intent;
        return (BuildPublicationOutcome)await WriteAsync(actor, "publish", intent.Revision.OrganizationId, intent.Revision.ProjectId, intent.BuildId, 0,
            JsonSerializer.Serialize(new { artifactDigest = publication.Artifact.ContentDigest.Value }), intent.CopyCanonicalBytes(), null, token);
    }

    internal async Task<PrivateObjectPublishResult> PublishObjectAsync(ActorContext actor, RevisionScope scope, PrivateObjectReference reference,
        byte[] bytes, CancellationToken token)
    {
        var staged = await ReadStagedAsync(actor, scope.OrganizationId, reference.Id.ObjectId, token);
        if (staged is null) throw new PrivateObjectStorageException(PrivateObjectStorageError.Missing);
        if (staged.Publication.Object != reference || staged.Publication.Artifact.Intent.Revision != scope ||
            Sha256Digest.FromBytes(bytes) != reference.Digest || bytes.LongLength != reference.ByteCount) throw Invalid();
        var result = await WriteAsync(actor, "object", scope.OrganizationId, scope.ProjectId, reference.Id.ObjectId, 0,
            JsonSerializer.Serialize(new { artifactDigest = reference.Digest.Value }), staged.Publication.Artifact.Intent.CopyCanonicalBytes(), bytes, token);
        return result switch
        {
            0 => PrivateObjectPublishResult.Created,
            1 => PrivateObjectPublishResult.AlreadyPresent,
            2 => throw new PrivateObjectStorageException(PrivateObjectStorageError.Conflict),
            _ => throw new PrivateObjectStorageException(PrivateObjectStorageError.Missing)
        };
    }

    private async Task<StagedBuild?> ReadStageAsync(ActorContext actor, string operation, Guid organizationId, Guid buildId, CancellationToken token)
    {
        var row = await ReadAsync(actor, operation, organizationId, Guid.Empty, buildId, 0, token);
        if (row is null) return null;
        using var doc = JsonDocument.Parse(row.Document);
        var approval = Approval(doc.RootElement.GetProperty("approval"), row.Intent!);
        var digest = Digest(doc.RootElement.GetProperty("artifactDigest").GetString()!);
        if (row.Artifact is null || row.Artifact.Length is < 1 or > MaximumArtifactBytes || Sha256Digest.FromBytes(row.Artifact) != digest ||
            approval.Actor != actor.PrincipalId || approval.Intent.Revision.OrganizationId != organizationId || approval.Intent.BuildId != buildId) throw Invalid();
        var artifact = BuildArtifactReference.Restore(approval.Intent, digest, row.Artifact.Length);
        return new(new(approval.Actor, artifact, new(PrivateObjectId.Restore(approval.Intent.Revision, buildId, PrivateObjectClass.DerivedArtifact), digest, row.Artifact.Length)),
            new(row.Artifact, approval.Intent.Profile.MaximumArtifactBytes));
    }

    private static BuildApproval Approval(JsonElement value, byte[] bytes)
    {
        var intent = BuildIntentCodec.Decode(bytes);
        if (Id(value, "organization_id") != intent.Revision.OrganizationId || Id(value, "project_id") != intent.Revision.ProjectId ||
            Id(value, "build_id") != intent.BuildId || Id(value, "approval_id") != intent.ApprovalId || Id(value, "mapping_id") != intent.Revision.MappingId ||
            Id(value, "revision_id") != intent.Revision.RevisionId || Id(value, "environment_id") != intent.Environment.EnvironmentId ||
            value.GetProperty("environment_version").GetInt64() != intent.Environment.Version || Digest(value.GetProperty("intent_digest").GetString()!) != intent.Fingerprint ||
            Id(value, "evidence_id") != intent.RegressionEvidenceId || value.GetProperty("revoked").GetBoolean()) throw Invalid();
        var native = new PrivateObjectReference(PrivateObjectId.Restore(intent.Revision, Id(value, "native_id"), PrivateObjectClass.NativeArtifact),
            Digest(value.GetProperty("native_digest").GetString()!), value.GetProperty("native_bytes").GetInt64());
        if (native.Digest != intent.NativeDigest) throw Invalid();
        return new(new(Id(value, "actor_id")), intent, native, Digest(value.GetProperty("evidence_digest").GetString()!));
    }

    private static void Check(ActorContext actor, BuildPublication publication)
    {
        var intent = publication.Artifact.Intent;
        if (publication.Actor != actor.PrincipalId || publication.Object.Id.Scope != intent.Revision || publication.Object.Id.ObjectId != intent.BuildId ||
            publication.Object.Id.Class != PrivateObjectClass.DerivedArtifact || publication.Object.Digest != publication.Artifact.ContentDigest ||
            publication.Object.ByteCount != publication.Artifact.ByteCount || publication.Object.ByteCount > MaximumArtifactBytes) throw Invalid();
    }

    private sealed record Row(string Document, byte[]? Intent, byte[]? Artifact);
    private async Task<Row?> ReadAsync(ActorContext actor, string operation, Guid org, Guid project, Guid key, long version, CancellationToken token)
    {
        await using var tx = await BeginAsync(actor, org, false, token);
        await using var command = tx.CreateCommand("SELECT document::text,intent,artifact FROM weft.read_build_record(@op,@org,@project,@key,@version)");
        Arguments(command, operation, org, project, key, version);
        Row? result = null;
        await using (var rows = await command.ExecuteReaderAsync(token))
        {
            if (await rows.ReadAsync(token))
            {
                var document = rows.GetString(0);
                var intent = rows.IsDBNull(1) ? null : rows.GetFieldValue<byte[]>(1);
                var artifact = rows.IsDBNull(2) ? null : rows.GetFieldValue<byte[]>(2);
                if (document.Length > 1048576 || intent?.Length > 1048576 || artifact?.Length > MaximumArtifactBytes || await rows.ReadAsync(token)) throw Invalid();
                result = new(document, intent, artifact);
            }
        }
        if (result is not null)
        {
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM weft.read_build_record(@op,@org,@project,@key,@version))";
            if (await command.ExecuteScalarAsync(token) is not true) result = null;
        }
        await tx.CommitAsync(token);
        return result;
    }

    private async Task<int> WriteAsync(ActorContext actor, string operation, Guid org, Guid project, Guid key, long version,
        string info, byte[]? intent, byte[]? artifact, CancellationToken token)
    {
        if (!writer) throw Invalid();
        await using var tx = await BeginAsync(actor, org, true, token);
        await using var command = tx.CreateCommand("SELECT weft.write_build_record(@op,@org,@project,@key,@version,@info,@intent,@artifact)");
        Arguments(command, operation, org, project, key, version);
        command.Parameters.AddWithValue("info", NpgsqlDbType.Jsonb, info);
        command.Parameters.AddWithValue("intent", NpgsqlDbType.Bytea, (object?)intent ?? DBNull.Value);
        command.Parameters.AddWithValue("artifact", NpgsqlDbType.Bytea, (object?)artifact ?? DBNull.Value);
        var value = await command.ExecuteScalarAsync(token);
        if (value is not int result || result is < 0 or > 3) throw Invalid();
        await tx.CommitAsync(token);
        return result;
    }

    private async Task<PostgresTransaction> BeginAsync(ActorContext actor, Guid org, bool write, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (org == Guid.Empty) throw Invalid();
        var connection = await source.OpenConnectionAsync(token);
        NpgsqlTransaction? transaction = null;
        try
        {
            transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
            await using (var settings = new NpgsqlCommand("SET LOCAL search_path=pg_catalog; SET LOCAL TimeZone='UTC'; SET LOCAL DateStyle='ISO, MDY'; SET LOCAL statement_timeout='15s'; SET LOCAL lock_timeout='5s'", connection, transaction))
                await settings.ExecuteNonQueryAsync(token);
            if (!write) await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction)) await readOnly.ExecuteNonQueryAsync(token);
            await BuildRoleGuard.ValidateAsync(connection, transaction, writer, token);
            await using (var setting = new NpgsqlCommand("SELECT set_config('weft.build_principal_id',@actor,true)", connection, transaction))
            {
                setting.Parameters.AddWithValue("actor", actor.PrincipalId.Value.ToString("D"));
                await setting.ExecuteNonQueryAsync(token);
            }
            var tx = new PostgresTransaction(connection, transaction, org);
            await tx.BindOrganizationAsync(org, token);
            return tx;
        }
        catch { if (transaction is not null) await transaction.DisposeAsync(); await connection.DisposeAsync(); throw; }
    }

    private static void Arguments(NpgsqlCommand command, string operation, Guid org, Guid project, Guid key, long version)
    {
        if (key == Guid.Empty) throw Invalid();
        command.Parameters.AddWithValue("op", operation);
        command.Parameters.AddWithValue("org", org);
        command.Parameters.AddWithValue("project", project);
        command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("version", version);
    }
    private static Guid Id(JsonElement value, string key) => Guid.ParseExact(value.GetProperty(key).GetString()!, "D");
    private static Sha256Digest Digest(string value) { var digest = Sha256Digest.Parse(value); return value == digest.Value ? digest : throw Invalid(); }
    private static InvalidOperationException Invalid() => new("Build persistence contract refused.");
    public ValueTask DisposeAsync() => source.DisposeAsync();
}
