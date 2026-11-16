using System.Globalization;
using System.Text;
using Braid.Contracts.Operations;
using Braid.Contracts.Projections;
using Braid.Contracts.Validation;

namespace Braid.Application.Comparisons;

public sealed partial class RevisionComparisonService
{
    private sealed class Budget(RevisionComparisonLimits limits, CancellationToken token)
    {
        private static readonly UTF8Encoding Utf8 = new(false, true);
        private int _items;
        private int _work;
        internal RevisionComparisonLimits Limits { get; } = limits;
        internal CancellationToken Token { get; } = token;
        internal void Work(int amount)
        {
            Token.ThrowIfCancellationRequested();
            if (amount < 0 || amount > Limits.WorkUnits - _work) throw Limit();
            _work += amount;
        }
        internal void Input(int count = 1)
        {
            Work(count);
            if (count < 0 || count > Limits.InputItems - _items) throw Limit();
            _items += count;
        }
        internal void Text(string value)
        {
            if (value is null) throw Invalid();
            if (value.Length > 2048) throw Limit();
            Work(value.Length);
            try { if (Utf8.GetByteCount(value) > 2048) throw Limit(); }
            catch (EncoderFallbackException) { throw Invalid(); }
        }
        internal void Optional(string? value) { if (value is not null) Text(value); }
        internal void Known<T>(T value) where T : struct, Enum
        {
            Work(1);
            if (!Enum.IsDefined(value)) throw Invalid();
        }
        internal void Array<T>(IReadOnlyList<T> values, Action<T> scan)
        {
            if (values is null) throw Invalid();
            Input(values.Count);
            for (var i = 0; i < values.Count; i++) { Work(1); scan(values[i]); }
        }
        internal string Key(params string[] values) => Key((IEnumerable<string>)values);
        internal string Key(IEnumerable<string> values)
        {
            var result = new StringBuilder();
            foreach (var value in values)
            {
                var length = value.Length.ToString(CultureInfo.InvariantCulture);
                Work(value.Length + length.Length + 2);
                result.Append(length).Append(':').Append(value).Append(';');
            }
            return result.ToString();
        }
        internal int Compare(string left, string right)
        {
            Work(1);
            var count = Math.Min(left.Length, right.Length);
            for (var i = 0; i < count; i++)
            {
                Work(1);
                var result = left[i].CompareTo(right[i]);
                if (result != 0) return result;
            }
            return left.Length.CompareTo(right.Length);
        }
        // A local stable merge avoids framework Sort wrapping cancellation/limit exceptions.
        internal void Sort<T>(List<T> values, Comparison<T> compare)
        {
            Work(values.Count);
            if (values.Count < 2) return;
            var from = values.ToArray(); var to = new T[from.Length];
            for (var width = 1; width < from.Length; width *= 2)
            {
                for (var start = 0; start < from.Length; start += width * 2)
                {
                    var middle = Math.Min(start + width, from.Length);
                    var end = Math.Min(start + width * 2, from.Length);
                    var l = start; var r = middle;
                    for (var dest = start; dest < end; dest++)
                    {
                        Work(1);
                        to[dest] = l < middle && (r >= end || compare(from[l], from[r]) <= 0) ? from[l++] : from[r++];
                    }
                }
                (from, to) = (to, from);
            }
            values.Clear(); values.AddRange(from);
        }
    }

    private sealed partial class ComparisonContext
    {
        private void ScanSide(RevisionComparisonSide side)
        {
            var p = side.Projection.Report;
            _budget.Input(); _budget.Known(p.Status); ScanProvenance(p.Provenance);
            _budget.Array(p.Diagnostics, v =>
            {
                _budget.Input(); _budget.Text(v.Code); _budget.Known(v.Severity); _budget.Text(v.Message);
                if (v.NativeLocator is not null) ScanLocator(v.NativeLocator);
            });
            var doc = p.Document ?? throw Invalid();
            _budget.Input(); ScanProjectionProvenance(doc.Provenance);
            _budget.Input(); _budget.Known(doc.Coverage.Status);
            _budget.Array(doc.Coverage.Gaps, v =>
            {
                _budget.Input(); _budget.Text(v.Code); _budget.Text(v.Message); ScanLocator(v.NativeLocator);
                if (v.ProjectionLocator is not null) ScanReference(v.ProjectionLocator);
            });
            _budget.Array(doc.Boundaries, v =>
            {
                _budget.Input(); _budget.Text(v.Id); _budget.Text(v.Name); _budget.Known(v.Role);
                _budget.Text(v.Format); _budget.Known(v.Shape); _budget.Optional(v.RootFieldId); ScanLocator(v.NativeLocator);
            });
            _budget.Array(doc.Fields, v =>
            {
                _budget.Input(); _budget.Text(v.Id); _budget.Text(v.BoundaryId); _budget.Text(v.Name);
                _budget.Array(v.Path, ScanPath); ScanType(v.DataType);
                _budget.Input(); _budget.Known(v.Cardinality.Kind); _budget.Optional(v.Cardinality.Minimum);
                _budget.Optional(v.Cardinality.Maximum); _budget.Known(v.Requirement); _budget.Known(v.Nullability);
                ScanLocator(v.NativeLocator);
            });
            _budget.Array(doc.Parameters, v =>
            {
                _budget.Input(); _budget.Text(v.Id); _budget.Text(v.Name); ScanType(v.DataType);
                _budget.Known(v.Requirement); if (v.DefaultValue is not null) ScanLiteral(v.DefaultValue);
                ScanLocator(v.NativeLocator);
            });
            _budget.Array(doc.Rules, v =>
            {
                _budget.Input(); _budget.Text(v.Id); _budget.Text(v.OutputFieldId); _budget.Text(v.Summary);
                _budget.Array(v.Dependencies, ScanDependency); _budget.Array(v.Details, x => ScanDetail(x, 0));
                ScanLocator(v.NativeLocator);
            });
            _budget.Array(doc.Controls, v =>
            {
                _budget.Input(); _budget.Text(v.Id); _budget.Known(v.Kind); _budget.Text(v.Summary);
                _budget.Array(v.AppliesTo, ScanReference); _budget.Array(v.Dependencies, ScanDependency);
                _budget.Array(v.Details, x => ScanDetail(x, 0)); ScanLocator(v.NativeLocator);
            });
            _budget.Array(doc.Relationships, v =>
            {
                _budget.Input(); _budget.Text(v.Id); _budget.Known(v.Kind); ScanReference(v.From); ScanReference(v.To);
            });
            _budget.Array(doc.Diagnostics, v =>
            {
                _budget.Input(); _budget.Text(v.Code); _budget.Known(v.Severity); _budget.Text(v.Message);
                if (v.NativeLocator is not null) ScanLocator(v.NativeLocator);
                if (v.ProjectionLocator is not null) ScanReference(v.ProjectionLocator);
            });
            _budget.Array(doc.Capabilities, v =>
            {
                _budget.Input(); _budget.Known(v.Operation); _budget.Known(v.Status); _budget.Text(v.Profile);
                _budget.Array(v.BoundaryIds, _budget.Text);
                _budget.Array(v.Conditions, c => { _budget.Input(); _budget.Text(c.Code); _budget.Text(c.Description); });
            });
            if (side.Validation is not { } validated) return;
            var report = validated.Report;
            _budget.Input(); _budget.Known(report.Status); ScanProvenance(report.Provenance);
            _budget.Array(report.Diagnostics, v =>
            {
                _budget.Input(); _budget.Text(v.Code); _budget.Known(v.Severity); _budget.Text(v.Message);
                _budget.Optional(v.Location); if (v.NativeLocator is not null) ScanLocator(v.NativeLocator);
                if (v.ProjectionLocator is not null) ScanReference(v.ProjectionLocator);
                if (v.Owner is not null) ScanOwner(v.Owner);
            });
        }
        private void ScanProjectionProvenance(ProjectionProvenance p)
        {
            _budget.Input(); ScanScope(p.Revision); ScanDigest(p.NativeArtifactDigest); ScanBackend(p.Backend);
            _budget.Text(p.AdapterVersion); _budget.Text(p.ProjectionVersion);
        }
        private void ScanProvenance(OperationProvenanceV1? p)
        {
            if (p is null) return;
            _budget.Input(); ScanScope(p.Revision); ScanDigest(p.NativeArtifactDigest); ScanBackend(p.Backend); _budget.Text(p.AdapterVersion);
        }
        private void ScanScope(ScopedRevisionId s)
        {
            _budget.Input(); _budget.Text(s.OrganizationId); _budget.Text(s.ProjectId); _budget.Text(s.MappingId); _budget.Text(s.RevisionId);
        }
        private void ScanDigest(ArtifactDigest d) { _budget.Input(); _budget.Text(d.Algorithm); _budget.Text(d.Value); }
        private void ScanBackend(BackendIdentity b)
        { _budget.Input(); _budget.Text(b.Id); _budget.Text(b.Revision); _budget.Text(b.BuildId); }
        private void ScanType(FieldDataType t)
        { _budget.Input(); _budget.Known(t.Kind); _budget.Array(t.ScalarTypes, _budget.Known); _budget.Optional(t.NativeName); }
        private void ScanPath(PathSegment v)
        {
            _budget.Input();
            switch (v)
            {
                case FieldPathSegment f: _budget.Text(f.Name); _budget.Optional(f.NamespaceUri); break;
                case AttributePathSegment a: _budget.Text(a.Name); _budget.Optional(a.NamespaceUri); break;
                case IndexPathSegment i: _budget.Text(i.Index); break;
                case AnyItemPathSegment: break;
                default: throw Invalid();
            }
        }
        private void ScanReference(ProjectionNodeReference value)
        {
            _budget.Input(); _budget.Text(value switch
            {
                BoundaryNodeReference v => v.BoundaryId,
                FieldNodeReference v => v.FieldId,
                ParameterNodeReference v => v.ParameterId,
                RuleNodeReference v => v.RuleId,
                ControlNodeReference v => v.ControlId,
                _ => throw Invalid()
            });
        }
        private void ScanLocator(NativeLocator value)
        {
            _budget.Input(); _budget.Text(value.DocumentId);
            switch (value)
            {
                case DocumentNativeLocator: break;
                case JsonPointerNativeLocator v: _budget.Text(v.Pointer); break;
                case GraphNodeNativeLocator v: _budget.Text(v.NodeId); _budget.Optional(v.FunctionId); break;
                case TextSpanNativeLocator v: _budget.Text(v.StartOffset); _budget.Text(v.Length); _budget.Known(v.Unit); break;
                case OpaqueNativeLocator v: _budget.Text(v.Scheme); _budget.Text(v.Value); break;
                default: throw Invalid();
            }
        }
        private void ScanLiteral(LiteralValue value)
        {
            _budget.Input();
            switch (value)
            {
                case StringLiteralValue v: _budget.Text(v.Value); break;
                case BooleanLiteralValue: break;
                case ExactNumberLiteralValue v: _budget.Known(v.Flavor); _budget.Text(v.Value); break;
                case NullLiteralValue v: _budget.Known(v.NullKind); break;
                default: throw Invalid();
            }
        }
        private void ScanDependency(RuleDependency value)
        {
            _budget.Input(); _budget.Known(value.Role);
            switch (value)
            {
                case FieldDependency v: _budget.Text(v.FieldId); break;
                case ParameterDependency v: _budget.Text(v.ParameterId); break;
                case ConstantDependency v: ScanLiteral(v.Value); break;
                case NodeDependency v: ScanReference(v.Node); break;
                default: throw Invalid();
            }
        }
        private void ScanDetail(DetailNode value, int depth)
        {
            _budget.Input(); if (depth > 16) throw Limit();
            switch (value)
            {
                case GroupDetail v: _budget.Text(v.Label); _budget.Array(v.Children, x => ScanDetail(x, depth + 1)); break;
                case ExpressionDetail v: _budget.Text(v.Operation); _budget.Array(v.Arguments, x => ScanDetail(x, depth + 1)); break;
                case LiteralDetail v: ScanLiteral(v.Value); break;
                case ReferenceDetail v: ScanReference(v.Reference); break;
                case UnsupportedDetail v: _budget.Text(v.Construct); _budget.Text(v.Description); ScanLocator(v.NativeLocator); break;
                default: throw Invalid();
            }
        }
        private void ScanOwner(NativeDiagnosticOwnerV1 value)
        {
            _budget.Input();
            switch (value)
            {
                case GraphNodeNativeOwnerV1 v: _budget.Optional(v.FunctionId); _budget.Text(v.NodeId); break;
                case UserFunctionNativeOwnerV1 v: _budget.Text(v.FunctionId); break;
                case EndpointNativeOwnerV1 v: ScanEndpoint(v.Endpoint); break;
                case SchemaNodeNativeOwnerV1 v:
                    ScanEndpoint(v.Endpoint);
                    _budget.Array(v.Path, s =>
                    {
                        _budget.Input();
                        switch (s)
                        {
                            case ChildSchemaNativeOwnerStepV1 x: _budget.Text(x.Index); break;
                            case DynamicFieldSchemaNativeOwnerStepV1: break;
                            case ContainsSchemaNativeOwnerStepV1 x: _budget.Text(x.Index); break;
                            case DependentSchemaNativeOwnerStepV1 x: _budget.Text(x.Index); break;
                            default: throw Invalid();
                        }
                    }); break;
                case ScopeNativeOwnerV1 v:
                    ScanEndpoint(v.Target);
                    _budget.Array(v.Path, s =>
                    {
                        _budget.Input();
                        switch (s)
                        {
                            case ChildScopeNativeOwnerStepV1 x: _budget.Text(x.Index); break;
                            case DynamicChildScopeNativeOwnerStepV1 x: _budget.Text(x.Index); break;
                            case SegmentScopeNativeOwnerStepV1 x: _budget.Text(x.Index); break;
                            default: throw Invalid();
                        }
                    }); break;
                case FailureRuleNativeOwnerV1 v: _budget.Text(v.Index); break;
                default: throw Invalid();
            }
        }
        private void ScanEndpoint(NativeEndpointV1 value)
        {
            _budget.Input();
            switch (value)
            {
                case SourceNativeEndpointV1: case TargetNativeEndpointV1: break;
                case NamedSourceNativeEndpointV1 v: _budget.Text(v.Index); _budget.Text(v.Name); break;
                case NamedTargetNativeEndpointV1 v: _budget.Text(v.Index); _budget.Text(v.Name); break;
                default: throw Invalid();
            }
        }
    }
}
