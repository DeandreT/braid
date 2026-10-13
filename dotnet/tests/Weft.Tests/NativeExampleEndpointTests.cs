using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Weft.Api;
using Weft.Contracts.Examples;
using Weft.Contracts.Operations;
using Weft.Contracts.Projections;
using Weft.Contracts.Validation;
using Weft.Domain;
using Weft.MappingBackends;

namespace Weft.Tests;

public sealed class NativeExampleEndpointTests
{
    private const string ExamplesPath = "/api/v1/examples/mappings";
    private const string FunctionId = "9007199254740993";
    private const string WorkerBuild = "test.native-example-worker.v1";
    private static readonly JsonSerializerOptions Options = ProjectionJson.CreateOptions();

    [Fact]
    public async Task ManifestIsStableAndAvailableWithoutStartingABackend()
    {
        var projectionBackend = new ProjectionBackend();
        var validationBackend = new ValidationBackend();
        await using var app = await CreateApp("Development", projectionBackend, validationBackend);
        using var client = app.GetTestClient();
        using var firstResponse = await client.GetAsync(ExamplesPath);
        using var secondResponse = await client.GetAsync(ExamplesPath);
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        var first = await firstResponse.Content.ReadFromJsonAsync<NativeExampleDescriptorV1[]>(Options);
        var second = await secondResponse.Content.ReadFromJsonAsync<NativeExampleDescriptorV1[]>(Options);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first, second);
        Assert.Equal(new[] { "basic", "exact", "conditions", "controls", "functions", "partial", "invalid", "generated", "ambiguous" },
            first.Select(example => example.Id));
        Assert.Equal(first.Length, first.Select(example => example.Revision).Distinct().Count());
        foreach (var example in first)
        {
            Assert.False(string.IsNullOrWhiteSpace(example.Title));
            Assert.False(string.IsNullOrWhiteSpace(example.Source));
            Assert.InRange(example.ByteCount, 1, InspectionPolicy.MaximumNativeBytes);
            Assert.Equal("sha256", example.Digest.Algorithm);
            Assert.Equal(Sha256Digest.Parse(example.Digest.Value).Value, example.Digest.Value);
            Assert.All(new[] { example.Revision.OrganizationId, example.Revision.ProjectId, example.Revision.MappingId, example.Revision.RevisionId },
                id => Assert.True(Guid.TryParseExact(id, "D", out var parsed) && parsed != Guid.Empty));
        }
        Assert.Equal(0, projectionBackend.Calls);
        Assert.Equal(0, validationBackend.ValidationCalls);
        Assert.Equal(0, validationBackend.InspectionCalls);
    }

    [Fact]
    public async Task MissingWorkerConfigurationKeepsMetadataAndReportsExplicitUnavailableOperations()
    {
        await using var app = await CreateApp("Development");
        using var client = app.GetTestClient();
        var examples = await client.GetFromJsonAsync<NativeExampleDescriptorV1[]>(ExamplesPath, Options);
        Assert.NotNull(examples);
        using var response = await client.GetAsync(ExamplesPath + "/basic");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<NativeExampleResultV1>(Options);
        Assert.NotNull(result);
        Assert.Equal(Assert.Single(examples, example => example.Id == "basic"), result.Example);
        Assert.Equal(OperationStatusV1.BackendUnavailable, result.Projection.Status);
        Assert.Null(result.Projection.Provenance);
        Assert.Null(result.Projection.Document);
        Assert.NotEmpty(result.Projection.Diagnostics);
        Assert.Equal(OperationStatusV1.BackendUnavailable, result.Validation.Status);
        Assert.Null(result.Validation.Provenance);
        Assert.Null(result.Validation.IsValid);
        Assert.Equal(0, result.Validation.TotalIssueCount);
        Assert.Equal(0, result.Validation.EmittedIssueCount);
        Assert.False(result.Validation.Truncated);
        Assert.NotEmpty(result.Validation.Diagnostics);
        Assert.All(result.Projection.Diagnostics, diagnostic => Assert.InRange(Encoding.UTF8.GetByteCount(diagnostic.Message), 1, InspectionPolicy.MaximumTextBytes));
        Assert.All(result.Validation.Diagnostics, diagnostic => Assert.InRange(Encoding.UTF8.GetByteCount(diagnostic.Message), 1, InspectionPolicy.MaximumTextBytes));
        using var raw = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("backendUnavailable", raw.RootElement.GetProperty("projection").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, raw.RootElement.GetProperty("projection").GetProperty("provenance").ValueKind);
        Assert.Equal(JsonValueKind.Null, raw.RootElement.GetProperty("projection").GetProperty("document").ValueKind);
        Assert.Equal(JsonValueKind.Null, raw.RootElement.GetProperty("validation").GetProperty("isValid").ValueKind);
    }

    [Theory]
    [InlineData("Development", "missing-example")]
    [InlineData("Production", "basic")]
    [InlineData("Staging", "exact")]
    public async Task UnknownAndNonDevelopmentExamplesReturnEmpty404sWithoutBackendWork(string environment, string id)
    {
        var projectionBackend = new ProjectionBackend();
        var validationBackend = new ValidationBackend();
        await using var app = await CreateApp(environment, projectionBackend, validationBackend);
        using var client = app.GetTestClient();
        using var response = await client.GetAsync(ExamplesPath + "/" + id);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
        if (environment != "Development")
        {
            using var manifest = await client.GetAsync(ExamplesPath);
            Assert.Equal(HttpStatusCode.NotFound, manifest.StatusCode);
            Assert.Equal("", await manifest.Content.ReadAsStringAsync());
        }
        Assert.Equal(0, projectionBackend.Calls);
        Assert.Equal(0, validationBackend.ValidationCalls);
        Assert.Equal(0, validationBackend.InspectionCalls);
    }

    [Fact]
    public async Task InvalidValidationAndSuccessfulProjectionRemainIndependentTypedReports()
    {
        var projectionBackend = new ProjectionBackend();
        var validationBackend = new ValidationBackend();
        await using var app = await CreateApp("Development", projectionBackend, validationBackend);
        using var client = app.GetTestClient();
        using var response = await client.GetAsync(ExamplesPath + "/basic");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<NativeExampleResultV1>(Options);
        Assert.NotNull(result);
        Assert.Equal(OperationStatusV1.Succeeded, result.Projection.Status);
        Assert.Empty(result.Projection.Diagnostics);
        var document = Assert.IsType<ProjectionDocumentV1>(result.Projection.Document);
        Assert.Equal(CoverageStatus.Complete, document.Coverage.Status);
        Assert.Equal(result.Example.Revision, document.Provenance.Revision);
        Assert.Equal(result.Example.Digest, document.Provenance.NativeArtifactDigest);
        Assert.Equal(result.Projection.Provenance!.Backend, document.Provenance.Backend);
        Assert.Equal(result.Projection.Provenance.AdapterVersion, document.Provenance.AdapterVersion);
        Assert.Equal(new ExactNumberLiteralValue(ExactNumberFlavor.Integer, "9007199254740993"),
            Assert.IsType<ConstantDependency>(document.Rules[0].Dependencies[1]).Value);
        Assert.Equal(OperationStatusV1.InvalidMapping, result.Validation.Status);
        Assert.False(result.Validation.IsValid);
        Assert.Equal(1, result.Validation.TotalIssueCount);
        Assert.Equal(1, result.Validation.EmittedIssueCount);
        var issue = Assert.Single(result.Validation.Diagnostics);
        Assert.Equal(new GraphNodeNativeOwnerV1(FunctionId, "12"), issue.Owner);
        Assert.Equal(new GraphNodeNativeLocator(InspectionPolicy.DocumentId, "12", FunctionId), issue.NativeLocator);
        Assert.Equal(new RuleNodeReference("rule:example"), issue.ProjectionLocator);
        Assert.Equal("Output amount", issue.Location);
        Assert.Equal(1, projectionBackend.Calls);
        Assert.Equal(1, validationBackend.ValidationCalls);
        Assert.Equal(0, validationBackend.InspectionCalls);
        Assert.NotNull(projectionBackend.LastArtifact);
        Assert.NotNull(validationBackend.LastArtifact);
        Assert.Equal(projectionBackend.LastArtifact.Scope, validationBackend.LastArtifact.Scope);
        Assert.Equal(projectionBackend.LastArtifact.CopyBytes(), validationBackend.LastArtifact.CopyBytes());
        Assert.Equal(result.Example.Digest.Value, projectionBackend.LastArtifact.Digest.Value);
        Assert.Equal(result.Example.ByteCount, projectionBackend.LastArtifact.ByteCount);
        using var raw = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var validation = raw.RootElement.GetProperty("validation");
        Assert.Equal("invalidMapping", validation.GetProperty("status").GetString());
        var owner = validation.GetProperty("diagnostics")[0].GetProperty("owner");
        Assert.Equal("graphNode", owner.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.String, owner.GetProperty("functionId").ValueKind);
        Assert.Equal(FunctionId, owner.GetProperty("functionId").GetString());
        Assert.Equal("rule", validation.GetProperty("diagnostics")[0].GetProperty("projectionLocator").GetProperty("kind").GetString());
    }

    [Theory]
    [InlineData(MappingOperationStatus.TimedOut, MappingOperationStatus.InvalidMapping, OperationStatusV1.TimedOut, OperationStatusV1.InvalidMapping)]
    [InlineData(MappingOperationStatus.Succeeded, MappingOperationStatus.BackendFailure, OperationStatusV1.Succeeded, OperationStatusV1.BackendFailure)]
    public async Task IndividualOperationFailuresAreReturnedAsReportsWithHttp200(MappingOperationStatus projectionStatus,
        MappingOperationStatus validationStatus, OperationStatusV1 expectedProjection, OperationStatusV1 expectedValidation)
    {
        var projectionBackend = new ProjectionBackend(projectionStatus);
        var validationBackend = new ValidationBackend(validationStatus);
        await using var app = await CreateApp("Development", projectionBackend, validationBackend);
        using var client = app.GetTestClient();
        using var response = await client.GetAsync(ExamplesPath + "/basic");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<NativeExampleResultV1>(Options);
        Assert.NotNull(result);
        Assert.Equal(expectedProjection, result.Projection.Status);
        Assert.Equal(expectedValidation, result.Validation.Status);
        if (projectionStatus == MappingOperationStatus.Succeeded)
            Assert.NotNull(result.Projection.Document);
        else
        {
            Assert.Null(result.Projection.Document);
            Assert.NotEmpty(result.Projection.Diagnostics);
        }
        if (validationStatus == MappingOperationStatus.InvalidMapping)
            Assert.False(result.Validation.IsValid);
        else
        {
            Assert.Null(result.Validation.IsValid);
            Assert.NotEmpty(result.Validation.Diagnostics);
        }
        Assert.Equal(1, projectionBackend.Calls);
        Assert.Equal(1, validationBackend.ValidationCalls);
    }

    [Fact]
    public async Task OpenApiDescribesPublicReportsNullableValidationAndNativeOwnerUnions()
    {
        await using var app = await CreateApp("Development");
        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.StartsWith("3.1.", root.GetProperty("openapi").GetString(), StringComparison.Ordinal);
        var paths = root.GetProperty("paths");
        var manifestResponses = paths.GetProperty(ExamplesPath).GetProperty("get").GetProperty("responses");
        var listSchema = manifestResponses.GetProperty("200").GetProperty("content").GetProperty("application/json").GetProperty("schema");
        Assert.Equal("array", listSchema.GetProperty("type").GetString());
        Assert.Equal("#/components/schemas/NativeExampleDescriptorV1", listSchema.GetProperty("items").GetProperty("$ref").GetString());
        var exampleResponses = paths.GetProperty(ExamplesPath + "/{exampleId}").GetProperty("get").GetProperty("responses");
        Assert.Equal("#/components/schemas/NativeExampleResultV1", exampleResponses.GetProperty("200").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
        Assert.False(exampleResponses.GetProperty("404").TryGetProperty("content", out _));
        var schemas = root.GetProperty("components").GetProperty("schemas");
        var resultProperties = schemas.GetProperty("NativeExampleResultV1").GetProperty("properties");
        Assert.Equal("#/components/schemas/ProjectionReportV1", resultProperties.GetProperty("projection").GetProperty("$ref").GetString());
        Assert.Equal("#/components/schemas/ValidationReportV1", resultProperties.GetProperty("validation").GetProperty("$ref").GetString());
        var validationSchema = schemas.GetProperty("ValidationReportV1");
        var isValid = validationSchema.GetProperty("properties").GetProperty("isValid");
        Assert.True(AllowsType(isValid, schemas, "boolean"));
        Assert.True(AllowsType(isValid, schemas, "null"));
        var status = Resolve(schemas.GetProperty("ProjectionReportV1").GetProperty("properties").GetProperty("status"), schemas);
        Assert.Contains(status.GetProperty("enum").EnumerateArray(), item => item.GetString() == "backendUnavailable");
        Assert.Contains(status.GetProperty("enum").EnumerateArray(), item => item.GetString() == "invalidMapping");
        Assert.All(status.GetProperty("enum").EnumerateArray(), item => Assert.Equal(JsonValueKind.String, item.ValueKind));
        foreach (var type in new[] { "NativeDiagnosticOwnerV1", "NativeEndpointV1", "SchemaNativeOwnerStepV1", "ScopeNativeOwnerStepV1", "ProjectionNodeReference", "NativeLocator" })
        {
            var union = schemas.GetProperty(type);
            Assert.Equal("kind", union.GetProperty("discriminator").GetProperty("propertyName").GetString());
            var variants = union.GetProperty("discriminator").GetProperty("mapping").EnumerateObject().ToArray();
            Assert.True(variants.Length > 1);
            foreach (var variant in variants)
            {
                var subtype = schemas.GetProperty(variant.Value.GetString()!["#/components/schemas/".Length..]);
                Assert.Contains(subtype.GetProperty("required").EnumerateArray(), item => item.GetString() == "kind");
                Assert.Equal(variant.Name, Assert.Single(subtype.GetProperty("properties").GetProperty("kind").GetProperty("enum").EnumerateArray()).GetString());
            }
        }
        var graphOwnerComponent = schemas.GetProperty("NativeDiagnosticOwnerV1").GetProperty("discriminator")
            .GetProperty("mapping").GetProperty("graphNode").GetString()!["#/components/schemas/".Length..];
        Assert.Contains(schemas.GetProperty(graphOwnerComponent).GetProperty("required").EnumerateArray(), item => item.GetString() == "nodeId");
        foreach (var internalType in new[] { "MappingProjectionResult", "MappingValidationResult", "MappingDiagnostic", "MappingDiagnosticOwner", "GraphNodeDiagnosticOwner", "RevisionScope", "Sha256Digest" })
            Assert.False(schemas.TryGetProperty(internalType, out _));
    }

    private static JsonElement Resolve(JsonElement schema, JsonElement components)
    {
        while (schema.TryGetProperty("$ref", out var reference))
            schema = components.GetProperty(reference.GetString()!["#/components/schemas/".Length..]);
        return schema;
    }

    private static bool AllowsType(JsonElement schema, JsonElement components, string type)
    {
        schema = Resolve(schema, components);
        if (schema.TryGetProperty("type", out var declared) &&
            (declared.ValueKind == JsonValueKind.String && declared.GetString() == type ||
                declared.ValueKind == JsonValueKind.Array && declared.EnumerateArray().Any(item => item.GetString() == type)))
            return true;
        return new[] { "anyOf", "oneOf" }.Any(key => schema.TryGetProperty(key, out var variants) &&
            variants.EnumerateArray().Any(variant => AllowsType(variant, components, type)));
    }

    private static async Task<WebApplication> CreateApp(string environment, ProjectionBackend? projectionBackend = null, ValidationBackend? validationBackend = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        builder.Services.AddWeftApi();
        if (projectionBackend is not null) builder.Services.AddSingleton<IMappingProjectionBackend>(projectionBackend);
        if (validationBackend is not null) builder.Services.AddSingleton<IMappingInspectionBackend>(validationBackend);
        builder.Services.AddWeftNativeExamples();
        var app = builder.Build();
        app.MapWeftNativeExamples();
        if (environment == "Development") app.MapOpenApi();
        await app.StartAsync();
        return app;
    }

    private static MappingOperationProvenance BackendProvenance(NativeArtifact artifact, bool confirmed) =>
        new(artifact.Scope, artifact.Digest, "ferrule", FerruleInspectionBackend.EngineRevision, WorkerBuild, FerruleInspectionBackend.AdapterBuild, confirmed);

    private static ProjectionDocumentV1 Projection(NativeArtifact artifact)
    {
        var backend = BackendProvenance(artifact, true);
        var revision = new ScopedRevisionId(artifact.Scope.OrganizationId.ToString("D"), artifact.Scope.ProjectId.ToString("D"),
            artifact.Scope.MappingId.ToString("D"), artifact.Scope.RevisionId.ToString("D"));
        var scalar = new FieldDataType(FieldDataTypeKind.Scalar, [ScalarDataType.String]);
        var cardinality = new FieldCardinality(CardinalityKind.ExactlyOne);
        var source = new ProjectionField("field:source", "boundary:input", [new FieldPathSegment("customer.name")], "customer.name", scalar,
            cardinality, FieldRequirement.Required, FieldNullability.NonNullable, new JsonPointerNativeLocator(InspectionPolicy.DocumentId, "/source/a~0b~1c"));
        var target = new ProjectionField("field:output", "boundary:output", [new FieldPathSegment("amount")], "amount", new FieldDataType(FieldDataTypeKind.Scalar, [ScalarDataType.Integer]),
            cardinality, FieldRequirement.Required, FieldNullability.NonNullable, new JsonPointerNativeLocator(InspectionPolicy.DocumentId, "/target/amount"));
        var number = new ExactNumberLiteralValue(ExactNumberFlavor.Integer, "9007199254740993");
        return new ProjectionDocumentV1(new ProjectionProvenance(revision, new ArtifactDigest("sha256", artifact.Digest.Value),
                new BackendIdentity(backend.BackendId, backend.EngineRevision, backend.BackendBuild), backend.AdapterBuild, FerruleInspectionBackend.ProjectionVersion),
            new ProjectionCoverage(CoverageStatus.Complete, []),
            [new ProjectionBoundary("boundary:input", "Input", BoundaryRole.PrimaryInput, "native-json", BoundaryShape.Object, null, new DocumentNativeLocator(InspectionPolicy.DocumentId)),
                new ProjectionBoundary("boundary:output", "Output", BoundaryRole.PrimaryOutput, "native-json", BoundaryShape.Object, null, new DocumentNativeLocator(InspectionPolicy.DocumentId))],
            [source, target], [],
            [new ProjectionRule("rule:example", target.Id, "Explain an exact amount.", [new FieldDependency(source.Id), new ConstantDependency(number)],
                [new ExpressionDetail("choose", [new ReferenceDetail(new FieldNodeReference(source.Id)), new LiteralDetail(number)])], new GraphNodeNativeLocator(InspectionPolicy.DocumentId, "12", FunctionId))],
            [],
            [new ProjectionRelationship("relationship:input", RelationshipKind.BoundaryMember, new FieldNodeReference(source.Id), new BoundaryNodeReference(source.BoundaryId)),
                new ProjectionRelationship("relationship:output", RelationshipKind.BoundaryMember, new FieldNodeReference(target.Id), new BoundaryNodeReference(target.BoundaryId)),
                new ProjectionRelationship("relationship:read", RelationshipKind.ReadsFrom, new RuleNodeReference("rule:example"), new FieldNodeReference(source.Id)),
                new ProjectionRelationship("relationship:write", RelationshipKind.WritesTo, new RuleNodeReference("rule:example"), new FieldNodeReference(target.Id))],
            [],
            Enum.GetValues<MappingOperation>().Select(operation => new OperationCapability(operation,
                operation is MappingOperation.Inspect or MappingOperation.Validate or MappingOperation.Project ? CapabilityStatus.Supported : CapabilityStatus.NotQualified,
                "ferrule.projection.v1", operation is MappingOperation.Preview or MappingOperation.Build ? [target.BoundaryId] : [],
                [new CapabilityCondition("boundedReadOnlyProjection", "This example explains the mapping without executing it.")])).ToArray());
    }

    private sealed class ProjectionBackend(MappingOperationStatus status = MappingOperationStatus.Succeeded) : IMappingProjectionBackend
    {
        public int Calls { get; private set; }
        public NativeArtifact? LastArtifact { get; private set; }

        public Task<MappingProjectionResult> ProjectAsync(NativeArtifact artifact, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastArtifact = artifact;
            var successful = status == MappingOperationStatus.Succeeded;
            return Task.FromResult(new MappingProjectionResult(status, BackendProvenance(artifact, successful), successful ? Projection(artifact) : null,
                successful ? [] : [new MappingDiagnostic("mapping.projection.timeout", DiagnosticSeverity.Error, "The projection worker exceeded its deadline.", false)]));
        }
    }

    private sealed class ValidationBackend(MappingOperationStatus status = MappingOperationStatus.InvalidMapping) : IMappingInspectionBackend
    {
        public int InspectionCalls { get; private set; }
        public int ValidationCalls { get; private set; }
        public NativeArtifact? LastArtifact { get; private set; }

        public Task<MappingInspectionResult> InspectAsync(NativeArtifact artifact, CancellationToken cancellationToken = default)
        {
            InspectionCalls++;
            throw new InvalidOperationException("Example routes do not request inspection.");
        }

        public Task<MappingValidationResult> ValidateAsync(NativeArtifact artifact, CancellationToken cancellationToken = default)
        {
            ValidationCalls++;
            LastArtifact = artifact;
            if (status == MappingOperationStatus.InvalidMapping)
                return Task.FromResult(new MappingValidationResult(status, BackendProvenance(artifact, true), false, 1, 1, false,
                    [new MappingDiagnostic("ferrule.validation", DiagnosticSeverity.Error, "The output rule is invalid.", false,
                        new GraphNodeNativeLocator(InspectionPolicy.DocumentId, "12", FunctionId), new GraphNodeDiagnosticOwner(FunctionId, "12"), "Output amount")]));
            return Task.FromResult(new MappingValidationResult(status, BackendProvenance(artifact, false), null, 0, 0, false,
                [new MappingDiagnostic("mapping.validation.failure", DiagnosticSeverity.Error, "The validation worker failed.", false)]));
        }
    }
}
