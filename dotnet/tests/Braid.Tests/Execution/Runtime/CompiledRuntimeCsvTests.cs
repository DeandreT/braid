using System.Buffers.Binary;
using System.Text;
using Braid.Application;
using Braid.Application.Regression;
using Braid.Deployment;

namespace Braid.Tests;

public sealed class CompiledRuntimeCsvTests
{
    [Theory]
    [InlineData(",")]
    [InlineData("\t")]
    public void Empty_csv_frames_are_explicit_and_dialect_bound(string delimiter)
    {
        var dialect = BuildCsvContractTests.Dialect(delimiter);
        var intent = BuildCsvContractTests.Intent(dialect);
        var artifact = new BuildBytes([1, 2, 3], intent.Profile.MaximumArtifactBytes);
        var reference = BuildArtifactReference.Restore(intent, artifact.Digest, artifact.Length);
        var replay = new RegressionReplay(PreviewTargetSelection.All, "2026-10-30T12:00:00Z", PreviewLimits.Default,
            new(null, [], dialect), [], []);
        var header = CompiledRuntimeProtocol.For(reference, replay);
        Assert.Equal(CompiledRuntimeProtocol.CsvProtocol, header.ProtocolVersion);
        var framed = CompiledRuntimeProtocol.EncodeRequest(header, intent, artifact, replay);
        Assert.Equal(0, BinaryPrimitives.ReadInt32BigEndian(framed.AsSpan(framed.Length - 4)));
        var invocation = CompiledRuntimeProtocol.DecodeRequest(framed);
        Assert.Empty(invocation.Replay.Primary.CopyBytes());
        Assert.Equal(dialect, invocation.Replay.Primary.CsvDialect);
        Assert.Throws<InvalidOperationException>(() => CompiledRuntimeProtocol.DecodeRequest(framed[..^4]));
        var outputs = RegressionOutputSet.Capture([RegressionOutput.Capture(RegressionOutputKey.Primary, [],
            mediaType: BuildWorkerProtocol.MediaType(intent.Boundaries[1].Format), csvDialect: dialect)]);
        var wire = CompiledRuntimeProtocol.EncodeResponse(invocation, "succeeded", outputs);
        var restored = CompiledRuntimeProtocol.DecodeResponse(wire, header, intent);
        Assert.Empty(Assert.Single(restored.Outputs!.Outputs).CopyBytes());
        Assert.Equal(dialect, Assert.Single(restored.Outputs.Outputs).CsvDialect);
        var relabelled = RegressionOutputSet.Capture([RegressionOutput.Capture(RegressionOutputKey.Primary, [],
            mediaType: BuildWorkerProtocol.MediaType(intent.Boundaries[1].Format), csvDialect: dialect with { HasHeaderRow = true })]);
        Assert.Throws<InvalidOperationException>(() => CompiledRuntimeProtocol.EncodeResponse(invocation, "succeeded", relabelled));
    }

    [Theory]
    [InlineData("A,\"one\r\ntwo\"\n", CsvDocumentBoundResult.Accepted)]
    [InlineData("A,\"one\"\"two\"\n", CsvDocumentBoundResult.Accepted)]
    [InlineData("A,\"unfinished", CsvDocumentBoundResult.Invalid)]
    public void Output_bounds_follow_quoted_cells_without_transforming_bytes(string text, CsvDocumentBoundResult expected)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var original = bytes.ToArray();
        Assert.Equal(expected, CsvDocumentBounds.Check(bytes, BuildCsvContractTests.Dialect(), "text/csv"));
        Assert.Equal(original, bytes);
    }

    [Fact]
    public void Output_native_cell_column_and_record_ceilings_are_independent_of_outer_bytes()
    {
        var dialect = BuildCsvContractTests.Dialect();
        Assert.Equal(CsvDocumentBoundResult.Limit, CsvDocumentBounds.Check(Encoding.UTF8.GetBytes(new string('x', CsvDocumentPolicy.MaximumCellBytes + 1)), dialect, "text/csv"));
        Assert.Equal(CsvDocumentBoundResult.Limit, CsvDocumentBounds.Check(Encoding.UTF8.GetBytes(new string(',', CsvDocumentPolicy.MaximumColumns)), dialect, "text/csv"));
        Assert.Equal(CsvDocumentBoundResult.Limit, CsvDocumentBounds.Check(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("x\n", CsvDocumentPolicy.MaximumRecords + 1))), dialect, "text/csv"));
        Assert.Equal(CsvDocumentBoundResult.Invalid, CsvDocumentBounds.Check([0xff], dialect, "text/csv"));
    }
}
