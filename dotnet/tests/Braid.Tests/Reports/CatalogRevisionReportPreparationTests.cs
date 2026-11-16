using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Braid.Api.Catalog;
using Braid.Application;
using Braid.Contracts.Catalog;
using Braid.Contracts.Projections;
using Braid.Domain;
using F = Braid.Tests.CatalogRevisionReportTestFacts;

namespace Braid.Tests;

/// <summary>Whole-wrapper admission over authored synthetic metadata and owned neutral reports; no transport qualification.</summary>
public sealed class CatalogRevisionReportPreparationTests
{
    [Fact]
    public async Task NotDerivedHandCountIncludesMetadataAndNullsWithScalarsAtContainingDepth()
    {
        var read = RevisionReportReadResult.ForSnapshot(F.Empty(F.ProjectionKey()));
        var mapped = CatalogRevisionReportMapperTests.Map(read, new(responseItemLimit: 16, jsonDepthLimit: 2));
        var prepared = CatalogRevisionReportResponsePreparer.Prepare(mapped, default);
        var (context, bytes) = await Deliver(prepared);
        using var json = JsonDocument.Parse(bytes);
        // Root1 + schema1 + revision(object1 + UUID4) + operation/state/outcome3 + nullable values6 = 16.
        Assert.Equal(16, ValueCount(json.RootElement));
        Assert.Equal(2, ContainerDepth(json.RootElement));
        Assert.Equal("observed", json.RootElement.GetProperty("readOutcome").GetString());
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
        Assert.Equal(bytes.LongLength, context.Response.ContentLength);
        Assert.Equal("application/json", context.Response.ContentType);
        Assert.False(context.Response.Headers.ContainsKey("ETag"));
        var oneLess = CatalogRevisionReportResponsePreparer.Prepare(
            CatalogRevisionReportMapperTests.Map(read, new(responseItemLimit: 15, jsonDepthLimit: 2)), default);
        Assert.Equal(503, oneLess.StatusCode);
    }

    [Fact]
    public async Task FullWrapperByteBoundaryIsInclusiveAndOneLessRemovesOnlyPayload()
    {
        var verified = F.VerifiedProjection();
        var read = RevisionReportReadResult.ForAvailable(F.Authority(), verified);
        var ordinary = CatalogRevisionReportMapperTests.Map(read);
        // Independent public contract serialization establishes the entire delivered UTF8 length.
        var expected = JsonSerializer.SerializeToUtf8Bytes(ordinary.Projection, ProjectionJson.CreateOptions());
        var inclusive = CatalogRevisionReportResponsePreparer.Prepare(
            CatalogRevisionReportMapperTests.Map(read, new(responseByteLimit: expected.Length)), default);
        Assert.Equal(expected.Length, inclusive.EncodedByteCount);
        Assert.Equal(expected, (await Deliver(inclusive)).Bytes);
        var limited = CatalogRevisionReportResponsePreparer.Prepare(
            CatalogRevisionReportMapperTests.Map(read, new(responseByteLimit: expected.Length - 1)), default);
        Assert.Equal(200, limited.StatusCode);
        using var refusal = JsonDocument.Parse((await Deliver(limited)).Bytes);
        Assert.Equal("resourceLimit", refusal.RootElement.GetProperty("readOutcome").GetString());
        Assert.Equal("available", refusal.RootElement.GetProperty("state").GetString());
        Assert.Equal("succeeded", refusal.RootElement.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, refusal.RootElement.GetProperty("report").ValueKind);
        Assert.Equal(JsonValueKind.Null, refusal.RootElement.GetProperty("failure").ValueKind);
        Assert.Equal(verified.Metadata.DerivationId.ToString("D"), refusal.RootElement.GetProperty("result").GetProperty("derivationId").GetString());
    }

    [Fact]
    public async Task CompleteAvailableWrapperHandCountIncludesItsResultAndFullReport()
    {
        var verified = F.VerifiedProjection();
        var read = RevisionReportReadResult.ForAvailable(F.Authority(), verified);
        var exact = CatalogRevisionReportResponsePreparer.Prepare(
            CatalogRevisionReportMapperTests.Map(read, new(responseItemLimit: 65)), default);
        using var admitted = JsonDocument.Parse((await Deliver(exact)).Bytes);
        // Empty projection report: object/status/provenance/document/diagnostics = 46 values.
        // The wrapper has 19 other values once success metadata replaces a nullable result: 65 total.
        Assert.Equal(65, ValueCount(admitted.RootElement));
        Assert.Equal(JsonValueKind.Object, admitted.RootElement.GetProperty("report").ValueKind);
        var limited = CatalogRevisionReportResponsePreparer.Prepare(
            CatalogRevisionReportMapperTests.Map(read, new(responseItemLimit: 64)), default);
        using var refused = JsonDocument.Parse((await Deliver(limited)).Bytes);
        Assert.Equal("resourceLimit", refused.RootElement.GetProperty("readOutcome").GetString());
        Assert.Equal(JsonValueKind.Null, refused.RootElement.GetProperty("report").ValueKind);
    }

    [Fact]
    public async Task ReportAcceptedAtInnerDepthFourNeedsExactlyOneOuterLevel()
    {
        var verified = F.VerifiedProjection(new(jsonDepthLimit: 5), readCaps: new(jsonDepthLimit: 4));
        var read = RevisionReportReadResult.ForAvailable(F.Authority(), verified);
        using var inner = JsonDocument.Parse(JsonSerializer.Serialize(verified.Report, ProjectionJson.CreateOptions()));
        Assert.Equal(4, ContainerDepth(inner.RootElement));
        var inclusive = CatalogRevisionReportResponsePreparer.Prepare(
            CatalogRevisionReportMapperTests.Map(read, new(jsonDepthLimit: 5)), default);
        using var outer = JsonDocument.Parse((await Deliver(inclusive)).Bytes);
        Assert.Equal(5, ContainerDepth(outer.RootElement));
        Assert.Equal(JsonValueKind.Object, outer.RootElement.GetProperty("report").ValueKind);
        var oneLess = CatalogRevisionReportResponsePreparer.Prepare(
            CatalogRevisionReportMapperTests.Map(read, new(jsonDepthLimit: 4)), default);
        using var refusal = JsonDocument.Parse((await Deliver(oneLess)).Bytes);
        Assert.Equal("resourceLimit", refusal.RootElement.GetProperty("readOutcome").GetString());
        Assert.Null(JsonSerializer.Deserialize<CatalogProjectionReportV1>(refusal.RootElement.GetRawText(), ProjectionJson.CreateOptions())!.Report);
    }

    [Fact]
    public async Task DeepOwnedDetailBudgetStaysBoundedAndNeverRaisesDeliveredHardDepth()
    {
        DetailNode detail = new LiteralDetail(new StringLiteralValue("exact"));
        for (var index = 0; index < 16; index++) detail = new GroupDetail("group", [detail]);
        var controls = new[] { new ProjectionControl("c", ControlKind.Construction, "scope", [], [], [detail], new DocumentNativeLocator(F.Source.LogicalDocument)) };
        var verified = F.VerifiedProjection(controls: controls);
        var read = RevisionReportReadResult.ForAvailable(F.Authority(), verified);
        using var inner = JsonDocument.Parse(JsonSerializer.Serialize(verified.Report, ProjectionJson.CreateOptions()));
        // The admitted leaf is at detail depth16: nested group object/child-array pairs add32,
        // above the control detail baseline, with the literal object at inner container depth39.
        Assert.Equal(39, ContainerDepth(inner.RootElement));
        var exact = CatalogRevisionReportResponsePreparer.Prepare(
            CatalogRevisionReportMapperTests.Map(read, new(jsonDepthLimit: 40)), default);
        using var admitted = JsonDocument.Parse((await Deliver(exact)).Bytes);
        Assert.Equal(40, ContainerDepth(admitted.RootElement));
        Assert.Equal(65, CatalogRevisionReportResponsePreparer.MaximumPreparationJsonDepth);
        Assert.True(ContainerDepth(admitted.RootElement) <= RevisionReportPolicy.MaximumJsonDepthLimit);
        var reduced = CatalogRevisionReportResponsePreparer.Prepare(
            CatalogRevisionReportMapperTests.Map(read, new(jsonDepthLimit: 39)), default);
        using var refused = JsonDocument.Parse((await Deliver(reduced)).Bytes);
        Assert.Equal("resourceLimit", refused.RootElement.GetProperty("readOutcome").GetString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task TinyCapsThatCannotFitReplacementUseOneFixedErrorWithoutRetryOrLocation(int cap)
    {
        var policy = cap switch
        {
            0 => new RevisionReportPolicy(responseByteLimit: 1),
            1 => new RevisionReportPolicy(responseItemLimit: 1),
            _ => new RevisionReportPolicy(jsonDepthLimit: 1)
        };
        var pending = RevisionReportEnsureResult.ForPending(F.Pending(F.ProjectionKey()));
        var mapped = CatalogRevisionReportMapper.Ensure(F.Actor, F.Revision(), RevisionReportOperation.Projection,
            null, pending, new(true, true, policy), default);
        Assert.Equal(202, mapped.StatusCode);
        Assert.NotNull(mapped.Location);
        var prepared = CatalogRevisionReportResponsePreparer.Prepare(mapped, default);
        var (context, bytes) = await Deliver(prepared);
        Assert.Equal(503, prepared.StatusCode);
        Assert.Equal("{\"code\":\"revisionReportResourceLimit\"}", Encoding.UTF8.GetString(bytes));
        Assert.True(bytes.Length <= 1024);
        Assert.False(context.Response.Headers.ContainsKey("Location"));
        Assert.False(context.Response.Headers.ContainsKey("Retry-After"));
        Assert.Equal(1024, CatalogRevisionReportResponsePreparer.MaximumErrorBytes);
    }

    [Fact]
    public async Task ResourceReplacementPreservesOnlyAlreadyVerifiedValidationSelectionAndActualFalseStatus()
    {
        var verified = F.VerifiedValidation(selectedProjection: F.ProjectionId, message: new string('m', 1500));
        var read = RevisionReportReadResult.ForAvailable(F.Authority(), verified);
        var mapped = CatalogRevisionReportMapper.Read(F.Actor, F.Revision(), RevisionReportOperation.Validation,
            null, F.ProjectionId, read, new(true, false, new(responseByteLimit: 1000)), default);
        var prepared = CatalogRevisionReportResponsePreparer.Prepare(mapped, default);
        using var json = JsonDocument.Parse((await Deliver(prepared)).Bytes);
        Assert.Equal(200, prepared.StatusCode);
        Assert.Equal("invalidMapping", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("resourceLimit", json.RootElement.GetProperty("readOutcome").GetString());
        Assert.Equal(F.ProjectionId.ToString("D"), json.RootElement.GetProperty("projectionDerivationId").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("report").ValueKind);
    }

    [Fact]
    public async Task ExactLiteralLexemesAndOwnedSnapshotsSurvivePreparationAndInputMutation()
    {
        var parameters = new List<ProjectionParameter>
        {
            new("i", "integer", new(FieldDataTypeKind.Scalar, [ScalarDataType.Integer]), FieldRequirement.Optional,
                new ExactNumberLiteralValue(ExactNumberFlavor.Integer, "900719925474099312345"), new DocumentNativeLocator(F.Source.LogicalDocument)),
            new("d", "decimal", new(FieldDataTypeKind.Scalar, [ScalarDataType.Decimal]), FieldRequirement.Optional,
                new ExactNumberLiteralValue(ExactNumberFlavor.Decimal, "-0.0000012300E+42"), new DocumentNativeLocator(F.Source.LogicalDocument)),
            new("f", "float", new(FieldDataTypeKind.Scalar, [ScalarDataType.BinaryFloat]), FieldRequirement.Optional,
                new ExactNumberLiteralValue(ExactNumberFlavor.BinaryFloat, "7ff8000000000042"), new DocumentNativeLocator(F.Source.LogicalDocument))
        };
        var verified = F.VerifiedProjection(parameters: parameters);
        var mapped = CatalogRevisionReportMapperTests.Map(RevisionReportReadResult.ForAvailable(F.Authority(), verified));
        var prepared = CatalogRevisionReportResponsePreparer.Prepare(mapped, default);
        parameters.Clear();
        var first = (await Deliver(prepared)).Bytes;
        var second = (await Deliver(prepared)).Bytes;
        Assert.Equal(first, second);
        using var json = JsonDocument.Parse(first);
        var values = json.RootElement.GetProperty("report").GetProperty("document").GetProperty("parameters");
        Assert.Equal("900719925474099312345", values[0].GetProperty("defaultValue").GetProperty("value").GetString());
        Assert.Equal("-0.0000012300E+42", values[1].GetProperty("defaultValue").GetProperty("value").GetString());
        Assert.Equal("7ff8000000000042", values[2].GetProperty("defaultValue").GetProperty("value").GetString());
    }

    [Fact]
    public async Task OriginalCancellationPreventsPreparationAndDeliveryWithoutInventingBytes()
    {
        var mapped = CatalogRevisionReportMapperTests.Map(RevisionReportReadResult.ForSnapshot(F.Empty(F.ProjectionKey())));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => CatalogRevisionReportResponsePreparer.Prepare(mapped, cancellation.Token));
        var prepared = CatalogRevisionReportResponsePreparer.Prepare(mapped, default);
        var context = new DefaultHttpContext { RequestAborted = cancellation.Token };
        using var body = new MemoryStream();
        context.Response.Body = body;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prepared.ExecuteAsync(context));
        Assert.Equal(0, body.Length);
        Assert.Null(context.Response.ContentLength);
    }

    private static async Task<(DefaultHttpContext Context, byte[] Bytes)> Deliver(CatalogRevisionReportPreparedResponse prepared)
    {
        var context = new DefaultHttpContext();
        using var body = new MemoryStream();
        context.Response.Body = body;
        await prepared.ExecuteAsync(context);
        return (context, body.ToArray());
    }

    private static int ValueCount(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => 1 + value.EnumerateObject().Sum(property => ValueCount(property.Value)),
        JsonValueKind.Array => 1 + value.EnumerateArray().Sum(ValueCount),
        _ => 1
    };

    private static int ContainerDepth(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => 1 + value.EnumerateObject().Select(property => ContainerDepth(property.Value)).DefaultIfEmpty(0).Max(),
        JsonValueKind.Array => 1 + value.EnumerateArray().Select(ContainerDepth).DefaultIfEmpty(0).Max(),
        _ => 0
    };
}
