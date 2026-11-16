using Braid.Contracts.Projections;
using Braid.Domain;
using Braid.MappingBackends.Ferrule;

namespace Braid.MappingBackends;

public sealed partial class FerruleInspectionBackend
{
    public const string ProjectionVersion = "braid.ferrule-projection.v1";

    public async Task<MappingProjectionResult> ProjectAsync(NativeArtifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var invocation = await InvokeAsync(artifact, "project", cancellationToken).ConfigureAwait(false);
        if (invocation.Response?.Result is HostProjectionResult projection)
        {
            var scope = artifact.Scope;
            var provenance = new ProjectionProvenance(
                new ScopedRevisionId(scope.OrganizationId.ToString("D"), scope.ProjectId.ToString("D"), scope.MappingId.ToString("D"), scope.RevisionId.ToString("D")),
                new ArtifactDigest("sha256", artifact.Digest.Value), new BackendIdentity("ferrule", EngineRevision, _backendBuild),
                AdapterBuild, ProjectionVersion);
            var document = new ProjectionDocumentV1(provenance, projection.Coverage, projection.Boundaries, projection.Fields, projection.Parameters,
                projection.Rules, projection.Controls, projection.Relationships, projection.Diagnostics, projection.Capabilities);
            return new MappingProjectionResult(MappingOperationStatus.Succeeded, Provenance(artifact, true), document, []);
        }

        return new MappingProjectionResult(invocation.Status, Provenance(artifact, invocation.Confirmed), null, [invocation.Diagnostic!]);
    }
}
