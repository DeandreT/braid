using Weft.Domain;

namespace Weft.MappingBackends;

public interface IMappingProjectionBackend
{
    Task<MappingProjectionResult> ProjectAsync(NativeArtifact artifact, CancellationToken cancellationToken = default);
}
