using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Weft.Api.Catalog;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

public sealed partial class MappingDraftPersistenceTests
{
    [PostgresFact]
    public async Task HttpRoutesFenceAuthorEtagsTokensAndClosedSchemaBeforeAnyWorker()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var original = await test.CommitCreateAsync();
        var scope = original.Facts.FutureRevision;
        var mapping = new MappingScope(scope.OrganizationId, scope.ProjectId, scope.MappingId);
        await using var reader = new PostgresMappingDraftDatabase(test.Case.RegressionConnectionString("weft_draft_reader"), MappingDraftDatabaseProfile.Reader);
        await using var writer = new PostgresMappingDraftDatabase(test.Case.RegressionConnectionString("weft_draft_writer"), MappingDraftDatabaseProfile.Writer);
        var store = new PostgresMappingDraftStore(reader, writer);
        var immutableBase = (await store.SelectBaseAsync(test.Actor, mapping, scope.RevisionId, false))!;
        var draft = (await store.CreateAsync(test.Actor, mapping, Guid.NewGuid(), immutableBase, Checkpoint(scope))).Draft!;
        void Configure(IServiceCollection services)
        {
            services.AddSingleton(new CatalogMappingEditOptions(true));
            services.AddSingleton(new MappingDraftService(store, new DraftTestObjects(), new DraftTestBackend(), new DraftTestIngestion()));
        }
        await using var api = await CatalogHttpTestSupport.StartAsync(test.Case, test.Actor.PrincipalId.Value, scope.OrganizationId, configureServices: Configure);
        var path = CatalogHttpTestSupport.MappingPath(scope) + "/drafts/" + draft.Id.ToString("D");
        var context = await api.Client.GetFromJsonAsync<JsonElement>("/api/v1/catalog/context");
        var token = context.GetProperty("requestToken").GetString()!;
        Assert.True(context.GetProperty("features").GetProperty("edit").GetBoolean());
        using var metadata = await api.Client.GetAsync(path);
        var document = await CatalogHttpTestSupport.JsonAsync(metadata);
        Assert.Equal(draft.Id.ToString("D"), document.GetProperty("id").GetString());
        var tag = metadata.Headers.ETag!.ToString();
        Assert.True(metadata.Headers.CacheControl!.NoStore);
        using var source = await api.Client.GetAsync(path + "/base");
        Assert.Equal(PostgresIngestionTestSupport.SyntheticBody, await source.Content.ReadAsByteArrayAsync());

        async Task<HttpResponseMessage> Put(string body, string? match, string? requestToken = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, path + "/operations") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.Add("Origin", api.Origin.GetLeftPart(UriPartial.Authority));
            request.Headers.Add("X-XSRF-TOKEN", requestToken ?? token);
            if (match is not null) request.Headers.TryAddWithoutValidation("If-Match", match);
            return await api.Client.SendAsync(request);
        }
        using var missing = await Put(MappingEditScriptTests.Bind, null);
        Assert.Equal((HttpStatusCode)428, missing.StatusCode);
        using var badToken = await Put(MappingEditScriptTests.Bind, tag, "not-a-request-token");
        Assert.Equal(HttpStatusCode.Forbidden, badToken.StatusCode);
        using var unknown = await Put("{\"format\":\"ferrule.edit.v1\",\"operations\":[],\"native\":{}}", tag);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        using var accepted = await Put(MappingEditScriptTests.Bind, tag);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        using var stale = await Put(MappingEditScriptTests.Bind, tag);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.True(stale.Headers.CacheControl!.NoStore);
        await using var other = await CatalogHttpTestSupport.StartAsync(test.Case, test.Seed.Admitted, scope.OrganizationId, configureServices: Configure);
        using var hidden = await other.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.True(hidden.Headers.CacheControl!.NoStore);
    }

    internal sealed class DraftTestObjects : IPrivateObjectStore
    {
        public Task<byte[]> ReadVerifiedAsync(RevisionScope expectedScope, PrivateObjectReference reference, CancellationToken cancellationToken = default) => Task.FromResult(PostgresIngestionTestSupport.SyntheticBody);
        public Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(RevisionScope expectedScope, PrivateObjectReference reference, Stream source, CancellationToken cancellationToken = default) => throw new InvalidOperationException("This HTTP admission test must not publish.");
    }
    internal sealed class DraftTestBackend : IMappingEditBackend
    {
        public Task<MappingEditResult> ReplayAsync(NativeArtifact artifact, MappingEditScript script, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Read/autosave admission must not invoke a worker.");
    }
    internal sealed class DraftTestIngestion : IRevisionIngestionService
    {
        public Task<RevisionIngestionResult> CreateAsync(ActorContext actor, ProjectScope project, Guid idempotencyKey, CreateRevisionRequest request, Stream nativeSource, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<RevisionIngestionResult> AppendAsync(ActorContext actor, MappingScope target, Guid idempotencyKey, AppendRevisionRequest request, Stream nativeSource, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<RevisionIngestionResult> ResumeAsync(ActorContext actor, ProjectScope project, Guid operationId, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<RevisionIngestionReceipt?> GetOwnReceiptAsync(ActorContext actor, ProjectScope project, Guid operationId, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
}
