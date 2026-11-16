using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Braid.Contracts.Projections;
using Braid.Domain;
using Braid.MappingBackends.Ferrule;
using Braid.MappingBackends.Workers;

namespace Braid.MappingBackends;

public sealed partial class FerruleInspectionBackend
{
    internal async Task<MappingProjectionResult> ProjectReportAsync(NativeArtifact artifact, RevisionReportPolicy limits,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(limits);
        var invocation = await InvokeReportAsync(artifact, "project", limits, cancellationToken).ConfigureAwait(false);
        if (invocation.Response?.Result is HostProjectionResult projection)
        {
            var scope = artifact.Scope;
            var provenance = new ProjectionProvenance(
                new(scope.OrganizationId.ToString("D"), scope.ProjectId.ToString("D"), scope.MappingId.ToString("D"), scope.RevisionId.ToString("D")),
                new("sha256", artifact.Digest.Value), new("ferrule", EngineRevision, _backendBuild), AdapterBuild, ProjectionVersion);
            var document = new ProjectionDocumentV1(provenance, projection.Coverage, projection.Boundaries, projection.Fields,
                projection.Parameters, projection.Rules, projection.Controls, projection.Relationships, projection.Diagnostics, projection.Capabilities);
            return new(MappingOperationStatus.Succeeded, Provenance(artifact, true), document, []);
        }
        return new(invocation.Status, Provenance(artifact, invocation.Confirmed), null, [invocation.Diagnostic!]);
    }

    internal async Task<MappingValidationResult> ValidateReportAsync(NativeArtifact artifact, RevisionReportPolicy limits,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(limits);
        var invocation = await InvokeReportAsync(artifact, "validate", limits, cancellationToken).ConfigureAwait(false);
        if (invocation.Response?.Result is HostValidationResult validation)
        {
            var diagnostics = new MappingDiagnostic[validation.Issues.Count];
            for (var index = 0; index < diagnostics.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var issue = validation.Issues[index];
                diagnostics[index] = new(issue.Code, DiagnosticSeverity.Error, issue.Message, issue.MessageTruncated,
                    issue.NativeLocator, issue.Owner, issue.Location, issue.LocationTruncated);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new(validation.IsValid ? MappingOperationStatus.Succeeded : MappingOperationStatus.InvalidMapping,
                Provenance(artifact, true), validation.IsValid, (int)validation.TotalIssueCount, (int)validation.EmittedIssueCount,
                validation.Truncated, diagnostics);
        }
        // In particular, do not let a canceled token overwrite an observed cleanup/isolation failure.
        return new(invocation.Status, Provenance(artifact, invocation.Confirmed), null, 0, 0, false, [invocation.Diagnostic!]);
    }

    private async Task<Invocation> InvokeReportAsync(NativeArtifact artifact, string operation, RevisionReportPolicy limits,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return LocalFailure(MappingOperationStatus.Canceled);
        if (artifact.ByteCount > limits.NativeByteLimit) return LocalFailure(MappingOperationStatus.ResourceLimit);
        var request = new HostRequest(ProtocolVersion, Guid.NewGuid().ToString("D"), operation,
            new(artifact.Scope.OrganizationId.ToString("D"), artifact.Scope.ProjectId.ToString("D"),
                artifact.Scope.MappingId.ToString("D"), artifact.Scope.RevisionId.ToString("D")),
            artifact.Digest.Value, InspectionPolicy.DocumentId, new(limits.DiagnosticLimit, limits.ItemLimit, limits.TextByteLimit));
        WorkerProcessResult process;
        try
        {
            var frame = FrameReportRequest(request, artifact, limits, cancellationToken);
            process = await _runner.RunAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return LocalFailure(MappingOperationStatus.Canceled); }
        catch (ProjectionLimitException) { return LocalFailure(MappingOperationStatus.ResourceLimit); }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        { return LocalFailure(MappingOperationStatus.BackendFailure); }
        if (process.Failure != WorkerProcessFailure.None) return LocalFailure(ProcessStatus(process.Failure));
        try
        {
            var response = DecodeReportResponse(process.Output, request, limits, cancellationToken);
            if (response.Failure is { } failure)
                return new(null, FailureStatus(failure.Code), true,
                    new("ferrule." + failure.Code, DiagnosticSeverity.Error, failure.Message, failure.MessageTruncated, failure.NativeLocator));
            return new(response, MappingOperationStatus.Succeeded, true, null);
        }
        catch (OperationCanceledException) { return LocalFailure(MappingOperationStatus.Canceled); }
        catch (ProjectionLimitException) { return LocalFailure(MappingOperationStatus.ResourceLimit); }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or EncoderFallbackException or ProtocolException or NotSupportedException)
        { return LocalFailure(MappingOperationStatus.ProtocolFailure); }
    }

    private static byte[] FrameReportRequest(HostRequest request, NativeArtifact artifact, RevisionReportPolicy limits,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var header = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);
        token.ThrowIfCancellationRequested();
        if (header.Length > limits.HeaderByteLimit || artifact.ByteCount > limits.NativeByteLimit)
            throw new ProjectionLimitException();
        var frameLength = 8L + header.Length + artifact.ByteCount;
        if (frameLength > DockerInspectionWorkerOptions.MaximumFrameBytes) throw new ProjectionLimitException();
        var native = artifact.CopyBytes();
        token.ThrowIfCancellationRequested();
        var frame = new byte[checked((int)frameLength)];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)header.Length);
        CopyReportBytes(header, frame.AsSpan(4, header.Length), token);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4 + header.Length), (uint)native.Length);
        CopyReportBytes(native, frame.AsSpan(8 + header.Length), token);
        token.ThrowIfCancellationRequested();
        return frame;
    }

    private static void CopyReportBytes(ReadOnlySpan<byte> source, Span<byte> target, CancellationToken token)
    {
        for (var offset = 0; offset < source.Length; offset += 4096)
        {
            token.ThrowIfCancellationRequested();
            var count = Math.Min(4096, source.Length - offset);
            source.Slice(offset, count).CopyTo(target.Slice(offset, count));
        }
        token.ThrowIfCancellationRequested();
    }

    private static HostResponse DecodeReportResponse(byte[] output, HostRequest request, RevisionReportPolicy limits,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Require(output is not null && output.Length >= 4);
        var length = BinaryPrimitives.ReadUInt32BigEndian(output);
        if (length > limits.WorkerResponseByteLimit || output.LongLength > 4L + limits.WorkerResponseByteLimit)
            throw new ProjectionLimitException();
        Require(length > 0 && output.LongLength == 4L + length);
        PreflightReportJson(output.AsSpan(4), limits, token);
        token.ThrowIfCancellationRequested();
        var json = StrictUtf8.GetString(output, 4, (int)length);
        token.ThrowIfCancellationRequested();
        var response = JsonSerializer.Deserialize<HostResponse>(json, JsonOptions) ?? throw new ProtocolException();
        token.ThrowIfCancellationRequested();
        Require(response.ProtocolVersion == ProtocolVersion && response.RequestId == request.RequestId && response.Operation == request.Operation);
        Require(response.Revision == request.Revision && response.NativeDigest == request.NativeDigest && response.DocumentId == request.DocumentId);
        Require(response.EngineRevision == EngineRevision && (response.Result is null) != (response.Failure is null));
        if (response.Failure is { } failure)
        {
            _ = FailureStatus(failure.Code);
            var budget = new ProjectionBudget(limits, token);
            budget.Text(failure.Message);
            if (failure.NativeLocator is not null) ReportValidationLocator(budget, failure.NativeLocator);
        }
        else if (request.Operation == "validate" && response.Result is HostValidationResult validation)
            ValidateReportValidationResult(validation, limits, token);
        else if (request.Operation == "project" && response.Result is HostProjectionResult projection)
            ValidateProjection(projection, limits, token);
        else throw new ProtocolException();
        token.ThrowIfCancellationRequested();
        return response;
    }
}
