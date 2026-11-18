using System.Text;
using Braid.Deployment;
using Braid.Domain;

namespace Braid.Tests;

public sealed class MappingPackageManifestTests
{
    [Fact]
    public async Task Package_stream_is_bounded_and_rejects_truncation_append_and_substitution()
    {
        var (artifact, bytes) = Fixture();
        var package = new MappingPackage(MappingPackageManifest.Create(artifact), bytes);
        using var wire = new MemoryStream();
        await package.WriteAsync(wire, default);
        async Task<MappingPackage> Read(byte[] data, int max = 3)
        {
            using var source = new MemoryStream(data);
            return await MappingPackage.ReadAsync(source, max, default);
        }
        var frame = wire.ToArray();
        Assert.True((await Read(frame)).Manifest.Matches(artifact, bytes));
        await Assert.ThrowsAsync<BuildContractException>(() => Read(frame, 2));
        await Assert.ThrowsAsync<BuildContractException>(() => Read(frame[..^1]));
        await Assert.ThrowsAsync<BuildContractException>(() => Read([.. frame, 0]));
        await Assert.ThrowsAsync<BuildContractException>(() => Read([127, 255, 255, 255]));
        frame[^1] ^= 1;
        await Assert.ThrowsAsync<BuildContractException>(() => Read(frame));
    }
    [Fact]
    public void Canonical_roundtrip_binds_bytes_and_complete_build_intent()
    {
        var (artifact, bytes) = Fixture();
        var manifest = MappingPackageManifest.Create(artifact);
        var restored = MappingPackageManifest.Decode(manifest.CopyCanonicalBytes());
        Assert.Equal(manifest.Fingerprint, restored.Fingerprint);
        Assert.Equal(artifact.Intent.CopyCanonicalBytes(), restored.Artifact.Intent.CopyCanonicalBytes());
        Assert.True(restored.Matches(artifact, bytes));
        Assert.False(restored.Matches(artifact, new BuildBytes([9], 10)));
        Assert.Equal(nameof(MappingPackageManifest), manifest.ToString());
    }

    [Fact]
    public void Neither_returned_bytes_nor_the_supplied_bytes_can_change_identity()
    {
        var (artifact, bytes) = Fixture();
        var manifest = MappingPackageManifest.Create(artifact);
        var copy = manifest.CopyCanonicalBytes();
        var restored = MappingPackageManifest.Decode(copy);
        Array.Fill<byte>(copy, 0);
        Assert.Equal(manifest.CopyCanonicalBytes(), restored.CopyCanonicalBytes());
        Assert.True(restored.Matches(artifact, bytes));
    }

    [Fact]
    public void Identical_dll_under_another_build_does_not_match()
    {
        var (artifact, bytes) = Fixture();
        var (other, _) = Fixture();
        Assert.False(MappingPackageManifest.Create(artifact).Matches(other, bytes));
    }

    [Theory]
    [InlineData("whitespace")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("version")]
    [InlineData("path")]
    [InlineData("empty")]
    [InlineData("size")]
    [InlineData("null")]
    [InlineData("intent")]
    public void Noncanonical_or_unbounded_manifest_is_refused(string mutation)
    {
        var (artifact, _) = Fixture();
        var text = Encoding.UTF8.GetString(MappingPackageManifest.Create(artifact).CopyCanonicalBytes());
        text = mutation switch
        {
            "whitespace" => " " + text,
            "unknown" => text[..^1] + ",\"extra\":true}",
            "duplicate" => text.Replace("{\"schema\":", "{\"schema\":\"duplicate\",\"schema\":", StringComparison.Ordinal),
            "version" => text.Replace(MappingPackageManifest.SchemaVersion, "braid.mapping-package.v999", StringComparison.Ordinal),
            "path" => text.Replace(BuildCompilation.Output, "../Ferrule.Generated.dll", StringComparison.Ordinal),
            "empty" => "",
            "size" => text.Replace("\"byteCount\":3", "\"byteCount\":0", StringComparison.Ordinal),
            "null" => "null",
            _ => text.Replace("\"intent\":{", "\"intent\":{\"unknown\":1,", StringComparison.Ordinal)
        };
        Assert.Throws<BuildContractException>(() => MappingPackageManifest.Decode(Encoding.UTF8.GetBytes(text)));
    }

    [Fact]
    public void Oversized_manifest_is_refused_before_parsing() =>
        Assert.Throws<BuildContractException>(() => MappingPackageManifest.Decode(new byte[MappingPackageManifest.MaximumBytes + 1]));

    private static (BuildArtifactReference Artifact, BuildBytes Bytes) Fixture()
    {
        var scope = new RevisionScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var intent = new BuildIntent(Guid.NewGuid(), scope, Sha256Digest.FromBytes([4]), FixtureBuildCompiler.Backend, FixtureBuildCompiler.Profile,
            [new(BuildBoundaryKind.PrimaryInput, null, "fixture-bytes"), new(BuildBoundaryKind.NamedOutput, "name-é", "fixture-bytes")],
            new(new(scope.OrganizationId, scope.ProjectId), Guid.NewGuid(), 2), Guid.NewGuid(), Guid.NewGuid());
        var bytes = new BuildBytes([1, 2, 3], 10);
        return (BuildArtifactReference.Restore(intent, bytes.Digest, bytes.Length), bytes);
    }
}
