using Weft.Domain;

namespace Weft.Application;

/// <summary>A conditional metadata version bound to the complete mapping scope.</summary>
public sealed record MappingMetadataVersion
{
    public MappingMetadataVersion(MappingScope scope, long version)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        Scope = scope;
        Version = version;
    }

    public MappingScope Scope { get; }
    public long Version { get; }
}

/// <summary>Accepted metadata, using the same normalization and Unicode limits as revision creation.</summary>
public sealed record MappingMetadataEdit
{
    public MappingMetadataEdit(string name, string description)
    {
        var accepted = new IngestionCreateMetadata(name, description);
        Name = accepted.Name;
        Description = accepted.Description;
    }

    public string Name { get; }
    public string Description { get; }
}

public enum MappingMetadataWriteOutcome
{
    Updated = 1,
    Unchanged = 2,
    PreconditionFailed = 3,
    Unavailable = 4,
    VersionExhausted = 5
}

/// <summary>Only a successful authorized observation carries exact metadata.</summary>
public sealed record MappingMetadataWriteResult
{
    private MappingMetadataWriteResult(MappingMetadataWriteOutcome outcome, CatalogMappingMetadata? metadata)
    {
        Outcome = outcome;
        Metadata = metadata;
    }

    public MappingMetadataWriteOutcome Outcome { get; }
    public CatalogMappingMetadata? Metadata { get; }

    public static MappingMetadataWriteResult ForMetadata(MappingMetadataWriteOutcome outcome,
        CatalogMappingMetadata metadata)
    {
        if (outcome is not (MappingMetadataWriteOutcome.Updated or MappingMetadataWriteOutcome.Unchanged))
            throw new ArgumentOutOfRangeException(nameof(outcome), "Only a successful metadata outcome carries metadata.");
        ArgumentNullException.ThrowIfNull(metadata);
        return new(outcome, metadata);
    }

    public static MappingMetadataWriteResult WithoutMetadata(MappingMetadataWriteOutcome outcome)
    {
        if (outcome is not (MappingMetadataWriteOutcome.PreconditionFailed or MappingMetadataWriteOutcome.Unavailable or
            MappingMetadataWriteOutcome.VersionExhausted))
            throw new ArgumentOutOfRangeException(nameof(outcome), "A failed metadata outcome cannot carry metadata.");
        return new(outcome, null);
    }
}

/// <summary>Replaces mapping metadata after one fresh scoped Publisher or Administrator observation.</summary>
public interface IMappingMetadataWriter
{
    /// <summary>
    /// A denied or missing target returns Unavailable before comparing the expected version.
    /// A foreign expected scope or stale version returns PreconditionFailed, including for a no-op.
    /// Matching accepted fields return Unchanged even at the maximum version; an actual change at
    /// that version returns VersionExhausted. Otherwise the version advances once and returns Updated.
    /// Only success returns metadata. Persistence failures remain exceptions and cancellation propagates.
    /// </summary>
    Task<MappingMetadataWriteResult> ReplaceAsync(ActorContext actor, MappingScope scope,
        MappingMetadataVersion expectedVersion, MappingMetadataEdit edit, CancellationToken cancellationToken = default);
}
