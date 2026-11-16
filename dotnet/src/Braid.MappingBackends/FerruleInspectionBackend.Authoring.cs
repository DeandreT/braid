using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Braid.Application;
using Braid.Contracts.Projections;
using Braid.Domain;
using Braid.MappingBackends.Ferrule;
using Braid.MappingBackends.Workers;

namespace Braid.MappingBackends;

public sealed partial class FerruleInspectionBackend
{
    public const string AuthoringProtocolVersion = "braid.ferrule-authoring.v1";

    /// <summary>The Rust compiler owns the closed plan grammar. C# owns framing, identity and result verification.</summary>
    public async Task<MappingAuthoringResult> AuthorAsync(ProjectScope project, Guid authoringId, ReadOnlyMemory<byte> plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (authoringId == Guid.Empty) throw new ArgumentException("Authoring identity is required.", nameof(authoringId));
        if (cancellationToken.IsCancellationRequested) return AuthoringLocalFailure(MappingOperationStatus.Canceled);
        if (plan.Length is < 1 or > MappingAuthoringPolicy.MaximumPlanBytes) return AuthoringLocalFailure(MappingOperationStatus.ResourceLimit);
        if (_runner is DockerInspectionWorkerRunner docker && docker.Profile != DockerWorkerProfile.Authoring)
            throw new InvalidOperationException("Authoring requires its fixed worker profile.");
        if (_runner is ProcessWorkerRunner child && child.Profile != DockerWorkerProfile.Authoring ||
            _runner is FerruleServiceRunner service && service.ProfileName != FerruleServiceProtocol.AuthoringProfile)
            throw new InvalidOperationException("Authoring requires its fixed service profile.");
        var ownedPlan = plan.ToArray();
        var digest = Sha256Digest.FromBytes(ownedPlan);
        var request = new AuthoringHostRequest(AuthoringProtocolVersion, Guid.NewGuid().ToString("D"), "author",
            project.OrganizationId.ToString("D"), project.ProjectId.ToString("D"), authoringId.ToString("D"), digest.Value);
        try
        {
            var header = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);
            Require(header.Length <= InspectionPolicy.MaximumHeaderBytes);
            var frame = new byte[header.Length + ownedPlan.Length + 8];
            BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)header.Length);
            header.CopyTo(frame, 4);
            BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4 + header.Length), (uint)ownedPlan.Length);
            ownedPlan.CopyTo(frame, 8 + header.Length);
            var process = await _runner.RunAsync(frame, cancellationToken).ConfigureAwait(false);
            if (process.Failure != WorkerProcessFailure.None) return AuthoringLocalFailure(ProcessStatus(process.Failure));
            return DecodeAuthoring(process.Output, request, project, authoringId, digest);
        }
        catch (OperationCanceledException) { return AuthoringLocalFailure(MappingOperationStatus.Canceled); }
        catch (ProjectionLimitException) { return AuthoringLocalFailure(MappingOperationStatus.ResourceLimit); }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or EncoderFallbackException or ProtocolException or NotSupportedException or ArgumentException or RevisionReportValidationException)
        { return AuthoringLocalFailure(MappingOperationStatus.ProtocolFailure); }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        { return AuthoringLocalFailure(MappingOperationStatus.BackendFailure); }
    }

    private MappingAuthoringResult DecodeAuthoring(byte[] bytes, AuthoringHostRequest request, ProjectScope project,
        Guid authoringId, Sha256Digest planDigest)
    {
        Require(bytes is { Length: >= 4 } && bytes.Length <= MappingAuthoringPolicy.MaximumResponseBytes);
        var length = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        Require(length is > 0 and <= MappingEditPolicy.MaximumReportBytes && bytes.Length >= 4L + length);
        var json = bytes.AsSpan(4, (int)length);
        PreflightJson(json, true);
        var response = JsonSerializer.Deserialize<AuthoringHostResponse>(json, JsonOptions) ?? throw new ProtocolException();
        Require(response.ProtocolVersion == AuthoringProtocolVersion && response.Request == request && response.EngineRevision == EngineRevision);
        Require((response.Result is null) != (response.Failure is null));
        if (response.Failure is { } failure)
        {
            Require(bytes.Length == 4L + length);
            Text(failure.Message);
            if (failure.OperationIndex is { } index)
                Require(int.TryParse(index, NumberStyles.None, CultureInfo.InvariantCulture, out var position)
                    && position >= 0 && position < MappingEditPolicy.MaximumOperations && position.ToString(CultureInfo.InvariantCulture) == index);
            Require((failure.Code == "operationRejected") == (failure.OperationIndex is not null));
            var status = failure.Code switch
            {
                "invalidAuthoring" or "invalidSchema" or "invalidBoundary" or "unsupportedSemantics" or "operationRejected" or "invalidOperations" => IngestionBackendStatus.InvalidMapping,
                "authoringLimit" or "operationsLimitExceeded" or "nativeLimitExceeded" or "resultLimitExceeded" => IngestionBackendStatus.ResourceLimit,
                "invalidFrame" or "invalidHeader" or "unsupportedOperation" or "invalidContext" or "digestMismatch" or "nativeEncodeFailed" or "nativeDecodeFailed" => IngestionBackendStatus.ProtocolFailure,
                _ => throw new ProtocolException()
            };
            return new(null, new(failure.Code, failure.Message, failure.MessageTruncated, failure.OperationIndex, status));
        }
        var result = response.Result!;
        Require(result.NativeByteCount is > 0 and <= MappingEditPolicy.MaximumNativeBytes);
        var offset = 4 + (int)length;
        Require(bytes.Length == offset + 4L + result.NativeByteCount);
        Require(BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset)) == result.NativeByteCount);
        var native = bytes[(offset + 4)..];
        Require(Sha256Digest.FromBytes(native).Value == result.NativeDigest);
        Require(result.Validation is not null && result.Projection is not null);
        ValidateValidation(result.Validation);
        ValidateProjection(result.Projection);
        var validation = result.Validation;
        var issues = validation.Issues.Select(issue => new MappingDiagnostic(issue.Code, DiagnosticSeverity.Error,
            issue.Message, issue.MessageTruncated, issue.NativeLocator, issue.Owner, issue.Location, issue.LocationTruncated)).ToArray();
        var projection = result.Projection;
        return new(new(project, authoringId, planDigest, native,
            new(validation.IsValid, (int)validation.TotalIssueCount, validation.Truncated, issues),
            new(projection.Coverage, projection.Boundaries, projection.Fields, projection.Parameters, projection.Rules,
                projection.Controls, projection.Relationships, projection.Diagnostics, projection.Capabilities), _backendBuild), null);
    }

    private static MappingAuthoringResult AuthoringLocalFailure(MappingOperationStatus status)
    {
        var failure = EditLocalFailure(status).Failure!;
        return new(null, failure with
        {
            Code = failure.Code.Replace("edit", "authoring", StringComparison.Ordinal),
            Message = failure.Message.Replace("Edit replay", "Authoring", StringComparison.Ordinal)
        });
    }

    private sealed record AuthoringHostRequest(string ProtocolVersion, string RequestId, string Operation,
        string OrganizationId, string ProjectId, string AuthoringId, string PlanDigest);
    private sealed record AuthoringHostResponse(string ProtocolVersion, AuthoringHostRequest? Request, string EngineRevision,
        AuthoringHostResult? Result, EditHostFailure? Failure);
    private sealed record AuthoringHostResult(string NativeDigest, int NativeByteCount, HostValidationResult Validation, HostProjectionResult Projection);
}
