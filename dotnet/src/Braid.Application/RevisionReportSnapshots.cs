using System.Text;
using Braid.Contracts.Operations;
using Braid.Contracts.Projections;
using Braid.Contracts.Validation;
using Braid.Domain;

namespace Braid.Application;

internal static partial class RevisionReportSnapshots
{
    internal static ProjectionReportV1 CopyProjection(ProjectionReportV1 report, RevisionReportPolicy caps,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        var copy = new SnapshotCopy(caps, cancellationToken);
        return new(copy.Known(report.Status), copy.OperationProvenance(report.Provenance),
            report.Document is { } document ? copy.Document(document) : null,
            copy.List(report.Diagnostics, copy.OperationDiagnostic, caps.DiagnosticLimit));
    }

    internal static ValidationReportV1 CopyValidation(ValidationReportV1 report, RevisionReportPolicy caps,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        var copy = new SnapshotCopy(caps, cancellationToken);
        copy.Count(report.TotalIssueCount, caps.ValidationIssueLimit);
        copy.Count(report.EmittedIssueCount, Math.Min(report.TotalIssueCount, caps.DiagnosticLimit));
        return new(copy.Known(report.Status), copy.OperationProvenance(report.Provenance), report.IsValid,
            report.TotalIssueCount, report.EmittedIssueCount, report.Truncated,
            copy.List(report.Diagnostics, copy.ValidationDiagnostic, caps.DiagnosticLimit));
    }

    private sealed partial class SnapshotCopy
    {
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);
        private readonly RevisionReportPolicy _caps;
        private readonly CancellationToken _cancellationToken;
        private int _items;

        internal SnapshotCopy(RevisionReportPolicy caps, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(caps);
            cancellationToken.ThrowIfCancellationRequested();
            _caps = caps;
            _cancellationToken = cancellationToken;
        }

        private static RevisionReportValidationException Integrity() => new(RevisionReportValidationFailure.Integrity);
        private static RevisionReportValidationException Limit() => new(RevisionReportValidationFailure.ResourceLimit);

        private T Required<T>(T? value) where T : class
        {
            _cancellationToken.ThrowIfCancellationRequested();
            return value ?? throw Integrity();
        }

        internal T Known<T>(T value) where T : struct, Enum
        {
            _cancellationToken.ThrowIfCancellationRequested();
            return Enum.IsDefined(value) ? value : throw Integrity();
        }

        internal void Count(int count, int maximum)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (count < 0)
                throw Integrity();
            if (count > maximum)
                throw Limit();
        }

        private string Text(string value)
        {
            Required(value);
            // UTF8 requires at least this many bytes: refuse huge inputs before scanning them.
            if (value.Length > _caps.TextByteLimit)
                throw Limit();
            try
            {
                if (StrictUtf8.GetByteCount(value) > _caps.TextByteLimit)
                    throw Limit();
            }
            catch (EncoderFallbackException)
            {
                throw Integrity();
            }
            _cancellationToken.ThrowIfCancellationRequested();
            return value;
        }

        private string? OptionalText(string? value) => value is null ? null : Text(value);

        internal IReadOnlyList<T> List<T>(IReadOnlyList<T> values, Func<T, T> copy, int? maximum = null)
        {
            Required(values);
            int count;
            try { count = values.Count; }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { throw Integrity(); }
            Count(count, maximum ?? _caps.ItemLimit);
            if (count > _caps.ItemLimit - _items)
                throw Limit();
            _items += count;
            var owned = new T[count];
            for (var index = 0; index < count; index++)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                T item;
                try
                {
                    if (values.Count != count)
                        throw Integrity();
                    item = values[index];
                }
                catch (OperationCanceledException) { throw; }
                catch (RevisionReportValidationException) { throw; }
                catch (Exception) { throw Integrity(); }
                owned[index] = copy(item);
            }
            _cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (values.Count != count)
                    throw Integrity();
            }
            catch (OperationCanceledException) { throw; }
            catch (RevisionReportValidationException) { throw; }
            catch (Exception) { throw Integrity(); }
            return Array.AsReadOnly(owned);
        }

        private ScopedRevisionId Scope(ScopedRevisionId value)
        {
            Required(value);
            return new(Text(value.OrganizationId), Text(value.ProjectId), Text(value.MappingId), Text(value.RevisionId));
        }

        private ArtifactDigest Digest(ArtifactDigest value)
        {
            Required(value);
            return new(Text(value.Algorithm), Text(value.Value));
        }

        private BackendIdentity Backend(BackendIdentity value)
        {
            Required(value);
            return new(Text(value.Id), Text(value.Revision), Text(value.BuildId));
        }

        internal OperationProvenanceV1? OperationProvenance(OperationProvenanceV1? value) => value is null ? null :
            new(Scope(value.Revision), Digest(value.NativeArtifactDigest), Backend(value.Backend), Text(value.AdapterVersion), value.Confirmed);

        internal OperationDiagnosticV1 OperationDiagnostic(OperationDiagnosticV1 value)
        {
            Required(value);
            return new(Text(value.Code), Known(value.Severity), Text(value.Message), value.MessageTruncated,
                value.NativeLocator is { } locator ? Locator(locator) : null);
        }

        private NativeLocator Locator(NativeLocator value) => Required(value) switch
        {
            DocumentNativeLocator item => new DocumentNativeLocator(Text(item.DocumentId)),
            JsonPointerNativeLocator item => new JsonPointerNativeLocator(Text(item.DocumentId), Text(item.Pointer)),
            GraphNodeNativeLocator item => new GraphNodeNativeLocator(Text(item.DocumentId), Text(item.NodeId), OptionalText(item.FunctionId)),
            TextSpanNativeLocator item => new TextSpanNativeLocator(Text(item.DocumentId), Text(item.StartOffset), Text(item.Length), Known(item.Unit)),
            OpaqueNativeLocator item => new OpaqueNativeLocator(Text(item.DocumentId), Text(item.Scheme), Text(item.Value)),
            _ => throw Integrity()
        };

        private ProjectionNodeReference Reference(ProjectionNodeReference value) => Required(value) switch
        {
            BoundaryNodeReference item => new BoundaryNodeReference(Text(item.BoundaryId)),
            FieldNodeReference item => new FieldNodeReference(Text(item.FieldId)),
            ParameterNodeReference item => new ParameterNodeReference(Text(item.ParameterId)),
            RuleNodeReference item => new RuleNodeReference(Text(item.RuleId)),
            ControlNodeReference item => new ControlNodeReference(Text(item.ControlId)),
            _ => throw Integrity()
        };

        private LiteralValue Literal(LiteralValue value) => Required(value) switch
        {
            StringLiteralValue item => new StringLiteralValue(Text(item.Value)),
            BooleanLiteralValue item => new BooleanLiteralValue(item.Value),
            ExactNumberLiteralValue item => new ExactNumberLiteralValue(Known(item.Flavor), Text(item.Value)),
            NullLiteralValue item => new NullLiteralValue(Known(item.NullKind)),
            _ => throw Integrity()
        };
    }
}
