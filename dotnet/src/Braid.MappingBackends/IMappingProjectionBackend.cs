using Braid.Domain;

namespace Braid.MappingBackends;

public interface IMappingProjectionBackend
{
    Task<MappingProjectionResult> ProjectAsync(NativeArtifact artifact, CancellationToken cancellationToken = default);
}
