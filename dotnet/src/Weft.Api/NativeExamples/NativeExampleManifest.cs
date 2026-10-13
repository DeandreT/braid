using System.Diagnostics.CodeAnalysis;
using Weft.Contracts.Examples;
using Weft.Contracts.Projections;
using Weft.Domain;

namespace Weft.Api;

/// <summary>Fixed, self-authored native bytes packaged with the development host.</summary>
public sealed class NativeExampleManifest
{
    public const int MaximumNativeBytes = 32 * 1024;
    private readonly Dictionary<string, (NativeExampleDescriptorV1 Descriptor, NativeArtifact Artifact)> _examples = new(StringComparer.Ordinal);

    public NativeExampleManifest()
    {
        var definitions = new (string Id, string Title, string File, int ByteCount, string Digest, string MappingId)[]
        {
            ("basic", "Direct fields and dotted names", "valid-basic.json", 2191, "4220877674cba63c004226d487f5a80042369aab30350a964dc5fff5df48dc26", "63333333-3333-4333-8333-000000000001"),
            ("exact", "Exact numbers and negative zero", "versioned-exact.json", 1566, "b9ac8045a2b3fba94520043cdb5f1a0be7dde34aa2ecd577fafce9168dd440e7", "63333333-3333-4333-8333-000000000002"),
            ("conditions", "Conditions, defaults and parameters", "projection-basic.json", 6261, "b89a104bc196aa83ecc0b028865ce211eaa1ea15d7cb7de80e0800e31e12cbb2", "63333333-3333-4333-8333-000000000003"),
            ("controls", "Controls and named boundaries", "projection-controls.json", 8612, "49ab8c57239e61876c125d0c3469d20babb41e50cf3b26a2aa98f78eb8e9ba19", "63333333-3333-4333-8333-000000000004"),
            ("functions", "Reusable functions", "projection-functions.json", 2308, "898dc06ca138da7c50786ddb223f9f11d0770927cab0b8906c900796c6344b5e", "63333333-3333-4333-8333-000000000005"),
            ("partial", "Partial explanation and dynamic sources", "projection-partial.json", 2478, "fb01b40491fa38c35e65463d63c8d057db975e12b80254fa03ca4a4f289d152e", "63333333-3333-4333-8333-000000000006"),
            ("invalid", "Native validation findings", "validation-cycle.json", 526, "54514537939302ceda435e4403da8051935f55c9e684dc2f9dadfb05145f52c2", "63333333-3333-4333-8333-000000000007"),
            ("generated", "Generated source context", "projection-generated-source-context.json", 776, "34a41f42abeba2bd28caf051063839d8827a3ee99c33fb1759964e7a950136c1", "63333333-3333-4333-8333-000000000008"),
            ("ambiguous", "Ambiguous join source context", "projection-join-context.json", 1701, "308ad4c997a12597bf80309eff9ff2a676f110eab7b6f9c218db75517259547d", "63333333-3333-4333-8333-000000000009")
        };
        var descriptors = new List<NativeExampleDescriptorV1>(definitions.Length);
        foreach (var definition in definitions)
        {
            using var stream = typeof(NativeExampleManifest).Assembly.GetManifestResourceStream("Weft.Api.NativeExamples." + definition.File)
                ?? throw new InvalidOperationException("A packaged native example is missing.");
            var bytes = new byte[definition.ByteCount];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1)
                throw new InvalidOperationException("A packaged native example has changed without a revision update.");
            var scope = new RevisionScope(
                Guid.Parse("61111111-1111-4111-8111-111111111111"),
                Guid.Parse("62222222-2222-4222-8222-222222222222"),
                Guid.Parse(definition.MappingId),
                Guid.ParseExact(definition.Digest[..32], "N"));
            var artifact = NativeArtifact.Admit(scope, bytes, MaximumNativeBytes);
            if (artifact.Digest.Value != definition.Digest)
                throw new InvalidOperationException("A packaged native example has changed without a revision update.");
            var descriptor = new NativeExampleDescriptorV1(definition.Id, definition.Title, "Self-authored native example",
                Revision(scope), new ArtifactDigest("sha256", artifact.Digest.Value), artifact.ByteCount);
            _examples.Add(definition.Id, (descriptor, artifact));
            descriptors.Add(descriptor);
        }
        Examples = descriptors.AsReadOnly();
    }

    public IReadOnlyList<NativeExampleDescriptorV1> Examples { get; }

    public bool TryGet(string id, [NotNullWhen(true)] out NativeExampleDescriptorV1? descriptor, [NotNullWhen(true)] out NativeArtifact? artifact)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (_examples.TryGetValue(id, out var example))
        {
            descriptor = example.Descriptor;
            artifact = example.Artifact;
            return true;
        }
        descriptor = null;
        artifact = null;
        return false;
    }

    internal static ScopedRevisionId Revision(RevisionScope scope) => new(scope.OrganizationId.ToString("D"), scope.ProjectId.ToString("D"),
        scope.MappingId.ToString("D"), scope.RevisionId.ToString("D"));
}
