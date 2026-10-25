namespace Weft.Application;

/// <summary>Separate display-only binding metadata; callers must freshly authenticate before reading.</summary>
public interface IAccountDisplayMetadata
{
    Task<string?> ReadAsync(Guid organizationId, Guid bindingId, CancellationToken cancellationToken);
    Task ObserveAsync(Guid organizationId, Guid bindingId, string? displayName, DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken);
}
