using System.IO.Compression;
using Braid.Application;
using Braid.Application.Regression;
using Braid.Domain;

namespace Braid.Tests;

public sealed class NativeDocumentRecoveryTests
{
    [Theory]
    [InlineData("flextext")]
    [InlineData("xlsx")]
    [InlineData("protobuf")]
    [InlineData("xbrl")]
    public void Original_request_case_and_observation_keep_exact_bytes_and_settings(string key)
    {
        var format = NativeDocumentFormat.All.Single(f => f.Key == key);
        var scope = new RevisionScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var bytes = key switch { "flextext" => [], "xlsx" => Package(), "protobuf" => new byte[] { 10, 2, 0, 255 }, _ => "<instance/>\r\n"u8.ToArray() };
        var settings = new NativeDocumentSettings(format.SettingsSchema, "{\"side\":\"input\"}");
        var outputSettings = new NativeDocumentSettings(format.SettingsSchema, "{\"side\":\"output\"}");
        var input = new PreviewInputDocument(null, bytes, documentSettings: settings);
        var replay = new RegressionReplay(PreviewTargetSelection.Primary, "2026-11-18T00:00:00Z", PreviewLimits.Default, input, [], [], profile: format.Execution);
        var submission = new PreviewRunSubmission(new RevisionPreviewSubject(scope), Guid.NewGuid(), replay.Target, input, [], []) { Profile = format.Execution };
        var request = PreviewRequestDocument.Encode(submission, replay.Limits);
        var declared = PreviewRequestDocument.Decode(request);
        Assert.Equal(format.Execution, declared.Profile);
        Assert.Equal(settings, declared.Inputs.Single().DocumentSettings);
        Assert.Equal(input.Digest, declared.Inputs.Single().Digest);
        Assert.NotEqual(request, PreviewRequestDocument.Encode(submission with { Primary = new(null, bytes, documentSettings: outputSettings) }, replay.Limits));
        var baseline = RegressionOutputSet.Capture([RegressionOutput.Capture(RegressionOutputKey.Primary, bytes, mediaType: format.MediaType, documentSettings: outputSettings)]);
        var value = new RegressionCaseVersion(Guid.NewGuid(), 1, new PrincipalId(Guid.NewGuid()), "native", scope, Sha256Digest.FromBytes([1]),
            new("ferrule", "pin", "image", format.AdapterBuild, format.Profile), replay, RegressionComparisonService.ExactBytesPolicy, baseline);
        var codec = new RegressionCaseEnvelopeCodec();
        var saved = codec.Encode(value);
        var loaded = codec.Decode(saved.Reference, saved.CopyBytes());
        Assert.Equal(bytes, loaded.Replay.Primary.CopyBytes());
        Assert.Equal(settings, loaded.Replay.Primary.DocumentSettings);
        Assert.Equal(bytes, loaded.Expected.Outputs.Single().CopyBytes());
        Assert.Equal(outputSettings, loaded.Expected.Outputs.Single().DocumentSettings);
        var run = Guid.NewGuid();
        var results = new RegressionRunResultCodec();
        var observation = results.EncodeObservation(run, loaded, MappingPreviewStatus.Succeeded, baseline);
        Assert.Equal(bytes, results.Decode(run, loaded, observation).Outputs!.Outputs.Single().CopyBytes());
        var corrupt = saved.CopyBytes();
        corrupt[^1] ^= 1;
        Assert.Throws<RegressionCaseException>(() => codec.Decode(saved.Reference, corrupt));
    }

    // Managed storage admission uses the ZIP directory only, not a workbook parser.
    private static byte[] Package()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        using (var part = zip.CreateEntry("xl/workbook.xml").Open()) part.Write("<workbook/>"u8);
        return stream.ToArray();
    }
}
