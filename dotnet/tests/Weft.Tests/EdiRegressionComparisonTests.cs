using System.Text;
using Weft.Application.Regression;
using Weft.Contracts.Operations;

namespace Weft.Tests;

public sealed class EdiRegressionComparisonTests
{
    private static readonly EdiComparisonOptions Strict = new(true, false, false);

    private static RegressionComparisonResult Compare(string expected, string actual, string media,
        EdiComparisonOptions? options = null, RegressionComparisonLimits? limits = null) => new RegressionComparisonService().Compare(
            Set(expected, media), new(OperationStatusV1.Succeeded, Set(actual, media)), RegressionComparisonService.EdiSegmentsPolicy,
            limits, ediOptions: options ?? Strict);

    private static RegressionOutputSet Set(string text, string media) => RegressionOutputSet.Capture(
        [RegressionOutput.Capture(RegressionOutputKey.Primary, Encoding.UTF8.GetBytes(text), mediaType: media)]);

    public static IEnumerable<object[]> NonScalarEnvelopeFields()
    {
        foreach (var (media, original, fields, component, repetition) in new[]
        {
            ("application/edi-x12", DocumentPreviewTestData.X12,
                new[] { ("GS*PO*WEFTSENDER*WEFTRECEIVER*20261024*1200*330", "330"), ("ST*850*0330", "0330"),
                    ("SE*3", "3"), ("SE*3*0330", "0330"), ("GE*1", "1"), ("GE*1*330", "330"),
                    ("IEA*1", "1"), ("IEA*1*000000330", "000000330") }, ":", "^"),
            ("application/edifact", DocumentPreviewTestData.Edifact.Replace("UNOC:3", "UNOC:4", StringComparison.Ordinal),
                new[] { ("UNB+UNOC:4+WEFTSENDER+WEFTRECEIVER+261024:1200+330", "330"), ("UNH+330", "330"),
                    ("UNT+3", "3"), ("UNT+3+330", "330"), ("UNZ+1", "1"), ("UNZ+1+330", "330") }, ":", "*")
        })
            foreach (var (prefix, value) in fields)
                foreach (var delimiter in new[] { component, repetition })
                    yield return [media, original.Replace(prefix, prefix + delimiter + value, StringComparison.Ordinal)];
        yield return ["application/edifact", "UNH+1:A+ORDERS:D:96A:UN'BGM+220+X'UNT+3+1:B'"];
        yield return ["application/edifact", "UNA:+.?*'UNH+1*2+ORDERS:D:96A:UN'UNT+2+1*2'"];
        yield return ["application/edifact", "UNB+UNOC:4+S+R+261024:1200+1'UNG+ORDERS+S+R+261024:1200+2:3+UN+D:96A'UNE+0+2:3'UNZ+1+1'"];
    }

    [Theory]
    [MemberData(nameof(NonScalarEnvelopeFields))]
    public void Complete_scalar_envelope_values_are_required_before_every_mask(string media, string document)
    {
        var result = Compare(document, document, media, new(false, true, true));
        Assert.Equal(RegressionComparisonOutcome.NotEvaluated, result.Outcome);
        Assert.Equal(RegressionNotEvaluatedReason.InvalidDocument, result.NotEvaluatedReason);
        Assert.False(Weft.Application.EdiEnvelopeValidation.IsValid(Encoding.UTF8.GetBytes(document), media));
    }

    [Theory]
    [InlineData("1?:A", "1?:A", RegressionComparisonOutcome.Passed)]
    [InlineData("1?*A", "1?*A", RegressionComparisonOutcome.Passed)]
    [InlineData("1?:A", "1?:B", RegressionComparisonOutcome.NotEvaluated)]
    public void Released_component_content_is_scalar_but_the_complete_reference_must_match(string start, string end, RegressionComparisonOutcome outcome)
    {
        var document = $"UNA:+.?*'UNH+{start}+ORDERS:D:96A:UN'BGM+220+X'UNT+3+{end}'";
        Assert.Equal(outcome, Compare(document, document, "application/edifact", new(false, true, true)).Outcome);
    }

    [Fact]
    public void Mixed_x12_syntax_is_comparable_and_valid_for_execution()
    {
        var mixed = DocumentPreviewTestData.X12 + DocumentPreviewTestData.X12.Replace('*', '|').Replace('~', '!');
        Assert.Equal(RegressionComparisonOutcome.Passed, Compare(mixed, mixed, "application/edi-x12").Outcome);
        Assert.Equal(Weft.Application.EdiExecutionValidation.Valid,
            Weft.Application.EdiEnvelopeValidation.ValidateExecution(Encoding.UTF8.GetBytes(mixed), "application/edi-x12"));
    }

    [Fact]
    public void Mixed_outputs_keep_json_bytes_exact_and_media_mismatches_are_not_relabelled()
    {
        var x12 = RegressionOutput.Capture(RegressionOutputKey.Primary, Encoding.UTF8.GetBytes(DocumentPreviewTestData.X12), mediaType: "application/edi-x12");
        var expected = RegressionOutputSet.Capture([x12, RegressionOutput.Capture(RegressionOutputKey.Named("audit"), "{\"n\":1}"u8)]);
        var actual = RegressionOutputSet.Capture([x12, RegressionOutput.Capture(RegressionOutputKey.Named("audit"), "{ \"n\": 1.0 }"u8)]);
        var result = new RegressionComparisonService().Compare(expected, new(OperationStatusV1.Succeeded, actual),
            RegressionComparisonService.EdiSegmentsPolicy, ediOptions: new(false, true, true));
        Assert.Equal(RegressionDifferenceKind.BytesDiffer, Assert.Single(result.Differences).Kind);
        var wrongMedia = RegressionOutputSet.Capture([RegressionOutput.Capture(RegressionOutputKey.Primary, x12.CopyBytes(), mediaType: "application/edifact")]);
        result = new RegressionComparisonService().Compare(RegressionOutputSet.Capture([x12]), new(OperationStatusV1.Succeeded, wrongMedia),
            RegressionComparisonService.EdiSegmentsPolicy, ediOptions: Strict);
        Assert.Equal(RegressionComparisonOutcome.Failed, result.Outcome);
        Assert.Equal(RegressionDifferenceKind.MediaTypeDiffer, Assert.Single(result.Differences).Kind);
    }

    [Theory]
    [InlineData("997", "AK2")]
    [InlineData("999", "IK2")]
    public void Acknowledgement_controls_are_explicitly_masked_but_response_codes_remain_significant(string transaction, string transactionReference)
    {
        var source = DocumentPreviewTestData.X12.Replace("ST*850*0330~BEG*00*SA*WEFT-330~SE*3*0330~",
            $"ST*{transaction}*0330~AK1*PO*330~{transactionReference}*850*0330~AK9*A*1*1*1~SE*5*0330~", StringComparison.Ordinal);
        Assert.Contains("AK9", source);
        var changed = source.Replace("AK1*PO*330", "AK1*PO*900", StringComparison.Ordinal)
            .Replace($"{transactionReference}*850*0330", $"{transactionReference}*850*0900", StringComparison.Ordinal);
        Assert.Equal(RegressionComparisonOutcome.Failed, Compare(source, changed, "application/edi-x12").Outcome);
        Assert.Equal(RegressionComparisonOutcome.Passed, Compare(source, changed, "application/edi-x12", new(true, true, false)).Outcome);
        Assert.Equal(RegressionComparisonOutcome.Failed, Compare(source, changed.Replace("AK9*A", "AK9*R", StringComparison.Ordinal),
            "application/edi-x12", new(true, true, true)).Outcome);
    }

    [Fact]
    public void Edifact_contrl_control_references_do_not_mask_acknowledgement_actions()
    {
        const string expected = "UNB+UNOC:3+SENDER+RECEIVER+261024:1200+1'UNH+1+CONTRL:D:96A:UN'UCI+330+SENDER+RECEIVER+7'UCM+330+ORDERS:D:96A:UN+7'UNT+4+1'UNZ+1+1'";
        var actual = expected.Replace("+330+", "+331+", StringComparison.Ordinal);
        Assert.Equal(RegressionComparisonOutcome.Passed, Compare(expected, actual, "application/edifact", new(true, true, false)).Outcome);
        Assert.Equal(RegressionComparisonOutcome.Failed, Compare(expected, actual.Replace("+7'", "+4'", StringComparison.Ordinal),
            "application/edifact", new(true, true, true)).Outcome);
    }

    [Theory]
    [InlineData("application/edi-x12")]
    [InlineData("application/edifact")]
    public void CompleteEnvelopesCompareWithoutLenientSegmentSkipping(string media)
    {
        var document = media == "application/edi-x12" ? DocumentPreviewTestData.X12 : DocumentPreviewTestData.Edifact;
        Assert.Equal(RegressionComparisonOutcome.Passed, Compare(document, document, media).Outcome);
        var changed = document.Replace("WEFT-330", "WEFT-331", StringComparison.Ordinal);
        var result = Compare(document, changed, media, new(true, true, true));
        Assert.Equal(RegressionComparisonOutcome.Failed, result.Outcome);
        var difference = Assert.Single(result.Differences);
        Assert.Equal(RegressionDifferenceKind.SegmentsDiffer, difference.Kind);
        Assert.Equal(media == "application/edi-x12" ? "BEG" : "BGM", difference.Location!.SegmentId);
        Assert.Equal(media == "application/edi-x12" ? 3 : 2, difference.Location.Element);
        Assert.Null(difference.FirstDifferentByte);
    }

    [Theory]
    [InlineData("application/edi-x12", "SE*3*0330", "SE*4*0330", "SE", 1)]
    [InlineData("application/edi-x12", "SE*3*0330", "SE*3*9999", "SE", 2)]
    [InlineData("application/edi-x12", "GE*1*330", "GE*2*330", "GE", 1)]
    [InlineData("application/edi-x12", "GE*1*330", "GE*1*331", "GE", 2)]
    [InlineData("application/edi-x12", "IEA*1*", "IEA*2*", "IEA", 1)]
    [InlineData("application/edifact", "UNT+3+330", "UNT+4+330", "UNT", 1)]
    [InlineData("application/edifact", "UNT+3+330", "UNT+3+331", "UNT", 2)]
    [InlineData("application/edifact", "UNZ+1+330", "UNZ+2+330", "UNZ", 1)]
    [InlineData("application/edifact", "UNZ+1+330", "UNZ+1+331", "UNZ", 2)]
    public void Envelope_count_and_control_failures_report_the_exact_segment_before_ignoring_values(string media,
        string source, string replacement, string segment, int element)
    {
        var original = media == "application/edi-x12" ? DocumentPreviewTestData.X12 : DocumentPreviewTestData.Edifact;
        var result = Compare(original, original.Replace(source, replacement, StringComparison.Ordinal), media, new(false, true, true));
        Assert.Equal(RegressionComparisonOutcome.NotEvaluated, result.Outcome);
        Assert.Equal(RegressionNotEvaluatedReason.InvalidDocument, result.NotEvaluatedReason);
        Assert.Equal("actual", result.DocumentDiagnostic!.Side);
        Assert.Equal(segment, result.DocumentDiagnostic.Location.SegmentId);
        Assert.Equal(element, result.DocumentDiagnostic.Location.Element);
    }

    [Fact]
    public void ServiceSeparatorsAreComparedUnlessThePolicyExplicitlyIgnoresThem()
    {
        var alternateX12 = DocumentPreviewTestData.X12.Replace('*', '|').Replace('~', '!');
        var alternateEdifact = "UNA>|.! ~" + DocumentPreviewTestData.Edifact.Replace('+', '|').Replace(':', '>').Replace('\'', '~');
        foreach (var (expected, actual, media) in new[]
        {
            (DocumentPreviewTestData.X12, alternateX12, "application/edi-x12"),
            (DocumentPreviewTestData.X12, DocumentPreviewTestData.X12.Replace('~', '\n'), "application/edi-x12"),
            (DocumentPreviewTestData.Edifact, alternateEdifact, "application/edifact")
        })
        {
            Assert.Equal(RegressionDifferenceKind.SeparatorsDiffer, Assert.Single(Compare(expected, actual, media).Differences).Kind);
            Assert.Equal(RegressionComparisonOutcome.Passed, Compare(expected, actual, media, new(false, false, false)).Outcome);
        }
    }

    [Fact]
    public void IgnoredControlNumbersAndDatesDoNotHideBusinessChangesOrInvalidEnvelopeReferences()
    {
        var x12 = DocumentPreviewTestData.X12.Replace("000000330", "000000331", StringComparison.Ordinal)
            .Replace("261024", "261025", StringComparison.Ordinal).Replace("20261024", "20261025", StringComparison.Ordinal);
        var edifact = DocumentPreviewTestData.Edifact.Replace("261024:1200+330'", "261025:1300+331'", StringComparison.Ordinal)
            .Replace("UNZ+1+330'", "UNZ+1+331'", StringComparison.Ordinal)
            .Replace("UNH+330+", "UNH+332+", StringComparison.Ordinal).Replace("UNT+3+330'", "UNT+3+332'", StringComparison.Ordinal);
        foreach (var (expected, actual, media) in new[]
        {
            (DocumentPreviewTestData.X12, x12, "application/edi-x12"),
            (DocumentPreviewTestData.Edifact, edifact, "application/edifact")
        })
        {
            Assert.Equal(RegressionComparisonOutcome.Failed, Compare(expected, actual, media).Outcome);
            Assert.Equal(RegressionComparisonOutcome.Passed, Compare(expected, actual, media, new(true, true, true)).Outcome);
            var broken = actual.Replace(media == "application/edi-x12" ? "IEA*1*000000331" : "UNZ+1+331",
                media == "application/edi-x12" ? "IEA*1*000000999" : "UNZ+1+999", StringComparison.Ordinal);
            var result = Compare(expected, broken, media, new(true, true, true));
            Assert.Equal(RegressionComparisonOutcome.NotEvaluated, result.Outcome);
            Assert.Equal(RegressionNotEvaluatedReason.InvalidDocument, result.NotEvaluatedReason);
            Assert.Equal("actual", result.DocumentDiagnostic!.Side);
            Assert.Equal(2, result.DocumentDiagnostic.Location.Element);
        }
    }

    [Fact]
    public void RepetitionsAndComponentsKeepTheirOrderAndElementLocation()
    {
        foreach (var (document, media, source, reversed) in new[]
        {
            (DocumentPreviewTestData.X12, "application/edi-x12", "WEFT-330^SECOND:9", "SECOND:9^WEFT-330"),
            (DocumentPreviewTestData.Edifact.Replace("UNOC:3", "UNOC:4", StringComparison.Ordinal), "application/edifact", "WEFT-330*SECOND:9", "SECOND:9*WEFT-330")
        })
        {
            var expected = document.Replace("WEFT-330", source, StringComparison.Ordinal);
            var actual = document.Replace("WEFT-330", reversed, StringComparison.Ordinal);
            Assert.Equal(RegressionComparisonOutcome.Passed, Compare(expected, expected, media).Outcome);
            var location = Assert.Single(Compare(expected, actual, media).Differences).Location!;
            Assert.Equal(1, location.Repetition);
            Assert.Equal(1, location.Component);
        }
        var escaped = DocumentPreviewTestData.Edifact.Replace("WEFT-330", "WEFT?+?':330", StringComparison.Ordinal);
        Assert.Equal(RegressionComparisonOutcome.Passed, Compare(escaped, escaped, "application/edifact").Outcome);
    }

    [Fact]
    public void MultipleInterchangesGroupsAndTransactionsAreCountedAndNeverSorted()
    {
        var transactions = DocumentPreviewTestData.X12.Replace("GE*1*330~", "ST*850*0331~BEG*00*SA*SECOND~SE*3*0331~GE*2*330~", StringComparison.Ordinal);
        var groups = DocumentPreviewTestData.X12.Replace("IEA*1*000000330~", "GS*PO*WEFTSENDER*WEFTRECEIVER*20261024*1200*331*X*005010~ST*850*0331~BEG*00*SA*SECOND~SE*3*0331~GE*1*331~IEA*2*000000330~", StringComparison.Ordinal);
        var edifactMessages = DocumentPreviewTestData.Edifact.Replace("UNZ+1+330'", "UNH+331+ORDERS:D:96A:UN'BGM+220+SECOND'UNT+3+331'UNZ+2+330'", StringComparison.Ordinal);
        var edifactGroups = DocumentPreviewTestData.Edifact.Replace("UNH+", "UNG+ORDERS+WEFTSENDER+WEFTRECEIVER+261024:1200+1+UN+D:96A'UNH+", StringComparison.Ordinal)
            .Replace("UNZ+", "UNE+1+1'UNZ+", StringComparison.Ordinal);
        foreach (var (document, media) in new[] { (transactions, "application/edi-x12"), (groups, "application/edi-x12"),
            (edifactMessages, "application/edifact"), (edifactGroups, "application/edifact"),
            (DocumentPreviewTestData.X12 + "\r\n" + groups, "application/edi-x12"),
            (DocumentPreviewTestData.Edifact + "\n" + edifactMessages, "application/edifact") })
        {
            var result = Compare(document, document, media);
            Assert.True(result.Outcome == RegressionComparisonOutcome.Passed, result.DocumentDiagnostic?.ToString());
        }
        var changedSecond = DocumentPreviewTestData.X12 + "\n" + transactions.Replace("SECOND", "THIRD", StringComparison.Ordinal);
        var mismatch = Compare(DocumentPreviewTestData.X12 + transactions, changedSecond, "application/edi-x12");
        Assert.Equal(2, Assert.Single(mismatch.Differences).Location!.Interchange);
    }

    [Theory]
    [InlineData("BEG*00*SA*WEFT-330~", "application/edi-x12")]
    [InlineData("BGM+220+WEFT-330'", "application/edifact")]
    [InlineData("UNH+1+ORDERS:D:96A:UN'BGM+220+ONE'UNT+9+1'", "application/edifact")]
    public void MissingEnvelopesAndWrongCountsAreNotSuccessfulComparisons(string document, string media) =>
        Assert.Equal(RegressionComparisonOutcome.NotEvaluated, Compare(document, document, media).Outcome);

    [Fact]
    public void ComparisonBoundsAndExplicitOptionsAreEnforced()
    {
        Assert.Throws<RegressionComparisonException>(() => Compare(DocumentPreviewTestData.X12, DocumentPreviewTestData.X12,
            "application/edi-x12", limits: new(1)));
        Assert.Throws<RegressionComparisonException>(() => new RegressionComparisonService().Compare(Set(DocumentPreviewTestData.X12, "application/edi-x12"),
            new(OperationStatusV1.Succeeded, Set(DocumentPreviewTestData.X12, "application/edi-x12")), RegressionComparisonService.EdiSegmentsPolicy));
        var bad = "UNH+1+ORDERS:D:96A:UN'FTX+" + new string('x', 16_385) + "'UNT+3+1'";
        Assert.Equal(RegressionNotEvaluatedReason.InvalidDocument, Compare(bad, bad, "application/edifact").NotEvaluatedReason);
    }
}
