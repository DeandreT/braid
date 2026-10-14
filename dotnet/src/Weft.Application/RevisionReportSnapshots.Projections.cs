using Weft.Contracts.Projections;

namespace Weft.Application;

internal static partial class RevisionReportSnapshots
{
    private sealed partial class SnapshotCopy
    {
        internal ProjectionDocumentV1 Document(ProjectionDocumentV1 value)
        {
            Required(value);
            var provenance = Required(value.Provenance);
            var coverage = Required(value.Coverage);
            return new(new(Scope(provenance.Revision), Digest(provenance.NativeArtifactDigest), Backend(provenance.Backend),
                    Text(provenance.AdapterVersion), Text(provenance.ProjectionVersion)),
                new(Known(coverage.Status), List(coverage.Gaps, Gap, _caps.DiagnosticLimit)),
                List(value.Boundaries, Boundary), List(value.Fields, Field), List(value.Parameters, Parameter),
                List(value.Rules, Rule), List(value.Controls, Control), List(value.Relationships, Relationship),
                List(value.Diagnostics, Diagnostic, _caps.DiagnosticLimit), List(value.Capabilities, Capability));
        }

        private CoverageGap Gap(CoverageGap value)
        {
            Required(value);
            return new(Text(value.Code), Text(value.Message), Locator(value.NativeLocator),
                value.ProjectionLocator is { } reference ? Reference(reference) : null);
        }

        private ProjectionBoundary Boundary(ProjectionBoundary value)
        {
            Required(value);
            return new(Text(value.Id), Text(value.Name), Known(value.Role), Text(value.Format), Known(value.Shape),
                OptionalText(value.RootFieldId), Locator(value.NativeLocator));
        }

        private ProjectionField Field(ProjectionField value)
        {
            Required(value);
            var cardinality = Required(value.Cardinality);
            return new(Text(value.Id), Text(value.BoundaryId), List(value.Path, Path), Text(value.Name), DataType(value.DataType),
                new(Known(cardinality.Kind), OptionalText(cardinality.Minimum), OptionalText(cardinality.Maximum)),
                Known(value.Requirement), Known(value.Nullability), Locator(value.NativeLocator));
        }

        private PathSegment Path(PathSegment value) => Required(value) switch
        {
            FieldPathSegment item => new FieldPathSegment(Text(item.Name), OptionalText(item.NamespaceUri)),
            AttributePathSegment item => new AttributePathSegment(Text(item.Name), OptionalText(item.NamespaceUri)),
            IndexPathSegment item => new IndexPathSegment(Text(item.Index)),
            AnyItemPathSegment => new AnyItemPathSegment(),
            _ => throw Integrity()
        };

        private FieldDataType DataType(FieldDataType value)
        {
            Required(value);
            return new(Known(value.Kind), List(value.ScalarTypes, Known), OptionalText(value.NativeName));
        }

        private ProjectionParameter Parameter(ProjectionParameter value)
        {
            Required(value);
            return new(Text(value.Id), Text(value.Name), DataType(value.DataType), Known(value.Requirement),
                value.DefaultValue is { } literal ? Literal(literal) : null, Locator(value.NativeLocator));
        }

        private ProjectionRule Rule(ProjectionRule value)
        {
            Required(value);
            return new(Text(value.Id), Text(value.OutputFieldId), Text(value.Summary), List(value.Dependencies, Dependency),
                List(value.Details, item => Detail(item, 0)), Locator(value.NativeLocator));
        }

        private RuleDependency Dependency(RuleDependency value)
        {
            Required(value);
            var role = Known(value.Role);
            return value switch
            {
                FieldDependency item => new FieldDependency(Text(item.FieldId), role),
                ParameterDependency item => new ParameterDependency(Text(item.ParameterId), role),
                ConstantDependency item => new ConstantDependency(Literal(item.Value), role),
                NodeDependency item => new NodeDependency(Reference(item.Node), role),
                _ => throw Integrity()
            };
        }

        private DetailNode Detail(DetailNode value, int depth)
        {
            Required(value);
            if (depth > _caps.DetailDepthLimit)
                throw Limit();
            return value switch
            {
                GroupDetail item => new GroupDetail(Text(item.Label), List(item.Children, child => Detail(child, depth + 1))),
                ExpressionDetail item => new ExpressionDetail(Text(item.Operation), List(item.Arguments, child => Detail(child, depth + 1))),
                LiteralDetail item => new LiteralDetail(Literal(item.Value)),
                ReferenceDetail item => new ReferenceDetail(Reference(item.Reference)),
                UnsupportedDetail item => new UnsupportedDetail(Text(item.Construct), Text(item.Description), Locator(item.NativeLocator)),
                _ => throw Integrity()
            };
        }

        private ProjectionControl Control(ProjectionControl value)
        {
            Required(value);
            return new(Text(value.Id), Known(value.Kind), Text(value.Summary), List(value.AppliesTo, Reference),
                List(value.Dependencies, Dependency), List(value.Details, item => Detail(item, 0)), Locator(value.NativeLocator));
        }

        private ProjectionRelationship Relationship(ProjectionRelationship value)
        {
            Required(value);
            return new(Text(value.Id), Known(value.Kind), Reference(value.From), Reference(value.To));
        }

        private ProjectionDiagnostic Diagnostic(ProjectionDiagnostic value)
        {
            Required(value);
            return new(Text(value.Code), Known(value.Severity), Text(value.Message),
                value.NativeLocator is { } locator ? Locator(locator) : null,
                value.ProjectionLocator is { } reference ? Reference(reference) : null);
        }

        private OperationCapability Capability(OperationCapability value)
        {
            Required(value);
            return new(Known(value.Operation), Known(value.Status), Text(value.Profile), List(value.BoundaryIds, Text),
                List(value.Conditions, Condition));
        }

        private CapabilityCondition Condition(CapabilityCondition value)
        {
            Required(value);
            return new(Text(value.Code), Text(value.Description));
        }
    }
}
