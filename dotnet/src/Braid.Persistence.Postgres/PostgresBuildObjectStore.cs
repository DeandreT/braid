using Braid.Application;
using Braid.Domain;

namespace Braid.Persistence.Postgres;

/// <summary>Private build artifacts reside in durable database staging, not the filesystem maintenance namespace.</summary>
public sealed class PostgresBuildObjectStore(PostgresBuildStore builds, IPrivateObjectStore native, ActorContext actor) : IPrivateObjectStore
{
    public async Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(RevisionScope expectedScope, PrivateObjectReference reference,
        Stream source, CancellationToken cancellationToken = default)
    {
        if (reference.Id.Scope != expectedScope || reference.Id.Class != PrivateObjectClass.DerivedArtifact || reference.ByteCount is < 1 or > PostgresBuildStore.MaximumArtifactBytes)
            throw new PrivateObjectStorageException(PrivateObjectStorageError.ScopeMismatch);
        var bytes = new byte[(int)reference.ByteCount];
        await source.ReadExactlyAsync(bytes, cancellationToken);
        if (await source.ReadAsync(new byte[1], cancellationToken) != 0 || Sha256Digest.FromBytes(bytes) != reference.Digest)
            throw new PrivateObjectStorageException(PrivateObjectStorageError.Integrity);
        return await builds.PublishObjectAsync(actor, expectedScope, reference, bytes, cancellationToken);
    }

    public async Task<byte[]> ReadVerifiedAsync(RevisionScope expectedScope, PrivateObjectReference reference, CancellationToken cancellationToken = default)
    {
        if (reference.Id.Scope != expectedScope) throw new PrivateObjectStorageException(PrivateObjectStorageError.ScopeMismatch);
        if (reference.Id.Class == PrivateObjectClass.NativeArtifact) return await native.ReadVerifiedAsync(expectedScope, reference, cancellationToken);
        var staged = await builds.ReadObjectAsync(actor, expectedScope.OrganizationId, reference.Id.ObjectId, cancellationToken);
        if (staged is null) throw new PrivateObjectStorageException(PrivateObjectStorageError.Missing);
        if (staged.Publication.Object != reference || staged.Publication.Artifact.Intent.Revision != expectedScope)
            throw new PrivateObjectStorageException(PrivateObjectStorageError.Integrity);
        return staged.Bytes.Copy();
    }
}
