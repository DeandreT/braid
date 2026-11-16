using System.Globalization;
using Braid.Contracts.Comparisons;
using Braid.Contracts.Projections;
using Braid.Domain;

namespace Braid.Application.Comparisons;

public sealed partial class RevisionComparisonService
{
    public RevisionComparisonV1 Compare(RevisionComparisonSide left, RevisionComparisonSide right,
        RevisionComparisonLimits? limits = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (left is null || right is null || left.Scope.OrganizationId != right.Scope.OrganizationId ||
            left.Scope.ProjectId != right.Scope.ProjectId || left.Scope.MappingId != right.Scope.MappingId)
            throw Invalid();
        return new ComparisonContext(left, right, limits ?? new(), cancellationToken).Run();
    }
    private static RevisionComparisonException Invalid() => new(RevisionComparisonFailure.InvalidInput);
    private static RevisionComparisonException Limit() => new(RevisionComparisonFailure.ResourceLimit);
    public override string ToString() => "Pure revision comparison service.";

    private sealed partial class ComparisonContext
    {
        private readonly Budget _budget;
        private readonly SideData _left;
        private readonly SideData _right;
        private readonly List<Group> _groups = [];
        private bool _detailsCompatible;

        internal ComparisonContext(RevisionComparisonSide left, RevisionComparisonSide right,
            RevisionComparisonLimits limits, CancellationToken token)
        {
            _budget = new(limits, token);
            _left = new(left);
            _right = new(right);
        }

        internal RevisionComparisonV1 Run()
        {
            ScanSide(_left.Side);
            ScanSide(_right.Side);
            _detailsCompatible = Compatible();
            MatchAll();
            var emitted = new List<ComparisonItemV1>(Math.Min(_groups.Count, _budget.Limits.OutputItems));
            var different = false;
            var uncertain = _left.Document.Coverage.Status != CoverageStatus.Complete ||
                _right.Document.Coverage.Status != CoverageStatus.Complete;
            var candidatesClipped = false;
            foreach (var group in _groups)
            {
                _budget.Work(1);
                var item = Item(group);
                foreach (var facet in item.Facets)
                {
                    _budget.Work(1);
                    different |= facet.HasObservedDifference;
                    uncertain |= facet.HasUncertainty;
                }
                candidatesClipped |= item.CandidatesTruncated;
                if (emitted.Count < _budget.Limits.OutputItems) emitted.Add(item);
            }
            var validationIncluded = _left.Side.Validation is not null || _right.Side.Validation is not null;
            var truncated = candidatesClipped || emitted.Count < _groups.Count;
            uncertain |= truncated;
            var result = new RevisionComparisonV1("braid.revision-comparison.v1", "braid.projection-facts-comparison.v1",
                Citation(_left.Side), Citation(_right.Side), CopyCoverage(_left.Document.Coverage),
                CopyCoverage(_right.Document.Coverage), validationIncluded, ValidationSummary(_left.Side),
                ValidationSummary(_right.Side), different, uncertain, _groups.Count, emitted.Count,
                truncated, Array.AsReadOnly(emitted.ToArray()));
            try
            {
                var encoded = RevisionReportResponseSerializer.Serialize(result, _budget.Limits.OutputUtf8Bytes, _budget.Token);
                // Account for output string/punctuation work too; the buffer is independently bounded.
                _budget.Work(System.Text.Encoding.UTF8.GetCharCount(encoded));
            }
            catch (RevisionReportValidationException error)
            {
                throw error.Failure == RevisionReportValidationFailure.ResourceLimit ? Limit() : Invalid();
            }
            _budget.Token.ThrowIfCancellationRequested();
            return result;
        }

        private bool Compatible()
        {
            var l = _left.Side.Format;
            var r = _right.Side.Format;
            var lp = _left.Side.Profile;
            var rp = _right.Side.Profile;
            // Every string comparison is charged, even though metadata has already been verified.
            var same = _budget.Compare(lp.BackendId, rp.BackendId) == 0;
            same &= _budget.Compare(lp.BackendRevision, rp.BackendRevision) == 0;
            same &= _budget.Compare(lp.BackendBuild, rp.BackendBuild) == 0;
            same &= _budget.Compare(lp.AdapterBuild, rp.AdapterBuild) == 0;
            same &= _budget.Compare(l.SchemaVersion, r.SchemaVersion) == 0;
            same &= _budget.Compare(l.AlgorithmVersion, r.AlgorithmVersion) == 0;
            same &= _budget.Compare(l.CodecVersion, r.CodecVersion) == 0;
            var lb = _left.Side.Policy.CopyCanonicalUtf8();
            var rb = _right.Side.Policy.CopyCanonicalUtf8();
            _budget.Work(lb.Length + rb.Length);
            same &= _budget.Compare(Sha256Digest.FromBytes(lb).Value, Sha256Digest.FromBytes(rb).Value) == 0;
            return same;
        }

        private ComparisonSideCitationV1 Citation(RevisionComparisonSide side)
        {
            var p = side.Projection.Report.Document!.Provenance;
            var scope = Scope(side.Scope);
            var provenance = new ProjectionProvenance(Scope(side.Scope), new(p.NativeArtifactDigest.Algorithm,
                p.NativeArtifactDigest.Value), new(p.Backend.Id, p.Backend.Revision, p.Backend.BuildId),
                p.AdapterVersion, p.ProjectionVersion);
            var draft = side.DraftId is { } id ? new ComparisonDraftCitationV1(side.Scope.OrganizationId.ToString("D"),
                side.Scope.ProjectId.ToString("D"), side.Scope.MappingId.ToString("D"), id.ToString("D"), side.DraftVersion!.Value.ToString(CultureInfo.InvariantCulture)) : null;
            return new(draft is null ? scope : null, draft is null ? side.Sequence.ToString(CultureInfo.InvariantCulture) : null,
                new("sha256", side.NativeDigest.Value), side.NativeByteCount.ToString(CultureInfo.InvariantCulture),
                provenance, side.Projection.Metadata is { } projection ? ReportCitation(projection) : null,
                side.Validation?.Metadata is { } validation ? ReportCitation(validation) : null, draft);
        }
        private ComparisonReportCitationV1 ReportCitation(RevisionReportResult metadata)
        {
            var canonical = metadata.Key.CopyCanonicalUtf8();
            _budget.Work(canonical.Length);
            return new(metadata.DerivationId.ToString("D"), metadata.Generation.ToString(CultureInfo.InvariantCulture),
                Sha256Digest.FromBytes(canonical).Value);
        }
        private static ScopedRevisionId Scope(RevisionScope scope) => new(scope.OrganizationId.ToString("D"),
            scope.ProjectId.ToString("D"), scope.MappingId.ToString("D"), scope.RevisionId.ToString("D"));
        private ComparisonValidationSummaryV1? ValidationSummary(RevisionComparisonSide side)
        {
            if (side.Validation is not { } selected) return null;
            var report = selected.Report;
            var textTruncated = false;
            foreach (var item in report.Diagnostics)
            {
                _budget.Work(1);
                textTruncated |= item.MessageTruncated || item.LocationTruncated;
            }
            return new(report.IsValid!.Value, report.TotalIssueCount, report.EmittedIssueCount, report.Truncated, textTruncated);
        }

        private ProjectionCoverage CopyCoverage(ProjectionCoverage coverage)
        {
            var gaps = new CoverageGap[coverage.Gaps.Count];
            for (var index = 0; index < gaps.Length; index++)
            {
                _budget.Work(1);
                var g = coverage.Gaps[index];
                gaps[index] = new(g.Code, g.Message, CopyLocator(g.NativeLocator),
                    g.ProjectionLocator is { } reference ? CopyReference(reference) : null);
            }
            return new(coverage.Status, Array.AsReadOnly(gaps));
        }
        private static NativeLocator CopyLocator(NativeLocator value) => value switch
        {
            DocumentNativeLocator v => new DocumentNativeLocator(v.DocumentId),
            JsonPointerNativeLocator v => new JsonPointerNativeLocator(v.DocumentId, v.Pointer),
            GraphNodeNativeLocator v => new GraphNodeNativeLocator(v.DocumentId, v.NodeId, v.FunctionId),
            TextSpanNativeLocator v => new TextSpanNativeLocator(v.DocumentId, v.StartOffset, v.Length, v.Unit),
            OpaqueNativeLocator v => new OpaqueNativeLocator(v.DocumentId, v.Scheme, v.Value),
            _ => throw Invalid()
        };
        private static ProjectionNodeReference CopyReference(ProjectionNodeReference value) => value switch
        {
            BoundaryNodeReference v => new BoundaryNodeReference(v.BoundaryId),
            FieldNodeReference v => new FieldNodeReference(v.FieldId),
            ParameterNodeReference v => new ParameterNodeReference(v.ParameterId),
            RuleNodeReference v => new RuleNodeReference(v.RuleId),
            ControlNodeReference v => new ControlNodeReference(v.ControlId),
            _ => throw Invalid()
        };

        private sealed class SideData(RevisionComparisonSide side)
        {
            internal RevisionComparisonSide Side { get; } = side;
            internal ProjectionDocumentV1 Document => Side.Projection.Report.Document!;
            internal Dictionary<(ComparisonEntityV1, string), string> Tokens { get; } = [];
        }
        private sealed record Entity(ComparisonEntityV1 Kind, string Id, object Value)
        {
            public override string ToString() => "Comparison entity.";
        }
        private sealed record Group(ComparisonEntityV1 Kind, string Key, List<Entity> Left, List<Entity> Right, bool Unresolved)
        {
            public override string ToString() => "Comparison correspondence group.";
        }
    }
}
