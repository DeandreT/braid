using Weft.Domain;

namespace Weft.Application;

/// <summary>Restores exact selected retained evidence through the common trusted-store boundary.</summary>
public sealed class CreationSpecificationLoader(ISpecBundleServiceFactory factory)
{
    public async Task<CreationSpecification> LoadAsync(ActorContext actor, SpecBundleScope bundle, IReadOnlyList<Guid> extractionIds,
        CancellationToken ct = default, Func<CancellationToken, Task>? authorizeSharing = null)
    {
        if (extractionIds.Count is < 1 or > 20 || extractionIds.Any(id => id == Guid.Empty) || extractionIds.Distinct().Count() != extractionIds.Count)
            throw new ArgumentException("Select 1–20 distinct retained extractions.");
        Task Sharing() => authorizeSharing?.Invoke(ct) ?? Task.CompletedTask;
        await Sharing().ConfigureAwait(false);
        var service = await factory.OpenAsync(actor, false, ct).ConfigureAwait(false);
        await Sharing().ConfigureAwait(false);
        var before = await service.InventoryAsync(bundle, ct).ConfigureAwait(false);
        await Sharing().ConfigureAwait(false);
        if (before.Bundle.Scope != bundle || before.Bundle.Tombstoned) throw new SpecStorageException(SpecStorageError.NotAdmitted);
        var selected = new List<SpecInventoryItem>();
        var sources = new List<SpecCapturedSource>();
        var extractions = new List<CreationEvidenceDocument>();
        long originalBytes = 0, evidenceBytes = 0;
        foreach (var extractionId in extractionIds.Order())
        {
            var extraction = before.Objects.SingleOrDefault(x => x.Reference.Id.ObjectId == extractionId && x.Reference.Id.Kind == SpecObjectKind.Extraction)
                ?? throw new SpecStorageException(SpecStorageError.Missing);
            if (extraction.State != "published" || extraction.Reference.Id.Source.Bundle != bundle || extraction.Extraction is not { } pin ||
                pin.Source != extraction.Reference.Id.Source || pin.Format != extraction.Format || pin.Outcome != extraction.Outcome || pin.EvidenceDigest != extraction.Reference.Digest)
                throw new SpecStorageException(SpecStorageError.NotAdmitted);
            var original = before.Objects.SingleOrDefault(x => x.Reference.Id.Source == extraction.Reference.Id.Source && x.Reference.Id.Kind == SpecObjectKind.Original)
                ?? throw new SpecStorageException(SpecStorageError.Missing);
            if (original.State != "published" || original.Format != extraction.Format) throw new SpecStorageException(SpecStorageError.Integrity);
            if (sources.Any(s => s.Identity == original.Reference.Id.Source)) throw new ArgumentException("Select one extraction per source version.");
            originalBytes += original.Reference.ByteCount; evidenceBytes += extraction.Reference.ByteCount;
            if (originalBytes > 20 * 1024 * 1024 || evidenceBytes > 20 * 1024 * 1024) throw new SpecStorageException(SpecStorageError.Limit);
            await Sharing().ConfigureAwait(false);
            var bytes = await service.ReadAsync(bundle, original.Reference.Id.ObjectId, ct).ConfigureAwait(false);
            await Sharing().ConfigureAwait(false);
            var evidence = await service.ReadExtractionAsync(bundle, extractionId, ct).ConfigureAwait(false);
            await Sharing().ConfigureAwait(false);
            if (bytes.Length != original.Reference.ByteCount || Sha256Digest.FromBytes(bytes) != original.Reference.Digest ||
                evidence.Length != extraction.Reference.ByteCount || Sha256Digest.FromBytes(evidence) != extraction.Reference.Digest)
                throw new SpecStorageException(SpecStorageError.Integrity);
            var captured = new SpecCapturedSource(original.Reference.Id.Source, original.Format, bytes);
            var extracted = CreationEvidenceDocument.RestoreRetained(captured, pin, evidence);
            selected.Add(original); selected.Add(extraction); sources.Add(captured); extractions.Add(extracted);
        }
        var after = await service.InventoryAsync(bundle, ct).ConfigureAwait(false);
        await Sharing().ConfigureAwait(false);
        if (after.Bundle.Scope != bundle || after.Bundle.Tombstoned || selected.Any(item => !after.Objects.Contains(item)))
            throw new SpecStorageException(SpecStorageError.NotAdmitted);
        return new(bundle, sources, extractions);
    }
}
