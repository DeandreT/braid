using System.Text.Json;

namespace Weft.Domain;

/// <summary>Project-owned evidence, independent of any mapping or revision. Not an authorization grant.</summary>
public sealed record SpecBundleScope
{
    public SpecBundleScope(ProjectScope project, Guid bundleId)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (bundleId == Guid.Empty) throw new ArgumentException("A bundle identifier is required.", nameof(bundleId));
        Project = project;
        BundleId = bundleId;
    }

    public ProjectScope Project { get; }
    public Guid BundleId { get; }
}

public sealed record SpecSourceIdentity
{
    public SpecSourceIdentity(SpecBundleScope bundle, Guid sourceId, long version)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        if (sourceId == Guid.Empty) throw new ArgumentException("A source identifier is required.", nameof(sourceId));
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        Bundle = bundle;
        SourceId = sourceId;
        Version = version;
    }

    public SpecBundleScope Bundle { get; }
    public Guid SourceId { get; }
    public long Version { get; }
}

public enum SpecSourceFormat { Unsupported = 0, Text = 1, Json = 2, OpenApiJson = 3 }
public enum SpecExtractionOutcome { Complete = 1, Unsupported = 2, Malformed = 3, LimitExceeded = 4 }

/// <summary>Owns a defensive copy of exact captured bytes, including any BOM and original line endings.</summary>
public sealed class SpecCapturedSource
{
    public const int MaximumBytes = 10 * 1024 * 1024;
    private readonly byte[] bytes;

    public SpecCapturedSource(SpecSourceIdentity identity, SpecSourceFormat format, ReadOnlySpan<byte> content)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!Enum.IsDefined(format)) throw new ArgumentOutOfRangeException(nameof(format));
        if (content.Length > MaximumBytes) throw new ArgumentOutOfRangeException(nameof(content));
        Identity = identity;
        Format = format;
        bytes = content.ToArray();
        Digest = Sha256Digest.FromBytes(bytes);
    }

    public SpecSourceIdentity Identity { get; }
    public SpecSourceFormat Format { get; }
    public Sha256Digest Digest { get; }
    public int ByteCount => bytes.Length;
    public byte[] CopyBytes() => (byte[])bytes.Clone();
    public override string ToString() => nameof(SpecCapturedSource);
}

/// <summary>Pins source bytes, adapter version, budgets, outcome and canonical extracted evidence.</summary>
public sealed record SpecExtractionIdentity
{
    public SpecExtractionIdentity(SpecSourceIdentity source, Sha256Digest sourceDigest, SpecSourceFormat format,
        int extractorVersion, Sha256Digest profileDigest, SpecExtractionOutcome outcome, Sha256Digest evidenceDigest)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sourceDigest);
        ArgumentNullException.ThrowIfNull(profileDigest);
        ArgumentNullException.ThrowIfNull(evidenceDigest);
        if (!Enum.IsDefined(format)) throw new ArgumentOutOfRangeException(nameof(format));
        if (!Enum.IsDefined(outcome)) throw new ArgumentOutOfRangeException(nameof(outcome));
        ArgumentOutOfRangeException.ThrowIfLessThan(extractorVersion, 1);
        Source = source;
        SourceDigest = sourceDigest;
        Format = format;
        ExtractorVersion = extractorVersion;
        ProfileDigest = profileDigest;
        Outcome = outcome;
        EvidenceDigest = evidenceDigest;
    }

    public const string ExtractorFamily = "weft.spec-extraction";
    public SpecSourceIdentity Source { get; }
    public Sha256Digest SourceDigest { get; }
    public SpecSourceFormat Format { get; }
    public int ExtractorVersion { get; }
    public Sha256Digest ProfileDigest { get; }
    public SpecExtractionOutcome Outcome { get; }
    public Sha256Digest EvidenceDigest { get; }
}

/// <summary>
/// Canonical set of selected extractions. Ordering is by source ID, not upload order; document precedence is not inferred.
/// Includes failed extractions so unsupported evidence cannot silently disappear from the snapshot.
/// </summary>
public sealed class SpecBundleSnapshot
{
    public const int MaximumSources = 20;

    public SpecBundleSnapshot(SpecBundleScope bundle, IEnumerable<SpecExtractionIdentity> extractions)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(extractions);
        var selected = new List<SpecExtractionIdentity>();
        var ids = new HashSet<Guid>();
        foreach (var extraction in extractions)
        {
            ArgumentNullException.ThrowIfNull(extraction);
            if (selected.Count == MaximumSources) throw new ArgumentException("The snapshot source limit was exceeded.", nameof(extractions));
            if (extraction.Source.Bundle != bundle || !ids.Add(extraction.Source.SourceId))
                throw new ArgumentException("Sources must be unique and belong to the exact bundle.", nameof(extractions));
            selected.Add(extraction);
        }
        if (selected.Count == 0) throw new ArgumentException("A snapshot requires a source.", nameof(extractions));
        selected.Sort((a, b) => string.CompareOrdinal(a.Source.SourceId.ToString("N"), b.Source.SourceId.ToString("N")));
        Bundle = bundle;
        Extractions = selected.AsReadOnly();
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            writer.WriteStringValue("weft.spec-snapshot.v1");
            writer.WriteStringValue(bundle.Project.OrganizationId.ToString("N"));
            writer.WriteStringValue(bundle.Project.ProjectId.ToString("N"));
            writer.WriteStringValue(bundle.BundleId.ToString("N"));
            foreach (var item in selected)
            {
                writer.WriteStartArray();
                writer.WriteStringValue(item.Source.SourceId.ToString("N"));
                writer.WriteNumberValue(item.Source.Version);
                writer.WriteStringValue(item.SourceDigest.Value);
                writer.WriteNumberValue((int)item.Format);
                writer.WriteStringValue(SpecExtractionIdentity.ExtractorFamily);
                writer.WriteNumberValue(item.ExtractorVersion);
                writer.WriteStringValue(item.ProfileDigest.Value);
                writer.WriteNumberValue((int)item.Outcome);
                writer.WriteStringValue(item.EvidenceDigest.Value);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
        }
        Digest = Sha256Digest.FromBytes(stream.ToArray());
    }

    public SpecBundleScope Bundle { get; }
    public IReadOnlyList<SpecExtractionIdentity> Extractions { get; }
    public Sha256Digest Digest { get; }
    public override string ToString() => nameof(SpecBundleSnapshot);
}
