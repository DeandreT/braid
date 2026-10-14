using System.Collections;
using Weft.Application;
using Weft.Contracts.Operations;
using Weft.Contracts.Projections;
using Weft.Domain;
using Weft.MappingBackends;

namespace Weft.Tests;

/// <summary>Authored provider evidence only; these normalization proofs execute no worker, storage or database.</summary>
public sealed class FerruleReportProjectionLinkTests
{
    private static readonly JsonPointerNativeLocator FieldOrigin = new(InspectionPolicy.DocumentId, "/source/kind/children/2");
    private static readonly JsonPointerNativeLocator ChildMetadata = new(InspectionPolicy.DocumentId, "/source/kind/children/2/kind/dynamic");
    private static readonly FieldNodeReference Field = new("field");

    [Fact]
    public void ChildSchemaMetadataKeepsAllNativeEvidenceAndPartialFactsWithoutInventingAnExactFieldOrigin()
    {
        var artifact = FerruleReportTestData.Artifact();
        var key = FerruleReportTestData.ProjectionKey(artifact);
        var document = Document(key, ChildMetadata, Field);
        var result = Result(artifact, document);
        var example = FerruleReportNormalizer.ProjectionForExample(result, artifact);
        Assert.Same(document, example.Document);
        Assert.Equal(Field, Assert.Single(example.Document!.Coverage.Gaps).ProjectionLocator);
        Assert.Equal(Field, Assert.Single(example.Document.Diagnostics).ProjectionLocator);

        var strict = FerruleReportNormalizer.Projection(result, artifact, new());
        var normalized = strict.Document!;
        Assert.Null(Assert.Single(normalized.Coverage.Gaps).ProjectionLocator);
        Assert.Null(Assert.Single(normalized.Diagnostics).ProjectionLocator);
        AssertPreserved(document, normalized);
        Assert.Equal(example.Status, strict.Status);
        Assert.Equal(example.Provenance, strict.Provenance);
        Assert.Equal(example.Diagnostics.ToArray(), strict.Diagnostics.ToArray());
        Assert.Equal(MappingOperationStatus.Succeeded, result.Status);
        Assert.True(strict.Provenance!.Confirmed);
        var observation = RevisionReportProjectionValidation.Validate(key, strict, key.Policy);
        var backend = RevisionReportBackendResult.ForProjection(key, strict, observation);
        Assert.True(backend.IsUsable);
        Assert.Equal(RevisionReportCoverage.Partial, backend.Observation!.Projection!.Coverage);
        Assert.Equal(11, backend.Observation.Projection.DocumentItemCount);
        Assert.Equal(1, backend.Observation.Projection.CoverageGapCount);
        Assert.Equal(1, backend.Observation.Projection.DocumentDiagnosticCount);
        new FerruleRevisionReportFormatValidator().Validate(key, backend.ProjectionReport!);
        // Normalization never rewrites the caller's native evidence or the example document.
        Assert.Equal(ChildMetadata, Assert.Single(document.Coverage.Gaps).NativeLocator);
        Assert.Equal(Field, Assert.Single(document.Coverage.Gaps).ProjectionLocator);
    }

    [Theory]
    [InlineData("exact", true)]
    [InlineData("shared", false)]
    [InlineData("wrongOwner", false)]
    [InlineData("document", false)]
    [InlineData("emptyPointer", false)]
    public void OnlyTheExistingExactUniqueOwnerKeepsItsNavigationLink(string evidence, bool retained)
    {
        var artifact = FerruleReportTestData.Artifact();
        var key = FerruleReportTestData.ProjectionKey(artifact);
        NativeLocator native = evidence switch
        {
            "document" => new DocumentNativeLocator(InspectionPolicy.DocumentId),
            "emptyPointer" => new JsonPointerNativeLocator(InspectionPolicy.DocumentId, ""),
            _ => FieldOrigin
        };
        var document = Document(key, native, Field);
        if (evidence is "shared" or "wrongOwner")
        {
            var second = FieldDefinition("other", evidence == "shared" ? FieldOrigin : new JsonPointerNativeLocator(InspectionPolicy.DocumentId, "/other"));
            document = document with
            {
                Fields = [document.Fields[0], second],
                Relationships = [document.Relationships[0], Membership("member-other", second.Id)]
            };
            if (evidence == "wrongOwner") document = document with
            {
                Coverage = document.Coverage with { Gaps = [document.Coverage.Gaps[0] with { ProjectionLocator = new FieldNodeReference(second.Id) }] },
                Diagnostics = [document.Diagnostics[0] with { ProjectionLocator = new FieldNodeReference(second.Id) }]
            };
        }
        // Provider-valid structural links are checked before the stricter exact-origin filter.
        FerruleInspectionBackend.ValidateStoredProjectionDocument(document, new());
        var normalized = FerruleReportNormalizer.Projection(Result(artifact, document), artifact, new());
        var expected = retained ? Field : null;
        Assert.Equal(expected, Assert.Single(normalized.Document!.Coverage.Gaps).ProjectionLocator);
        Assert.Equal(expected, Assert.Single(normalized.Document.Diagnostics).ProjectionLocator);
        AssertPreserved(document, normalized.Document);
        var observation = RevisionReportProjectionValidation.Validate(key, normalized, key.Policy);
        Assert.True(RevisionReportBackendResult.ForProjection(key, normalized, observation).IsUsable);
    }

    [Fact]
    public void DiagnosticWithoutNativeEvidenceLosesOnlyItsNavigationWhileAnExactGapRemainsLinked()
    {
        var artifact = FerruleReportTestData.Artifact();
        var key = FerruleReportTestData.ProjectionKey(artifact);
        var document = Document(key, FieldOrigin, Field);
        document = document with { Diagnostics = [document.Diagnostics[0] with { NativeLocator = null }] };
        var normalized = FerruleReportNormalizer.Projection(Result(artifact, document), artifact, new()).Document!;
        Assert.Equal(Field, Assert.Single(normalized.Coverage.Gaps).ProjectionLocator);
        Assert.Null(Assert.Single(normalized.Diagnostics).ProjectionLocator);
        Assert.Null(Assert.Single(normalized.Diagnostics).NativeLocator);
        AssertPreserved(document, normalized);
    }

    [Theory]
    [InlineData("danglingGap")]
    [InlineData("danglingDiagnostic")]
    [InlineData("wrongVariant")]
    [InlineData("unknownVariant")]
    [InlineData("malformedNative")]
    [InlineData("graphWithoutOrigin")]
    public void ProviderInvalidReferencesAndLocatorsCannotBeAcceptedByDroppingTheirNavigation(string corruption)
    {
        var artifact = FerruleReportTestData.Artifact();
        var key = FerruleReportTestData.ProjectionKey(artifact);
        var document = Document(key, ChildMetadata, Field);
        if (corruption == "danglingDiagnostic") document = document with
        { Diagnostics = [document.Diagnostics[0] with { ProjectionLocator = new FieldNodeReference("missing") }] };
        else if (corruption is "danglingGap" or "wrongVariant" or "unknownVariant")
        {
            ProjectionNodeReference reference = corruption switch
            {
                "danglingGap" => new FieldNodeReference("missing"),
                "wrongVariant" => new ParameterNodeReference(Field.FieldId),
                _ => new UnknownReference()
            };
            document = document with { Coverage = document.Coverage with { Gaps = [document.Coverage.Gaps[0] with { ProjectionLocator = reference }] } };
        }
        else
        {
            NativeLocator native = corruption == "malformedNative" ? new JsonPointerNativeLocator(InspectionPolicy.DocumentId, "/invalid~2escape") :
                new GraphNodeNativeLocator(InspectionPolicy.DocumentId, "1");
            document = document with { Coverage = document.Coverage with { Gaps = [document.Coverage.Gaps[0] with { NativeLocator = native }] } };
        }
        Refuses(RevisionReportValidationFailure.Integrity, () => FerruleReportNormalizer.Projection(Result(artifact, document), artifact, new()));
    }

    [Fact]
    public void GapAndDiagnosticCopiesOwnTheirCollectionsAndBackendThenOwnsTheWholeGraph()
    {
        var artifact = FerruleReportTestData.Artifact();
        var key = FerruleReportTestData.ProjectionKey(artifact);
        var document = Document(key, ChildMetadata, Field);
        CoverageGap[] gaps = [document.Coverage.Gaps[0]];
        ProjectionDiagnostic[] diagnostics = [document.Diagnostics[0]];
        ProjectionField[] fields = [document.Fields[0]];
        document = document with { Coverage = document.Coverage with { Gaps = gaps }, Diagnostics = diagnostics, Fields = fields };
        var normalized = FerruleReportNormalizer.Projection(Result(artifact, document), artifact, new());
        var backend = RevisionReportBackendResult.ForProjection(key, normalized,
            RevisionReportProjectionValidation.Validate(key, normalized, key.Policy));
        gaps[0] = gaps[0] with { Code = "changed", Message = "changed", NativeLocator = FieldOrigin, ProjectionLocator = Field };
        diagnostics[0] = diagnostics[0] with { Code = "changed", Message = "changed", ProjectionLocator = Field };
        fields[0] = fields[0] with { Id = "changed", NativeLocator = new DocumentNativeLocator(InspectionPolicy.DocumentId) };
        var captured = backend.ProjectionReport!.Document!;
        Assert.Equal("schema.dynamicProperties", Assert.Single(normalized.Document!.Coverage.Gaps).Code);
        Assert.Equal("schema.dynamicProperties", Assert.Single(normalized.Document.Diagnostics).Code);
        Assert.Equal(ChildMetadata, Assert.Single(captured.Coverage.Gaps).NativeLocator);
        Assert.Equal(FieldOrigin, Assert.Single(captured.Fields).NativeLocator);
        Assert.Equal(Field.FieldId, Assert.Single(captured.Fields).Id);
        Assert.Null(Assert.Single(captured.Coverage.Gaps).ProjectionLocator);
        Assert.Throws<NotSupportedException>(() => ((IList<CoverageGap>)normalized.Document.Coverage.Gaps).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<ProjectionDiagnostic>)normalized.Document.Diagnostics).Clear());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangingLinkCollectionCountIsRefusedBeforeAnyUnboundedOrSanitizingTraversal(bool duringRead)
    {
        var artifact = FerruleReportTestData.Artifact();
        var key = FerruleReportTestData.ProjectionKey(artifact);
        var document = Document(key, ChildMetadata, Field);
        var gaps = new ChangingList<CoverageGap>(document.Coverage.Gaps[0], duringRead);
        document = document with { Coverage = document.Coverage with { Gaps = gaps } };
        Refuses(RevisionReportValidationFailure.Integrity, () => FerruleReportNormalizer.Projection(Result(artifact, document), artifact, new()));
        Assert.Equal(duringRead ? 1 : 0, gaps.IndexedReads);
    }

    [Fact]
    public void CancellationBeforeAndDuringLinkCaptureNeverReturnsANormalizedReport()
    {
        var artifact = FerruleReportTestData.Artifact();
        var key = FerruleReportTestData.ProjectionKey(artifact);
        var document = Document(key, ChildMetadata, Field);
        using var before = new CancellationTokenSource();
        before.Cancel();
        Assert.Throws<OperationCanceledException>(() => FerruleReportNormalizer.Projection(Result(artifact, document), artifact, new(), before.Token));
        using var during = new CancellationTokenSource();
        var gaps = new CancelOnRead<CoverageGap>(document.Coverage.Gaps[0], during);
        document = document with { Coverage = document.Coverage with { Gaps = gaps } };
        Assert.Throws<OperationCanceledException>(() => FerruleReportNormalizer.Projection(Result(artifact, document), artifact, new(), during.Token));
        Assert.Equal(1, gaps.IndexedReads);
    }

    [Fact]
    public void CapturedEvidenceRetainsEffectiveDiagnosticTextAndAggregateLimits()
    {
        var artifact = FerruleReportTestData.Artifact();
        var key = FerruleReportTestData.ProjectionKey(artifact);
        var document = Document(key, ChildMetadata, Field);
        var result = Result(artifact, document);
        FerruleReportNormalizer.Projection(result, artifact, new(itemLimit: 19));
        Refuses(RevisionReportValidationFailure.ResourceLimit, () => FerruleReportNormalizer.Projection(result, artifact, new(itemLimit: 18)));
        var doubled = document with { Coverage = document.Coverage with { Gaps = [document.Coverage.Gaps[0], document.Coverage.Gaps[0]] } };
        FerruleReportNormalizer.Projection(Result(artifact, doubled), artifact, new(diagnosticLimit: 2));
        Refuses(RevisionReportValidationFailure.ResourceLimit, () => FerruleReportNormalizer.Projection(Result(artifact, doubled), artifact, new(diagnosticLimit: 1)));
        var longEvidence = document with { Coverage = document.Coverage with { Gaps = [document.Coverage.Gaps[0] with { Message = new string('x', 129) }] } };
        Refuses(RevisionReportValidationFailure.ResourceLimit, () => FerruleReportNormalizer.Projection(Result(artifact, longEvidence), artifact, new(textByteLimit: 128)));
    }

    private static ProjectionDocumentV1 Document(ProjectionCacheKey key, NativeLocator native, ProjectionNodeReference reference) =>
        FerruleReportTestData.Document(key) with
        {
            Coverage = new(CoverageStatus.Partial, [new("schema.dynamicProperties", "Native dynamic schema evidence", native, reference)]),
            Fields = [FieldDefinition(Field.FieldId, FieldOrigin)],
            Relationships = [Membership("member", Field.FieldId)],
            Diagnostics = [new("schema.dynamicProperties", DiagnosticSeverity.Warning, "Native dynamic schema evidence", native, reference)]
        };

    private static ProjectionField FieldDefinition(string id, NativeLocator native) => new(id, "primary-input", [], id,
        new(FieldDataTypeKind.Scalar, [ScalarDataType.String]), new(CardinalityKind.ExactlyOne), FieldRequirement.Required,
        FieldNullability.NonNullable, native);

    private static ProjectionRelationship Membership(string id, string fieldId) =>
        new(id, RelationshipKind.BoundaryMember, new FieldNodeReference(fieldId), new BoundaryNodeReference("primary-input"));

    private static MappingProjectionResult Result(NativeArtifact artifact, ProjectionDocumentV1 document) =>
        new(MappingOperationStatus.Succeeded, FerruleReportTestData.Provenance(artifact), document, []);

    private static void AssertPreserved(ProjectionDocumentV1 original, ProjectionDocumentV1 normalized)
    {
        Assert.Equal(original.Provenance, normalized.Provenance);
        Assert.Equal(original.SchemaVersion, normalized.SchemaVersion);
        Assert.Equal(original.Coverage.Status, normalized.Coverage.Status);
        Assert.Equal(original.Coverage.Gaps.Count, normalized.Coverage.Gaps.Count);
        for (var index = 0; index < original.Coverage.Gaps.Count; index++)
            Assert.Equal(original.Coverage.Gaps[index] with { ProjectionLocator = null }, normalized.Coverage.Gaps[index] with { ProjectionLocator = null });
        Assert.Equal(original.Diagnostics.Count, normalized.Diagnostics.Count);
        for (var index = 0; index < original.Diagnostics.Count; index++)
            Assert.Equal(original.Diagnostics[index] with { ProjectionLocator = null }, normalized.Diagnostics[index] with { ProjectionLocator = null });
        Assert.Same(original.Boundaries, normalized.Boundaries);
        Assert.Same(original.Fields, normalized.Fields);
        Assert.Same(original.Parameters, normalized.Parameters);
        Assert.Same(original.Rules, normalized.Rules);
        Assert.Same(original.Controls, normalized.Controls);
        Assert.Same(original.Relationships, normalized.Relationships);
        Assert.Same(original.Capabilities, normalized.Capabilities);
    }

    private static void Refuses(RevisionReportValidationFailure failure, Action action) =>
        Assert.Equal(failure, Assert.Throws<RevisionReportValidationException>(action).Failure);

    private sealed record UnknownReference : ProjectionNodeReference;

    private sealed class ChangingList<T>(T item, bool duringRead) : IReadOnlyList<T>
    {
        private int countReads;
        private bool changed;
        internal int IndexedReads { get; private set; }
        public int Count
        {
            get
            {
                if (++countReads == 2 && !duringRead) changed = true;
                return changed ? 2 : 1;
            }
        }
        public T this[int index]
        {
            get
            {
                if (index != 0) throw new InvalidOperationException("Traversal exceeded its captured bound.");
                IndexedReads++;
                if (duringRead) changed = true;
                return item;
            }
        }
        public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException("Use bounded indexed capture.");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class CancelOnRead<T>(T item, CancellationTokenSource cancellation) : IReadOnlyList<T>
    {
        internal int IndexedReads { get; private set; }
        public int Count => 1;
        public T this[int index]
        {
            get
            {
                if (index != 0) throw new ArgumentOutOfRangeException(nameof(index));
                IndexedReads++;
                cancellation.Cancel();
                return item;
            }
        }
        public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException("Use bounded indexed capture.");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
