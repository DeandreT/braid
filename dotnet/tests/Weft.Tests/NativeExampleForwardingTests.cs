using System.Collections;
using Weft.Api;
using Weft.Application;
using Weft.Contracts.Examples;
using Weft.Contracts.Operations;
using Weft.Contracts.Projections;
using Weft.Contracts.Validation;
using Weft.Domain;
using Weft.MappingBackends;

namespace Weft.Tests;

/// <summary>Authored backend outcomes exercise only the example forwarding boundary.</summary>
public sealed class NativeExampleForwardingTests
{
    private static readonly NativeExampleManifest Manifest = new();
    private static readonly NativeLocator Native = new GraphNodeNativeLocator("entry.native.json", "4294967295", "18446744073709551615");
    private static readonly FieldDataType TextType = new(FieldDataTypeKind.Scalar, [ScalarDataType.String]);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CompletedCleanupFailureOutranksCancellationAndKeepsActualNativeEvidence(bool duringProjection)
    {
        using var cancellation = new CancellationTokenSource();
        var backend = new Backend();
        if (duringProjection)
        {
            backend.Projection = (artifact, token) =>
            {
                Assert.Equal(cancellation.Token, token);
                cancellation.Cancel();
                return Task.FromResult(new MappingProjectionResult(MappingOperationStatus.IsolationFailure, Provenance(artifact), null,
                    [Diagnostic("mapping.cleanupFailed", "The worker did not confirm isolation cleanup.")]));
            };
        }
        else
        {
            backend.Validation = (artifact, token) =>
            {
                Assert.Equal(cancellation.Token, token);
                cancellation.Cancel();
                return Task.FromResult(new MappingValidationResult(MappingOperationStatus.IsolationFailure, Provenance(artifact), null, 0, 0, false,
                    [Diagnostic("mapping.cleanupFailed", "The worker did not confirm isolation cleanup.")]));
            };
        }

        var result = Assert.IsType<NativeExampleResultV1>(await new NativeExampleService(Manifest, backend, backend).GetAsync("basic", cancellation.Token));

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(OperationStatusV1.IsolationFailure, duringProjection ? result.Projection.Status : result.Validation.Status);
        var diagnostic = duringProjection ? Assert.Single(result.Projection.Diagnostics) : ToOperation(Assert.Single(result.Validation.Diagnostics));
        Assert.Equal("mapping.cleanupFailed", diagnostic.Code);
        Assert.Equal("The worker did not confirm isolation cleanup.", diagnostic.Message);
        Assert.Equal(Native, diagnostic.NativeLocator);
        var evidence = duringProjection ? result.Projection.Provenance : result.Validation.Provenance;
        Assert.NotNull(evidence);
        Assert.Equal(result.Example.Revision, evidence.Revision);
        Assert.Equal(result.Example.Digest, evidence.NativeArtifactDigest);
        Assert.Equal(new BackendIdentity("fixture.backend", "fixture.engine", "fixture.build"), evidence.Backend);
        Assert.Equal(1, backend.ProjectionCalls);
        Assert.Equal(duringProjection ? 0 : 1, backend.ValidationCalls);
        if (duringProjection) Assert.Equal(OperationStatusV1.Canceled, result.Validation.Status);
        else Assert.Equal(OperationStatusV1.Succeeded, result.Projection.Status);
    }

    [Fact]
    public async Task ExplicitExampleContextPreservesLegacyTextAndExactTypedOwnerWhileStandardReportsStayBounded()
    {
        var message = new string('x', 4096);
        var location = new string('y', 4096);
        var backend = new Backend
        {
            Projection = (artifact, _) => Task.FromResult(new MappingProjectionResult(MappingOperationStatus.ResourceLimit, Provenance(artifact), null,
                [Diagnostic("example.actual", message)])),
            Validation = (artifact, _) => Task.FromResult(new MappingValidationResult(MappingOperationStatus.InvalidMapping, Provenance(artifact), false, 1, 1, false,
                [Diagnostic("example.issue", message) with
                {
                    Owner = new SchemaNodeDiagnosticOwner(new NamedSourceMappingEndpoint("9007199254740993", "a/b~c"),
                        [new ChildSchemaOwnerStep("9007199254740994"), new DynamicFieldSchemaOwnerStep()]),
                    Location = location, LocationTruncated = true, MessageTruncated = true
                }]))
        };

        var result = Assert.IsType<NativeExampleResultV1>(await new NativeExampleService(Manifest, backend, backend).GetAsync("exact"));

        Assert.Equal(OperationStatusV1.ResourceLimit, result.Projection.Status);
        Assert.Equal(message, Assert.Single(result.Projection.Diagnostics).Message);
        Assert.Equal(OperationStatusV1.InvalidMapping, result.Validation.Status);
        var issue = Assert.Single(result.Validation.Diagnostics);
        Assert.Equal(message, issue.Message);
        Assert.Equal(location, issue.Location);
        Assert.True(issue.MessageTruncated);
        Assert.True(issue.LocationTruncated);
        var owner = Assert.IsType<SchemaNodeNativeOwnerV1>(issue.Owner);
        Assert.Equal(new NamedSourceNativeEndpointV1("9007199254740993", "a/b~c"), owner.Endpoint);
        Assert.Equal(new ChildSchemaNativeOwnerStepV1("9007199254740994"), owner.Path[0]);
        Assert.IsType<DynamicFieldSchemaNativeOwnerStepV1>(owner.Path[1]);
        Assert.Null(issue.ProjectionLocator);
        Assert.True(Manifest.TryGet("exact", out _, out var artifact));
        Assert.NotNull(artifact);
        var actualProjection = await backend.Projection(artifact, default);
        Assert.Equal(RevisionReportValidationFailure.ResourceLimit,
            Assert.Throws<RevisionReportValidationException>(() => FerruleReportNormalizer.Projection(actualProjection, artifact, new RevisionReportPolicy())).Failure);
    }

    [Theory]
    [InlineData("trueWithIssues")]
    [InlineData("falseWithoutIssues")]
    [InlineData("untruncatedMissingIssues")]
    [InlineData("nullWithIssues")]
    public async Task LegacyExamplesRetainStrongerValidityAndEmissionChecksWithSafeConfirmedFailureEvidence(string contradiction)
    {
        var backend = new Backend
        {
            Validation = (artifact, _) => Task.FromResult(contradiction switch
            {
                "trueWithIssues" => new MappingValidationResult(MappingOperationStatus.Succeeded, Provenance(artifact), true, 1, 1, false, [Diagnostic("issue", "Finding")]),
                "falseWithoutIssues" => new MappingValidationResult(MappingOperationStatus.InvalidMapping, Provenance(artifact), false, 0, 0, false, []),
                "untruncatedMissingIssues" => new MappingValidationResult(MappingOperationStatus.InvalidMapping, Provenance(artifact), false, 2, 1, false, [Diagnostic("issue", "Finding")]),
                _ => new MappingValidationResult(MappingOperationStatus.ProtocolFailure, Provenance(artifact), null, 1, 0, false, [])
            })
        };

        var result = Assert.IsType<NativeExampleResultV1>(await new NativeExampleService(Manifest, backend, backend).GetAsync("basic"));

        Assert.Equal(OperationStatusV1.ProtocolFailure, result.Validation.Status);
        Assert.Null(result.Validation.IsValid);
        Assert.Equal(0, result.Validation.TotalIssueCount);
        Assert.Equal(0, result.Validation.EmittedIssueCount);
        Assert.False(result.Validation.Truncated);
        Assert.True(result.Validation.Provenance!.Confirmed);
        Assert.Equal(result.Example.Revision, result.Validation.Provenance.Revision);
        var failure = Assert.Single(result.Validation.Diagnostics);
        Assert.Equal("mapping.example.protocol", failure.Code);
        Assert.Equal("The native inspection backend returned an inconsistent report.", failure.Message);
        Assert.Null(failure.NativeLocator);
        Assert.Null(failure.Owner);
        Assert.Null(failure.ProjectionLocator);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeValidationRefusalPrecedesOriginIndexLimitAndValidResultMapsIndexLimitSafely(bool consistent)
    {
        var backend = new Backend
        {
            Projection = (artifact, _) => Task.FromResult(Projection(artifact, Enumerable.Repeat(Parameter(), 8193).ToArray())),
            Validation = (artifact, _) => Task.FromResult(new MappingValidationResult(MappingOperationStatus.InvalidMapping, Provenance(artifact), false,
                consistent ? 1 : 0, 1, false, [Diagnostic("actual.issue", "Finding")]))
        };

        var result = Assert.IsType<NativeExampleResultV1>(await new NativeExampleService(Manifest, backend, backend).GetAsync("basic"));

        Assert.Equal(OperationStatusV1.Succeeded, result.Projection.Status);
        Assert.Equal(8193, result.Projection.Document!.Parameters.Count);
        Assert.Equal(consistent ? OperationStatusV1.ResourceLimit : OperationStatusV1.ProtocolFailure, result.Validation.Status);
        Assert.Equal(consistent ? "mapping.example.limit" : "mapping.example.protocol", Assert.Single(result.Validation.Diagnostics).Code);
        Assert.Equal(consistent ? "The example report exceeded the server response policy." : "The native inspection backend returned an inconsistent report.",
            Assert.Single(result.Validation.Diagnostics).Message);
        Assert.True(result.Validation.Provenance!.Confirmed);
        Assert.Null(result.Validation.IsValid);
        Assert.Equal(1, backend.ProjectionCalls);
        Assert.Equal(1, backend.ValidationCalls);
    }

    [Fact]
    public async Task CancellationDuringOriginIndexStopsLinkingAndKeepsTheCompletedProjectionIndependent()
    {
        using var cancellation = new CancellationTokenSource();
        var values = new CancelingList<ProjectionParameter>(Parameter(), cancellation);
        var backend = new Backend
        {
            Projection = (artifact, _) => Task.FromResult(Projection(artifact, values)),
            Validation = (artifact, _) => Task.FromResult(new MappingValidationResult(MappingOperationStatus.InvalidMapping, Provenance(artifact), false,
                1, 1, false, [Diagnostic("actual.issue", "Finding")]))
        };

        var result = Assert.IsType<NativeExampleResultV1>(await new NativeExampleService(Manifest, backend, backend).GetAsync("basic", cancellation.Token));

        Assert.Equal(1, values.Reads);
        Assert.Equal(OperationStatusV1.Succeeded, result.Projection.Status);
        Assert.Equal(OperationStatusV1.Canceled, result.Validation.Status);
        Assert.Null(result.Validation.IsValid);
        Assert.Equal("mapping.example.canceled", Assert.Single(result.Validation.Diagnostics).Code);
        Assert.Equal("The example request was canceled.", Assert.Single(result.Validation.Diagnostics).Message);
        Assert.True(result.Validation.Provenance!.Confirmed);
        Assert.Equal(cancellation.Token, backend.ProjectionToken);
        Assert.Equal(cancellation.Token, backend.ValidationToken);
    }

    [Fact]
    public async Task ForwardedFailureRetainsConfirmedOperationProvenanceWithoutPublishingContradictoryDocument()
    {
        var backend = new Backend
        {
            Projection = (artifact, _) =>
            {
                var report = Projection(artifact, [Parameter()]);
                var document = report.Projection!;
                return Task.FromResult(report with
                {
                    Projection = document with
                    {
                        Provenance = document.Provenance with { AdapterVersion = "foreign-adapter" }
                    }
                });
            }
        };

        var result = Assert.IsType<NativeExampleResultV1>(await new NativeExampleService(Manifest, backend, backend).GetAsync("basic"));

        Assert.Equal(OperationStatusV1.ProtocolFailure, result.Projection.Status);
        Assert.Null(result.Projection.Document);
        Assert.True(result.Projection.Provenance!.Confirmed);
        Assert.Equal(result.Example.Revision, result.Projection.Provenance.Revision);
        Assert.Equal(result.Example.Digest, result.Projection.Provenance.NativeArtifactDigest);
        Assert.Equal("fixture.adapter", result.Projection.Provenance.AdapterVersion);
        Assert.Equal("mapping.example.protocol", Assert.Single(result.Projection.Diagnostics).Code);
        Assert.True(result.Validation.IsValid);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyExactConfirmedCrossOperationEvidenceLinksTheExampleParameter(bool foreignProfile)
    {
        var backend = new Backend
        {
            Validation = (artifact, _) => Task.FromResult(new MappingValidationResult(MappingOperationStatus.InvalidMapping,
                foreignProfile ? Provenance(artifact) with { BackendBuild = "foreign-build" } : Provenance(artifact), false, 1, 1, false,
                [Diagnostic("actual.issue", "Finding") with { Owner = new GraphNodeDiagnosticOwner("18446744073709551615", "4294967295") }]))
        };

        var result = Assert.IsType<NativeExampleResultV1>(await new NativeExampleService(Manifest, backend, backend).GetAsync("functions"));

        var issue = Assert.Single(result.Validation.Diagnostics);
        Assert.Equal(OperationStatusV1.InvalidMapping, result.Validation.Status);
        Assert.Equal(Native, issue.NativeLocator);
        Assert.Equal(new GraphNodeNativeOwnerV1("18446744073709551615", "4294967295"), issue.Owner);
        if (foreignProfile) Assert.Null(issue.ProjectionLocator);
        else Assert.Equal(new ParameterNodeReference("parameter"), issue.ProjectionLocator);
        Assert.Equal(new[] { "project", "validate" }, backend.Operations);
        Assert.Same(backend.ProjectionArtifact, backend.ValidationArtifact);
    }

    private static MappingOperationProvenance Provenance(NativeArtifact artifact) => new(artifact.Scope, artifact.Digest,
        "fixture.backend", "fixture.engine", "fixture.build", "fixture.adapter", true);
    private static MappingDiagnostic Diagnostic(string code, string message) => new(code, DiagnosticSeverity.Error, message, false, Native);
    private static ProjectionParameter Parameter() => new("parameter", "Parameter", TextType, FieldRequirement.Optional, null, Native);
    private static MappingProjectionResult Projection(NativeArtifact artifact, IReadOnlyList<ProjectionParameter> parameters)
    {
        var provenance = Provenance(artifact);
        var scope = new ScopedRevisionId(artifact.Scope.OrganizationId.ToString("D"), artifact.Scope.ProjectId.ToString("D"),
            artifact.Scope.MappingId.ToString("D"), artifact.Scope.RevisionId.ToString("D"));
        return new(MappingOperationStatus.Succeeded, provenance, new(new(scope, new("sha256", artifact.Digest.Value),
            new(provenance.BackendId, provenance.EngineRevision, provenance.BackendBuild), provenance.AdapterBuild, "fixture.projection"),
            new(CoverageStatus.Complete, []), [], [], parameters, [], [], [], [], []), []);
    }
    private static OperationDiagnosticV1 ToOperation(ValidationDiagnosticV1 value) => new(value.Code, value.Severity, value.Message,
        value.MessageTruncated, value.NativeLocator);

    private sealed class Backend : IMappingInspectionBackend, IMappingProjectionBackend
    {
        public Func<NativeArtifact, CancellationToken, Task<MappingProjectionResult>> Projection { get; set; } =
            (artifact, _) => Task.FromResult(NativeExampleForwardingTests.Projection(artifact, [Parameter()]));
        public Func<NativeArtifact, CancellationToken, Task<MappingValidationResult>> Validation { get; set; } =
            (artifact, _) => Task.FromResult(new MappingValidationResult(MappingOperationStatus.Succeeded, Provenance(artifact), true, 0, 0, false, []));
        public int ProjectionCalls { get; private set; }
        public int ValidationCalls { get; private set; }
        public List<string> Operations { get; } = [];
        public NativeArtifact? ProjectionArtifact { get; private set; }
        public NativeArtifact? ValidationArtifact { get; private set; }
        public CancellationToken ProjectionToken { get; private set; }
        public CancellationToken ValidationToken { get; private set; }
        public Task<MappingProjectionResult> ProjectAsync(NativeArtifact artifact, CancellationToken cancellationToken = default)
        {
            ProjectionCalls++;
            ProjectionArtifact = artifact;
            ProjectionToken = cancellationToken;
            Operations.Add("project");
            return Projection(artifact, cancellationToken);
        }
        public Task<MappingValidationResult> ValidateAsync(NativeArtifact artifact, CancellationToken cancellationToken = default)
        {
            ValidationCalls++;
            ValidationArtifact = artifact;
            ValidationToken = cancellationToken;
            Operations.Add("validate");
            return Validation(artifact, cancellationToken);
        }
        public Task<MappingInspectionResult> InspectAsync(NativeArtifact artifact, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Example reads only request projection and validation.");
    }
    private sealed class CancelingList<T>(T item, CancellationTokenSource cancellation) : IReadOnlyList<T>
    {
        public int Count => 1;
        public int Reads { get; private set; }
        public T this[int index] { get { Reads++; cancellation.Cancel(); return item; } }
        // The final existing example serializer may enumerate the already completed projection.
        // Cancellation is deliberately observed by the new indexed origin traversal, once.
        public IEnumerator<T> GetEnumerator() => Enumerable.Repeat(item, 1).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
