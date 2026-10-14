using System.Numerics;
using Weft.Application;
using Weft.Contracts.Projections;
using Weft.Domain;

namespace Weft.MappingBackends;

public sealed partial class FerruleInspectionBackend
{
    private static readonly RevisionReportPolicy DefaultReportLimits = new();

    private static void ReportProvider(Action validate)
    {
        try { validate(); }
        catch (ProjectionLimitException)
        {
            throw new RevisionReportValidationException(RevisionReportValidationFailure.ResourceLimit);
        }
        catch (Exception error) when (error is ProtocolException or ArgumentException or InvalidOperationException or
            NullReferenceException or IndexOutOfRangeException or OverflowException)
        {
            throw new RevisionReportValidationException(RevisionReportValidationFailure.Integrity);
        }
    }

    internal static void ValidateStoredProjectionDocument(ProjectionDocumentV1 document, RevisionReportPolicy limits,
        CancellationToken cancellationToken = default) => ReportProvider(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        Require(document is not null);
        ArgumentNullException.ThrowIfNull(limits);
        // Provenance binds to the historical key in the neutral validator, never to a current runner here.
        ValidateProjection(new(document.Coverage, document.Boundaries, document.Fields, document.Parameters,
            document.Rules, document.Controls, document.Relationships, document.Diagnostics, document.Capabilities),
            limits, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    });

    private sealed class ProjectionBudget
    {
        private readonly CancellationToken token;
        private readonly bool reportOnly;
        private int items;
        internal ProjectionBudget(RevisionReportPolicy limits, CancellationToken cancellationToken, bool reportOnly = true)
        {
            ArgumentNullException.ThrowIfNull(limits);
            Limits = limits;
            token = cancellationToken;
            this.reportOnly = reportOnly;
            Check();
        }
        internal RevisionReportPolicy Limits { get; }
        internal void Check() => token.ThrowIfCancellationRequested();

        internal void Charge(int count)
        {
            Check();
            Require(count >= 0);
            if (count > Limits.ItemLimit - items) throw new ProjectionLimitException();
            items += count;
        }

        internal void Array<T>(IReadOnlyList<T> values, int? maximum = null)
        {
            Check();
            Require(values is not null);
            var count = values.Count;
            Check();
            Require(count >= 0);
            if (count > Math.Min(maximum ?? Limits.ItemLimit, Limits.ItemLimit)) throw new ProjectionLimitException();
            Charge(count);
        }

        internal void Text(string value, bool allowEmpty = true)
        {
            Check();
            Require(value is not null);
            // Preserve legacy default classification; report inputs reject huge strings before a whitespace scan.
            if (reportOnly && value.Length > Limits.TextByteLimit) throw new ProjectionLimitException();
            Require(allowEmpty || !string.IsNullOrWhiteSpace(value));
            if (StrictUtf8.GetByteCount(value) > Limits.TextByteLimit) throw new ProjectionLimitException();
            Check();
        }

        internal BigInteger Unsigned(string value) => ProjectionUnsignedInteger(value, this);
        internal void Locator(NativeLocator value) => ProjectionLocator(value, this);
        internal void Literal(LiteralValue value) => ProjectionLiteral(value, this);
        internal void Cardinality(FieldCardinality value) => ProjectionCardinality(value, this);
    }
}
