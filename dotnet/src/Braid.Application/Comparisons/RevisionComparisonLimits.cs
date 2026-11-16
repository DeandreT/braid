namespace Braid.Application.Comparisons;

public enum RevisionComparisonFailure { InvalidInput = 1, ResourceLimit = 2 }

public sealed class RevisionComparisonException : Exception
{
    internal RevisionComparisonException(RevisionComparisonFailure failure)
        : base(failure == RevisionComparisonFailure.ResourceLimit
            ? "The revision comparison exceeds a limit." : "The revision comparison input is inconsistent.") => Failure = failure;
    public RevisionComparisonFailure Failure { get; }
    public override string ToString() => "Revision comparison failure.";
}

public sealed class RevisionComparisonLimits
{
    public RevisionComparisonLimits(int inputItems = 32768, int workUnits = 1000000,
        int outputItems = 1024, int candidatesPerSide = 8, int outputUtf8Bytes = 1048576)
    {
        InputItems = Limit(inputItems, 32768, nameof(inputItems));
        WorkUnits = Limit(workUnits, 1000000, nameof(workUnits));
        OutputItems = Limit(outputItems, 1024, nameof(outputItems));
        CandidatesPerSide = Limit(candidatesPerSide, 8, nameof(candidatesPerSide));
        OutputUtf8Bytes = Limit(outputUtf8Bytes, 1048576, nameof(outputUtf8Bytes));
    }
    public int InputItems { get; }
    public int WorkUnits { get; }
    public int OutputItems { get; }
    public int CandidatesPerSide { get; }
    public int OutputUtf8Bytes { get; }
    private static int Limit(int value, int maximum, string name) => value >= 1 && value <= maximum
        ? value : throw new ArgumentOutOfRangeException(name, "A comparison limit is outside its supported bounds.");
    public override string ToString() => "Revision comparison limits.";
}
