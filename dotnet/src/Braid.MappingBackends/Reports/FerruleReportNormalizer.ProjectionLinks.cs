using Braid.Application;
using Braid.Contracts.Projections;

namespace Braid.MappingBackends;

public static partial class FerruleReportNormalizer
{
    private static ProjectionDocumentV1 StrictProjectionLinks(ProjectionDocumentV1 document, Context context)
    {
        context.Require(document.Coverage is not null);
        // Capture the exact link-bearing records first. Provider admission must see their original
        // references and locators before any unsupported navigation can be removed.
        var gaps = context.Copy(document.Coverage.Gaps, gap =>
        {
            context.Require(gap is not null);
            return gap with { };
        }, context.Limits.DiagnosticLimit);
        var diagnostics = context.Copy(document.Diagnostics, diagnostic =>
        {
            context.Require(diagnostic is not null);
            return diagnostic with { };
        }, context.Limits.DiagnosticLimit);
        var captured = document with
        {
            Coverage = document.Coverage with { Gaps = gaps },
            Diagnostics = diagnostics
        };
        FerruleInspectionBackend.ValidateStoredProjectionDocument(captured, context.Limits, context.Token);
        var origins = ProjectionOriginLinks.Create(captured, context.Limits, context.Token);

        var normalizedGaps = new CoverageGap[gaps.Count];
        for (var index = 0; index < normalizedGaps.Length; index++)
        {
            context.Token.ThrowIfCancellationRequested();
            var gap = gaps[index];
            normalizedGaps[index] = gap with
            {
                ProjectionLocator = ExactProjectionLink(gap.NativeLocator, gap.ProjectionLocator, origins, context)
            };
        }
        var normalizedDiagnostics = new ProjectionDiagnostic[diagnostics.Count];
        for (var index = 0; index < normalizedDiagnostics.Length; index++)
        {
            context.Token.ThrowIfCancellationRequested();
            var diagnostic = diagnostics[index];
            normalizedDiagnostics[index] = diagnostic with
            {
                ProjectionLocator = ExactProjectionLink(diagnostic.NativeLocator, diagnostic.ProjectionLocator, origins, context)
            };
        }
        context.Token.ThrowIfCancellationRequested();
        return captured with
        {
            Coverage = captured.Coverage with { Gaps = Array.AsReadOnly(normalizedGaps) },
            Diagnostics = Array.AsReadOnly(normalizedDiagnostics)
        };
    }

    private static ProjectionNodeReference? ExactProjectionLink(NativeLocator? native, ProjectionNodeReference? reference,
        ProjectionOriginLinks origins, Context context)
    {
        context.Token.ThrowIfCancellationRequested();
        return reference is not null && origins.Find(native, context.Token) == reference ? reference : null;
    }
}
