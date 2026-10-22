using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Weft.Application;
using Weft.Domain;

namespace Weft.Storage.Blob;

public sealed partial class BlobPrivateObjectStore
{
    /// <summary>Current committed facts at one name, or null when no committed blob exists there.</summary>
    private static async Task<BlobFacts?> InspectAsync(BlobClient blob, CancellationToken token)
    {
        try
        {
            var properties = (await blob.GetPropertiesAsync(conditions: null, token).ConfigureAwait(false)).Value;
            var facts = BlobFacts.Classify(properties.ETag, properties.ContentLength, properties.Metadata);
            return properties.BlobType == BlobType.Block ? facts : facts with { Form = BlobForm.Unknown };
        }
        catch (RequestFailedException error) when (error.Status == 404) { return null; }
    }

    /// <summary>Installs a permanent empty gate once; an existing name must already be exactly a gate.</summary>
    private async Task EnsureGateAsync(string name, CancellationToken token, bool finalObject = false)
    {
        if (await TryCreateAsync(BlockBlob(name), [], BlobFacts.Metadata(BlobForm.Gate), token).ConfigureAwait(false))
            return;
        var existing = await InspectAsync(Blob(name), token).ConfigureAwait(false);
        if (existing is not { Form: BlobForm.Gate } &&
            !(finalObject && existing is { Form: BlobForm.Object or BlobForm.Retired }))
            throw Failure(PrivateObjectStorageError.UnsafeStorage);
    }

    private async Task<BlobGate?> TryAcquireGateAsync(string name, CancellationToken token, bool finalObject = false)
    {
        var gate = await BlobGate.TryAcquireAsync(Blob(name), token).ConfigureAwait(false);
        if (gate is null) return null;
        try
        {
            var existing = await InspectAsync(Blob(name), token).ConfigureAwait(false);
            if (existing is not { Form: BlobForm.Gate } &&
                !(finalObject && existing is { Form: BlobForm.Object or BlobForm.Retired }))
                throw Failure(PrivateObjectStorageError.UnsafeStorage);
            return gate;
        }
        catch { await gate.DisposeAsync().ConfigureAwait(false); throw; }
    }

    /// <summary>
    /// Replaces the exact observed blob, or claims an absent name, with a permanent empty retirement fence.
    /// Promotion requires a vacant reservation and its current lease, so a fenced name cannot hold payload again.
    /// The fence is an empty block-list commit, the same conditional primitive as publication, so
    /// another publisher's uncommitted blocks neither block it nor survive it.
    /// </summary>
    private async Task<bool> TryFenceAsync(string name, BlobFacts? observed, CancellationToken token, string? leaseId = null)
    {
        try
        {
            await BlockBlob(name).CommitBlockListAsync([], new CommitBlockListOptions
            {
                Conditions = observed is { } existing
                    ? new BlobRequestConditions { IfMatch = existing.ETag, LeaseId = leaseId }
                    : new BlobRequestConditions { IfNoneMatch = ETag.All },
                Metadata = BlobFacts.Metadata(BlobForm.Retired)
            }, token).ConfigureAwait(false);
            return true;
        }
        catch (RequestFailedException error) when (leaseId is not null && error.Status is 409 or 412 &&
            error.ErrorCode is "LeaseIdMismatchWithBlobOperation" or "LeaseNotPresentWithBlobOperation" or "LeaseLost")
        {
            throw Failure(PrivateObjectStorageError.Io);
        }
        catch (RequestFailedException error) when (error.Status is 404 or 409 or 412) { return false; }
    }

    /// <summary>
    /// Reads a final object in one consistent download. Only an exact published object can return
    /// bytes; a retirement fence is Missing and every other form is unsafe.
    /// </summary>
    private async Task<(byte[] Bytes, BlobFacts Facts)> ReadFinalAsync(PrivateObjectReference reference, CancellationToken token)
    {
        Response<BlobDownloadStreamingResult> response;
        try
        {
            response = await Blob(BlobLayout.FinalName(reference.Id)).DownloadStreamingAsync(new BlobDownloadOptions(), token)
                .ConfigureAwait(false);
        }
        catch (RequestFailedException error) when (error.Status == 404) { throw Failure(PrivateObjectStorageError.Missing); }
        using var result = response.Value;
        var details = result.Details;
        var facts = BlobFacts.Classify(details.ETag, details.ContentLength, details.Metadata);
        if (details.BlobType != BlobType.Block) throw Failure(PrivateObjectStorageError.UnsafeStorage);
        if (facts.Form is BlobForm.Retired or BlobForm.Gate) throw Failure(PrivateObjectStorageError.Missing);
        if (facts.Form != BlobForm.Object || facts.DeclaredLength != details.ContentLength)
            throw Failure(PrivateObjectStorageError.UnsafeStorage);
        if (details.ContentLength != reference.ByteCount || facts.Sha256 != reference.Digest.Value)
            throw Failure(PrivateObjectStorageError.Integrity);
        var bytes = new byte[checked((int)reference.ByteCount)];
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        await using var content = result.Content;
        var count = 0;
        while (count < bytes.Length)
        {
            token.ThrowIfCancellationRequested();
            var read = await content.ReadAsync(bytes.AsMemory(count, Math.Min(ChunkSize, bytes.Length - count)), token)
                .ConfigureAwait(false);
            if (read == 0) throw Failure(PrivateObjectStorageError.Integrity);
            hash.AppendData(bytes.AsSpan(count, read));
            count += read;
        }
        if (await content.ReadAsync(new byte[1], token).ConfigureAwait(false) != 0 ||
            Convert.ToHexStringLower(hash.GetHashAndReset()) != reference.Digest.Value)
            throw Failure(PrivateObjectStorageError.Integrity);
        return (bytes, facts);
    }

    private async Task<(BlobPublicationRecord Record, Sha256Digest Digest)> InstallSealAsync(BlobPublicationRecord record,
        CancellationToken token)
    {
        var bytes = BlobRecordCodec.Encode(record);
        var name = BlobLayout.Seal(_controlId, record.Context.Reference.Id, record.Context.TempId, record.Kind);
        if (!await TryCreateAsync(BlockBlob(name), bytes, BlobFacts.Metadata(BlobForm.Seal), token).ConfigureAwait(false))
            throw Failure(PrivateObjectStorageError.UnsafeStorage);
        return (record, BlobRecordCodec.Digest(bytes));
    }

    private async Task<(BlobPublicationRecord Record, Sha256Digest Digest)?> ReadSealAsync(PrivateObjectId id, Guid tempId,
        BlobRecordKind kind, CancellationToken token)
    {
        var bytes = await ReadSmallAsync(Blob(BlobLayout.Seal(_controlId, id, tempId, kind)), BlobForm.Seal,
            BlobRecordCodec.MaximumRecordBytes, token).ConfigureAwait(false);
        if (bytes is null) return null;
        var record = BlobRecordCodec.Decode(bytes);
        if (record.Kind != kind || record.Context.RootId != _rootId || record.Context.ControlId != _controlId ||
            record.Context.Reference.Id != id || record.Context.TempId != tempId)
            throw Failure(PrivateObjectStorageError.UnsafeStorage);
        return (record, BlobRecordCodec.Digest(bytes));
    }
}
