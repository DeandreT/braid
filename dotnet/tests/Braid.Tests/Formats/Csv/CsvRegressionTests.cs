using System.Text;
using Braid.Application;
using Braid.Application.Regression;
using Braid.Contracts.Operations;
using Braid.Domain;

namespace Braid.Tests;

public sealed class CsvRegressionTests
{
    private static readonly CsvDocumentDialect Dialect = new(CsvDocumentPolicy.DialectSchema, ",", "\"", false, true, false, true);

    [Theory]
    [InlineData("a,b\r\n\"x\",\"y\"\r\n", "a,b\nx,y\n")]
    [InlineData("a\n\"x\"\n", "\ufeffa\nx\n")]
    [InlineData("a,b\n\"x,y\",\"line 1\nline 2\"\n", "a,b\r\n\"x,y\",\"line 1\nline 2\"\r\n")]
    [InlineData("", "\ufeff")]
    public void Rows_compare_logical_strings_not_quoting_bom_or_record_terminators(string expected, string actual)
    {
        Assert.Equal(RegressionComparisonOutcome.Passed, Compare(expected, actual).Outcome);
        if (expected != actual) Assert.Equal(RegressionComparisonOutcome.Failed,
            Compare(expected, actual, RegressionComparisonService.ExactBytesPolicy).Outcome);
    }

    [Theory]
    [InlineData("a,b\nx\n", "a,b\nx,\n", 2, 2)]
    [InlineData("a,b\nx, \n", "a,b\nx,\n", 2, 2)]
    [InlineData("a,b\nx,y\n", "b,a\nx,y\n", 1, 1)]
    [InlineData("a\nx\ny\n", "a\ny\nx\n", 2, 1)]
    [InlineData("a\nx\n", "a\nx\ny\n", 3, 1)]
    public void Rows_preserve_headers_whitespace_order_and_missing_cells(string expected, string actual, int row, int column)
    {
        var result = Compare(expected, actual);
        Assert.Equal(RegressionComparisonOutcome.Failed, result.Outcome);
        Assert.Equal(new RegressionCsvLocation(row, column), Assert.Single(result.Differences).CsvLocation);
    }

    [Theory]
    [InlineData("a\n\"unterminated", 2, 1)]
    [InlineData("a,b\nx,\"y\"z\n", 2, 2)]
    public void Invalid_csv_has_bounded_one_based_diagnostics(string actual, int row, int column)
    {
        var result = Compare("a,b\nx,y\n", actual);
        Assert.Equal(RegressionNotEvaluatedReason.InvalidDocument, result.NotEvaluatedReason);
        Assert.Equal("actual", result.CsvDiagnostic!.Side);
        Assert.Equal(new RegressionCsvLocation(row, column), result.CsvDiagnostic.Location);
    }

    [Fact]
    public void Invalid_utf8_is_not_replacement_decoded()
    {
        var result = new RegressionComparisonService().Compare(Set("a"u8.ToArray()),
            new(OperationStatusV1.Succeeded, Set([0xff])), RegressionComparisonService.CsvRowsPolicy);
        Assert.Equal(RegressionNotEvaluatedReason.InvalidDocument, result.NotEvaluatedReason);
        Assert.Equal(new RegressionCsvLocation(1, 1), result.CsvDiagnostic!.Location);
    }

    [Theory]
    [InlineData("a,b\r\nx,", 2, 2)]
    [InlineData("\ufeffa,b\r\nx,\"quoted\r\nline,\"\"text\"\"", 2, 2)]
    [InlineData("a,b\nx,\"line\ntext\"\ny,", 3, 2)]
    public void Invalid_utf8_location_uses_logical_records_and_cells(string validPrefix, int row, int column)
    {
        var bytes = Encoding.UTF8.GetBytes(validPrefix).Concat(new byte[] { 0xff }).ToArray();
        var result = new RegressionComparisonService().Compare(Set("a,b\nx,y\n"u8.ToArray()),
            new(OperationStatusV1.Succeeded, Set(bytes)), RegressionComparisonService.CsvRowsPolicy);
        Assert.Equal(RegressionNotEvaluatedReason.InvalidDocument, result.NotEvaluatedReason);
        Assert.Equal(new RegressionCsvLocation(row, column), result.CsvDiagnostic!.Location);
    }

    [Fact]
    public void Comparison_refuses_dialect_changes_and_bounds_work()
    {
        var different = RegressionOutputSet.Capture([RegressionOutput.Capture(RegressionOutputKey.Primary, "a"u8,
            mediaType: "text/csv", csvDialect: Dialect with { HasHeaderRow = false })]);
        var result = new RegressionComparisonService().Compare(Set("a"u8.ToArray()), new(OperationStatusV1.Succeeded, different),
            RegressionComparisonService.CsvRowsPolicy);
        Assert.Equal(RegressionDifferenceKind.CsvDialectDiffer, Assert.Single(result.Differences).Kind);
        Assert.Throws<RegressionComparisonException>(() => new RegressionComparisonService().Compare(Set("a"u8.ToArray()),
            new(OperationStatusV1.Succeeded, Set("a"u8.ToArray())), RegressionComparisonService.CsvRowsPolicy,
            new RegressionComparisonLimits(comparedBytes: 1)));
        Assert.Throws<RegressionComparisonException>(() => Compare(new string('x', CsvDocumentPolicy.MaximumCellBytes + 1), "a"));
    }

    [Fact]
    public void Csv_case_and_completion_roundtrip_empty_present_input_and_exact_dialect()
    {
        var value = Case([]);
        var codec = new RegressionCaseEnvelopeCodec();
        var envelope = codec.Encode(value);
        var restored = codec.Decode(envelope.Reference, envelope.CopyBytes());
        Assert.Equal(PreviewExecutionProfile.CsvDocument, restored.Replay.Profile);
        Assert.Equal(0, restored.Replay.Primary.ByteCount);
        Assert.Equal(Dialect, restored.Replay.Primary.CsvDialect);
        Assert.Equal(Dialect, Assert.Single(restored.Expected.Outputs).CsvDialect);
        Assert.Equal(envelope.CopyBytes(), codec.Encode(restored).CopyBytes());
        var results = new RegressionRunResultCodec();
        var id = Guid.NewGuid();
        var bytes = results.EncodeObservation(id, restored, MappingPreviewStatus.Succeeded, restored.Expected);
        Assert.Contains(RegressionRunResultCodec.CsvSchema, Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.Equal(RegressionComparisonOutcome.Passed, results.Decode(id, restored, bytes).Comparison!.Outcome);
        var changed = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("\"hasHeaderRow\":true", "\"hasHeaderRow\":false", StringComparison.Ordinal));
        Assert.Throws<InvalidOperationException>(() => results.Decode(id, restored, changed));
    }

    [Fact]
    public void Legacy_cannot_use_empty_inputs_or_csv_metadata()
    {
        Assert.Throws<RegressionCaseException>(() => new RegressionReplay(PreviewTargetSelection.All, "2026-10-31T00:00:00Z",
            PreviewLimits.Default, new(null, []), [], []));
        Assert.Throws<RegressionComparisonException>(() => RegressionOutput.Capture(RegressionOutputKey.Primary, [], mediaType: "text/csv"));
        var value = Case([]);
        Assert.Throws<RegressionCaseException>(() => new RegressionCaseVersion(value.CaseId, 1, value.Owner, value.Name, value.Revision,
            value.NativeDigest, value.Runtime with { }, value.Replay, RegressionComparisonService.EdiSegmentsPolicy, value.Expected));
    }

    private static RegressionCaseVersion Case(byte[] input) => new(Guid.NewGuid(), 1, new PrincipalId(Guid.NewGuid()), "CSV",
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), Sha256Digest.FromBytes("native"u8),
        new("ferrule", "engine", "build", "adapter", CsvDocumentPolicy.Profile),
        new(PreviewTargetSelection.All, "2026-10-31T00:00:00Z", PreviewLimits.Default, new(null, input, Dialect), [], []),
        RegressionComparisonService.CsvRowsPolicy, Set([]));

    private static RegressionComparisonResult Compare(string expected, string actual, string policy = RegressionComparisonService.CsvRowsPolicy) =>
        new RegressionComparisonService().Compare(Set(Encoding.UTF8.GetBytes(expected)),
            new(OperationStatusV1.Succeeded, Set(Encoding.UTF8.GetBytes(actual))), policy);
    private static RegressionOutputSet Set(byte[] bytes) => RegressionOutputSet.Capture([
        RegressionOutput.Capture(RegressionOutputKey.Primary, bytes, mediaType: "text/csv", csvDialect: Dialect)]);
}
