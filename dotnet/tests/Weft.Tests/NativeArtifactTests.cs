using System.Text;
using Weft.Domain;

namespace Weft.Tests;

public sealed class NativeArtifactTests
{
    private static RevisionScope Scope => new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Guid.Parse("33333333-3333-3333-3333-333333333333"),
        Guid.Parse("44444444-4444-4444-4444-444444444444"));

    [Fact]
    public void ArtifactOwnsAdmissionAndRetrievedBuffers()
    {
        var source = "abc"u8.ToArray();
        var artifact = NativeArtifact.Admit(Scope, source, 3);
        const string expectedDigest = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
        source[0] = (byte)'z';
        var retrieved = artifact.CopyBytes();
        Assert.Equal("abc"u8.ToArray(), retrieved);
        retrieved[1] = (byte)'z';

        Assert.Equal("abc"u8.ToArray(), artifact.CopyBytes());
        Assert.Equal(expectedDigest, artifact.Digest.Value);
        Assert.Equal(Sha256Digest.FromBytes(artifact.CopyBytes()), artifact.Digest);
        Assert.Equal(3, artifact.ByteCount);
        Assert.Equal(Scope, artifact.Scope);
        Assert.NotSame(retrieved, artifact.CopyBytes());
    }

    [Fact]
    public void ArtifactPreservesNativeFormattingAndUnknownMetadata()
    {
        var source = Encoding.UTF8.GetBytes("\uFEFF{\n  \"nativeId\": 9007199254740993, \"unknown\": {\"bits\": \"3ff0000000000001\"}\n}\n");
        var artifact = NativeArtifact.Admit(Scope, source, source.Length);

        Assert.Equal(source, artifact.CopyBytes());
        Assert.Equal(source.Length, artifact.ByteCount);
        Assert.Equal(Sha256Digest.FromBytes(source), artifact.Digest);
    }

    [Fact]
    public void ArtifactRejectsMalformedAndOversizedAdmission()
    {
        Assert.Throws<ArgumentNullException>(() => NativeArtifact.Admit(null!, "abc"u8, 3));
        Assert.Throws<ArgumentException>(() => NativeArtifact.Admit(Scope, [], 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeArtifact.Admit(Scope, "abc"u8, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeArtifact.Admit(Scope, "abc"u8, -1));
        Assert.Throws<ArgumentException>(() => NativeArtifact.Admit(Scope, "abc"u8, 2));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RevisionScopeRejectsEachEmptyIdentifier(int emptyIndex)
    {
        Guid[] ids = [Scope.OrganizationId, Scope.ProjectId, Scope.MappingId, Scope.RevisionId];
        ids[emptyIndex] = Guid.Empty;

        Assert.Throws<ArgumentException>(() => new RevisionScope(ids[0], ids[1], ids[2], ids[3]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015a")]
    [InlineData("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015add")]
    [InlineData("za7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    [InlineData(" ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    public void DigestRejectsMalformedText(string value) =>
        Assert.Throws<ArgumentException>(() => Sha256Digest.Parse(value));

    [Fact]
    public void DigestNormalizesHexadecimalCase()
    {
        var digest = Sha256Digest.FromBytes("abc"u8);

        Assert.Equal(digest, Sha256Digest.Parse(digest.Value.ToUpperInvariant()));
        Assert.Throws<ArgumentNullException>(() => Sha256Digest.Parse(null!));
    }

    [Fact]
    public void CacheIdentityIsDeterministicAndIncludesEveryProvenanceComponent()
    {
        var artifact = NativeArtifact.Admit(Scope, "abc"u8, 3);
        var original = new ProjectionCacheKey(artifact.Scope, artifact.Digest, "weft.synthetic", "engine-1", "engine-build-1", "build-1", "1");
        var repeated = new ProjectionCacheKey(Scope, Sha256Digest.FromBytes("abc"u8), "weft.synthetic", "engine-1", "engine-build-1", "build-1", "1");
        Assert.Equal(original, repeated);
        Assert.Contains(repeated, new HashSet<ProjectionCacheKey> { original });

        var changedId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        ProjectionCacheKey[] changes =
        [
            new(Scope, Sha256Digest.FromBytes("abd"u8), "weft.synthetic", "engine-1", "engine-build-1", "build-1", "1"),
            new(Scope, artifact.Digest, "another.backend", "engine-1", "engine-build-1", "build-1", "1"),
            new(Scope, artifact.Digest, "weft.synthetic", "engine-2", "engine-build-1", "build-1", "1"),
            new(Scope, artifact.Digest, "weft.synthetic", "engine-1", "engine-build-2", "build-1", "1"),
            new(Scope, artifact.Digest, "weft.synthetic", "engine-1", "engine-build-1", "build-2", "1"),
            new(Scope, artifact.Digest, "weft.synthetic", "engine-1", "engine-build-1", "build-1", "2"),
            new(new RevisionScope(changedId, Scope.ProjectId, Scope.MappingId, Scope.RevisionId), artifact.Digest, "weft.synthetic", "engine-1", "engine-build-1", "build-1", "1"),
            new(new RevisionScope(Scope.OrganizationId, changedId, Scope.MappingId, Scope.RevisionId), artifact.Digest, "weft.synthetic", "engine-1", "engine-build-1", "build-1", "1"),
            new(new RevisionScope(Scope.OrganizationId, Scope.ProjectId, changedId, Scope.RevisionId), artifact.Digest, "weft.synthetic", "engine-1", "engine-build-1", "build-1", "1"),
            new(new RevisionScope(Scope.OrganizationId, Scope.ProjectId, Scope.MappingId, changedId), artifact.Digest, "weft.synthetic", "engine-1", "engine-build-1", "build-1", "1")
        ];

        Assert.All(changes, changed => Assert.NotEqual(original, changed));
    }

    [Theory]
    [InlineData("", "engine-1", "engine-build-1", "build-1", "1")]
    [InlineData("weft.synthetic", "", "engine-build-1", "build-1", "1")]
    [InlineData("weft.synthetic", "engine-1", " ", "build-1", "1")]
    [InlineData("weft.synthetic", "engine-1", "engine-build-1", " ", "1")]
    [InlineData("weft.synthetic", "engine-1", "engine-build-1", "build-1", "")]
    public void CacheIdentityRejectsMissingProvenance(string backendId, string backendRevision, string backendBuild, string adapterBuild, string schemaVersion) =>
        Assert.Throws<ArgumentException>(() => new ProjectionCacheKey(Scope, Sha256Digest.FromBytes("abc"u8), backendId, backendRevision, backendBuild, adapterBuild, schemaVersion));
}
