using System.Text;
using System.Text.Json;
using Weft.Contracts.Catalog;
using Weft.Contracts.Projections;

namespace Weft.Tests;

/// <summary>Synthetic public wire checks only; these do not qualify database authority or HTTP identity.</summary>
public sealed class CatalogContractTests
{
    private const string Organization = "00112233-4455-6677-8899-aabbccddeeff";
    private const string Project = "10213243-5465-7687-98a9-bacbdcedfe0f";
    private const string Mapping = "20314253-6475-8697-a8b9-cadbecfd0e1f";
    private const string Revision = "30415263-7485-96a7-b8c9-daebfc0d1e2f";
    private const string Principal = "ffeeddcc-bbaa-9988-7766-554433221100";
    private const string Other = "40516273-8495-a6b7-c8d9-eafb0c1d2e3f";
    private const string Largest = "9223372036854775807";
    private static DateTimeOffset Timestamp => new(2026, 10, 13, 10, 0, 0, TimeSpan.FromHours(-7));
    private static JsonSerializerOptions Options => ProjectionJson.CreateOptions();
    private static CatalogProjectScopeV1 ProjectScope => new(Organization, Project);
    private static CatalogMappingScopeV1 MappingScope => new(Organization, Project, Mapping);
    private static CatalogRevisionScopeV1 RevisionScope => new(Organization, Project, Mapping, Revision);

    [Fact]
    public void BigintMetadataAndHistoryStayExactDecimalStringsThroughThePublicRoundtrip()
    {
        var mapping = MappingRecord(Largest, "9007199254740993");
        var history = RevisionRecord(Largest);
        using var mappingJson = JsonDocument.Parse(JsonSerializer.Serialize(mapping, Options));
        Assert.Equal(JsonValueKind.String, mappingJson.RootElement.GetProperty("version").ValueKind);
        Assert.Equal(Largest, mappingJson.RootElement.GetProperty("version").GetString());
        Assert.Equal("9007199254740993", mappingJson.RootElement.GetProperty("head").GetProperty("sequence").GetString());
        using var historyJson = JsonDocument.Parse(JsonSerializer.Serialize(history, Options));
        Assert.Equal(JsonValueKind.String, historyJson.RootElement.GetProperty("sequence").ValueKind);
        Assert.Equal(Largest, historyJson.RootElement.GetProperty("sequence").GetString());
        Assert.Equal(4194304, historyJson.RootElement.GetProperty("nativeByteCount").GetInt32());
        Assert.Equal("decodedInvalid", historyJson.RootElement.GetProperty("admission").GetString());

        var cursor = Encode("{\"v\":1}");
        var page = new CatalogMappingPageV1([mapping], cursor);
        var restored = JsonSerializer.Deserialize<CatalogMappingPageV1>(JsonSerializer.Serialize(page, Options), Options)!;
        Assert.Equal(mapping, Assert.Single(restored.Mappings));
        Assert.Equal(cursor, restored.NextCursor);
        Assert.Equal(history, JsonSerializer.Deserialize<CatalogRevisionV1>(JsonSerializer.Serialize(history, Options), Options));
    }

    [Fact]
    public void CanonicalIdentifierAndDecimalGuardsPreventAmbiguousOrRoundedWireFacts()
    {
        foreach (var invalid in new[] { "", Guid.Empty.ToString("D"), Organization.ToUpperInvariant(), " " + Organization, "{" + Organization + "}" })
        {
            Rejects(() => new CatalogProjectScopeV1(invalid, Project));
            Rejects(() => new CatalogMappingScopeV1(Organization, invalid, Mapping));
            Rejects(() => new CatalogRevisionScopeV1(Organization, Project, Mapping, invalid));
            Rejects(() => new CatalogProjectV1(ProjectScope, "Name", "", invalid, Timestamp, Timestamp, "1", Access()));
        }
        foreach (var invalid in new[] { "", "0", "01", "-1", "+1", "1.0", "1e2", " 1", "١", "9223372036854775808" })
        {
            Rejects(() => MappingRecord(version: invalid));
            Rejects(() => new CatalogHeadV1(RevisionScope, invalid, CatalogAdmissionV1.Valid));
            Rejects(() => new CatalogMappingMetadataV1(MappingScope, "Name", "", invalid));
            Rejects(() => RevisionRecord(sequence: invalid));
        }
        Assert.Equal(RevisionScope, new CatalogRevisionScopeV1(Organization, Project, Mapping, Revision));
        Assert.NotEqual(RevisionScope, new CatalogRevisionScopeV1(Organization, Project, Other, Revision));
        Rejects(() => MappingRecord(head: new CatalogHeadV1(new CatalogRevisionScopeV1(Other, Project, Mapping, Revision), "1", CatalogAdmissionV1.Valid)));
        Rejects(() => MappingRecord(head: new CatalogHeadV1(new CatalogRevisionScopeV1(Organization, Other, Mapping, Revision), "1", CatalogAdmissionV1.Valid)));
        Rejects(() => MappingRecord(head: new CatalogHeadV1(new CatalogRevisionScopeV1(Organization, Project, Other, Revision), "1", CatalogAdmissionV1.Valid)));
    }

    [Theory]
    [InlineData(CatalogRoleV1.Viewer, "viewer")]
    [InlineData(CatalogRoleV1.Runner, "runner")]
    [InlineData(CatalogRoleV1.Publisher, "publisher")]
    [InlineData(CatalogRoleV1.Administrator, "administrator")]
    public void ReadOnlyAvailabilityDoesNotAdvertiseUnimplementedActionsForAnyRole(CatalogRoleV1 role, string label)
    {
        var access = CatalogAccessV1.ReadOnly(role);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(access, Options));
        Assert.Equal(label, json.RootElement.GetProperty("role").GetString());
        var actions = json.RootElement.GetProperty("actions");
        Assert.True(actions.GetProperty("view").GetBoolean());
        Assert.All(actions.EnumerateObject().Where(property => property.Name != "view"), property => Assert.False(property.Value.GetBoolean()));
        using var features = JsonDocument.Parse(JsonSerializer.Serialize(CatalogFeaturesV1.ReadOnly, Options));
        Assert.True(features.RootElement.GetProperty("readCatalog").GetBoolean());
        Assert.All(features.RootElement.EnumerateObject().Where(property => property.Name != "readCatalog"), property => Assert.False(property.Value.GetBoolean()));
    }

    [Fact]
    public void Csv_preview_feature_is_optional_and_preserves_legacy_constructor_invariants()
    {
        var legacy = new CatalogFeaturesV1(true, false, false, false, false, false, false, true, false, false);
        var disabled = new CatalogFeaturesV1(true, false, false, false, false, false, false, true, false, false, csvPreview: false);
        var missing = new CatalogFeaturesV1(true, false, false, false, false, false, false, true, false, false, csvPreview: null);
        var text = JsonSerializer.Serialize(legacy, Options);
        Assert.Equal("{\"readCatalog\":true,\"createProjects\":false,\"createMappings\":false,\"editMetadata\":false,\"uploadRevisions\":false,\"readReports\":false,\"chat\":false,\"preview\":true,\"manageMembership\":false,\"deriveReports\":false,\"edit\":false}", text);
        Assert.Equal(text, JsonSerializer.Serialize(disabled, Options));
        Assert.Equal(text, JsonSerializer.Serialize(missing, Options));
        Assert.DoesNotContain("csvPreview", text, StringComparison.Ordinal);
        Assert.Null(JsonSerializer.Deserialize<CatalogFeaturesV1>(text, Options)!.CsvPreview);
        var enabled = new CatalogFeaturesV1(true, false, false, false, false, false, false, true, false, false, csvPreview: true);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(enabled, Options));
        Assert.Equal(JsonValueKind.True, json.RootElement.GetProperty("csvPreview").ValueKind);
        Assert.True(JsonSerializer.Deserialize<CatalogFeaturesV1>(json.RootElement.GetRawText(), Options)!.CsvPreview);
        Rejects(() => new CatalogFeaturesV1(true, false, false, false, false, false, false, false, false, false, csvPreview: true));
        Rejects(() => new CatalogFeaturesV1(true, false, false, false, false, false, false, true, false, true, csvPreview: true));
    }

    [Fact]
    public void KnownStringRolesAdmissionAndErrorsAreClosedWithoutNumericFallback()
    {
        Assert.Equal("\"decodedInvalid\"", JsonSerializer.Serialize(CatalogAdmissionV1.DecodedInvalid));
        Assert.Equal("\"valid\"", JsonSerializer.Serialize(CatalogAdmissionV1.Valid));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CatalogRoleV1>("1"));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CatalogAdmissionV1>("2"));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CatalogErrorCodeV1>("1"));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CatalogRoleV1>("\"owner\""));
        Rejects(() => new CatalogAccessV1((CatalogRoleV1)0, CatalogActionsV1.ReadOnly));
        Rejects(() => new CatalogHeadV1(RevisionScope, "1", (CatalogAdmissionV1)3));
        Rejects(() => new CatalogErrorV1((CatalogErrorCodeV1)0));
        using var error = JsonDocument.Parse(JsonSerializer.Serialize(new CatalogErrorV1(CatalogErrorCodeV1.PersistenceFailure), Options));
        Assert.Equal("code", Assert.Single(error.RootElement.EnumerateObject()).Name);
        Assert.Equal("persistenceFailure", error.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public void AdmittedContextSerializesItsRequestTokenButKeepsItOutOfDiagnosticText()
    {
        const string token = "synthetic-request-token-do-not-log";
        var context = new CatalogContextV1("development", Principal, Organization, "Local catalog", CatalogFeaturesV1.ReadOnly, token);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(context, Options));
        Assert.Equal(token, json.RootElement.GetProperty("requestToken").GetString());
        Assert.Equal("development", json.RootElement.GetProperty("mode").GetString());
        Assert.Equal(Principal, json.RootElement.GetProperty("actorPrincipalId").GetString());
        Assert.Equal(Organization, json.RootElement.GetProperty("allowedOrganizationId").GetString());
        Assert.False(json.RootElement.TryGetProperty("admitted", out _));
        Assert.DoesNotContain(token, context.ToString(), StringComparison.Ordinal);
        Rejects(() => new CatalogContextV1("hosted", Principal, Organization, "Local", CatalogFeaturesV1.ReadOnly, token));
        Rejects(() => new CatalogContextV1("development", Principal, Organization, "Local", CatalogFeaturesV1.ReadOnly, ""));
    }

    [Fact]
    public void BearerContextRoundTripsWithoutSessionFieldsAndOtherModesStillRequireTheirToken()
    {
        var bearer = new CatalogContextV1("bearer", Principal, Organization, "Local catalog", CatalogFeaturesV1.ReadOnly,
            CatalogOrganizationActionsV1.None);
        var text = JsonSerializer.Serialize(bearer, Options);
        using (var json = JsonDocument.Parse(text))
        {
            Assert.Equal("bearer", json.RootElement.GetProperty("mode").GetString());
            Assert.False(json.RootElement.TryGetProperty("requestToken", out _));
            Assert.False(json.RootElement.TryGetProperty("identityContext", out _));
        }
        // Strongly typed clients read it with the shipped options, which respect required constructor parameters.
        var read = JsonSerializer.Deserialize<CatalogContextV1>(text, Options)!;
        Assert.Equal(("bearer", Principal, Organization, (string?)null, (string?)null),
            (read.Mode, read.ActorPrincipalId, read.AllowedOrganizationId, read.RequestToken, read.IdentityContext));
        Rejects(() => new CatalogContextV1("bearer", Principal, Organization, "Local", CatalogFeaturesV1.ReadOnly,
            CatalogOrganizationActionsV1.None, "token"));
        var tokenless = text.Replace("\"bearer\"", "\"development\"", StringComparison.Ordinal);
        Assert.ThrowsAny<Exception>(() => JsonSerializer.Deserialize<CatalogContextV1>(tokenless, Options));
    }

    [Fact]
    public void MetadataRepresentationExcludesHeadActivityAndActorVaryingActions()
    {
        var metadata = new CatalogMappingMetadataV1(MappingScope, " Exact name ", "Description\n", Largest);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(metadata, Options));
        Assert.Equal(new[] { "description", "name", "scope", "version" },
            json.RootElement.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(" Exact name ", json.RootElement.GetProperty("name").GetString());
        Assert.Equal("Description\n", json.RootElement.GetProperty("description").GetString());
        var mapping = MappingRecord();
        Assert.Equal(TimeSpan.Zero, mapping.CreatedAtUtc.Offset);
        Assert.Equal(Timestamp.ToUniversalTime(), mapping.CreatedAtUtc);
        Assert.Equal(TimeSpan.Zero, RevisionRecord().CommittedAtUtc.Offset);
    }

    [Fact]
    public void RevisionMetadataPreservesExactFactsWithoutStorageProfileLeaseOrEvidenceFields()
    {
        var revision = RevisionRecord();
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(revision, Options));
        Assert.Equal(new[]
        {
            "admission", "backendId", "committedAtUtc", "creatorPrincipalId", "logicalDocument",
            "nativeByteCount", "nativeDigest", "originalFilename", "scope", "sequence"
        }, json.RootElement.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal("sha256", json.RootElement.GetProperty("nativeDigest").GetProperty("algorithm").GetString());
        Assert.Equal(new string('a', 64), json.RootElement.GetProperty("nativeDigest").GetProperty("value").GetString());
        Assert.Equal("測定.native.json", revision.OriginalFilename);
        Rejects(() => RevisionRecord(byteCount: 0));
        Rejects(() => RevisionRecord(byteCount: 4194305));
        Rejects(() => RevisionRecord(digest: new ArtifactDigest("sha256", new string('A', 64))));
        Rejects(() => RevisionRecord(digest: new ArtifactDigest("sha512", new string('a', 64))));
        Rejects(() => RevisionRecord(filename: "dir/native.json"));
        Rejects(() => RevisionRecord(logicalDocument: "other.native.json"));
    }

    [Fact]
    public void PageSnapshotsAreBoundedReadOnlyAndRoundtripVisibleEmptyCollections()
    {
        var original = ProjectRecord();
        var source = new List<CatalogProjectV1> { original };
        var page = new CatalogProjectPageV1(source, Encode("{}"));
        source.Clear();
        Assert.Same(original, Assert.Single(page.Projects));
        var exposed = Assert.IsAssignableFrom<IList<CatalogProjectV1>>(page.Projects);
        Assert.True(exposed.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => exposed.Clear());
        Assert.Equal(100, new CatalogProjectPageV1(Enumerable.Repeat(original, 100)).Projects.Count);
        Rejects(() => new CatalogProjectPageV1(Enumerable.Repeat(original, 101)));
        Rejects(() => new CatalogMappingPageV1(Enumerable.Repeat(MappingRecord(), 101)));
        Rejects(() => new CatalogRevisionPageV1(Enumerable.Repeat(RevisionRecord(), 101)));
        Rejects(() => new CatalogProjectPageV1((IEnumerable<CatalogProjectV1>)null!));
        Rejects(() => new CatalogProjectPageV1([null!]));
        var empty = JsonSerializer.Deserialize<CatalogProjectPageV1>(JsonSerializer.Serialize(new CatalogProjectPageV1([]), Options), Options)!;
        Assert.Empty(empty.Projects);
        Assert.Null(empty.NextCursor);
        Assert.Empty(new CatalogMappingPageV1([]).Mappings);
        Assert.Empty(new CatalogRevisionPageV1([]).Revisions);
        Rejects(() => new CatalogProjectPageV1([original, ProjectRecord(new CatalogProjectScopeV1(Other, Project))]));
        Rejects(() => new CatalogMappingPageV1([MappingRecord(), MappingRecord(scope: new CatalogMappingScopeV1(Organization, Other, Mapping),
            head: new CatalogHeadV1(new CatalogRevisionScopeV1(Organization, Other, Mapping, Revision), "1", CatalogAdmissionV1.Valid))]));
    }

    [Fact]
    public void PublicCursorRepresentationCapsDecodedBytesAndRejectsNoncanonicalEncodings()
    {
        var maximum = Encode(new byte[512]);
        Assert.Equal(683, maximum.Length);
        Assert.Equal(maximum, new CatalogProjectPageV1([], maximum).NextCursor);
        foreach (var invalid in new[] { "", "A", "e30=", "Zh", " e30", Encode(new byte[513]) })
        {
            Rejects(() => new CatalogProjectPageV1([], invalid));
            Rejects(() => new CatalogMappingPageV1([], invalid));
            Rejects(() => new CatalogRevisionPageV1([], invalid));
        }
    }

    [Fact]
    public void PersistedTextKeepsUnicodeScalarsAndRejectsRuntimeMalformedUtf16()
    {
        var longestName = string.Concat(Enumerable.Repeat("😀", 200));
        Assert.Equal(longestName, ProjectRecord(name: longestName).Name);
        Rejects(() => ProjectRecord(name: longestName + "😀"));
        foreach (var surrogate in new[] { (char)0xd800, (char)0xdfff })
        {
            var malformed = new string(surrogate, 1);
            Rejects(() => ProjectRecord(name: malformed));
            Rejects(() => new CatalogMappingMetadataV1(MappingScope, "Name", malformed, "1"));
            Rejects(() => RevisionRecord(filename: "file" + malformed));
        }
        Rejects(() => ProjectRecord(name: "nul\0name"));
    }

    private static CatalogAccessV1 Access() => CatalogAccessV1.ReadOnly(CatalogRoleV1.Administrator);
    private static CatalogProjectV1 ProjectRecord(CatalogProjectScopeV1? scope = null, string name = "Name") =>
        new(scope ?? ProjectScope, name, "Description", Principal, Timestamp, Timestamp, "1", Access());
    private static CatalogMappingV1 MappingRecord(string version = "1", string sequence = "1", CatalogMappingScopeV1? scope = null,
        CatalogHeadV1? head = null) => new(scope ?? MappingScope, "Name", "Description", "ferrule", Principal, Other,
        Timestamp, Timestamp, version, head ?? new CatalogHeadV1(RevisionScope, sequence, CatalogAdmissionV1.Valid), Access());
    private static CatalogRevisionV1 RevisionRecord(string sequence = "1", int byteCount = 4194304,
        ArtifactDigest? digest = null, string filename = "測定.native.json", string logicalDocument = "entry.native.json") =>
        new(RevisionScope, sequence, CatalogAdmissionV1.DecodedInvalid, Principal, Timestamp, filename, logicalDocument,
            digest ?? new ArtifactDigest("sha256", new string('a', 64)), byteCount, "ferrule");
    private static string Encode(string value) => Encode(Encoding.UTF8.GetBytes(value));
    private static string Encode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static void Rejects(Action action) => Assert.ThrowsAny<ArgumentException>(action);
}
