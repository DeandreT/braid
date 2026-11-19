using System.Text.Json;
using Braid.Application;
using Braid.Domain;

namespace Braid.Deployment;

/// <summary>
/// Immutable build intent. Approval and regression identifiers bind provenance; they do not authorize work.
/// A later application workflow must reload and verify their exact revision/environment ownership.
/// </summary>
public sealed class BuildIntent
{
    public const string SchemaVersion = "braid.build-intent.v1";

    public BuildIntent(Guid buildId, RevisionScope revision, Sha256Digest nativeDigest, BuildBackendIdentity backend,
        BuildRuntimeProfile profile, IReadOnlyList<BuildBoundary> boundaries, BuildEnvironmentReference environment,
        Guid approvalId, Guid regressionEvidenceId)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(nativeDigest);
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(environment);
        if (environment.Project.OrganizationId != revision.OrganizationId || environment.Project.ProjectId != revision.ProjectId)
            throw new BuildContractException(BuildContractFailure.InvalidInput);
        BuildId = BuildValues.Id(buildId);
        Revision = revision;
        NativeDigest = nativeDigest;
        Backend = backend;
        Profile = profile;
        Boundaries = BuildValues.Boundaries(boundaries);
        if (profile.Id == BuildWorkerProtocol.CsvProfile ? !BuildWorkerProtocol.AdmitsCsvPlan(Boundaries) : Boundaries.Any(boundary => boundary.CsvDialect is not null))
            throw new BuildContractException(BuildContractFailure.InvalidInput);
        Environment = environment;
        if (profile.Id == BuildWorkerProtocol.XmlProfile)
        {
            if (Boundaries.Any(b => b.Format != "xml" || b.XmlMetadata is null)) throw new BuildContractException(BuildContractFailure.InvalidInput);
            XmlDocumentMetadataJson.MeasureOperation(Boundaries.Where(b => b.XmlRole == XmlDocumentRole.Input).Select(b => b.XmlMetadata!).ToArray(),
                Boundaries.Where(b => b.XmlRole == XmlDocumentRole.Output).Select(b => b.XmlMetadata!).ToArray());
        }
        else if (Boundaries.Any(b => b.XmlMetadata is not null)) throw new BuildContractException(BuildContractFailure.InvalidInput);
        ApprovalId = BuildValues.Id(approvalId);
        RegressionEvidenceId = BuildValues.Id(regressionEvidenceId);
        Fingerprint = Sha256Digest.FromBytes(CopyCanonicalBytes());
    }

    public Guid BuildId { get; }
    public RevisionScope Revision { get; }
    public Sha256Digest NativeDigest { get; }
    public BuildBackendIdentity Backend { get; }
    public BuildRuntimeProfile Profile { get; }
    public IReadOnlyList<BuildBoundary> Boundaries { get; }
    public BuildEnvironmentReference Environment { get; }
    public Guid ApprovalId { get; }
    public Guid RegressionEvidenceId { get; }
    public Sha256Digest Fingerprint { get; }

    public byte[] CopyCanonicalBytes()
    {
        using var bytes = new MemoryStream();
        using (var json = new Utf8JsonWriter(bytes))
        {
            json.WriteStartObject();
            json.WriteString("schema", SchemaVersion);
            json.WriteString("buildId", BuildId.ToString("D"));
            json.WriteString("organizationId", Revision.OrganizationId.ToString("D"));
            json.WriteString("projectId", Revision.ProjectId.ToString("D"));
            json.WriteString("mappingId", Revision.MappingId.ToString("D"));
            json.WriteString("revisionId", Revision.RevisionId.ToString("D"));
            json.WriteString("nativeDigest", NativeDigest.Value);
            json.WriteString("backendId", Backend.BackendId);
            json.WriteString("engineRevision", Backend.EngineRevision);
            json.WriteString("engineBuild", Backend.EngineBuild);
            json.WriteString("adapterVersion", Backend.AdapterVersion);
            json.WriteString("profileId", Profile.Id);
            json.WriteNumber("profileVersion", Profile.Version);
            json.WriteString("runtime", Profile.Runtime);
            json.WriteString("runtimeVersion", Profile.RuntimeVersion);
            json.WriteString("artifactFormat", Profile.ArtifactFormat);
            json.WriteNumber("maximumArtifactBytes", Profile.MaximumArtifactBytes);
            json.WriteString("environmentId", Environment.EnvironmentId.ToString("D"));
            json.WriteNumber("environmentVersion", Environment.Version);
            json.WriteString("approvalId", ApprovalId.ToString("D"));
            json.WriteString("regressionEvidenceId", RegressionEvidenceId.ToString("D"));
            json.WriteStartArray("boundaries");
            foreach (var boundary in Boundaries)
            {
                json.WriteStartObject();
                json.WriteString("kind", boundary.Kind switch
                {
                    BuildBoundaryKind.PrimaryInput => "primaryInput",
                    BuildBoundaryKind.NamedInput => "namedInput",
                    BuildBoundaryKind.PrimaryOutput => "primaryOutput",
                    BuildBoundaryKind.NamedOutput => "namedOutput",
                    _ => throw new BuildContractException(BuildContractFailure.InvalidInput)
                });
                json.WriteString("name", boundary.Name);
                json.WriteString("format", boundary.Format);
                if (boundary.XmlMetadata is { } metadata) { json.WritePropertyName("xmlMetadata"); XmlDocumentMetadataJson.Write(json, metadata, boundary.XmlRole); }
                if (boundary.CsvDialect is { } dialect)
                {
                    json.WritePropertyName("csvDialect");
                    BuildCsvDialect.Write(json, dialect);
                }
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        // All fields and collection counts were bounded before serialization.
        if (bytes.Length > 1024 * 1024) throw new BuildContractException(BuildContractFailure.ResourceLimit);
        return bytes.ToArray();
    }

    public override string ToString() => nameof(BuildIntent);
}
