namespace Weft.Application;

/// <summary>Resolved saved metadata. Locations are lexical hints, never resources to open or validate against.</summary>
public sealed record XmlDocumentMetadata(string Schema, string SafetyProfile, XmlSchemaHints? SchemaHints)
{
    public static XmlDocumentMetadata WithoutHints { get; } = new(XmlDocumentPolicy.MetadataSchema,
        XmlDocumentPolicy.SafetyProfile, null);
}

public sealed record XmlSchemaLocation(string Namespace, string Location);

/// <summary>Ordered, immutable hints with value equality. Null and an empty hint object are not interchangeable.</summary>
public sealed record XmlSchemaHints
{
    public XmlSchemaHints(string? noNamespaceLocation, IReadOnlyList<XmlSchemaLocation> locations)
    {
        ArgumentNullException.ThrowIfNull(locations);
        if (locations.Count > XmlDocumentPolicy.MaximumSchemaHintPairs)
            throw new XmlDocumentException(XmlDocumentFailure.InvalidMetadata);
        NoNamespaceLocation = noNamespaceLocation;
        Locations = Array.AsReadOnly(locations.ToArray());
    }

    public string? NoNamespaceLocation { get; }
    public IReadOnlyList<XmlSchemaLocation> Locations { get; }

    public bool Equals(XmlSchemaHints? other) => other is not null &&
        NoNamespaceLocation == other.NoNamespaceLocation && Locations.SequenceEqual(other.Locations);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(NoNamespaceLocation, StringComparer.Ordinal);
        foreach (var pair in Locations) hash.Add(pair);
        return hash.ToHashCode();
    }
}
