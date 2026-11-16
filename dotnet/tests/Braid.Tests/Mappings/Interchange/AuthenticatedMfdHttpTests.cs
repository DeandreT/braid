using System.Net;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Braid.Api.Catalog;
using Braid.Application;
using Braid.Contracts.Catalog;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class AuthenticatedMfdHttpTests(PostgresFixture postgres)
{
    [PostgresFact]
    public Task BearerHttpReviewsConfirmsReplaysAppendsAndDownloadsWithCurrentAuthority()
    {
        var backend = new SyntheticBackend();
        return RunAsync(postgres, backend, new([new(backend.Profile, 4 * 1024 * 1024, backend)]));
    }

    // Also qualifies the real conversion and admission backends through authenticated HTTP.
    internal static async Task RunAsync(PostgresFixture postgres, IInterchangeBackend backend, IngestionBackendRegistry native)
    {
        using var root = new PrivateStorageTestRoot();
        await using var objects = root.Initialize();
        var source = await backend.ExportAsync(FerruleServiceTestSupport.Artifact("valid-basic"), "maps/design.mfd", CancellationToken.None);
        await AuthHttpFixture.RunAsync(postgres, async test =>
        {
            var token = test.Crypto.SignClaims(UserTokenValidationTests.Claims().ToJsonString());
            var records = new List<AuthHttpRecord>();
            var context = (await Send(AuthHttpFixture.ContextPath)).Json();
            Assert.Equal("bearer", context.GetProperty("mode").GetString());
            Assert.Equal(test.Data.Actor.PrincipalId.Value.ToString("D"), context.GetProperty("actorPrincipalId").GetString());
            Assert.True(Read<CatalogInterchangeCapabilitiesV1>(await Send("/api/v1/catalog/interchange")).Enabled);
            var projectPath = CatalogHttpTestSupport.ProjectPath(test.Data.Project);
            var reviewPath = projectPath + $"/mfd-imports/review?name=HTTP%20imported&backendId={Uri.EscapeDataString(backend.Profile.BackendId)}";
            var package = source.Package.Encode();
            var initial = await test.Data.Case.ScalarAsync<long>("SELECT count(*) FROM braid.mapping_revisions");
            var review = Read<CatalogInterchangeReviewV1>(await Send(reviewPath, package, CatalogInterchangeParser.MediaType));
            Assert.Equal(source.Package.Digest.Value, review.PackageDigest);
            Assert.Equal(initial, await test.Data.Case.ScalarAsync<long>("SELECT count(*) FROM braid.mapping_revisions"));
            var savedReviewPath = projectPath + $"/mfd-imports/{review.ReviewId}";
            Assert.Equal(review.ReviewDigest, Read<CatalogInterchangeReviewV1>(await Send(savedReviewPath)).ReviewDigest);
            var confirmPath = savedReviewPath + "/confirm";
            await Send(confirmPath, Confirmation(new string('0', 64)), "application/json", review.ReviewId, HttpStatusCode.PreconditionFailed);
            if (review.Warnings.Count != 0)
                await Send(confirmPath, Confirmation(review.ReviewDigest, false), "application/json", review.ReviewId, HttpStatusCode.PreconditionFailed);
            Assert.Equal(initial, await test.Data.Case.ScalarAsync<long>("SELECT count(*) FROM braid.mapping_revisions"));
            var committed = await Confirm(review);
            var committedReceipt = Assert.IsType<CatalogRevisionIngestionReceiptV1>(committed.Receipt);
            Assert.Equal(committedReceipt.OperationId, (await Confirm(review)).Receipt!.OperationId);
            Assert.Equal(initial + 1, await test.Data.Case.ScalarAsync<long>("SELECT count(*) FROM braid.mapping_revisions"));
            var revision = committedReceipt.FutureRevision;
            var receipt = await test.Data.Ingestion.FindOwnReceiptAsync(test.Data.Actor, test.Data.Project, IngestionOperationKind.Create, null, Guid.Parse(review.ReviewId));
            Assert.NotNull(receipt);
            Assert.Equal(committedReceipt.OperationId, receipt.Facts.OperationId.ToString("D"));
            Assert.Equal(package, receipt.Facts.Intent.ImportProvenance!.Package.Encode());
            var mappingPath = projectPath + $"/mappings/{revision.MappingId}";
            var append = Read<CatalogInterchangeReviewV1>(await Send(mappingPath + $"/mfd-imports/review?expectedHeadRevisionId={revision.RevisionId}",
                package, CatalogInterchangeParser.MediaType));
            Assert.Equal(revision.MappingId, append.TargetMappingId);
            Assert.Equal(revision.RevisionId, append.ExpectedHeadRevisionId);
            var appended = await Confirm(append);
            Assert.Equal(appended.Receipt!.OperationId, (await Confirm(append)).Receipt!.OperationId);
            Assert.Equal(initial + 2, await test.Data.Case.ScalarAsync<long>("SELECT count(*) FROM braid.mapping_revisions"));
            Assert.Equal(2L, await test.Data.Case.ScalarAsync<long>("SELECT count(*) FROM braid.revision_import_provenance"));
            await test.Data.Case.ExecuteAsync("UPDATE braid.project_memberships SET role='viewer',version=version+1 WHERE principal_id=$1", test.Data.Actor.PrincipalId.Value);
            await Send(savedReviewPath, expected: HttpStatusCode.NotFound);
            await Send(confirmPath, Confirmation(review.ReviewDigest), "application/json", review.ReviewId, HttpStatusCode.NotFound);
            var deniedImport = await Send(reviewPath, package, CatalogInterchangeParser.MediaType, expected: HttpStatusCode.NotFound);
            Assert.Equal(0, deniedImport.Record.BodyReads);
            var scope = new RevisionScope(test.Data.Project.OrganizationId, test.Data.Project.ProjectId, Guid.Parse(revision.MappingId), Guid.Parse(revision.RevisionId));
            var exportPath = CatalogHttpTestSupport.RevisionPath(scope) + "/mfd-exports";
            var exported = Read<CatalogInterchangeReviewV1>(await Send(exportPath + "/review", []));
            var snapshot = await test.Data.Ingestion.FindExportAsync(test.Data.Actor, scope, Guid.Parse(exported.ReviewId));
            Assert.NotNull(snapshot);
            var downloadPath = exportPath + $"/{exported.ReviewId}/download";
            if (exported.Warnings.Count != 0)
                await Send(downloadPath, Confirmation(exported.ReviewDigest, false), "application/json", expected: HttpStatusCode.PreconditionFailed);
            var downloaded = await Send(downloadPath, Confirmation(exported.ReviewDigest), "application/json");
            Assert.Equal("application/zip", Assert.Single(downloaded.Headers["Content-Type"]));
            Assert.Equal(new ExportDownload(snapshot.Output.Package).CopyBytes(), downloaded.Body);
            Assert.Equal(Sha256Digest.FromBytes(downloaded.Body).Value, Assert.Single(downloaded.Headers["X-Content-Sha256"]));
            await test.Data.Case.ExecuteAsync("UPDATE braid.project_memberships SET active=false,version=version+1 WHERE principal_id=$1", test.Data.Actor.PrincipalId.Value);
            var denied = await Send(downloadPath, Confirmation(exported.ReviewDigest), "application/json", expected: HttpStatusCode.NotFound);
            Assert.Equal("notFound", denied.Json().GetProperty("code").GetString());
            Assert.False(denied.Headers.ContainsKey("Content-Disposition"));
            Assert.False(denied.Headers.ContainsKey("X-Content-Sha256"));
            Assert.NotEqual(downloaded.Body, denied.Body);
            Assert.Equal(initial + 2, await test.Data.Case.ScalarAsync<long>("SELECT count(*) FROM braid.mapping_revisions"));
            Assert.All(records, record => Assert.False(record.Unexpected));
            Assert.True(records.Sum(record => record.Audits) >= 6);

            async Task<CatalogRevisionIngestionResultV1> Confirm(CatalogInterchangeReviewV1 item)
            {
                var result = Read<CatalogRevisionIngestionResultV1>(await Send(projectPath + $"/mfd-imports/{item.ReviewId}/confirm",
                    Confirmation(item.ReviewDigest), "application/json", item.ReviewId));
                Assert.Contains(result.Outcome, new[] { CatalogRevisionIngestionOutcomeV1.CommittedValid, CatalogRevisionIngestionOutcomeV1.CommittedDecodedInvalid });
                return result;
            }

            async Task<AuthHttpReply> Send(string path, byte[]? bytes = null, string? media = null, string? key = null,
                HttpStatusCode expected = HttpStatusCode.OK)
            {
                var reply = await test.SendAsync(path, method: bytes is null ? "GET" : "POST", mutate: message =>
                {
                    message.Headers.Authorization = new("Bearer", token);
                    if (bytes is not null) message.Content = new ByteArrayContent(bytes);
                    if (media is not null) message.Content!.Headers.ContentType = new(media);
                    if (key is not null) message.Headers.Add("Idempotency-Key", key);
                });
                records.Add(reply.Record);
                Assert.Equal(expected, reply.Status);
                Assert.Equal(1, reply.Record.Resolves);
                Assert.Equal(bytes is null ? 0 : 1, reply.Record.Audits);
                Assert.Equal(0, reply.Record.SessionReads);
                Assert.False(reply.Headers.ContainsKey("Set-Cookie"));
                return reply;
            }
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

    private static byte[] Confirmation(string digest, bool acknowledgeWarnings = true) =>
        JsonSerializer.SerializeToUtf8Bytes(new { reviewDigest = digest, acknowledgeWarnings });

    private static T Read<T>(AuthHttpReply reply) => JsonSerializer.Deserialize<T>(reply.Body,
        new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } })!;

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
