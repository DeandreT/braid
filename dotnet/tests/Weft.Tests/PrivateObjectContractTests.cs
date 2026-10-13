using Weft.Application;
using Weft.Domain;

namespace Weft.Tests;

public sealed class PrivateObjectContractTests
{
    private static RevisionScope Scope => new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Guid.Parse("33333333-3333-3333-3333-333333333333"),
        Guid.Parse("44444444-4444-4444-4444-444444444444"));
    private static Guid ObjectId => Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static Sha256Digest Digest =>
        Sha256Digest.Parse("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");

    [Theory]
    [InlineData(PrivateObjectClass.NativeArtifact)]
    [InlineData(PrivateObjectClass.DerivedArtifact)]
    public void IndependentCreatesGenerateDistinctNonemptyIdentitiesWithinTheFullScope(PrivateObjectClass @class)
    {
        var identities = Enumerable.Range(0, 128).Select(_ => PrivateObjectId.Create(Scope, @class)).ToArray();

        Assert.All(identities, identity =>
        {
            Assert.NotEqual(Guid.Empty, identity.ObjectId);
            Assert.Equal(Scope, identity.Scope);
            Assert.Equal(@class, identity.Class);
            Assert.Equal(identity, PrivateObjectId.Restore(Scope, identity.ObjectId, @class));
        });
        Assert.Equal(identities.Length, identities.Select(identity => identity.ObjectId).Distinct().Count());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(int.MaxValue)]
    public void ObjectIdentityRejectsUnknownClassesForNewAndPersistedFacts(int unknown)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PrivateObjectId.Create(Scope, (PrivateObjectClass)unknown));
        Assert.Throws<ArgumentOutOfRangeException>(() => PrivateObjectId.Restore(Scope, ObjectId, (PrivateObjectClass)unknown));
    }

    [Fact]
    public void ObjectIdentityRejectsMissingScopeAndEmptyPersistedIdentifier()
    {
        Assert.Throws<ArgumentNullException>(() => PrivateObjectId.Create(null!, PrivateObjectClass.NativeArtifact));
        Assert.Throws<ArgumentNullException>(() => PrivateObjectId.Restore(null!, ObjectId, PrivateObjectClass.NativeArtifact));
        Assert.Throws<ArgumentException>(() => PrivateObjectId.Restore(Scope, Guid.Empty, PrivateObjectClass.NativeArtifact));
    }

    [Fact]
    public void IdentityEqualityIncludesEveryAncestryIdentifierObjectIdentifierAndClass()
    {
        var identity = PrivateObjectId.Restore(Scope, ObjectId, PrivateObjectClass.NativeArtifact);
        var changed = Guid.Parse("66666666-6666-6666-6666-666666666666");
        RevisionScope[] otherScopes =
        [
            new(changed, Scope.ProjectId, Scope.MappingId, Scope.RevisionId),
            new(Scope.OrganizationId, changed, Scope.MappingId, Scope.RevisionId),
            new(Scope.OrganizationId, Scope.ProjectId, changed, Scope.RevisionId),
            new(Scope.OrganizationId, Scope.ProjectId, Scope.MappingId, changed)
        ];

        Assert.Equal(identity, PrivateObjectId.Restore(
            new RevisionScope(Scope.OrganizationId, Scope.ProjectId, Scope.MappingId, Scope.RevisionId),
            ObjectId, PrivateObjectClass.NativeArtifact));
        foreach (var otherScope in otherScopes)
            Assert.NotEqual(identity, PrivateObjectId.Restore(otherScope, ObjectId, PrivateObjectClass.NativeArtifact));
        Assert.NotEqual(identity, PrivateObjectId.Restore(Scope, changed, PrivateObjectClass.NativeArtifact));
        Assert.NotEqual(identity, PrivateObjectId.Restore(Scope, ObjectId, PrivateObjectClass.DerivedArtifact));
        Assert.Equal(Scope, identity.Scope);
        Assert.Equal(ObjectId, identity.ObjectId);
        Assert.Equal(PrivateObjectClass.NativeArtifact, identity.Class);
    }

    [Fact]
    public void ReferenceRetainsExactDigestCountAndTypedIdentityAsImmutableValueFacts()
    {
        var identity = PrivateObjectId.Restore(Scope, ObjectId, PrivateObjectClass.NativeArtifact);
        var reference = new PrivateObjectReference(identity, Digest, 3);

        Assert.Equal(identity, reference.Id);
        Assert.Equal(Digest, reference.Digest);
        Assert.Equal(3, reference.ByteCount);
        Assert.Equal(reference, new PrivateObjectReference(identity,
            Sha256Digest.Parse(Digest.Value.ToUpperInvariant()), 3));
        Assert.NotEqual(reference, new PrivateObjectReference(identity, Sha256Digest.FromBytes("abd"u8), 3));
        Assert.NotEqual(reference, new PrivateObjectReference(identity, Digest, 4));
        Assert.NotEqual(reference, new PrivateObjectReference(
            PrivateObjectId.Restore(Scope, ObjectId, PrivateObjectClass.DerivedArtifact), Digest, 3));
        Assert.Equal(Digest, reference.Digest);
        Assert.Equal(3, reference.ByteCount);
    }

    [Theory]
    [InlineData(PrivateObjectClass.NativeArtifact)]
    [InlineData(PrivateObjectClass.DerivedArtifact)]
    public void GenericReferenceAllowsZeroAndLeavesUpperLimitsToTheAdapter(PrivateObjectClass @class)
    {
        var identity = PrivateObjectId.Restore(Scope, ObjectId, @class);
        var emptyDigest = Sha256Digest.Parse("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");

        Assert.Equal(0, new PrivateObjectReference(identity, emptyDigest, 0).ByteCount);
        Assert.Equal(long.MaxValue, new PrivateObjectReference(identity, Digest, long.MaxValue).ByteCount);
    }

    [Fact]
    public void ReferenceRejectsMissingFactsAndNegativeByteCounts()
    {
        var identity = PrivateObjectId.Restore(Scope, ObjectId, PrivateObjectClass.NativeArtifact);

        Assert.Throws<ArgumentNullException>(() => new PrivateObjectReference(null!, Digest, 3));
        Assert.Throws<ArgumentNullException>(() => new PrivateObjectReference(identity, null!, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PrivateObjectReference(identity, Digest, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PrivateObjectReference(identity, Digest, long.MinValue));
    }

    [Fact]
    public void StorageFailuresHaveStableBoundedMessagesWithoutStorageDetails()
    {
        var failures = Enum.GetValues<PrivateObjectStorageError>()
            .Select(error => new PrivateObjectStorageException(error)).ToArray();

        Assert.Equal(8, failures.Length);
        Assert.All(failures, failure =>
        {
            Assert.True(Enum.IsDefined(failure.Error));
            Assert.InRange(failure.Message.Length, 1, 128);
            Assert.DoesNotContain("/", failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("\\", failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("\n", failure.Message, StringComparison.Ordinal);
            Assert.Null(failure.InnerException);
            Assert.Equal(failure.Message, new PrivateObjectStorageException(failure.Error).Message);
        });
        Assert.Equal(failures.Length, failures.Select(failure => failure.Message).Distinct().Count());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(int.MaxValue)]
    public void StorageFailuresRejectUnknownErrorClassification(int unknown) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new PrivateObjectStorageException((PrivateObjectStorageError)unknown));
}
