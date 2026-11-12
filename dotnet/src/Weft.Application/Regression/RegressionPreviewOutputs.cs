using System.Globalization;

namespace Weft.Application.Regression;

/// <summary>Strict conversion for the case's pinned Ferrule preview profile, not arbitrary backend names.</summary>
internal static class RegressionPreviewOutputs
{
    internal static RegressionOutputSet Read(RegressionCaseVersion value, MappingPreviewResult result, CancellationToken token)
        => Read(value, new(value.Revision, value.NativeDigest), result, token);

    internal static RegressionOutputSet Read(RegressionCaseVersion value, RegressionEvaluationTarget target,
        MappingPreviewResult result, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(result);
        var observed = result.Provenance;
        if (observed is null || observed.Scope != target.Revision || observed.NativeDigest != target.NativeDigest ||
            observed.BackendId != value.Runtime.BackendId || observed.EngineRevision != value.Runtime.EngineRevision ||
            observed.BackendBuild != value.Runtime.BackendBuild || observed.AdapterBuild != value.Runtime.AdapterBuild ||
            result.Profile != value.Runtime.Profile || !Enum.IsDefined(result.Status) || result.Artifacts is null || result.RecordsWritten < 0)
            throw Invalid();
        if (result.Status != MappingPreviewStatus.Succeeded)
        {
            if (result.Artifacts.Count != 0 || result.RecordsWritten != 0 || result.Failure is null) throw Invalid();
            return RegressionOutputSet.Capture([], token);
        }
        if (!observed.Confirmed || result.Failure is not null || result.Artifacts.Count is < 1 ||
            result.Artifacts.Count > value.Replay.Limits.MaximumArtifacts) throw Invalid();

        var outputs = new List<RegressionOutput>();
        var boundaries = new HashSet<string>(StringComparer.Ordinal);
        var total = 0;
        var primary = 0;
        foreach (var output in result.Artifacts)
        {
            token.ThrowIfCancellationRequested();
            if (output is null || !boundaries.Add(output.BoundaryId) ||
                (value.Runtime.Profile switch
                {
                    CsvDocumentPolicy.Profile => !CsvDocumentPolicy.Admits(output.CsvDialect, output.MediaType),
                    PreviewPolicy.Profile => output.CsvDialect is not null || output.MediaType != PreviewPolicy.MediaType,
                    _ => output.CsvDialect is not null || !DocumentPreviewPolicy.AdmitsMediaType(output.MediaType)
                }) ||
                output.ByteCount > value.Replay.Limits.MaximumOutputBytes - total) throw Invalid();
            total += output.ByteCount;
            RegressionOutputKey key;
            if (output.BoundaryId == "primary-output")
            {
                if (value.Replay.Target.Kind == PreviewTargetKind.Named || ++primary > 1) throw Invalid();
                key = RegressionOutputKey.Primary;
            }
            else
            {
                const string prefix = "named-output:";
                if (value.Replay.Target.Kind == PreviewTargetKind.Primary || output.TargetNameTruncated ||
                    !output.BoundaryId.StartsWith(prefix, StringComparison.Ordinal) ||
                    !ulong.TryParse(output.BoundaryId.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var index) ||
                    output.BoundaryId != prefix + index.ToString(CultureInfo.InvariantCulture)) throw Invalid();
                RegressionCaseValues.Name(output.TargetName);
                if (value.Replay.Target.Kind == PreviewTargetKind.Named && output.TargetName != value.Replay.Target.Name) throw Invalid();
                key = RegressionOutputKey.Named(output.TargetName);
            }
            if (value.Runtime.Profile == CsvDocumentPolicy.Profile &&
                (key != RegressionOutputKey.Primary || value.Expected.Outputs.Single().CsvDialect != output.CsvDialect)) throw Invalid();
            outputs.Add(RegressionOutput.Capture(key, output.Bytes, token, output.MediaType, output.CsvDialect));
        }
        if (value.Replay.Target.Kind == PreviewTargetKind.Named ? outputs.Count != 1 : primary != 1) throw Invalid();
        return RegressionOutputSet.Capture(outputs, token);
    }

    private static InvalidOperationException Invalid() => new("Regression preview result refused.");
}
