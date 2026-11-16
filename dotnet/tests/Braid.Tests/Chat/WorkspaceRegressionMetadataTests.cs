using System.Reflection;
using System.Text.RegularExpressions;
using Braid.Api.Catalog;
using Braid.Api.Chat;
using Braid.Api.Chat.Workspace;
using Braid.Application;
using Braid.Application.Regression;
using Braid.Deployment;
using Braid.Domain;
using Braid.MappingBackends;
using Braid.MappingBackends.Workers;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

public sealed class WorkspaceRegressionMetadataTests
{
    [Fact]
    public async Task Shared_csv_capabilities_use_only_fresh_catalog_authority()
    {
        var fixture = new WorkspaceToolFixture { Role = ProjectRole.Publisher };
        var services = new CapabilityServices(fixture.Services);
        var json = new RegressionRuntimeIdentity("ferrule", "engine", "build", "json", PreviewPolicy.Profile);
        var csv = new RegressionRuntimeIdentity("ferrule", "engine", "build", "csv", CsvDocumentPolicy.Profile);
        var regression = await CatalogEndpoints.RegressionCapabilitiesAsync(services, WorkspaceToolFixture.Actor,
            WorkspaceToolFixture.Mapping, new(true, json, csvRuntime: csv), default);
        Assert.Equal(CsvDocumentPolicy.Profile, regression!.CsvRuntime!.Profile);
        Assert.Equal(new[] { RegressionComparisonService.ExactBytesPolicy, RegressionComparisonService.CsvRowsPolicy }, regression.ComparisonPolicies);
        var backend = new BuildBackendIdentity("ferrule", "engine", "build", "json");
        var csvBackend = new BuildBackendIdentity("ferrule", "engine", "build", "csv");
        var build = await CatalogEndpoints.BuildCapabilitiesAsync(services, WorkspaceToolFixture.Actor, WorkspaceToolFixture.Mapping,
            new(true, backend, FerruleBuildCompiler.Profile, csvBackend: csvBackend, csvProfile: FerruleBuildCompiler.CsvProfile), default);
        Assert.Equal(BuildWorkerProtocol.CsvProfile, build!.CsvProfile!.Id);
        Assert.Equal("csv", build.CsvBackend!.AdapterVersion);
        fixture.OnCatalogRead = _ => { fixture.Active = false; return Task.CompletedTask; };
        Assert.Null(await CatalogEndpoints.RegressionCapabilitiesAsync(services, WorkspaceToolFixture.Actor,
            WorkspaceToolFixture.Mapping, new(true, json, csvRuntime: csv), default));
    }

    private sealed class CapabilityServices(IServiceProvider other) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(IPrivateObjectStore) || serviceType == typeof(IRevisionReportMetadataReader) ||
                serviceType == typeof(IInspectionWorkerRunner) || serviceType == typeof(ICsvBuildBoundaryReader))
                throw new InvalidOperationException("A capability read resolved private content or a worker.");
            return other.GetService(serviceType);
        }
    }

    [Fact]
    public void The_embedded_metadata_definition_is_not_swallowed_by_preview_dollar_quotes()
    {
        var loader = typeof(RegressionRunRoleGuard).GetMethod("Load", BindingFlags.Static | BindingFlags.NonPublic)!;
        var definitions = Assert.IsType<Dictionary<string, Match>>(loader.Invoke(null, null));
        var metadata = definitions["read_regression_run_metadata"];
        Assert.Equal("org uuid, id uuid", metadata.Groups[2].Value);
        Assert.DoesNotContain("result_body", metadata.Groups[7].Value, StringComparison.Ordinal);
        Assert.Contains("r.submitter_id", metadata.Groups[7].Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Metadata_tools_and_citation_checks_never_read_the_private_completion()
    {
        var fixture = new WorkspaceToolFixture { Role = ProjectRole.Runner };
        var reader = new Reader();
        var service = new WorkspaceToolService(new Services(fixture.Services, reader), new ChatApiOptions(), new(false, true, false, false));
        var result = await fixture.CallAsync(service, "get_run", $$"""{"kind":"regressionRun","runId":"{{Reader.Id:D}}"}""");
        Assert.Equal("available", result.Outcome);
        Assert.Equal("completed", result.Data.GetProperty("state").GetString());
        Assert.Equal(0, reader.CompletionReads);
        var citations = await service.VerifyAsync(WorkspaceToolFixture.Actor, fixture.Conversation(),
            [ChatWorkspaceCitationFact.RegressionRun(WorkspaceToolFixture.Mapping, Reader.Id)], CancellationToken.None);
        Assert.Single(citations);
        Assert.Equal(2, reader.MetadataReads);
        Assert.Equal(0, reader.CompletionReads);
    }

    [Fact]
    public async Task A_denied_payload_call_locates_metadata_without_reading_the_private_completion()
    {
        var fixture = new WorkspaceToolFixture(payloads: true) { Role = ProjectRole.Runner };
        var reader = new Reader();
        var service = new WorkspaceToolService(new Services(fixture.Services, reader), new ChatApiOptions(), new(false, true, false, false));
        var result = await fixture.CallAsync(service, "get_regression_run_output", $$"""{"runId":"{{Reader.Id:D}}","ordinal":0}""",
            fixture.Context(service, payloads: true));
        Assert.Equal("notPermitted", result.Outcome);
        Assert.Equal(1, reader.MetadataReads);
        Assert.Equal(0, reader.CompletionReads);
        Assert.Null(result.Result.DataShared);
    }

    private sealed class Services(IServiceProvider other, Reader reader) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IRegressionRunReader) || serviceType == typeof(IRegressionRunMetadataReader)
            ? reader : other.GetService(serviceType);
    }

    private sealed class Reader : IRegressionRunReader, IRegressionRunMetadataReader
    {
        internal static readonly Guid Id = Guid.Parse("11111111-cccc-4444-8888-111111111111");
        internal int MetadataReads, CompletionReads;
        public Task<RegressionRunSummary?> ReadMetadataAsync(ActorContext actor, Guid organizationId, Guid runId, CancellationToken token = default)
        {
            MetadataReads++;
            return Task.FromResult<RegressionRunSummary?>(new(Id, Guid.Parse("22222222-cccc-4444-8888-222222222222"), 1,
                WorkspaceToolFixture.Head, RegressionRunState.Completed, 1, WorkspaceToolFixture.Now));
        }
        public Task<RegressionRunSnapshot?> ReadAsync(ActorContext actor, Guid organizationId, Guid runId, CancellationToken token = default)
        {
            CompletionReads++;
            throw new InvalidOperationException("An ungated chat read reached private completion bytes.");
        }
        public Task<RegressionRunPage> ListAsync(ActorContext actor, MappingScope mapping, int limit, Guid? afterRunId = null,
            CancellationToken token = default) => throw new InvalidOperationException("Unexpected listing.");
    }
}
