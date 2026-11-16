using System.Text.Json;
using Braid.Application;
using Braid.Contracts.Projections;
using Braid.Domain;
using static Braid.Tests.RevisionReportCodecTestSupport;

namespace Braid.Tests;

public sealed class RevisionReportEnvelopePrecisionTests
{
    [Theory]
    [InlineData("0000000000000000")]
    [InlineData("8000000000000000")]
    [InlineData("3ff0000000000000")]
    [InlineData("7ff0000000000000")]
    [InlineData("fff0000000000000")]
    [InlineData("7ff0000000000001")]
    [InlineData("7ff8000000000042")]
    [InlineData("fff8deadbeefcafe")]
    public void NeutralBinary64CodecPreservesEveryAuthoredBitPatternIncludingInfinityAndNaNPayloads(string bits)
    {
        var literal = "{\"kind\":\"exactNumber\",\"flavor\":\"binaryFloat\",\"value\":\"" + bits + "\"}";
        var result = RoundTripParameter(literal, "binaryFloat");
        var actual = Assert.IsType<ExactNumberLiteralValue>(result.DefaultValue);
        Assert.Equal(ExactNumberFlavor.BinaryFloat, actual.Flavor);
        Assert.Equal(bits, actual.Value);
    }

    [Theory]
    [InlineData("integer", "-0")]
    [InlineData("integer", "9007199254740993")]
    [InlineData("integer", "-9223372036854775809")]
    [InlineData("decimal", "1.2300")]
    [InlineData("decimal", "-12.30e+4")]
    public void ExactNumericStringsKeepSignScaleExponentAndPrecision(string flavor, string text)
    {
        var literal = "{\"kind\":\"exactNumber\",\"flavor\":\"" + flavor + "\",\"value\":\"" + text + "\"}";
        var actual = Assert.IsType<ExactNumberLiteralValue>(RoundTripParameter(literal, flavor).DefaultValue);
        Assert.Equal(text, actual.Value);
    }

    [Theory]
    [InlineData("7ff00000")]
    [InlineData("7FF0000000000000")]
    [InlineData("7ff00000000000000")]
    [InlineData("7ff000000000000g")]
    [InlineData(" 7ff0000000000000")]
    public void WrongWidthCaseAndNonhexBinaryFloatLexemesAreRefused(string bits)
    {
        var key = ProjectionKey();
        var literal = "{\"kind\":\"exactNumber\",\"flavor\":\"binaryFloat\",\"value\":" + Quote(bits) + "}";
        var report = ProjectionJson(key, "[" + Parameter(literal, "binaryFloat") + "]");
        var bytes = Bytes(Envelope(Claim(key), report));
        Assert.Equal(RevisionReportCodecFailure.Integrity, Assert.Throws<RevisionReportCodecException>(() =>
            new RevisionReportEnvelopeCodec(new StoredOnlyRegistry()).Decode(Metadata(Claim(key), bytes,
                ProjectionObservation(key, 1)), bytes, new())).Failure);
    }

    [Fact]
    public void ExactStringLiteralPreservesEmbeddedNulOtherControlsAndNonBmpUnicode()
    {
        const string text = "left\0\t\n\r\b\f\"\\ 😀e\u0301 right";
        var literal = "{\"kind\":\"string\",\"value\":" + Quote(text) + "}";
        Assert.Equal(text, Assert.IsType<StringLiteralValue>(RoundTripParameter(literal, "string").DefaultValue).Value);
    }

    [Fact]
    public void LiteralPathNamesNamespacesIndicesAndPointerEscapesSurviveWithoutNormalization()
    {
        var key = ProjectionKey();
        const string pathName = " ../a~1/\0😀 ";
        const string namespaceText = "urn:literal:../x/";
        const string pointer = "/a~0~1b/01/~01";
        var boundaries = $$$"""
            [{"id":"b","name":"input","role":"primaryInput","format":"neutral","shape":"object","rootFieldId":"root","nativeLocator":{"kind":"document","documentId":{{{Quote(Source.LogicalDocument)}}}}}]
            """;
        var rootField = $$$"""
            {"id":"root","boundaryId":"b","path":[],"name":"root","dataType":{"kind":"object","scalarTypes":[],"nativeName":null},"cardinality":{"kind":"exactlyOne","minimum":null,"maximum":null},"requirement":"required","nullability":"nonNullable","nativeLocator":{"kind":"document","documentId":{{{Quote(Source.LogicalDocument)}}}}}
            """;
        var childField = $$$"""
            {"id":"child","boundaryId":"b","path":[{"kind":"field","name":{{{Quote(pathName)}}},"namespaceUri":{{{Quote(namespaceText)}}}}],"name":{{{Quote(pathName)}}},"dataType":{"kind":"scalar","scalarTypes":["string"],"nativeName":null},"cardinality":{"kind":"bounded","minimum":"0","maximum":"9007199254740993"},"requirement":"optional","nullability":"nullable","nativeLocator":{"kind":"jsonPointer","documentId":{{{Quote(Source.LogicalDocument)}}},"pointer":{{{Quote(pointer)}}}}}
            """;
        const string relationships = """
            [{"id":"member-root","kind":"boundaryMember","from":{"kind":"field","fieldId":"root"},"to":{"kind":"boundary","boundaryId":"b"}},{"id":"member-child","kind":"boundaryMember","from":{"kind":"field","fieldId":"child"},"to":{"kind":"boundary","boundaryId":"b"}}]
            """;
        var report = ProjectionJson(key, fields: "[" + rootField + "," + childField + "]", boundaries: boundaries)
            .Replace("\"relationships\":[]", "\"relationships\":" + relationships, StringComparison.Ordinal);
        var bytes = Bytes(Envelope(Claim(key), report));
        var actual = Assert.IsType<VerifiedProjectionReport>(new RevisionReportEnvelopeCodec(new StoredOnlyRegistry())
            .Decode(Metadata(Claim(key), bytes, ProjectionObservation(key, 5)), bytes, new()));
        var child = actual.Report.Document!.Fields.Single(field => field.Id == "child");
        var path = Assert.IsType<FieldPathSegment>(Assert.Single(child.Path));
        Assert.Equal(pathName, path.Name);
        Assert.Equal(namespaceText, path.NamespaceUri);
        Assert.Equal("9007199254740993", child.Cardinality.Maximum);
        Assert.Equal(pointer, Assert.IsType<JsonPointerNativeLocator>(child.NativeLocator).Pointer);
        var encoded = new RevisionReportEnvelopeCodec(new StoredOnlyRegistry()).Encode(Claim(key), Backend(key, report, 5));
        var decoded = Assert.IsType<VerifiedProjectionReport>(new RevisionReportEnvelopeCodec(new StoredOnlyRegistry())
            .Decode(Metadata(Claim(key), encoded.CopyBytes(), ProjectionObservation(key, 5)), encoded.CopyBytes(), new()));
        Assert.Equal(pathName, Assert.IsType<FieldPathSegment>(decoded.Report.Document!.Fields[1].Path[0]).Name);
    }

    [Theory]
    [InlineData(9007199254740993L)]
    [InlineData(9223372036854775806L)]
    [InlineData(long.MaxValue)]
    public void PositiveInt64GenerationRemainsAnExactJsonIntegerAcrossEncodeAndDecode(long generation)
    {
        var key = ProjectionKey();
        var claim = Claim(key, generation);
        var codec = new RevisionReportEnvelopeCodec(new StoredOnlyRegistry());
        var encoded = codec.Encode(claim, Backend(key, ProjectionJson(key)));
        var bytes = encoded.CopyBytes();
        using var raw = JsonDocument.Parse(bytes);
        Assert.Equal(generation.ToString(System.Globalization.CultureInfo.InvariantCulture), raw.RootElement.GetProperty("generation").GetRawText());
        Assert.Equal(generation, codec.Decode(Metadata(claim, bytes), bytes, new()).Metadata.Generation);
    }

    private static ProjectionParameter RoundTripParameter(string literal, string scalarType)
    {
        var key = ProjectionKey();
        var claim = Claim(key);
        var report = ProjectionJson(key, "[" + Parameter(literal, scalarType) + "]");
        var bytes = Bytes(Envelope(claim, report));
        var codec = new RevisionReportEnvelopeCodec(new StoredOnlyRegistry());
        var actual = Assert.IsType<VerifiedProjectionReport>(codec.Decode(Metadata(claim, bytes,
            ProjectionObservation(key, 1)), bytes, new()));
        var fromRaw = Assert.Single(actual.Report.Document!.Parameters);
        var encoded = codec.Encode(claim, Backend(key, report, 1));
        var encodedBytes = encoded.CopyBytes();
        var again = Assert.IsType<VerifiedProjectionReport>(codec.Decode(Metadata(claim, encodedBytes,
            ProjectionObservation(key, 1)), encodedBytes, new()));
        var fromEncoded = Assert.Single(again.Report.Document!.Parameters);
        Assert.Equal(fromRaw.Id, fromEncoded.Id);
        Assert.Equal(fromRaw.Name, fromEncoded.Name);
        Assert.Equal(fromRaw.DefaultValue, fromEncoded.DefaultValue);
        Assert.Equal(fromRaw.DataType.Kind, fromEncoded.DataType.Kind);
        Assert.Equal(fromRaw.DataType.ScalarTypes.ToArray(), fromEncoded.DataType.ScalarTypes.ToArray());
        Assert.Equal(fromRaw.NativeLocator, fromEncoded.NativeLocator);
        return fromEncoded;
    }
}
