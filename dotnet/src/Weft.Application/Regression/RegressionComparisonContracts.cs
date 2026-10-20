using Weft.Contracts.Operations;
using Weft.Domain;

namespace Weft.Application.Regression;

public enum RegressionComparisonFailure { InvalidInput, ResourceLimit }

public sealed class RegressionComparisonException : Exception
{
    public RegressionComparisonException(RegressionComparisonFailure failure) : base("Regression comparison refused.")
    {
        if (!Enum.IsDefined(failure)) throw new ArgumentOutOfRangeException(nameof(failure));
        Failure = failure;
    }

    public RegressionComparisonFailure Failure { get; }
}

public enum RegressionComparisonOutcome { Passed, Failed, NotEvaluated }
public enum RegressionNotEvaluatedReason { None, UnsupportedPolicy, ExecutionUnsuccessful, MissingBaseline }
public enum RegressionDifferenceKind { MissingOutput, UnexpectedOutput, BytesDiffer }

/// <summary>Execution facts supplied by a trusted run reader; this type does not authenticate them.</summary>
public sealed class RegressionExecutionObservation
{
    public RegressionExecutionObservation(OperationStatusV1 status, RegressionOutputSet outputs)
    {
        if (!Enum.IsDefined(status)) throw new ArgumentOutOfRangeException(nameof(status));
        ArgumentNullException.ThrowIfNull(outputs);
        Status = status;
        Outputs = outputs;
    }

    public OperationStatusV1 Status { get; }
    public RegressionOutputSet Outputs { get; }
    public override string ToString() => nameof(RegressionExecutionObservation);
}

/// <summary>No byte excerpts are retained in a difference or emitted to diagnostics.</summary>
public sealed record RegressionOutputDifference(RegressionOutputKey Key, RegressionDifferenceKind Kind,
    int? ExpectedByteCount, int? ActualByteCount, Sha256Digest? ExpectedDigest, Sha256Digest? ActualDigest,
    int? FirstDifferentByte)
{
    public override string ToString() => nameof(RegressionOutputDifference);
}

public sealed class RegressionComparisonLimits
{
    public const int MaximumComparedBytes = 64 * 1024 * 1024;
    public const int MaximumDifferences = 2 * RegressionOutputSet.MaximumOutputCount;

    public RegressionComparisonLimits(int comparedBytes = MaximumComparedBytes, int differences = 64)
    {
        if (comparedBytes is < 0 or > MaximumComparedBytes)
            throw new ArgumentOutOfRangeException(nameof(comparedBytes), "Invalid regression comparison limits.");
        if (differences is < 1 or > MaximumDifferences)
            throw new ArgumentOutOfRangeException(nameof(differences), "Invalid regression comparison limits.");
        ComparedBytes = comparedBytes;
        Differences = differences;
    }

    public int ComparedBytes { get; }
    public int Differences { get; }
    public override string ToString() => nameof(RegressionComparisonLimits);
}

public sealed class RegressionComparisonResult
{
    internal RegressionComparisonResult(string policy, RegressionComparisonOutcome outcome,
        RegressionNotEvaluatedReason notEvaluatedReason, OperationStatusV1 executionStatus,
        int expectedOutputCount, int actualOutputCount, int totalDifferenceCount,
        IReadOnlyList<RegressionOutputDifference> differences)
    {
        Policy = policy;
        Outcome = outcome;
        NotEvaluatedReason = notEvaluatedReason;
        ExecutionStatus = executionStatus;
        ExpectedOutputCount = expectedOutputCount;
        ActualOutputCount = actualOutputCount;
        TotalDifferenceCount = totalDifferenceCount;
        Differences = differences;
    }

    public string Policy { get; }
    public RegressionComparisonOutcome Outcome { get; }
    public RegressionNotEvaluatedReason NotEvaluatedReason { get; }
    public OperationStatusV1 ExecutionStatus { get; }
    public int ExpectedOutputCount { get; }
    public int ActualOutputCount { get; }
    public int TotalDifferenceCount { get; }
    public IReadOnlyList<RegressionOutputDifference> Differences { get; }
    public bool Truncated => Differences.Count < TotalDifferenceCount;
    public override string ToString() => $"{nameof(RegressionComparisonResult)}:{Outcome}";
}
