using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Weft.Contracts.Examples;
using Weft.Contracts.Operations;
using Weft.Contracts.Projections;
using Weft.Contracts.Validation;
using Weft.Domain;
using Weft.MappingBackends;

namespace Weft.Api;

public sealed class NativeExampleService(
    NativeExampleManifest manifest,
    IMappingInspectionBackend? inspectionBackend = null,
    IMappingProjectionBackend? projectionBackend = null)
{
    public const int MaximumResponseBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = ProjectionJson.CreateOptions();
    private readonly NativeExampleManifest _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));

    public IReadOnlyList<NativeExampleDescriptorV1> Examples => _manifest.Examples;

    public async Task<NativeExampleResultV1?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!_manifest.TryGet(id, out var descriptor, out var artifact)) return null;
        var projection = await Project(artifact, cancellationToken).ConfigureAwait(false);
        var validation = await Validate(artifact, projection, cancellationToken).ConfigureAwait(false);
        var result = new NativeExampleResultV1(descriptor, projection, validation);
        try
        {
            using var limit = new ResponseLimitStream(MaximumResponseBytes);
            JsonSerializer.Serialize(limit, result, JsonOptions);
            return result;
        }
        catch (Exception error) when (error is NativeExampleLimitException or JsonException)
        {
            return new NativeExampleResultV1(descriptor, ProjectionFailure(OperationStatusV1.ResourceLimit, projection.Provenance),
                ValidationFailure(OperationStatusV1.ResourceLimit, validation.Provenance));
        }
    }

    private async Task<ProjectionReportV1> Project(NativeArtifact artifact, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return ProjectionFailure(OperationStatusV1.Canceled);
        if (projectionBackend is null) return ProjectionFailure(OperationStatusV1.BackendUnavailable);
        OperationProvenanceV1? provenance = null;
        try
        {
            var result = await projectionBackend.ProjectAsync(artifact, cancellationToken).ConfigureAwait(false);
            provenance = Provenance(result.Provenance, artifact);
            var status = Status(result.Status);
            Require((status == OperationStatusV1.Succeeded) == (result.Projection is not null));
            if (result.Projection is { } document)
            {
                Require(provenance.Confirmed && document.Provenance.Revision == provenance.Revision
                    && document.Provenance.NativeArtifactDigest == provenance.NativeArtifactDigest
                    && document.Provenance.Backend == provenance.Backend && document.Provenance.AdapterVersion == provenance.AdapterVersion);
            }
            CheckDiagnostics(result.Diagnostics);
            return new ProjectionReportV1(status, provenance, result.Projection, result.Diagnostics.Select(diagnostic => new OperationDiagnosticV1(
                diagnostic.Code, diagnostic.Severity, diagnostic.Message, diagnostic.MessageTruncated, diagnostic.NativeLocator)).ToArray());
        }
        catch (OperationCanceledException) { return ProjectionFailure(OperationStatusV1.Canceled, provenance); }
        catch (NativeExampleLimitException) { return ProjectionFailure(OperationStatusV1.ResourceLimit, provenance); }
        catch (NativeExampleProtocolException) { return ProjectionFailure(OperationStatusV1.ProtocolFailure, provenance); }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        { return ProjectionFailure(OperationStatusV1.BackendFailure, provenance); }
    }

    private async Task<ValidationReportV1> Validate(NativeArtifact artifact, ProjectionReportV1 projection, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return ValidationFailure(OperationStatusV1.Canceled);
        if (inspectionBackend is null) return ValidationFailure(OperationStatusV1.BackendUnavailable);
        OperationProvenanceV1? provenance = null;
        try
        {
            var result = await inspectionBackend.ValidateAsync(artifact, cancellationToken).ConfigureAwait(false);
            provenance = Provenance(result.Provenance, artifact);
            var status = Status(result.Status);
            CheckDiagnostics(result.Diagnostics);
            Require(result.TotalIssueCount is >= 0 and <= InspectionPolicy.MaximumValidationIssues
                && result.EmittedIssueCount >= 0 && result.EmittedIssueCount <= result.TotalIssueCount);
            if (result.IsValid is { } isValid)
            {
                Require(provenance.Confirmed && status == (isValid ? OperationStatusV1.Succeeded : OperationStatusV1.InvalidMapping)
                    && isValid == (result.TotalIssueCount == 0) && result.EmittedIssueCount == result.Diagnostics.Count
                    && (result.Truncated || result.EmittedIssueCount == result.TotalIssueCount));
            }
            else Require(status != OperationStatusV1.Succeeded && result.TotalIssueCount == 0 && result.EmittedIssueCount == 0 && !result.Truncated);
            NativeProjectionLinks? links = null;
            if (projection.Document is { } document && projection.Provenance == provenance && provenance.Confirmed)
                links = new NativeProjectionLinks(document);
            var diagnostics = result.Diagnostics.Select(diagnostic => new ValidationDiagnosticV1(diagnostic.Code, diagnostic.Severity,
                diagnostic.Message, diagnostic.MessageTruncated, diagnostic.NativeLocator, Owner(diagnostic.Owner), diagnostic.Location,
                diagnostic.LocationTruncated, links?.Find(diagnostic.NativeLocator))).ToArray();
            return new ValidationReportV1(status, provenance, result.IsValid, result.TotalIssueCount, result.EmittedIssueCount, result.Truncated, diagnostics);
        }
        catch (OperationCanceledException) { return ValidationFailure(OperationStatusV1.Canceled, provenance); }
        catch (NativeExampleLimitException) { return ValidationFailure(OperationStatusV1.ResourceLimit, provenance); }
        catch (NativeExampleProtocolException) { return ValidationFailure(OperationStatusV1.ProtocolFailure, provenance); }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        { return ValidationFailure(OperationStatusV1.BackendFailure, provenance); }
    }

    private static OperationProvenanceV1 Provenance(MappingOperationProvenance provenance, NativeArtifact artifact)
    {
        Require(provenance is not null && provenance.Scope == artifact.Scope && provenance.NativeDigest == artifact.Digest);
        return new OperationProvenanceV1(NativeExampleManifest.Revision(artifact.Scope), new ArtifactDigest("sha256", artifact.Digest.Value),
            new BackendIdentity(provenance.BackendId, provenance.EngineRevision, provenance.BackendBuild), provenance.AdapterBuild, provenance.Confirmed);
    }

    private static OperationStatusV1 Status(MappingOperationStatus status) => status switch
    {
        MappingOperationStatus.Succeeded => OperationStatusV1.Succeeded,
        MappingOperationStatus.InvalidMapping => OperationStatusV1.InvalidMapping,
        MappingOperationStatus.UnsupportedArtifact => OperationStatusV1.UnsupportedArtifact,
        MappingOperationStatus.ResourceLimit => OperationStatusV1.ResourceLimit,
        MappingOperationStatus.ProtocolFailure => OperationStatusV1.ProtocolFailure,
        MappingOperationStatus.BackendUnavailable => OperationStatusV1.BackendUnavailable,
        MappingOperationStatus.BackendFailure => OperationStatusV1.BackendFailure,
        MappingOperationStatus.TimedOut => OperationStatusV1.TimedOut,
        MappingOperationStatus.Canceled => OperationStatusV1.Canceled,
        MappingOperationStatus.IsolationFailure => OperationStatusV1.IsolationFailure,
        _ => throw new NativeExampleProtocolException()
    };

    private static NativeDiagnosticOwnerV1? Owner(MappingDiagnosticOwner? owner) => owner switch
    {
        null => null,
        GraphNodeDiagnosticOwner graph => new GraphNodeNativeOwnerV1(graph.FunctionId, graph.NodeId),
        UserFunctionDiagnosticOwner function => new UserFunctionNativeOwnerV1(function.FunctionId),
        EndpointDiagnosticOwner endpoint => new EndpointNativeOwnerV1(Endpoint(endpoint.Endpoint)),
        SchemaNodeDiagnosticOwner schema => new SchemaNodeNativeOwnerV1(Endpoint(schema.Endpoint), SchemaPath(schema.Path)),
        ScopeDiagnosticOwner scope => new ScopeNativeOwnerV1(Endpoint(scope.Target), ScopePath(scope.Path)),
        FailureRuleDiagnosticOwner failure => new FailureRuleNativeOwnerV1(failure.Index),
        _ => throw new NativeExampleProtocolException()
    };

    private static NativeEndpointV1 Endpoint(MappingEndpoint endpoint) => endpoint switch
    {
        SourceMappingEndpoint => new SourceNativeEndpointV1(),
        TargetMappingEndpoint => new TargetNativeEndpointV1(),
        NamedSourceMappingEndpoint source => new NamedSourceNativeEndpointV1(source.Index, source.Name),
        NamedTargetMappingEndpoint target => new NamedTargetNativeEndpointV1(target.Index, target.Name),
        _ => throw new NativeExampleProtocolException()
    };

    private static SchemaNativeOwnerStepV1[] SchemaPath(IReadOnlyList<SchemaOwnerStep> path)
    {
        if (path.Count > InspectionPolicy.MaximumResponseItems) throw new NativeExampleLimitException();
        return path.Select<SchemaOwnerStep, SchemaNativeOwnerStepV1>(step => step switch
        {
            ChildSchemaOwnerStep child => new ChildSchemaNativeOwnerStepV1(child.Index),
            DynamicFieldSchemaOwnerStep => new DynamicFieldSchemaNativeOwnerStepV1(),
            ContainsSchemaOwnerStep contains => new ContainsSchemaNativeOwnerStepV1(contains.Index),
            DependentSchemaOwnerStep dependent => new DependentSchemaNativeOwnerStepV1(dependent.Index),
            _ => throw new NativeExampleProtocolException()
        }).ToArray();
    }

    private static ScopeNativeOwnerStepV1[] ScopePath(IReadOnlyList<ScopeOwnerStep> path)
    {
        if (path.Count > InspectionPolicy.MaximumResponseItems) throw new NativeExampleLimitException();
        return path.Select<ScopeOwnerStep, ScopeNativeOwnerStepV1>(step => step switch
        {
            ChildScopeOwnerStep child => new ChildScopeNativeOwnerStepV1(child.Index),
            DynamicChildScopeOwnerStep dynamicStep => new DynamicChildScopeNativeOwnerStepV1(dynamicStep.Index),
            SegmentScopeOwnerStep segment => new SegmentScopeNativeOwnerStepV1(segment.Index),
            _ => throw new NativeExampleProtocolException()
        }).ToArray();
    }

    private static void CheckDiagnostics(IReadOnlyList<MappingDiagnostic> diagnostics)
    {
        if (diagnostics.Count > InspectionPolicy.MaximumDiagnostics) throw new NativeExampleLimitException();
        Require(diagnostics.All(diagnostic => diagnostic is not null && Enum.IsDefined(diagnostic.Severity)));
    }

    private static ProjectionReportV1 ProjectionFailure(OperationStatusV1 status, OperationProvenanceV1? provenance = null)
        => new(status, provenance, null, [Failure(status)]);

    private static ValidationReportV1 ValidationFailure(OperationStatusV1 status, OperationProvenanceV1? provenance = null)
    {
        var diagnostic = Failure(status);
        return new ValidationReportV1(status, provenance, null, 0, 0, false,
            [new ValidationDiagnosticV1(diagnostic.Code, diagnostic.Severity, diagnostic.Message, false)]);
    }

    private static OperationDiagnosticV1 Failure(OperationStatusV1 status)
    {
        var (code, message) = status switch
        {
            OperationStatusV1.BackendUnavailable => ("unavailable", "The configured native inspection backend is unavailable."),
            OperationStatusV1.ResourceLimit => ("limit", "The example report exceeded the server response policy."),
            OperationStatusV1.Canceled => ("canceled", "The example request was canceled."),
            OperationStatusV1.ProtocolFailure => ("protocol", "The native inspection backend returned an inconsistent report."),
            _ => ("failure", "The native inspection backend failed.")
        };
        return new OperationDiagnosticV1("mapping.example." + code, DiagnosticSeverity.Error, message, false);
    }

    private static void Require([DoesNotReturnIf(false)] bool condition)
    {
        if (!condition) throw new NativeExampleProtocolException();
    }

    private sealed class NativeExampleProtocolException : Exception { }

    private sealed class ResponseLimitStream(int maximumBytes) : Stream
    {
        private long _count;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _count;
        public override long Position { get => _count; set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (_count + buffer.Length > maximumBytes) throw new NativeExampleLimitException();
            _count += buffer.Length;
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
