namespace Weft.Domain;

public sealed record ProjectionCacheKey : RevisionReportKey
{
    public ProjectionCacheKey(RevisionReportSource source, RevisionReportBackendProfile profile,
        string schemaVersion, string algorithmVersion, string codecVersion, RevisionReportPolicy policy)
        : base(RevisionReportOperation.Projection, source, profile, schemaVersion, algorithmVersion, codecVersion, policy)
    {
    }
}
