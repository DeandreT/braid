using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Weft.Api;
using Weft.Api.Catalog;
using Weft.Application;
using Weft.Contracts.Operations;
using Weft.Contracts.Projections;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

/// <summary>Actual HTTP host and catalog revisions; retained reports come from an authored reader port.</summary>
[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class CatalogRevisionComparisonHttpTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Start = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);

    [PostgresFact]
    public async Task TwoRetainedRevisionsCompareThroughExactAnchorsAndCiteBothDerivations()
    {
        await using var ingestion = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var first = await CatalogHttpTestSupport.CommitSyntheticAsync(ingestion);
        var second = await CatalogHttpTestSupport.CommitSyntheticAsync(ingestion, PostgresIngestionTestSupport.AppendIntent(first));
        await using var host = await Host.StartAsync(ingestion);
        var left = await host.SummaryAsync(ingestion.Actor, first.Facts.FutureRevision);
        var right = await host.SummaryAsync(ingestion.Actor, second.Facts.FutureRevision);
        var leftId = Guid.NewGuid();
        var rightId = Guid.NewGuid();
        host.Reports.Add(left, leftId, ingestion.Actor.PrincipalId,
            Parameter("p1", "source_code", FieldRequirement.Optional), Parameter("p2", "legacy_code", FieldRequirement.Optional));
        host.Reports.Add(right, rightId, ingestion.Actor.PrincipalId,
            Parameter("q1", "source_code", FieldRequirement.Required));

        using (var response = await host.GetAsync(left.Scope, Query(left.Scope, leftId, left.Scope, leftId)))
        {
            var body = await CatalogHttpTestSupport.JsonAsync(response);
            Assert.True(response.Headers.CacheControl?.NoStore);
            Assert.Null(response.Headers.ETag);
            Assert.False(body.GetProperty("hasObservedDifferences").GetBoolean());
            Assert.All(body.GetProperty("items").EnumerateArray(), item => Assert.Equal("matched", item.GetProperty("match").GetString()));
        }

        using (var response = await host.GetAsync(left.Scope, Query(left.Scope, leftId, right.Scope, rightId)))
        {
            var body = await CatalogHttpTestSupport.JsonAsync(response);
            Assert.True(body.GetProperty("hasObservedDifferences").GetBoolean());
            Assert.False(body.GetProperty("validationIncluded").GetBoolean());
            Cites(body.GetProperty("left"), left.Scope, leftId);
            Cites(body.GetProperty("right"), right.Scope, rightId);
            var parameters = body.GetProperty("items").EnumerateArray()
                .Where(item => item.GetProperty("entity").GetString() == "parameter").ToArray();
            // Different report-local IDs still pair through the exact parameter name.
            var paired = Assert.Single(parameters, item => item.GetProperty("match").GetString() == "matched");
            Assert.Contains(paired.GetProperty("facets").EnumerateArray(), facet =>
                facet.GetProperty("aspect").GetString() == "requirement" && facet.GetProperty("hasObservedDifference").GetBoolean());
            Assert.Single(parameters, item => item.GetProperty("match").GetString() == "onlyLeft");
        }

        // A derivation belongs to its own revision; it cannot be cited for another one.
        using (var response = await host.GetAsync(left.Scope, Query(left.Scope, rightId, right.Scope, rightId)))
            await CatalogRevisionReportHttpTestSupport.ErrorAsync(response, HttpStatusCode.NotFound, "notFound");

        Assert.Equal(0, host.Reports.CurrentReads);
        Assert.Equal(5, host.Reports.RetainedReads);
    }

    private static void Cites(JsonElement citation, RevisionScope scope, Guid derivationId)
    {
        Assert.Equal(scope.RevisionId.ToString("D"), citation.GetProperty("revision").GetProperty("revisionId").GetString());
        Assert.Equal(derivationId.ToString("D"), citation.GetProperty("projection").GetProperty("derivationId").GetString());
        Assert.Equal(JsonValueKind.Null, citation.GetProperty("validation").ValueKind);
    }

    private static string Query(RevisionScope left, Guid leftProjection, RevisionScope right, Guid rightProjection) =>
        $"?leftRevision={left.RevisionId:D}&leftProjection={leftProjection:D}&rightRevision={right.RevisionId:D}&rightProjection={rightProjection:D}";

    private static ProjectionParameter Parameter(string id, string name, FieldRequirement requirement) =>
        new(id, name, new(FieldDataTypeKind.Scalar, [ScalarDataType.String]), requirement, null,
            new JsonPointerNativeLocator(IngestionIntent.EntryDocument, "/parameters/" + id));

    private sealed class Reports : IRevisionReportReader
    {
        private readonly ConcurrentDictionary<(RevisionScope, Guid), (AuthorizedRevisionReportSource, VerifiedRevisionReport)> reports = new();
        private int currentReads, retainedReads;
        internal int CurrentReads => Volatile.Read(ref currentReads);
        internal int RetainedReads => Volatile.Read(ref retainedReads);

        internal void Add(CatalogRevisionSummary revision, Guid derivationId, PrincipalId actor, params ProjectionParameter[] parameters)
        {
            var scope = revision.Scope;
            var source = new RevisionReportSource(scope, new(PrivateObjectId.Restore(scope, Guid.NewGuid(), PrivateObjectClass.NativeArtifact),
                revision.NativeDigest, revision.NativeByteCount), revision.LogicalDocument, "fixed-dependencies", revision.BackendId);
            var profile = new RevisionReportBackendProfile(revision.BackendId, "v1", "build", "adapter");
            var key = new ProjectionCacheKey(source, profile, "weft.mapping-projection.v1", "neutral.projection.v1",
                "weft.revision-report-codec.v1", new());
            var wire = new ScopedRevisionId(scope.OrganizationId.ToString("D"), scope.ProjectId.ToString("D"),
                scope.MappingId.ToString("D"), scope.RevisionId.ToString("D"));
            var digest = new ArtifactDigest("sha256", revision.NativeDigest.Value);
            var backend = new BackendIdentity(profile.BackendId, profile.BackendRevision, profile.BackendBuild);
            var report = new ProjectionReportV1(OperationStatusV1.Succeeded, new(wire, digest, backend, profile.AdapterBuild, true),
                new(new(wire, digest, backend, profile.AdapterBuild, key.AlgorithmVersion), new(CoverageStatus.Complete, []),
                    [], [], parameters, [], [], [], [], []), []);
            var observation = new RevisionReportObservation(key.Operation, RevisionReportBackendStatus.Succeeded,
                new(scope, key.NativeDigest, profile, true), new(RevisionReportCoverage.Complete, parameters.Length, 0, 0, 0), null);
            var result = new RevisionReportResult(derivationId, Guid.NewGuid(), key, 1, actor, Start, Start.AddSeconds(2),
                new(PrivateObjectId.Restore(scope, Guid.NewGuid(), PrivateObjectClass.DerivedArtifact),
                    Sha256Digest.Parse(new string('2', 64)), 1), observation);
            reports[(scope, derivationId)] = (new(source, actor, Start.AddSeconds(3)),
                VerifiedProjectionReport.Create(result, report, new()));
        }

        public Task<RevisionReportReadResult> GetCurrentAsync(ActorContext actor, RevisionScope scope, RevisionReportOperation operation,
            Guid? projectionDerivationId = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref currentReads);
            throw new InvalidOperationException("Comparison must read retained reports only.");
        }

        public Task<RevisionReportReadResult> GetRetainedAsync(ActorContext actor, RevisionScope scope, RevisionReportOperation operation,
            Guid derivationId, Guid? projectionDerivationId = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref retainedReads);
            return Task.FromResult(operation == RevisionReportOperation.Projection && reports.TryGetValue((scope, derivationId), out var stored)
                ? RevisionReportReadResult.ForAvailable(stored.Item1, stored.Item2)
                : RevisionReportReadResult.WithoutData(RevisionReportReadOutcome.NotAuthorizedOrNotFound));
        }

        public override string ToString() => "Authored retained report reader.";
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly WebApplication app;
        private readonly PostgresCatalogDatabase database;
        private readonly HttpClient client;
        internal Reports Reports { get; }

        private Host(WebApplication app, PostgresCatalogDatabase database, HttpClient client, Reports reports)
        { this.app = app; this.database = database; this.client = client; Reports = reports; }

        internal static async Task<Host> StartAsync(PostgresIngestionTestSupport ingestion)
        {
            var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            var origin = new Uri($"http://127.0.0.1:{((IPEndPoint)reservation.LocalEndpoint).Port}");
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(server =>
                server.Listen(IPAddress.Loopback, origin.Port, listen => listen.Protocols = HttpProtocols.Http1));
            builder.Services.AddWeftApi();
            builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            builder.Services.AddWeftCatalog(new(true, true, ingestion.Actor.PrincipalId.Value, ingestion.Project.OrganizationId,
                    origin.GetLeftPart(UriPartial.Authority)), builder.Environment,
                new CatalogMetadataEditOptions(), new CatalogRevisionIngestionOptions(), new CatalogProjectCreationOptions(),
                new CatalogRevisionReportOptions(true, false, null));
            var database = new PostgresCatalogDatabase(ingestion.Case.CatalogConnectionString);
            var reports = new Reports();
            builder.Services.AddSingleton<ICatalogReader>(new PostgresCatalogReader(database));
            builder.Services.AddSingleton<IRevisionReportReader>(reports);
            var app = builder.Build();
            app.MapWeftCatalog();
            reservation.Stop();
            await app.StartAsync();
            return new(app, database, new HttpClient { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(30) }, reports);
        }

        internal async Task<CatalogRevisionSummary> SummaryAsync(ActorContext actor, RevisionScope scope) =>
            Assert.IsType<CatalogRevision>(await new PostgresCatalogReader(database).GetRevisionAsync(actor, scope)).Summary;

        internal Task<HttpResponseMessage> GetAsync(RevisionScope mapping, string query) => client.GetAsync(
            $"/api/v1/organizations/{mapping.OrganizationId:D}/projects/{mapping.ProjectId:D}/mappings/{mapping.MappingId:D}/comparisons{query}");

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await app.DisposeAsync();
            await database.DisposeAsync();
        }
    }
}
