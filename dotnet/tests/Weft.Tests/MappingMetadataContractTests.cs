using System.Text.Json;
using Weft.Application;
using Weft.Contracts.Catalog;
using Weft.Contracts.Projections;
using Weft.Domain;

namespace Weft.Tests;

/// <summary>Contract checks only; actual writer authority, concurrency and HTTP parsing have separate qualification.</summary>
public sealed class MappingMetadataContractTests
{
    private const string Organization = "00112233-4455-6677-8899-aabbccddeeff";
    private const string Project = "10213243-5465-7687-98a9-bacbdcedfe0f";
    private const string Mapping = "20314253-6475-8697-a8b9-cadbecfd0e1f";
    private static Guid Other => Guid.Parse("40516273-8495-a6b7-c8d9-eafb0c1d2e3f");
    private static MappingScope Scope => new(Guid.Parse(Organization), Guid.Parse(Project), Guid.Parse(Mapping));
    private static JsonSerializerOptions Options => ProjectionJson.CreateOptions();

    [Fact]
    public void ExpectedMetadataVersionKeepsEveryAncestorAndExactBigintPrecision()
    {
        var version = new MappingMetadataVersion(Scope, long.MaxValue);
        Assert.Equal(Scope, version.Scope);
        Assert.Equal(long.MaxValue, version.Version);
        Assert.Equal(version, new MappingMetadataVersion(Scope, long.MaxValue));
        Assert.NotEqual(version, new MappingMetadataVersion(Scope, long.MaxValue - 1));
        foreach (var foreignScope in new[]
        {
            new MappingScope(Other, Scope.ProjectId, Scope.MappingId),
            new MappingScope(Scope.OrganizationId, Other, Scope.MappingId),
            new MappingScope(Scope.OrganizationId, Scope.ProjectId, Other)
        })
        {
            // A foreign but valid expected scope reaches the authorized writer for comparison.
            var foreign = new MappingMetadataVersion(foreignScope, long.MaxValue);
            Assert.Equal(foreignScope, foreign.Scope);
            Assert.NotEqual(version, foreign);
        }
        Assert.Throws<ArgumentNullException>(() => new MappingMetadataVersion(null!, 1));
        foreach (var invalid in new[] { long.MinValue, -1L, 0L })
            Assert.Throws<ArgumentOutOfRangeException>(() => new MappingMetadataVersion(Scope, invalid));
    }

    [Fact]
    public void EditUsesAcceptedCreationNormalizationAndPostgresUnicodeScalarBounds()
    {
        var name = string.Concat(Enumerable.Repeat("😀", 200));
        var description = string.Concat(Enumerable.Repeat("😀", 4000));
        var paddedName = new string(' ', 5000) + name + "\r\n\t";
        var edit = new MappingMetadataEdit(paddedName, description);
        var acceptedAtCreation = new IngestionCreateMetadata(paddedName, description);
        var document = new CatalogMappingMetadataEditV1(paddedName, description);

        Assert.Equal(name, edit.Name);
        Assert.Equal(description, edit.Description);
        Assert.Equal(acceptedAtCreation.Name, edit.Name);
        Assert.Equal(acceptedAtCreation.Description, edit.Description);
        Assert.Equal(paddedName, document.Name);
        Assert.Equal(description, document.Description);
        Assert.Equal(edit, new MappingMetadataEdit(name, description));
        Assert.Equal("\r\n exact \t", new MappingMetadataEdit("Name", "\r\n exact \t").Description);
        Assert.Equal(string.Empty, new MappingMetadataEdit("Name", string.Empty).Description);
        Assert.ThrowsAny<ArgumentException>(() => new MappingMetadataEdit(name + "😀", description));
        Assert.ThrowsAny<ArgumentException>(() => new MappingMetadataEdit(name, description + "😀"));
    }

    [Fact]
    public void MetadataInputsRejectBlankNamesNullsNulAndRuntimeMalformedUnicode()
    {
        foreach (var invalidName in new[] { "", " \r\n\t", "name\0", new string((char)0xd800, 1), new string((char)0xdfff, 1) })
        {
            Assert.ThrowsAny<ArgumentException>(() => new MappingMetadataEdit(invalidName, string.Empty));
            Assert.ThrowsAny<ArgumentException>(() => new CatalogMappingMetadataEditV1(invalidName, string.Empty));
        }
        foreach (var invalidDescription in new[] { "description\0", new string((char)0xd800, 1), new string((char)0xdfff, 1) })
        {
            Assert.ThrowsAny<ArgumentException>(() => new MappingMetadataEdit("Name", invalidDescription));
            Assert.ThrowsAny<ArgumentException>(() => new CatalogMappingMetadataEditV1("Name", invalidDescription));
        }
        Assert.Throws<ArgumentNullException>(() => new MappingMetadataEdit(null!, string.Empty));
        Assert.Throws<ArgumentNullException>(() => new MappingMetadataEdit("Name", null!));
        Assert.Throws<ArgumentNullException>(() => new CatalogMappingMetadataEditV1(null!, string.Empty));
        Assert.Throws<ArgumentNullException>(() => new CatalogMappingMetadataEditV1("Name", null!));
    }

    [Fact]
    public void FailedWriteResultsCannotExposeCurrentMetadataOrPretendSuccess()
    {
        var metadata = new CatalogMappingMetadata(Scope, "Name", " exact ", long.MaxValue);
        foreach (var outcome in new[] { MappingMetadataWriteOutcome.Updated, MappingMetadataWriteOutcome.Unchanged })
        {
            var result = MappingMetadataWriteResult.ForMetadata(outcome, metadata);
            Assert.Equal(outcome, result.Outcome);
            Assert.Same(metadata, result.Metadata);
            Assert.Equal(long.MaxValue, result.Metadata!.Version);
            Assert.Throws<ArgumentNullException>(() => MappingMetadataWriteResult.ForMetadata(outcome, null!));
            Assert.Throws<ArgumentOutOfRangeException>(() => MappingMetadataWriteResult.WithoutMetadata(outcome));
        }
        foreach (var outcome in new[]
        {
            MappingMetadataWriteOutcome.PreconditionFailed,
            MappingMetadataWriteOutcome.Unavailable,
            MappingMetadataWriteOutcome.VersionExhausted
        })
        {
            var result = MappingMetadataWriteResult.WithoutMetadata(outcome);
            Assert.Equal(outcome, result.Outcome);
            Assert.Null(result.Metadata);
            Assert.Throws<ArgumentOutOfRangeException>(() => MappingMetadataWriteResult.ForMetadata(outcome, metadata));
        }
        foreach (var unknown in new[] { -1, 0, 6, 999 })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => MappingMetadataWriteResult.ForMetadata((MappingMetadataWriteOutcome)unknown, metadata));
            Assert.Throws<ArgumentOutOfRangeException>(() => MappingMetadataWriteResult.WithoutMetadata((MappingMetadataWriteOutcome)unknown));
        }
    }

    [Fact]
    public void EditDocumentPreservesRawNameAndDescriptionUntilApplicationAcceptance()
    {
        var acceptedName = string.Concat(Enumerable.Repeat("😀", 200));
        var rawName = new string(' ', 5000) + acceptedName + "\r\n";
        var description = "  exact description\r\n";
        var request = new CatalogMappingMetadataEditV1(rawName, description);
        var encoded = JsonSerializer.Serialize(request, Options);
        using var json = JsonDocument.Parse(encoded);

        Assert.Equal(new[] { "description", "name" }, json.RootElement.EnumerateObject()
            .Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(rawName, json.RootElement.GetProperty("name").GetString());
        Assert.Equal(description, json.RootElement.GetProperty("description").GetString());
        var restored = JsonSerializer.Deserialize<CatalogMappingMetadataEditV1>(encoded, Options)!;
        Assert.Equal(request, restored);
        var accepted = new MappingMetadataEdit(restored.Name, restored.Description);
        Assert.Equal(acceptedName, accepted.Name);
        Assert.Equal(description, accepted.Description);
        Assert.ThrowsAny<ArgumentException>(() => new CatalogMappingMetadataEditV1(acceptedName + "😀", description));
        Assert.ThrowsAny<ArgumentException>(() => new CatalogMappingMetadataEditV1("Name", new string('x', 4001)));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"name\":\"Name\"}")]
    [InlineData("{\"description\":\"Description\"}")]
    [InlineData("{\"name\":null,\"description\":\"Description\"}")]
    [InlineData("{\"name\":\"Name\",\"description\":null}")]
    [InlineData("{\"name\":\"Name\",\"description\":\"Description\",\"version\":\"1\"}")]
    public void EditWireShapeRequiresTwoNonnullableStringsAndRejectsUnmappedFields(string json) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CatalogMappingMetadataEditV1>(json, Options));

    [Theory]
    [InlineData(CatalogErrorCodeV1.PreconditionRequired, 7, "preconditionRequired")]
    [InlineData(CatalogErrorCodeV1.PreconditionFailed, 8, "preconditionFailed")]
    [InlineData(CatalogErrorCodeV1.UnsupportedMediaType, 9, "unsupportedMediaType")]
    [InlineData(CatalogErrorCodeV1.BodyLimit, 10, "bodyLimit")]
    [InlineData(CatalogErrorCodeV1.VersionExhausted, 11, "versionExhausted")]
    public void MetadataErrorsRemainClosedCategoricalStrings(CatalogErrorCodeV1 code, int value, string text)
    {
        Assert.Equal(value, (int)code);
        var error = new CatalogErrorV1(code);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(error, Options));
        Assert.Equal("code", Assert.Single(json.RootElement.EnumerateObject()).Name);
        Assert.Equal(text, json.RootElement.GetProperty("code").GetString());
        Assert.Equal(error, JsonSerializer.Deserialize<CatalogErrorV1>(JsonSerializer.Serialize(error, Options), Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CatalogErrorCodeV1>(value.ToString(System.Globalization.CultureInfo.InvariantCulture), Options));
    }

    [Fact]
    public void AppendedErrorsPreserveLegacyValuesAndDenyUnknownCategories()
    {
        Assert.Equal(1, (int)CatalogErrorCodeV1.Unavailable);
        Assert.Equal(2, (int)CatalogErrorCodeV1.InvalidRequest);
        Assert.Equal(3, (int)CatalogErrorCodeV1.NotFound);
        Assert.Equal(4, (int)CatalogErrorCodeV1.IdentityRejected);
        Assert.Equal(5, (int)CatalogErrorCodeV1.PersistenceFailure);
        Assert.Equal(6, (int)CatalogErrorCodeV1.InternalFailure);
        Assert.Throws<ArgumentOutOfRangeException>(() => new CatalogErrorV1((CatalogErrorCodeV1)12));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CatalogErrorCodeV1>("\"unknownCategory\"", Options));
    }
}
