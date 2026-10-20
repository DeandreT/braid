using Weft.Contracts.Comparisons;
using Weft.Contracts.Projections;

namespace Weft.Application.Comparisons;

public sealed partial class RevisionComparisonService
{
    private sealed partial class ComparisonContext
    {
        private sealed record Fact(string Tag, string? Value, IReadOnlyList<Fact> Children,
            bool UnknownValue = false, bool UnknownWhole = false, ComparisonReasonV1? Reason = null)
        {
            public override string ToString() => "Comparison fact.";
        }
        private sealed class Observation
        {
            internal bool Different;
            internal bool Uncertain;
            internal HashSet<ComparisonReasonV1> Reasons { get; } = [];
        }
        private Fact Value(string tag, string value)
        { _budget.Work(tag.Length + value.Length + 1); return new(tag, value, []); }
        private Fact Node(string tag, params Fact[] children)
        { _budget.Work(tag.Length + children.Length + 1); return new(tag, null, children); }
        private Fact Unknown(string tag, ComparisonReasonV1 reason, bool whole = false, params Fact[] children)
        { _budget.Work(tag.Length + children.Length + 1); return new(tag, null, children, true, whole, reason); }
        private Fact Optional(string tag, string? value) => value is null ? Node(tag + ".none") : Value(tag + ".text", value);
        private Fact EnumFact<T>(string tag, T value, bool unknown = false) where T : struct, Enum => unknown
            ? Unknown(tag, ComparisonReasonV1.UnknownFact, true) : Value(tag, Convert.ToInt32(value).ToString(System.Globalization.CultureInfo.InvariantCulture));
        private Fact Bool(string tag, bool value) => Value(tag, value ? "true" : "false");

        private ComparisonItemV1 Item(Group group)
        {
            if (group.Kind == ComparisonEntityV1.Validation) return ValidationItem();
            var left = Candidates(group.Left);
            var right = Candidates(group.Right);
            var clipped = group.Left.Count > left.Count || group.Right.Count > right.Count;
            var ambiguous = group.Unresolved || group.Left.Count > 1 || group.Right.Count > 1;
            List<ComparisonFacetV1> facets;
            ComparisonMatchV1 match;
            if (ambiguous)
            {
                match = ComparisonMatchV1.Ambiguous;
                var reasons = new List<ComparisonReasonV1> { ComparisonReasonV1.AmbiguousMatch };
                if (group.Unresolved) reasons.Add(ComparisonReasonV1.UnresolvedReference);
                facets = [Facet(Primary(group.Kind), false, true, reasons)];
            }
            else if (group.Left.Count == 1 && group.Right.Count == 1)
            {
                match = ComparisonMatchV1.Matched;
                facets = [];
                var l = Facts(_left, group.Left[0]); var r = Facts(_right, group.Right[0]);
                if (l.Count != r.Count) throw Invalid();
                for (var i = 0; i < l.Count; i++)
                {
                    _budget.Work(1);
                    if (l[i].Aspect != r[i].Aspect) throw Invalid();
                    var observation = new Observation();
                    CompareFact(l[i].Fact, r[i].Fact, observation);
                    facets.Add(Facet(l[i].Aspect, observation.Different, observation.Uncertain, observation.Reasons));
                }
            }
            else
            {
                var onlyLeft = group.Left.Count == 1;
                match = onlyLeft ? ComparisonMatchV1.OnlyLeft : ComparisonMatchV1.OnlyRight;
                var oppositeComplete = (onlyLeft ? _right : _left).Document.Coverage.Status == CoverageStatus.Complete;
                var observation = new Observation();
                foreach (var fact in Facts(onlyLeft ? _left : _right, onlyLeft ? group.Left[0] : group.Right[0]))
                    CollectUnknown(fact.Fact, observation);
                if (!oppositeComplete) { observation.Uncertain = true; observation.Reasons.Add(ComparisonReasonV1.IncompleteCoverage); }
                facets = [Facet(Primary(group.Kind), oppositeComplete, observation.Uncertain, observation.Reasons)];
            }
            if (clipped)
            {
                for (var i = 0; i < facets.Count; i++)
                    facets[i] = Facet(facets[i].Aspect, facets[i].HasObservedDifference, true,
                        facets[i].Reasons.Append(ComparisonReasonV1.CandidatesOmitted));
            }
            return new(group.Kind, match, group.Left.Count, group.Right.Count, clipped,
                left, right, Array.AsReadOnly(facets.ToArray()));
        }
        private static ComparisonAspectV1 Primary(ComparisonEntityV1 kind) => kind switch
        {
            ComparisonEntityV1.Rule => ComparisonAspectV1.Details,
            ComparisonEntityV1.Control => ComparisonAspectV1.Control,
            ComparisonEntityV1.Relationship => ComparisonAspectV1.Relationship,
            _ => ComparisonAspectV1.Shape
        };
        private IReadOnlyList<ComparisonCandidateV1> Candidates(List<Entity> values)
        {
            var sorted = new List<Entity>(values.Count);
            foreach (var entity in values)
            { _budget.Work(1); sorted.Add(entity); }
            // Each group has one entity kind; raw report-local IDs order display, never identity.
            _budget.Sort(sorted, (l, r) => _budget.Compare(l.Id, r.Id));
            var count = Math.Min(sorted.Count, _budget.Limits.CandidatesPerSide);
            var result = new ComparisonCandidateV1[count];
            for (var i = 0; i < count; i++)
            {
                _budget.Work(1);
                var item = sorted[i];
                result[i] = item.Kind == ComparisonEntityV1.Relationship ? new(null, item.Id) : new(item.Kind switch
                {
                    ComparisonEntityV1.Boundary => new BoundaryNodeReference(item.Id),
                    ComparisonEntityV1.Field => new FieldNodeReference(item.Id),
                    ComparisonEntityV1.Parameter => new ParameterNodeReference(item.Id),
                    ComparisonEntityV1.Rule => new RuleNodeReference(item.Id),
                    ComparisonEntityV1.Control => new ControlNodeReference(item.Id),
                    _ => throw Invalid()
                }, null);
            }
            return Array.AsReadOnly(result);
        }
        private ComparisonFacetV1 Facet(ComparisonAspectV1 aspect, bool difference, bool unknown,
            IEnumerable<ComparisonReasonV1> reasons)
        {
            var values = new List<ComparisonReasonV1>();
            foreach (var reason in reasons) { _budget.Work(1); if (!values.Contains(reason)) values.Add(reason); }
            _budget.Sort(values, (l, r) => l.CompareTo(r));
            return new(aspect, difference, unknown, Array.AsReadOnly(values.ToArray()));
        }
        private void CompareFact(Fact left, Fact right, Observation result)
        {
            _budget.Work(1);
            OwnUnknown(left, result); OwnUnknown(right, result);
            if (left.UnknownWhole || right.UnknownWhole)
            { CollectUnknown(left, result); CollectUnknown(right, result); return; }
            if (_budget.Compare(left.Tag, right.Tag) != 0)
            {
                result.Different = true;
                CollectUnknown(left, result); CollectUnknown(right, result); return;
            }
            if (!left.UnknownValue && !right.UnknownValue)
            {
                if (left.Value is null || right.Value is null) result.Different |= (left.Value is null) != (right.Value is null);
                else result.Different |= _budget.Compare(left.Value, right.Value) != 0;
            }
            result.Different |= left.Children.Count != right.Children.Count;
            var count = Math.Max(left.Children.Count, right.Children.Count);
            for (var i = 0; i < count; i++)
            {
                _budget.Work(1);
                if (i >= left.Children.Count) CollectUnknown(right.Children[i], result);
                else if (i >= right.Children.Count) CollectUnknown(left.Children[i], result);
                else CompareFact(left.Children[i], right.Children[i], result);
            }
        }
        private static void OwnUnknown(Fact fact, Observation result)
        {
            if (fact.UnknownValue || fact.UnknownWhole)
            {
                result.Uncertain = true;
                if (fact.Reason is { } reason) result.Reasons.Add(reason);
            }
        }
        private void CollectUnknown(Fact fact, Observation result)
        {
            _budget.Work(1); OwnUnknown(fact, result);
            foreach (var child in fact.Children) CollectUnknown(child, result);
        }
        private List<(ComparisonAspectV1 Aspect, Fact Fact)> Facts(SideData side, Entity entity)
        {
            _budget.Work(1);
            switch (entity.Value)
            {
                case ProjectionBoundary v:
                    return [(ComparisonAspectV1.Shape, Node("boundary", EnumFact("role", v.Role),
                        v.Format is "" or "unknown" ? Unknown("format", ComparisonReasonV1.UnknownFact, true) : Value("format", v.Format),
                        EnumFact("shape", v.Shape, v.Shape == BoundaryShape.Unknown),
                        v.RootFieldId is { } root ? Reference(side, new FieldNodeReference(root)) : Node("root.none"))),
                        (ComparisonAspectV1.Presentation, Value("name", v.Name))];
                case ProjectionField v:
                    return [(ComparisonAspectV1.Shape, Reference(side, new BoundaryNodeReference(v.BoundaryId))),
                        (ComparisonAspectV1.DataType, Type(v.DataType)),
                        (ComparisonAspectV1.Cardinality, v.Cardinality.Kind == CardinalityKind.Unknown
                            ? Unknown("cardinality", ComparisonReasonV1.UnknownFact, true)
                            : Node("cardinality", EnumFact("kind", v.Cardinality.Kind), Optional("min", v.Cardinality.Minimum), Optional("max", v.Cardinality.Maximum))),
                        (ComparisonAspectV1.Requirement, EnumFact("requirement", v.Requirement, v.Requirement == FieldRequirement.Unknown)),
                        (ComparisonAspectV1.Nullability, EnumFact("nullability", v.Nullability, v.Nullability == FieldNullability.Unknown)),
                        (ComparisonAspectV1.Presentation, Value("name", v.Name))];
                case ProjectionParameter v:
                    return [(ComparisonAspectV1.DataType, Type(v.DataType)),
                        (ComparisonAspectV1.Requirement, EnumFact("requirement", v.Requirement, v.Requirement == FieldRequirement.Unknown)),
                        (ComparisonAspectV1.Default, v.DefaultValue is { } value ? Literal(value) : Node("default.none")),
                        (ComparisonAspectV1.Presentation, Value("name", v.Name))];
                case ProjectionRule v:
                    return [(ComparisonAspectV1.Shape, Node("ruleShape", Reference(side, new FieldNodeReference(v.OutputFieldId)), DependencyOrder(v.Dependencies))),
                        (ComparisonAspectV1.Value, Dependencies(side, v.Dependencies, DependencyRole.Value)),
                        (ComparisonAspectV1.Condition, Dependencies(side, v.Dependencies, DependencyRole.Condition)),
                        (ComparisonAspectV1.Default, Dependencies(side, v.Dependencies, DependencyRole.Default)),
                        (ComparisonAspectV1.Details, Details(side, v.Details)),
                        (ComparisonAspectV1.Presentation, Presentation(v.Summary, v.Details))];
                case ProjectionControl v:
                    var targets = new List<Fact>();
                    foreach (var target in v.AppliesTo) { _budget.Work(1); targets.Add(Reference(side, target)); }
                    return [(ComparisonAspectV1.Control, Node("control", EnumFact("kind", v.Kind, v.Kind == ControlKind.Unknown), Node("targets", targets.ToArray()), DependencyOrder(v.Dependencies))),
                        (ComparisonAspectV1.Value, Dependencies(side, v.Dependencies, DependencyRole.Value)),
                        (ComparisonAspectV1.Condition, Dependencies(side, v.Dependencies, DependencyRole.Condition)),
                        (ComparisonAspectV1.Default, Dependencies(side, v.Dependencies, DependencyRole.Default)),
                        (ComparisonAspectV1.Details, Details(side, v.Details)),
                        (ComparisonAspectV1.Presentation, Presentation(v.Summary, v.Details))];
                case ProjectionRelationship v:
                    return [(ComparisonAspectV1.Relationship, Node("relationship", EnumFact("kind", v.Kind), Reference(side, v.From), Reference(side, v.To)))];
                default: throw Invalid();
            }
        }
        private Fact Type(FieldDataType value)
        {
            var unknown = value.Kind == FieldDataTypeKind.Unknown;
            var scalars = new List<Fact>();
            foreach (var item in value.ScalarTypes) { _budget.Work(1); scalars.Add(EnumFact("scalar", item)); }
            // Unknown neutral type does not erase an independently supplied literal native-name hint.
            return Node("type", EnumFact("kind", value.Kind, unknown),
                unknown ? Unknown("scalars", ComparisonReasonV1.UnknownFact, true) : Node("scalars", scalars.ToArray()),
                Optional("nativeName", value.NativeName));
        }
        private Fact Reference(SideData side, ProjectionNodeReference value)
        {
            var tag = value switch
            {
                BoundaryNodeReference => "boundary",
                FieldNodeReference => "field",
                ParameterNodeReference => "parameter",
                RuleNodeReference => "rule",
                ControlNodeReference => "control",
                _ => throw Invalid()
            };
            return ReferenceToken(side, value) is { } token ? Value("reference." + tag, token)
                : Unknown("reference." + tag, ComparisonReasonV1.UnresolvedReference);
        }
        private Fact Literal(LiteralValue value) => value switch
        {
            StringLiteralValue v => Value("string", v.Value),
            BooleanLiteralValue v => Bool("boolean", v.Value),
            ExactNumberLiteralValue v => Node("exactNumber", EnumFact("flavor", v.Flavor), Value("lexical", v.Value)),
            NullLiteralValue v => EnumFact("nullKind", v.NullKind),
            _ => throw Invalid()
        };
        private Fact DependencyOrder(IReadOnlyList<RuleDependency> dependencies)
        {
            var roles = new List<Fact>(dependencies.Count);
            foreach (var item in dependencies) { _budget.Work(1); roles.Add(EnumFact("role", item.Role)); }
            return Node("dependencyOrder", roles.ToArray());
        }
        private Fact Dependencies(SideData side, IReadOnlyList<RuleDependency> dependencies, DependencyRole role)
        {
            var values = new List<Fact>();
            foreach (var dependency in dependencies)
            {
                _budget.Work(1);
                if (dependency.Role != role) continue;
                values.Add(dependency switch
                {
                    FieldDependency v => Node("fieldDependency", Reference(side, new FieldNodeReference(v.FieldId))),
                    ParameterDependency v => Node("parameterDependency", Reference(side, new ParameterNodeReference(v.ParameterId))),
                    ConstantDependency v => Node("constantDependency", Literal(v.Value)),
                    NodeDependency v => Node("nodeDependency", Reference(side, v.Node)),
                    _ => throw Invalid()
                });
            }
            return Node("dependencies", values.ToArray());
        }
        private Fact Details(SideData side, IReadOnlyList<DetailNode> details)
        {
            var values = new List<Fact>();
            foreach (var detail in details) { _budget.Work(1); values.Add(Detail(side, detail, 0)); }
            return Node("details", values.ToArray());
        }
        private Fact Detail(SideData side, DetailNode value, int depth)
        {
            _budget.Work(1); if (depth > 16) throw Limit();
            Fact[] Children(IReadOnlyList<DetailNode> values)
            {
                var children = new Fact[values.Count];
                for (var i = 0; i < children.Length; i++) children[i] = Detail(side, values[i], depth + 1);
                return children;
            }
            return value switch
            {
                GroupDetail v => Node("group", Children(v.Children)),
                ExpressionDetail v => _detailsCompatible ? Node("expression", Value("operation", v.Operation), Node("arguments", Children(v.Arguments)))
                    : Node("expression", Unknown("operation", ComparisonReasonV1.IncompatibleProfile), Node("arguments", Children(v.Arguments))),
                LiteralDetail v => Node("literal", Literal(v.Value)),
                ReferenceDetail v => Node("reference", Reference(side, v.Reference)),
                UnsupportedDetail => Unknown("unsupported", ComparisonReasonV1.UnsupportedDetail, true),
                _ => throw Invalid()
            };
        }
        private Fact Presentation(string summary, IReadOnlyList<DetailNode> details)
        {
            var labels = new List<Fact> { Value("summary", summary) };
            void Visit(DetailNode node)
            {
                _budget.Work(1);
                switch (node)
                {
                    case GroupDetail g:
                        labels.Add(Value("groupLabel", g.Label)); foreach (var c in g.Children) Visit(c); break;
                    case ExpressionDetail e: foreach (var c in e.Arguments) Visit(c); break;
                    case UnsupportedDetail u: labels.Add(Value("construct", u.Construct)); labels.Add(Value("description", u.Description)); break;
                }
            }
            foreach (var detail in details) Visit(detail);
            return Node("presentation", labels.ToArray());
        }
        private ComparisonItemV1 ValidationItem()
        {
            var l = ValidationSummary(_left.Side); var r = ValidationSummary(_right.Side);
            List<ComparisonFacetV1> facets;
            if (l is null || r is null)
            {
                facets = [Facet(ComparisonAspectV1.ValidationValidity, false, true, [ComparisonReasonV1.MissingValidation]),
                    Facet(ComparisonAspectV1.ValidationCounts, false, true, [ComparisonReasonV1.MissingValidation])];
            }
            else
            {
                // Neutral verification permits omitted issues without the producer's truncation flag.
                var truncated = l.Truncated || r.Truncated || l.HasTruncatedDiagnosticText || r.HasTruncatedDiagnosticText ||
                    l.TotalIssueCount > l.EmittedIssueCount || r.TotalIssueCount > r.EmittedIssueCount;
                facets = [Facet(ComparisonAspectV1.ValidationValidity, l.IsValid != r.IsValid, false, []),
                    Facet(ComparisonAspectV1.ValidationCounts, l.TotalIssueCount != r.TotalIssueCount ||
                        l.EmittedIssueCount != r.EmittedIssueCount || l.Truncated != r.Truncated ||
                        l.HasTruncatedDiagnosticText != r.HasTruncatedDiagnosticText, truncated,
                        truncated ? [ComparisonReasonV1.TruncatedValidation] : [])];
            }
            return new(ComparisonEntityV1.Validation, l is null ? ComparisonMatchV1.OnlyRight : r is null ? ComparisonMatchV1.OnlyLeft : ComparisonMatchV1.Matched,
                0, 0, false, Array.AsReadOnly(Array.Empty<ComparisonCandidateV1>()),
                Array.AsReadOnly(Array.Empty<ComparisonCandidateV1>()), Array.AsReadOnly(facets.ToArray()));
        }
    }
}
