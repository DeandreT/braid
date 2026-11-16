using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Braid.Application;
using Braid.Contracts.Projections;
using Braid.Domain;

namespace Braid.Tests;

public sealed class RevisionReportResponseSerializerTests
{
    [Fact]
    public void ActualCompleteWrapperOverheadIsChargedWithinExactByteBudget()
    {
        var value = new ResponseWrapper("wrapper.v1", new StringLiteralValue("literal"), "tail");
        const string expected = "{\"schemaVersion\":\"wrapper.v1\",\"literal\":{\"kind\":\"string\",\"value\":\"literal\"},\"tail\":\"tail\"}";
        var bytes = Encoding.UTF8.GetBytes(expected);
        Assert.Equal(bytes, RevisionReportResponseSerializer.Serialize(value, bytes.Length));
        Assert.Equal(RevisionReportValidationFailure.ResourceLimit, Assert.Throws<RevisionReportValidationException>(() =>
            RevisionReportResponseSerializer.Serialize(value, bytes.Length - 1)).Failure);
        var innerOnly = RevisionReportResponseSerializer.Serialize<LiteralValue>(value.Literal, 1024);
        Assert.True(innerOnly.Length < bytes.Length);
        Assert.Equal(RevisionReportValidationFailure.ResourceLimit, Assert.Throws<RevisionReportValidationException>(() =>
            RevisionReportResponseSerializer.Serialize(value, innerOnly.Length)).Failure);
    }

    [Fact]
    public void ReturnedSerializationBytesAreOwnedAndPublicProjectionOptionsRemainUnchanged()
    {
        var value = new ResponseWrapper("wrapper.v1", new ExactNumberLiteralValue(ExactNumberFlavor.BinaryFloat, "7ff8000000000042"), "tail");
        var original = RevisionReportResponseSerializer.Serialize(value, 1024);
        var returned = RevisionReportResponseSerializer.Serialize(value, 1024);
        returned.AsSpan().Fill(0);
        Assert.Equal(original, RevisionReportResponseSerializer.Serialize(value, 1024));
        Assert.True(ProjectionJson.CreateOptions().PropertyNameCaseInsensitive);
        Assert.Contains("7ff8000000000042", Encoding.UTF8.GetString(original), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(RevisionReportPolicy.MaximumResponseByteLimit + 1)]
    public void CallersCannotRaiseOrRemoveTheHardSerializationCeiling(int maximumBytes) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => RevisionReportResponseSerializer.Serialize("x", maximumBytes));

    [Fact]
    public void SerializationObservesCancellationBeforeWorkAndAfterConverterSerializationBegins()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => RevisionReportResponseSerializer.Serialize("x", 10, canceled.Token));
        using var during = new CancellationTokenSource();
        var response = new CancelDuringWrite(during);
        Assert.Throws<OperationCanceledException>(() => RevisionReportResponseSerializer.Serialize(response, 4096, during.Token));
        Assert.True(response.WriterFlushFinished);
    }

    [Fact]
    public void BoundedStreamRefusesLimitPlusOneBeforeGrowthAndCopiesWithCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        using var stream = new RevisionReportResponseSerializer.BoundedWriteStream(4, cancellation.Token);
        stream.Write(new byte[] { 1, 2, 3, 4 });
        Assert.Equal(4, stream.Length);
        Assert.Equal(RevisionReportValidationFailure.ResourceLimit,
            Assert.Throws<RevisionReportValidationException>(() => stream.WriteByte(5)).Failure);
        Assert.Equal(4, stream.Length);
        var returned = stream.CopyBytes();
        returned[0] = 255;
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, stream.CopyBytes());
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => stream.WriteByte(5));
        Assert.Throws<OperationCanceledException>(() => stream.CopyBytes());
    }

    private sealed record ResponseWrapper(string SchemaVersion, LiteralValue Literal, string Tail);

    [JsonConverter(typeof(CancelDuringWriteConverter))]
    private sealed class CancelDuringWrite(CancellationTokenSource cancellation)
    {
        internal CancellationTokenSource Cancellation { get; } = cancellation;
        internal bool WriterFlushFinished { get; set; }
    }

    private sealed class CancelDuringWriteConverter : JsonConverter<CancelDuringWrite>
    {
        public CancelDuringWriteConverter() { }
        public override CancelDuringWrite Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, CancelDuringWrite value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("actual", "first flush");
            writer.Flush();
            value.WriterFlushFinished = true;
            value.Cancellation.Cancel();
            writer.WriteEndObject();
            writer.Flush();
        }
    }
}
