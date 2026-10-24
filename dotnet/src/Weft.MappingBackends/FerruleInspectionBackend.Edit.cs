using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Weft.Application;
using Weft.Contracts.Projections;
using Weft.Domain;
using Weft.MappingBackends.Ferrule;
using Weft.MappingBackends.Ingestion;
using Weft.MappingBackends.Workers;

namespace Weft.MappingBackends;

public sealed partial class FerruleInspectionBackend
{
    public const string EditProtocolVersion = "weft.ferrule-edit.v1";

    public async Task<MappingEditResult> ReplayAsync(NativeArtifact immutableBase, MappingEditScript operations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(immutableBase);
        ArgumentNullException.ThrowIfNull(operations);
        if (cancellationToken.IsCancellationRequested) return EditLocalFailure(MappingOperationStatus.Canceled);
        if (_runner is DockerInspectionWorkerRunner docker && docker.Profile != DockerWorkerProfile.Edit)
            throw new InvalidOperationException("Edit replay requires the fixed edit worker profile.");
        var artifact = immutableBase;
        var scope = artifact.Scope;
        var request = new EditHostRequest(EditProtocolVersion, Guid.NewGuid().ToString("D"), "edit",
            new(scope.OrganizationId.ToString("D"), scope.ProjectId.ToString("D"), scope.MappingId.ToString("D"), scope.RevisionId.ToString("D")),
            artifact.Digest.Value, InspectionPolicy.DocumentId, operations.Digest.Value);
        if (artifact.ByteCount > MappingEditPolicy.MaximumNativeBytes) return EditLocalFailure(MappingOperationStatus.ResourceLimit);
        try
        {
            var process = await _runner.RunAsync(EditFrame(request, artifact, operations), cancellationToken).ConfigureAwait(false);
            if (process.Failure != WorkerProcessFailure.None) return EditLocalFailure(ProcessStatus(process.Failure));
            return DecodeEdit(process.Output, request, artifact, operations, cancellationToken);
        }
        catch (OperationCanceledException) { return EditLocalFailure(MappingOperationStatus.Canceled); }
        catch (ProjectionLimitException) { return EditLocalFailure(MappingOperationStatus.ResourceLimit); }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or EncoderFallbackException or ProtocolException or NotSupportedException or ArgumentException or RevisionReportValidationException)
        { return EditLocalFailure(MappingOperationStatus.ProtocolFailure); }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        { return EditLocalFailure(MappingOperationStatus.BackendFailure); }
    }

    private static byte[] EditFrame(EditHostRequest request, NativeArtifact artifact, MappingEditScript operations)
    {
        var header = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);
        if (header.Length > InspectionPolicy.MaximumHeaderBytes) throw new ProtocolException();
        var native = artifact.CopyBytes();
        var script = operations.CopyUtf8();
        using var buffer = new MemoryStream(12 + header.Length + native.Length + script.Length);
        Span<byte> length = stackalloc byte[4];
        foreach (var body in new[] { header, native, script })
        {
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)body.Length);
            buffer.Write(length); buffer.Write(body);
        }
        return buffer.ToArray();
    }

    private MappingEditResult DecodeEdit(byte[] bytes, EditHostRequest request, NativeArtifact immutableBase,
        MappingEditScript operations, CancellationToken token)
    {
        Require(bytes is { Length: >= 4 } && bytes.Length <= MappingEditPolicy.MaximumResponseBytes);
        var length = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        Require(length is > 0 and <= MappingEditPolicy.MaximumReportBytes && bytes.Length >= 4L + length);
        var json = bytes.AsSpan(4, (int)length);
        PreflightJson(json, true);
        var response = JsonSerializer.Deserialize<EditHostResponse>(json, JsonOptions) ?? throw new ProtocolException();
        Require(response.ProtocolVersion == EditProtocolVersion && response.Request == request && response.EngineRevision == EngineRevision);
        Require((response.Result is null) != (response.Failure is null));
        if (response.Failure is { } failure)
        {
            Require(bytes.Length == 4L + length);
            Text(failure.Message);
            if (failure.OperationIndex is { } index)
                Require(int.TryParse(index, NumberStyles.None, CultureInfo.InvariantCulture, out var position)
                    && position >= 0 && position < operations.Count && position.ToString(CultureInfo.InvariantCulture) == index);
            Require((failure.Code == "operationRejected") == (failure.OperationIndex is not null));
            var status = failure.Code switch
            {
                "operationRejected" or "nativeDecodeFailed" or "invalidOperations" => IngestionBackendStatus.InvalidMapping,
                "operationsLimitExceeded" or "nativeLimitExceeded" or "resultLimitExceeded" => IngestionBackendStatus.ResourceLimit,
                "invalidFrame" or "invalidHeader" or "unsupportedOperation" or "invalidContext" or "digestMismatch" or "nativeEncodeFailed" => IngestionBackendStatus.ProtocolFailure,
                _ => throw new ProtocolException()
            };
            return new(null, new(failure.Code, failure.Message, failure.MessageTruncated, failure.OperationIndex, status));
        }
        var result = response.Result!;
        Require(result.NativeByteCount is > 0 and <= MappingEditPolicy.MaximumNativeBytes
            && result.OperationsDigest == operations.Digest.Value && result.OperationCount == operations.Count);
        var offset = 4 + (int)length;
        Require(bytes.Length == offset + 4L + result.NativeByteCount);
        Require(BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset)) == result.NativeByteCount);
        var checkpointArtifact = NativeArtifact.Admit(immutableBase.Scope, bytes.AsSpan(offset + 4), MappingEditPolicy.MaximumNativeBytes);
        Require(checkpointArtifact.Digest.Value == result.NativeDigest);
        ValidateValidation(result.Validation);
        ValidateProjection(result.Projection);
        var provenance = new MappingOperationProvenance(checkpointArtifact.Scope, checkpointArtifact.Digest, "ferrule", EngineRevision, _backendBuild, AdapterBuild, true);
        var validation = result.Validation;
        var diagnostics = validation.Issues.Select(issue => new MappingDiagnostic(issue.Code, DiagnosticSeverity.Error,
            issue.Message, issue.MessageTruncated, issue.NativeLocator, issue.Owner, issue.Location, issue.LocationTruncated)).ToArray();
        var validated = new MappingValidationResult(validation.IsValid ? MappingOperationStatus.Succeeded : MappingOperationStatus.InvalidMapping,
            provenance, validation.IsValid, (int)validation.TotalIssueCount, (int)validation.EmittedIssueCount, validation.Truncated, diagnostics);
        var projected = result.Projection;
        var scope = checkpointArtifact.Scope;
        var document = new ProjectionDocumentV1(new(new(scope.OrganizationId.ToString("D"), scope.ProjectId.ToString("D"), scope.MappingId.ToString("D"), scope.RevisionId.ToString("D")),
            new("sha256", checkpointArtifact.Digest.Value), new("ferrule", EngineRevision, _backendBuild), AdapterBuild, ProjectionVersion),
            projected.Coverage, projected.Boundaries, projected.Fields, projected.Parameters, projected.Rules, projected.Controls,
            projected.Relationships, projected.Diagnostics, projected.Capabilities);
        var evidence = IngestionValidationMapper.Map(validated).Evidence ?? throw new ProtocolException();
        var policy = new RevisionReportPolicy();
        return new(new(checkpointArtifact, evidence, FerruleReportNormalizer.Validation(validated, checkpointArtifact, policy, cancellationToken: token),
            FerruleReportNormalizer.Projection(new(MappingOperationStatus.Succeeded, provenance, document, []), checkpointArtifact, policy, token)), null);
    }

    private static MappingEditResult EditLocalFailure(MappingOperationStatus status)
    {
        var failure = LocalFailure(status);
        var diagnostic = failure.Diagnostic!;
        return new(null, new(diagnostic.Code.Replace("inspection", "edit", StringComparison.Ordinal),
            diagnostic.Message.Replace("Inspection", "Edit replay", StringComparison.Ordinal), false, null,
            status switch
            {
                MappingOperationStatus.ResourceLimit => IngestionBackendStatus.ResourceLimit,
                MappingOperationStatus.ProtocolFailure => IngestionBackendStatus.ProtocolFailure,
                MappingOperationStatus.BackendUnavailable => IngestionBackendStatus.BackendUnavailable,
                MappingOperationStatus.TimedOut => IngestionBackendStatus.TimedOut,
                MappingOperationStatus.Canceled => IngestionBackendStatus.Canceled,
                MappingOperationStatus.IsolationFailure => IngestionBackendStatus.IsolationFailure,
                _ => IngestionBackendStatus.BackendFailure
            }));
    }

    private sealed record EditHostRequest(string ProtocolVersion, string RequestId, string Operation, HostRevision Revision,
        string NativeDigest, string DocumentId, string OperationsDigest);
    private sealed record EditHostResponse(string ProtocolVersion, EditHostRequest? Request, string EngineRevision,
        EditHostResult? Result, EditHostFailure? Failure);
    private sealed record EditHostResult(string NativeDigest, int NativeByteCount, string OperationsDigest, int OperationCount,
        HostValidationResult Validation, HostProjectionResult Projection);
    private sealed record EditHostFailure(string Code, string Message, bool MessageTruncated, string? OperationIndex);
}
