using System.Text;

namespace Weft.Domain;

public enum RevisionReportOperation
{
    Projection = 1,
    Validation = 2
}

/// <summary>Exact committed native facts, not a permission grant or a storage path.</summary>
public sealed record RevisionReportSource
{
    public RevisionReportSource(RevisionScope scope, PrivateObjectReference nativeObject,
        string logicalDocument, string dependencyIdentity, string sourceBackendId)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(nativeObject);
        if (nativeObject.Id.Scope != scope || nativeObject.Id.Class != PrivateObjectClass.NativeArtifact)
            throw new ArgumentException("A report source must be a native object in the exact revision scope.", nameof(nativeObject));
        if (nativeObject.ByteCount is < 1 or > RevisionReportPolicy.MaximumNativeByteLimit)
            throw new ArgumentOutOfRangeException(nameof(nativeObject), "A report source exceeds the native byte bounds.");

        Scope = scope;
        NativeObject = nativeObject;
        LogicalDocument = RevisionReportValue.Text(logicalDocument, 256, nameof(logicalDocument));
        DependencyIdentity = RevisionReportValue.Text(dependencyIdentity, 256, nameof(dependencyIdentity));
        SourceBackendId = RevisionReportValue.Text(sourceBackendId, 128, nameof(sourceBackendId));
    }

    public RevisionScope Scope { get; }
    public PrivateObjectReference NativeObject { get; }
    public string LogicalDocument { get; }
    public string DependencyIdentity { get; }
    public string SourceBackendId { get; }
}

/// <summary>Trusted selected backend identity; it need not be the original admission build.</summary>
public sealed record RevisionReportBackendProfile
{
    public RevisionReportBackendProfile(string backendId, string backendRevision, string backendBuild, string adapterBuild)
    {
        BackendId = RevisionReportValue.Text(backendId, 128, nameof(backendId));
        BackendRevision = RevisionReportValue.Text(backendRevision, 256, nameof(backendRevision));
        BackendBuild = RevisionReportValue.Text(backendBuild, 2048, nameof(backendBuild));
        AdapterBuild = RevisionReportValue.Text(adapterBuild, 256, nameof(adapterBuild));
    }

    public string BackendId { get; }
    public string BackendRevision { get; }
    public string BackendBuild { get; }
    public string AdapterBuild { get; }
}

internal static class RevisionReportValue
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static string Text(string value, int maximumBytes, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Contains('\0'))
            throw new ArgumentException("Report identity text cannot contain NUL.", parameterName);
        try
        {
            if (StrictUtf8.GetByteCount(value) > maximumBytes)
                throw new ArgumentException("Report identity text exceeds its UTF8 byte bound.", parameterName);
        }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException("Report identity text must contain valid Unicode.", parameterName);
        }

        return value;
    }
}
