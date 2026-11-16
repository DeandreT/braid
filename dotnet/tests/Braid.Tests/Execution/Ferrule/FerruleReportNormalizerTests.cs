using Braid.Application;
using Braid.Contracts.Operations;
using Braid.Contracts.Projections;
using Braid.Contracts.Validation;
using Braid.Domain;
using Braid.MappingBackends;

namespace Braid.Tests;

public sealed class FerruleReportNormalizerTests
{
    [Theory]
    [InlineData(MappingOperationStatus.InvalidMapping, OperationStatusV1.InvalidMapping)]
    [InlineData(MappingOperationStatus.UnsupportedArtifact, OperationStatusV1.UnsupportedArtifact)]
    [InlineData(MappingOperationStatus.ResourceLimit, OperationStatusV1.ResourceLimit)]
    [InlineData(MappingOperationStatus.ProtocolFailure, OperationStatusV1.ProtocolFailure)]
    [InlineData(MappingOperationStatus.BackendUnavailable, OperationStatusV1.BackendUnavailable)]
    [InlineData(MappingOperationStatus.BackendFailure, OperationStatusV1.BackendFailure)]
    [InlineData(MappingOperationStatus.TimedOut, OperationStatusV1.TimedOut)]
    [InlineData(MappingOperationStatus.Canceled, OperationStatusV1.Canceled)]
    [InlineData(MappingOperationStatus.IsolationFailure, OperationStatusV1.IsolationFailure)]
    public void ClosedOperationalStatusesAndAbsentValidityAreMappedWithoutInventedSuccess(MappingOperationStatus actual, OperationStatusV1 expected)
    {
        var artifact = FerruleReportTestData.Artifact();
        var provenance = FerruleReportTestData.Provenance(artifact, false);
        var diagnostic = new MappingDiagnostic("actual.failure", DiagnosticSeverity.Error, "Actual failure", false);
        var projection = FerruleReportNormalizer.Projection(new(actual, provenance, null, [diagnostic]), artifact, new());
        var validation = FerruleReportNormalizer.Validation(new(actual, provenance, null, 0, 0, false, [diagnostic]), artifact, new());
        Assert.Equal(expected, projection.Status); Assert.Equal(expected, validation.Status);
        Assert.Null(projection.Document); Assert.Null(validation.IsValid);
        Assert.False(validation.Provenance!.Confirmed);
        Assert.Equal("actual.failure", Assert.Single(validation.Diagnostics).Code);
    }

    [Fact]
    public void StrictReportCapsApplyEvenAtDefaultsWhileExampleCompatibilityIsExplicit()
    {
        var artifact = FerruleReportTestData.Artifact();
        var provenance = FerruleReportTestData.Provenance(artifact);
        var longMessage = new string('A', 2049);
        var actual = new MappingProjectionResult(MappingOperationStatus.ResourceLimit, provenance, null,
            [new("actual.limit", DiagnosticSeverity.Error, longMessage, false)]);
        AssertFailure(RevisionReportValidationFailure.ResourceLimit, () => FerruleReportNormalizer.Projection(actual, artifact, new()));
        Assert.Equal(longMessage, Assert.Single(FerruleReportNormalizer.ProjectionForExample(actual, artifact).Diagnostics).Message);
        AssertFailure(RevisionReportValidationFailure.ResourceLimit, () => FerruleReportNormalizer.Projection(actual with
        { Diagnostics = [new("actual.limit", DiagnosticSeverity.Error, new string('x', 129), false)] }, artifact, new(textByteLimit: 128)));
    }

    [Fact]
    public void OperationProvenanceUsesTheCorrectContextWithoutClaimingCurrentProfileVerification()
    {
        var artifact = FerruleReportTestData.Artifact();
        var longBuild = new string('B', 2049);
        var provenance = FerruleReportTestData.Provenance(artifact) with { BackendBuild = longBuild };
        Assert.Equal(longBuild, FerruleReportNormalizer.Provenance(provenance, artifact).Backend.BuildId);
        var result = new MappingProjectionResult(MappingOperationStatus.BackendFailure, provenance, null, []);
        Assert.Equal(longBuild, FerruleReportNormalizer.ProjectionForExample(result, artifact).Provenance!.Backend.BuildId);
        AssertFailure(RevisionReportValidationFailure.ResourceLimit, () => FerruleReportNormalizer.Projection(result, artifact, new()));
        var reduced = result with { Provenance = provenance with { BackendBuild = new string('b', 100) } };
        AssertFailure(RevisionReportValidationFailure.ResourceLimit, () => FerruleReportNormalizer.Projection(reduced, artifact, new(textByteLimit: 80)));
        AssertFailure(RevisionReportValidationFailure.Integrity, () => FerruleReportNormalizer.Provenance(provenance with
        { NativeDigest = Sha256Digest.FromBytes("other"u8) }, artifact));
    }

    [Fact]
    public void StrictNullValidityRetainsIndependentCountsWhileExampleKeepsItsOlderRestriction()
    {
        var artifact = FerruleReportTestData.Artifact();
        var result = new MappingValidationResult(MappingOperationStatus.BackendFailure, FerruleReportTestData.Provenance(artifact),
            null, 3, 1, true, [new("actual.failure", DiagnosticSeverity.Warning, "Actual partial failure", true)]);
        var report = FerruleReportNormalizer.Validation(result, artifact, new());
        Assert.Null(report.IsValid); Assert.Equal(3, report.TotalIssueCount); Assert.Equal(1, report.EmittedIssueCount);
        Assert.True(report.Truncated); Assert.Single(report.Diagnostics);
        AssertFailure(RevisionReportValidationFailure.Integrity, () => FerruleReportNormalizer.ValidationForExample(result, artifact));
    }

    [Fact]
    public void ExampleContradictionsRetainProtocolClassificationAndDiagnosticCheckPrecedence()
    {
        var artifact = FerruleReportTestData.Artifact();
        var provenance = FerruleReportTestData.Provenance(artifact);
        var result = new MappingValidationResult(MappingOperationStatus.InvalidMapping, provenance, false, 0, 1, false,
            [new("ferrule.validation", DiagnosticSeverity.Error, "Issue", false)]);
        AssertFailure(RevisionReportValidationFailure.Integrity, () => FerruleReportNormalizer.ValidationForExample(result, artifact));
        var tooManyTotal = result with { IsValid = null, TotalIssueCount = 32769, EmittedIssueCount = 0 };
        AssertFailure(RevisionReportValidationFailure.Integrity, () => FerruleReportNormalizer.ValidationForExample(tooManyTotal, artifact));
        AssertFailure(RevisionReportValidationFailure.ResourceLimit, () => FerruleReportNormalizer.Validation(tooManyTotal, artifact, new()));
        var tooManyDiagnostics = tooManyTotal with { Diagnostics = Enumerable.Repeat(result.Diagnostics[0], 129).ToArray() };
        AssertFailure(RevisionReportValidationFailure.ResourceLimit, () => FerruleReportNormalizer.ValidationForExample(tooManyDiagnostics, artifact));
    }

    [Fact]
    public void StrictProviderAdmissionRunsBeforeOptionalResponseLinking()
    {
        var artifact = FerruleReportTestData.Artifact();
        var result = new MappingValidationResult(MappingOperationStatus.InvalidMapping, FerruleReportTestData.Provenance(artifact),
            false, 1, 1, false, [new("ferrule.validation", DiagnosticSeverity.Error, "Issue", false,
                new DocumentNativeLocator(InspectionPolicy.DocumentId), Location: "schema")]);
        var calls = 0;
        var report = FerruleReportNormalizer.Validation(result, artifact, new(), _ => { calls++; return new FieldNodeReference("selected.field"); });
        Assert.Equal("selected.field", Assert.IsType<FieldNodeReference>(Assert.Single(report.Diagnostics).ProjectionLocator).FieldId);
        Assert.Equal(1, calls);
        var nativeOnly = FerruleReportNormalizer.Validation(result, artifact, new());
        Assert.Null(Assert.Single(nativeOnly.Diagnostics).ProjectionLocator);
    }

    [Fact]
    public void AllNativeOwnersEndpointsAndPathStepsKeepExactLexicalFactsAndOwnPathArrays()
    {
        var artifact = FerruleReportTestData.Artifact();
        SchemaOwnerStep[] schema = [new ChildSchemaOwnerStep("0"), new DynamicFieldSchemaOwnerStep(), new ContainsSchemaOwnerStep("1"), new DependentSchemaOwnerStep("2")];
        ScopeOwnerStep[] scope = [new ChildScopeOwnerStep("0"), new DynamicChildScopeOwnerStep("1"), new SegmentScopeOwnerStep("2")];
        MappingDiagnosticOwner[] owners = [new GraphNodeDiagnosticOwner("9007199254740993", "4294967295"), new UserFunctionDiagnosticOwner("9007199254740993"),
            new EndpointDiagnosticOwner(new SourceMappingEndpoint()), new EndpointDiagnosticOwner(new TargetMappingEndpoint()),
            new EndpointDiagnosticOwner(new NamedSourceMappingEndpoint("0", "source/a~b")), new EndpointDiagnosticOwner(new NamedTargetMappingEndpoint("1", "target.name")),
            new SchemaNodeDiagnosticOwner(new NamedSourceMappingEndpoint("0", "source"), schema), new ScopeDiagnosticOwner(new TargetMappingEndpoint(), scope), new FailureRuleDiagnosticOwner("9007199254740993")];
        var diagnostics = owners.Select(owner => new MappingDiagnostic("actual", DiagnosticSeverity.Error, "Issue", false,
            new DocumentNativeLocator(InspectionPolicy.DocumentId), owner, "a\0/~b", true)).ToArray();
        var report = FerruleReportNormalizer.ValidationForExample(new(MappingOperationStatus.InvalidMapping,
            FerruleReportTestData.Provenance(artifact), false, diagnostics.Length, diagnostics.Length, true, diagnostics), artifact);
        schema[0] = new ChildSchemaOwnerStep("99"); scope[0] = new ChildScopeOwnerStep("98");
        Assert.Equal("9007199254740993", Assert.IsType<GraphNodeNativeOwnerV1>(report.Diagnostics[0].Owner).FunctionId);
        Assert.Equal("4294967295", Assert.IsType<GraphNodeNativeOwnerV1>(report.Diagnostics[0].Owner).NodeId);
        Assert.Equal("9007199254740993", Assert.IsType<UserFunctionNativeOwnerV1>(report.Diagnostics[1].Owner).FunctionId);
        Assert.IsType<SourceNativeEndpointV1>(Assert.IsType<EndpointNativeOwnerV1>(report.Diagnostics[2].Owner).Endpoint);
        Assert.IsType<TargetNativeEndpointV1>(Assert.IsType<EndpointNativeOwnerV1>(report.Diagnostics[3].Owner).Endpoint);
        Assert.Equal("source/a~b", Assert.IsType<NamedSourceNativeEndpointV1>(Assert.IsType<EndpointNativeOwnerV1>(report.Diagnostics[4].Owner).Endpoint).Name);
        Assert.Equal("target.name", Assert.IsType<NamedTargetNativeEndpointV1>(Assert.IsType<EndpointNativeOwnerV1>(report.Diagnostics[5].Owner).Endpoint).Name);
        var schemaOwner = Assert.IsType<SchemaNodeNativeOwnerV1>(report.Diagnostics[6].Owner);
        Assert.Equal("0", Assert.IsType<ChildSchemaNativeOwnerStepV1>(schemaOwner.Path[0]).Index);
        Assert.IsType<DynamicFieldSchemaNativeOwnerStepV1>(schemaOwner.Path[1]);
        Assert.Equal("1", Assert.IsType<ContainsSchemaNativeOwnerStepV1>(schemaOwner.Path[2]).Index);
        Assert.Equal("2", Assert.IsType<DependentSchemaNativeOwnerStepV1>(schemaOwner.Path[3]).Index);
        var scopeOwner = Assert.IsType<ScopeNativeOwnerV1>(report.Diagnostics[7].Owner);
        Assert.Equal("0", Assert.IsType<ChildScopeNativeOwnerStepV1>(scopeOwner.Path[0]).Index);
        Assert.Equal("1", Assert.IsType<DynamicChildScopeNativeOwnerStepV1>(scopeOwner.Path[1]).Index);
        Assert.Equal("2", Assert.IsType<SegmentScopeNativeOwnerStepV1>(scopeOwner.Path[2]).Index);
        Assert.Equal("9007199254740993", Assert.IsType<FailureRuleNativeOwnerV1>(report.Diagnostics[8].Owner).Index);
        Assert.All(report.Diagnostics, diagnostic => Assert.Equal("a\0/~b", diagnostic.Location));
        Assert.Throws<NotSupportedException>(() => ((IList<SchemaNativeOwnerStepV1>)schemaOwner.Path).Clear());
    }

    [Fact]
    public void UnknownModelsAndCanceledNormalizationRefuseWithoutManufacturingFacts()
    {
        var artifact = FerruleReportTestData.Artifact();
        var result = new MappingProjectionResult((MappingOperationStatus)999, FerruleReportTestData.Provenance(artifact), null, []);
        AssertFailure(RevisionReportValidationFailure.Integrity, () => FerruleReportNormalizer.Projection(result, artifact, new()));
        Assert.Throws<OperationCanceledException>(() => FerruleReportNormalizer.ProjectionForExample(result, artifact, new(true)));
        AssertFailure(RevisionReportValidationFailure.Integrity, () => FerruleReportNormalizer.ValidationForExample(
            new(MappingOperationStatus.Succeeded, FerruleReportTestData.Provenance(artifact), null, 0, 0, false, []), artifact));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ChangedDiagnosticCountRefusesBeforeTraversalCanExceedItsAdmittedBound(bool example, bool mutateDuringRead)
    {
        var artifact = FerruleReportTestData.Artifact();
        var diagnostics = new ChangingDiagnostics(mutateDuringRead);
        var result = new MappingValidationResult(MappingOperationStatus.BackendFailure,
            FerruleReportTestData.Provenance(artifact), null, 0, 0, false, diagnostics);

        AssertFailure(RevisionReportValidationFailure.Integrity, () =>
        {
            if (example) FerruleReportNormalizer.ValidationForExample(result, artifact);
            else FerruleReportNormalizer.Validation(result, artifact, new());
        });
        Assert.Equal(mutateDuringRead ? 1 : 0, diagnostics.IndexedReads);
    }

    private sealed class ChangingDiagnostics(bool mutateDuringRead) : IReadOnlyList<MappingDiagnostic>
    {
        private int _countReads;
        private bool _changed;
        public int IndexedReads { get; private set; }
        public int Count
        {
            get
            {
                if (++_countReads == 2 && !mutateDuringRead) _changed = true;
                return _changed ? 2 : 1;
            }
        }
        public MappingDiagnostic this[int index]
        {
            get
            {
                if (index != 0) throw new InvalidOperationException("Traversal exceeded the admitted diagnostic count.");
                IndexedReads++;
                if (mutateDuringRead) _changed = true;
                return new("actual.failure", DiagnosticSeverity.Error, "Actual failure", false);
            }
        }
        public IEnumerator<MappingDiagnostic> GetEnumerator() => throw new InvalidOperationException("Use bounded indexed reads.");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static void AssertFailure(RevisionReportValidationFailure failure, Action action) =>
        Assert.Equal(failure, Assert.Throws<RevisionReportValidationException>(action).Failure);
}
