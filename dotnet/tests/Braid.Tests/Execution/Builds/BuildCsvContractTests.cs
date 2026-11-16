using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Braid.Application;
using Braid.Deployment;
using Braid.Domain;

namespace Braid.Tests;

public sealed class BuildCsvContractTests
{
    internal static CsvDocumentDialect Dialect(string delimiter = ",", bool headers = false) =>
        new(CsvDocumentPolicy.DialectSchema, delimiter, "\"", false, headers, false, true);

    internal static BuildIntent Intent(CsvDocumentDialect? source = null, CsvDocumentDialect? target = null)
    {
        source ??= Dialect(); target ??= source;
        var scope = new RevisionScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        return new(Guid.NewGuid(), scope, Sha256Digest.FromBytes([1]),
            new("ferrule", BuildWorkerProtocol.Engine, "sha256:" + new string('a', 64), "braid.ferrule-csv-preview-adapter.v1"),
            new(BuildWorkerProtocol.CsvProfile, 1, "dotnet", BuildWorkerProtocol.RuntimeVersion, "braid.dotnet-assembly.v1", BuildWorkerProtocol.MaximumSourceBytes),
            [new(BuildBoundaryKind.PrimaryInput, null, source.Delimiter == "\t" ? "tsv" : "csv", source),
                new(BuildBoundaryKind.PrimaryOutput, null, target.Delimiter == "\t" ? "tsv" : "csv", target)],
            new(new(scope.OrganizationId, scope.ProjectId), Guid.NewGuid(), 1), Guid.NewGuid(), Guid.NewGuid());
    }

    [Fact]
    public void Csv_intent_round_trip_retains_both_dialects_and_changes_fingerprint_for_changed_settings()
    {
        var intent = Intent(Dialect(), Dialect("\t", true));
        var bytes = intent.CopyCanonicalBytes();
        var restored = BuildIntentCodec.Decode(bytes);
        Assert.Equal(intent.Fingerprint, restored.Fingerprint);
        Assert.Equal(intent.Boundaries, restored.Boundaries);
        using var parsed = JsonDocument.Parse(bytes);
        Assert.Equal(new[] { "schema", "delimiter", "quote", "quoteDisabled", "hasHeaderRow", "utf8Bom", "preserveEmptyStrings" },
            parsed.RootElement.GetProperty("boundaries")[0].GetProperty("csvDialect").EnumerateObject().Select(value => value.Name));
        var changed = new BuildIntent(intent.BuildId, intent.Revision, intent.NativeDigest, intent.Backend, intent.Profile,
            [intent.Boundaries[0], new(BuildBoundaryKind.PrimaryOutput, null, "tsv", Dialect("\t", false))],
            intent.Environment, intent.ApprovalId, intent.RegressionEvidenceId);
        Assert.NotEqual(intent.Fingerprint, changed.Fingerprint);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrongMedia")]
    [InlineData("unknown")]
    [InlineData("null")]
    public void Csv_intent_cannot_lose_or_relabel_its_dialect(string mutation)
    {
        var value = JsonNode.Parse(Intent().CopyCanonicalBytes())!;
        var boundary = value["boundaries"]![0]!.AsObject();
        if (mutation == "missing") boundary.Remove("csvDialect");
        if (mutation == "wrongMedia") boundary["format"] = "tsv";
        if (mutation == "unknown") boundary["csvDialect"]!["unknown"] = true;
        if (mutation == "null") boundary["csvDialect"] = null;
        Assert.ThrowsAny<Exception>(() => BuildIntentCodec.Decode(Encoding.UTF8.GetBytes(value.ToJsonString())));
    }

    [Fact]
    public void Legacy_boundary_serialization_omits_the_new_member()
    {
        var value = new BuildWorkerProtocol.Boundary("primaryInput", null, "json");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("{\"kind\":\"primaryInput\",\"name\":null,\"format\":\"json\"}", Encoding.UTF8.GetString(bytes));
        Assert.Throws<BuildContractException>(() => new BuildBoundary(BuildBoundaryKind.NamedOutput, "other", "csv", Dialect()));
    }

    [Fact]
    public void Worker_metadata_and_preflight_bind_dialect_not_just_format()
    {
        var intent = Intent();
        var changed = new BuildCapabilitySnapshot(intent.Revision, intent.NativeDigest, intent.Backend, intent.Profile,
            [intent.Boundaries[0], new(BuildBoundaryKind.PrimaryOutput, null, "csv", Dialect() with { Utf8Bom = true })],
            Braid.Contracts.Projections.CapabilityStatus.Supported);
        Assert.Equal(BuildPreflightReason.BoundaryMismatch, new BuildPreflightService().Evaluate(intent, changed).Reason);
        var refused = new BuildCapabilitySnapshot(intent.Revision, intent.NativeDigest, intent.Backend, intent.Profile, intent.Boundaries,
            Braid.Contracts.Projections.CapabilityStatus.Unsupported, refusalReason: BuildPreflightReason.UnsupportedCsvPlan);
        Assert.Equal(new BuildPreflightResult(BuildPreflightOutcome.Unsupported, BuildPreflightReason.UnsupportedCsvPlan), new BuildPreflightService().Evaluate(intent, refused));
        var request = BuildWorkerProtocol.For(intent, "inspect");
        var boundaries = intent.Boundaries.Select(value => new BuildWorkerProtocol.Boundary(
            value.Kind == BuildBoundaryKind.PrimaryInput ? "primaryInput" : "primaryOutput", null, value.Format, value.CsvDialect)).ToArray();
        var response = new BuildWorkerProtocol.Response(BuildWorkerProtocol.Protocol, request.RequestId, intent.Revision,
            request.NativeDigest, request.IntentDigest, BuildWorkerProtocol.Engine, BuildWorkerProtocol.CsvProfile, "supported", boundaries, []);
        var reply = BuildWorkerProtocol.DecodeReply(BuildWorkerProtocol.EncodeReply(response, []), request);
        Assert.Equal(intent.Boundaries, reply.Header.Boundaries.Select(BuildWorkerProtocol.ToBoundary));
        response = response with { Boundaries = [boundaries[0] with { CsvDialect = null }, boundaries[1]] };
        Assert.Throws<InvalidOperationException>(() => BuildWorkerProtocol.DecodeReply(BuildWorkerProtocol.EncodeReply(response, []), request));
    }
}
