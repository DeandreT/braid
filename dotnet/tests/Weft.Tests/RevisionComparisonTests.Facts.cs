using Weft.Contracts.Comparisons;
using Weft.Contracts.Projections;

namespace Weft.Tests;

public sealed partial class RevisionComparisonTests
{
    [Fact]
    public void RelabeledLocalIdsKeepAllFourteenHandwrittenCorrespondencesWithoutDifferences()
    {
        var left = Direct();
        var right = Direct(2, "R");
        var result = Compare(left, right);

        Assert.Equal("weft.revision-comparison.v1", result.SchemaVersion);
        Assert.Equal("weft.projection-facts-comparison.v1", result.PolicyVersion);
        Assert.False(result.HasObservedDifferences);
        Assert.False(result.HasUncertainty);
        Assert.False(result.Truncated);
        Assert.Equal(14, result.TotalItemCount);
        Assert.Equal(14, result.EmittedItemCount);
        Assert.Equal(2, result.Items.Count(item => item.Entity == ComparisonEntityV1.Boundary));
        Assert.Equal(3, result.Items.Count(item => item.Entity == ComparisonEntityV1.Field));
        Assert.Equal(1, result.Items.Count(item => item.Entity == ComparisonEntityV1.Parameter));
        Assert.Equal(1, result.Items.Count(item => item.Entity == ComparisonEntityV1.Rule));
        Assert.Equal(1, result.Items.Count(item => item.Entity == ComparisonEntityV1.Control));
        Assert.Equal(6, result.Items.Count(item => item.Entity == ComparisonEntityV1.Relationship));
        Assert.All(result.Items, item =>
        {
            Assert.Equal(ComparisonMatchV1.Matched, item.Match);
            Assert.Equal(1, item.LeftCandidateCount);
            Assert.Equal(1, item.RightCandidateCount);
            Assert.False(item.CandidatesTruncated);
            Assert.All(item.Facets, facet => { Assert.False(facet.HasObservedDifference); Assert.False(facet.HasUncertainty); });
        });
        Assert.Equal("R.a", NodeId(Assert.Single(Item(result, ComparisonEntityV1.Field, "L.a").Right).Node));
        Assert.NotNull(result.Left.Revision);
        Assert.Equal(left.Catalog.Scope.RevisionId.ToString("D"), result.Left.Revision.RevisionId);
        Assert.Equal("9007199254740992", result.Left.Sequence);
        Assert.Equal("9007199254740993", result.Right.Sequence);
        Assert.NotNull(result.Left.Projection);
        Assert.Equal("9007199254740993", result.Left.Projection.Generation);
        Assert.Equal(left.Key.Fingerprint.Value, result.Left.Projection.KeyFingerprint);
        Assert.Equal(left.Verified().Metadata.DerivationId.ToString("D"), result.Left.Projection.DerivationId);
        Assert.Equal("3", result.Left.NativeByteCount);
        Assert.Equal(Digest.Value, result.Left.NativeDigest.Value);
        Assert.False(result.ValidationIncluded);
        Assert.Null(result.LeftValidation);
        Assert.Null(result.RightValidation);
    }

    [Fact]
    public void SameLocalIdsDoNotHideAChangedDirectValueReference()
    {
        var left = Direct();
        var right = Direct(2, "L");
        var dependencies = right.Document.Rules[0].Dependencies.ToArray();
        dependencies[0] = new FieldDependency("L.b");
        right = RuleWith(right, dependencies);
        var result = Compare(left, right);
        var rule = Item(result, ComparisonEntityV1.Rule, "L.rule");

        Assert.Equal(ComparisonMatchV1.Matched, rule.Match);
        Assert.True(Facet(rule, ComparisonAspectV1.Value).HasObservedDifference);
        Assert.False(Facet(rule, ComparisonAspectV1.Value).HasUncertainty);
        Assert.False(Facet(rule, ComparisonAspectV1.Condition).HasObservedDifference);
        Assert.False(Facet(rule, ComparisonAspectV1.Default).HasObservedDifference);
        Assert.True(result.HasObservedDifferences);
        Assert.False(result.HasUncertainty);

        var reversed = Compare(right, left);
        var reversedRule = Item(reversed, ComparisonEntityV1.Rule, "L.rule");
        Assert.True(result.Right == reversed.Left, "Reversal did not swap the left citation.");
        Assert.True(result.Left == reversed.Right, "Reversal did not swap the right citation.");
        Assert.Equal(ComparisonMatchV1.Matched, reversedRule.Match);
        Assert.Equal(1, reversedRule.LeftCandidateCount);
        Assert.Equal(1, reversedRule.RightCandidateCount);
        Assert.True(NodeId(Assert.Single(reversedRule.Left).Node) == "L.rule", "Reversed left rule candidate changed.");
        Assert.True(NodeId(Assert.Single(reversedRule.Right).Node) == "L.rule", "Reversed right rule candidate changed.");
        Assert.True(Facet(reversedRule, ComparisonAspectV1.Value).HasObservedDifference);
        Assert.False(Facet(reversedRule, ComparisonAspectV1.Value).HasUncertainty);
        Assert.Equal(result.HasObservedDifferences, reversed.HasObservedDifferences);
        Assert.Equal(result.HasUncertainty, reversed.HasUncertainty);
    }

    [Theory]
    [InlineData(DependencyRole.Condition, ComparisonAspectV1.Condition)]
    [InlineData(DependencyRole.Default, ComparisonAspectV1.Default)]
    public void ConditionAndDefaultChangesStayInTheirOwnDependencyFacets(DependencyRole role, ComparisonAspectV1 aspect)
    {
        var left = Direct();
        var right = Direct(2, "R");
        var changed = right.Document.Rules[0].Dependencies.Select(dependency => dependency.Role == role
            ? new ConstantDependency(new StringLiteralValue("changed"), role) : dependency).ToArray();
        right = RuleWith(right, changed);
        var result = Compare(left, right);
        var rule = Item(result, ComparisonEntityV1.Rule, "L.rule");

        Assert.True(Facet(rule, aspect).HasObservedDifference);
        Assert.False(Facet(rule, aspect).HasUncertainty);
        Assert.False(Facet(rule, ComparisonAspectV1.Value).HasObservedDifference);
        Assert.False(Facet(rule, role == DependencyRole.Condition ? ComparisonAspectV1.Default : ComparisonAspectV1.Condition).HasObservedDifference);
        Assert.Single(result.Items, item => item.Facets.Any(facet => facet.HasObservedDifference));
        Assert.False(result.HasUncertainty);
    }

    [Fact]
    public void ControlOnlyKindChangeStillPairsByItsGovernedTargets()
    {
        var left = Direct();
        var right = Direct(2, "R");
        right = right.WithDocument(right.Document with { Controls = R(right.Document.Controls[0] with { Kind = ControlKind.Sorting }) });
        var result = Compare(left, right);
        var control = Item(result, ComparisonEntityV1.Control, "L.control");

        Assert.Equal(ComparisonMatchV1.Matched, control.Match);
        Assert.True(Facet(control, ComparisonAspectV1.Control).HasObservedDifference);
        Assert.False(Facet(control, ComparisonAspectV1.Control).HasUncertainty);
        Assert.Single(result.Items, item => item.Facets.Any(facet => facet.HasObservedDifference));
        Assert.All(Item(result, ComparisonEntityV1.Rule, "L.rule").Facets, facet => Assert.False(facet.HasObservedDifference));
    }

    [Fact]
    public void ControlTargetOrderIsComparedAfterMultiplicityPreservingMultisetAnchoring()
    {
        var left = Direct();
        var right = Direct(2, "R");
        left = left.WithDocument(left.Document with
        {
            Controls = R(left.Document.Controls[0] with
            { AppliesTo = R<ProjectionNodeReference>(new RuleNodeReference("L.rule"), new FieldNodeReference("L.a"), new FieldNodeReference("L.a")) })
        });
        right = right.WithDocument(right.Document with
        {
            Controls = R(right.Document.Controls[0] with
            { AppliesTo = R<ProjectionNodeReference>(new FieldNodeReference("R.a"), new RuleNodeReference("R.rule"), new FieldNodeReference("R.a")) })
        });
        var control = Item(Compare(left, right), ComparisonEntityV1.Control, "L.control");

        Assert.Equal(ComparisonMatchV1.Matched, control.Match);
        Assert.True(Facet(control, ComparisonAspectV1.Control).HasObservedDifference);
        Assert.False(Facet(control, ComparisonAspectV1.Control).HasUncertainty);
    }

    [Fact]
    public void RepeatedControlTargetsDoNotCollapseIntoSetBasedMatching()
    {
        var left = Direct();
        var right = Direct(2, "R");
        left = left.WithDocument(left.Document with
        {
            Controls = R(left.Document.Controls[0] with
            { AppliesTo = R<ProjectionNodeReference>(new RuleNodeReference("L.rule"), new RuleNodeReference("L.rule")) })
        });
        var result = Compare(left, right);
        Assert.Equal(ComparisonMatchV1.OnlyLeft, Item(result, ComparisonEntityV1.Control, "L.control").Match);
        Assert.Contains(result.Items, item => item.Entity == ComparisonEntityV1.Control && item.Match == ComparisonMatchV1.OnlyRight);
        Assert.True(result.HasObservedDifferences);
    }

    [Fact]
    public void DuplicateParameterAnchorsAreAmbiguousRatherThanGreedilyPairedOrReportedMissing()
    {
        var left = Direct();
        var right = Direct(2, "R");
        left = left.WithDocument(left.Document with
        {
            Parameters = R(left.Document.Parameters[0], left.Document.Parameters[0] with
            { Id = "L.other-p", DefaultValue = new StringLiteralValue("different"), NativeLocator = Origin("L.other-p") })
        });
        var result = Compare(left, right);
        var item = Assert.Single(result.Items, item => item.Entity == ComparisonEntityV1.Parameter);

        Assert.Equal(ComparisonMatchV1.Ambiguous, item.Match);
        Assert.Equal(2, item.LeftCandidateCount);
        Assert.Equal(1, item.RightCandidateCount);
        Assert.Contains(item.Facets, facet => facet.HasUncertainty && facet.Reasons.Contains(ComparisonReasonV1.AmbiguousMatch));
        Assert.True(result.HasUncertainty);
        Assert.DoesNotContain(result.Items, item => item.Entity == ComparisonEntityV1.Parameter && item.Match is ComparisonMatchV1.OnlyLeft or ComparisonMatchV1.OnlyRight);
    }

    [Fact]
    public void DuplicateBoundaryAnchorsLeaveDependentFieldsUnresolved()
    {
        var left = Direct();
        var right = Direct(2, "R");
        left = left.WithDocument(left.Document with
        {
            Boundaries = R(left.Document.Boundaries[0], left.Document.Boundaries[1],
            left.Document.Boundaries[0] with { Id = "L.duplicate-in", NativeLocator = Origin("L.duplicate-in") })
        });
        var result = Compare(left, right);
        var input = Item(result, ComparisonEntityV1.Boundary, "L.in");
        Assert.Equal(ComparisonMatchV1.Ambiguous, input.Match);
        Assert.Equal(2, input.LeftCandidateCount);
        var field = Item(result, ComparisonEntityV1.Field, "L.a");
        Assert.Contains(field.Facets, facet => facet.HasUncertainty && facet.Reasons.Contains(ComparisonReasonV1.UnresolvedReference));
        Assert.True(result.HasUncertainty);
    }

    [Fact]
    public void MultipleProducersStayAmbiguousAndCannotResolveAControlByFirstProducer()
    {
        var left = Direct();
        var right = Direct(2, "R");
        left = left.WithDocument(left.Document with
        {
            Rules = R(left.Document.Rules[0], left.Document.Rules[0] with
            { Id = "L.second-rule", NativeLocator = Origin("L.second-rule") })
        });
        var result = Compare(left, right);
        var rule = Item(result, ComparisonEntityV1.Rule, "L.rule");
        Assert.Equal(ComparisonMatchV1.Ambiguous, rule.Match);
        Assert.Equal(2, rule.LeftCandidateCount);
        Assert.Equal(1, rule.RightCandidateCount);
        Assert.Contains(Item(result, ComparisonEntityV1.Control, "L.control").Facets,
            facet => facet.HasUncertainty && facet.Reasons.Contains(ComparisonReasonV1.UnresolvedReference));
    }

    [Fact]
    public void DuplicateControlsWithTheSameTargetMultisetStayAmbiguous()
    {
        var left = Direct();
        var right = Direct(2, "R");
        left = left.WithDocument(left.Document with
        {
            Controls = R(left.Document.Controls[0], left.Document.Controls[0] with
            { Id = "L.second-control", Kind = ControlKind.Ordering, NativeLocator = Origin("L.second-control") })
        });
        var result = Compare(left, right);
        var item = Item(result, ComparisonEntityV1.Control, "L.control");
        Assert.Equal(ComparisonMatchV1.Ambiguous, item.Match);
        Assert.Equal(2, item.LeftCandidateCount);
        Assert.Equal(1, item.RightCandidateCount);
        Assert.True(result.HasUncertainty);
    }

    [Fact]
    public void ControlToControlTargetsRemainUncertainWithoutRecursiveIsomorphism()
    {
        var left = Direct();
        var right = Direct(2, "R");
        Fixture AddOuter(Fixture fixture, string prefix) => fixture.WithDocument(fixture.Document with
        {
            Controls = R(fixture.Document.Controls[0], new ProjectionControl(prefix + ".outer", ControlKind.Ordering, "Outer",
            R<ProjectionNodeReference>(new ControlNodeReference(prefix + ".control")), R<RuleDependency>(), R<DetailNode>(), Origin(prefix + ".outer")))
        });
        var result = Compare(AddOuter(left, "L"), AddOuter(right, "R"));
        Assert.True(result.HasUncertainty);
        Assert.Contains(Item(result, ComparisonEntityV1.Control, "L.outer").Facets,
            facet => facet.HasUncertainty && facet.Reasons.Contains(ComparisonReasonV1.UnresolvedReference));
    }

    public static IEnumerable<object[]> DistinctPaths()
    {
        yield return [new PathSegment[] { new FieldPathSegment("customer.name") }, new PathSegment[] { new FieldPathSegment("customer"), new FieldPathSegment("name") }];
        yield return [new PathSegment[] { new FieldPathSegment("id", "urn:a") }, new PathSegment[] { new FieldPathSegment("id", "urn:b") }];
        yield return [new PathSegment[] { new FieldPathSegment("id", "urn:a") }, new PathSegment[] { new AttributePathSegment("id", "urn:a") }];
        yield return [new PathSegment[] { new IndexPathSegment("9007199254740992") }, new PathSegment[] { new IndexPathSegment("9007199254740993") }];
        yield return [new PathSegment[] { new AnyItemPathSegment() }, new PathSegment[] { new FieldPathSegment("*") }];
        yield return [new PathSegment[] { new FieldPathSegment("x", null) }, new PathSegment[] { new FieldPathSegment("x", "") }];
        yield return [new PathSegment[] { new FieldPathSegment("a/b~c") }, new PathSegment[] { new FieldPathSegment("a"), new FieldPathSegment("b~c") }];
    }

    [Theory]
    [MemberData(nameof(DistinctPaths))]
    public void TypedPathsRemainExactAnchorsEvenWhenLocalFieldIdsAreIdentical(PathSegment[] leftPath, PathSegment[] rightPath)
    {
        var left = Direct();
        var right = Direct(2, "L");
        left = left.WithDocument(left.Document with { Fields = R(left.Document.Fields[0] with { Path = R(leftPath) }, left.Document.Fields[1], left.Document.Fields[2]) });
        right = right.WithDocument(right.Document with { Fields = R(right.Document.Fields[0] with { Path = R(rightPath) }, right.Document.Fields[1], right.Document.Fields[2]) });
        var result = Compare(left, right);
        Assert.Equal(ComparisonMatchV1.OnlyLeft, Item(result, ComparisonEntityV1.Field, "L.a").Match);
        Assert.Contains(result.Items, item => item.Entity == ComparisonEntityV1.Field && item.Match == ComparisonMatchV1.OnlyRight &&
            item.Right.Any(candidate => NodeId(candidate.Node) == "L.a"));
        Assert.Contains(Item(result, ComparisonEntityV1.Rule, "L.rule").Facets,
            facet => facet.Aspect == ComparisonAspectV1.Value && facet.HasUncertainty && facet.Reasons.Contains(ComparisonReasonV1.UnresolvedReference));
    }

    public static IEnumerable<object[]> DistinctLiterals()
    {
        yield return [new ExactNumberLiteralValue(ExactNumberFlavor.Integer, "9007199254740992"), new ExactNumberLiteralValue(ExactNumberFlavor.Integer, "9007199254740993")];
        yield return [new ExactNumberLiteralValue(ExactNumberFlavor.Decimal, "1.0"), new ExactNumberLiteralValue(ExactNumberFlavor.Decimal, "1.00")];
        yield return [new ExactNumberLiteralValue(ExactNumberFlavor.Decimal, "-0.00e+2"), new ExactNumberLiteralValue(ExactNumberFlavor.Decimal, "0")];
        yield return [new ExactNumberLiteralValue(ExactNumberFlavor.BinaryFloat, "8000000000000000"), new ExactNumberLiteralValue(ExactNumberFlavor.BinaryFloat, "0000000000000000")];
        yield return [new ExactNumberLiteralValue(ExactNumberFlavor.BinaryFloat, "7ff8000000000001"), new ExactNumberLiteralValue(ExactNumberFlavor.BinaryFloat, "7ff8000000000002")];
        yield return [new ExactNumberLiteralValue(ExactNumberFlavor.Integer, "1"), new ExactNumberLiteralValue(ExactNumberFlavor.Decimal, "1")];
        yield return [new NullLiteralValue(NullLiteralKind.Absent), new NullLiteralValue(NullLiteralKind.ExplicitNull)];
        yield return [new NullLiteralValue(NullLiteralKind.ExplicitNull), new NullLiteralValue(NullLiteralKind.XmlNil)];
        yield return [new StringLiteralValue("true"), new BooleanLiteralValue(true)];
        yield return [new StringLiteralValue("a\0b"), new StringLiteralValue("ab")];
    }

    [Theory]
    [MemberData(nameof(DistinctLiterals))]
    public void LiteralsUseExactLexicalTypeNullKindAndFloatBits(LiteralValue leftValue, LiteralValue rightValue)
    {
        var left = RuleWith(Direct(), R<RuleDependency>(new ConstantDependency(leftValue)));
        var right = RuleWith(Direct(2, "R"), R<RuleDependency>(new ConstantDependency(rightValue)));
        var result = Compare(left, right);
        var value = Facet(Item(result, ComparisonEntityV1.Rule, "L.rule"), ComparisonAspectV1.Value);
        Assert.True(value.HasObservedDifference);
        Assert.False(value.HasUncertainty);
        Assert.False(result.HasUncertainty);
    }

    [Fact]
    public void DependencyOrderAndRepeatedValuesRemainFacts()
    {
        var a = new ConstantDependency(new StringLiteralValue("a"));
        var b = new ConstantDependency(new StringLiteralValue("b"));
        var left = RuleWith(Direct(), R<RuleDependency>(a, b, a));
        var reordered = RuleWith(Direct(2, "R"), R<RuleDependency>(a, a, b));
        var deduplicated = RuleWith(Direct(2, "R"), R<RuleDependency>(a, b));
        Assert.True(Facet(Item(Compare(left, reordered), ComparisonEntityV1.Rule, "L.rule"), ComparisonAspectV1.Value).HasObservedDifference);
        Assert.True(Facet(Item(Compare(left, deduplicated), ComparisonEntityV1.Rule, "L.rule"), ComparisonAspectV1.Value).HasObservedDifference);
    }

    [Fact]
    public void DetailArgumentOrderAndNestedGroupLabelsStaySeparateFacts()
    {
        var a = new LiteralDetail(new StringLiteralValue("a"));
        var b = new LiteralDetail(new StringLiteralValue("b"));
        var before = new GroupDetail("before", R<DetailNode>(new ExpressionDetail("opaque.op", R<DetailNode>(a, b))));
        var after = new GroupDetail("after", R<DetailNode>(new ExpressionDetail("opaque.op", R<DetailNode>(b, a))));
        var left = RuleWith(Direct(), details: R<DetailNode>(before));
        var right = RuleWith(Direct(2, "R"), details: R<DetailNode>(after));
        var rule = Item(Compare(left, right), ComparisonEntityV1.Rule, "L.rule");
        Assert.True(Facet(rule, ComparisonAspectV1.Details).HasObservedDifference);
        Assert.True(Facet(rule, ComparisonAspectV1.Presentation).HasObservedDifference);
        Assert.False(Facet(rule, ComparisonAspectV1.Details).HasUncertainty);
    }

    [Fact]
    public void UnsupportedDetailsDoNotEraseAKnownLiteralDifferenceInTheSameFacet()
    {
        var left = RuleWith(Direct(), details: R<DetailNode>(new LiteralDetail(new StringLiteralValue("before")),
            new UnsupportedDetail("native.extension", "Opaque", Origin("unsupported-left"))));
        var right = RuleWith(Direct(2, "R"), details: R<DetailNode>(new LiteralDetail(new StringLiteralValue("after")),
            new UnsupportedDetail("native.extension", "Opaque", Origin("unsupported-right"))));
        var facet = Facet(Item(Compare(left, right), ComparisonEntityV1.Rule, "L.rule"), ComparisonAspectV1.Details);
        Assert.True(facet.HasObservedDifference);
        Assert.True(facet.HasUncertainty);
        Assert.Contains(ComparisonReasonV1.UnsupportedDetail, facet.Reasons);
    }

    [Theory]
    [InlineData("backend")]
    [InlineData("revision")]
    [InlineData("build")]
    [InlineData("adapter")]
    [InlineData("algorithm")]
    [InlineData("codec")]
    [InlineData("policy")]
    public void IncompatibleOperationProfilePreservesNeutralFactsInsideAndOutsideTheExpression(string component)
    {
        var profile = component switch
        {
            "backend" => new Weft.Domain.RevisionReportBackendProfile("another.fixture", Profile.BackendRevision, Profile.BackendBuild, Profile.AdapterBuild),
            "revision" => new(Profile.BackendId, "engine.2", Profile.BackendBuild, Profile.AdapterBuild),
            "build" => new(Profile.BackendId, Profile.BackendRevision, "build.2", Profile.AdapterBuild),
            "adapter" => new(Profile.BackendId, Profile.BackendRevision, Profile.BackendBuild, "adapter.2"),
            _ => Profile
        };
        var left = RuleWith(Direct(), details: R<DetailNode>(new ExpressionDetail("op.left", R<DetailNode>(new LiteralDetail(new StringLiteralValue("before"))))));
        var right = RuleWith(Direct(2, "R", profile, component == "algorithm" ? "fixture.project.2" : "fixture.project.1",
            component == "codec" ? "fixture.codec.2" : "fixture.codec.1",
            component == "policy" ? new Weft.Domain.RevisionReportPolicy(responseByteLimit: 16777215) : null),
            details: R<DetailNode>(new ExpressionDetail("op.right", R<DetailNode>(new LiteralDetail(new StringLiteralValue("after"))))));
        var dependencies = right.Document.Rules[0].Dependencies.ToArray();
        dependencies[0] = new FieldDependency("R.b");
        right = RuleWith(right, dependencies);
        var result = Compare(left, right);
        var rule = Item(result, ComparisonEntityV1.Rule, "L.rule");
        var details = Facet(rule, ComparisonAspectV1.Details);
        Assert.True(details.HasObservedDifference);
        Assert.True(details.HasUncertainty);
        Assert.Contains(ComparisonReasonV1.IncompatibleProfile, details.Reasons);
        Assert.True(Facet(rule, ComparisonAspectV1.Value).HasObservedDifference);
        Assert.False(Facet(rule, ComparisonAspectV1.Value).HasUncertainty);
    }

    [Fact]
    public void ProfileDifferencesAloneDoNotMakeNeutralLiteralDetailsUncertain()
    {
        var result = Compare(Direct(), Direct(2, "R", new(Profile.BackendId, "engine.2", "build.2", "adapter.2")));
        Assert.False(result.HasObservedDifferences);
        Assert.False(result.HasUncertainty);
    }

    [Fact]
    public void UnknownFieldFactsAffectOnlyTheirFacetsWhileKnownRequirementDifferenceSurvives()
    {
        var left = Direct();
        var right = Direct(2, "R");
        left = left.WithDocument(left.Document with
        {
            Fields = R(left.Document.Fields[0] with
            { DataType = new(FieldDataTypeKind.Unknown, R<ScalarDataType>()), Cardinality = new(CardinalityKind.Unknown), Nullability = FieldNullability.Unknown },
            left.Document.Fields[1], left.Document.Fields[2])
        });
        right = right.WithDocument(right.Document with
        {
            Fields = R(right.Document.Fields[0] with { Requirement = FieldRequirement.Optional },
            right.Document.Fields[1], right.Document.Fields[2])
        });
        var field = Item(Compare(left, right), ComparisonEntityV1.Field, "L.a");
        foreach (var aspect in new[] { ComparisonAspectV1.DataType, ComparisonAspectV1.Cardinality, ComparisonAspectV1.Nullability })
        {
            Assert.True(Facet(field, aspect).HasUncertainty);
            Assert.Contains(ComparisonReasonV1.UnknownFact, Facet(field, aspect).Reasons);
        }
        Assert.True(Facet(field, ComparisonAspectV1.Requirement).HasObservedDifference);
        Assert.False(Facet(field, ComparisonAspectV1.Requirement).HasUncertainty);
        Assert.False(Facet(field, ComparisonAspectV1.Presentation).HasUncertainty);
    }

    [Fact]
    public void DeclaredRelationshipIsPairedByKindAndMappedEndpointsInsteadOfId()
    {
        var left = Direct();
        var right = Direct(2, "R");
        left = left.WithDocument(left.Document with
        {
            Relationships = R(left.Document.Relationships.Concat([
            new ProjectionRelationship("left.declared", RelationshipKind.Produces, new ParameterNodeReference("L.p"), new FieldNodeReference("L.o"))]).ToArray())
        }, false);
        right = right.WithDocument(right.Document with
        {
            Relationships = R(right.Document.Relationships.Concat([
            new ProjectionRelationship("right.unrelated-id", RelationshipKind.Produces, new ParameterNodeReference("R.p"), new FieldNodeReference("R.o"))]).ToArray())
        }, false);
        var result = Compare(left, right);
        var edge = Assert.Single(result.Items, item => item.Left.Any(candidate => candidate.RelationshipId == "left.declared"));
        Assert.Equal(ComparisonMatchV1.Matched, edge.Match);
        Assert.Equal("right.unrelated-id", Assert.Single(edge.Right).RelationshipId);
        Assert.Null(Assert.Single(edge.Right).Node);
        Assert.False(result.HasObservedDifferences);
        Assert.False(result.HasUncertainty);
    }

    [Fact]
    public void BoundaryFormatAndShapeAreVisibleKnownFacts()
    {
        var left = Direct();
        var right = Direct(2, "R");
        right = right.WithDocument(right.Document with
        {
            Boundaries = R(right.Document.Boundaries[0] with
            { Format = "another-record-format", Shape = BoundaryShape.Rows }, right.Document.Boundaries[1])
        });
        var result = Compare(left, right);
        var boundary = Item(result, ComparisonEntityV1.Boundary, "L.in");
        Assert.Equal(ComparisonMatchV1.Matched, boundary.Match);
        Assert.True(Facet(boundary, ComparisonAspectV1.Shape).HasObservedDifference);
        Assert.False(Facet(boundary, ComparisonAspectV1.Shape).HasUncertainty);
        Assert.True(result.HasObservedDifferences);
    }

    [Fact]
    public void ExactLargeBoundedCardinalityIsNotRounded()
    {
        var left = Direct();
        var right = Direct(2, "R");
        left = left.WithDocument(left.Document with
        {
            Fields = R(left.Document.Fields[0] with
            { Cardinality = new(CardinalityKind.Bounded, "9007199254740992", "9007199254740994") }, left.Document.Fields[1], left.Document.Fields[2])
        });
        right = right.WithDocument(right.Document with
        {
            Fields = R(right.Document.Fields[0] with
            { Cardinality = new(CardinalityKind.Bounded, "9007199254740993", "9007199254740994") }, right.Document.Fields[1], right.Document.Fields[2])
        });
        var facet = Facet(Item(Compare(left, right), ComparisonEntityV1.Field, "L.a"), ComparisonAspectV1.Cardinality);
        Assert.True(facet.HasObservedDifference);
        Assert.False(facet.HasUncertainty);
    }

    [Fact]
    public void ParameterDefaultsAndNativeTypeMetadataRemainTypedFacts()
    {
        var left = Direct();
        var right = Direct(2, "R");
        right = right.WithDocument(right.Document with
        {
            Parameters = R(right.Document.Parameters[0] with
            { DefaultValue = new NullLiteralValue(NullLiteralKind.ExplicitNull), DataType = TextType with { NativeName = "native-text-v2" } })
        });
        var parameter = Item(Compare(left, right), ComparisonEntityV1.Parameter, "L.p");
        Assert.True(Facet(parameter, ComparisonAspectV1.Default).HasObservedDifference);
        Assert.True(Facet(parameter, ComparisonAspectV1.DataType).HasObservedDifference);
        Assert.False(Facet(parameter, ComparisonAspectV1.Default).HasUncertainty);
    }

    [Fact]
    public void FieldDisplayNameAndRuleSummaryChangesArePresentation()
    {
        var left = Direct();
        var right = Direct(2, "R");
        right = right.WithDocument(right.Document with
        {
            Fields = R(right.Document.Fields[0] with { Name = "Renamed display label" }, right.Document.Fields[1], right.Document.Fields[2]),
            Rules = R(right.Document.Rules[0] with { Summary = "Another explanation" })
        });
        var result = Compare(left, right);
        Assert.True(Facet(Item(result, ComparisonEntityV1.Field, "L.a"), ComparisonAspectV1.Presentation).HasObservedDifference);
        var rule = Item(result, ComparisonEntityV1.Rule, "L.rule");
        Assert.True(Facet(rule, ComparisonAspectV1.Presentation).HasObservedDifference);
        Assert.False(Facet(rule, ComparisonAspectV1.Value).HasObservedDifference);
        Assert.False(result.HasUncertainty);
    }

    [Fact]
    public void ParameterNameAnchoringIsOrdinalAndDoesNotInferRenameFromEqualLocalIds()
    {
        var left = Direct();
        var right = Direct(2, "L");
        right = right.WithDocument(right.Document with { Parameters = R(right.Document.Parameters[0] with { Name = "fallback" }) });
        var result = Compare(left, right);
        Assert.Equal(ComparisonMatchV1.OnlyLeft, Item(result, ComparisonEntityV1.Parameter, "L.p").Match);
        Assert.Contains(result.Items, item => item.Entity == ComparisonEntityV1.Parameter && item.Match == ComparisonMatchV1.OnlyRight &&
            item.Right.Any(candidate => NodeId(candidate.Node) == "L.p"));
        Assert.True(result.HasObservedDifferences);
        Assert.False(result.HasUncertainty);
    }
}
