using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Braid.Contracts.Catalog;
using Braid.Contracts.Projections;
using Braid.Domain;

namespace Braid.Tests;

/// <summary>
/// Pure public-contract checks only. Actual authority, strict raw HTTP parsing,
/// atomic creation and replay have separate PostgreSQL and transport qualification.
/// </summary>
public sealed class CatalogProjectCreationContractTests
{
    private const string Organization = "00112233-4455-6677-8899-aabbccddeeff";
    private const string Project = "10213243-5465-7687-98a9-bacbdcedfe0f";
    private const string Creator = "20314253-6475-8697-a8b9-cadbecfd0e1f";
    private const string Other = "40516273-8495-a6b7-c8d9-eafb0c1d2e3f";
    private const string Token = "private-framework-request-token";
    private static readonly DateTimeOffset CreatedAt = new(2026, 10, 14, 12, 34, 56, TimeSpan.FromHours(-7));
    private static JsonSerializerOptions Options => ProjectionJson.CreateOptions();
    private static CatalogProjectScopeV1 Scope => new(Organization, Project);
    private static string ProjectUrl => $"/api/v1/organizations/{Organization}/projects/{Project}";

    [Fact]
    public void CreationInputPreservesOverLimitRawWhitespaceUntilExistingTrimOnceAcceptance()
    {
        var acceptedName = Repeat("😀", 200);
        var rawName = new string(' ', 5000) + acceptedName + new string('\u3000', 1000) + "\r\n\t";
        const string description = " \r\nCafe\u0301😀\t ";
        var document = new CatalogProjectCreateV1(rawName, description);
        var encoded = JsonSerializer.Serialize(document, Options);
        using var json = JsonDocument.Parse(encoded);

        Assert.True(rawName.EnumerateRunes().Count() > 200);
        Assert.True(Encoding.UTF8.GetByteCount(rawName) > 800);
        Assert.True(Encoding.UTF8.GetByteCount(encoded) < 65536);
        Assert.Equal(new[] { "description", "name" }, PropertyNames(json.RootElement));
        Assert.Equal(rawName, document.Name);
        Assert.Equal(rawName, json.RootElement.GetProperty("name").GetString());
        Assert.Equal(description, json.RootElement.GetProperty("description").GetString());
        var restored = JsonSerializer.Deserialize<CatalogProjectCreateV1>(encoded, Options)!;
        Assert.Equal(document, restored);

        var accepted = new ProjectCreationMetadata(restored.Name, restored.Description);
        var existing = new IngestionCreateMetadata(rawName, description);
        Assert.Equal(acceptedName, accepted.Name);
        Assert.Equal(existing.Name, accepted.Name);
        Assert.Equal(description, accepted.Description);
        var key = Guid.Parse(Other);
        var actor = new PrincipalId(Guid.Parse(Creator));
        Assert.Equal(ProjectCreationFingerprint.Create(Guid.Parse(Organization), actor, key, accepted),
            ProjectCreationFingerprint.Create(Guid.Parse(Organization), actor, key,
                new ProjectCreationMetadata(acceptedName, description)));
    }

    [Fact]
    public void CreationInputKeepsDescriptionWhitespaceAndUnicodeWithoutNormalization()
    {
        var composed = new CatalogProjectCreateV1("é", " \r\nexact\t ");
        var decomposed = new CatalogProjectCreateV1("e\u0301", composed.Description);
        Assert.NotEqual(composed, decomposed);
        Assert.Equal("é", composed.Name);
        Assert.Equal("e\u0301", decomposed.Name);
        Assert.Equal(" \r\nexact\t ", composed.Description);
        Assert.Equal(string.Empty, new CatalogProjectCreateV1("Project", string.Empty).Description);
        Assert.Equal(Repeat("😀", 4000), new CatalogProjectCreateV1("Project", Repeat("😀", 4000)).Description);
    }

    [Fact]
    public void CreationInputRejectsInvalidAcceptedTextWithoutLimitingOuterWhitespace()
    {
        foreach (var name in new[] { "", " \t\r\n\u3000", "Project\0", Repeat("😀", 201), new string('x', 201) })
            Assert.ThrowsAny<ArgumentException>(() => new CatalogProjectCreateV1(name, string.Empty));
        foreach (var description in new[] { "before\0after", Repeat("😀", 4001), new string('x', 4001) })
            Assert.ThrowsAny<ArgumentException>(() => new CatalogProjectCreateV1("Project", description));
        foreach (var malformed in new[] { new string((char)0xd800, 1), new string((char)0xdc00, 1) })
        {
            Assert.ThrowsAny<ArgumentException>(() => new CatalogProjectCreateV1("Project" + malformed, string.Empty));
            Assert.ThrowsAny<ArgumentException>(() => new CatalogProjectCreateV1("Project", malformed));
        }
        Assert.Throws<ArgumentNullException>(() => new CatalogProjectCreateV1(null!, string.Empty));
        Assert.Throws<ArgumentNullException>(() => new CatalogProjectCreateV1("Project", null!));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"name\":\"Project\"}")]
    [InlineData("{\"description\":\"\"}")]
    [InlineData("{\"name\":null,\"description\":\"\"}")]
    [InlineData("{\"name\":\"Project\",\"description\":null}")]
    [InlineData("{\"name\":7,\"description\":\"\"}")]
    [InlineData("{\"name\":\"Project\",\"description\":false}")]
    [InlineData("{\"name\":\"Project\",\"description\":\"\",\"projectId\":\"client-choice\"}")]
    [InlineData("{\"name\":\"Project\",\"description\":\"\",\"idempotencyKey\":\"client-key\"}")]
    public void RequiredCreationStringsAndUnmappedFieldsRejectUnderConfiguredContractJson(string json) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CatalogProjectCreateV1>(json, Options));

    [Fact]
    public void CaseSensitiveContractJsonAcceptsDecodedNamesAndRejectsDifferentPropertyCase()
    {
        // Global Web reflection options are case-insensitive; the actual HTTP
        // parser independently enforces exact case and duplicate-member refusal.
        var options = Options;
        options.PropertyNameCaseInsensitive = false;
        var accepted = JsonSerializer.Deserialize<CatalogProjectCreateV1>(
            "{\"na\\u006de\":\"Project\",\"description\":\"\"}", options)!;
        Assert.Equal(new CatalogProjectCreateV1("Project", string.Empty), accepted);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CatalogProjectCreateV1>(
            "{\"Name\":\"Project\",\"description\":\"\"}", options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CatalogProjectCreateV1>(
            "{\"name\":\"Project\",\"Description\":\"\"}", options));
    }

    [Fact]
    public void OrganizationActionsAreRequiredAndIndependentOfSupportedFeaturesAndProjectRoles()
    {
        foreach (var supported in new[] { false, true })
        {
            var context = Context(supported, false);
            Assert.Equal(supported, context.Features.CreateProjects);
            Assert.False(context.OrganizationActions.CreateProjects);
        }
        var allowed = Context(true, true);
        Assert.True(allowed.Features.CreateProjects);
        Assert.True(allowed.OrganizationActions.CreateProjects);
        Assert.Throws<ArgumentException>(() => Context(false, true));
        Assert.Throws<ArgumentNullException>(() => new CatalogContextV1("development", Creator,
            Organization, "Organization", Features(true), null!, Token));
        foreach (var role in Enum.GetValues<CatalogRoleV1>())
            Assert.False(CatalogAccessV1.ReadOnly(role).Actions.CreateProjects);
        Assert.False(CatalogOrganizationActionsV1.None.CreateProjects);
    }

    [Fact]
    public void ContextEmitsRequiredOrganizationActionAndStillOmitsTokenFromDiagnosticText()
    {
        var context = Context(true, true);
        var encoded = JsonSerializer.Serialize(context, Options);
        using var json = JsonDocument.Parse(encoded);
        Assert.Equal(new[] { "actorPrincipalId", "allowedOrganizationId", "features", "mode",
            "organizationActions", "organizationName", "requestToken" }, PropertyNames(json.RootElement));
        var actions = json.RootElement.GetProperty("organizationActions");
        Assert.Equal("createProjects", Assert.Single(actions.EnumerateObject()).Name);
        Assert.True(actions.GetProperty("createProjects").GetBoolean());
        Assert.Equal(Token, json.RootElement.GetProperty("requestToken").GetString());
        Assert.DoesNotContain(Token, context.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("RequestToken", context.ToString(), StringComparison.Ordinal);
        Assert.Equal(context, JsonSerializer.Deserialize<CatalogContextV1>(encoded, Options));

        var missingActions = JsonNode.Parse(encoded)!.AsObject();
        Assert.True(missingActions.Remove("organizationActions"));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CatalogContextV1>(missingActions.ToJsonString(), Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CatalogOrganizationActionsV1>("{}", Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CatalogOrganizationActionsV1>("{\"createProjects\":null}", Options));

        var compatibility = new CatalogContextV1("development", Creator, Organization, "Organization", Features(false), Token);
        Assert.Same(CatalogOrganizationActionsV1.None, compatibility.OrganizationActions);
        Assert.False(compatibility.OrganizationActions.CreateProjects);
        Assert.DoesNotContain(Token, compatibility.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ReceiptSerializesOnlyImmutableInitialFactsWithUtcAndDecimalStrings()
    {
        var receipt = Receipt();
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(receipt, Options));
        Assert.Equal(new[] { "createdAtUtc", "creatorPrincipalId", "description", "initialMembershipActive",
            "initialMembershipRole", "initialMembershipVersion", "initialProjectVersion", "name", "project" }, PropertyNames(json.RootElement));
        Assert.Equal(new[] { "organizationId", "projectId" }, PropertyNames(json.RootElement.GetProperty("project")));
        Assert.Equal(Organization, receipt.Project.OrganizationId);
        Assert.Equal(Project, receipt.Project.ProjectId);
        Assert.Equal(Creator, json.RootElement.GetProperty("creatorPrincipalId").GetString());
        Assert.Equal("Project", json.RootElement.GetProperty("name").GetString());
        Assert.Equal(" \r\nexact\t ", json.RootElement.GetProperty("description").GetString());
        Assert.Equal(CreatedAt.ToUniversalTime(), receipt.CreatedAtUtc);
        Assert.Equal(TimeSpan.Zero, receipt.CreatedAtUtc.Offset);
        Assert.Equal(receipt.CreatedAtUtc, json.RootElement.GetProperty("createdAtUtc").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.String, json.RootElement.GetProperty("initialProjectVersion").ValueKind);
        Assert.Equal("1", json.RootElement.GetProperty("initialProjectVersion").GetString());
        Assert.Equal(JsonValueKind.String, json.RootElement.GetProperty("initialMembershipVersion").ValueKind);
        Assert.Equal("1", json.RootElement.GetProperty("initialMembershipVersion").GetString());
        Assert.Equal("administrator", json.RootElement.GetProperty("initialMembershipRole").GetString());
        Assert.True(json.RootElement.GetProperty("initialMembershipActive").GetBoolean());
        Assert.Equal(receipt, JsonSerializer.Deserialize<CatalogProjectCreationReceiptV1>(JsonSerializer.Serialize(receipt, Options), Options));
    }

    [Fact]
    public void ReceiptRefusesCurrentOrMalformedFactsInsteadOfChangingHistoricalMembership()
    {
        foreach (var version in new[] { "", "0", "01", "2", "9223372036854775807", " 1", "1 ", "1.0" })
        {
            Assert.ThrowsAny<ArgumentException>(() => Receipt(initialProjectVersion: version));
            Assert.ThrowsAny<ArgumentException>(() => Receipt(initialMembershipVersion: version));
        }
        foreach (var role in new[] { (CatalogRoleV1)0, CatalogRoleV1.Viewer, CatalogRoleV1.Runner, CatalogRoleV1.Publisher, (CatalogRoleV1)999 })
            Assert.Throws<ArgumentOutOfRangeException>(() => Receipt(initialMembershipRole: role));
        Assert.Throws<ArgumentException>(() => Receipt(initialMembershipActive: false));
        Assert.ThrowsAny<ArgumentException>(() => Receipt(initialProjectVersion: null!));
        Assert.ThrowsAny<ArgumentException>(() => Receipt(initialMembershipVersion: null!));
        Assert.ThrowsAny<ArgumentException>(() => Receipt(name: " Project "));
        Assert.ThrowsAny<ArgumentException>(() => Receipt(name: new string('x', 201)));
        Assert.ThrowsAny<ArgumentException>(() => Receipt(description: "before\0after"));
        Assert.ThrowsAny<ArgumentException>(() => Receipt(description: Repeat("😀", 4001)));
        Assert.Throws<ArgumentNullException>(() => new CatalogProjectCreationReceiptV1(null!, "Project", string.Empty,
            Creator, CreatedAt, "1", CatalogRoleV1.Administrator, true, "1"));
        foreach (var id in new[] { Guid.Empty.ToString("D"), Creator.ToUpperInvariant(), Guid.Parse(Creator).ToString("N"), " " + Creator })
            Assert.ThrowsAny<ArgumentException>(() => Receipt(creatorPrincipalId: id));
        Assert.Throws<ArgumentNullException>(() => Receipt(creatorPrincipalId: null!));
    }

    [Fact]
    public void ReceiptWireRequiresEveryHistoricalFactAndNeverDefaultsMembership()
    {
        var encoded = JsonSerializer.Serialize(Receipt(), Options);
        foreach (var property in new[] { "project", "name", "description", "creatorPrincipalId", "createdAtUtc",
            "initialProjectVersion", "initialMembershipRole", "initialMembershipActive", "initialMembershipVersion" })
        {
            var missing = JsonNode.Parse(encoded)!.AsObject();
            Assert.True(missing.Remove(property));
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CatalogProjectCreationReceiptV1>(missing.ToJsonString(), Options));
        }
    }

    [Theory]
    [InlineData(CatalogProjectCreationOutcomeV1.Created, 1, "created")]
    [InlineData(CatalogProjectCreationOutcomeV1.Replayed, 2, "replayed")]
    public void SuccessOutcomesRemainClosedStringsAndUseTheSameHistoricalReceipt(
        CatalogProjectCreationOutcomeV1 outcome, int value, string text)
    {
        Assert.Equal(value, (int)outcome);
        var receipt = Receipt();
        var result = new CatalogProjectCreationResultV1(outcome, receipt, ProjectUrl);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result, Options));
        Assert.Equal(new[] { "outcome", "projectUrl", "receipt" }, PropertyNames(json.RootElement));
        Assert.Equal(text, json.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(ProjectUrl, json.RootElement.GetProperty("projectUrl").GetString());
        Assert.Same(receipt, result.Receipt);
        Assert.Equal(result, JsonSerializer.Deserialize<CatalogProjectCreationResultV1>(JsonSerializer.Serialize(result, Options), Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CatalogProjectCreationOutcomeV1>(value.ToString(System.Globalization.CultureInfo.InvariantCulture), Options));
    }

    [Fact]
    public void CreatedAndReplayedResultsRetainByteEquivalentReceiptAndScopedUrl()
    {
        var receipt = Receipt();
        var created = new CatalogProjectCreationResultV1(CatalogProjectCreationOutcomeV1.Created, receipt, ProjectUrl);
        var replayed = new CatalogProjectCreationResultV1(CatalogProjectCreationOutcomeV1.Replayed, receipt, ProjectUrl);
        Assert.Equal(JsonSerializer.Serialize(created.Receipt, Options), JsonSerializer.Serialize(replayed.Receipt, Options));
        Assert.Equal(created.ProjectUrl, replayed.ProjectUrl);
        Assert.NotEqual(created.Outcome, replayed.Outcome);
    }

    [Fact]
    public void ResultsRejectUnknownOutcomesMissingReceiptsAndForeignOrNoncanonicalUrls()
    {
        var receipt = Receipt();
        foreach (var value in new[] { int.MinValue, -1, 0, 3, 999, int.MaxValue })
            Assert.Throws<ArgumentOutOfRangeException>(() => new CatalogProjectCreationResultV1((CatalogProjectCreationOutcomeV1)value, receipt, ProjectUrl));
        Assert.Throws<ArgumentNullException>(() => new CatalogProjectCreationResultV1(CatalogProjectCreationOutcomeV1.Created, null!, ProjectUrl));
        Assert.Throws<ArgumentNullException>(() => new CatalogProjectCreationResultV1(CatalogProjectCreationOutcomeV1.Created, receipt, null!));
        foreach (var url in new[] { "", "/projects/" + Project, ProjectUrl + "/", ProjectUrl + "?detail=true", ProjectUrl + "#project",
            "https://example.invalid" + ProjectUrl, ProjectUrl.Replace(Organization, Other, StringComparison.Ordinal),
            ProjectUrl.Replace(Project, Other, StringComparison.Ordinal), ProjectUrl.ToUpperInvariant() })
            Assert.Throws<ArgumentException>(() => new CatalogProjectCreationResultV1(CatalogProjectCreationOutcomeV1.Created, receipt, url));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CatalogProjectCreationOutcomeV1>("\"notAdmitted\"", Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CatalogProjectCreationOutcomeV1>("\"idempotencyConflict\"", Options));
    }

    [Theory]
    [InlineData(CatalogErrorCodeV1.PermissionDenied, 13, "permissionDenied")]
    [InlineData(CatalogErrorCodeV1.IdempotencyConflict, 14, "idempotencyConflict")]
    [InlineData(CatalogErrorCodeV1.ProjectCreationAdmissionBusy, 15, "projectCreationAdmissionBusy")]
    public void ProjectCreationErrorsAreOpaqueCategoricalStrings(CatalogErrorCodeV1 code, int value, string text)
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
    public void NewErrorsPreserveAllPredecessorValuesAndUnknownErrorsStayClosed()
    {
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 }, new[]
        {
            CatalogErrorCodeV1.Unavailable, CatalogErrorCodeV1.InvalidRequest, CatalogErrorCodeV1.NotFound,
            CatalogErrorCodeV1.IdentityRejected, CatalogErrorCodeV1.PersistenceFailure, CatalogErrorCodeV1.InternalFailure,
            CatalogErrorCodeV1.PreconditionRequired, CatalogErrorCodeV1.PreconditionFailed, CatalogErrorCodeV1.UnsupportedMediaType,
            CatalogErrorCodeV1.BodyLimit, CatalogErrorCodeV1.VersionExhausted, CatalogErrorCodeV1.IngestionAdmissionBusy
        }.Select(value => (int)value));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CatalogErrorV1((CatalogErrorCodeV1)999));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CatalogErrorCodeV1>("\"unknownError\"", Options));
    }

    [Fact]
    public void PublicCreationContractsHaveNoWritableOrHiddenPersistenceFacts()
    {
        foreach (var type in new[] { typeof(CatalogProjectCreateV1), typeof(CatalogOrganizationActionsV1),
            typeof(CatalogProjectCreationReceiptV1), typeof(CatalogProjectCreationResultV1) })
        {
            Assert.True(type.IsSealed);
            Assert.All(type.GetProperties(BindingFlags.Public | BindingFlags.Instance), property => Assert.Null(property.SetMethod));
        }
        Assert.Equal(new[] { "CreateProjects" }, typeof(CatalogOrganizationActionsV1).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name));
        var receiptProperties = typeof(CatalogProjectCreationReceiptV1).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (var prohibited in new[] { "Access", "Actions", "IdempotencyKey", "Fingerprint", "CurrentRole", "CurrentVersion" })
            Assert.DoesNotContain(receiptProperties, property => property.Name == prohibited);
    }

    private static CatalogFeaturesV1 Features(bool createProjects) =>
        new(true, createProjects, false, false, false, false, false, false, false);

    private static CatalogContextV1 Context(bool supported, bool allowed) =>
        new("development", Creator, Organization, "Organization", Features(supported), new CatalogOrganizationActionsV1(allowed), Token);

    private static CatalogProjectCreationReceiptV1 Receipt(string name = "Project", string description = " \r\nexact\t ",
        string creatorPrincipalId = Creator, string initialProjectVersion = "1",
        CatalogRoleV1 initialMembershipRole = CatalogRoleV1.Administrator, bool initialMembershipActive = true,
        string initialMembershipVersion = "1") =>
        new(Scope, name, description, creatorPrincipalId, CreatedAt, initialProjectVersion,
            initialMembershipRole, initialMembershipActive, initialMembershipVersion);

    private static IEnumerable<string> PropertyNames(JsonElement element) =>
        element.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal);

    private static string Repeat(string value, int count) => string.Concat(Enumerable.Repeat(value, count));
}
