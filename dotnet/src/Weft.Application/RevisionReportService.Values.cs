using System.Security.Cryptography;
using Weft.Domain;

namespace Weft.Application;

public sealed partial class RevisionReportService
{
    private async Task<AuthorizedRevisionReportSource?> SourceAsync(ActorContext actor, RevisionScope scope,
        RevisionReportSource? expected, CancellationToken token)
    {
        var source = await MetadataReadAsync(() => _reader.GetSourceAsync(actor, scope, token));
        token.ThrowIfCancellationRequested();
        if (source is not null)
        {
            if (source.Actor != actor.PrincipalId || source.Source.Scope != scope ||
                expected is not null && source.Source != expected) throw Inconsistent();
        }
        return source;
    }

    private async Task<RevisionReportReadSnapshot?> CurrentAsync(ActorContext actor, RevisionReportKey key,
        CancellationToken token)
    {
        var snapshot = await MetadataReadAsync(() => _reader.GetCurrentAsync(actor, key, token));
        token.ThrowIfCancellationRequested();
        if (snapshot is not null) MatchSnapshot(actor, key, snapshot);
        return snapshot;
    }

    private async Task<AuthorizedRevisionReportAttempt?> RetainedAsync(ActorContext actor, RevisionScope scope,
        RevisionReportOperation operation, Guid derivationId, CancellationToken token)
    {
        var retained = await MetadataReadAsync(() => _reader.GetRetainedAsync(actor, scope, operation, derivationId, token));
        token.ThrowIfCancellationRequested();
        if (retained is not null && (retained.Source.Actor != actor.PrincipalId || retained.Key.Scope != scope ||
            retained.Key.Operation != operation || retained.Attempt.DerivationId != derivationId)) throw Inconsistent();
        return retained;
    }

    private static async Task<T> MetadataReadAsync<T>(Func<Task<T>> read)
    {
        try { return await read(); }
        catch (OperationCanceledException) { throw; }
        catch (RevisionReportServiceException) { throw; }
        catch (Exception)
        {
            // Availability is a service category, not an invented backend observation or denial.
            throw new RevisionReportServiceException(RevisionReportFailureCategory.BackendUnavailable);
        }
    }

    private static void MatchSnapshot(ActorContext actor, RevisionReportKey key, RevisionReportReadSnapshot snapshot)
    {
        if (snapshot.Key != key || snapshot.Source.Source != key.Source || snapshot.Source.Actor != actor.PrincipalId)
            throw Inconsistent();
    }

    private (RevisionReportRegistration Registration, RevisionReportKey Key)? SelectCurrent(
        RevisionReportSource source, RevisionReportOperation operation)
    {
        RevisionReportRegistration? registration;
        try { registration = _registry.FindCurrent(source.SourceBackendId, operation); }
        catch (RevisionReportServiceException) { throw; }
        catch (Exception) { throw new RevisionReportServiceException(RevisionReportFailureCategory.ProfileUnavailable); }
        if (registration is null) return null;
        var format = registration.Format;
        if (format.Operation != operation || format.BackendId != source.SourceBackendId ||
            registration.Profile.BackendId != source.SourceBackendId)
            throw new RevisionReportServiceException(RevisionReportFailureCategory.ProfileUnavailable);
        var policy = _storage.ApplyTo(registration.Policy);
        RevisionReportKey key = operation == RevisionReportOperation.Projection
            ? new ProjectionCacheKey(source, registration.Profile, format.SchemaVersion, format.AlgorithmVersion, format.CodecVersion, policy)
            : new ValidationCacheKey(source, registration.Profile, format.SchemaVersion, format.AlgorithmVersion, format.CodecVersion, policy);
        return (registration, key);
    }

    private IPrivateObjectStore Store()
    {
        try { return _storage.GetStore(); }
        catch (OperationCanceledException) { throw; }
        catch (PrivateObjectStorageException) { throw; }
        catch (RevisionReportServiceException) { throw; }
        catch (Exception) { throw new RevisionReportServiceException(RevisionReportFailureCategory.StorageIo); }
    }

    private static async Task<byte[]> ReadObjectAsync(IPrivateObjectStore store, PrivateObjectReference reference,
        CancellationToken token)
    {
        try { return await store.ReadVerifiedAsync(reference.Id.Scope, reference, token); }
        catch (OperationCanceledException) { throw; }
        catch (PrivateObjectStorageException) { throw; }
        catch (RevisionReportServiceException) { throw; }
        catch (Exception) { throw new RevisionReportServiceException(RevisionReportFailureCategory.StorageIo); }
    }

    private static byte[] OwnBytes(byte[]? returned, PrivateObjectReference reference, int maximum, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (returned is null) throw new PrivateObjectStorageException(PrivateObjectStorageError.Integrity);
        if (returned.Length > maximum) throw new PrivateObjectStorageException(PrivateObjectStorageError.Limit);
        if (returned.LongLength != reference.ByteCount) throw new PrivateObjectStorageException(PrivateObjectStorageError.Integrity);
        var owned = new byte[returned.Length];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var offset = 0; offset < owned.Length; offset += 4096)
        {
            token.ThrowIfCancellationRequested();
            var count = Math.Min(4096, owned.Length - offset);
            returned.AsSpan(offset, count).CopyTo(owned.AsSpan(offset, count));
            hash.AppendData(owned.AsSpan(offset, count));
        }
        token.ThrowIfCancellationRequested();
        if (Sha256Digest.Parse(Convert.ToHexStringLower(hash.GetHashAndReset())) != reference.Digest)
            throw new PrivateObjectStorageException(PrivateObjectStorageError.Integrity);
        return owned;
    }

    private static RevisionReportReadOutcome ReadFailure(PrivateObjectStorageException error) => error.Error switch
    {
        PrivateObjectStorageError.Missing => RevisionReportReadOutcome.ObjectUnavailable,
        PrivateObjectStorageError.ScopeMismatch or PrivateObjectStorageError.Conflict or PrivateObjectStorageError.Integrity
            => RevisionReportReadOutcome.StorageIntegrity,
        PrivateObjectStorageError.Limit => RevisionReportReadOutcome.ResourceLimit,
        _ => throw new RevisionReportServiceException(RevisionReportFailureCategory.StorageIo)
    };

    private static RevisionReportReadOutcome ReadFailure(RevisionReportCodecException error) => error.Failure switch
    {
        RevisionReportCodecFailure.UnsupportedFormat => RevisionReportReadOutcome.UnsupportedFormat,
        RevisionReportCodecFailure.Integrity => RevisionReportReadOutcome.StorageIntegrity,
        RevisionReportCodecFailure.ResourceLimit => RevisionReportReadOutcome.ResourceLimit,
        _ => throw Inconsistent()
    };

    private static RevisionReportFailureCategory Failure(Exception error) => error switch
    {
        RevisionReportServiceException service => service.Failure,
        RevisionReportValidationException { Failure: RevisionReportValidationFailure.ResourceLimit } => RevisionReportFailureCategory.ResourceLimit,
        RevisionReportValidationException => RevisionReportFailureCategory.ProtocolFailure,
        RevisionReportCodecException { Failure: RevisionReportCodecFailure.ResourceLimit } => RevisionReportFailureCategory.ResourceLimit,
        RevisionReportCodecException { Failure: RevisionReportCodecFailure.UnsupportedFormat } => RevisionReportFailureCategory.ProfileUnavailable,
        RevisionReportCodecException => RevisionReportFailureCategory.ProtocolFailure,
        PrivateObjectStorageException { Error: PrivateObjectStorageError.Missing } => RevisionReportFailureCategory.ObjectUnavailable,
        PrivateObjectStorageException { Error: PrivateObjectStorageError.Limit } => RevisionReportFailureCategory.ResourceLimit,
        PrivateObjectStorageException { Error: PrivateObjectStorageError.Integrity or PrivateObjectStorageError.ScopeMismatch or PrivateObjectStorageError.Conflict }
            => RevisionReportFailureCategory.StorageIntegrity,
        PrivateObjectStorageException => RevisionReportFailureCategory.StorageIo,
        _ => RevisionReportFailureCategory.ProtocolFailure
    };
}
