using System.Globalization;
using Braid.Application;
using Braid.Application.Comparisons;
using Braid.Contracts.Comparisons;
using Braid.Contracts.Operations;
using Braid.Contracts.Projections;
using Braid.Contracts.Validation;
using Braid.Domain;

namespace Braid.Tests;

/// <summary>Handwritten neutral facts and expected correspondences; no native engine or I/O.</summary>
public sealed partial class RevisionComparisonTests
{
    private static readonly RevisionReportBackendProfile Profile = new("independent.fixture", "engine.1", "build.1", "adapter.1");
    private static readonly PrincipalId Actor = new(Id(5));
    private static readonly DateTimeOffset Started = new(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);
    private static readonly Sha256Digest Digest = Sha256Digest.FromBytes([1, 2, 3]);
    private static Guid Id(int value) => Guid.Parse($"00000000-0000-0000-0000-{value:D12}");
    private static IReadOnlyList<T> R<T>(params T[] values) => Array.AsReadOnly(values);
    private static NativeLocator Origin(string id) => new GraphNodeNativeLocator("fixture.document", "native/" + id);
    private static FieldDataType TextType => new(FieldDataTypeKind.Scalar, R(ScalarDataType.String));
    private static ProjectionField Field(string id, string boundary, string name, params PathSegment[] path) =>
        new(id, boundary, R(path), name, TextType, new(CardinalityKind.ExactlyOne),
            FieldRequirement.Required, FieldNullability.NonNullable, Origin(id));
    private static ScopedRevisionId PublicScope(RevisionScope scope) => new(scope.OrganizationId.ToString("D"),
        scope.ProjectId.ToString("D"), scope.MappingId.ToString("D"), scope.RevisionId.ToString("D"));
    private static OperationProvenanceV1 OperationProvenance(RevisionReportKey key) => new(PublicScope(key.Scope),
        new("sha256", key.NativeDigest.Value), new(key.BackendId, key.BackendRevision, key.BackendBuild), key.AdapterBuild, true);
    private static ProjectionProvenance DocumentProvenance(RevisionReportKey key) => new(PublicScope(key.Scope),
        new("sha256", key.NativeDigest.Value), new(key.BackendId, key.BackendRevision, key.BackendBuild), key.AdapterBuild, key.AlgorithmVersion);

    private sealed record Fixture(int Seed, CatalogRevisionSummary Catalog, ProjectionCacheKey Key, ProjectionReportV1 Report)
    {
        internal ProjectionDocumentV1 Document => Report.Document!;
        internal Fixture WithDocument(ProjectionDocumentV1 document, bool rebuildRelationships = true) => this with
        {
            Report = Report with { Document = rebuildRelationships ? document with { Relationships = CanonicalEdges(document) } : document }
        };
        internal VerifiedProjectionReport Verified()
        {
            var observed = RevisionReportProjectionValidation.Validate(Key, Report, Key.Policy);
            return VerifiedProjectionReport.Create(Metadata(Key, observed, Seed), Report, Key.Policy);
        }
        internal RevisionComparisonSide Side(VerifiedValidationReport? validation = null, CancellationToken token = default) =>
            RevisionComparisonSide.Create(Catalog, Verified(), validation, token);
        internal VerifiedValidationReport Validation(bool valid, int total = 0,
            IReadOnlyList<ValidationDiagnosticV1>? diagnostics = null, bool truncated = false,
            Guid? linkedProjection = null, RevisionReportBackendProfile? profile = null)
        {
            var key = new ValidationCacheKey(Key.Source, profile ?? Key.Profile, "braid.mapping-validation.v1",
                "fixture.validate.1", Key.CodecVersion, Key.Policy);
            var issues = diagnostics ?? R<ValidationDiagnosticV1>();
            var report = new ValidationReportV1(valid ? OperationStatusV1.Succeeded : OperationStatusV1.InvalidMapping,
                OperationProvenance(key), valid, total, issues.Count, truncated, issues);
            var observed = RevisionReportValidationValidation.Validate(key, report, key.Policy);
            return VerifiedValidationReport.Create(Metadata(key, observed, Seed + 100), report, key.Policy, linkedProjection);
        }
    }

    private static Fixture Direct(int seed = 1, string prefix = "L", RevisionReportBackendProfile? profile = null,
        string algorithm = "fixture.project.1", string codec = "fixture.codec.1", RevisionReportPolicy? policy = null)
    {
        var scope = new RevisionScope(Id(1), Id(2), Id(3), Id(100 + seed));
        var backend = profile ?? Profile;
        var source = new RevisionReportSource(scope,
            new(PrivateObjectId.Restore(scope, Id(1000 + seed), PrivateObjectClass.NativeArtifact), Digest, 3),
            IngestionIntent.EntryDocument, IngestionIntent.EmptyDependencyIdentity, backend.BackendId);
        var key = new ProjectionCacheKey(source, backend, "braid.mapping-projection.v1", algorithm, codec, policy ?? new());
        var catalog = new CatalogRevisionSummary(scope, 9007199254740991L + seed,
            IngestionAdmissionClassification.EligibleValid, Actor, Started, "PRIVATE-fixture-source.native",
            IngestionIntent.EntryDocument, Digest, 3, backend.BackendId);
        var boundaries = R(new ProjectionBoundary(prefix + ".in", "Input", BoundaryRole.NamedInput, "record",
                BoundaryShape.Object, null, Origin(prefix + ".in")),
            new ProjectionBoundary(prefix + ".out", "Output", BoundaryRole.NamedOutput, "record",
                BoundaryShape.Object, null, Origin(prefix + ".out")));
        var fields = R(Field(prefix + ".a", prefix + ".in", "Incoming", new FieldPathSegment("customer.name")),
            Field(prefix + ".b", prefix + ".in", "Backup", new FieldPathSegment("backup")),
            Field(prefix + ".o", prefix + ".out", "Result", new FieldPathSegment("result")));
        var parameter = new ProjectionParameter(prefix + ".p", "Fallback", TextType, FieldRequirement.Optional,
            new StringLiteralValue("none"), Origin(prefix + ".p"));
        var rule = new ProjectionRule(prefix + ".rule", prefix + ".o", "Copy incoming",
            R<RuleDependency>(new FieldDependency(prefix + ".a"), new ConstantDependency(new BooleanLiteralValue(true), DependencyRole.Condition),
                new ConstantDependency(new StringLiteralValue("fallback"), DependencyRole.Default)),
            R<DetailNode>(new LiteralDetail(new StringLiteralValue("copy"))), Origin(prefix + ".rule"));
        var control = new ProjectionControl(prefix + ".control", ControlKind.Filter, "Guard result",
            R<ProjectionNodeReference>(new RuleNodeReference(rule.Id)),
            R<RuleDependency>(new ConstantDependency(new BooleanLiteralValue(true), DependencyRole.Condition)),
            R<DetailNode>(new LiteralDetail(new BooleanLiteralValue(true))), Origin(prefix + ".control"));
        var document = new ProjectionDocumentV1(DocumentProvenance(key), new(CoverageStatus.Complete, R<CoverageGap>()),
            boundaries, fields, R(parameter), R(rule), R(control), R<ProjectionRelationship>(), R<ProjectionDiagnostic>(), R<OperationCapability>());
        document = document with { Relationships = CanonicalEdges(document) };
        return new(seed, catalog, key, new(OperationStatusV1.Succeeded, OperationProvenance(key), document, R<OperationDiagnosticV1>()));
    }

    // This fixture helper emits the canonical edges demanded by the existing projection verifier.
    // It performs no cross-revision matching and contains no comparison oracle.
    private static IReadOnlyList<ProjectionRelationship> CanonicalEdges(ProjectionDocumentV1 document)
    {
        var edges = new List<ProjectionRelationship>();
        var seen = new HashSet<(RelationshipKind, ProjectionNodeReference, ProjectionNodeReference)>();
        void Add(RelationshipKind kind, ProjectionNodeReference from, ProjectionNodeReference to)
        {
            if (seen.Add((kind, from, to))) edges.Add(new("edge." + edges.Count.ToString(CultureInfo.InvariantCulture), kind, from, to));
        }
        void Dependencies(ProjectionNodeReference from, IReadOnlyList<RuleDependency> dependencies)
        {
            foreach (var dependency in dependencies)
                switch (dependency)
                {
                    case FieldDependency field: Add(RelationshipKind.ReadsFrom, from, new FieldNodeReference(field.FieldId)); break;
                    case ParameterDependency parameter: Add(RelationshipKind.DependsOn, from, new ParameterNodeReference(parameter.ParameterId)); break;
                    case NodeDependency node: Add(RelationshipKind.DependsOn, from, node.Node); break;
                }
        }
        foreach (var field in document.Fields) Add(RelationshipKind.BoundaryMember, new FieldNodeReference(field.Id), new BoundaryNodeReference(field.BoundaryId));
        foreach (var rule in document.Rules)
        {
            var from = new RuleNodeReference(rule.Id);
            Add(RelationshipKind.WritesTo, from, new FieldNodeReference(rule.OutputFieldId));
            Dependencies(from, rule.Dependencies);
        }
        foreach (var control in document.Controls)
        {
            var target = new ControlNodeReference(control.Id);
            foreach (var source in control.AppliesTo) Add(RelationshipKind.GovernedBy, source, target);
            Dependencies(target, control.Dependencies);
        }
        return R(edges.ToArray());
    }

    private static RevisionReportResult Metadata(RevisionReportKey key, RevisionReportObservation observation, int seed) =>
        new(Id(3000 + seed), Id(4000 + seed), key, 9007199254740993L, Actor, Started, Started.AddSeconds(1),
            new(PrivateObjectId.Restore(key.Scope, Id(2000 + seed), PrivateObjectClass.DerivedArtifact), Sha256Digest.FromBytes([4]), 1), observation);
    private static RevisionComparisonV1 Compare(Fixture left, Fixture right, RevisionComparisonLimits? limits = null) =>
        new RevisionComparisonService().Compare(left.Side(), right.Side(), limits);
    private static ComparisonItemV1 Item(RevisionComparisonV1 result, ComparisonEntityV1 entity, string leftId) =>
        Assert.Single(result.Items, item => item.Entity == entity && item.Left.Any(candidate => NodeId(candidate.Node) == leftId));
    private static ComparisonFacetV1 Facet(ComparisonItemV1 item, ComparisonAspectV1 aspect) =>
        Assert.Single(item.Facets, facet => facet.Aspect == aspect);
    private static string? NodeId(ProjectionNodeReference? node) => node switch
    {
        BoundaryNodeReference boundary => boundary.BoundaryId,
        FieldNodeReference field => field.FieldId,
        ParameterNodeReference parameter => parameter.ParameterId,
        RuleNodeReference rule => rule.RuleId,
        ControlNodeReference control => control.ControlId,
        null => null,
        _ => throw new InvalidOperationException("Unexpected fixture node kind.")
    };
    private static Fixture RuleWith(Fixture fixture, IReadOnlyList<RuleDependency>? dependencies = null,
        IReadOnlyList<DetailNode>? details = null)
    {
        var rule = fixture.Document.Rules[0];
        return fixture.WithDocument(fixture.Document with
        {
            Rules = R(rule with
            { Dependencies = dependencies ?? rule.Dependencies, Details = details ?? rule.Details })
        });
    }
    private static void ResourceLimit(Action action) => Assert.Equal(RevisionComparisonFailure.ResourceLimit,
        Assert.Throws<RevisionComparisonException>(action).Failure);
    private static void Invalid(Action action) => Assert.Equal(RevisionComparisonFailure.InvalidInput,
        Assert.Throws<RevisionComparisonException>(action).Failure);
    private static byte[] Bytes(RevisionComparisonV1 result) => RevisionReportResponseSerializer.Serialize(result, 1048576);
}
