using System.Globalization;
using Weft.Contracts.Comparisons;
using Weft.Contracts.Projections;

namespace Weft.Application.Comparisons;

public sealed partial class RevisionComparisonService
{
    private sealed partial class ComparisonContext
    {
        private void MatchAll()
        {
            Match(ComparisonEntityV1.Boundary,
                Entities(_left.Document.Boundaries, v => v.Id, ComparisonEntityV1.Boundary),
                Entities(_right.Document.Boundaries, v => v.Id, ComparisonEntityV1.Boundary),
                (_, e) => e.Value is ProjectionBoundary b ? _budget.Key(Number((int)b.Role), b.Name) : throw Invalid());
            Match(ComparisonEntityV1.Field,
                Entities(_left.Document.Fields, v => v.Id, ComparisonEntityV1.Field),
                Entities(_right.Document.Fields, v => v.Id, ComparisonEntityV1.Field), FieldAnchor);
            Match(ComparisonEntityV1.Parameter,
                Entities(_left.Document.Parameters, v => v.Id, ComparisonEntityV1.Parameter),
                Entities(_right.Document.Parameters, v => v.Id, ComparisonEntityV1.Parameter),
                (_, e) => e.Value is ProjectionParameter v ? _budget.Key(v.Name) : throw Invalid());
            Match(ComparisonEntityV1.Rule,
                Entities(_left.Document.Rules, v => v.Id, ComparisonEntityV1.Rule),
                Entities(_right.Document.Rules, v => v.Id, ComparisonEntityV1.Rule),
                (s, e) => e.Value is ProjectionRule r ? Token(s, ComparisonEntityV1.Field, r.OutputFieldId) : throw Invalid());
            Match(ComparisonEntityV1.Control,
                Entities(_left.Document.Controls, v => v.Id, ComparisonEntityV1.Control),
                Entities(_right.Document.Controls, v => v.Id, ComparisonEntityV1.Control), ControlAnchor);
            Match(ComparisonEntityV1.Relationship,
                Entities(_left.Document.Relationships, v => v.Id, ComparisonEntityV1.Relationship),
                Entities(_right.Document.Relationships, v => v.Id, ComparisonEntityV1.Relationship), RelationshipAnchor);
            if (_left.Side.Validation is not null || _right.Side.Validation is not null)
                _groups.Add(new(ComparisonEntityV1.Validation, "", [], [], false));
            _budget.Sort(_groups, (l, r) => l.Kind != r.Kind ? l.Kind.CompareTo(r.Kind) : _budget.Compare(l.Key, r.Key));
        }
        private List<Entity> Entities<T>(IReadOnlyList<T> values, Func<T, string> id, ComparisonEntityV1 kind) where T : class
        {
            var result = new List<Entity>(values.Count);
            foreach (var value in values) { _budget.Work(1); result.Add(new(kind, id(value), value)); }
            return result;
        }
        private void Match(ComparisonEntityV1 kind, List<Entity> left, List<Entity> right,
            Func<SideData, Entity, string?> anchor)
        {
            var known = new List<(string Key, bool Left, Entity Item)>();
            var unknownLeft = new List<Entity>();
            var unknownRight = new List<Entity>();
            void Add(SideData side, List<Entity> items, bool isLeft)
            {
                foreach (var item in items)
                {
                    _budget.Work(1);
                    var key = anchor(side, item);
                    if (key is null) (isLeft ? unknownLeft : unknownRight).Add(item);
                    else known.Add((key, isLeft, item));
                }
            }
            Add(_left, left, true); Add(_right, right, false);
            _budget.Sort(known, (l, r) => _budget.Compare(l.Key, r.Key));
            for (var index = 0; index < known.Count;)
            {
                _budget.Work(1);
                var key = known[index].Key;
                var l = new List<Entity>(); var r = new List<Entity>();
                do
                {
                    var value = known[index++];
                    (value.Left ? l : r).Add(value.Item);
                } while (index < known.Count && _budget.Compare(known[index].Key, key) == 0);
                _groups.Add(new(kind, _budget.Key("0", key), l, r, false));
                if (l.Count == 1 && r.Count == 1 && kind != ComparisonEntityV1.Relationship)
                {
                    var common = _budget.Key(Number((int)kind), key);
                    _budget.Work(l[0].Id.Length + r[0].Id.Length);
                    _left.Tokens.Add((kind, l[0].Id), common);
                    _right.Tokens.Add((kind, r[0].Id), common);
                }
            }
            // Missing/ambiguous parents cannot manufacture definite child absence.
            if (unknownLeft.Count != 0 || unknownRight.Count != 0)
                _groups.Add(new(kind, _budget.Key("1"), unknownLeft, unknownRight, true));
        }
        private string? FieldAnchor(SideData side, Entity entity)
        {
            var field = (ProjectionField)entity.Value;
            var parent = Token(side, ComparisonEntityV1.Boundary, field.BoundaryId);
            if (parent is null) return null;
            var parts = new List<string> { parent };
            foreach (var segment in field.Path)
            {
                _budget.Work(1);
                parts.Add(segment switch
                {
                    FieldPathSegment v => _budget.Key("field", v.Name, NullableKey(v.NamespaceUri)),
                    AttributePathSegment v => _budget.Key("attribute", v.Name, NullableKey(v.NamespaceUri)),
                    IndexPathSegment v => _budget.Key("index", v.Index),
                    AnyItemPathSegment => _budget.Key("anyItem"),
                    _ => throw Invalid()
                });
            }
            return _budget.Key(parts);
        }
        private string? ControlAnchor(SideData side, Entity entity)
        {
            var control = (ProjectionControl)entity.Value;
            var targets = new List<string>(control.AppliesTo.Count);
            foreach (var reference in control.AppliesTo)
            {
                _budget.Work(1);
                if (reference is ControlNodeReference) return null;
                var target = ReferenceToken(side, reference);
                if (target is null) return null;
                targets.Add(target);
            }
            _budget.Sort(targets, _budget.Compare);
            return _budget.Key(targets);
        }
        private string? RelationshipAnchor(SideData side, Entity entity)
        {
            var relation = (ProjectionRelationship)entity.Value;
            var from = ReferenceToken(side, relation.From); var to = ReferenceToken(side, relation.To);
            return from is null || to is null ? null : _budget.Key(Number((int)relation.Kind), from, to);
        }
        private string? Token(SideData side, ComparisonEntityV1 kind, string id)
        {
            _budget.Work(id.Length + 1);
            return side.Tokens.TryGetValue((kind, id), out var result) ? result : null;
        }
        private string? ReferenceToken(SideData side, ProjectionNodeReference reference) => reference switch
        {
            BoundaryNodeReference v => Token(side, ComparisonEntityV1.Boundary, v.BoundaryId),
            FieldNodeReference v => Token(side, ComparisonEntityV1.Field, v.FieldId),
            ParameterNodeReference v => Token(side, ComparisonEntityV1.Parameter, v.ParameterId),
            RuleNodeReference v => Token(side, ComparisonEntityV1.Rule, v.RuleId),
            ControlNodeReference v => Token(side, ComparisonEntityV1.Control, v.ControlId),
            _ => throw Invalid()
        };
        private string NullableKey(string? value) => value is null ? _budget.Key("null") : _budget.Key("text", value);
        private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
