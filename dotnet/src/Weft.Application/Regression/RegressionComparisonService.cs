using Weft.Contracts.Operations;

namespace Weft.Application.Regression;

/// <summary>Pure, bounded output comparison. It neither runs mappings nor admits a revision or an actor.</summary>
public sealed class RegressionComparisonService
{
    public const string ExactBytesPolicy = "weft.regression.exact-bytes.v1";
    private const int ChunkBytes = 64 * 1024;

    public RegressionComparisonResult Compare(RegressionOutputSet expected, RegressionExecutionObservation actual,
        string policy = ExactBytesPolicy, RegressionComparisonLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        if (policy is null || policy.Length is 0 or > 128 || policy.Any(char.IsControl))
            throw new RegressionComparisonException(RegressionComparisonFailure.InvalidInput);
        if (!StringComparer.Ordinal.Equals(policy, ExactBytesPolicy))
            return NotEvaluated(RegressionNotEvaluatedReason.UnsupportedPolicy);
        if (actual.Status != OperationStatusV1.Succeeded)
            return NotEvaluated(RegressionNotEvaluatedReason.ExecutionUnsuccessful);
        if (expected.Outputs.Count == 0)
            return NotEvaluated(RegressionNotEvaluatedReason.MissingBaseline);

        var effective = limits ?? new();
        var remainingBytes = effective.ComparedBytes;
        var emitted = new List<RegressionOutputDifference>();
        var total = 0;
        var expectedIndex = 0;
        var actualIndex = 0;
        while (expectedIndex < expected.Outputs.Count || actualIndex < actual.Outputs.Outputs.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var left = expectedIndex < expected.Outputs.Count ? expected.Outputs[expectedIndex] : null;
            var right = actualIndex < actual.Outputs.Outputs.Count ? actual.Outputs.Outputs[actualIndex] : null;
            var order = left is null ? 1 : right is null ? -1 : StringComparer.Ordinal.Compare(left.Key.Name, right.Key.Name);
            if (order < 0)
            {
                Add(new(left!.Key, RegressionDifferenceKind.MissingOutput, left.ByteCount, null, left.Digest, null, null));
                expectedIndex++;
            }
            else if (order > 0)
            {
                Add(new(right!.Key, RegressionDifferenceKind.UnexpectedOutput, null, right.ByteCount, null, right.Digest, null));
                actualIndex++;
            }
            else
            {
                var offset = FirstDifference(left!, right!, ref remainingBytes, cancellationToken);
                if (offset is not null)
                    Add(new(left!.Key, RegressionDifferenceKind.BytesDiffer, left.ByteCount, right!.ByteCount,
                        left.Digest, right.Digest, offset));
                expectedIndex++;
                actualIndex++;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(policy, total == 0 ? RegressionComparisonOutcome.Passed : RegressionComparisonOutcome.Failed,
            RegressionNotEvaluatedReason.None, actual.Status, expected.Outputs.Count, actual.Outputs.Outputs.Count,
            total, Array.AsReadOnly(emitted.ToArray()));

        void Add(RegressionOutputDifference difference)
        {
            total++;
            if (emitted.Count < effective.Differences) emitted.Add(difference);
        }

        RegressionComparisonResult NotEvaluated(RegressionNotEvaluatedReason reason)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(policy, RegressionComparisonOutcome.NotEvaluated, reason, actual.Status,
                expected.Outputs.Count, actual.Outputs.Outputs.Count, 0, Array.Empty<RegressionOutputDifference>());
        }
    }

    private static int? FirstDifference(RegressionOutput left, RegressionOutput right, ref int remainingBytes,
        CancellationToken token)
    {
        var shared = Math.Min(left.ByteCount, right.ByteCount);
        for (var offset = 0; offset < shared;)
        {
            token.ThrowIfCancellationRequested();
            var size = Math.Min(ChunkBytes, shared - offset);
            var cost = size * 2;
            if (cost > remainingBytes)
                throw new RegressionComparisonException(RegressionComparisonFailure.ResourceLimit);
            remainingBytes -= cost;
            var expected = left.Span.Slice(offset, size);
            var actual = right.Span.Slice(offset, size);
            if (!expected.SequenceEqual(actual))
            {
                for (var index = 0; index < size; index++)
                    if (expected[index] != actual[index]) return offset + index;
            }
            offset += size;
        }
        return left.ByteCount == right.ByteCount ? null : shared;
    }

    public override string ToString() => nameof(RegressionComparisonService);
}
