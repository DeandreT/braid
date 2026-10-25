using Weft.Application;
using Weft.Contracts.Operations;
using Weft.Contracts.Projections;
using Weft.Contracts.Validation;
using Weft.Domain;

namespace Weft.Tests;

public sealed partial class RevisionReportNeutralValidationTests
{
    [Theory]
    [InlineData("operation", "organization")]
    [InlineData("operation", "project")]
    [InlineData("operation", "mapping")]
    [InlineData("operation", "revision")]
    [InlineData("operation", "algorithm")]
    [InlineData("operation", "digest")]
    [InlineData("operation", "backend")]
    [InlineData("operation", "backendRevision")]
    [InlineData("operation", "backendBuild")]
    [InlineData("operation", "adapter")]
    [InlineData("document", "organization")]
    [InlineData("document", "project")]
    [InlineData("document", "mapping")]
    [InlineData("document", "revision")]
    [InlineData("document", "algorithm")]
    [InlineData("document", "digest")]
    [InlineData("document", "backend")]
    [InlineData("document", "backendRevision")]
    [InlineData("document", "backendBuild")]
    [InlineData("document", "adapter")]
    public void EveryScopeDigestBackendBuildAndAdapterFactMustMatchTheFullKey(string location, string component)
    {
        var report = Graph();
        var revision = component switch
        {
            "organization" => ContractScope with { OrganizationId = Scope.OrganizationId.ToString("N") },
            "project" => ContractScope with { ProjectId = "00000000-0000-0000-0000-000000000000" },
            "mapping" => ContractScope with { MappingId = Scope.RevisionId.ToString("D") },
            "revision" => ContractScope with { RevisionId = Scope.MappingId.ToString("D") },
            _ => ContractScope
        };
        var digest = component switch
        {
            "algorithm" => ContractDigest with { Algorithm = "SHA256" },
            "digest" => ContractDigest with { Value = Digest.Value.ToUpperInvariant() },
            _ => ContractDigest
        };
        var backend = component switch
        {
            "backend" => ContractBackend with { Id = "another.provider" },
            "backendRevision" => ContractBackend with { Revision = "engine-b" },
            "backendBuild" => ContractBackend with { BuildId = "engine-build-b" },
            _ => ContractBackend
        };
        var adapter = component == "adapter" ? "adapter-b" : Profile.AdapterBuild;
        report = location == "operation" ? report with { Provenance = new(revision, digest, backend, adapter, true) } :
            WithDocument(report, report.Document! with { Provenance = new(revision, digest, backend, adapter, "neutral.project.v1") });
        Integrity(() => CheckProjection(report));
    }

    [Fact]
    public void ReportSchemaAlgorithmOperationAndConfirmedDocumentFactsAreIndependent()
    {
        var report = Graph();
        var doc = report.Document!;
        Integrity(() => CheckProjection(WithDocument(report, doc with
        { Provenance = doc.Provenance with { ProjectionVersion = "weft.mapping-projection.v1" } })));
        var wrongSchema = new ProjectionCacheKey(Source, Profile, "other.projection.v1", "neutral.project.v1", "neutral.codec.v1", new());
        Integrity(() => CheckProjection(report, key: wrongSchema));
        Integrity(() => RevisionReportProjectionValidation.Validate(ValidationKey(), report, new()));
        Integrity(() => CheckProjection(report with { Provenance = Provenance with { Confirmed = false } }));
        Integrity(() => CheckProjection(report with { Provenance = null }));
        Integrity(() => CheckProjection(report with { Status = OperationStatusV1.BackendFailure }));
        Integrity(() => CheckProjection(report with { Status = (OperationStatusV1)999 }));
    }

    [Fact]
    public void SuccessfulStatusWithoutADocumentIsAnObservationAndNeverAUsableProjection()
    {
        var report = new ProjectionReportV1(OperationStatusV1.Succeeded, Provenance, null, R<OperationDiagnosticV1>());
        var observation = CheckProjection(report);
        Assert.Equal(RevisionReportBackendStatus.Succeeded, observation.Status);
        Assert.Null(observation.Projection!.Coverage);
        Integrity(() => RevisionReportBackendResult.ForProjection(ProjectionKey(), report, observation));
    }

    [Theory]
    [InlineData(OperationStatusV1.Succeeded)]
    [InlineData(OperationStatusV1.InvalidMapping)]
    [InlineData(OperationStatusV1.BackendFailure)]
    [InlineData(OperationStatusV1.Canceled)]
    public void NullableValidityKeepsActualStatusAndIndependentOperationalDiagnosticsWithoutBecomingUsable(OperationStatusV1 status)
    {
        var report = new ValidationReportV1(status, Provenance, null, 2, 0, false,
            R(new ValidationDiagnosticV1("operation", DiagnosticSeverity.Warning, "Observed message", false)));
        var observed = CheckValidation(report);
        Assert.Null(observed.Validation!.IsValid);
        Assert.Equal(2, observed.Validation.TotalIssueCount);
        Assert.Equal(0, observed.Validation.EmittedIssueCount);
        Assert.Equal(1, observed.Validation.DiagnosticCount);
        Assert.Equal((RevisionReportBackendStatus)((int)status + 1), observed.Status);
        Integrity(() => RevisionReportBackendResult.ForValidation(ValidationKey(), report, observed));
    }

    [Fact]
    public void ValidityCountsAndTruncationRemainIndependentActualFacts()
    {
        var diagnostic = new ValidationDiagnosticV1("warning", DiagnosticSeverity.Warning, "A reported warning", false);
        var trueWithAnIssue = new ValidationReportV1(OperationStatusV1.Succeeded, Provenance, true, 1, 1, false, R(diagnostic));
        Assert.True(CheckValidation(trueWithAnIssue).Validation!.IsValid);
        var falseWithoutIssues = new ValidationReportV1(OperationStatusV1.InvalidMapping, Provenance, false, 0, 0, true, R<ValidationDiagnosticV1>());
        var observed = CheckValidation(falseWithoutIssues);
        Assert.False(observed.Validation!.IsValid);
        Assert.True(observed.Validation.Truncated);
        Assert.True(RevisionReportBackendResult.ForValidation(ValidationKey(), falseWithoutIssues, observed).IsUsable);
        Integrity(() => CheckValidation(trueWithAnIssue with { Status = OperationStatusV1.InvalidMapping }));
        Integrity(() => CheckValidation(falseWithoutIssues with { Status = OperationStatusV1.Succeeded }));
        Integrity(() => CheckValidation(trueWithAnIssue with { Provenance = Provenance with { Confirmed = false } }));
        Integrity(() => CheckValidation(trueWithAnIssue with { EmittedIssueCount = 0 }));
    }

    [Fact]
    public void ValidationIssueAndDiagnosticCapsIncludeTheNullableValiditySuccessorContract()
    {
        var diagnostics = Enumerable.Range(0, 128).Select(index =>
            new ValidationDiagnosticV1("issue", DiagnosticSeverity.Error, "Issue " + index, false)).ToArray();
        var report = new ValidationReportV1(OperationStatusV1.BackendFailure, null, null, 32768, 128, false, R(diagnostics));
        Assert.Equal(128, CheckValidation(report).Validation!.EmittedIssueCount);
        Limit(() => CheckValidation(report with { TotalIssueCount = 32769 }));
        Limit(() => CheckValidation(report with { EmittedIssueCount = 129 }));
        Limit(() => CheckValidation(report with { Diagnostics = R(diagnostics.Concat([diagnostics[0]]).ToArray()) }));
        Integrity(() => CheckValidation(report with { TotalIssueCount = -1 }));
        Integrity(() => CheckValidation(report with { EmittedIssueCount = -1 }));
        Integrity(() => CheckValidation(report with { TotalIssueCount = 127 }));
        Limit(() => CheckValidation(report, new(validationIssueLimit: 32767)));
        Limit(() => CheckValidation(report, new(diagnosticLimit: 127)));
    }

    public static IEnumerable<object[]> NativeOwners()
    {
        yield return ["GraphNodeOpaque"];
        yield return ["UserFunctionOpaque"];
        yield return ["SourceEndpoint"];
        yield return ["TargetEndpoint"];
        yield return ["NamedSourceEmptyNameLargeIndex"];
        yield return ["NamedTargetLargeIndex"];
        yield return ["SchemaPathAllVariants"];
        yield return ["ScopePathAllVariants"];
        yield return ["FailureRuleLargeIndex"];
    }

    private static NativeDiagnosticOwnerV1 NativeOwner(string caseName) => caseName switch
    {
        "GraphNodeOpaque" => new GraphNodeNativeOwnerV1("function/β", "opaque-node/α"),
        "UserFunctionOpaque" => new UserFunctionNativeOwnerV1("function/β"),
        "SourceEndpoint" => new EndpointNativeOwnerV1(new SourceNativeEndpointV1()),
        "TargetEndpoint" => new EndpointNativeOwnerV1(new TargetNativeEndpointV1()),
        "NamedSourceEmptyNameLargeIndex" => new EndpointNativeOwnerV1(new NamedSourceNativeEndpointV1("9007199254740993", "")),
        "NamedTargetLargeIndex" => new EndpointNativeOwnerV1(new NamedTargetNativeEndpointV1("9007199254740994", "named")),
        "SchemaPathAllVariants" => new SchemaNodeNativeOwnerV1(new SourceNativeEndpointV1(), R<SchemaNativeOwnerStepV1>(
            new ChildSchemaNativeOwnerStepV1("0"), new DynamicFieldSchemaNativeOwnerStepV1(),
            new ContainsSchemaNativeOwnerStepV1("9007199254740993"), new DependentSchemaNativeOwnerStepV1("1"))),
        "ScopePathAllVariants" => new ScopeNativeOwnerV1(new TargetNativeEndpointV1(), R<ScopeNativeOwnerStepV1>(
            new ChildScopeNativeOwnerStepV1("0"), new DynamicChildScopeNativeOwnerStepV1("9007199254740993"), new SegmentScopeNativeOwnerStepV1("1"))),
        "FailureRuleLargeIndex" => new FailureRuleNativeOwnerV1("9007199254740993"),
        _ => throw new ArgumentOutOfRangeException(nameof(caseName))
    };

    [Theory]
    [MemberData(nameof(NativeOwners))]
    public void NativeOwnersAreClosedTypedEvidenceWithoutFerruleWidthOrDocumentRestrictions(string caseName)
    {
        var owner = NativeOwner(caseName);
        var diagnostic = new ValidationDiagnosticV1("native", DiagnosticSeverity.Information, "Native evidence", false,
            new JsonPointerNativeLocator("unrelated.provider.document", "/a~1b/~0literal"), owner, "path\0text", true);
        var report = new ValidationReportV1(OperationStatusV1.InvalidMapping, Provenance, false, 1, 1, true, R(diagnostic));
        Assert.Equal(1, CheckValidation(report).Validation!.DiagnosticCount);
    }

    public static IEnumerable<object[]> InvalidNativeOwners()
    {
        yield return ["FailureRuleLeadingZero"];
        yield return ["NamedSourceNegative"];
        yield return ["NamedTargetPlus"];
        yield return ["SchemaChildLeadingZero"];
        yield return ["SchemaContainsFraction"];
        yield return ["SchemaDependentNegative"];
        yield return ["ScopeChildLeadingZero"];
        yield return ["ScopeDynamicNegative"];
        yield return ["ScopeSegmentFraction"];
    }

    private static NativeDiagnosticOwnerV1 InvalidNativeOwner(string caseName) => caseName switch
    {
        "FailureRuleLeadingZero" => new FailureRuleNativeOwnerV1("01"),
        "NamedSourceNegative" => new EndpointNativeOwnerV1(new NamedSourceNativeEndpointV1("-1", "source")),
        "NamedTargetPlus" => new EndpointNativeOwnerV1(new NamedTargetNativeEndpointV1("+1", "target")),
        "SchemaChildLeadingZero" => new SchemaNodeNativeOwnerV1(new SourceNativeEndpointV1(), R<SchemaNativeOwnerStepV1>(new ChildSchemaNativeOwnerStepV1("01"))),
        "SchemaContainsFraction" => new SchemaNodeNativeOwnerV1(new SourceNativeEndpointV1(), R<SchemaNativeOwnerStepV1>(new ContainsSchemaNativeOwnerStepV1("1.0"))),
        "SchemaDependentNegative" => new SchemaNodeNativeOwnerV1(new SourceNativeEndpointV1(), R<SchemaNativeOwnerStepV1>(new DependentSchemaNativeOwnerStepV1("-1"))),
        "ScopeChildLeadingZero" => new ScopeNativeOwnerV1(new TargetNativeEndpointV1(), R<ScopeNativeOwnerStepV1>(new ChildScopeNativeOwnerStepV1("01"))),
        "ScopeDynamicNegative" => new ScopeNativeOwnerV1(new TargetNativeEndpointV1(), R<ScopeNativeOwnerStepV1>(new DynamicChildScopeNativeOwnerStepV1("-1"))),
        "ScopeSegmentFraction" => new ScopeNativeOwnerV1(new TargetNativeEndpointV1(), R<ScopeNativeOwnerStepV1>(new SegmentScopeNativeOwnerStepV1("1.0"))),
        _ => throw new ArgumentOutOfRangeException(nameof(caseName))
    };

    [Theory]
    [MemberData(nameof(InvalidNativeOwners))]
    public void EveryNumericOwnerPathVariantRefusesNoncanonicalIndexText(string caseName)
    {
        var owner = InvalidNativeOwner(caseName);
        var diagnostic = new ValidationDiagnosticV1("native", DiagnosticSeverity.Error, "Bad owner", false, Owner: owner);
        Integrity(() => CheckValidation(new(OperationStatusV1.InvalidMapping, Provenance, false, 1, 1, false, R(diagnostic))));
    }

    [Fact]
    public void OwnerPathItemsAndTraversalCancellationAreBoundedIndependently()
    {
        var path = R<ScopeNativeOwnerStepV1>(new ChildScopeNativeOwnerStepV1("0"), new DynamicChildScopeNativeOwnerStepV1("1"), new SegmentScopeNativeOwnerStepV1("2"));
        var diagnostic = new ValidationDiagnosticV1("native", DiagnosticSeverity.Error, "Scope owner", false,
            Owner: new ScopeNativeOwnerV1(new SourceNativeEndpointV1(), path));
        var report = new ValidationReportV1(OperationStatusV1.InvalidMapping, Provenance, false, 1, 1, false, R(diagnostic));
        Assert.NotNull(CheckValidation(report, new(itemLimit: 4)).Validation);
        Limit(() => CheckValidation(report, new(itemLimit: 3)));
        using var source = new CancellationTokenSource();
        var gated = new CancelOnFirstRead<ScopeNativeOwnerStepV1>(path, source);
        diagnostic = diagnostic with { Owner = new ScopeNativeOwnerV1(new SourceNativeEndpointV1(), gated) };
        Assert.Throws<OperationCanceledException>(() => CheckValidation(report with { Diagnostics = R(diagnostic) }, token: source.Token));
        Assert.Equal(1, gated.Reads);
    }

    [Fact]
    public void NativeOnlyStorageFactoryRefusesPreexistingProjectionLinks()
    {
        var diagnostic = new ValidationDiagnosticV1("linked", DiagnosticSeverity.Error, "Stored diagnostics are native only", false,
            ProjectionLocator: new FieldNodeReference("a-bounded-selected-id"));
        var report = new ValidationReportV1(OperationStatusV1.InvalidMapping, Provenance, false, 1, 1, false, R(diagnostic));
        var observation = CheckValidation(report);
        Assert.NotNull(observation.Validation);
        Integrity(() => RevisionReportBackendResult.ForValidation(ValidationKey(), report, observation));
    }

    [Fact]
    public void MalformedPointersClosedVariantsAndNullMembersRefuseAcceptance()
    {
        var report = Graph();
        var doc = report.Document!;
        foreach (var pointer in new[] { "relative", "/bad~2escape", "/unfinished~" })
            Integrity(() => CheckProjection(WithDocument(report, doc with
            { Parameters = R(doc.Parameters[0] with { NativeLocator = new JsonPointerNativeLocator("document", pointer) }) })));
        Integrity(() => CheckProjection(WithDocument(report, doc with { Fields = R(doc.Fields[0] with { Path = R<PathSegment>(new ForeignPath()) }, doc.Fields[1]) })));
        Integrity(() => CheckProjection(WithDocument(report, doc with { Parameters = R(doc.Parameters[0] with { DefaultValue = new ForeignLiteral() }) })));
        Integrity(() => CheckProjection(WithDocument(report, doc with { Rules = R(doc.Rules[0] with { Details = R<DetailNode>(new ForeignDetail()) }) })));
        Integrity(() => CheckProjection(WithDocument(report, doc with { Rules = R(doc.Rules[0] with { Dependencies = R<RuleDependency>(new ForeignDependency()) }) })));
        Integrity(() => CheckProjection(WithDocument(report, doc with { Parameters = R(doc.Parameters[0] with { NativeLocator = new ForeignLocator("document") }) })));
        Integrity(() => CheckProjection(WithDocument(report, doc with { Controls = R(doc.Controls[0] with { AppliesTo = R<ProjectionNodeReference>(new ForeignReference()) }) })));
        Integrity(() => CheckProjection(report with { Diagnostics = null! }));
        Integrity(() => CheckProjection(WithDocument(report, doc with { Fields = R<ProjectionField>([null!]) })));
        var bad = new ValidationDiagnosticV1("bad", DiagnosticSeverity.Error, "Unsupported owner", false, Owner: new ForeignOwner());
        Integrity(() => CheckValidation(new(OperationStatusV1.InvalidMapping, Provenance, false, 1, 1, false, R(bad))));
        Integrity(() => CheckValidation(new(OperationStatusV1.InvalidMapping, Provenance, false, 1, 1, false,
            R(bad with { Owner = new EndpointNativeOwnerV1(new ForeignEndpoint()) }))));
    }

    private sealed record ForeignPath : PathSegment;
    private sealed record ForeignLiteral : LiteralValue;
    private sealed record ForeignDetail : DetailNode;
    private sealed record ForeignDependency() : RuleDependency(DependencyRole.Value);
    private sealed record ForeignLocator(string DocumentId) : NativeLocator(DocumentId);
    private sealed record ForeignReference : ProjectionNodeReference;
    private sealed record ForeignOwner : NativeDiagnosticOwnerV1;
    private sealed record ForeignEndpoint : NativeEndpointV1;
}
