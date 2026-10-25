using System.Diagnostics.CodeAnalysis;
using System.Text;
using Weft.Application;
using Weft.Contracts.Operations;
using Weft.Contracts.Projections;
using Weft.Contracts.Validation;
using Weft.Domain;

namespace Weft.MappingBackends;

/// <summary>Maps actual adapter observations. It performs no execution, storage or metadata fabrication.</summary>
public static partial class FerruleReportNormalizer
{
    public static OperationProvenanceV1 Provenance(MappingOperationProvenance provenance, NativeArtifact artifact,
        CancellationToken cancellationToken = default)
    {
        return ProvenanceCore(provenance, artifact, new(new(), true, cancellationToken));
    }

    private static OperationProvenanceV1 ProvenanceCore(MappingOperationProvenance provenance, NativeArtifact artifact, Context context)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        context.Require(provenance is not null && provenance.Scope == artifact.Scope && provenance.NativeDigest == artifact.Digest);
        return new(new(artifact.Scope.OrganizationId.ToString("D"), artifact.Scope.ProjectId.ToString("D"),
                artifact.Scope.MappingId.ToString("D"), artifact.Scope.RevisionId.ToString("D")),
            new("sha256", artifact.Digest.Value),
            new(context.Text(provenance!.BackendId, context.Example), context.Text(provenance.EngineRevision, context.Example), context.Text(provenance.BackendBuild, context.Example)),
            context.Text(provenance.AdapterBuild, context.Example), provenance.Confirmed);
    }

    public static ProjectionReportV1 Projection(MappingProjectionResult result, NativeArtifact artifact,
        RevisionReportPolicy limits, CancellationToken cancellationToken = default) =>
        ProjectionCore(result, artifact, new(limits, false, cancellationToken));

    public static ValidationReportV1 Validation(MappingValidationResult result, NativeArtifact artifact,
        RevisionReportPolicy limits, Func<NativeLocator, ProjectionNodeReference?>? projectionSelector = null,
        CancellationToken cancellationToken = default) =>
        ValidationCore(result, artifact, new(limits, false, cancellationToken), projectionSelector);

    /// <summary>Explicit development-example compatibility; the API still applies its combined response ceiling.</summary>
    public static ProjectionReportV1 ProjectionForExample(MappingProjectionResult result, NativeArtifact artifact,
        CancellationToken cancellationToken = default) => ProjectionCore(result, artifact, new(new(), true, cancellationToken));

    /// <summary>Preserves the example's historic text/path and validity semantics, never used for report admission.</summary>
    public static ValidationReportV1 ValidationForExample(MappingValidationResult result, NativeArtifact artifact,
        Func<NativeLocator, ProjectionNodeReference?>? projectionSelector = null, CancellationToken cancellationToken = default) =>
        ValidationCore(result, artifact, new(new(), true, cancellationToken), projectionSelector);

    private static ProjectionReportV1 ProjectionCore(MappingProjectionResult result, NativeArtifact artifact, Context context)
    {
        ArgumentNullException.ThrowIfNull(result);
        var provenance = ProvenanceCore(result.Provenance, artifact, context);
        context.CheckProvenance(provenance, artifact);
        var status = Status(result.Status);
        context.Require((status == OperationStatusV1.Succeeded) == (result.Projection is not null));
        var projection = result.Projection;
        if (result.Projection is { } document)
        {
            var observed = document.Provenance;
            context.Require(provenance.Confirmed && observed is not null && observed.Revision == provenance.Revision &&
                observed.NativeArtifactDigest == provenance.NativeArtifactDigest && observed.Backend == provenance.Backend &&
                observed.AdapterVersion == provenance.AdapterVersion);
            if (!context.Example)
                projection = StrictProjectionLinks(document, context);
        }
        var diagnostics = context.Copy(result.Diagnostics, diagnostic =>
        {
            context.Diagnostic(diagnostic);
            return new OperationDiagnosticV1(context.Text(diagnostic.Code, false), diagnostic.Severity,
                context.Text(diagnostic.Message), diagnostic.MessageTruncated, diagnostic.NativeLocator);
        }, context.Limits.DiagnosticLimit);
        return new(status, provenance, projection, diagnostics);
    }

    private static ValidationReportV1 ValidationCore(MappingValidationResult result, NativeArtifact artifact, Context context,
        Func<NativeLocator, ProjectionNodeReference?>? selector)
    {
        ArgumentNullException.ThrowIfNull(result);
        var provenance = ProvenanceCore(result.Provenance, artifact, context);
        context.CheckProvenance(provenance, artifact);
        var status = Status(result.Status);
        context.Require(result.Diagnostics is not null);
        var diagnosticCount = result.Diagnostics!.Count;
        context.Count(diagnosticCount, context.Limits.DiagnosticLimit);
        for (var index = 0; index < diagnosticCount; index++)
        {
            context.Require(result.Diagnostics.Count == diagnosticCount);
            context.Diagnostic(result.Diagnostics[index]);
        }
        context.Require(result.Diagnostics.Count == diagnosticCount);
        if (context.Example)
            context.Require(result.TotalIssueCount is >= 0 and <= InspectionPolicy.MaximumValidationIssues &&
                result.EmittedIssueCount >= 0 && result.EmittedIssueCount <= result.TotalIssueCount);
        else
        {
            context.Count(result.TotalIssueCount, context.Limits.ValidationIssueLimit);
            context.Count(result.EmittedIssueCount, context.Limits.DiagnosticLimit);
            context.Require(result.EmittedIssueCount <= result.TotalIssueCount);
        }
        if (result.IsValid is { } isValid)
            context.Require(provenance.Confirmed && status == (isValid ? OperationStatusV1.Succeeded : OperationStatusV1.InvalidMapping) &&
                isValid == (result.TotalIssueCount == 0) && result.EmittedIssueCount == result.Diagnostics.Count &&
                (result.Truncated || result.EmittedIssueCount == result.TotalIssueCount));
        else if (context.Example)
            context.Require(status != OperationStatusV1.Succeeded && result.TotalIssueCount == 0 && result.EmittedIssueCount == 0 && !result.Truncated);
        var diagnostics = context.Copy(result.Diagnostics, diagnostic =>
        {
            context.Diagnostic(diagnostic);
            var native = diagnostic.NativeLocator;
            var owner = context.Owner(diagnostic.Owner);
            var location = context.OptionalText(diagnostic.Location);
            return new ValidationDiagnosticV1(context.Text(diagnostic.Code, false), diagnostic.Severity,
                context.Text(diagnostic.Message), diagnostic.MessageTruncated, native, owner, location,
                diagnostic.LocationTruncated);
        }, context.Limits.DiagnosticLimit);
        var report = new ValidationReportV1(status, provenance, result.IsValid, result.TotalIssueCount, result.EmittedIssueCount, result.Truncated, diagnostics);
        if (!context.Example)
            FerruleInspectionBackend.ValidateStoredValidationReport(report, context.Limits, context.Token);
        if (selector is not null)
        {
            // Provider admission sees native-only evidence before trusted response linking.
            var linked = new ValidationDiagnosticV1[diagnostics.Count];
            for (var index = 0; index < linked.Length; index++)
            {
                context.Token.ThrowIfCancellationRequested();
                var diagnostic = diagnostics[index];
                var link = diagnostic.NativeLocator is { } locator ? selector(locator) : null;
                context.Token.ThrowIfCancellationRequested();
                linked[index] = diagnostic with { ProjectionLocator = link };
            }
            report = report with { Diagnostics = Array.AsReadOnly(linked) };
        }
        return report;
    }

    internal static OperationStatusV1 Status(MappingOperationStatus status) => status switch
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
        _ => throw new RevisionReportValidationException(RevisionReportValidationFailure.Integrity)
    };

    private sealed partial class Context
    {
        private static readonly UTF8Encoding Utf8 = new(false, true);
        private int _items;
        internal Context(RevisionReportPolicy limits, bool example, CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(limits);
            token.ThrowIfCancellationRequested();
            Limits = limits; Example = example; Token = token;
        }
        internal RevisionReportPolicy Limits { get; }
        internal bool Example { get; }
        internal CancellationToken Token { get; }
        internal void CheckProvenance(OperationProvenanceV1 provenance, NativeArtifact artifact)
        {
            if (Example) return;
            if (artifact.ByteCount > Limits.NativeByteLimit)
                throw new RevisionReportValidationException(RevisionReportValidationFailure.ResourceLimit);
            Text(provenance.Revision.OrganizationId, false); Text(provenance.Revision.ProjectId, false);
            Text(provenance.Revision.MappingId, false); Text(provenance.Revision.RevisionId, false);
            Text(provenance.NativeArtifactDigest.Algorithm, false); Text(provenance.NativeArtifactDigest.Value, false);
            Text(provenance.Backend.Id, false); Text(provenance.Backend.Revision, false); Text(provenance.Backend.BuildId, false);
            Text(provenance.AdapterVersion, false);
        }
        internal void Require([DoesNotReturnIf(false)] bool value)
        {
            Token.ThrowIfCancellationRequested();
            if (!value) throw new RevisionReportValidationException(RevisionReportValidationFailure.Integrity);
        }
        internal void Count(int count, int maximum)
        {
            Require(count >= 0);
            if (count > maximum) throw new RevisionReportValidationException(RevisionReportValidationFailure.ResourceLimit);
        }
        internal string Text(string value, bool allowEmpty = true)
        {
            Require(value is not null);
            var maximum = Example ? RevisionReportPolicy.MaximumResponseByteLimit : Limits.TextByteLimit;
            if (value!.Length > maximum) throw new RevisionReportValidationException(RevisionReportValidationFailure.ResourceLimit);
            try
            {
                var encoder = Utf8.GetEncoder();
                var bytes = 0;
                for (var index = 0; index < value.Length; index += 1024)
                {
                    Token.ThrowIfCancellationRequested();
                    var count = Math.Min(1024, value.Length - index);
                    bytes += encoder.GetByteCount(value.AsSpan(index, count), index + count == value.Length);
                    if (bytes > maximum) throw new RevisionReportValidationException(RevisionReportValidationFailure.ResourceLimit);
                }
            }
            catch (EncoderFallbackException) { throw new RevisionReportValidationException(RevisionReportValidationFailure.Integrity); }
            Require(allowEmpty || !string.IsNullOrWhiteSpace(value));
            return value;
        }
        internal string? OptionalText(string? value) => value is null ? null : Text(value);
        internal void Diagnostic(MappingDiagnostic value) => Require(value is not null && Enum.IsDefined(value.Severity));
        internal IReadOnlyList<TOut> Copy<TIn, TOut>(IReadOnlyList<TIn> values, Func<TIn, TOut> map, int maximum)
        {
            Require(values is not null);
            var count = values!.Count;
            Count(count, maximum);
            if (!Example)
            {
                if (count > Limits.ItemLimit - _items) throw new RevisionReportValidationException(RevisionReportValidationFailure.ResourceLimit);
                _items += count;
            }
            var owned = new TOut[count];
            for (var index = 0; index < count; index++)
            {
                Require(values.Count == count);
                owned[index] = map(values[index]);
            }
            Require(values.Count == count);
            return Array.AsReadOnly(owned);
        }
    }
}
