using System.Globalization;
using Braid.Domain;

namespace Braid.Application.Lifecycle;

public enum ResourceLifecycleAction { Archive = 1, Restore = 2 }

/// <summary>Closed outcomes; a database failure is never reported as not found.</summary>
public enum ResourceLifecycleOutcome { Archived = 1, Restored, Replayed, NotAuthorizedOrNotFound, PreconditionFailed, Conflict, InvalidRequest }

/// <summary>A complete project or mapping scope, never an isolated mapping identity.</summary>
public sealed record ResourceKey
{
    public ResourceKey(ProjectScope project, Guid? mappingId = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (mappingId == Guid.Empty) throw new ArgumentException("A mapping lifecycle requires its mapping identity.", nameof(mappingId));
        Project = project;
        MappingId = mappingId;
    }

    public ProjectScope Project { get; }
    public Guid? MappingId { get; }
    public bool IsMapping => MappingId is not null;
}

/// <summary>Effective versions are the observed project version and, for a mapping, its own version.</summary>
public sealed record ResourceLifecycleVersion
{
    public ResourceLifecycleVersion(long project, long? mapping)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(project, 1);
        if (mapping is < 1) throw new ArgumentOutOfRangeException(nameof(mapping));
        Project = project;
        Mapping = mapping;
    }

    public long Project { get; }
    public long? Mapping { get; }

    public static string ETag(ResourceKey resource, ResourceLifecycleVersion version)
    {
        ArgumentNullException.ThrowIfNull(resource); ArgumentNullException.ThrowIfNull(version);
        if (resource.IsMapping != (version.Mapping is not null)) throw new ArgumentException("Lifecycle versions must match the resource kind.");
        var project = version.Project.ToString(CultureInfo.InvariantCulture);
        return resource.MappingId is { } mapping
            ? $"\"lifecycle-{mapping:D}-{project}-{version.Mapping!.Value.ToString(CultureInfo.InvariantCulture)}\""
            : $"\"lifecycle-{resource.Project.ProjectId:D}-{project}\"";
    }

    /// <summary>Only an exact canonical strong tag for this resource is accepted.</summary>
    public static ResourceLifecycleVersion? ParseETag(ResourceKey resource, string? text)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (text is null || text.Length > 128) return null;
        var prefix = $"\"lifecycle-{(resource.MappingId ?? resource.Project.ProjectId):D}-";
        if (!text.StartsWith(prefix, StringComparison.Ordinal) || !text.EndsWith('"')) return null;
        var parts = text[prefix.Length..^1].Split('-');
        if (parts.Length != (resource.IsMapping ? 2 : 1) || parts.Any(part => !long.TryParse(part, NumberStyles.None,
                CultureInfo.InvariantCulture, out var value) || value < 1)) return null;
        var version = new ResourceLifecycleVersion(long.Parse(parts[0], CultureInfo.InvariantCulture),
            resource.IsMapping ? long.Parse(parts[1], CultureInfo.InvariantCulture) : null);
        return ETag(resource, version) == text ? version : null;
    }
}

public sealed record ResourceLifecycleChange
{
    public ResourceLifecycleChange(ResourceKey resource, ResourceLifecycleAction action, Guid operationId, ResourceLifecycleVersion expected)
    {
        ArgumentNullException.ThrowIfNull(resource); ArgumentNullException.ThrowIfNull(expected);
        if (!Enum.IsDefined(action) || operationId == Guid.Empty || resource.IsMapping != (expected.Mapping is not null))
            throw new ArgumentException("Invalid lifecycle change.");
        Resource = resource; Action = action; OperationId = operationId; Expected = expected;
    }

    public ResourceKey Resource { get; }
    public ResourceLifecycleAction Action { get; }
    public Guid OperationId { get; }
    public ResourceLifecycleVersion Expected { get; }
}

public sealed record ResourceLifecycleState(ResourceKey Resource, bool Archived, bool EffectivelyArchived,
    ResourceLifecycleVersion Version, DateTimeOffset? ArchivedAtUtc, bool CanArchive, bool CanRestore, Guid? DeletionJobId = null);

public sealed record ResourceLifecycleReceipt(ResourceLifecycleOutcome Outcome, Guid OperationId, ResourceKey Resource,
    ResourceLifecycleAction Action, ResourceLifecycleVersion Version, DateTimeOffset RecordedAtUtc);

/// <summary>Fresh authority on every call; replaying a receipt never repeats the mutation.</summary>
public interface IResourceLifecycleStore
{
    Task<ResourceLifecycleState?> ReadAsync(ActorContext actor, ResourceKey resource, CancellationToken token);
    Task<(ResourceLifecycleOutcome Outcome, ResourceLifecycleReceipt? Receipt)> ChangeAsync(ActorContext actor, ResourceLifecycleChange change, CancellationToken token);
    Task<ResourceLifecycleReceipt?> ReadOperationAsync(ActorContext actor, Guid organizationId, Guid operationId, CancellationToken token);
}

/// <summary>Categorical refusal by the archive fence; it carries no database diagnostic text.</summary>
public sealed class ResourceArchivedException() : Exception("The project or mapping is archived.");

/// <summary>Recognizes the database fence that refused authoring on an archived resource or epoch.</summary>
public static class ResourceLifecycleFence
{
    public const string SqlState = "PWA01";

    public static bool Refused(Exception? error)
    {
        for (var depth = 0; error is not null && depth < 8; error = error.InnerException, depth++)
            if (error is ResourceArchivedException or System.Data.Common.DbException { SqlState: SqlState }) return true;
        return false;
    }

    /// <summary>Lets persistence adapters that redact database failures keep the fence category.</summary>
    public static void ThrowIfRefused(Exception error)
    {
        if (Refused(error)) throw new ResourceArchivedException();
    }
}
