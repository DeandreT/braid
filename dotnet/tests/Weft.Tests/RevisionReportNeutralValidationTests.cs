using System.Collections;
using Weft.Application;
using Weft.Contracts.Examples;
using Weft.Contracts.Operations;
using Weft.Contracts.Projections;
using Weft.Contracts.Validation;
using Weft.Domain;

namespace Weft.Tests;

/// <summary>Self-authored provider-neutral reports; these tests claim no native engine work.</summary>
public sealed partial class RevisionReportNeutralValidationTests
{
    private static readonly RevisionScope Scope = new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Guid.Parse("33333333-3333-3333-3333-333333333333"), Guid.Parse("44444444-4444-4444-4444-444444444444"));
    private static readonly RevisionReportBackendProfile Profile = new("neutral.provider", "engine-a", "engine-build-a", "adapter-a");
    private static readonly Sha256Digest Digest = Sha256Digest.Parse("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    private static RevisionReportSource Source => new(Scope,
        new(PrivateObjectId.Restore(Scope, Guid.Parse("55555555-5555-5555-5555-555555555555"), PrivateObjectClass.NativeArtifact), Digest, 3),
        "source.bin", "dependency-set-a", Profile.BackendId);
    private static ProjectionCacheKey ProjectionKey(RevisionReportPolicy? policy = null) =>
        new(Source, Profile, "weft.mapping-projection.v1", "neutral.project.v1", "neutral.codec.v1", policy ?? new());
    private static ValidationCacheKey ValidationKey(RevisionReportPolicy? policy = null) =>
        new(Source, Profile, "weft.mapping-validation.v1", "neutral.validate.v1", "neutral.codec.v1", policy ?? new());
    private static ScopedRevisionId ContractScope => new(Scope.OrganizationId.ToString("D"), Scope.ProjectId.ToString("D"),
        Scope.MappingId.ToString("D"), Scope.RevisionId.ToString("D"));
    private static ArtifactDigest ContractDigest => new("sha256", Digest.Value);
    private static BackendIdentity ContractBackend => new(Profile.BackendId, Profile.BackendRevision, Profile.BackendBuild);
    private static OperationProvenanceV1 Provenance => new(ContractScope, ContractDigest, ContractBackend, Profile.AdapterBuild, true);
    private static NativeLocator Origin(string id) => new GraphNodeNativeLocator("provider.document", id, "function/alpha");
    private static IReadOnlyList<T> R<T>(params T[] values) => Array.AsReadOnly(values);
    private static ProjectionNodeReference In => new FieldNodeReference("in.field");
    private static ProjectionNodeReference Out => new FieldNodeReference("out.field");
    private static ProjectionNodeReference Rule => new RuleNodeReference("rule");
    private static ProjectionNodeReference Control => new ControlNodeReference("control");
    private static ProjectionNodeReference Parameter => new ParameterNodeReference("parameter");

    private static ProjectionReportV1 Graph()
    {
        var textType = new FieldDataType(FieldDataTypeKind.Scalar, R(ScalarDataType.String));
        var boundaries = R(
            new ProjectionBoundary("out", "Output", BoundaryRole.NamedOutput, "opaque", BoundaryShape.Object, null, Origin("output-graph")),
            new ProjectionBoundary("in", "Input", BoundaryRole.NamedInput, "opaque", BoundaryShape.Rows, null, Origin("input-graph")));
        var fields = R(
            new ProjectionField("in.field", "in", R<PathSegment>(new FieldPathSegment("customer.name", "urn:source"),
                new AttributePathSegment("id", "urn:attribute"), new IndexPathSegment("9007199254740993"), new AnyItemPathSegment()),
                "Incoming", textType, new(CardinalityKind.Unknown), FieldRequirement.Unknown, FieldNullability.Nullable, Origin("opaque-node-A")),
            new ProjectionField("out.field", "out", R<PathSegment>(new FieldPathSegment("result")), "Outgoing", textType,
                new(CardinalityKind.Bounded, "9007199254740993", "9007199254740994"), FieldRequirement.Required,
                FieldNullability.NonNullable, Origin("opaque-node-B")));
        var parameters = R(new ProjectionParameter("parameter", "Default", textType, FieldRequirement.Optional,
            new StringLiteralValue("fallback"), Origin("parameter-node")));
        var rules = R(new ProjectionRule("rule", "out.field", "Choose a value",
            R<RuleDependency>(new FieldDependency("in.field"), new ParameterDependency("parameter"),
                new ConstantDependency(new ExactNumberLiteralValue(ExactNumberFlavor.Decimal, "-0.000E+07"))),
            R<DetailNode>(new GroupDetail("Explanation", R<DetailNode>(new LiteralDetail(new BooleanLiteralValue(true)),
                new ReferenceDetail(Parameter), new UnsupportedDetail("extension", "Provider extension", Origin("nested-extension"))))), Origin("rule-node")));
        var controls = R(new ProjectionControl("control", ControlKind.Filter, "Guard the rule", R(Rule),
            R<RuleDependency>(new NodeDependency(Rule, DependencyRole.Condition)),
            R<DetailNode>(new ExpressionDetail("guard", R<DetailNode>(new LiteralDetail(new BooleanLiteralValue(true))))), Origin("control-node")));
        var relationships = R(
            new ProjectionRelationship("member-in", RelationshipKind.BoundaryMember, In, new BoundaryNodeReference("in")),
            new ProjectionRelationship("member-out", RelationshipKind.BoundaryMember, Out, new BoundaryNodeReference("out")),
            new ProjectionRelationship("write", RelationshipKind.WritesTo, Rule, Out),
            new ProjectionRelationship("read", RelationshipKind.ReadsFrom, Rule, In),
            new ProjectionRelationship("default", RelationshipKind.DependsOn, Rule, Parameter),
            new ProjectionRelationship("control-dependency", RelationshipKind.DependsOn, Control, Rule),
            new ProjectionRelationship("govern", RelationshipKind.GovernedBy, Rule, Control));
        var capabilities = R(
            new OperationCapability(MappingOperation.Project, CapabilityStatus.Supported, "provider.fast", R("out"), R<CapabilityCondition>()),
            new OperationCapability(MappingOperation.Project, CapabilityStatus.Conditional, "provider.strict", R<string>(),
                R(new CapabilityCondition("selectedMode", "Requires selected mode"))));
        return new(OperationStatusV1.Succeeded, Provenance, new(
            new(ContractScope, ContractDigest, ContractBackend, Profile.AdapterBuild, "neutral.project.v1"),
            new(CoverageStatus.Complete, R<CoverageGap>()), boundaries, fields, parameters, rules, controls,
            relationships, R<ProjectionDiagnostic>(), capabilities), R<OperationDiagnosticV1>());
    }

    private static ProjectionReportV1 WithDocument(ProjectionReportV1 report, ProjectionDocumentV1 document) => report with { Document = document };
    private static RevisionReportObservation CheckProjection(ProjectionReportV1 report, RevisionReportPolicy? caps = null,
        ProjectionCacheKey? key = null, CancellationToken token = default) =>
        RevisionReportProjectionValidation.Validate(key ?? ProjectionKey(), report, caps ?? new(), token);
    private static RevisionReportObservation CheckValidation(ValidationReportV1 report, RevisionReportPolicy? caps = null,
        ValidationCacheKey? key = null, CancellationToken token = default) =>
        RevisionReportValidationValidation.Validate(key ?? ValidationKey(), report, caps ?? new(), token);
    private static void Integrity(Action action) => Assert.Equal(RevisionReportValidationFailure.Integrity,
        Assert.Throws<RevisionReportValidationException>(action).Failure);
    private static void Limit(Action action) => Assert.Equal(RevisionReportValidationFailure.ResourceLimit,
        Assert.Throws<RevisionReportValidationException>(action).Failure);

    private sealed class CancelOnFirstRead<T>(IReadOnlyList<T> values, CancellationTokenSource source) : IReadOnlyList<T>
    {
        public int Reads { get; private set; }
        public int Count => values.Count;
        public T this[int index]
        {
            get { Reads++; source.Cancel(); return values[index]; }
        }
        public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException("Traversal must be bounded by index.");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
