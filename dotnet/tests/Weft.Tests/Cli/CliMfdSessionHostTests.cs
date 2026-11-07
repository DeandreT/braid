using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Weft.Api.Catalog;
using Weft.Application;
using Weft.Cli;
using Weft.Contracts.Catalog;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests.Cli;

[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class CliMfdSessionHostTests(PostgresFixture postgres)
{
    [PostgresFact]
    public Task AuthenticatedCliReviewsConfirmsRecoversAppendsAndDownloadsWithCurrentAuthority()
    {
        var backend = new SyntheticBackend();
        return RunAsync(postgres, backend, new([new(backend.Profile, 4 * 1024 * 1024, backend)]));
    }

    // Also exercised by the real Docker/service workflow with its actual conversion and admission backends.
    internal static async Task RunAsync(PostgresFixture postgres, IInterchangeBackend backend, IngestionBackendRegistry native)
    {
        using var root = new PrivateStorageTestRoot();
        await using var objects = root.Initialize();
        var source = await backend.ExportAsync(FerruleServiceTestSupport.Artifact("valid-basic"), "maps/design.mfd", CancellationToken.None);
        await AuthHttpFixture.RunAsync(postgres, async test =>
            {
                var records = new ConcurrentQueue<AuthHttpRecord>();
                using var cli = CliSessionHostTests.Harness(test, records);
                await cli.ConfigureAsync();
                await cli.LoginAsync();
                var project = test.Data.Project.ProjectId.ToString("D");
                var capabilities = Read<CatalogInterchangeCapabilitiesV1>(await cli.RunAsync("mfd", "capabilities", "--json"));
                Assert.True(capabilities.Enabled);
                var command = new List<string> { "mfd", "import", "--project", project, "--name", "CLI imported", "-", "--design-path", source.Package.DesignPath, "--json" };
                foreach (var asset in source.Package.Files.Where(file => file.Path != source.Package.DesignPath))
                {
                    var path = Path.Combine(cli.ConfigurationDirectory, Guid.NewGuid().ToString("N"));
                    await File.WriteAllBytesAsync(path, asset.Bytes.ToArray());
                    command.AddRange(["--asset", asset.Path + "=" + path]);
                }
                var design = source.Package.Files.Single(file => file.Path == source.Package.DesignPath).Bytes.ToArray();
                var initial = await test.Data.Case.ScalarAsync<long>("SELECT count(*) FROM weft.mapping_revisions");
                var review = Read<CatalogInterchangeReviewV1>(await cli.RunWithInputAsync(design, command.ToArray()));
                Assert.Equal(source.Package.Digest.Value, review.PackageDigest);
                Assert.Equal(initial, await test.Data.Case.ScalarAsync<long>("SELECT count(*) FROM weft.mapping_revisions"));
                Assert.Equal(review.ReviewDigest, Read<CatalogInterchangeReviewV1>(await cli.RunAsync("mfd", "review", "--project", project, review.ReviewId, "--json")).ReviewDigest);
                Assert.Equal(CliExitCode.PreconditionFailed, (await cli.RunAsync("mfd", "confirm", "--project", project, review.ReviewId,
                    "--review-digest", new string('0', 64), "--acknowledge-warnings")).Code);
                if (review.Warnings.Count != 0)
                    Assert.Equal(CliExitCode.Usage, (await cli.RunAsync("mfd", "confirm", "--project", project, review.ReviewId, "--review-digest", review.ReviewDigest)).Code);
                var committed = await Confirm(review);
                await Confirm(review);
                Assert.Equal(initial + 1, await test.Data.Case.ScalarAsync<long>("SELECT count(*) FROM weft.mapping_revisions"));
                var revision = committed.Receipt!.FutureRevision!;
                var receipt = await test.Data.Ingestion.FindOwnReceiptAsync(test.Data.Actor, test.Data.Project, IngestionOperationKind.Create, null, Guid.Parse(review.ReviewId));
                Assert.Equal(committed.Receipt.OperationId, receipt!.Facts.OperationId.ToString("D"));
                Assert.Equal(source.Package.Encode(), receipt!.Facts.Intent.ImportProvenance!.Package.Encode());
                command.RemoveRange(4, 2); // The append selects an exact head instead of a new mapping name.
                command.AddRange(["--mapping", revision.MappingId, "--expected-head", revision.RevisionId]);
                var append = Read<CatalogInterchangeReviewV1>(await cli.RunWithInputAsync(design, command.ToArray()));
                Assert.Equal(revision.RevisionId, append.ExpectedHeadRevisionId);
                await Confirm(append);
                Assert.Equal(initial + 2, await test.Data.Case.ScalarAsync<long>("SELECT count(*) FROM weft.mapping_revisions"));
                Assert.Equal(2L, await test.Data.Case.ScalarAsync<long>("SELECT count(*) FROM weft.revision_import_provenance"));
                await test.Data.Case.ExecuteAsync("UPDATE weft.project_memberships SET role='viewer',version=version+1 WHERE principal_id=$1", test.Data.Actor.PrincipalId.Value);
                var exported = Read<CatalogInterchangeReviewV1>(await cli.RunAsync("mfd", "export", "--project", project,
                    "--mapping", revision.MappingId, "--revision", revision.RevisionId, "--json"));
                var scope = new RevisionScope(test.Data.Project.OrganizationId, test.Data.Project.ProjectId, Guid.Parse(revision.MappingId), Guid.Parse(revision.RevisionId));
                var snapshot = await test.Data.Ingestion.FindExportAsync(test.Data.Actor, scope, Guid.Parse(exported.ReviewId));
                Assert.NotNull(snapshot);
                var download = new[] { "mfd", "download", "--project", project, "--mapping", revision.MappingId, "--revision", revision.RevisionId,
                    exported.ReviewId, "--review-digest", exported.ReviewDigest, "--acknowledge-warnings", "--output", "-" };
                var downloaded = await cli.RunAsync(download);
                Assert.True(downloaded.Exit == 0, downloaded.ToString());
                Assert.Equal(new ExportDownload(snapshot.Output.Package).CopyBytes(), downloaded.Binary);
                Assert.Equal(CliExitCode.NotFound, (await cli.RunAsync("mfd", "review", "--project", project, review.ReviewId)).Code);
                await test.Data.Case.ExecuteAsync("UPDATE weft.project_memberships SET active=false,version=version+1 WHERE principal_id=$1", test.Data.Actor.PrincipalId.Value);
                var denied = await cli.RunAsync(download);
                Assert.Equal(CliExitCode.NotFound, denied.Code);
                Assert.Empty(denied.Binary);
                Assert.All(records, record => Assert.False(record.Unexpected));
                Assert.True(records.Sum(record => record.Audits) >= 6);
                Assert.Equal(1, test.Issuer.PublicDeviceGrants);

                async Task<CatalogRevisionIngestionResultV1> Confirm(CatalogInterchangeReviewV1 item) => Read<CatalogRevisionIngestionResultV1>(
                    await cli.RunAsync("mfd", "confirm", "--project", project, item.ReviewId, "--review-digest", item.ReviewDigest, "--acknowledge-warnings", "--wait", "--json"));
            }, bearerBurst: 100, configureCatalogServices: (test, services) =>
            {
                var registry = new Registry(backend);
                services.AddSingleton(_ => new PostgresDatabase(test.Data.Case.RuntimeConnectionString));
                services.AddSingleton(_ => new PostgresRevisionReportDatabase(test.Data.Case.ReportConnectionString));
                services.AddSingleton<IProjectReader>(provider => new PostgresProjectReader(provider.GetRequiredService<PostgresDatabase>()));
                services.AddSingleton(new CatalogInterchangeOptions(true));
                services.AddSingleton<IInterchangeBackendRegistry>(registry);
                services.AddSingleton<IIngestionWorkflowReader>(test.Data.Ingestion);
                services.AddSingleton(provider =>
                {
                    var projects = provider.GetRequiredService<IProjectReader>();
                    var ingestion = new RevisionIngestionService(test.Data.Ingestion, test.Data.Ingestion, projects, new(objects, 4 * 1024 * 1024), native);
                    return new InterchangeImportService(projects, test.Data.Ingestion, registry, test.Data.Ingestion, ingestion, TimeProvider.System);
                });
                services.AddSingleton(provider => new InterchangeExportService(
                    new PostgresRevisionReportMetadataReader(provider.GetRequiredService<PostgresRevisionReportDatabase>()),
                    objects, registry, test.Data.Ingestion, TimeProvider.System));
            });
    }

    private static T Read<T>(CliResult result)
    {
        AuthHttpFixture.Check(result.Exit == 0, $"The MFD CLI expected {typeof(T).Name} but returned {result.Code}.");
        return JsonSerializer.Deserialize<T>(result.Out, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } })!;
    }

    private sealed class Registry(IInterchangeBackend backend) : IInterchangeBackendRegistry
    {
        public IInterchangeBackend? GetCurrent(string backendId) => backend.Profile.BackendId == backendId ? backend : null;
    }

    private sealed class SyntheticBackend : IInterchangeBackend, IIngestionAdmissionBackend
    {
        public IngestionBackendProfile Profile => PostgresIngestionTestSupport.Profile;
        public Task<InterchangeImportOutput> ImportAsync(InterchangePackage package, CancellationToken cancellationToken) =>
            Task.FromResult(new InterchangeImportOutput(Profile, package.Digest, "synthetic-native"u8, new("Synthetic mapping", [new("unsupported", "Review this warning")])));
        public Task<InterchangeExportOutput> ExportAsync(NativeArtifact artifact, string designPath, CancellationToken cancellationToken) =>
            Task.FromResult(new InterchangeExportOutput(Profile, artifact.Digest, new(designPath, [new(designPath, "<self-authored/>"u8), new("maps/schema.json", "{}"u8)]),
                new("Synthetic export", [new("lossy", "Review this warning")])));
        public Task<IngestionAttemptCompletion> ValidateAsync(NativeArtifact artifact, CancellationToken cancellationToken = default)
        {
            using var diagnostics = JsonDocument.Parse("[]");
            return Task.FromResult(IngestionAttemptCompletion.FromValidation(IngestionEvidenceDocument.Create(
                new(IngestionBackendStatus.Succeeded, new(artifact.Scope, artifact.Digest, Profile, true), true, 0, 0, false), diagnostics.RootElement)));
        }
    }
}
