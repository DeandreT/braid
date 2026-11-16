using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Braid.Domain;
using Braid.MappingBackends.Ferrule;
using Braid.MappingBackends.Workers;

namespace Braid.MappingBackends;

public sealed partial class FerruleInspectionBackend
{
    public const string AuthoredPreviewProtocolVersion = "braid.ferrule-authored-preview.v1";
    private static readonly JsonSerializerOptions AuthoredPreviewJsonOptions = CreateAuthoredPreviewOptions();
    private static JsonSerializerOptions CreateAuthoredPreviewOptions()
    {
        var options = CreateOptions(); options.RespectRequiredConstructorParameters = true; return options;
    }

    /// <summary>Executes retained native bytes against one exact sample without a fabricated saved-revision identity.</summary>
    public async Task<MappingAuthoredPreviewResult> PreviewAuthoredAsync(MappingAuthoredPreviewSubject subject,
        ReadOnlyMemory<byte> native, ReadOnlyMemory<byte> sample, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (subject.Project is null || subject.AuthoringId == Guid.Empty || subject.ProposalDigest is null ||
            subject.NativeDigest is null || subject.SampleDigest is null) throw new ArgumentException("Exact preview identity is required.");
        if (cancellationToken.IsCancellationRequested) return PreviewFailure("canceled", MappingOperationStatus.Canceled);
        if (native.Length is < 1 or > MappingAuthoredPreviewPolicy.MaximumNativeBytes || sample.Length > MappingAuthoredPreviewPolicy.MaximumSampleBytes)
            return PreviewFailure("resourceLimit", MappingOperationStatus.ResourceLimit);
        if (_runner is DockerInspectionWorkerRunner docker && docker.Profile != DockerWorkerProfile.AuthoredPreview ||
            _runner is ProcessWorkerRunner child && child.Profile != DockerWorkerProfile.AuthoredPreview ||
            _runner is FerruleServiceRunner service && service.ProfileName != FerruleServiceProtocol.AuthoredPreviewProfile)
            throw new InvalidOperationException("Authored preview requires its fixed worker profile.");
        var ownedNative = native.ToArray(); var ownedSample = sample.ToArray();
        if (Sha256Digest.FromBytes(ownedNative) != subject.NativeDigest || Sha256Digest.FromBytes(ownedSample) != subject.SampleDigest)
            return PreviewFailure("digestMismatch", MappingOperationStatus.ProtocolFailure);
        var request = new AuthoredPreviewRequest(AuthoredPreviewProtocolVersion, Guid.NewGuid().ToString("D"), "previewAuthored",
            subject.Project.OrganizationId.ToString("D"), subject.Project.ProjectId.ToString("D"), subject.AuthoringId.ToString("D"),
            subject.ProposalDigest.Value, subject.NativeDigest.Value, subject.SampleDigest.Value);
        try
        {
            var header = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);
            Require(header.Length <= MappingAuthoredPreviewPolicy.MaximumHeaderBytes);
            var frame = new byte[12 + header.Length + ownedNative.Length + ownedSample.Length];
            var offset = 0;
            foreach (var part in new[] { header, ownedNative, ownedSample })
            {
                BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(offset), (uint)part.Length);
                part.CopyTo(frame, offset + 4); offset += part.Length + 4;
            }
            var process = await _runner.RunAsync(frame, cancellationToken).ConfigureAwait(false);
            if (process.Failure != WorkerProcessFailure.None) return PreviewFailure("workerFailure", ProcessStatus(process.Failure));
            return DecodeAuthoredPreview(process.Output, request, subject);
        }
        catch (OperationCanceledException) { return PreviewFailure("canceled", MappingOperationStatus.Canceled); }
        catch (ProjectionLimitException) { return PreviewFailure("resourceLimit", MappingOperationStatus.ResourceLimit); }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or EncoderFallbackException or ProtocolException or NotSupportedException or ArgumentException)
        { return PreviewFailure("invalidResponse", MappingOperationStatus.ProtocolFailure); }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        { return PreviewFailure("backendFailure", MappingOperationStatus.BackendFailure); }
    }

    private MappingAuthoredPreviewResult DecodeAuthoredPreview(byte[] bytes, AuthoredPreviewRequest request, MappingAuthoredPreviewSubject subject)
    {
        Require(bytes is { Length: >= 4 } && bytes.Length <= MappingAuthoredPreviewPolicy.MaximumResponseBytes);
        var length = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        Require(length is > 0 and <= MappingAuthoredPreviewPolicy.MaximumHeaderBytes && bytes.Length >= 4L + length);
        var json = bytes.AsSpan(4, (int)length);
        PreflightJson(json, true);
        var response = JsonSerializer.Deserialize<AuthoredPreviewResponse>(json, AuthoredPreviewJsonOptions) ?? throw new ProtocolException();
        Require(response.ProtocolVersion == AuthoredPreviewProtocolVersion && response.Request == request && response.EngineRevision == EngineRevision);
        Require((response.Result is null) != (response.Failure is null));
        if (response.Failure is { } failure)
        {
            Require(bytes.Length == 4L + length);
            var status = failure.Code switch
            {
                "invalidFrame" or "invalidHeader" or "unsupportedOperation" or "invalidContext" or "digestMismatch" => MappingOperationStatus.ProtocolFailure,
                "invalidMapping" => MappingOperationStatus.InvalidMapping,
                "unsupportedSemantics" => MappingOperationStatus.UnsupportedArtifact,
                "invalidSample" or "executionFailed" => MappingOperationStatus.BackendFailure,
                "resourceLimit" => MappingOperationStatus.ResourceLimit,
                _ => throw new ProtocolException()
            };
            return PreviewFailure(failure.Code, status);
        }
        var result = response.Result!;
        Require(result.OutputByteCount is >= 0 and <= MappingAuthoredPreviewPolicy.MaximumOutputBytes);
        var offset = 4 + (int)length;
        Require(bytes.Length == offset + 4L + result.OutputByteCount);
        Require(BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset)) == result.OutputByteCount);
        var output = bytes[(offset + 4)..];
        Require(Sha256Digest.FromBytes(output).Value == result.OutputDigest);
        Require((result.Format, result.MediaType) is ("json", "application/json") or ("xml", "application/xml") or
            ("csv", "text/csv") or ("tsv", "text/tab-separated-values") or ("x12", "application/edi-x12") or ("edifact", "application/edifact"));
        return new(new(subject, output, result.Format, result.MediaType, _backendBuild), null);
    }

    private static MappingAuthoredPreviewResult PreviewFailure(string code, MappingOperationStatus status) => new(null, new(code, status));
    private sealed record AuthoredPreviewRequest(string ProtocolVersion, string RequestId, string Operation, string OrganizationId,
        string ProjectId, string AuthoringId, string ProposalDigest, string NativeDigest, string SampleDigest);
    private sealed record AuthoredPreviewResponse(string ProtocolVersion, AuthoredPreviewRequest? Request, string EngineRevision,
        AuthoredPreviewOutput? Result, AuthoredPreviewError? Failure);
    private sealed record AuthoredPreviewOutput(string OutputDigest, int OutputByteCount, string Format, string MediaType);
    private sealed record AuthoredPreviewError(string Code);
}
