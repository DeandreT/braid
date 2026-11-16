using System.Text;
using Braid.Deployment;
using Braid.Domain;

namespace Braid.Tests;

public sealed class BuildIntentCodecTests
{
    [Fact]
    public void Canonical_roundtrip_preserves_every_execution_identity()
    {
        var intent = Intent();
        var restored = BuildIntentCodec.Decode(intent.CopyCanonicalBytes());
        Assert.Equal(intent.Fingerprint, restored.Fingerprint);
        Assert.Equal(intent.CopyCanonicalBytes(), restored.CopyCanonicalBytes());
    }

    [Theory]
    [InlineData("space")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("null")]
    public void Noncanonical_or_incomplete_documents_are_rejected(string mutation)
    {
        var text = Encoding.UTF8.GetString(Intent().CopyCanonicalBytes());
        text = mutation switch
        {
            "space" => " " + text,
            "duplicate" => text.Replace("{\"schema\":", "{\"schema\":\"duplicate\",\"schema\":", StringComparison.Ordinal),
            "unknown" => text[..^1] + ",\"extra\":true}",
            _ => text.Replace("\"schema\":\"braid.build-intent.v1\"", "\"schema\":null", StringComparison.Ordinal)
        };
        Assert.Throws<BuildContractException>(() => BuildIntentCodec.Decode(Encoding.UTF8.GetBytes(text)));
    }

    private static BuildIntent Intent()
    {
        var scope = new RevisionScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        return new(Guid.NewGuid(), scope, Sha256Digest.FromBytes([1, 2, 3]), FixtureBuildCompiler.Backend, FixtureBuildCompiler.Profile,
            [new(BuildBoundaryKind.PrimaryInput, null, "fixture-bytes"), new(BuildBoundaryKind.NamedOutput, "name-é", "fixture-bytes")],
            new(new(scope.OrganizationId, scope.ProjectId), Guid.NewGuid(), 2), Guid.NewGuid(), Guid.NewGuid());
    }
}
