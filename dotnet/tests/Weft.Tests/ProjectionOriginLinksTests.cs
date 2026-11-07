using System.Collections;
using Weft.Application;
using Weft.Contracts.Operations;
using Weft.Contracts.Projections;
using Weft.Domain;

namespace Weft.Tests;

/// <summary>Authored native evidence only; these pure index tests do not claim engine or storage execution.</summary>
public sealed class ProjectionOriginLinksTests
{
    private static readonly RevisionReportPolicy Limits = new();
    private static readonly NativeLocator Graph = new GraphNodeNativeLocator("provider.document", "node/exact", "function/exact");
    private static readonly FieldDataType TextType = new(FieldDataTypeKind.Scalar, [ScalarDataType.String]);

    [Fact]
    public void ExplicitOriginsResolveToAllFiveTypedOwnersWithoutVerifyingExampleProvenance()
    {
        var boundaryNative = new JsonPointerNativeLocator("provider.document", "/boundary/a~1b~0c");
        var fieldNative = new TextSpanNativeLocator("provider.document", "9007199254740993", "2", TextSpanUnit.UnicodeScalars);
        var parameterNative = new OpaqueNativeLocator("provider.document", "custom", "value\0/exact");
        var controlNative = new GraphNodeNativeLocator("provider.document", "node/exact", "other-function");
        var document = Document() with
        {
            Boundaries = [new("boundary", "Boundary", BoundaryRole.NamedInput, "opaque", BoundaryShape.Object, null, boundaryNative)],
            Fields = [new("field", "boundary", [], "Field", TextType, new(CardinalityKind.Unknown),
                FieldRequirement.Unknown, FieldNullability.Unknown, fieldNative)],
            Parameters = [Parameter("parameter", parameterNative)],
            Rules = [Rule("rule", Graph)],
            Controls = [Control("control", controlNative)]
        };

        var links = ProjectionOriginLinks.Create(document, Limits);

        Assert.Equal(new BoundaryNodeReference("boundary"), links.Find(boundaryNative));
        Assert.Equal(new FieldNodeReference("field"), links.Find(fieldNative));
        Assert.Equal(new ParameterNodeReference("parameter"), links.Find(parameterNative));
        Assert.Equal(new RuleNodeReference("rule"), links.Find(Graph));
        Assert.Equal(new ControlNodeReference("control"), links.Find(controlNative));
        Assert.Null(links.Find(new GraphNodeNativeLocator("other.document", "node/exact", "function/exact")));
        Assert.Null(links.Find(new GraphNodeNativeLocator("provider.document", "node/exact")));
        Assert.Null(links.Find(null));
        // Pure Create deliberately accepts this unrelated display provenance; persisted callers use FromVerifiedReport.
        Assert.Equal("display-only", document.Provenance.Revision.OrganizationId);
    }

    [Fact]
    public void RepeatedExplicitOriginForOneOwnerIsUniqueAndSharedOriginStaysAmbiguous()
    {
        DetailNode[] repeated = [new UnsupportedDetail("extension", "First", Graph),
            new GroupDetail("Nested", [new ExpressionDetail("operation", [new UnsupportedDetail("extension", "Again", Graph)])])];
        var document = Document() with { Rules = [Rule("rule", Graph, repeated)] };
        var unique = ProjectionOriginLinks.Create(document, Limits);
        Assert.Equal(new RuleNodeReference("rule"), unique.Find(Graph));

        var shared = ProjectionOriginLinks.Create(document with
        {
            Controls = [Control("control", new OpaqueNativeLocator("provider.document", "custom", "other"),
                [new UnsupportedDetail("extension", "Shared", Graph)])],
            Parameters = [Parameter("parameter", Graph), Parameter("rule", Graph)]
        }, Limits);
        Assert.Null(shared.Find(Graph));
    }

    [Fact]
    public void BroadEvidenceAndDisplayTextNeverInventNavigation()
    {
        var broad = new DocumentNativeLocator("provider.document");
        var empty = new JsonPointerNativeLocator("provider.document", "");
        var document = Document() with
        {
            Rules = [Rule("rule", broad,
                [new ExpressionDetail("node/exact:function/exact", [new LiteralDetail(new StringLiteralValue("/native/pointer")),
                    new ReferenceDetail(new RuleNodeReference("rule")), new UnsupportedDetail("extension", "Broad", empty)])])],
            Controls = [Control("control", empty)]
        };

        var links = ProjectionOriginLinks.Create(document, Limits);

        Assert.Null(links.Find(broad));
        Assert.Null(links.Find(empty));
        Assert.Null(links.Find(Graph));
        Assert.Null(links.Find(new JsonPointerNativeLocator("provider.document", "/native/pointer")));
    }

    [Fact]
    public void IndexOwnsItsEvidenceAfterCallerReplacesAndClearsTopLevelAndNestedLists()
    {
        var nestedNative = new OpaqueNativeLocator("provider.document", "custom", "nested");
        var replacement = new OpaqueNativeLocator("provider.document", "custom", "replacement");
        var children = new List<DetailNode> { new UnsupportedDetail("extension", "Original", nestedNative) };
        DetailNode[] details = [new GroupDetail("Nested", children)];
        var rules = new List<ProjectionRule> { Rule("rule", Graph, details) };
        var parameters = new List<ProjectionParameter> { Parameter("parameter", replacement) };
        var links = ProjectionOriginLinks.Create(Document() with { Rules = rules, Parameters = parameters }, Limits);

        rules[0] = Rule("other", replacement);
        children.Clear();
        details[0] = new UnsupportedDetail("extension", "Changed", replacement);
        parameters.Clear();
        rules.Clear();

        Assert.Equal(new RuleNodeReference("rule"), links.Find(Graph));
        Assert.Equal(new RuleNodeReference("rule"), links.Find(nestedNative));
        Assert.Equal(new ParameterNodeReference("parameter"), links.Find(replacement));
    }

    [Fact]
    public void ActualOriginAndDetailEntriesShareOneInclusiveAggregateBudget()
    {
        // One rule origin, one group detail, and one unsupported detail: three actual entries,
        // even though the same owner/origin is retained once in the dictionary.
        var document = Document() with
        {
            Rules = [Rule("rule", Graph, [new GroupDetail("Nested", [new UnsupportedDetail("extension", "Again", Graph)])])]
        };
        Assert.Equal(new RuleNodeReference("rule"), ProjectionOriginLinks.Create(document, new(itemLimit: 3)).Find(Graph));
        Limit(() => ProjectionOriginLinks.Create(document, new(itemLimit: 2)));
        var ignored = Document() with { Parameters = [Parameter("parameter", new DocumentNativeLocator("provider.document"))], Rules = document.Rules };
        Limit(() => ProjectionOriginLinks.Create(ignored, new(itemLimit: 3)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DefaultAggregateLimitCountsDuplicateOwnersRatherThanDictionaryCardinality(bool exceed)
    {
        var count = 8192 + (exceed ? 1 : 0);
        var parameters = Enumerable.Repeat(Parameter("same-owner", Graph), count).ToArray();
        var document = Document() with { Parameters = parameters };
        if (exceed) Limit(() => ProjectionOriginLinks.Create(document, Limits));
        else Assert.Equal(new ParameterNodeReference("same-owner"), ProjectionOriginLinks.Create(document, Limits).Find(Graph));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(16)]
    public void DetailDepthIncludesActualNestedUnsupportedLeafAndEmptyChildrenDoNotAddALevel(int limit)
    {
        var caps = new RevisionReportPolicy(detailDepthLimit: limit);
        var leafAtLimit = Document() with { Rules = [Rule("rule", Graph, [Nested(limit)])] };
        Assert.Equal(new RuleNodeReference("rule"), ProjectionOriginLinks.Create(leafAtLimit, caps).Find(Graph));
        var leafTooDeep = Document() with { Rules = [Rule("rule", Graph, [Nested(limit + 1)])] };
        Limit(() => ProjectionOriginLinks.Create(leafTooDeep, caps));
        var emptyAtLimit = Document() with { Rules = [Rule("rule", Graph, [Nested(limit, emptyLeaf: true)])] };
        Assert.Equal(new RuleNodeReference("rule"), ProjectionOriginLinks.Create(emptyAtLimit, caps).Find(Graph));
    }

    [Fact]
    public void ChangedCountAndUnreadableCollectionRefuseCategoricallyWithoutRetainingInputText()
    {
        Integrity(() => ProjectionOriginLinks.Create(Document() with { Parameters = new ChangingList<ProjectionParameter>(Parameter("parameter", Graph)) }, Limits));
        var refusal = Assert.Throws<RevisionReportValidationException>(() => ProjectionOriginLinks.Create(Document() with
        {
            Rules = [Rule("rule", Graph, new UnreadableList<DetailNode>())]
        }, Limits));
        Assert.Equal(RevisionReportValidationFailure.Integrity, refusal.Failure);
        Assert.DoesNotContain("private-source", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CancellationDuringIndexedReadStopsBeforeAnyFurtherTraversalAndLookupObservesToken()
    {
        using var source = new CancellationTokenSource();
        var values = new CancelingList<ProjectionParameter>(Parameter("parameter", Graph), source);
        Assert.Throws<OperationCanceledException>(() => ProjectionOriginLinks.Create(Document() with { Parameters = values }, Limits, source.Token));
        Assert.Equal(1, values.Reads);
        var links = ProjectionOriginLinks.Create(Document(), Limits);
        Assert.Throws<OperationCanceledException>(() => links.Find(null, source.Token));
        Assert.Throws<OperationCanceledException>(() => ProjectionOriginLinks.Create(Document(), Limits, source.Token));
    }

    [Fact]
    public void LookupAndBuildBoundExactLocatorAndOwnerTextAndRejectUnknownOrNullVariants()
    {
        var caps = new RevisionReportPolicy(textByteLimit: 32);
        var links = ProjectionOriginLinks.Create(Document() with { Parameters = [Parameter("parameter", Graph)] }, caps);
        Limit(() => links.Find(new OpaqueNativeLocator("provider.document", "custom", new string('x', 33))));
        Limit(() => ProjectionOriginLinks.Create(Document() with { Parameters = [Parameter(new string('x', 33), Graph)] }, caps));
        Integrity(() => links.Find(new UnknownNativeLocator("provider.document")));
        Integrity(() => ProjectionOriginLinks.Create(Document() with { Parameters = [null!] }, Limits));
        Integrity(() => ProjectionOriginLinks.Create(Document() with { Rules = [Rule("rule", Graph, [new UnknownDetail()])] }, Limits));
        Integrity(() => ProjectionOriginLinks.Create(Document() with { Rules = [Rule("rule", Graph, [null!])] }, Limits));
    }

    [Fact]
    public void VerifiedEntryUsesActualOwnedReportAndIntersectionOfStoredAndActivePolicy()
    {
        // These are explicitly authored metadata facts for a pure test, not persisted examples or a storage claim.
        var scope = new RevisionScope(Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Guid.Parse("33333333-3333-3333-3333-333333333333"), Guid.Parse("44444444-4444-4444-4444-444444444444"));
        var digest = Sha256Digest.FromBytes("abc"u8);
        var profile = new RevisionReportBackendProfile("independent", "engine-a", "build-a", "adapter-a");
        var source = new RevisionReportSource(scope, new(PrivateObjectId.Restore(scope, Guid.Parse("55555555-5555-5555-5555-555555555555"),
            PrivateObjectClass.NativeArtifact), digest, 3), "provider.document", "dependencies-a", profile.BackendId);
        var key = new ProjectionCacheKey(source, profile, "weft.mapping-projection.v1", "independent.project.v1", "codec-a", new(itemLimit: 4));
        var publicScope = new ScopedRevisionId(scope.OrganizationId.ToString("D"), scope.ProjectId.ToString("D"), scope.MappingId.ToString("D"), scope.RevisionId.ToString("D"));
        var operation = new OperationProvenanceV1(publicScope, new("sha256", digest.Value), new(profile.BackendId, profile.BackendRevision, profile.BackendBuild), profile.AdapterBuild, true);
        ProjectionParameter[] parameters = [Parameter("parameter", Graph)];
        var report = new ProjectionReportV1(OperationStatusV1.Succeeded, operation, Document() with
        {
            Provenance = new(publicScope, operation.NativeArtifactDigest, operation.Backend, profile.AdapterBuild, key.AlgorithmVersion),
            Parameters = parameters
        }, []);
        var observation = new RevisionReportObservation(RevisionReportOperation.Projection, RevisionReportBackendStatus.Succeeded,
            new(scope, digest, profile, true), new(RevisionReportCoverage.Complete, 1, 0, 0, 0), null);
        var start = new DateTimeOffset(2026, 10, 14, 0, 0, 0, TimeSpan.Zero);
        var metadata = new RevisionReportResult(Guid.Parse("66666666-6666-6666-6666-666666666666"), Guid.Parse("77777777-7777-7777-7777-777777777777"),
            key, 1, new PrincipalId(Guid.Parse("88888888-8888-8888-8888-888888888888")), start, start.AddSeconds(1),
            new(PrivateObjectId.Restore(scope, Guid.Parse("99999999-9999-9999-9999-999999999999"), PrivateObjectClass.DerivedArtifact),
                Sha256Digest.FromBytes([4]), 1), observation);
        var verified = VerifiedProjectionReport.Create(metadata, report, Limits);
        parameters[0] = Parameter("changed", new OpaqueNativeLocator("provider.document", "custom", "changed"));

        var links = ProjectionOriginLinks.FromVerifiedReport(verified, Limits);
        Assert.Equal(new ParameterNodeReference("parameter"), links.Find(Graph));
        Assert.Same(metadata, verified.Metadata);
        Assert.Equal(new ParameterNodeReference("parameter"), ProjectionOriginLinks.FromVerifiedReport(verified, new(itemLimit: 1)).Find(Graph));
        Limit(() => ProjectionOriginLinks.FromVerifiedReport(verified, new(textByteLimit: 4)));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => ProjectionOriginLinks.FromVerifiedReport(verified, Limits, canceled.Token));
    }

    private static ProjectionDocumentV1 Document() => new(new(new("display-only", "project", "mapping", "revision"),
        new("sha256", "display-digest"), new("provider", "engine", "build"), "adapter", "projection"),
        new(CoverageStatus.Complete, []), [], [], [], [], [], [], [], []);
    private static ProjectionParameter Parameter(string id, NativeLocator native) => new(id, "Parameter", TextType, FieldRequirement.Optional, null, native);
    private static ProjectionRule Rule(string id, NativeLocator native, IReadOnlyList<DetailNode>? details = null) => new(id, "unverified-output", "Rule", [], details ?? [], native);
    private static ProjectionControl Control(string id, NativeLocator native, IReadOnlyList<DetailNode>? details = null) => new(id, ControlKind.Filter, "Control", [], [], details ?? [], native);
    private static DetailNode Nested(int levels, bool emptyLeaf = false)
    {
        DetailNode detail = emptyLeaf ? new ExpressionDetail("empty", []) : new UnsupportedDetail("extension", "Origin", Graph);
        for (var index = 0; index < levels; index++) detail = new GroupDetail("Nested", [detail]);
        return detail;
    }
    private static void Integrity(Action action) => Assert.Equal(RevisionReportValidationFailure.Integrity, Assert.Throws<RevisionReportValidationException>(action).Failure);
    private static void Limit(Action action) => Assert.Equal(RevisionReportValidationFailure.ResourceLimit, Assert.Throws<RevisionReportValidationException>(action).Failure);

    private sealed record UnknownNativeLocator(string DocumentId) : NativeLocator(DocumentId);
    private sealed record UnknownDetail : DetailNode;
    private sealed class ChangingList<T>(T item) : IReadOnlyList<T>
    {
        private int _reads;
        public int Count => ++_reads == 1 ? 1 : 2;
        public T this[int index] => item;
        public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException("Indexed traversal only.");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private sealed class UnreadableList<T> : IReadOnlyList<T>
    {
        public int Count => 1;
        public T this[int index] => throw new InvalidOperationException("private-source");
        public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException("Indexed traversal only.");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private sealed class CancelingList<T>(T item, CancellationTokenSource cancellation) : IReadOnlyList<T>
    {
        public int Count => 1;
        public int Reads { get; private set; }
        public T this[int index] { get { Reads++; cancellation.Cancel(); return item; } }
        public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException("Indexed traversal only.");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
