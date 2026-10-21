using Weft.Application;
using Weft.Application.Regression;

namespace Weft.Tests;

public sealed class RegressionRunResultCodecTests
{
    [Fact]
    public void Observation_encoding_reopens_exact_bytes_and_recomputes_mismatch()
    {
        var value = new BuildQualificationTests.Fixture().Case;
        var changed = RegressionOutputSet.Capture([RegressionOutput.Capture(RegressionOutputKey.Primary, "{}"u8)]);
        var id = Guid.NewGuid(); var codec = new RegressionRunResultCodec();
        var bytes = codec.EncodeObservation(id, value, MappingPreviewStatus.Succeeded, changed);
        var reopened = codec.Decode(id, value, bytes);
        Assert.Equal(RegressionComparisonOutcome.Failed, reopened.Comparison!.Outcome);
        Assert.Equal("{}"u8.ToArray(), Assert.Single(reopened.Outputs!.Outputs).CopyBytes());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Observation_encoding_rejects_success_without_output_and_failure_with_output(bool succeeded)
    {
        var value = new BuildQualificationTests.Fixture().Case;
        Assert.Throws<InvalidOperationException>(() => new RegressionRunResultCodec().EncodeObservation(Guid.NewGuid(), value,
            succeeded ? MappingPreviewStatus.Succeeded : MappingPreviewStatus.BackendFailure,
            succeeded ? RegressionOutputSet.Capture([]) : value.Expected));
    }

    [Fact]
    public void Unknown_status_and_cancellation_do_not_encode_a_result()
    {
        var value = new BuildQualificationTests.Fixture().Case;
        Assert.Throws<InvalidOperationException>(() => new RegressionRunResultCodec().EncodeObservation(Guid.NewGuid(), value,
            (MappingPreviewStatus)int.MaxValue, value.Expected));
        using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => new RegressionRunResultCodec().EncodeObservation(Guid.NewGuid(), value,
            MappingPreviewStatus.Succeeded, value.Expected, stop.Token));
    }
}
