using Weft.Application;
using Weft.Domain;

namespace Weft.Tests;

public sealed class PreviewRequestDocumentTests
{
    private static readonly RevisionScope Revision = new(
        Guid.Parse("11111111-1111-4111-8111-111111111111"), Guid.Parse("22222222-2222-4222-8222-222222222222"),
        Guid.Parse("33333333-3333-4333-8333-333333333333"), Guid.Parse("44444444-4444-4444-8444-444444444444"));

    private static PreviewRunSubmission Submission() => new(Revision, Guid.Parse("55555555-5555-4555-8555-555555555555"),
        PreviewTargetSelection.Named("summary"), new PreviewInputDocument(null, "{\"id\":1}"u8),
        [new PreviewInputDocument("rates", "{\"currency\":\"EUR\"}"u8)],
        [new("limit", PreviewParameterKind.Integer, "9007199254740993"), new("express", PreviewParameterKind.Boolean, "true")]);

    [Fact]
    public void CanonicalDocumentIsExactAndRoundTripsWithoutInputBytes()
    {
        var document = PreviewRequestDocument.Encode(Submission(), PreviewLimits.Default);
        Assert.Equal(
            "{\"schema\":\"weft.preview-request.v1\",\"organizationId\":\"11111111-1111-4111-8111-111111111111\","
            + "\"projectId\":\"22222222-2222-4222-8222-222222222222\",\"mappingId\":\"33333333-3333-4333-8333-333333333333\","
            + "\"revisionId\":\"44444444-4444-4444-8444-444444444444\",\"target\":{\"kind\":\"named\",\"name\":\"summary\"},"
            + "\"parameters\":[{\"name\":\"limit\",\"kind\":\"integer\",\"value\":\"9007199254740993\"},"
            + "{\"name\":\"express\",\"kind\":\"boolean\",\"value\":\"true\"}],"
            + "\"inputs\":[{\"name\":null,\"byteLength\":8,\"sha256\":\"" + Sha256Digest.FromBytes("{\"id\":1}"u8).Value + "\"},"
            + "{\"name\":\"rates\",\"byteLength\":18,\"sha256\":\"" + Sha256Digest.FromBytes("{\"currency\":\"EUR\"}"u8).Value + "\"}],"
            + "\"limits\":{\"maximumInputBytes\":8388608,\"maximumOutputBytes\":8388608,\"maximumArtifacts\":64,\"maximumTextBytes\":2048}}",
            document);
        var decoded = PreviewRequestDocument.Decode(document);
        Assert.Equal(Revision, decoded.Revision);
        Assert.Equal(PreviewTargetSelection.Named("summary"), decoded.Target);
        Assert.Equal(Submission().Parameters, decoded.Parameters);
        Assert.Equal([null, "rates"], decoded.Inputs.Select(input => input.Name));
        Assert.Equal([8L, 18L], decoded.Inputs.Select(input => input.ByteLength));
        Assert.Equal(PreviewLimits.Default, decoded.Limits);
        Assert.Equal(PreviewRequestDocument.Fingerprint(document), PreviewRequestDocument.Fingerprint(
            PreviewRequestDocument.Encode(Submission() with { Primary = new(null, "{\"id\":1}"u8) }, PreviewLimits.Default)));
    }

    [Fact]
    public void DocumentProfileHasASeparateFingerprintAndRoundTripsWhileLegacyBytesStayV1()
    {
        var legacy = PreviewRequestDocument.Encode(Submission(), PreviewLimits.Default);
        var document = PreviewRequestDocument.Encode(Submission() with { Profile = PreviewExecutionProfile.Document }, PreviewLimits.Default);
        Assert.NotEqual(PreviewRequestDocument.Fingerprint(legacy), PreviewRequestDocument.Fingerprint(document));
        Assert.Equal(PreviewExecutionProfile.Document, PreviewRequestDocument.Decode(document).Profile);
        Assert.Equal(PreviewExecutionProfile.LegacyJson, PreviewRequestDocument.Decode(legacy).Profile);
        Assert.Equal(legacy.Replace(PreviewRequestDocument.Schema, PreviewRequestDocument.DocumentSchema, StringComparison.Ordinal), document);
    }

    [Fact]
    public void EveryDeclaredValueChangesTheFingerprint()
    {
        var original = PreviewRequestDocument.Fingerprint(PreviewRequestDocument.Encode(Submission(), PreviewLimits.Default));
        var variants = new[]
        {
            Submission() with { Target = PreviewTargetSelection.All },
            Submission() with { Primary = new(null, "{\"id\":2}"u8) },
            Submission() with { NamedInputs = [new PreviewInputDocument("other", "{\"currency\":\"EUR\"}"u8)] },
            Submission() with { Parameters = [new("limit", PreviewParameterKind.String, "9007199254740993"), new("express", PreviewParameterKind.Boolean, "true")] },
            Submission() with { Parameters = [new("express", PreviewParameterKind.Boolean, "true"), new("limit", PreviewParameterKind.Integer, "9007199254740993")] },
            Submission() with { Revision = new(Revision.OrganizationId, Revision.ProjectId, Revision.MappingId, Guid.NewGuid()) }
        };
        foreach (var variant in variants)
            Assert.NotEqual(original, PreviewRequestDocument.Fingerprint(PreviewRequestDocument.Encode(variant, PreviewLimits.Default)));
        Assert.NotEqual(original, PreviewRequestDocument.Fingerprint(PreviewRequestDocument.Encode(Submission(),
            PreviewLimits.Default with { MaximumOutputBytes = 1024 })));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"schema\":\"weft.preview-request.v2\"}")]
    public void MalformedStoredDocumentsAreRefused(string document) =>
        Assert.Throws<PreviewRunException>(() => PreviewRequestDocument.Decode(document));

    [Fact]
    public void ClockUsesUtcWithSevenFractionalDigits() =>
        Assert.Equal("2026-10-20T06:30:00.1234567Z",
            PreviewRequestDocument.Clock(new DateTimeOffset(2026, 10, 20, 8, 30, 0, TimeSpan.FromHours(2)).AddTicks(1234567)));
}
