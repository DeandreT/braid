using System.Text.Json;
using Weft.Application;
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
    private static readonly RevisionReportPolicy ReportLimits = new();
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
            // Cleanup failure is the actual worker outcome even when its request token was canceled.
            var normalizationToken = result.Status == MappingOperationStatus.IsolationFailure ? CancellationToken.None : cancellationToken;
            provenance = FerruleReportNormalizer.Provenance(result.Provenance, artifact, normalizationToken);
            return FerruleReportNormalizer.ProjectionForExample(result, artifact, normalizationToken);
        }
        catch (OperationCanceledException) { return ProjectionFailure(OperationStatusV1.Canceled, provenance); }
        catch (NativeExampleLimitException) { return ProjectionFailure(OperationStatusV1.ResourceLimit, provenance); }
        catch (RevisionReportValidationException error)
        {
            return ProjectionFailure(error.Failure == RevisionReportValidationFailure.ResourceLimit
                ? OperationStatusV1.ResourceLimit : OperationStatusV1.ProtocolFailure, provenance);
        }
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
            var normalizationToken = result.Status == MappingOperationStatus.IsolationFailure ? CancellationToken.None : cancellationToken;
            provenance = FerruleReportNormalizer.Provenance(result.Provenance, artifact, normalizationToken);
            // Normalize the native result before indexing the projection, preserving failure precedence.
            var report = FerruleReportNormalizer.ValidationForExample(result, artifact, cancellationToken: normalizationToken);
            NativeProjectionLinks? links = null;
            if (projection.Document is { } document && projection.Provenance == provenance && provenance.Confirmed)
                links = new NativeProjectionLinks(document, ReportLimits, normalizationToken);
            if (links is null) return report;
            var diagnostics = new ValidationDiagnosticV1[report.Diagnostics.Count];
            for (var index = 0; index < diagnostics.Length; index++)
            {
                normalizationToken.ThrowIfCancellationRequested();
                var diagnostic = report.Diagnostics[index];
                diagnostics[index] = diagnostic with { ProjectionLocator = links.Find(diagnostic.NativeLocator, normalizationToken) };
            }
            normalizationToken.ThrowIfCancellationRequested();
            return report with { Diagnostics = Array.AsReadOnly(diagnostics) };
        }
        catch (OperationCanceledException) { return ValidationFailure(OperationStatusV1.Canceled, provenance); }
        catch (NativeExampleLimitException) { return ValidationFailure(OperationStatusV1.ResourceLimit, provenance); }
        catch (RevisionReportValidationException error)
        {
            return ValidationFailure(error.Failure == RevisionReportValidationFailure.ResourceLimit
                ? OperationStatusV1.ResourceLimit : OperationStatusV1.ProtocolFailure, provenance);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        { return ValidationFailure(OperationStatusV1.BackendFailure, provenance); }
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
