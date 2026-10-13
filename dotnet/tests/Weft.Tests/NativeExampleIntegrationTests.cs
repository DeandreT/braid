using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Weft.Api;
using Weft.Contracts.Examples;
using Weft.Contracts.Operations;
using Weft.Contracts.Projections;
using Weft.MappingBackends;
using Weft.MappingBackends.Workers;

namespace Weft.Tests;

public sealed class NativeExampleIntegrationTests
{
    [FerruleWorkerIntegrationTests.WorkerFact]
    public async Task ActualExampleHttpPreservesNativeRevisionAndSeparatesValidationFromCoverage()
    {
        var image = Environment.GetEnvironmentVariable("WEFT_FERRULE_WORKER_IMAGE")!;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        builder.Services.AddWeftApi();
        builder.Services.AddSingleton<IInspectionWorkerRunner>(new DockerInspectionWorkerRunner(new DockerInspectionWorkerOptions("/usr/bin/docker", image)));
        builder.Services.AddSingleton<FerruleInspectionBackend>();
        builder.Services.AddSingleton<IMappingInspectionBackend>(services => services.GetRequiredService<FerruleInspectionBackend>());
        builder.Services.AddSingleton<IMappingProjectionBackend>(services => services.GetRequiredService<FerruleInspectionBackend>());
        builder.Services.AddWeftNativeExamples();
        await using var app = builder.Build();
        app.MapWeftNativeExamples();
        await app.StartAsync();
        using var client = app.GetTestClient();
        var manifest = await client.GetFromJsonAsync<NativeExampleDescriptorV1[]>("/api/v1/examples/mappings", ProjectionJson.CreateOptions());
        Assert.NotNull(manifest);
        var descriptor = Assert.Single(manifest, value => value.Id == "basic");
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "FerruleFixtures", "valid-basic.json"));
        Assert.Equal(bytes.Length, descriptor.ByteCount);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), descriptor.Digest.Value);

        foreach (var (id, isValid, coverage) in new[]
        {
            ("basic", true, CoverageStatus.Complete),
            ("invalid", false, CoverageStatus.Partial),
            ("ambiguous", true, CoverageStatus.Partial)
        })
        {
            using var response = await client.GetAsync("/api/v1/examples/mappings/" + id);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var report = await response.Content.ReadFromJsonAsync<NativeExampleResultV1>(ProjectionJson.CreateOptions());
            Assert.NotNull(report);
            Assert.Equal(Assert.Single(manifest, value => value.Id == id), report.Example);
            Assert.Equal(OperationStatusV1.Succeeded, report.Projection.Status);
            Assert.Equal(isValid ? OperationStatusV1.Succeeded : OperationStatusV1.InvalidMapping, report.Validation.Status);
            Assert.Equal(isValid, report.Validation.IsValid);
            var projection = Assert.IsType<ProjectionDocumentV1>(report.Projection.Document);
            Assert.Equal(coverage, projection.Coverage.Status);
            Assert.Equal(report.Example.Revision, projection.Provenance.Revision);
            Assert.Equal(report.Example.Digest, projection.Provenance.NativeArtifactDigest);
            Assert.Equal("ferrule", projection.Provenance.Backend.Id);
            Assert.Equal(FerruleInspectionBackend.EngineRevision, projection.Provenance.Backend.Revision);
            Assert.Equal(image, projection.Provenance.Backend.BuildId);
            Assert.True(report.Projection.Provenance!.Confirmed);
            Assert.Equal(report.Projection.Provenance, report.Validation.Provenance);
            if (!isValid)
            {
                Assert.True(report.Validation.TotalIssueCount > 0);
                Assert.NotEmpty(report.Validation.Diagnostics);
                Assert.Contains(report.Validation.Diagnostics, value => value.Owner is not null && value.NativeLocator is not null);
                Assert.Contains(report.Validation.Diagnostics, value => value.ProjectionLocator is RuleNodeReference);
            }
            if (coverage == CoverageStatus.Partial)
                Assert.NotEmpty(projection.Coverage.Gaps);
        }

        using var missing = await client.GetAsync("/api/v1/examples/mappings/unknown");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("", await missing.Content.ReadAsStringAsync());
    }
}
