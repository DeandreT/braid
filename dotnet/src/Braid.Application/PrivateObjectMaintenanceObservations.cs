using Braid.Domain;

namespace Braid.Application;

/// <summary>Read-only, scoped status observations; these confer no cleanup authority.</summary>
public interface IPrivateObjectMaintenanceStatusReader
{
    Task<ObjectCleanupStatusPage> GetStatusesAsync(ProjectScope project,
        ObjectCleanupStatusCursor? after = null, CancellationToken cancellationToken = default);
}

/// <summary>Matches physical observations to flat retained registrations, never to current permission.</summary>
public interface IPrivateObjectPublicationObservationReader
{
    Task<PrivatePublicationObservationBatch> MatchAsync(PublicationObservationRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>A bounded read-only traversal, not an atomic or eventual project inventory.</summary>
public interface IPrivateObjectStorageObserver
{
    Task<PrivateStorageObservationPage> ObserveAsync(ProjectScope project,
        CancellationToken cancellationToken = default);
}

internal static class MaintenanceObservationValue
{
    internal const int MaximumEntries = 100;

    internal static void Ordinal(int ordinal) => MaintenanceValue.Count(ordinal,
        MaximumEntries - 1, nameof(ordinal));

    internal static bool InProject(RevisionScope scope, ProjectScope project) =>
        scope.OrganizationId == project.OrganizationId && scope.ProjectId == project.ProjectId;

    internal static IReadOnlyList<T> Snapshot<T>(IEnumerable<T> values, int maximum,
        CancellationToken cancellationToken) where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(values);
        var owned = new List<T>();
        using var enumerator = values.GetEnumerator();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var more = enumerator.MoveNext();
            cancellationToken.ThrowIfCancellationRequested();
            if (!more) break;
            var current = enumerator.Current;
            if (owned.Count == maximum || current is null)
                throw new ArgumentException("An observation snapshot exceeds its bound or contains a missing value.", nameof(values));
            owned.Add(current);
        }
        return owned.AsReadOnly();
    }

    internal static int CompareIds(Guid left, Guid right)
    {
        Span<byte> leftBytes = stackalloc byte[16];
        Span<byte> rightBytes = stackalloc byte[16];
        left.TryWriteBytes(leftBytes, bigEndian: true, out _);
        right.TryWriteBytes(rightBytes, bigEndian: true, out _);
        return ((ReadOnlySpan<byte>)leftBytes).SequenceCompareTo(rightBytes);
    }
}
