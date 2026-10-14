using Weft.Application;
using Weft.Contracts.Examples;
using Weft.Contracts.Operations;
using Weft.Contracts.Projections;
using Weft.Domain;

namespace Weft.Tests;

public sealed partial class RevisionReportNeutralValidationTests
{
    [Fact]
    public void NeutralGraphAcceptsProviderIdsUnorderedNamedBoundariesAndMultipleProfiles()
    {
        var report = Graph();
        var observed = CheckProjection(report);
        Assert.Equal(RevisionReportBackendStatus.Succeeded, observed.Status);
        Assert.Equal(new RevisionReportProvenance(Scope, Digest, Profile, true), observed.Provenance);
        Assert.Equal(new RevisionReportProjectionSummary(RevisionReportCoverage.Complete, 16, 0, 0, 0), observed.Projection);
        Assert.Equal("customer.name", Assert.IsType<FieldPathSegment>(report.Document!.Fields[0].Path[0]).Name);
        Assert.Equal("urn:attribute", Assert.IsType<AttributePathSegment>(report.Document.Fields[0].Path[1]).NamespaceUri);
        Assert.Equal("9007199254740993", Assert.IsType<IndexPathSegment>(report.Document.Fields[0].Path[2]).Index);
        Assert.Equal("provider.document", report.Document.Fields[0].NativeLocator.DocumentId);
    }

    [Theory]
    [InlineData("boundary", "parameter")]
    [InlineData("parameter", "control")]
    [InlineData("control", "field")]
    [InlineData("field", "boundary")]
    public void DeclaredProducesAcceptsDifferentResolvedEndpointKinds(string fromKind, string toKind)
    {
        var report = Graph();
        ProjectionNodeReference Endpoint(string kind) => kind switch
        {
            "boundary" => new BoundaryNodeReference("in"),
            "parameter" => Parameter,
            "control" => Control,
            "field" => Out,
            _ => throw new InvalidOperationException()
        };
        var edge = new ProjectionRelationship("declared-produce", RelationshipKind.Produces, Endpoint(fromKind), Endpoint(toKind));
        report = WithDocument(report, report.Document! with { Relationships = R(report.Document.Relationships.Concat([edge]).ToArray()) });
        Assert.Equal(17, CheckProjection(report).Projection!.DocumentItemCount);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrongKind")]
    [InlineData("self")]
    [InlineData("duplicate")]
    public void DeclaredProducesRefusesUnresolvedSelfOrDuplicateEdges(string change)
    {
        var report = Graph();
        var edge = new ProjectionRelationship("produce", RelationshipKind.Produces, Parameter, new BoundaryNodeReference("in"));
        edge = change switch
        {
            "missing" => edge with { To = new BoundaryNodeReference("absent") },
            "wrongKind" => edge with { To = new FieldNodeReference("in") },
            "self" => edge with { To = Parameter },
            _ => edge
        };
        var extra = change == "duplicate" ? new[] { edge, edge with { Id = "produce-again" } } : [edge];
        report = WithDocument(report, report.Document! with { Relationships = R(report.Document.Relationships.Concat(extra).ToArray()) });
        Integrity(() => CheckProjection(report));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void EveryCanonicalRelationshipIsRequiredByItsRepresentedFact(int omitted)
    {
        var report = Graph();
        report = WithDocument(report, report.Document! with
        { Relationships = R(report.Document.Relationships.Where((_, index) => index != omitted).ToArray()) });
        Integrity(() => CheckProjection(report));
    }

    [Theory]
    [InlineData("edgeKind")]
    [InlineData("reverse")]
    [InlineData("duplicateTuple")]
    [InlineData("duplicateId")]
    [InlineData("idCollidesWithNode")]
    public void RelationshipsRefuseInventedLineageAndIdentityCollisions(string change)
    {
        var report = Graph();
        var edges = report.Document!.Relationships.ToArray();
        edges[3] = change switch
        {
            "edgeKind" => edges[3] with { Kind = RelationshipKind.DependsOn },
            "reverse" => edges[3] with { From = In, To = Rule },
            "duplicateId" => edges[3] with { Id = edges[0].Id },
            "idCollidesWithNode" => edges[3] with { Id = "rule" },
            _ => edges[3]
        };
        var result = change == "duplicateTuple" ? edges.Concat([edges[3] with { Id = "extra-read" }]).ToArray() : edges;
        report = WithDocument(report, report.Document with { Relationships = R(result) });
        Integrity(() => CheckProjection(report));
    }

    [Fact]
    public void GlobalNodeIdsAndTypedReferenceKindsRemainDistinct()
    {
        var report = Graph();
        var doc = report.Document!;
        Integrity(() => CheckProjection(WithDocument(report, doc with { Parameters = R(doc.Parameters[0] with { Id = "rule" }) })));
        var rule = doc.Rules[0] with { Dependencies = R<RuleDependency>(new NodeDependency(new FieldNodeReference("parameter"))) };
        Integrity(() => CheckProjection(WithDocument(report, doc with { Rules = R(rule) })));
        var control = doc.Controls[0] with { Details = R<DetailNode>(new ReferenceDetail(new RuleNodeReference("absent"))) };
        Integrity(() => CheckProjection(WithDocument(report, doc with { Controls = R(control) })));
    }

    [Fact]
    public void RootFieldMustBelongToItsBoundaryWithAnExactlyEmptyPath()
    {
        var report = Graph();
        var doc = report.Document!;
        var rootBoundary = doc.Boundaries[0] with { RootFieldId = "out.field" };
        var rooted = doc with
        {
            Boundaries = R(rootBoundary, doc.Boundaries[1]),
            Fields = R(doc.Fields[0], doc.Fields[1] with { Path = R<PathSegment>() })
        };
        Assert.NotNull(CheckProjection(WithDocument(report, rooted)).Projection);
        Integrity(() => CheckProjection(WithDocument(report, doc with { Boundaries = R(rootBoundary, doc.Boundaries[1]) })));
        Integrity(() => CheckProjection(WithDocument(report, rooted with
        { Boundaries = R(rootBoundary with { RootFieldId = "in.field" }, doc.Boundaries[1]) })));
    }

    [Theory]
    [InlineData(CoverageStatus.Partial)]
    [InlineData(CoverageStatus.Unavailable)]
    public void LocatedIncompleteCoverageMayRetainTheActualNonemptyGraph(CoverageStatus coverage)
    {
        var report = Graph();
        var doc = report.Document!;
        var gap = new CoverageGap("missing", "This extension has no explanation", Origin("nested-extension"), Rule);
        var changed = doc with { Coverage = new(coverage, R(gap)) };
        var observed = CheckProjection(WithDocument(report, changed));
        Assert.Equal(17, observed.Projection!.DocumentItemCount);
        Assert.Equal(coverage == CoverageStatus.Partial ? RevisionReportCoverage.Partial : RevisionReportCoverage.Unavailable, observed.Projection.Coverage);
        Integrity(() => CheckProjection(WithDocument(report, changed with { Coverage = new(coverage, R<CoverageGap>()) })));
        Integrity(() => CheckProjection(WithDocument(report, changed with { Coverage = new(CoverageStatus.Complete, R(gap)) })));
    }

    [Fact]
    public void NativeOnlyDiagnosticsRemainHonestWhenSharedOriginsCannotNavigate()
    {
        var report = Graph();
        var doc = report.Document!;
        var shared = Origin("shared-origin");
        var fields = R(doc.Fields[0] with { NativeLocator = shared }, doc.Fields[1] with { NativeLocator = shared });
        var diagnostic = new ProjectionDiagnostic("ambiguous", DiagnosticSeverity.Warning, "Multiple owners", shared);
        var changed = doc with { Fields = fields, Diagnostics = R(diagnostic) };
        Assert.Equal(1, CheckProjection(WithDocument(report, changed)).Projection!.DocumentDiagnosticCount);
        Integrity(() => CheckProjection(WithDocument(report, changed with { Diagnostics = R(diagnostic with { ProjectionLocator = In }) })));
        Integrity(() => CheckProjection(WithDocument(report, doc with
        { Diagnostics = R(diagnostic with { NativeLocator = null, ProjectionLocator = In }) })));
    }

    [Theory]
    [InlineData("document")]
    [InlineData("emptyPointer")]
    public void BroadDocumentOriginsCannotNavigateEvenWithOneOwner(string kind)
    {
        var report = Graph();
        var doc = report.Document!;
        NativeLocator locator = kind == "document" ? new DocumentNativeLocator("provider.document") : new JsonPointerNativeLocator("provider.document", "");
        var changed = doc with
        {
            Parameters = R(doc.Parameters[0] with { NativeLocator = locator }),
            Diagnostics = R(new ProjectionDiagnostic("broad", DiagnosticSeverity.Information, "Native document", locator, Parameter))
        };
        Integrity(() => CheckProjection(WithDocument(report, changed)));
    }

    [Theory]
    [InlineData("duplicateProfile")]
    [InlineData("missingCondition")]
    [InlineData("missingBoundary")]
    [InlineData("duplicateBoundary")]
    public void CapabilitiesRequireExactProfileIdentityConditionsAndClosedBoundaryScope(string change)
    {
        var report = Graph();
        var doc = report.Document!;
        var capabilities = doc.Capabilities.ToArray();
        capabilities[1] = change switch
        {
            "duplicateProfile" => capabilities[1] with { Profile = capabilities[0].Profile },
            "missingCondition" => capabilities[1] with { Conditions = R<CapabilityCondition>() },
            "missingBoundary" => capabilities[1] with { BoundaryIds = R("absent") },
            "duplicateBoundary" => capabilities[1] with { BoundaryIds = R("in", "in") },
            _ => capabilities[1]
        };
        Integrity(() => CheckProjection(WithDocument(report, doc with { Capabilities = R(capabilities) })));
    }

    [Fact]
    public void RepeatedDependencyRolesAndADeclaredReferenceCycleDoNotInventDagRestrictions()
    {
        var report = Graph();
        var doc = report.Document!;
        var rule = doc.Rules[0] with
        {
            Dependencies = R(doc.Rules[0].Dependencies.Concat(
            [new FieldDependency("in.field", DependencyRole.Condition), new NodeDependency(Control)]).ToArray())
        };
        var edges = R(doc.Relationships.Concat([new ProjectionRelationship("cycle", RelationshipKind.DependsOn, Rule, Control)]).ToArray());
        Assert.Equal(17, CheckProjection(WithDocument(report, doc with { Rules = R(rule), Relationships = edges })).Projection!.DocumentItemCount);
    }
}
