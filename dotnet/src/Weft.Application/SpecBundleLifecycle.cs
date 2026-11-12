using Weft.Domain;

namespace Weft.Application;

public sealed record SpecBundleStatus(SpecBundleScope Scope, long Version, bool Tombstoned);
public sealed record SpecObjectReservation(SpecPublicationClaim Claim, long BundleVersion);

/// <summary>Actor-bound durable authority. Metadata visibility alone never admits document content or AI sharing.</summary>
public interface ISpecBundleLifecycle
{
    Task<SpecBundleStatus> CreateAsync(SpecBundleScope scope, Guid creationKey, CancellationToken token);
    Task<SpecBundleStatus> AuthorizeAsync(SpecBundleScope scope, bool write, CancellationToken token);
    Task<SpecObjectReservation> ReserveAsync(SpecObjectReference reference, SpecSourceFormat format, Guid attemptId,
        long expectedBundleVersion, SpecExtractionIdentity? extraction, CancellationToken token);
    Task<SpecObjectReference> FindAsync(SpecBundleScope scope, Guid objectId, CancellationToken token);
    Task<SpecBundleStatus> TombstoneAsync(SpecBundleScope scope, SpecSourceIdentity? source, long expectedVersion, Guid key, CancellationToken token);
    Task<IReadOnlyList<SpecRetirementClaim>> RetirementsAsync(SpecBundleScope scope, CancellationToken token);
}

/// <summary>
/// Upload/read/delete orchestration over an actor-bound lifecycle and its physical store. No HTTP, AI, or native
/// filesystem capability is enabled here. Registry callbacks recheck authority and tombstones at physical boundaries.
/// </summary>
public sealed class SpecBundleService(ISpecBundleLifecycle lifecycle, ISpecObjectStore objects)
{
    private readonly ISpecBundleLifecycle lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
    private readonly ISpecObjectStore objects = objects ?? throw new ArgumentNullException(nameof(objects));

    public Task<SpecBundleStatus> CreateAsync(SpecBundleScope scope, Guid key, CancellationToken token = default) =>
        lifecycle.CreateAsync(scope, key, token);

    public async Task<SpecObjectReservation> UploadAsync(SpecSourceIdentity source, SpecSourceFormat format, Guid objectId,
        Guid attemptId, long expectedBundleVersion, Stream input, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(input);
        if (objectId == Guid.Empty || attemptId == Guid.Empty || expectedBundleVersion < 1 || !Enum.IsDefined(format))
            throw new ArgumentException("Valid upload identities and version are required.");
        await lifecycle.AuthorizeAsync(source.Bundle, true, token).ConfigureAwait(false);
        var captured = await SpecSourceCapture.ReadAsync(source, format, input, cancellationToken: token).ConfigureAwait(false);
        if (captured.Source is not { } content) throw new SpecStorageException(SpecStorageError.Limit);
        var reference = new SpecObjectReference(new(source, SpecObjectKind.Original, objectId), content.Digest, content.ByteCount);
        var reservation = await lifecycle.ReserveAsync(reference, format, attemptId, expectedBundleVersion, null, token).ConfigureAwait(false);
        using var bytes = new MemoryStream(content.CopyBytes(), false);
        await objects.PublishAsync(source.Bundle, reservation.Claim, bytes, token).ConfigureAwait(false);
        return reservation;
    }

    public async Task<SpecObjectReservation> PublishExtractionAsync(SpecExtractionIdentity extraction, Guid objectId, Guid attemptId,
        long expectedBundleVersion, Stream evidence, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(extraction);
        ArgumentNullException.ThrowIfNull(evidence);
        if (objectId == Guid.Empty || attemptId == Guid.Empty || expectedBundleVersion < 1) throw new ArgumentException("Valid publication identities are required.");
        await lifecycle.AuthorizeAsync(extraction.Source.Bundle, true, token).ConfigureAwait(false);
        var captured = await SpecSourceCapture.ReadAsync(extraction.Source, extraction.Format, evidence, cancellationToken: token).ConfigureAwait(false);
        if (captured.Source is not { } content) throw new SpecStorageException(SpecStorageError.Limit);
        if (content.Digest != extraction.EvidenceDigest) throw new SpecStorageException(SpecStorageError.Integrity);
        var reference = new SpecObjectReference(new(extraction.Source, SpecObjectKind.Extraction, objectId), content.Digest, content.ByteCount);
        var reservation = await lifecycle.ReserveAsync(reference, extraction.Format, attemptId, expectedBundleVersion, extraction, token).ConfigureAwait(false);
        using var bytes = new MemoryStream(content.CopyBytes(), false);
        await objects.PublishAsync(extraction.Source.Bundle, reservation.Claim, bytes, token).ConfigureAwait(false);
        return reservation;
    }

    public async Task<SpecObjectReservation> PublishExtractionAsync(SpecExtractionResult extraction, Guid objectId, Guid attemptId,
        long expectedBundleVersion, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(extraction);
        using var evidence = new MemoryStream(extraction.CopyEvidenceBytes(), false);
        return await PublishExtractionAsync(extraction.Identity, objectId, attemptId, expectedBundleVersion, evidence, token).ConfigureAwait(false);
    }

    public async Task<byte[]> ReadAsync(SpecBundleScope scope, Guid objectId, CancellationToken token = default)
    {
        var reference = await lifecycle.FindAsync(scope, objectId, token).ConfigureAwait(false);
        return await objects.ReadAsync(scope, reference, token).ConfigureAwait(false);
    }

    /// <summary>Only logical tombstoning. Physical cleanup is separately recoverable and never runs before this commits.</summary>
    public Task<SpecBundleStatus> TombstoneAsync(SpecBundleScope scope, SpecSourceIdentity? source, long version, Guid key,
        CancellationToken token = default) => lifecycle.TombstoneAsync(scope, source, version, key, token);

    public async Task<int> RetirePendingAsync(SpecBundleScope scope, CancellationToken token = default)
    {
        var claims = await lifecycle.RetirementsAsync(scope, token).ConfigureAwait(false);
        if (claims.Count > 100) throw new SpecStorageException(SpecStorageError.Integrity);
        var completed = 0;
        foreach (var claim in claims)
        {
            await objects.RetireAsync(scope, claim, token).ConfigureAwait(false);
            completed++;
        }
        return completed;
    }
}
