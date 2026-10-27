using Weft.Api.Catalog;
using Weft.Deployment;
using Weft.Domain;
using Weft.MappingBackends;

namespace Weft.Tests;

public sealed class X12InterchangeProfileTests
{
    [Fact]
    public void Explicit_profile_is_bound_to_intent_and_artifact_and_never_relabels_a_single_profile()
    {
        var scope = new RevisionScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var legacy = new BuildBackendIdentity("ferrule", BuildWorkerProtocol.Engine, "sha256:" + new string('a', 64), FerruleBuildCompiler.Adapter);
        var backend = new BuildBackendIdentity("ferrule", BuildWorkerProtocol.Engine, legacy.EngineBuild, FerrulePreviewBackend.DocumentAdapterBuild);
        var id = Guid.NewGuid(); var approval = Guid.NewGuid(); var evidence = Guid.NewGuid();
        var environment = new BuildEnvironmentReference(new(scope.OrganizationId, scope.ProjectId), Guid.NewGuid(), 1);
        BuildIntent Intent(BuildRuntimeProfile profile) => new(id, scope, Sha256Digest.FromBytes([1]), backend, profile,
            [new(BuildBoundaryKind.PrimaryInput, null, "x12"), new(BuildBoundaryKind.PrimaryOutput, null, "json")], environment, approval, evidence);
        var single = Intent(FerruleBuildCompiler.DocumentProfile);
        var multiple = Intent(FerruleBuildCompiler.X12InterchangesProfile);
        Assert.NotEqual(single.Fingerprint, multiple.Fingerprint);
        var artifact = BuildArtifactReference.Restore(multiple, Sha256Digest.FromBytes([2]), 1);
        Assert.False(artifact.MatchesIntent(single));
        Assert.True(artifact.MatchesIntent(multiple));
        Assert.Equal(BuildWorkerProtocol.X12InterchangesProfile, BuildWorkerProtocol.For(multiple, "inspect").Profile);
        Assert.Equal(CompiledRuntimeProtocol.DocumentProtocol, CompiledRuntimeProtocol.ProtocolFor(multiple));
        Assert.True(BuildWorkerProtocol.AdmitsFormat(BuildWorkerProtocol.X12InterchangesProfile, "x12"));
        Assert.False(BuildWorkerProtocol.AdmitsFormat(BuildWorkerProtocol.X12InterchangesProfile, "xml"));
        Assert.NotNull(new CatalogBuildOptions(true, legacy, FerruleBuildCompiler.Profile, backend, FerruleBuildCompiler.DocumentProfile, FerruleBuildCompiler.X12InterchangesProfile));
        Assert.Throws<ArgumentException>(() => new CatalogBuildOptions(true, legacy, FerruleBuildCompiler.Profile, backend, FerruleBuildCompiler.DocumentProfile, FerruleBuildCompiler.Profile));
    }
}
