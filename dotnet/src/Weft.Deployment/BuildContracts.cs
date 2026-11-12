using System.Text;
using System.Text.Json.Serialization;
using Weft.Application;
using Weft.Domain;

namespace Weft.Deployment;

public enum BuildContractFailure { InvalidInput, ResourceLimit, CapabilityUnavailable }

public sealed class BuildContractException : Exception
{
    public BuildContractException(BuildContractFailure failure) : base("Build contract refused.")
    {
        if (!Enum.IsDefined(failure)) throw new ArgumentOutOfRangeException(nameof(failure));
        Failure = failure;
    }

    public BuildContractFailure Failure { get; }
}

/// <summary>Exact engine and adapter identity, not a claim of runtime qualification.</summary>
public sealed record BuildBackendIdentity
{
    public BuildBackendIdentity(string backendId, string engineRevision, string engineBuild, string adapterVersion)
    {
        BackendId = BuildValues.Text(backendId, 128);
        EngineRevision = BuildValues.Text(engineRevision, 256);
        EngineBuild = BuildValues.Text(engineBuild, 256);
        AdapterVersion = BuildValues.Text(adapterVersion, 128);
    }

    public string BackendId { get; }
    public string EngineRevision { get; }
    public string EngineBuild { get; }
    public string AdapterVersion { get; }
    public override string ToString() => nameof(BuildBackendIdentity);
}

/// <summary>A server-owned runtime/artifact profile. Its size bound is metadata, not an allocation request.</summary>
public sealed record BuildRuntimeProfile
{
    public const long MaximumAllowedArtifactBytes = 1024L * 1024 * 1024;

    public BuildRuntimeProfile(string id, int version, string runtime, string runtimeVersion,
        string artifactFormat, long maximumArtifactBytes)
    {
        if (version < 1 || maximumArtifactBytes is < 1 or > MaximumAllowedArtifactBytes)
            throw new BuildContractException(BuildContractFailure.InvalidInput);
        Id = BuildValues.Text(id, 128);
        Version = version;
        Runtime = BuildValues.Text(runtime, 128);
        RuntimeVersion = BuildValues.Text(runtimeVersion, 128);
        ArtifactFormat = BuildValues.Text(artifactFormat, 128);
        MaximumArtifactBytes = maximumArtifactBytes;
    }

    public string Id { get; }
    public int Version { get; }
    public string Runtime { get; }
    public string RuntimeVersion { get; }
    public string ArtifactFormat { get; }
    public long MaximumArtifactBytes { get; }
    public override string ToString() => nameof(BuildRuntimeProfile);
}

public enum BuildBoundaryKind { PrimaryInput, NamedInput, PrimaryOutput, NamedOutput }

/// <summary>A physical-format requirement at an exact logical boundary. Names are not filesystem paths.</summary>
public sealed record BuildBoundary
{
    public BuildBoundary(BuildBoundaryKind kind, string? name, string format, CsvDocumentDialect? csvDialect = null)
    {
        if (!Enum.IsDefined(kind)) throw new BuildContractException(BuildContractFailure.InvalidInput);
        var named = kind is BuildBoundaryKind.NamedInput or BuildBoundaryKind.NamedOutput;
        if (named != (name is not null)) throw new BuildContractException(BuildContractFailure.InvalidInput);
        Kind = kind;
        Name = named ? BuildValues.Text(name!, 256) : null;
        Format = BuildValues.Text(format, 128);
        if (format is "csv" or "tsv" ? named || !CsvDocumentPolicy.Admits(csvDialect, BuildWorkerProtocol.MediaType(format)) : csvDialect is not null)
            throw new BuildContractException(BuildContractFailure.InvalidInput);
        CsvDialect = csvDialect;
    }

    public BuildBoundaryKind Kind { get; }
    public string? Name { get; }
    public string Format { get; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CsvDocumentDialect? CsvDialect { get; }
    public override string ToString() => nameof(BuildBoundary);
}

public sealed record BuildEnvironmentReference
{
    public BuildEnvironmentReference(ProjectScope project, Guid environmentId, long version)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (version < 1) throw new BuildContractException(BuildContractFailure.InvalidInput);
        Project = project;
        EnvironmentId = BuildValues.Id(environmentId);
        Version = version;
    }

    public ProjectScope Project { get; }
    public Guid EnvironmentId { get; }
    public long Version { get; }
    public override string ToString() => nameof(BuildEnvironmentReference);
}

internal static class BuildValues
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static Guid Id(Guid value) => value == Guid.Empty
        ? throw new BuildContractException(BuildContractFailure.InvalidInput) : value;

    internal static string Text(string value, int maximumBytes)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumBytes || value.Any(char.IsControl))
            throw new BuildContractException(BuildContractFailure.InvalidInput);
        try
        {
            if (Utf8.GetByteCount(value) > maximumBytes)
                throw new BuildContractException(BuildContractFailure.InvalidInput);
        }
        catch (EncoderFallbackException) { throw new BuildContractException(BuildContractFailure.InvalidInput); }
        return value;
    }

    internal static IReadOnlyList<BuildBoundary> Boundaries(IReadOnlyList<BuildBoundary> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var count = values.Count;
        if (count is < 1 or > 128) throw new BuildContractException(BuildContractFailure.ResourceLimit);
        var owned = new BuildBoundary[count];
        var identities = new HashSet<(BuildBoundaryKind, string?)>();
        for (var index = 0; index < count; index++)
        {
            var boundary = values[index];
            if (boundary is null || !identities.Add((boundary.Kind, boundary.Name)))
                throw new BuildContractException(BuildContractFailure.InvalidInput);
            owned[index] = boundary;
        }
        Array.Sort(owned, (left, right) => left.Kind != right.Kind ? left.Kind.CompareTo(right.Kind)
            : StringComparer.Ordinal.Compare(left.Name, right.Name));
        return Array.AsReadOnly(owned);
    }
}
