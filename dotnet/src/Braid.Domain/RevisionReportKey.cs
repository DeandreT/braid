using System.Text.Json;

namespace Braid.Domain;

/// <summary>The complete report identity. Its fingerprint is only a lookup discriminator.</summary>
public abstract record RevisionReportKey
{
    private protected RevisionReportKey(RevisionReportOperation operation, RevisionReportSource source,
        RevisionReportBackendProfile profile, string schemaVersion, string algorithmVersion,
        string codecVersion, RevisionReportPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(policy);
        if (!Enum.IsDefined(operation))
            throw new ArgumentOutOfRangeException(nameof(operation));
        if (source.SourceBackendId != profile.BackendId)
            throw new ArgumentException("The selected backend must match the source backend identity.", nameof(profile));

        Operation = operation;
        Source = source;
        Profile = profile;
        SchemaVersion = RevisionReportValue.Text(schemaVersion, 128, nameof(schemaVersion));
        AlgorithmVersion = RevisionReportValue.Text(algorithmVersion, 128, nameof(algorithmVersion));
        CodecVersion = RevisionReportValue.Text(codecVersion, 128, nameof(codecVersion));
        Policy = policy;
    }

    public RevisionReportOperation Operation { get; }
    public RevisionReportSource Source { get; }
    public RevisionReportBackendProfile Profile { get; }
    public string SchemaVersion { get; }
    public string AlgorithmVersion { get; }
    public string CodecVersion { get; }
    public RevisionReportPolicy Policy { get; }
    public Sha256Digest Fingerprint => Sha256Digest.FromBytes(CopyCanonicalUtf8());
    public RevisionScope Scope => Source.Scope;
    public Sha256Digest NativeDigest => Source.NativeObject.Digest;
    public string BackendId => Profile.BackendId;
    public string BackendRevision => Profile.BackendRevision;
    public string BackendBuild => Profile.BackendBuild;
    public string AdapterBuild => Profile.AdapterBuild;

    public byte[] CopyCanonicalUtf8()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("schemaVersion", "braid.revision-report-key.v1");
            writer.WriteString("operation", Operation == RevisionReportOperation.Projection ? "projection" : "validation");
            writer.WriteStartObject("scope");
            writer.WriteString("organizationId", Scope.OrganizationId.ToString("D"));
            writer.WriteString("projectId", Scope.ProjectId.ToString("D"));
            writer.WriteString("mappingId", Scope.MappingId.ToString("D"));
            writer.WriteString("revisionId", Scope.RevisionId.ToString("D"));
            writer.WriteEndObject();
            writer.WriteStartObject("nativeObject");
            writer.WriteString("objectId", Source.NativeObject.Id.ObjectId.ToString("D"));
            writer.WriteString("class", "native");
            writer.WriteString("digest", NativeDigest.Value);
            writer.WriteNumber("byteCount", Source.NativeObject.ByteCount);
            writer.WriteEndObject();
            writer.WriteString("logicalDocument", Source.LogicalDocument);
            writer.WriteString("dependencyIdentity", Source.DependencyIdentity);
            writer.WriteString("sourceBackendId", Source.SourceBackendId);
            writer.WriteStartObject("profile");
            writer.WriteString("backendId", BackendId);
            writer.WriteString("backendRevision", BackendRevision);
            writer.WriteString("backendBuild", BackendBuild);
            writer.WriteString("adapterBuild", AdapterBuild);
            writer.WriteEndObject();
            writer.WriteString("reportSchemaVersion", SchemaVersion);
            writer.WriteString("algorithmVersion", AlgorithmVersion);
            writer.WriteString("codecVersion", CodecVersion);
            writer.WritePropertyName("policy");
            Policy.WriteCanonical(writer);
            writer.WriteString("policyFingerprint", Policy.Fingerprint.Value);
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }
}

public sealed record ValidationCacheKey : RevisionReportKey
{
    public ValidationCacheKey(RevisionReportSource source, RevisionReportBackendProfile profile,
        string schemaVersion, string algorithmVersion, string codecVersion, RevisionReportPolicy policy)
        : base(RevisionReportOperation.Validation, source, profile, schemaVersion, algorithmVersion, codecVersion, policy)
    {
    }
}
