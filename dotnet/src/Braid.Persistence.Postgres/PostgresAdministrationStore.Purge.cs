using System.Text.Json;
using NpgsqlTypes;
using Braid.Application;
using Braid.Application.Lifecycle;

namespace Braid.Persistence.Postgres;

public sealed partial class PostgresAdministrationStore : IResourcePurgeStore
{
    public async Task<ResourcePurgePreview?> PreviewAsync(ActorContext actor, ResourceKey resource, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(actor); ArgumentNullException.ThrowIfNull(resource);
        await using var tx = await BeginAsync(resource.Project.OrganizationId, token, readOnly: true);
        await using var command = tx.CreateCommand("SELECT braid.preview_resource_purge(@org,@actor,@project,@mapping)::text");
        Scope(command, resource.Project.OrganizationId, actor.PrincipalId.Value, resource.Project.ProjectId);
        command.Parameters.AddWithValue("mapping", NpgsqlDbType.Uuid, (object?)resource.MappingId ?? DBNull.Value);
        ResourcePurgePreview? result = null;
        if (await command.ExecuteScalarAsync(token) is string text)
        {
            using var json = ParseLifecycle(text);
            var root = json.RootElement;
            if (LifecycleText(root, "resourceKind") != (resource.IsMapping ? "mapping" : "project") || LifecycleId(root, "projectId") != resource.Project.ProjectId ||
                OptionalLifecycleId(root, "mappingId") != resource.MappingId || LifecycleText(root, "planVersion") != "1") throw InvalidPurge();
            var state = LifecycleText(root, "effectiveState");
            if (state is not ("active" or "archived" or "deleting")) throw InvalidPurge();
            var deletion = root.GetProperty("deletion");
            var impact = root.GetProperty("impact");
            result = new(resource, state != "active", LifecycleVersion(root, resource), 1,
                deletion.ValueKind == JsonValueKind.Null ? null : Job(deletion, resource.Project.OrganizationId),
                PurgeCount(root, "activeReleases"), PurgeCount(root, "pendingMappingDeletions"),
                new(PurgeCount(impact, "mappings"), PurgeCount(impact, "revisions"), PurgeCount(impact, "conversations"), PurgeCount(impact, "previews"),
                    PurgeCount(impact, "regressionCases"), PurgeCount(impact, "regressionRuns"), PurgeCount(impact, "builds"),
                    PurgeCount(impact, "releaseTargets"), PurgeCount(impact, "retainedArtifacts")));
            if ((state == "deleting") != (result.Deletion is not null) || result.Deletion is { } job && job.Resource.Project != resource.Project) throw InvalidPurge();
        }
        await tx.CommitAsync(token);
        return result;
    }

    public async Task<ResourcePurgeResult> RequestAsync(ActorContext actor, ResourcePurgeRequest request, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(actor); ArgumentNullException.ThrowIfNull(request);
        var resource = request.Resource;
        await using var tx = await BeginAsync(resource.Project.OrganizationId, token, exclusiveLifecycle: true);
        await using var command = tx.CreateCommand("SELECT braid.request_resource_purge(@org,@actor,@operation,@confirmation,@project,@mapping,@projectVersion,@mappingVersion)::text");
        Scope(command, resource.Project.OrganizationId, actor.PrincipalId.Value, resource.Project.ProjectId);
        command.Parameters.AddWithValue("operation", request.OperationId);
        command.Parameters.AddWithValue("confirmation", request.ConfirmationId);
        command.Parameters.AddWithValue("mapping", NpgsqlDbType.Uuid, (object?)resource.MappingId ?? DBNull.Value);
        command.Parameters.AddWithValue("projectVersion", request.Expected.Project);
        command.Parameters.AddWithValue("mappingVersion", NpgsqlDbType.Bigint, (object?)request.Expected.Mapping ?? DBNull.Value);
        if (await command.ExecuteScalarAsync(token) is not string text) throw InvalidPurge();
        using var json = ParseLifecycle(text);
        var root = json.RootElement;
        var outcome = LifecycleText(root, "outcome") switch
        {
            "accepted" => ResourcePurgeOutcome.Accepted,
            "replayed" => ResourcePurgeOutcome.Replayed,
            "refused" => ResourcePurgeOutcome.NotAuthorizedOrNotFound,
            "preconditionFailed" => ResourcePurgeOutcome.PreconditionFailed,
            "activeRelease" => ResourcePurgeOutcome.ActiveRelease,
            "activeWork" => ResourcePurgeOutcome.ActiveWork,
            "conflict" => ResourcePurgeOutcome.Conflict,
            "invalidRequest" => ResourcePurgeOutcome.InvalidRequest,
            _ => throw InvalidPurge()
        };
        if (outcome == ResourcePurgeOutcome.ActiveRelease) return new(outcome, ActiveReleases: PurgeCount(root, "activeReleases"));
        if (outcome is not (ResourcePurgeOutcome.Accepted or ResourcePurgeOutcome.Replayed)) return new(outcome); // A refusal commits nothing.
        var job = Job(root.GetProperty("job"), resource.Project.OrganizationId);
        if (LifecycleId(root, "operationId") != request.OperationId || job.Resource != resource) throw InvalidPurge();
        await tx.CommitAsync(token);
        return new(outcome, job);
    }

    public async Task<DeletionJobStatus?> ReadJobAsync(ActorContext actor, Guid organizationId, Guid jobId, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (jobId == Guid.Empty) throw new ArgumentException("Invalid deletion job.", nameof(jobId));
        await using var tx = await BeginAsync(organizationId, token, readOnly: true);
        await using var command = tx.CreateCommand("SELECT braid.read_deletion_job(@org,@actor,@job)::text");
        command.Parameters.AddWithValue("org", organizationId);
        command.Parameters.AddWithValue("actor", actor.PrincipalId.Value);
        command.Parameters.AddWithValue("job", jobId);
        DeletionJobStatus? result = null;
        if (await command.ExecuteScalarAsync(token) is string text)
        {
            using var json = ParseLifecycle(text);
            result = Job(json.RootElement, organizationId);
            if (result.JobId != jobId) throw InvalidPurge();
        }
        await tx.CommitAsync(token);
        return result;
    }

    async Task<DeletionJobStatus?> IResourcePurgeStore.ReadOperationAsync(ActorContext actor, Guid organizationId, Guid operationId, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (operationId == Guid.Empty) throw new ArgumentException("Invalid deletion operation.", nameof(operationId));
        await using var tx = await BeginAsync(organizationId, token, readOnly: true);
        await using var command = tx.CreateCommand("SELECT braid.read_resource_purge_operation(@org,@actor,@operation)::text");
        command.Parameters.AddWithValue("org", organizationId);
        command.Parameters.AddWithValue("actor", actor.PrincipalId.Value);
        command.Parameters.AddWithValue("operation", operationId);
        DeletionJobStatus? result = null;
        if (await command.ExecuteScalarAsync(token) is string text)
        {
            using var json = ParseLifecycle(text);
            if (LifecycleText(json.RootElement, "outcome") != "recorded" || LifecycleId(json.RootElement, "operationId") != operationId) throw InvalidPurge();
            result = Job(json.RootElement.GetProperty("job"), organizationId);
        }
        await tx.CommitAsync(token);
        return result;
    }

    /// <summary>The covering job of a resource for its readers, or null.</summary>
    public async Task<(DeletionResumeOutcome Outcome, DeletionJobStatus? Job)> ResumeAsync(ActorContext actor, Guid organizationId, Guid jobId,
        Guid operationId, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (jobId == Guid.Empty || operationId == Guid.Empty) throw new ArgumentException("Invalid deletion resumption.");
        await using var tx = await BeginAsync(organizationId, token, exclusiveLifecycle: true);
        await using var command = tx.CreateCommand("SELECT braid.resume_deletion_job(@org,@actor,@operation,@job)::text");
        command.Parameters.AddWithValue("org", organizationId);
        command.Parameters.AddWithValue("actor", actor.PrincipalId.Value);
        command.Parameters.AddWithValue("operation", operationId);
        command.Parameters.AddWithValue("job", jobId);
        if (await command.ExecuteScalarAsync(token) is not string text) throw InvalidPurge();
        using var json = ParseLifecycle(text);
        var outcome = LifecycleText(json.RootElement, "outcome") switch
        {
            "resumed" => DeletionResumeOutcome.Resumed,
            "replayed" => DeletionResumeOutcome.Replayed,
            "refused" => DeletionResumeOutcome.NotAuthorizedOrNotFound,
            "stillBlocked" => DeletionResumeOutcome.StillBlocked,
            "conflict" => DeletionResumeOutcome.Conflict,
            "invalidRequest" => DeletionResumeOutcome.InvalidRequest,
            _ => throw InvalidPurge()
        };
        if (outcome is not (DeletionResumeOutcome.Resumed or DeletionResumeOutcome.Replayed)) return (outcome, null); // A refusal commits nothing.
        var job = Job(json.RootElement.GetProperty("job"), organizationId);
        if (job.JobId != jobId) throw InvalidPurge();
        await tx.CommitAsync(token);
        return (outcome, job);
    }

    private static async Task<(Guid JobId, string State)?> ReadPurgeAsync(PostgresTransaction tx, ActorContext actor, ResourceKey resource, CancellationToken token)
    {
        await using var command = tx.CreateCommand("SELECT braid.read_resource_purge(@org,@actor,@project,@mapping)::text");
        Scope(command, resource.Project.OrganizationId, actor.PrincipalId.Value, resource.Project.ProjectId);
        command.Parameters.AddWithValue("mapping", NpgsqlDbType.Uuid, (object?)resource.MappingId ?? DBNull.Value);
        if (await command.ExecuteScalarAsync(token) is not string text) return null;
        using var json = ParseLifecycle(text);
        return (LifecycleId(json.RootElement, "jobId"), LifecycleText(json.RootElement, "state"));
    }

    private static DeletionJobStatus Job(JsonElement row, Guid organizationId)
    {
        var resource = new ResourceKey(new(organizationId, LifecycleId(row, "projectId")), OptionalLifecycleId(row, "mappingId"));
        if (LifecycleText(row, "resourceKind") != (resource.IsMapping ? "mapping" : "project")) throw InvalidPurge();
        var state = LifecycleText(row, "state") switch
        {
            "quiescing" => DeletionJobState.Quiescing,
            "inventory" => DeletionJobState.Inventory,
            "catalogPurging" => DeletionJobState.CatalogPurging,
            "objectsPending" => DeletionJobState.ObjectsPending,
            "completed" => DeletionJobState.Completed,
            "blocked" => DeletionJobState.Blocked,
            _ => throw InvalidPurge()
        };
        DeletionJobBlocker? blocker = row.GetProperty("blocker").ValueKind == JsonValueKind.Null ? null : LifecycleText(row, "blocker") switch
        {
            "activeRelease" => DeletionJobBlocker.ActiveRelease,
            "integrity" => DeletionJobBlocker.Integrity,
            "storage" => DeletionJobBlocker.Storage,
            "attemptsExhausted" => DeletionJobBlocker.AttemptsExhausted,
            _ => throw InvalidPurge()
        };
        if ((state == DeletionJobState.Blocked) != (blocker is not null)) throw InvalidPurge();
        return new(LifecycleId(row, "jobId"), resource, state, blocker, row.GetProperty("catalogRemoved").GetBoolean(),
            PurgeCount(row, "objectCount"), PurgeCount(row, "objectsPending"), PurgeCount(row, "objectsBlocked"),
            PurgeCount(row, "retainedArtifacts"), PurgeCount(row, "retainedAuditRecords"),
            row.GetProperty("requestedAtUtc").GetDateTimeOffset(), row.GetProperty("changedAtUtc").GetDateTimeOffset(),
            PurgeTime(row, "catalogRemovedAtUtc"), PurgeTime(row, "completedAtUtc"));
    }

    private static DateTimeOffset? PurgeTime(JsonElement row, string name) =>
        row.GetProperty(name).ValueKind == JsonValueKind.Null ? null : row.GetProperty(name).GetDateTimeOffset();
    private static long PurgeCount(JsonElement row, string name) => LifecycleCount(LifecycleText(row, name));
    private static InvalidOperationException InvalidPurge() => new("Resource deletion job metadata is unavailable.");
}
