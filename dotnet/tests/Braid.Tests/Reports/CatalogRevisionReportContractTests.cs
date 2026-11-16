using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Braid.Contracts.Catalog;
using Braid.Contracts.Operations;
using Braid.Contracts.Projections;

namespace Braid.Tests;

public sealed class CatalogRevisionReportContractTests
{
    private static CatalogRevisionScopeV1 Scope => new("abcdef01-0000-4000-8000-000000000001", "abcdef01-0000-4000-8000-000000000002",
        "abcdef01-0000-4000-8000-000000000003", "abcdef01-0000-4000-8000-000000000004");
    private static CatalogProjectionReportV1 Empty() => new("braid.catalog-revision-report.v1", Scope, "projection",
        CatalogRevisionReportStateV1.NotDerived, CatalogRevisionReportReadOutcomeV1.Observed, null, null, null, null, null, null);

    [Fact]
    public void VersionsAndIdsRemainExactTextAndTimesNormalizeWithoutChangingInstants()
    {
        var start = new DateTimeOffset(2026, 10, 14, 16, 0, 0, TimeSpan.FromHours(-7));
        var result = new CatalogRevisionReportResultV1("abcdef01-0000-4000-8000-000000000006", "9223372036854775807", start, start.AddTicks(1));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result, ProjectionJson.CreateOptions()));
        Assert.Equal(JsonValueKind.String, json.RootElement.GetProperty("generation").ValueKind);
        Assert.Equal("9223372036854775807", json.RootElement.GetProperty("generation").GetString());
        Assert.Equal(TimeSpan.Zero, result.StartedAtUtc.Offset);
        Assert.Equal(start.UtcDateTime, result.StartedAtUtc.UtcDateTime);
        Assert.Throws<ArgumentException>(() => new CatalogRevisionReportResultV1(result.DerivationId.ToUpperInvariant(), "1", start, start));
        Assert.Throws<ArgumentException>(() => new CatalogRevisionReportResultV1(result.DerivationId, "01", start, start));
        Assert.Throws<ArgumentException>(() => new CatalogRevisionReportResultV1(result.DerivationId, "9223372036854775808", start, start));
        Assert.Throws<ArgumentException>(() => new CatalogRevisionReportResultV1(result.DerivationId, "1", start, start.AddTicks(-1)));
    }

    [Theory]
    [InlineData("schemaVersion")]
    [InlineData("revision")]
    [InlineData("operation")]
    [InlineData("state")]
    [InlineData("readOutcome")]
    [InlineData("status")]
    [InlineData("result")]
    [InlineData("currentAttempt")]
    [InlineData("nextAttemptAtUtc")]
    [InlineData("failure")]
    [InlineData("report")]
    public void EveryProjectionPropertyIsPhysicallyRequiredIncludingNulls(string property)
    {
        var options = ProjectionJson.CreateOptions();
        var root = JsonNode.Parse(JsonSerializer.Serialize(Empty(), options))!.AsObject();
        Assert.True(root.Remove(property));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CatalogProjectionReportV1>(root.ToJsonString(), options));
    }

    [Fact]
    public void ValidationSelectionIsRequiredOnWireAndUnknownFieldsAreRefused()
    {
        var empty = new CatalogValidationReportV1("braid.catalog-revision-report.v1", Scope, "validation",
            CatalogRevisionReportStateV1.NotDerived, CatalogRevisionReportReadOutcomeV1.Observed, null, null, null, null, null, null, null);
        var options = ProjectionJson.CreateOptions();
        var root = JsonNode.Parse(JsonSerializer.Serialize(empty, options))!.AsObject();
        Assert.True(root.Remove("projectionDerivationId"));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CatalogValidationReportV1>(root.ToJsonString(), options));
        var projection = JsonNode.Parse(JsonSerializer.Serialize(Empty(), options))!.AsObject();
        projection["privateObjectId"] = "must not be a wire field";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CatalogProjectionReportV1>(projection.ToJsonString(), options));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("999")]
    [InlineData("\"unknown\"")]
    [InlineData("\"1\"")]
    [InlineData("\"AVAILABLE\"")]
    [InlineData("\"observed, objectUnavailable\"")]
    public void ReportEnumsAreClosedStrings(string raw)
    {
        var options = ProjectionJson.CreateOptions();
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CatalogRevisionReportStateV1>(raw, options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CatalogRevisionReportReadOutcomeV1>(raw, options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CatalogRevisionReportFailureV1>(raw, options));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PublicConfigurationPrioritizesOnlyNewClosedEnumsEvenWithAnExistingGenericConverter(bool existingGeneric)
    {
        var options = existingGeneric ? new JsonSerializerOptions(JsonSerializerDefaults.Web) : ProjectionJson.CreateOptions();
        if (existingGeneric)
        {
            options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
            ProjectionJson.Configure(options);
        }
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CatalogRevisionReportStateV1>("\"AVAILABLE\"", options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CatalogRevisionReportStateV1>("\"notDerived, pending\"", options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CatalogRevisionReportReadOutcomeV1>("\"observed, objectUnavailable\"", options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CatalogRevisionReportFailureV1>("\"objectUnavailable, storageIntegrity\"", options));
        Assert.Equal("\"available\"", JsonSerializer.Serialize(CatalogRevisionReportStateV1.Available, options));
        Assert.Equal("\"resourceLimit\"", JsonSerializer.Serialize(CatalogRevisionReportReadOutcomeV1.ResourceLimit, options));
        Assert.Equal("\"storageIo\"", JsonSerializer.Serialize(CatalogRevisionReportFailureV1.StorageIo, options));
        // Existing operation enums retain their qualified converter behavior and numeric identity.
        Assert.Equal(OperationStatusV1.Succeeded, JsonSerializer.Deserialize<OperationStatusV1>("\"SUCCEEDED\"", options));
        Assert.Equal("\"succeeded\"", JsonSerializer.Serialize(OperationStatusV1.Succeeded, options));
        var wrapper = JsonNode.Parse(JsonSerializer.Serialize(Empty(), options))!.AsObject();
        wrapper["state"] = "NOTDERIVED";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CatalogProjectionReportV1>(wrapper.ToJsonString(), options));
    }

    [Theory]
    [InlineData(CatalogErrorCodeV1.RevisionReportAdmissionBusy, 16, "revisionReportAdmissionBusy")]
    [InlineData(CatalogErrorCodeV1.RevisionReportClaimLost, 17, "revisionReportClaimLost")]
    [InlineData(CatalogErrorCodeV1.RevisionReportSelectionChanged, 18, "revisionReportSelectionChanged")]
    [InlineData(CatalogErrorCodeV1.RevisionReportGenerationExhausted, 19, "revisionReportGenerationExhausted")]
    [InlineData(CatalogErrorCodeV1.RevisionReportUnavailable, 20, "revisionReportUnavailable")]
    [InlineData(CatalogErrorCodeV1.RevisionReportResourceLimit, 21, "revisionReportResourceLimit")]
    public void ReportErrorsAppendStableClosedWireValues(CatalogErrorCodeV1 code, int number, string text)
    {
        Assert.Equal(number, (int)code);
        Assert.Equal($"{{\"code\":\"{text}\"}}", JsonSerializer.Serialize(new CatalogErrorV1(code), ProjectionJson.CreateOptions()));
        Assert.Equal(15, (int)CatalogErrorCodeV1.ProjectCreationAdmissionBusy);
    }

    [Fact]
    public void OldFeatureAndActionConstructorsDefaultOffAndDerivationRequiresReads()
    {
        var features = new CatalogFeaturesV1(true, false, false, false, false, false, false, false, false);
        var actions = new CatalogActionsV1(true, false, false, false, false, false, false, false, false);
        Assert.False(features.DeriveReports);
        Assert.False(actions.DeriveReports);
        Assert.False(CatalogFeaturesV1.ReadOnly.ReadReports);
        Assert.False(CatalogActionsV1.ReadOnly.DeriveReports);
        Assert.Throws<ArgumentException>(() => new CatalogFeaturesV1(true, false, false, false, false, false, false, false, false, true));
        Assert.Throws<ArgumentException>(() => new CatalogActionsV1(true, false, false, false, false, false, false, false, false, true));
        var context = new CatalogContextV1("development", "abcdef01-0000-4000-8000-000000000005", Scope.OrganizationId,
            "organization", features, "private token");
        Assert.False(context.OrganizationActions.CreateProjects);
        Assert.DoesNotContain("private token", context.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void LifecycleShapesRefuseInventedSuccessAndContradictoryAttempts()
    {
        var time = CatalogRevisionReportTestFacts.Start;
        var result = new CatalogRevisionReportResultV1("abcdef01-0000-4000-8000-000000000006", "1", time, time);
        Assert.Throws<ArgumentException>(() => new CatalogProjectionReportV1("braid.catalog-revision-report.v1", Scope, "projection",
            CatalogRevisionReportStateV1.Available, CatalogRevisionReportReadOutcomeV1.Observed, OperationStatusV1.Succeeded,
            result, null, null, null, null));
        Assert.Throws<ArgumentException>(() => new CatalogProjectionReportV1("braid.catalog-revision-report.v1", Scope, "validation",
            CatalogRevisionReportStateV1.NotDerived, CatalogRevisionReportReadOutcomeV1.Observed, null, null, null, null, null, null));
        Assert.Throws<ArgumentException>(() => new CatalogRevisionReportAttemptV1(result.DerivationId, "1", time, null,
            OperationStatusV1.Succeeded, null));
        var failed = new CatalogRevisionReportAttemptV1(result.DerivationId, "1", time, time, null, CatalogRevisionReportFailureV1.StorageIo);
        Assert.Throws<ArgumentException>(() => new CatalogProjectionReportV1("braid.catalog-revision-report.v1", Scope, "projection",
            CatalogRevisionReportStateV1.Failed, CatalogRevisionReportReadOutcomeV1.Observed, null, null, failed,
            time.AddSeconds(30), CatalogRevisionReportFailureV1.ResourceLimit, null));
        Assert.Null(new CatalogProjectionReportV1("braid.catalog-revision-report.v1", Scope, "projection",
            CatalogRevisionReportStateV1.Available, CatalogRevisionReportReadOutcomeV1.StorageIntegrity, OperationStatusV1.Succeeded,
            result, null, null, null, null).Report);
    }
}
