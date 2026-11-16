using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Braid.Api.Catalog;
using Braid.Application;
using Braid.Domain;
using Braid.MappingBackends;
using Braid.MappingBackends.Ingestion;
using Braid.MappingBackends.Workers;
using Braid.Persistence.Postgres;
using Xunit.Abstractions;

namespace Braid.Tests;

public sealed class RealMfdFactAttribute : FactAttribute
{
    public RealMfdFactAttribute()
    {
        if (!OperatingSystem.IsLinux() || !PostgresFixture.IsConfigured || string.IsNullOrEmpty(FerruleServiceTestSupport.Executable) ||
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BRAID_FERRULE_WORKER_IMAGE")))
            Skip = "Requires PostgreSQL, the built worker and its exact Docker image for full interchange qualification.";
    }
}

[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class RealMfdWorkflowTests(PostgresFixture fixture, ITestOutputHelper output)
{
    [RealMfdFact]
    public async Task DockerAndServicePublishReviewedImportsAndAppendWithOriginalProvenance()
    {
        var temporary = FerruleServiceTestSupport.TemporaryRoot();
        using var certificate = InspectionTransportTests.TransportCase.Certificate("CN=MFD workflow");
        using var client = InspectionTransportTests.TransportCase.Certificate("CN=MFD client");
        var owned = new OwnedWorkers();
        try
        {
            await using var service = await PackagedFerruleService.StartAsync(PackagedFerruleService.Bundle(certificate),
                [InspectionTransportTests.TransportCase.Pin(client)], temporary);
            var connector = new PinnedServiceConnector(new(new Uri($"https://127.0.0.1:{service.Port}"),
                InspectionTransportTests.TransportCase.Pin(certificate), client));
            await using var serviceMfd = new FerruleServiceRunner(new(connector, FerruleServiceProtocol.InterchangeProfile, PackagedFerruleService.Identity));
            await using var serviceNative = new FerruleServiceRunner(new(connector, FerruleServiceProtocol.InspectionProfile, PackagedFerruleService.Identity));
            var image = Environment.GetEnvironmentVariable("BRAID_FERRULE_WORKER_IMAGE")!;
            await using var dockerMfd = new DockerInspectionWorkerRunner(new("/usr/bin/docker", image, profile: DockerWorkerProfile.Interchange), owned);
            await using var dockerNative = new DockerInspectionWorkerRunner(new("/usr/bin/docker", image), owned);
            var dockerPackage = await Workflow(dockerMfd, dockerNative);
            var servicePackage = await Workflow(serviceMfd, serviceNative);
            Assert.Equal(dockerPackage, servicePackage);
            await AuthenticatedMfdHttpTests.RunAsync(fixture, new FerruleInterchangeBackend(dockerMfd),
                new([FerruleIngestionAdmissionBackend.CreateRegistration(dockerNative)]));
            await AuthenticatedMfdHttpTests.RunAsync(fixture, new FerruleInterchangeBackend(serviceMfd),
                new([FerruleIngestionAdmissionBackend.CreateRegistration(serviceNative)]));
            await serviceMfd.StopAsync();
            await serviceNative.StopAsync();
            Assert.Equal(0, await service.StopAsync());
        }
        finally
        {
            foreach (var worker in owned.Created) output.WriteLine("MFD owned worker: " + worker);
            Directory.Delete(temporary, true);
        }
    }

    private async Task<Sha256Digest> Workflow(IInspectionWorkerRunner mfdRunner, IInspectionWorkerRunner nativeRunner)
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        await using var reads = new PostgresDatabase(test.Case.RuntimeConnectionString);
        await using var reports = new PostgresRevisionReportDatabase(test.Case.ReportConnectionString);
        using var root = new PrivateStorageTestRoot();
        await using var objects = root.Initialize();
        var projects = new PostgresProjectReader(reads);
        var backend = new FerruleInterchangeBackend(mfdRunner);
        var ingestion = new RevisionIngestionService(test.Store, test.Store, projects, new(objects, 4 * 1024 * 1024),
            new IngestionBackendRegistry([FerruleIngestionAdmissionBackend.CreateRegistration(nativeRunner)]));
        await using var host = await CatalogHttpTestSupport.StartAsync(test.Case, test.Actor.PrincipalId.Value,
            test.Project.OrganizationId, configureServices: services =>
            {
                services.AddSingleton(new CatalogInterchangeOptions(true));
                services.AddSingleton<IInterchangeBackendRegistry>(backend);
                services.AddSingleton<IProjectReader>(projects);
                services.AddSingleton<IIngestionWorkflowReader>(test.Store);
                services.AddSingleton(new InterchangeImportService(projects, test.Store, backend, test.Store, ingestion, TimeProvider.System));
                services.AddSingleton(new InterchangeExportService(new PostgresRevisionReportMetadataReader(reports), objects, backend, test.Store, TimeProvider.System));
            }, maximumRequestBodyBytes: 65536);
        var source = await backend.ExportAsync(FerruleServiceTestSupport.Artifact("valid-basic"), "maps/design.mfd", CancellationToken.None);
        Assert.True(source.Package.Files.Count > 1);
        var maximumFiles = source.Package.Files.ToList();
        var remaining = InterchangePackage.MaximumTotalBytes - source.Package.TotalBytes;
        for (var index = 0; remaining > 0; index++)
        {
            var count = Math.Min(InterchangePackage.MaximumFileBytes, remaining);
            maximumFiles.Add(new($"unused-{index}.bin", new byte[count]));
            remaining -= count;
        }
        var maximumPackage = new InterchangePackage(source.Package.DesignPath, maximumFiles);
        Assert.Equal(InterchangePackage.MaximumTotalBytes, maximumPackage.TotalBytes);
        var maximumImport = await backend.ImportAsync(maximumPackage, CancellationToken.None);
        Assert.Equal(maximumPackage.Digest, maximumImport.PackageDigest);
        var context = await CatalogHttpTestSupport.JsonAsync(await host.Client.GetAsync("/api/v1/catalog/context"), HttpStatusCode.OK);
        var token = context.GetProperty("requestToken").GetString()!;
        var projectPath = CatalogHttpTestSupport.ProjectPath(test.Project);
        var designOnly = new InterchangePackage(source.Package.DesignPath,
            source.Package.Files.Where(file => file.Path == source.Package.DesignPath));
        using (var refused = await Send(projectPath + "/mfd-imports/review?name=MissingAsset&backendId=ferrule",
            designOnly.Encode(), CatalogInterchangeParser.MediaType))
            Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.import_reviews"));
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.revision_ingestions"));
        var review = await JsonPost(projectPath + "/mfd-imports/review?name=RealMfd&backendId=ferrule", source.Package.Encode(), CatalogInterchangeParser.MediaType);
        var id = review.GetProperty("reviewId").GetString()!;
        var digest = review.GetProperty("reviewDigest").GetString()!;
        var confirmation = JsonSerializer.SerializeToUtf8Bytes(new { reviewDigest = digest, acknowledgeWarnings = true });
        var committed = await JsonPost(projectPath + $"/mfd-imports/{id}/confirm", confirmation, "application/json", id);
        Assert.Contains(committed.GetProperty("outcome").GetString(), new[] { "committedValid", "committedDecodedInvalid" });
        var receipt = await test.Store.FindOwnReceiptAsync(test.Actor, test.Project, IngestionOperationKind.Create, null, Guid.Parse(id));
        Assert.Equal(source.Package.Digest, receipt!.Facts.Intent.ImportProvenance!.Package.Digest);
        var scope = receipt.Facts.FutureRevision;
        await JsonPost(projectPath + $"/mfd-imports/{id}/confirm", confirmation, "application/json", id);
        var mappingPath = projectPath + $"/mappings/{scope.MappingId:D}";
        var append = await JsonPost(mappingPath + $"/mfd-imports/review?expectedHeadRevisionId={scope.RevisionId:D}", source.Package.Encode(), CatalogInterchangeParser.MediaType);
        var appendId = append.GetProperty("reviewId").GetString()!;
        await JsonPost(projectPath + $"/mfd-imports/{appendId}/confirm", JsonSerializer.SerializeToUtf8Bytes(new
        { reviewDigest = append.GetProperty("reviewDigest").GetString(), acknowledgeWarnings = true }), "application/json", appendId);
        Assert.Equal(2L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.mapping_revisions"));
        Assert.Equal(2L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.revision_import_provenance"));
        var exportPath = CatalogHttpTestSupport.RevisionPath(scope) + "/mfd-exports";
        var exported = await JsonPost(exportPath + "/review", [], null);
        var exportSnapshot = await test.Store.FindExportAsync(test.Actor, scope, Guid.Parse(exported.GetProperty("reviewId").GetString()!));
        Assert.NotNull(exportSnapshot);
        Assert.True(exportSnapshot.Output.Package.Files.Count > 1);
        using var download = await Send(exportPath + $"/{exported.GetProperty("reviewId").GetString()}/download",
            JsonSerializer.SerializeToUtf8Bytes(new { reviewDigest = exported.GetProperty("reviewDigest").GetString(), acknowledgeWarnings = true }), "application/json");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/zip", download.Content.Headers.ContentType!.MediaType);
        var downloaded = await download.Content.ReadAsByteArrayAsync();
        Assert.Equal(Sha256Digest.FromBytes(downloaded).Value, Assert.Single(download.Headers.GetValues("X-Content-Sha256")));
        using (var archive = new System.IO.Compression.ZipArchive(new MemoryStream(downloaded)))
        {
            Assert.Equal(exportSnapshot.Output.Package.Files.Select(file => file.Path), archive.Entries.Select(entry => entry.FullName));
            foreach (var entry in archive.Entries)
            {
                using var stream = entry.Open();
                using var bytes = new MemoryStream();
                await stream.CopyToAsync(bytes);
                Assert.Equal(exportSnapshot.Output.Package.Files.Single(file => file.Path == entry.FullName).Bytes.ToArray(), bytes.ToArray());
            }
        }
        return source.Package.Digest;

        async Task<JsonElement> JsonPost(string path, byte[] bytes, string? media, string? key = null) =>
            await CatalogHttpTestSupport.JsonAsync(await Send(path, bytes, media, key), HttpStatusCode.OK);
        Task<HttpResponseMessage> Send(string path, byte[] bytes, string? media, string? key = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new ByteArrayContent(bytes) };
            if (media is not null) request.Content.Headers.ContentType = new MediaTypeHeaderValue(media);
            request.Headers.Add("Origin", host.Origin.GetLeftPart(UriPartial.Authority));
            request.Headers.Add("X-XSRF-TOKEN", token);
            if (key is not null) request.Headers.Add("Idempotency-Key", key);
            return host.Client.SendAsync(request);
        }
    }

    private sealed class OwnedWorkers : IWorkerProcessLauncher
    {
        private readonly WorkerProcessLauncher inner = new();
        internal ConcurrentQueue<string> Created { get; } = new();
        public IWorkerProcess Start(ProcessStartInfo startInfo)
        {
            var arguments = startInfo.ArgumentList.ToArray();
            if (arguments.Contains("create"))
            {
                var name = Array.IndexOf(arguments, "--name");
                var label = Array.IndexOf(arguments, "--label");
                if (name >= 0 && label >= 0) Created.Enqueue(arguments[name + 1] + " " + arguments[label + 1]);
            }
            return inner.Start(startInfo);
        }
    }
}
