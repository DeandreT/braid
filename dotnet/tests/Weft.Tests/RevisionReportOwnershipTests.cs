using System.Collections;
using Weft.Application;
using Weft.Contracts.Examples;
using Weft.Contracts.Operations;
using Weft.Contracts.Projections;
using Weft.Contracts.Validation;
using Weft.Domain;

namespace Weft.Tests;

public sealed class RevisionReportOwnershipTests
{
    private static readonly RevisionScope Scope = new(Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Guid.Parse("22222222-2222-2222-2222-222222222222"), Guid.Parse("33333333-3333-3333-3333-333333333333"),
        Guid.Parse("44444444-4444-4444-4444-444444444444"));
    private static readonly PrincipalId Actor = new(Guid.Parse("55555555-5555-5555-5555-555555555555"));
    private static readonly RevisionReportBackendProfile Profile = new("independent", "engine.v1", "build.v1", "adapter.v1");
    private static readonly DateTimeOffset Started = new(2026, 10, 14, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void BackendAndVerifiedProjectionOwnParametersScalarTypesAndExactLiteralText()
    {
        var key = ProjectionKey();
        ScalarDataType[] types = [ScalarDataType.String];
        ProjectionParameter[] parameters = [new("p1", "Parameter", new(FieldDataTypeKind.Scalar, types),
            FieldRequirement.Optional, new StringLiteralValue("before\0after"), new DocumentNativeLocator("provider.document"))];
        var report = Projection(key, parameters);
        var observation = ProjectionObservation(key, 1);
        var backend = RevisionReportBackendResult.ForProjection(key, report, observation);
        var verified = VerifiedProjectionReport.Create(Result(key, observation), report, new RevisionReportPolicy());

        types[0] = ScalarDataType.Boolean;
        parameters[0] = parameters[0] with { Id = "replacement", DefaultValue = new StringLiteralValue("changed") };

        foreach (var owned in new[] { backend.ProjectionReport!, verified.Report })
        {
            Assert.Equal("p1", owned.Document!.Parameters[0].Id);
            Assert.Equal(ScalarDataType.String, Assert.Single(owned.Document.Parameters[0].DataType.ScalarTypes));
            Assert.Equal("before\0after", Assert.IsType<StringLiteralValue>(owned.Document.Parameters[0].DefaultValue).Value);
            Assert.False(owned.Document.Parameters is ProjectionParameter[]);
            Assert.Throws<NotSupportedException>(() => ((IList<ProjectionParameter>)owned.Document.Parameters)[0] = parameters[0]);
        }
        Assert.Equal(1, backend.Observation!.Projection!.DocumentItemCount);
        Assert.Null(backend.ValidationReport);
        Assert.Null(backend.Failure);
        Assert.NotSame(report.Document, verified.Report.Document);
    }

    [Fact]
    public void AcceptedValidationOwnsDiagnosticArraysAndBothNativeOwnerPathFamilies()
    {
        var key = ValidationKey();
        SchemaNativeOwnerStepV1[] schemaPath = [new ChildSchemaNativeOwnerStepV1("0"), new DynamicFieldSchemaNativeOwnerStepV1()];
        ScopeNativeOwnerStepV1[] scopePath = [new SegmentScopeNativeOwnerStepV1("2"), new DynamicChildScopeNativeOwnerStepV1("1")];
        ValidationDiagnosticV1[] diagnostics =
        [
            new("issue.schema", DiagnosticSeverity.Error, "Schema issue", false,
                new JsonPointerNativeLocator("provider.document", "/a~1b/~0"),
                new SchemaNodeNativeOwnerV1(new NamedSourceNativeEndpointV1("0", "Source"), schemaPath)),
            new("issue.scope", DiagnosticSeverity.Warning, "Scope issue", true,
                new GraphNodeNativeLocator("provider.document", "opaque-node", "opaque-function"),
                new ScopeNativeOwnerV1(new TargetNativeEndpointV1(), scopePath), "exact\0location")
        ];
        var report = new ValidationReportV1(OperationStatusV1.InvalidMapping, OperationProvenance(key), false, 2, 2, true, diagnostics);
        var observation = new RevisionReportObservation(RevisionReportOperation.Validation, RevisionReportBackendStatus.InvalidMapping,
            Provenance(key), null, new(false, 2, 2, true, 2));
        var backend = RevisionReportBackendResult.ForValidation(key, report, observation);
        var verified = VerifiedValidationReport.Create(Result(key, observation), report, new RevisionReportPolicy());

        schemaPath[0] = new ChildSchemaNativeOwnerStepV1("9");
        scopePath[0] = new SegmentScopeNativeOwnerStepV1("8");
        diagnostics[0] = diagnostics[0] with { Code = "changed", Owner = null };

        foreach (var owned in new[] { backend.ValidationReport!, verified.Report })
        {
            Assert.Equal("issue.schema", owned.Diagnostics[0].Code);
            Assert.Equal("0", Assert.IsType<ChildSchemaNativeOwnerStepV1>(Assert.IsType<SchemaNodeNativeOwnerV1>(owned.Diagnostics[0].Owner).Path[0]).Index);
            Assert.Equal("2", Assert.IsType<SegmentScopeNativeOwnerStepV1>(Assert.IsType<ScopeNativeOwnerV1>(owned.Diagnostics[1].Owner).Path[0]).Index);
            Assert.Equal("exact\0location", owned.Diagnostics[1].Location);
            Assert.False(owned.Diagnostics is ValidationDiagnosticV1[]);
            Assert.Throws<NotSupportedException>(() => ((IList<ValidationDiagnosticV1>)owned.Diagnostics).Clear());
        }
        Assert.Null(verified.ProjectionDerivationId);
        Assert.Null(backend.ProjectionReport);
    }

    [Fact]
    public void SnapshotOwnsNestedDetailsDependenciesPathsCapabilitiesCoverageAndOrigins()
    {
        var key = ProjectionKey();
        PathSegment[] path = [new FieldPathSegment("a\0/~b", "urn:exact")];
        ScalarDataType[] scalar = [ScalarDataType.Integer];
        RuleDependency[] dependencies = [new ConstantDependency(new ExactNumberLiteralValue(ExactNumberFlavor.Decimal, "-0.00e+2"))];
        DetailNode[] arguments = [new LiteralDetail(new ExactNumberLiteralValue(ExactNumberFlavor.BinaryFloat, "7ff8000000000001"))];
        DetailNode[] children = [new ExpressionDetail("opaque-operation", arguments)];
        DetailNode[] details = [new GroupDetail("group", children)];
        ProjectionNodeReference[] appliesTo = [new FieldNodeReference("f1")];
        string[] boundaryIds = ["b1"];
        CapabilityCondition[] conditions = [new("condition", "Condition description")];
        CoverageGap[] gaps = [new("gap", "Gap message", new OpaqueNativeLocator("other-document", "provider", "exact-origin"))];
        ProjectionDiagnostic[] diagnostics = [new("diag", DiagnosticSeverity.Warning, "Diagnostic", new JsonPointerNativeLocator("other-document", "/~1"))];
        ProjectionField[] fields = [new("f1", "b1", path, "Field", new(FieldDataTypeKind.Scalar, scalar),
            new(CardinalityKind.Bounded, "0", "9007199254740993"), FieldRequirement.Optional, FieldNullability.Nullable,
            new OpaqueNativeLocator("other-document", "provider", "field"))];
        var document = new ProjectionDocumentV1(DocumentProvenance(key), new(CoverageStatus.Partial, gaps),
            [new("b1", "Input", BoundaryRole.PrimaryInput, "custom", BoundaryShape.Object, null, new DocumentNativeLocator("other-document"))],
            fields, [], [new("r1", "f1", "Rule", dependencies, details, new GraphNodeNativeLocator("other-document", "opaque"))],
            [new("c1", ControlKind.Filter, "Control", appliesTo, dependencies, details, new DocumentNativeLocator("other-document"))], [], diagnostics,
            [new(MappingOperation.Project, CapabilityStatus.Conditional, "profile", boundaryIds, conditions)]);
        var copied = RevisionReportSnapshots.CopyProjection(new(OperationStatusV1.Succeeded, OperationProvenance(key), document, []), key.Policy);

        path[0] = new AnyItemPathSegment(); scalar[0] = ScalarDataType.Boolean;
        dependencies[0] = new FieldDependency("replacement"); arguments[0] = new LiteralDetail(new StringLiteralValue("changed"));
        children[0] = new UnsupportedDetail("replacement", "changed", new DocumentNativeLocator("changed"));
        details[0] = new GroupDetail("changed", []); appliesTo[0] = new RuleNodeReference("replacement");
        boundaryIds[0] = "replacement"; conditions[0] = new("replacement", "changed");
        gaps[0] = gaps[0] with { Message = "changed" }; diagnostics[0] = diagnostics[0] with { Message = "changed" };
        fields[0] = fields[0] with { Name = "changed" };

        var owned = copied.Document!;
        Assert.Equal("a\0/~b", Assert.IsType<FieldPathSegment>(owned.Fields[0].Path[0]).Name);
        Assert.Equal(ScalarDataType.Integer, owned.Fields[0].DataType.ScalarTypes[0]);
        Assert.Equal("9007199254740993", owned.Fields[0].Cardinality.Maximum);
        Assert.Equal("-0.00e+2", Assert.IsType<ExactNumberLiteralValue>(Assert.IsType<ConstantDependency>(owned.Rules[0].Dependencies[0]).Value).Value);
        var expression = Assert.IsType<ExpressionDetail>(Assert.IsType<GroupDetail>(owned.Rules[0].Details[0]).Children[0]);
        Assert.Equal("7ff8000000000001", Assert.IsType<ExactNumberLiteralValue>(Assert.IsType<LiteralDetail>(expression.Arguments[0]).Value).Value);
        Assert.Equal("f1", Assert.IsType<FieldNodeReference>(owned.Controls[0].AppliesTo[0]).FieldId);
        Assert.Equal("b1", owned.Capabilities[0].BoundaryIds[0]);
        Assert.Equal("Condition description", owned.Capabilities[0].Conditions[0].Description);
        Assert.Equal("Gap message", owned.Coverage.Gaps[0].Message);
        Assert.Equal("Diagnostic", owned.Diagnostics[0].Message);
        Assert.Throws<NotSupportedException>(() => ((IList<DetailNode>)expression.Arguments).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)owned.Capabilities[0].BoundaryIds).Clear());
    }

    [Fact]
    public void AcceptedFactsRefuseDifferentCountsFullScopeAndUnconfirmedProvenance()
    {
        var key = ProjectionKey();
        var report = Projection(key, []);
        AssertIntegrity(() => RevisionReportBackendResult.ForProjection(key, report, ProjectionObservation(key, 1)));
        var wrong = report with { Provenance = report.Provenance! with { Confirmed = false } };
        AssertIntegrity(() => RevisionReportBackendResult.ForProjection(key, wrong, ProjectionObservation(key, 0)));
        var foreign = report with { Provenance = report.Provenance! with { Revision = report.Provenance.Revision with { MappingId = Guid.NewGuid().ToString("D") } } };
        AssertIntegrity(() => VerifiedProjectionReport.Create(Result(key, ProjectionObservation(key, 0)), foreign, key.Policy));
    }

    [Fact]
    public void StoredValidationCannotClaimProjectionLinksOrAnEmptySelection()
    {
        var key = ValidationKey();
        var diagnostic = new ValidationDiagnosticV1("issue", DiagnosticSeverity.Error, "Issue", false,
            ProjectionLocator: new RuleNodeReference("r1"));
        var report = new ValidationReportV1(OperationStatusV1.InvalidMapping, OperationProvenance(key), false, 1, 1, false, [diagnostic]);
        var observation = new RevisionReportObservation(RevisionReportOperation.Validation, RevisionReportBackendStatus.InvalidMapping,
            Provenance(key), null, new(false, 1, 1, false, 1));
        AssertIntegrity(() => RevisionReportBackendResult.ForValidation(key, report, observation));
        AssertIntegrity(() => VerifiedValidationReport.Create(Result(key, observation), report, key.Policy));
        Assert.Throws<ArgumentException>(() => VerifiedValidationReport.Create(Result(key, observation), report, key.Policy, Guid.Empty));
    }

    [Fact]
    public void SnapshotLimitsBoundAggregateNestedWorkAndCycles()
    {
        var key = ProjectionKey();
        var parameters = new[] { new ProjectionParameter("p", "Parameter", new(FieldDataTypeKind.Scalar, new[] { ScalarDataType.String }),
            FieldRequirement.Optional, null, new DocumentNativeLocator("document")) };
        AssertLimit(() => RevisionReportSnapshots.CopyProjection(Projection(key, parameters), new(itemLimit: 1)));
        DetailNode[] children = new DetailNode[1];
        children[0] = new GroupDetail("cycle", children);
        var report = Projection(key, []) with
        {
            Document = Projection(key, []).Document! with
            { Rules = [new("r", "f", "Rule", [], children, new DocumentNativeLocator("document"))] }
        };
        AssertLimit(() => RevisionReportSnapshots.CopyProjection(report, new(detailDepthLimit: 2)));
    }

    [Fact]
    public void SnapshotRefusesChangingListsAndObservesCancellationDuringCopy()
    {
        var key = ProjectionKey();
        var parameter = new ProjectionParameter("p", "Parameter", new(FieldDataTypeKind.Scalar, [ScalarDataType.String]),
            FieldRequirement.Optional, null, new DocumentNativeLocator("document"));
        AssertIntegrity(() => RevisionReportSnapshots.CopyProjection(Projection(key, new ChangingList<ProjectionParameter>(parameter)), key.Policy));
        using var cancellation = new CancellationTokenSource();
        var list = new CancelingList<ProjectionParameter>(parameter, cancellation);
        Assert.Throws<OperationCanceledException>(() => RevisionReportSnapshots.CopyProjection(Projection(key, list), key.Policy, cancellation.Token));
    }

    [Fact]
    public void SnapshotTextKeepsExactControlsButBoundsBytesAndRejectsMalformedUnicode()
    {
        var key = ProjectionKey();
        ProjectionReportV1 WithText(string text) => Projection(key,
            [new("p", "Parameter", new(FieldDataTypeKind.Scalar, [ScalarDataType.String]), FieldRequirement.Optional,
                new StringLiteralValue(text), new DocumentNativeLocator("document"))]);
        AssertLimit(() => RevisionReportSnapshots.CopyProjection(WithText(new string('é', 33)), new(textByteLimit: 64)));
        AssertLimit(() => RevisionReportSnapshots.CopyProjection(WithText(new string('x', 1000000)), new(textByteLimit: 64)));
        AssertIntegrity(() => RevisionReportSnapshots.CopyProjection(WithText(new string((char)0xd800, 1)), key.Policy));
        var owned = RevisionReportSnapshots.CopyProjection(WithText("\0\n\t\r"), key.Policy);
        Assert.Equal("\0\n\t\r", Assert.IsType<StringLiteralValue>(owned.Document!.Parameters[0].DefaultValue).Value);
    }

    [Fact]
    public void FailureKeepsActualObservationOrTrueAbsenceWithoutAReport()
    {
        var observed = new RevisionReportObservation(RevisionReportOperation.Validation, RevisionReportBackendStatus.BackendFailure,
            null, null, new(null, 0, 0, false, 1));
        var failure = RevisionReportBackendResult.ForFailure(RevisionReportFailureCategory.StorageIo, observed);
        Assert.Same(observed, failure.Observation);
        Assert.False(failure.IsUsable);
        Assert.Null(failure.Key); Assert.Null(failure.ProjectionReport); Assert.Null(failure.ValidationReport);
        Assert.Null(RevisionReportBackendResult.ForFailure(RevisionReportFailureCategory.BackendUnavailable).Observation);
        Assert.Throws<ArgumentOutOfRangeException>(() => RevisionReportBackendResult.ForFailure((RevisionReportFailureCategory)999));
    }

    [Fact]
    public void VerifiedAvailabilityBindsFullSourceAndKeepsHistoricalActorSeparateFromPresentReader()
    {
        var key = ProjectionKey();
        var metadata = Result(key, ProjectionObservation(key, 0));
        var verified = VerifiedProjectionReport.Create(metadata, Projection(key, []), new(nativeByteLimit: 1));
        var reader = new PrincipalId(Guid.Parse("77777777-7777-7777-7777-777777777777"));
        var source = new AuthorizedRevisionReportSource(key.Source, reader, Started.AddSeconds(5));
        var read = RevisionReportReadResult.ForAvailable(source, verified);
        Assert.Same(source, read.Source);
        Assert.Equal(reader, read.Source!.Actor);
        Assert.Equal(Actor, read.Report!.Metadata.OriginalActor);
        Assert.NotEqual(reader, read.Report.Metadata.OriginalActor);
        Assert.Equal(3, verified.Metadata.Key.Source.NativeObject.ByteCount);
        AssertLimit(() => VerifiedProjectionReport.Create(Result(key, metadata.Observation, 2), Projection(key, []), new(storedObjectByteLimit: 1)));
        var replacement = new PrivateObjectReference(PrivateObjectId.Restore(key.Scope, Guid.NewGuid(), PrivateObjectClass.NativeArtifact), key.NativeDigest, 3);
        var otherSource = new RevisionReportSource(key.Scope, replacement, key.Source.LogicalDocument, key.Source.DependencyIdentity, key.BackendId);
        Assert.Throws<ArgumentException>(() => RevisionReportReadResult.ForAvailable(new(otherSource, reader, Started.AddSeconds(5)), verified));
    }

    [Fact]
    public void ReadAndEnsureFactoriesPreserveDatabaseClockAndRefuseFabricatedAvailable()
    {
        var key = ProjectionKey();
        var source = new AuthorizedRevisionReportSource(key.Source, Actor, Started.AddSeconds(5));
        var empty = new RevisionReportReadSnapshot(source, key, null, 0, null, null);
        Assert.Equal(RevisionReportState.NotDerived, RevisionReportReadResult.ForSnapshot(empty).State);
        var pending = new RevisionReportReadSnapshot(source, key, Guid.NewGuid(), 1, null,
            new(Guid.NewGuid(), 1, Started, Started.AddSeconds(60), null, null));
        Assert.Equal(Started.AddSeconds(60), RevisionReportEnsureResult.ForPending(pending).NextAttemptAtUtc);
        var expired = new RevisionReportReadSnapshot(new(key.Source, Actor, Started.AddSeconds(60)), key,
            pending.CacheEntryId, 1, null, pending.CurrentAttempt);
        Assert.Equal(RevisionReportState.Pending, RevisionReportReadResult.ForSnapshot(expired).State);
        Assert.Throws<ArgumentException>(() => RevisionReportEnsureResult.ForPending(expired));
        var failed = new RevisionReportReadSnapshot(source, key, Guid.NewGuid(), 1, null,
            new(Guid.NewGuid(), 1, Started, Started.AddSeconds(60), Started.AddSeconds(1),
                RevisionReportCompletion.ForFailure(RevisionReportFailureCategory.TimedOut)));
        Assert.Equal(Started.AddSeconds(31), RevisionReportEnsureResult.ForRetryDelayed(failed).NextAttemptAtUtc);
        var metadata = Result(key, ProjectionObservation(key, 0));
        var available = new RevisionReportReadSnapshot(source, key, metadata.CacheEntryId, 1, metadata,
            new(metadata.DerivationId, 1, Started, Started.AddSeconds(60), metadata.FinishedAtUtc,
                RevisionReportCompletion.ForUsable(key, metadata.Observation, metadata.DerivedObject)));
        Assert.Throws<ArgumentException>(() => RevisionReportReadResult.ForSnapshot(available));
        var corrupt = RevisionReportReadResult.ForReadFailure(source, metadata, RevisionReportReadOutcome.StorageIntegrity);
        Assert.Equal(RevisionReportState.Available, corrupt.State); Assert.Null(corrupt.Report); Assert.Null(corrupt.Failure);
        Assert.Equal(RevisionReportEnsureOutcome.Observed, RevisionReportEnsureResult.ForObserved(corrupt).Outcome);
        Assert.Throws<ArgumentException>(() => RevisionReportEnsureResult.ForObserved(RevisionReportReadResult.WithoutData(RevisionReportReadOutcome.AdmissionBusy)));
        Assert.Throws<ArgumentOutOfRangeException>(() => RevisionReportReadResult.WithoutData(RevisionReportReadOutcome.Observed));
        Assert.Throws<ArgumentOutOfRangeException>(() => RevisionReportEnsureResult.WithoutData(RevisionReportEnsureOutcome.Pending));
    }

    private static ProjectionCacheKey ProjectionKey() => new(Source(), Profile, "weft.mapping-projection.v1", "weft.synthetic-projection.v1", "weft.revision-report-codec.v1", new());
    private static ValidationCacheKey ValidationKey() => new(Source(), Profile, "weft.mapping-validation.v1", "weft.synthetic-validation.v1", "weft.revision-report-codec.v1", new());
    private static RevisionReportSource Source() => new(Scope, new(PrivateObjectId.Restore(Scope,
        Guid.Parse("66666666-6666-6666-6666-666666666666"), PrivateObjectClass.NativeArtifact), Sha256Digest.FromBytes([1, 2, 3]), 3),
        "entry.native.json", "weft.dependencies.empty.v1", Profile.BackendId);
    private static ScopedRevisionId PublicScope() => new(Scope.OrganizationId.ToString("D"), Scope.ProjectId.ToString("D"), Scope.MappingId.ToString("D"), Scope.RevisionId.ToString("D"));
    private static OperationProvenanceV1 OperationProvenance(RevisionReportKey key) => new(PublicScope(), new("sha256", key.NativeDigest.Value), new(key.BackendId, key.BackendRevision, key.BackendBuild), key.AdapterBuild, true);
    private static ProjectionProvenance DocumentProvenance(RevisionReportKey key) => new(PublicScope(), new("sha256", key.NativeDigest.Value), new(key.BackendId, key.BackendRevision, key.BackendBuild), key.AdapterBuild, key.AlgorithmVersion);
    private static ProjectionReportV1 Projection(RevisionReportKey key, IReadOnlyList<ProjectionParameter> parameters) =>
        new(OperationStatusV1.Succeeded, OperationProvenance(key), new(DocumentProvenance(key), new(CoverageStatus.Complete, []), [], [], parameters, [], [], [], [], []), []);
    private static RevisionReportProvenance Provenance(RevisionReportKey key) => new(key.Scope, key.NativeDigest, key.Profile, true);
    private static RevisionReportObservation ProjectionObservation(RevisionReportKey key, int documentItems) => new(RevisionReportOperation.Projection, RevisionReportBackendStatus.Succeeded, Provenance(key), new(RevisionReportCoverage.Complete, documentItems, 0, 0, 0), null);
    private static RevisionReportResult Result(RevisionReportKey key, RevisionReportObservation observation, long bytes = 1) => new(Guid.NewGuid(), Guid.NewGuid(), key, 1, Actor, Started, Started.AddSeconds(1),
        new(PrivateObjectId.Restore(Scope, Guid.NewGuid(), PrivateObjectClass.DerivedArtifact), Sha256Digest.FromBytes([4]), bytes), observation);
    private static void AssertIntegrity(Action action) => Assert.Equal(RevisionReportValidationFailure.Integrity, Assert.Throws<RevisionReportValidationException>(action).Failure);
    private static void AssertLimit(Action action) => Assert.Equal(RevisionReportValidationFailure.ResourceLimit, Assert.Throws<RevisionReportValidationException>(action).Failure);

    private sealed class ChangingList<T>(T item) : IReadOnlyList<T>
    {
        private int _reads;
        public int Count => ++_reads == 1 ? 1 : 2;
        public T this[int index] => item;
        public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException("Indexed bounded access only.");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class CancelingList<T>(T item, CancellationTokenSource cancellation) : IReadOnlyList<T>
    {
        public int Count => 1;
        public T this[int index] { get { cancellation.Cancel(); return item; } }
        public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException("Indexed bounded access only.");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
