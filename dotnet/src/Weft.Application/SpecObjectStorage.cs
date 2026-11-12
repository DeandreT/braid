using System.Security.Cryptography;
using Weft.Domain;

namespace Weft.Application;

public enum SpecStorageError { Missing = 1, ScopeMismatch = 2, Integrity = 3, Conflict = 4, Limit = 5, Unavailable = 6, UnsafeStorage = 7, Io = 8, NotAdmitted = 9 }
public enum SpecPublishResult { Created = 1, AlreadyPresent = 2 }
/// <summary>Current payload retired, or already absent. Neither result asserts deletion of provider-retained versions.</summary>
public enum SpecRetireResult { Retired = 1, Missing = 2 }

/// <summary>Closed diagnostics: no provider messages, document bytes, credentials or physical paths.</summary>
public sealed class SpecStorageException(SpecStorageError error) : Exception("The private specification storage operation failed.")
{
    public SpecStorageError Error { get; } = error;
}

public interface ISpecObjectPublisher
{
    Task<SpecPublishResult> PublishAsync(SpecBundleScope expectedScope, SpecPublicationClaim claim, Stream source, CancellationToken cancellationToken = default);
}
public interface ISpecObjectReader
{
    Task<byte[]> ReadAsync(SpecBundleScope expectedScope, SpecObjectReference reference, CancellationToken cancellationToken = default);
}
public interface ISpecObjectRetirementStore
{
    Task<SpecRetireResult> RetireAsync(SpecBundleScope expectedScope, SpecRetirementClaim claim, CancellationToken cancellationToken = default);
}
public interface ISpecObjectStore : ISpecObjectPublisher, ISpecObjectReader, ISpecObjectRetirementStore { }

/// <summary>
/// Trusted, authoritative lifecycle port, NOT user authorization. No production implementation exists yet.
/// Admission must match store, scope, source version, kind, object, digest, length and actual owner/attempt.
/// Publication admission precedes reading caller bytes. ExecutePublication rechecks tombstones and owns a cross-process
/// fence through physical publication AND durable acknowledgement. A bool check followed by an unfenced write is invalid.
/// Reads require a retained, non-tombstoned object. Retirement requires an issued exact retirement and permanently
/// excludes later publication/revival. All three execution callbacks are mutually fenced for the same object; bundle
/// tombstoning participates in that fence. Callbacks run once at most, are awaited, and are never retained for later use.
/// On a lost acknowledgement the registry reconciles physical state using the same claim; it must not invent a new owner.
/// Missing/denied records throw NotAdmitted; an absent registry is capability-unavailable, never an in-memory fallback.
/// Caller services must independently obtain fresh project/content authority before invoking this boundary.
/// </summary>
public interface ISpecObjectRegistry
{
    Task AdmitPublicationAsync(SpecPublicationClaim claim, CancellationToken cancellationToken);
    Task<SpecPublishResult> ExecutePublicationAsync(SpecPublicationClaim claim, Func<CancellationToken, Task<SpecPublishResult>> publish, CancellationToken cancellationToken);
    Task<byte[]> ExecuteReadAsync(Guid storeId, SpecObjectReference reference, Func<CancellationToken, Task<byte[]>> read, CancellationToken cancellationToken);
    Task<SpecRetireResult> ExecuteRetirementAsync(SpecRetirementClaim claim, Func<CancellationToken, Task<SpecRetireResult>> retire, CancellationToken cancellationToken);
}

/// <summary>Shared bounded verification. Consumes at most the exact expected length plus one byte, leaves input open.</summary>
public static class SpecObjectBytes
{
    public static async Task<byte[]> ReadVerifiedAsync(SpecObjectReference reference, Stream input, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(input);
        using var output = new MemoryStream();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[Math.Min(64 * 1024, reference.ByteCount + 1)];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var count = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, reference.ByteCount - (int)output.Length + 1)), token).ConfigureAwait(false);
            if (count == 0) break;
            if (count > reference.ByteCount - output.Length) throw new SpecStorageException(SpecStorageError.Integrity);
            hash.AppendData(buffer, 0, count);
            output.Write(buffer, 0, count);
        }
        token.ThrowIfCancellationRequested();
        if (output.Length != reference.ByteCount || Convert.ToHexStringLower(hash.GetHashAndReset()) != reference.Digest.Value)
            throw new SpecStorageException(SpecStorageError.Integrity);
        return output.ToArray();
    }
}

/// <summary>Registry-required workflow shared by physical adapters. No authentication or lifecycle decisions live here.</summary>
public abstract class SpecObjectStore : ISpecObjectStore
{
    private readonly ISpecObjectRegistry? registry;
    private readonly SemaphoreSlim concurrency = new(2, 2);
    protected SpecObjectStore(Guid storeId, ISpecObjectRegistry? registry)
    {
        if (storeId == Guid.Empty) throw new ArgumentException("A store identity is required.", nameof(storeId));
        StoreId = storeId;
        this.registry = registry;
    }
    public Guid StoreId { get; }

    public async Task<SpecPublishResult> PublishAsync(SpecBundleScope expectedScope, SpecPublicationClaim claim, Stream source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(source);
        Validate(expectedScope, claim.Reference, claim.StoreId);
        return await Run(async () =>
        {
            await Registry.AdmitPublicationAsync(claim, cancellationToken).ConfigureAwait(false);
            var bytes = await SpecObjectBytes.ReadVerifiedAsync(claim.Reference, source, cancellationToken).ConfigureAwait(false);
            return await Registry.ExecutePublicationAsync(claim, token => PublishCoreAsync(claim, bytes, token), cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<byte[]> ReadAsync(SpecBundleScope expectedScope, SpecObjectReference reference, CancellationToken cancellationToken = default)
    {
        Validate(expectedScope, reference, StoreId);
        return Run(() => Registry.ExecuteReadAsync(StoreId, reference, token => ReadCoreAsync(reference, token), cancellationToken), cancellationToken);
    }

    public Task<SpecRetireResult> RetireAsync(SpecBundleScope expectedScope, SpecRetirementClaim claim, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        Validate(expectedScope, claim.Reference, claim.StoreId);
        return Run(() => Registry.ExecuteRetirementAsync(claim, token => RetireCoreAsync(claim, token), cancellationToken), cancellationToken);
    }

    private ISpecObjectRegistry Registry => registry ?? throw new SpecStorageException(SpecStorageError.Unavailable);
    protected virtual void RequireCapability() { }
    protected abstract Task<SpecPublishResult> PublishCoreAsync(SpecPublicationClaim claim, byte[] verifiedBytes, CancellationToken token);
    protected abstract Task<byte[]> ReadCoreAsync(SpecObjectReference reference, CancellationToken token);
    protected abstract Task<SpecRetireResult> RetireCoreAsync(SpecRetirementClaim claim, CancellationToken token);

    private void Validate(SpecBundleScope scope, SpecObjectReference reference, Guid storeId)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(reference);
        if (scope != reference.Id.Source.Bundle || storeId != StoreId) throw new SpecStorageException(SpecStorageError.ScopeMismatch);
    }

    private async Task<T> Run<T>(Func<Task<T>> operation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _ = Registry;
        RequireCapability();
        await concurrency.WaitAsync(token).ConfigureAwait(false);
        try { return await operation().ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (SpecStorageException) { throw; }
        catch { throw new SpecStorageException(SpecStorageError.Io); }
        finally { concurrency.Release(); }
    }

    /// <summary>Only typed IDs enter this disjoint flat namespace. No filenames, URLs or arbitrary path components.</summary>
    public static string ObjectName(SpecObjectReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var source = reference.Id.Source;
        var scope = source.Bundle;
        return FormattableString.Invariant($"spec-v1-{scope.Project.OrganizationId:N}-{scope.Project.ProjectId:N}-{scope.BundleId:N}-{source.SourceId:N}-{source.Version}-{(int)reference.Id.Kind}-{reference.Id.ObjectId:N}");
    }
}
