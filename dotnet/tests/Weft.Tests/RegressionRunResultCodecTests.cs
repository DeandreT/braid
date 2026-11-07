using Weft.Application;
using Weft.Application.Regression;

namespace Weft.Tests;

public sealed class RegressionRunResultCodecTests
{
    [Theory]
    [InlineData("application/edi-x12")]
    [InlineData("application/edifact")]
    public void Document_result_reopens_media_and_recomputes_with_the_saved_options(string media)
    {
        var original = new BuildQualificationTests.Fixture().Case;
        var text = media == "application/edi-x12" ? DocumentPreviewTestData.X12 : DocumentPreviewTestData.Edifact;
        var output = RegressionOutput.Capture(RegressionOutputKey.Primary, System.Text.Encoding.UTF8.GetBytes(text), mediaType: media);
        var value = new RegressionCaseVersion(original.CaseId, original.Version, original.Owner, original.Name, original.Revision,
            original.NativeDigest, new(original.Runtime.BackendId, original.Runtime.EngineRevision, original.Runtime.BackendBuild,
                "document-adapter", DocumentPreviewPolicy.Profile), original.Replay, RegressionComparisonService.EdiSegmentsPolicy,
            RegressionOutputSet.Capture([output]), new(false, true, true));
        var observed = RegressionOutputSet.Capture([RegressionOutput.Capture(RegressionOutputKey.Primary,
            System.Text.Encoding.UTF8.GetBytes(text.Replace("WEFT-330", "DIFFERENT", StringComparison.Ordinal)), mediaType: media)]);
        var codec = new RegressionRunResultCodec(); var id = Guid.NewGuid();
        var encoded = codec.EncodeObservation(id, value, MappingPreviewStatus.Succeeded, observed);
        var restored = codec.Decode(id, value, encoded);
        Assert.Equal(media, Assert.Single(restored.Outputs!.Outputs).MediaType);
        Assert.Equal(RegressionComparisonOutcome.Failed, restored.Comparison!.Outcome);
        Assert.NotNull(Assert.Single(restored.Comparison.Differences).Location);
        Assert.Equal(encoded, codec.Encode(id, new RegressionCaseEnvelopeCodec().Encode(value).Reference, restored));
    }

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
