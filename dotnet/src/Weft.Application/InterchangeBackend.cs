using System.Text;
using System.Text.Json;
using Weft.Domain;

namespace Weft.Application;

/// <summary>Byte-only conversion port. Implementations must not resolve names outside the package.</summary>
public interface IInterchangeBackend
{
    IngestionBackendProfile Profile { get; }
    Task<InterchangeImportOutput> ImportAsync(InterchangePackage package, CancellationToken cancellationToken);
    Task<InterchangeExportOutput> ExportAsync(NativeArtifact native, string designPath,
        CancellationToken cancellationToken);
}

public interface IInterchangeBackendRegistry
{
    IInterchangeBackend? GetCurrent(string backendId);
}

public sealed record InterchangeWarning
{
    public InterchangeWarning(string code, string message)
    {
        Code = BoundedText(code, 128);
        Message = BoundedText(message, 2048);
    }
    public string Code { get; }
    public string Message { get; }
    internal static string BoundedText(string text, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (text.Contains('\0') || new UTF8Encoding(false, true).GetByteCount(text) > limit)
            throw new ArgumentException("Interchange text exceeds its bound.", nameof(text));
        return text;
    }
}

/// <summary>A complete bounded report: truncation is not acknowledgement of omitted warnings.</summary>
public sealed class InterchangeReport
{
    public const int MaximumWarnings = 256;
    public const int MaximumSummaryBytes = 8192;
    public InterchangeReport(string summary, IEnumerable<InterchangeWarning> warnings)
    {
        Summary = InterchangeWarning.BoundedText(summary, MaximumSummaryBytes);
        ArgumentNullException.ThrowIfNull(warnings);
        var owned = new List<InterchangeWarning>();
        foreach (var warning in warnings)
        {
            ArgumentNullException.ThrowIfNull(warning);
            if (owned.Count == MaximumWarnings)
                throw new ArgumentException("Interchange warning limit exceeded; conversion cannot be reviewed.", nameof(warnings));
            owned.Add(warning);
        }
        Warnings = owned.AsReadOnly();
        Digest = Sha256Digest.FromBytes(JsonSerializer.SerializeToUtf8Bytes(new { Summary, Warnings }));
    }
    public string Summary { get; }
    public IReadOnlyList<InterchangeWarning> Warnings { get; }
    public Sha256Digest Digest { get; }
}

public sealed class InterchangeImportOutput
{
    private readonly byte[] _native;
    public InterchangeImportOutput(IngestionBackendProfile profile, Sha256Digest packageDigest,
        ReadOnlySpan<byte> native, InterchangeReport report)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(packageDigest);
        ArgumentNullException.ThrowIfNull(report);
        if (native.Length is < 1 or > (int)IngestionAdmissionPolicy.MaximumNativeByteCount)
            throw new ArgumentException("Converted native output exceeds its bound.", nameof(native));
        Profile = profile;
        PackageDigest = packageDigest;
        _native = native.ToArray();
        NativeDigest = Sha256Digest.FromBytes(_native);
        Report = report;
    }
    public IngestionBackendProfile Profile { get; }
    public Sha256Digest PackageDigest { get; }
    public Sha256Digest NativeDigest { get; }
    public ReadOnlySpan<byte> Native => _native;
    public InterchangeReport Report { get; }
}

public sealed record InterchangeExportOutput(IngestionBackendProfile Profile, Sha256Digest NativeDigest,
    InterchangePackage Package, InterchangeReport Report);
