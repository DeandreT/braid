namespace Weft.Domain;

public sealed record ProjectionCacheKey
{
    public ProjectionCacheKey(RevisionScope scope, Sha256Digest nativeDigest, string backendId,
        string backendRevision, string backendBuild, string adapterBuild, string schemaVersion)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(nativeDigest);
        ArgumentException.ThrowIfNullOrWhiteSpace(backendId);
        ArgumentException.ThrowIfNullOrWhiteSpace(backendRevision);
        ArgumentException.ThrowIfNullOrWhiteSpace(backendBuild);
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterBuild);
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaVersion);
        Scope = scope;
        NativeDigest = nativeDigest;
        BackendId = backendId;
        BackendRevision = backendRevision;
        BackendBuild = backendBuild;
        AdapterBuild = adapterBuild;
        SchemaVersion = schemaVersion;
    }

    public RevisionScope Scope { get; }
    public Sha256Digest NativeDigest { get; }
    public string BackendId { get; }
    public string BackendRevision { get; }
    public string BackendBuild { get; }
    public string AdapterBuild { get; }
    public string SchemaVersion { get; }
}
