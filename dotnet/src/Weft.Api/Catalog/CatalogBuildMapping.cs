using System.Globalization;
using System.Text.Json;
using Weft.Contracts.Builds;
using Weft.Deployment;
using Weft.Domain;

namespace Weft.Api.Catalog;

internal static class CatalogBuildMapping
{
    internal static BuildBackendV1 Backend(BuildBackendIdentity value) => new(value.BackendId, value.EngineRevision, value.EngineBuild, value.AdapterVersion);
    internal static BuildRuntimeV1 Profile(BuildRuntimeProfile value) => new(value.Id, Number(value.Version), value.Runtime,
        value.RuntimeVersion, value.ArtifactFormat, value.MaximumArtifactBytes);
    internal static BuildEnvironmentReferenceV1 Reference(BuildEnvironmentReference value) => new(value.Project.OrganizationId.ToString("D"),
        value.Project.ProjectId.ToString("D"), value.EnvironmentId.ToString("D"), Number(value.Version));
    internal static BuildEnvironmentV1 Environment(RetainedBuildEnvironment value) => new(Reference(value.Environment.Reference), Profile(value.Environment.Profile),
        value.Environment.Active, value.Creator.Value.ToString("D"), value.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
    internal static BuildApprovalV1 Approval(RetainedBuildApproval value)
    {
        var intent = value.Intent;
        return new(intent.ApprovalId.ToString("D"), intent.BuildId.ToString("D"), Scope(intent.Revision), intent.NativeDigest.Value,
            Backend(intent.Backend), Profile(intent.Profile), intent.Boundaries.Select(boundary => new BuildBoundaryV1(Name(boundary.Kind), boundary.Name, boundary.Format)).ToArray(),
            Reference(intent.Environment), intent.RegressionEvidenceId.ToString("D"), value.Revoked, value.EnvironmentActive,
            value.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
    }
    internal static BuildRunV1 Run(BuildRunSnapshot value) => Run(new BuildRunSummary(value.Actor, value.Intent, value.State, value.Attempt, value.CreatedAt, value.Outcome, value.Artifact));
    internal static BuildRunV1 Run(BuildRunSummary value) => new(value.Intent.BuildId.ToString("D"), value.Intent.ApprovalId.ToString("D"), Scope(value.Intent.Revision),
        Name(value.State), value.Attempt, value.CreatedAt.ToString("O", CultureInfo.InvariantCulture), value.Outcome is { } outcome ? Name(outcome) : null,
        value.Artifact is { } artifact ? new(artifact.ContentDigest.Value, artifact.ByteCount, artifact.Intent.Profile.ArtifactFormat) : null);
    private static Weft.Contracts.Catalog.CatalogRevisionScopeV1 Scope(RevisionScope value) => new(value.OrganizationId.ToString("D"), value.ProjectId.ToString("D"), value.MappingId.ToString("D"), value.RevisionId.ToString("D"));
    internal static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Name<T>(T value) where T : Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
}
