using System.Text;
using Weft.Application.Regression;
using Weft.Contracts.Operations;
using Weft.Domain;

namespace Weft.Tests;

public sealed class RegressionComparisonTests
{
    private readonly RegressionComparisonService service = new();

    [Fact]
    public void Exact_outputs_match_by_identity_instead_of_position()
    {
        var primary = Output(null, "primary");
        var a = Output("a", "first");
        var b = Output("b", "second");
        var result = Compare(Set(b, primary, a), Set(a, b, primary));
        Assert.Equal(RegressionComparisonOutcome.Passed, result.Outcome);
        Assert.Equal(RegressionNotEvaluatedReason.None, result.NotEvaluatedReason);
        Assert.Equal(RegressionComparisonService.ExactBytesPolicy, result.Policy);
        Assert.Empty(result.Differences);
        Assert.Equal(0, result.TotalDifferenceCount);
        Assert.False(result.Truncated);
    }

    [Theory]
    [InlineData("a", "A")]
    [InlineData("a", " a")]
    [InlineData("a", "a ")]
    [InlineData("a/b", "a\\b")]
    [InlineData("é", "e\u0301")]
    public void Logical_names_are_exact_and_are_never_paths_or_normalized(string expected, string actual)
    {
        var result = Compare(Set(Output(expected, "same")), Set(Output(actual, "same")));
        Assert.Equal(RegressionComparisonOutcome.Failed, result.Outcome);
        Assert.Equal(2, result.TotalDifferenceCount);
        Assert.Contains(result.Differences, item => item.Kind == RegressionDifferenceKind.MissingOutput && item.Key.Name == expected);
        Assert.Contains(result.Differences, item => item.Kind == RegressionDifferenceKind.UnexpectedOutput && item.Key.Name == actual);
    }

    [Fact]
    public void Primary_and_a_named_output_called_primary_are_different()
    {
        var result = Compare(Set(Output(null, "x")), Set(Output("primary", "x")));
        Assert.Equal(2, result.TotalDifferenceCount);
        Assert.True(result.Differences[0].Key.IsPrimary);
        Assert.Equal(RegressionDifferenceKind.MissingOutput, result.Differences[0].Kind);
        Assert.Equal("primary", result.Differences[1].Key.Name);
    }

    [Theory]
    [InlineData("{\"v\":1}", "{ \"v\":1}")]
    [InlineData("{\"v\":1}", "{\"v\":1.0}")]
    [InlineData("{\"v\":1}", "{\"v\":1e0}")]
    [InlineData("é", "e\u0301")]
    [InlineData("line\n", "line\r\n")]
    [InlineData("same", "Same")]
    public void Semantically_similar_text_does_not_bypass_exact_bytes(string expected, string actual)
    {
        var result = Compare(Set(Output(null, expected)), Set(Output(null, actual)));
        var difference = Assert.Single(result.Differences);
        Assert.Equal(RegressionComparisonOutcome.Failed, result.Outcome);
        Assert.Equal(RegressionDifferenceKind.BytesDiffer, difference.Kind);
        Assert.NotNull(difference.FirstDifferentByte);
        Assert.Equal(Encoding.UTF8.GetByteCount(expected), difference.ExpectedByteCount);
        Assert.Equal(Sha256Digest.FromBytes(Encoding.UTF8.GetBytes(expected)), difference.ExpectedDigest);
    }

    [Fact]
    public void Encoding_and_empty_bytes_remain_exact()
    {
        var utf8 = RegressionOutput.Capture(RegressionOutputKey.Primary, Encoding.UTF8.GetBytes("value"));
        var utf16 = RegressionOutput.Capture(RegressionOutputKey.Primary, Encoding.Unicode.GetBytes("value"));
        Assert.Equal(RegressionComparisonOutcome.Failed, Compare(Set(utf8), Set(utf16)).Outcome);
        Assert.Equal(RegressionComparisonOutcome.Passed, Compare(Set(Output(null, "")), Set(Output(null, ""))).Outcome);
        Assert.Equal(0, Assert.Single(Compare(Set(Output(null, "")), Set(Output(null, "x"))).Differences).FirstDifferentByte);
    }

    [Theory]
    [InlineData(new byte[] { 1, 2, 3 }, new byte[] { 1, 4, 3 }, 1)]
    [InlineData(new byte[] { 1, 2 }, new byte[] { 1 }, 1)]
    [InlineData(new byte[] { 1 }, new byte[] { 1, 2 }, 1)]
    [InlineData(new byte[] { 0 }, new byte[] { 1 }, 0)]
    public void First_difference_offsets_are_zero_based(byte[] expected, byte[] actual, int offset)
    {
        var result = Compare(Set(RegressionOutput.Capture(RegressionOutputKey.Primary, expected)),
            Set(RegressionOutput.Capture(RegressionOutputKey.Primary, actual)));
        Assert.Equal(offset, Assert.Single(result.Differences).FirstDifferentByte);
    }

    [Fact]
    public void Missing_extra_and_changed_outputs_are_deterministic_with_truthful_truncation()
    {
        var expected = Set(Output("z", "old"), Output(null, "primary"), Output("missing", "x"));
        var actual = Set(Output("extra", "x"), Output(null, "changed"), Output("z", "new"));
        var all = Compare(expected, actual);
        Assert.Equal(new string?[] { null, "extra", "missing", "z" }, all.Differences.Select(item => item.Key.Name));
        Assert.Equal(new[] { RegressionDifferenceKind.BytesDiffer, RegressionDifferenceKind.UnexpectedOutput,
            RegressionDifferenceKind.MissingOutput, RegressionDifferenceKind.BytesDiffer }, all.Differences.Select(item => item.Kind));
        var clipped = Compare(expected, actual, new(differences: 1));
        Assert.Equal(RegressionComparisonOutcome.Failed, clipped.Outcome);
        Assert.Equal(4, clipped.TotalDifferenceCount);
        Assert.Single(clipped.Differences);
        Assert.True(clipped.Truncated);
        Assert.Equal(all.Differences[0], clipped.Differences[0]);
    }

    public static IEnumerable<object[]> UnsuccessfulStatuses() => Enum.GetValues<OperationStatusV1>()
        .Where(status => status != OperationStatusV1.Succeeded).Select(status => new object[] { status });

    [Theory]
    [MemberData(nameof(UnsuccessfulStatuses))]
    public void An_unsuccessful_execution_never_passes_even_with_matching_or_empty_outputs(OperationStatusV1 status)
    {
        foreach (var outputs in new[] { Set(), Set(Output(null, "same")) })
        {
            var result = service.Compare(Set(Output(null, "same")), new(status, outputs));
            Assert.Equal(RegressionComparisonOutcome.NotEvaluated, result.Outcome);
            Assert.Equal(RegressionNotEvaluatedReason.ExecutionUnsuccessful, result.NotEvaluatedReason);
            Assert.Equal(status, result.ExecutionStatus);
            Assert.Empty(result.Differences);
        }
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("weft.regression.exact-bytes.v0")]
    [InlineData("WEFT.REGRESSION.EXACT-BYTES.V1")]
    [InlineData("weft.regression.exact-bytes.v1 ")]
    public void Unknown_policy_is_not_an_implicit_exact_bytes_fallback(string policy)
    {
        var result = service.Compare(Set(Output(null, "same")), new(OperationStatusV1.Succeeded, Set(Output(null, "same"))), policy);
        Assert.Equal(RegressionComparisonOutcome.NotEvaluated, result.Outcome);
        Assert.Equal(RegressionNotEvaluatedReason.UnsupportedPolicy, result.NotEvaluatedReason);
    }

    [Fact]
    public void Missing_baseline_never_makes_an_empty_run_pass()
    {
        var result = Compare(Set(), Set());
        Assert.Equal(RegressionComparisonOutcome.NotEvaluated, result.Outcome);
        Assert.Equal(RegressionNotEvaluatedReason.MissingBaseline, result.NotEvaluatedReason);
        Assert.Equal(RegressionComparisonOutcome.Failed, Compare(Set(Output(null, "expected")), Set()).Outcome);
    }

    [Fact]
    public void Outputs_and_lists_are_defensively_owned()
    {
        var bytes = Encoding.UTF8.GetBytes("private-fixture-value");
        var output = RegressionOutput.Capture(RegressionOutputKey.Primary, bytes);
        var list = new List<RegressionOutput> { output };
        var expected = RegressionOutputSet.Capture(list);
        bytes[0] = 0;
        output.CopyBytes()[0] = 0;
        list.Clear();
        var actual = Set(Output(null, "private-fixture-value"));
        Assert.Equal(RegressionComparisonOutcome.Passed, Compare(expected, actual).Outcome);
        Assert.Throws<NotSupportedException>(() => ((IList<RegressionOutput>)expected.Outputs)[0] = Output(null, "changed"));
        var result = Compare(expected, Set(Output(null, "changed")));
        Assert.Throws<NotSupportedException>(() => ((IList<RegressionOutputDifference>)result.Differences).Clear());
        Assert.DoesNotContain("private-fixture-value", output.ToString());
        Assert.DoesNotContain("private-fixture-value", result.ToString());
        Assert.DoesNotContain("private-fixture-value", result.Differences[0].ToString());
    }

    [Fact]
    public void Duplicate_output_identity_and_invalid_names_are_rejected()
    {
        AssertFailure(RegressionComparisonFailure.InvalidInput, () => Set(Output(null, "a"), Output(null, "b")));
        AssertFailure(RegressionComparisonFailure.InvalidInput, () => Set(Output("same", "a"), Output("same", "a")));
        foreach (var name in new[] { "", " ", "line\n", "null\0", "\ud800", new string('x', 513) })
            AssertFailure(RegressionComparisonFailure.InvalidInput, () => RegressionOutputKey.Named(name));
        Assert.Equal(512, RegressionOutputKey.Named(new string('x', 512)).Name!.Length);
        Assert.Equal(RegressionComparisonOutcome.Passed, Compare(Set(Output("A", "a"), Output("a", "b")),
            Set(Output("a", "b"), Output("A", "a"))).Outcome);
    }

    [Fact]
    public void Artifact_count_individual_bytes_and_combined_bytes_have_exact_ceilings()
    {
        var maximum = Enumerable.Range(0, RegressionOutputSet.MaximumOutputCount).Select(index => Output(index.ToString(), "")).ToArray();
        Assert.Equal(RegressionOutputSet.MaximumOutputCount, RegressionOutputSet.Capture(maximum).Outputs.Count);
        AssertFailure(RegressionComparisonFailure.ResourceLimit, () => RegressionOutputSet.Capture([.. maximum, Output("extra", "")]));
        AssertFailure(RegressionComparisonFailure.ResourceLimit, () => RegressionOutput.Capture(RegressionOutputKey.Primary,
            new byte[RegressionOutput.MaximumByteCount + 1]));
        var primary = RegressionOutput.Capture(RegressionOutputKey.Primary, new byte[RegressionOutput.MaximumByteCount]);
        var named = RegressionOutput.Capture(RegressionOutputKey.Named("second"), new byte[RegressionOutput.MaximumByteCount]);
        Assert.Equal(RegressionOutputSet.MaximumByteCount, Set(primary, named).ByteCount);
        AssertFailure(RegressionComparisonFailure.ResourceLimit, () => Set(primary, named, Output("extra", "x")));
    }

    [Fact]
    public void Compared_byte_budget_includes_both_sides_and_cannot_be_skipped_by_digest_equality()
    {
        var values = Set(Output(null, "same"));
        Assert.Equal(RegressionComparisonOutcome.Passed, Compare(values, values, new(comparedBytes: 8)).Outcome);
        AssertFailure(RegressionComparisonFailure.ResourceLimit, () => Compare(values, values, new(comparedBytes: 7)));
        AssertFailure(RegressionComparisonFailure.ResourceLimit, () => Compare(values, values, new(comparedBytes: 0)));
        Assert.Equal(RegressionComparisonOutcome.Passed, Compare(values, values).Outcome);
    }

    [Fact]
    public void Byte_offsets_and_work_limits_cross_chunk_boundaries_exactly()
    {
        var expectedBytes = new byte[65_537];
        var actualBytes = new byte[65_537];
        actualBytes[^1] = 1;
        var expected = Set(RegressionOutput.Capture(RegressionOutputKey.Primary, expectedBytes));
        var actual = Set(RegressionOutput.Capture(RegressionOutputKey.Primary, actualBytes));
        var result = Compare(expected, actual, new(comparedBytes: 131_074));
        Assert.Equal(65_536, Assert.Single(result.Differences).FirstDifferentByte);
        AssertFailure(RegressionComparisonFailure.ResourceLimit, () => Compare(expected, actual, new(comparedBytes: 131_073)));
    }

    [Fact]
    public void Clipped_difference_lists_do_not_skip_later_comparison_work()
    {
        var expected = Set(Output(null, "a"), Output("later", "same"));
        var actual = Set(Output(null, "b"), Output("later", "same"));
        AssertFailure(RegressionComparisonFailure.ResourceLimit, () => Compare(expected, actual,
            new(comparedBytes: 2, differences: 1)));
        var complete = Compare(expected, actual, new(comparedBytes: 10, differences: 1));
        Assert.Equal(RegressionComparisonOutcome.Failed, complete.Outcome);
        Assert.Equal(1, complete.TotalDifferenceCount);
        Assert.False(complete.Truncated);
    }

    [Fact]
    public void Cancellation_precedes_work_and_does_not_poison_a_later_comparison()
    {
        var values = Set(Output(null, "same"));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => service.Compare(values,
            new(OperationStatusV1.Succeeded, values), cancellationToken: cancelled.Token));
        Assert.Throws<OperationCanceledException>(() => RegressionOutput.Capture(RegressionOutputKey.Primary, [1], cancelled.Token));
        Assert.Throws<OperationCanceledException>(() => RegressionOutputSet.Capture([Output(null, "same")], cancelled.Token));
        Assert.Equal(RegressionComparisonOutcome.Passed, Compare(values, values).Outcome);
    }

    [Fact]
    public void Invalid_limits_and_execution_status_fail_closed()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RegressionComparisonLimits(comparedBytes: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RegressionComparisonLimits(comparedBytes: RegressionComparisonLimits.MaximumComparedBytes + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RegressionComparisonLimits(differences: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RegressionComparisonLimits(differences: RegressionComparisonLimits.MaximumDifferences + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RegressionExecutionObservation((OperationStatusV1)(-1), Set()));
    }

    private RegressionComparisonResult Compare(RegressionOutputSet expected, RegressionOutputSet actual, RegressionComparisonLimits? limits = null) =>
        service.Compare(expected, new(OperationStatusV1.Succeeded, actual), limits: limits);
    private static RegressionOutputSet Set(params RegressionOutput[] values) => RegressionOutputSet.Capture(values);
    private static RegressionOutput Output(string? name, string text) => RegressionOutput.Capture(
        name is null ? RegressionOutputKey.Primary : RegressionOutputKey.Named(name), Encoding.UTF8.GetBytes(text));
    private static void AssertFailure(RegressionComparisonFailure expected, Action action) =>
        Assert.Equal(expected, Assert.Throws<RegressionComparisonException>(action).Failure);
}
