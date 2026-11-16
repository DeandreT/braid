using System.Collections.Generic;
using System.Text;
using Braid.Application;
using Braid.Application.Comparisons;
using Braid.Contracts.Comparisons;
using Braid.Contracts.Projections;
using Braid.Contracts.Validation;
using Braid.Domain;

namespace Braid.Tests;

public sealed partial class RevisionComparisonTests
{
    [Theory]
    [InlineData(CoverageStatus.Partial)]
    [InlineData(CoverageStatus.Unavailable)]
    public void IncompleteCoverageIsUncertainEvenWhenEveryVisibleFactAgrees(CoverageStatus coverage)
    {
        var left = Direct();
        var right = Direct(2, "R");
        right = right.WithDocument(right.Document with
        {
            Coverage = new(coverage,
            R(new CoverageGap("fixture.hidden", "Hidden native branch", Origin("hidden"))))
        });
        var result = Compare(left, right);
        Assert.False(result.HasObservedDifferences);
        Assert.True(result.HasUncertainty);
        Assert.Equal(coverage, result.RightCoverage.Status);
        Assert.Equal(CoverageStatus.Complete, result.LeftCoverage.Status);
    }

    [Theory]
    [InlineData(CoverageStatus.Complete, false)]
    [InlineData(CoverageStatus.Partial, true)]
    [InlineData(CoverageStatus.Unavailable, true)]
    public void AnObservedOneSidedParameterIsDefiniteOnlyAgainstCompleteCoverage(CoverageStatus oppositeCoverage, bool uncertain)
    {
        var left = Direct();
        var right = Direct(2, "R");
        right = right.WithDocument(right.Document with
        {
            Parameters = R<ProjectionParameter>(),
            Coverage = oppositeCoverage == CoverageStatus.Complete ? new(oppositeCoverage, R<CoverageGap>()) :
                new(oppositeCoverage, R(new CoverageGap("fixture.hidden", "Unknown remainder", Origin("hidden"))))
        });
        var result = Compare(left, right);
        var parameter = Item(result, ComparisonEntityV1.Parameter, "L.p");
        Assert.Equal(ComparisonMatchV1.OnlyLeft, parameter.Match);
        Assert.Equal(1, parameter.LeftCandidateCount);
        Assert.Equal(0, parameter.RightCandidateCount);
        Assert.Equal(uncertain, parameter.Facets.Any(facet => facet.HasUncertainty));
        Assert.Equal(uncertain, result.HasUncertainty);
        if (uncertain)
            Assert.Contains(parameter.Facets, facet => facet.Reasons.Contains(ComparisonReasonV1.IncompleteCoverage));
        else
            Assert.True(result.HasObservedDifferences);

        var reversed = Compare(right, left);
        var reversedParameter = Assert.Single(reversed.Items, item => item.Entity == ComparisonEntityV1.Parameter &&
            item.Right.Any(candidate => NodeId(candidate.Node) == "L.p"));
        Assert.Equal(ComparisonMatchV1.OnlyRight, reversedParameter.Match);
        Assert.Equal(0, reversedParameter.LeftCandidateCount);
        Assert.Equal(1, reversedParameter.RightCandidateCount);
        Assert.Empty(reversedParameter.Left);
        Assert.True(NodeId(Assert.Single(reversedParameter.Right).Node) == "L.p", "Reversed parameter candidate changed.");
        Assert.True(result.Right == reversed.Left, "Reversal did not swap the left citation.");
        Assert.True(result.Left == reversed.Right, "Reversal did not swap the right citation.");
        Assert.Equal(result.HasObservedDifferences, reversed.HasObservedDifferences);
        Assert.Equal(result.HasUncertainty, reversed.HasUncertainty);
        Assert.Equal(uncertain, reversedParameter.Facets.Any(facet => facet.HasUncertainty));
        Assert.Equal(oppositeCoverage, reversed.LeftCoverage.Status);
        Assert.Equal(CoverageStatus.Complete, reversed.RightCoverage.Status);
        if (uncertain)
            Assert.Contains(reversedParameter.Facets, facet => facet.Reasons.Contains(ComparisonReasonV1.IncompleteCoverage));
    }

    [Fact]
    public void IncompleteCoverageDoesNotEraseAKnownVisibleDifference()
    {
        var left = Direct();
        var right = Direct(2, "R");
        var dependencies = right.Document.Rules[0].Dependencies.ToArray();
        dependencies[1] = new ConstantDependency(new BooleanLiteralValue(false), DependencyRole.Condition);
        right = RuleWith(right, dependencies);
        right = right.WithDocument(right.Document with
        {
            Coverage = new(CoverageStatus.Partial,
            R(new CoverageGap("fixture.hidden", "Unprojected native branch", Origin("hidden"))))
        });
        var result = Compare(left, right);
        Assert.True(result.HasObservedDifferences);
        Assert.True(result.HasUncertainty);
        Assert.True(Facet(Item(result, ComparisonEntityV1.Rule, "L.rule"), ComparisonAspectV1.Condition).HasObservedDifference);
    }

    [Fact]
    public void NativeOnlyValidationComparesActualFalseValidityAndCountsAcrossProfiles()
    {
        var left = Direct();
        var right = Direct(2, "R");
        var leftValidation = left.Validation(false, 1, R(new ValidationDiagnosticV1("left.issue", DiagnosticSeverity.Error, "Left text", false)));
        var rightValidation = right.Validation(true, 0, profile: new(Profile.BackendId, "other.validation", "other.build", "other.adapter"));
        var result = new RevisionComparisonService().Compare(left.Side(leftValidation), right.Side(rightValidation));
        var item = Assert.Single(result.Items, item => item.Entity == ComparisonEntityV1.Validation);
        Assert.True(result.ValidationIncluded);
        Assert.False(result.LeftValidation!.IsValid);
        Assert.True(result.RightValidation!.IsValid);
        Assert.Equal(1, result.LeftValidation.TotalIssueCount);
        Assert.Equal(1, result.LeftValidation.EmittedIssueCount);
        Assert.True(Facet(item, ComparisonAspectV1.ValidationValidity).HasObservedDifference);
        Assert.True(Facet(item, ComparisonAspectV1.ValidationCounts).HasObservedDifference);
        Assert.False(result.HasUncertainty);
        Assert.Equal(leftValidation.Metadata.DerivationId.ToString("D"), result.Left.Validation!.DerivationId);
        Assert.Equal(rightValidation.Metadata.Key.Fingerprint.Value, result.Right.Validation!.KeyFingerprint);
        Assert.All(item.Left.Concat(item.Right), candidate => { Assert.Null(candidate.Node); Assert.Null(candidate.RelationshipId); });
    }

    [Fact]
    public void DiagnosticTextAndOwnersAreNotMatchedAsNewOrResolvedIssues()
    {
        var left = Direct();
        var right = Direct(2, "R");
        var before = left.Validation(false, 1, R(new ValidationDiagnosticV1("old.code", DiagnosticSeverity.Error,
            "PRIVATE diagnostic before", false, Origin("old.owner"))));
        var after = right.Validation(false, 1, R(new ValidationDiagnosticV1("new.code", DiagnosticSeverity.Warning,
            "PRIVATE diagnostic after", false, Origin("new.owner"), Location: "PRIVATE location")));
        var result = new RevisionComparisonService().Compare(left.Side(before), right.Side(after));
        Assert.True(result.ValidationIncluded);
        Assert.False(result.HasObservedDifferences);
        Assert.False(result.HasUncertainty);
        var json = Encoding.UTF8.GetString(Bytes(result));
        Assert.False(json.Contains("PRIVATE diagnostic", StringComparison.Ordinal), "Comparison response leaked diagnostic text.");
        Assert.False(json.Contains("PRIVATE location", StringComparison.Ordinal), "Comparison response leaked diagnostic location.");
        Assert.False(json.Contains("old.code", StringComparison.Ordinal), "Comparison response leaked a diagnostic code.");
        Assert.False(json.Contains("new.code", StringComparison.Ordinal), "Comparison response leaked a diagnostic code.");
    }

    [Fact]
    public void MissingValidationCounterpartRemainsUnknownRatherThanFalseOrZero()
    {
        var left = Direct();
        var right = Direct(2, "R");
        var result = new RevisionComparisonService().Compare(left.Side(left.Validation(false)), right.Side());
        Assert.True(result.ValidationIncluded);
        Assert.NotNull(result.LeftValidation);
        Assert.False(result.LeftValidation.IsValid);
        Assert.Null(result.RightValidation);
        Assert.Null(result.Right.Validation);
        Assert.True(result.HasUncertainty);
        var item = Assert.Single(result.Items, item => item.Entity == ComparisonEntityV1.Validation);
        Assert.Contains(item.Facets, facet => facet.HasUncertainty && facet.Reasons.Contains(ComparisonReasonV1.MissingValidation));
    }

    [Theory]
    [InlineData("list")]
    [InlineData("message")]
    [InlineData("location")]
    public void TruncatedValidationListOrDiagnosticTextMakesEvenEqualSummariesUncertain(string part)
    {
        var left = Direct();
        var right = Direct(2, "R");
        var diagnostic = new ValidationDiagnosticV1("issue", DiagnosticSeverity.Error, "bounded", part == "message",
            Location: "bounded location", LocationTruncated: part == "location");
        var before = left.Validation(false, 1, R(diagnostic), part == "list");
        var after = right.Validation(false, 1, R(diagnostic), part == "list");
        var result = new RevisionComparisonService().Compare(left.Side(before), right.Side(after));
        Assert.False(result.HasObservedDifferences);
        Assert.True(result.HasUncertainty);
        Assert.Equal(part == "list", result.LeftValidation!.Truncated);
        Assert.Equal(part != "list", result.LeftValidation.HasTruncatedDiagnosticText);
        var item = Assert.Single(result.Items, item => item.Entity == ComparisonEntityV1.Validation);
        Assert.Contains(item.Facets, facet => facet.HasUncertainty && facet.Reasons.Contains(ComparisonReasonV1.TruncatedValidation));
    }

    [Theory]
    [InlineData(0, 2, 2)]
    [InlineData(1, 2, 2)]
    [InlineData(1, 2, 1)]
    [InlineData(1, 1, 2)]
    [InlineData(1, 2, 3)]
    public void OmittedValidationIssuesRemainUncertainWithoutAnyTruncationFlag(int emitted, int leftTotal, int rightTotal)
    {
        var left = Direct();
        var right = Direct(2, "R");
        var diagnostics = emitted == 0 ? R<ValidationDiagnosticV1>() :
            R(new ValidationDiagnosticV1("issue", DiagnosticSeverity.Error, "bounded", false));
        // Use the real neutral factories: omission without the producer flag is a reachable verified input.
        var leftSide = left.Side(left.Validation(false, leftTotal, diagnostics));
        var rightSide = right.Side(right.Validation(false, rightTotal, diagnostics));
        var service = new RevisionComparisonService();
        var result = service.Compare(leftSide, rightSide);
        var reversed = service.Compare(rightSide, leftSide);
        foreach (var comparison in new[] { result, reversed })
        {
            Assert.True(comparison.ValidationIncluded);
            Assert.True(comparison.HasUncertainty);
            Assert.Equal(leftTotal != rightTotal, comparison.HasObservedDifferences);
            Assert.False(comparison.Truncated);
            Assert.Equal(comparison.TotalItemCount, comparison.EmittedItemCount);
            foreach (var summary in new[] { comparison.LeftValidation!, comparison.RightValidation! })
            {
                Assert.False(summary.IsValid);
                Assert.Equal(emitted, summary.EmittedIssueCount);
                Assert.False(summary.Truncated);
                Assert.False(summary.HasTruncatedDiagnosticText);
            }
            var item = Assert.Single(comparison.Items, item => item.Entity == ComparisonEntityV1.Validation);
            var validity = Facet(item, ComparisonAspectV1.ValidationValidity);
            Assert.False(validity.HasObservedDifference);
            Assert.False(validity.HasUncertainty);
            var counts = Facet(item, ComparisonAspectV1.ValidationCounts);
            Assert.Equal(leftTotal != rightTotal, counts.HasObservedDifference);
            Assert.True(counts.HasUncertainty);
            Assert.Contains(ComparisonReasonV1.TruncatedValidation, counts.Reasons);
        }
        Assert.Equal(leftTotal, result.LeftValidation!.TotalIssueCount);
        Assert.Equal(rightTotal, result.RightValidation!.TotalIssueCount);
        Assert.Equal(result.LeftValidation, reversed.RightValidation);
        Assert.Equal(result.RightValidation, reversed.LeftValidation);
        Assert.Equal(Bytes(result), Bytes(service.Compare(leftSide, rightSide)));
    }

    [Fact]
    public void ALinkedValidationMustSelectThisExactProjectionAndProfile()
    {
        var fixture = Direct();
        var id = fixture.Verified().Metadata.DerivationId;
        Assert.NotNull(fixture.Side(fixture.Validation(false, linkedProjection: id)));
        Invalid(() => fixture.Side(fixture.Validation(false, linkedProjection: Id(987654))));
        Invalid(() => fixture.Side(fixture.Validation(false, linkedProjection: id,
            profile: new(Profile.BackendId, "other.revision", Profile.BackendBuild, Profile.AdapterBuild))));
    }

    [Fact]
    public void ValidationFromAnotherRevisionCannotBeAttachedToTheSide()
    {
        var left = Direct();
        var other = Direct(2, "R");
        Invalid(() => left.Side(other.Validation(true)));
    }

    [Fact]
    public void NullValidityCannotBeSubstitutedIntoRealVerifiedUsableMetadata()
    {
        var fixture = Direct();
        var usable = fixture.Validation(true);
        var unknown = usable.Report with { IsValid = null };
        Assert.Throws<RevisionReportValidationException>(() => VerifiedValidationReport.Create(usable.Metadata, unknown, fixture.Key.Policy));
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("digest")]
    [InlineData("bytes")]
    [InlineData("backend")]
    public void CatalogSourceMismatchIsCategoricalAndDoesNotEchoPrivateInputs(string mismatch)
    {
        var fixture = Direct();
        var source = fixture.Catalog;
        var catalog = new CatalogRevisionSummary(mismatch == "scope" ? new RevisionScope(Id(1), Id(2), Id(3), Id(9999)) : source.Scope,
            source.Sequence, source.Admission, source.Creator, source.CommittedAtUtc, "PRIVATE-filename-do-not-echo",
            source.LogicalDocument, mismatch == "digest" ? Sha256Digest.FromBytes([9]) : source.NativeDigest,
            mismatch == "bytes" ? 4 : source.NativeByteCount, mismatch == "backend" ? "PRIVATE-backend-do-not-echo" : source.BackendId);
        var error = Assert.Throws<RevisionComparisonException>(() => RevisionComparisonSide.Create(catalog, fixture.Verified()));
        Assert.Equal(RevisionComparisonFailure.InvalidInput, error.Failure);
        Assert.Null(error.InnerException);
        Assert.False(error.ToString().Contains("PRIVATE", StringComparison.Ordinal), "Comparison refusal leaked private source text.");
        Assert.False(error.ToString().Contains(source.Scope.RevisionId.ToString("D"), StringComparison.Ordinal), "Comparison refusal leaked a revision identifier.");
        Assert.False(error.ToString().Contains(source.NativeDigest.Value, StringComparison.Ordinal), "Comparison refusal leaked a native digest.");
    }

    [Theory]
    [InlineData("organization")]
    [InlineData("project")]
    [InlineData("mapping")]
    public void FullOrganizationProjectAndMappingScopeMustAgree(string component)
    {
        var left = Direct();
        var right = Direct(2, "R");
        var scope = new RevisionScope(component == "organization" ? Id(9) : Id(1), component == "project" ? Id(9) : Id(2),
            component == "mapping" ? Id(9) : Id(3), right.Key.Scope.RevisionId);
        var source = new RevisionReportSource(scope, new(PrivateObjectId.Restore(scope, Id(1002), PrivateObjectClass.NativeArtifact), Digest, 3),
            right.Key.Source.LogicalDocument, right.Key.Source.DependencyIdentity, right.Key.BackendId);
        var key = new ProjectionCacheKey(source, right.Key.Profile, right.Key.SchemaVersion, right.Key.AlgorithmVersion, right.Key.CodecVersion, right.Key.Policy);
        var catalog = new CatalogRevisionSummary(scope, right.Catalog.Sequence, right.Catalog.Admission, Actor, Started, "source.native",
            IngestionIntent.EntryDocument, Digest, 3, key.BackendId);
        right = right with
        {
            Catalog = catalog,
            Key = key,
            Report = right.Report with
            {
                Provenance = OperationProvenance(key),
                Document = right.Document with { Provenance = DocumentProvenance(key) }
            }
        };
        Invalid(() => Compare(left, right));
    }

    [Fact]
    public void SameRevisionIsAllowedAndRetainsTwoExactCitations()
    {
        var fixture = Direct();
        var result = Compare(fixture, fixture);
        Assert.Equal(result.Left, result.Right);
        Assert.False(result.HasObservedDifferences);
        Assert.False(result.HasUncertainty);
    }

    [Fact]
    public void SideRetainsOwnedVerifiedFactsAfterOriginalCallerArraysAreMutated()
    {
        var fixture = Direct();
        var path = new PathSegment[] { new FieldPathSegment("customer.name") };
        var types = new[] { ScalarDataType.String };
        var dependencies = fixture.Document.Rules[0].Dependencies.ToArray();
        var detailArguments = new DetailNode[] { new LiteralDetail(new StringLiteralValue("copy")) };
        var fields = fixture.Document.Fields.ToArray();
        fields[0] = fields[0] with { Path = path, DataType = new(FieldDataTypeKind.Scalar, types) };
        var rules = new[] { fixture.Document.Rules[0] with { Dependencies = dependencies,
            Details = R<DetailNode>(new GroupDetail("Explanation", detailArguments)) } };
        fixture = fixture.WithDocument(fixture.Document with { Fields = fields, Rules = rules });
        var rightFixture = Direct(2, "R");
        rightFixture = RuleWith(rightFixture, details: R<DetailNode>(new GroupDetail("Explanation", R<DetailNode>(new LiteralDetail(new StringLiteralValue("copy"))))));
        var left = fixture.Side();
        var right = rightFixture.Side();
        var before = new RevisionComparisonService().Compare(left, right);

        path[0] = new AttributePathSegment("mutated");
        types[0] = ScalarDataType.Boolean;
        dependencies[0] = new ConstantDependency(new StringLiteralValue("mutated"));
        detailArguments[0] = new LiteralDetail(new StringLiteralValue("mutated"));
        fields[0] = fields[0] with { Name = "mutated" };
        rules[0] = rules[0] with { Summary = "mutated" };

        var after = new RevisionComparisonService().Compare(left, right);
        Assert.False(before.HasObservedDifferences);
        Assert.Equal(Bytes(before), Bytes(after));
    }

    [Fact]
    public void ResultCollectionsAndTargetsAreReadOnlyOwnedSnapshots()
    {
        var left = Direct();
        var right = Direct(2, "R");
        right = right.WithDocument(right.Document with
        {
            Coverage = new(CoverageStatus.Partial,
            R(new CoverageGap("fixture.gap", "A bounded coverage gap", Origin("gap"))))
        });
        var a = Compare(left, right);
        var b = Compare(left, right);
        ReadOnly(a.Items);
        ReadOnly(a.LeftCoverage.Gaps);
        ReadOnly(a.RightCoverage.Gaps);
        for (var index = 0; index < a.Items.Count; index++)
        {
            var item = a.Items[index];
            ReadOnly(item.Left); ReadOnly(item.Right); ReadOnly(item.Facets);
            foreach (var facet in item.Facets) ReadOnly(facet.Reasons);
            foreach (var candidate in item.Left.Concat(item.Right))
                Assert.True((candidate.Node is null) != (candidate.RelationshipId is null));
        }
        Assert.Equal(Bytes(a), Bytes(b));
    }

    private static void ReadOnly<T>(IReadOnlyList<T> values)
    {
        if (values is IList<T> mutableView)
        {
            Assert.True(mutableView.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => mutableView.Clear());
            if (values.Count > 0)
                Assert.Throws<NotSupportedException>(() => mutableView[0] = values[0]);
        }
    }

    [Fact]
    public void ReorderedSourceStoragePreservesDeterministicItemOrderAndFacets()
    {
        var left = Direct();
        var right = Direct(2, "R");
        var shuffled = right.WithDocument(right.Document with
        {
            Boundaries = R(right.Document.Boundaries.Reverse().ToArray()),
            Fields = R(right.Document.Fields.Reverse().ToArray()),
            Relationships = R(right.Document.Relationships.Reverse().ToArray())
        }, false);
        Assert.Equal(Bytes(Compare(left, right)), Bytes(Compare(left, shuffled)));
        var result = Compare(left, right);
        Assert.Equal(result.Items.Select(item => item.Entity).OrderBy(entity => entity), result.Items.Select(item => item.Entity));
    }

    [Fact]
    public void CallerCancellationIsPropagatedWithTheOriginalToken()
    {
        var fixture = Direct();
        var verified = fixture.Verified();
        var left = fixture.Side();
        var right = Direct(2, "R").Side();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var creation = Assert.ThrowsAny<OperationCanceledException>(() => RevisionComparisonSide.Create(fixture.Catalog, verified,
            cancellationToken: cancellation.Token));
        var comparison = Assert.ThrowsAny<OperationCanceledException>(() => new RevisionComparisonService().Compare(left, right,
            cancellationToken: cancellation.Token));
        Assert.Equal(cancellation.Token, creation.CancellationToken);
        Assert.Equal(cancellation.Token, comparison.CancellationToken);
    }

    [Fact]
    public void DefaultsAreFixedCeilingsAndEveryBoundMayBeReducedToOne()
    {
        var defaults = new RevisionComparisonLimits();
        Assert.Equal(32768, defaults.InputItems);
        Assert.Equal(1000000, defaults.WorkUnits);
        Assert.Equal(1024, defaults.OutputItems);
        Assert.Equal(8, defaults.CandidatesPerSide);
        Assert.Equal(1048576, defaults.OutputUtf8Bytes);
        var minimum = new RevisionComparisonLimits(1, 1, 1, 1, 1);
        Assert.Equal(1, minimum.InputItems); Assert.Equal(1, minimum.WorkUnits); Assert.Equal(1, minimum.OutputItems);
        Assert.Equal(1, minimum.CandidatesPerSide); Assert.Equal(1, minimum.OutputUtf8Bytes);
    }

    [Theory]
    [InlineData("input", 0)]
    [InlineData("input", -1)]
    [InlineData("input", 32769)]
    [InlineData("work", 0)]
    [InlineData("work", -1)]
    [InlineData("work", 1000001)]
    [InlineData("output", 0)]
    [InlineData("output", -1)]
    [InlineData("output", 1025)]
    [InlineData("candidates", 0)]
    [InlineData("candidates", -1)]
    [InlineData("candidates", 9)]
    [InlineData("bytes", 0)]
    [InlineData("bytes", -1)]
    [InlineData("bytes", 1048577)]
    public void BoundsRejectZeroNegativeAndAboveCeilingValues(string bound, int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RevisionComparisonLimits(
            inputItems: bound == "input" ? value : 32768, workUnits: bound == "work" ? value : 1000000,
            outputItems: bound == "output" ? value : 1024, candidatesPerSide: bound == "candidates" ? value : 8,
            outputUtf8Bytes: bound == "bytes" ? value : 1048576));
    }

    [Theory]
    [InlineData("input")]
    [InlineData("work")]
    [InlineData("bytes")]
    public void AnalysisOrSerializationOverflowReturnsCategoricalResourceLimitWithoutPartialResult(string bound)
    {
        var limits = new RevisionComparisonLimits(inputItems: bound == "input" ? 1 : 32768,
            workUnits: bound == "work" ? 1 : 1000000, outputUtf8Bytes: bound == "bytes" ? 1 : 1048576);
        ResourceLimit(() => Compare(Direct(), Direct(2, "R"), limits));
    }

    [Fact]
    public void LongAnchorTextConsumesWorkEvenWithFewTopLevelRecords()
    {
        Fixture ParameterOnly(Fixture fixture) => fixture.WithDocument(fixture.Document with
        {
            Boundaries = R<ProjectionBoundary>(),
            Fields = R<ProjectionField>(),
            Rules = R<ProjectionRule>(),
            Controls = R<ProjectionControl>()
        });
        var left = ParameterOnly(Direct());
        var right = ParameterOnly(Direct(2, "R"));
        Assert.False(Compare(left, right).HasObservedDifferences);
        var name = new string('x', 1900);
        left = left.WithDocument(left.Document with { Parameters = R(left.Document.Parameters[0] with { Name = name }) });
        right = right.WithDocument(right.Document with { Parameters = R(right.Document.Parameters[0] with { Name = name }) });
        ResourceLimit(() => Compare(left, right, new(workUnits: 1024)));
    }

    [Theory]
    [InlineData("anchor")]
    [InlineData("candidate")]
    public void KeyConstructionAndCandidateSortConsumeWorkBeyondEqualCostInputScanning(string phase)
    {
        var padding = new string('x', 1800);
        Fixture Many(Fixture fixture, bool charged) => fixture.WithDocument(fixture.Document with
        {
            Boundaries = R<ProjectionBoundary>(),
            Fields = R<ProjectionField>(),
            Rules = R<ProjectionRule>(),
            Controls = R<ProjectionControl>(),
            Parameters = R(Enumerable.Range(0, 32).Select(index => fixture.Document.Parameters[0] with
            {
                Id = (charged && phase == "candidate" ? padding : "") + index.ToString("D2", System.Globalization.CultureInfo.InvariantCulture),
                Name = (charged && phase == "anchor" ? padding : "") + "x",
                DefaultValue = null,
                NativeLocator = new GraphNodeNativeLocator("fixture.document", (charged ? "" : padding) + "native")
            }).ToArray())
        });
        var left = Direct();
        var right = Direct(2, "R");
        var limits = new RevisionComparisonLimits(workUnits: 180000);
        // Move exactly 1800 UTF16 units per parameter from an ignored locator to its anchor or ID.
        // Counts, enums and total scanned text stay identical; the control completes the whole pipeline.
        var control = Compare(Many(left, false), Many(right, false), limits);
        Assert.False(control.HasObservedDifferences);
        var controlItem = Assert.Single(control.Items);
        Assert.Equal(ComparisonMatchV1.Ambiguous, controlItem.Match);
        Assert.Equal(32, controlItem.LeftCandidateCount);
        Assert.Equal(32, controlItem.RightCandidateCount);
        var chargedLeft = Many(left, true);
        var chargedRight = Many(right, true);
        var full = Compare(chargedLeft, chargedRight);
        Assert.False(full.HasObservedDifferences);
        Assert.Equal(ComparisonMatchV1.Ambiguous, Assert.Single(full.Items).Match);
        ResourceLimit(() => Compare(chargedLeft, chargedRight, limits));
    }

    [Fact]
    public void OutputClippingRetainsFullCountsAndDifferencesBeyondTheEmittedPrefix()
    {
        var left = Direct();
        var right = Direct(2, "R");
        var dependencies = right.Document.Rules[0].Dependencies.ToArray();
        dependencies[1] = new ConstantDependency(new BooleanLiteralValue(false), DependencyRole.Condition);
        right = RuleWith(right, dependencies);
        var full = Compare(left, right);
        var clipped = Compare(left, right, new(outputItems: 1));
        Assert.Equal(14, full.TotalItemCount);
        Assert.Equal(full.TotalItemCount, clipped.TotalItemCount);
        Assert.Equal(1, clipped.EmittedItemCount);
        Assert.Single(clipped.Items);
        Assert.Equal(ComparisonEntityV1.Boundary, clipped.Items[0].Entity);
        Assert.All(clipped.Items[0].Facets, facet => Assert.False(facet.HasObservedDifference));
        Assert.True(clipped.HasObservedDifferences);
        Assert.True(clipped.HasUncertainty);
        Assert.True(clipped.Truncated);
        Assert.Equal(Bytes(clipped), Bytes(Compare(left, right, new(outputItems: 1))));
    }

    [Fact]
    public void CandidateClippingKeepsActualCountsWithoutCrossProductAndSortsDisplayReferences()
    {
        Fixture Many(Fixture fixture, string prefix) => fixture.WithDocument(fixture.Document with
        {
            Parameters = R(Enumerable.Range(0, 10).Reverse().Select(index => fixture.Document.Parameters[0] with
            { Id = prefix + ".p." + index.ToString("D2", System.Globalization.CultureInfo.InvariantCulture), NativeLocator = Origin(prefix + index) }).ToArray())
        });
        var result = Compare(Many(Direct(), "L"), Many(Direct(2, "R"), "R"), new(candidatesPerSide: 2));
        var item = Assert.Single(result.Items, item => item.Entity == ComparisonEntityV1.Parameter);
        Assert.Equal(ComparisonMatchV1.Ambiguous, item.Match);
        Assert.Equal(10, item.LeftCandidateCount);
        Assert.Equal(10, item.RightCandidateCount);
        Assert.Equal(new[] { "L.p.00", "L.p.01" }, item.Left.Select(candidate => NodeId(candidate.Node)));
        Assert.Equal(new[] { "R.p.00", "R.p.01" }, item.Right.Select(candidate => NodeId(candidate.Node)));
        Assert.True(item.CandidatesTruncated);
        Assert.True(result.HasUncertainty);
        Assert.Contains(item.Facets, facet => facet.Reasons.Contains(ComparisonReasonV1.CandidatesOmitted));
    }

    [Fact]
    public void CandidateClippingUsesRawOrdinalReferencesRatherThanLengthPrefixedKeys()
    {
        Fixture Duplicates(Fixture fixture, bool reverse) => fixture.WithDocument(fixture.Document with
        {
            Parameters = R((reverse ? new[] { "aa", "z" } : new[] { "z", "aa" }).Select(id =>
                fixture.Document.Parameters[0] with { Id = id, NativeLocator = Origin(id) }).ToArray())
        });
        var left = Duplicates(Direct(), false);
        var right = Duplicates(Direct(2, "R"), false);
        var full = Compare(left, right);
        var fullItem = Assert.Single(full.Items, item => item.Entity == ComparisonEntityV1.Parameter);
        Assert.Equal(new[] { "aa", "z" }, fullItem.Left.Select(candidate => NodeId(candidate.Node)));
        Assert.Equal(new[] { "aa", "z" }, fullItem.Right.Select(candidate => NodeId(candidate.Node)));
        var clipped = Compare(left, right, new(candidatesPerSide: 1));
        var item = Assert.Single(clipped.Items, item => item.Entity == ComparisonEntityV1.Parameter);
        // Even identical side-local IDs cannot resolve a duplicate semantic anchor by guessed pairing.
        Assert.Equal(ComparisonMatchV1.Ambiguous, item.Match);
        Assert.Equal(2, item.LeftCandidateCount);
        Assert.Equal(2, item.RightCandidateCount);
        Assert.True(NodeId(Assert.Single(item.Left).Node) == "aa", "Left display prefix is not raw ordinal.");
        Assert.True(NodeId(Assert.Single(item.Right).Node) == "aa", "Right display prefix is not raw ordinal.");
        Assert.True(item.CandidatesTruncated);
        Assert.True(clipped.Truncated);
        Assert.True(clipped.HasUncertainty);
        Assert.False(clipped.HasObservedDifferences);
        Assert.Equal(full.TotalItemCount, clipped.TotalItemCount);
        Assert.Contains(item.Facets, facet => facet.Reasons.Contains(ComparisonReasonV1.AmbiguousMatch) &&
            facet.Reasons.Contains(ComparisonReasonV1.CandidatesOmitted));
        Assert.Equal(Bytes(clipped), Bytes(Compare(Duplicates(Direct(), true), right, new(candidatesPerSide: 1))));
        Assert.Equal(Bytes(clipped), Bytes(Compare(left, Duplicates(Direct(2, "R"), true), new(candidatesPerSide: 1))));
        var reversed = Compare(right, left, new(candidatesPerSide: 1));
        var reversedItem = Assert.Single(reversed.Items, candidate => candidate.Entity == ComparisonEntityV1.Parameter);
        Assert.True(NodeId(Assert.Single(reversedItem.Left).Node) == "aa", "Reversed left prefix changed.");
        Assert.True(NodeId(Assert.Single(reversedItem.Right).Node) == "aa", "Reversed right prefix changed.");
        Assert.Equal(item.Match, reversedItem.Match);
        Assert.Equal(clipped.HasObservedDifferences, reversed.HasObservedDifferences);
        Assert.Equal(clipped.HasUncertainty, reversed.HasUncertainty);
    }

    [Fact]
    public void FinalUtf8ByteLimitIncludesBothCitationsAndTheWholeSerializedContract()
    {
        var left = Direct();
        var right = Direct(2, "R");
        var full = Compare(left, right);
        var length = Bytes(full).Length;
        Assert.Equal(Bytes(full), Bytes(Compare(left, right, new(outputUtf8Bytes: length))));
        ResourceLimit(() => Compare(left, right, new(outputUtf8Bytes: length - 1)));
    }

    [Fact]
    public void NewDiagnosticStringRepresentationsDoNotEchoSourceFacts()
    {
        var left = Direct();
        var right = Direct(2, "R");
        var side = left.Side();
        var result = new RevisionComparisonService().Compare(side, right.Side());
        var item = Item(result, ComparisonEntityV1.Rule, "L.rule");
        Assert.NotNull(result.Left.Projection);
        foreach (var text in new[] { side.ToString(), result.ToString(), result.Left.ToString(), result.Left.Projection.ToString(),
            item.ToString(), Assert.Single(item.Left).ToString() })
        {
            Assert.False(text.Contains("PRIVATE-fixture-source", StringComparison.Ordinal), "Comparison diagnostic string leaked private source text.");
            Assert.False(text.Contains("L.rule", StringComparison.Ordinal), "Comparison diagnostic string leaked a node identifier.");
            Assert.False(text.Contains(Digest.Value, StringComparison.Ordinal), "Comparison diagnostic string leaked a native digest.");
            Assert.False(text.Contains(left.Catalog.Scope.RevisionId.ToString("D"), StringComparison.Ordinal), "Comparison diagnostic string leaked a revision identifier.");
        }
    }
}
