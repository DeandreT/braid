namespace Braid.Tests;

public sealed class HistoricalRegressionSourceTests
{
    [Fact]
    public void Complete_legacy_case_and_run_declarations_parse_without_a_database() =>
        Assert.Equal((6, 11), HistoricalRegressionAccess.SourceCounts);
}
