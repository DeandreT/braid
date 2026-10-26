using System.Text.Json;
using System.Text.Json.Serialization;

namespace Weft.Domain;

/// <summary>Immutable original package and the exact review the actor confirmed.</summary>
public sealed class InterchangeImportProvenance : IEquatable<InterchangeImportProvenance>
{
    public const int MaximumEncodedBytes = 24 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };
    private readonly byte[] _encoded;

    public InterchangeImportProvenance(InterchangePackage package, Sha256Digest nativeDigest,
        Sha256Digest reviewDigest, Sha256Digest reportDigest, IngestionBackendProfile engine)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(nativeDigest);
        ArgumentNullException.ThrowIfNull(reviewDigest);
        ArgumentNullException.ThrowIfNull(reportDigest);
        ArgumentNullException.ThrowIfNull(engine);
        Package = package;
        NativeDigest = nativeDigest;
        ReviewDigest = reviewDigest;
        ReportDigest = reportDigest;
        Engine = engine;
        _encoded = JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, package.Encode(),
            nativeDigest.Value, reviewDigest.Value, reportDigest.Value, engine.BackendId,
            engine.EngineRevision, engine.BackendBuild, engine.AdapterBuild), Options);
        if (_encoded.Length > MaximumEncodedBytes)
            throw new ArgumentException("Import provenance exceeds its encoded limit.");
        Digest = Sha256Digest.FromBytes(_encoded);
    }

    public InterchangePackage Package { get; }
    public Sha256Digest NativeDigest { get; }
    public Sha256Digest ReviewDigest { get; }
    public Sha256Digest ReportDigest { get; }
    public IngestionBackendProfile Engine { get; }
    public Sha256Digest Digest { get; }
    public byte[] Encode() => (byte[])_encoded.Clone();

    public static InterchangeImportProvenance Decode(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length is < 1 or > MaximumEncodedBytes)
            throw new ArgumentException("Import provenance exceeds its encoded limit.", nameof(encoded));
        var value = JsonSerializer.Deserialize<Envelope>(encoded, Options)
            ?? throw new ArgumentException("Missing import provenance.", nameof(encoded));
        if (value.Version != 1 || value.Package is null)
            throw new ArgumentException("Unknown import provenance version.", nameof(encoded));
        var result = new InterchangeImportProvenance(InterchangePackage.Decode(value.Package),
            Sha256Digest.Parse(value.NativeDigest), Sha256Digest.Parse(value.ReviewDigest),
            Sha256Digest.Parse(value.ReportDigest), new IngestionBackendProfile(value.BackendId,
                value.EngineRevision, value.BackendBuild, value.AdapterBuild));
        if (!encoded.SequenceEqual(result._encoded))
            throw new ArgumentException("Import provenance must use canonical encoding.", nameof(encoded));
        return result;
    }

    public bool Equals(InterchangeImportProvenance? other) => other is not null && Digest == other.Digest;
    public override bool Equals(object? obj) => obj is InterchangeImportProvenance other && Equals(other);
    public override int GetHashCode() => Digest.GetHashCode();

    private sealed record Envelope(int Version, byte[] Package, string NativeDigest, string ReviewDigest,
        string ReportDigest, string BackendId, string EngineRevision, string BackendBuild, string AdapterBuild);
}
