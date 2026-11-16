using Braid.Domain;

namespace Braid.MappingBackends;

public interface IMappingInspectionBackend
{
    Task<MappingInspectionResult> InspectAsync(NativeArtifact artifact, CancellationToken cancellationToken = default);
    Task<MappingValidationResult> ValidateAsync(NativeArtifact artifact, CancellationToken cancellationToken = default);
}
