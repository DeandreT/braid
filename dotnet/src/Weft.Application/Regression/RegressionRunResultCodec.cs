using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Weft.Contracts.Operations;

namespace Weft.Application.Regression;

/// <summary>Canonical private completion document; reopening recomputes comparison from the pinned case.</summary>
public sealed class RegressionRunResultCodec
{
    public const int MaximumBytes = 12 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 8
    };

    public byte[] Encode(Guid runId, RegressionCaseReference reference, RegressionCaseExecution execution, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(execution);
        if (runId == Guid.Empty) throw Invalid();
        var document = new Document("weft.regression-run-result.v1", runId.ToString("D"), reference.ContentDigest.Value,
            execution.Outcome, execution.PreviewStatus, execution.Outputs?.Outputs.Select(output => new Output(output.Key.Name,
                Convert.ToBase64String(output.CopyBytes()).TrimEnd('=').Replace('+', '-').Replace('/', '_'))).ToArray());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, Json);
        if (bytes.Length > MaximumBytes) throw Invalid();
        token.ThrowIfCancellationRequested();
        return bytes;
    }

    public RegressionCaseExecution Decode(Guid runId, RegressionCaseVersion value, ReadOnlyMemory<byte> input, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(value);
        if (runId == Guid.Empty || input.Length is < 1 or > MaximumBytes) throw Invalid();
        var bytes = input.ToArray();
        try
        {
            _ = new UTF8Encoding(false, true).GetCharCount(bytes);
            var doc = JsonSerializer.Deserialize<Document>(bytes, Json) ?? throw Invalid();
            var reference = new RegressionCaseEnvelopeCodec().Encode(value, token).Reference;
            if (doc.Schema != "weft.regression-run-result.v1" || doc.RunId != runId.ToString("D") || doc.CaseDigest != reference.ContentDigest.Value ||
                !Enum.IsDefined(doc.Outcome)) throw Invalid();
            RegressionCaseExecution result;
            if (doc.Outcome != RegressionCaseExecutionOutcome.Completed)
            {
                if (doc.PreviewStatus is not null || doc.Outputs is not null) throw Invalid();
                result = RegressionCaseExecution.Refused(doc.Outcome);
            }
            else
            {
                if (doc.PreviewStatus is not { } status || !Enum.IsDefined(status) || doc.Outputs is null ||
                    doc.Outputs.Length > value.Replay.Limits.MaximumArtifacts) throw Invalid();
                var outputs = new List<RegressionOutput>();
                var remaining = value.Replay.Limits.MaximumOutputBytes;
                foreach (var output in doc.Outputs)
                {
                    token.ThrowIfCancellationRequested();
                    if (output is null || output.Content.Length > (remaining + 2L) / 3 * 4 ||
                        output.Content.Any(c => c is not (>= 'a' and <= 'z') and not (>= 'A' and <= 'Z') and not (>= '0' and <= '9') and not '-' and not '_')) throw Invalid();
                    var text = output.Content.Replace('-', '+').Replace('_', '/');
                    var raw = Convert.FromBase64String(text.PadRight((text.Length + 3) / 4 * 4, '='));
                    if (raw.Length > remaining) throw Invalid();
                    remaining -= raw.Length;
                    var key = output.Name is null ? RegressionOutputKey.Primary : RegressionOutputKey.Named(output.Name);
                    if (output.Name is not null) RegressionCaseValues.Name(output.Name);
                    outputs.Add(RegressionOutput.Capture(key, raw, token));
                }
                var set = RegressionOutputSet.Capture(outputs, token);
                if (status != MappingPreviewStatus.Succeeded && set.Outputs.Count != 0 || status == MappingPreviewStatus.Succeeded && set.Outputs.Count == 0)
                    throw Invalid();
                if (status == MappingPreviewStatus.Succeeded &&
                    (value.Replay.Target.Kind == PreviewTargetKind.Primary && (set.Outputs.Count != 1 || !set.Outputs[0].Key.IsPrimary) ||
                    value.Replay.Target.Kind == PreviewTargetKind.Named && (set.Outputs.Count != 1 || set.Outputs[0].Key.Name != value.Replay.Target.Name) ||
                    value.Replay.Target.Kind == PreviewTargetKind.All && !set.Outputs.Any(output => output.Key.IsPrimary))) throw Invalid();
                var comparison = new RegressionComparisonService().Compare(value.Expected,
                    new(status == MappingPreviewStatus.Succeeded ? OperationStatusV1.Succeeded : OperationStatusV1.BackendFailure, set),
                    value.ComparisonPolicy, cancellationToken: token);
                result = RegressionCaseExecution.Complete(status, set, comparison);
            }
            if (!Encode(runId, reference, result, token).AsSpan().SequenceEqual(bytes)) throw Invalid();
            token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception failure) when (failure is JsonException or ArgumentException or InvalidOperationException or FormatException or RegressionCaseException or RegressionComparisonException)
        {
            throw Invalid();
        }
    }

    private sealed record Output(string? Name, string Content);
    private sealed record Document(string Schema, string RunId, string CaseDigest, RegressionCaseExecutionOutcome Outcome,
        MappingPreviewStatus? PreviewStatus, Output[]? Outputs);
    private static InvalidOperationException Invalid() => new("Regression run completion is invalid.");
}
