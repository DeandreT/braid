using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Weft.Ai;
using Weft.Api;
using Weft.Api.Catalog;
using Weft.Api.Chat;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

// Real HTTP, antiforgery, restricted proposal pools and SQL ingestion transitions.
// The ingestion adapter asserts exact bytes and supplies synthetic admission evidence; no native worker is qualified here.
[Collection(PostgresCollection.Name)]
public sealed class PostgresChatApprovalHttpTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task Exact_HTTP_approval_is_required_and_recovery_cannot_first_reserve()
    {
        await using var f = await HttpFixture.StartAsync(fixture);
        var p = f.Proposal;
        await f.AssertCountsAsync(0, 0, 0);
        using (var read = await f.Client.GetAsync(f.Path))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            Assert.Equal("awaitingApproval", (await JsonAsync(read)).GetProperty("status").GetString());
        }
        using (var operations = await f.Client.GetAsync(f.Path + "/operations?expectedFingerprint=" + p.Fingerprint.Value))
        {
            Assert.Equal(HttpStatusCode.OK, operations.StatusCode);
            Assert.Equal(Encoding.UTF8.GetString(p.Content.Operations.CopyUtf8()),
                (await JsonAsync(operations)).GetProperty("canonicalScript").GetString());
        }
        using (var recover = await f.PostAsync("recover"))
        {
            Assert.Equal(HttpStatusCode.OK, recover.StatusCode);
            Assert.Equal("awaitingApproval", (await JsonAsync(recover)).GetProperty("status").GetString());
        }
        using (var missing = await f.PostAsync("approve", csrf: false)) Assert.Equal(HttpStatusCode.Forbidden, missing.StatusCode);
        using (var foreign = await f.PostAsync("approve", origin: "https://foreign.invalid")) Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        using (var mismatch = await f.PostAsync("approve", fingerprint: new string('0', 64))) Assert.Equal(HttpStatusCode.Conflict, mismatch.StatusCode);
        await f.AssertCountsAsync(0, 0, 0);
        Assert.Equal(0, f.Ingestion.Appends); Assert.Equal(0, f.Ingestion.Resumes);

        // A committed approval with a lost acknowledgement is not permission for recovery to first-reserve.
        Assert.Equal(ChatMappingEditStatus.Approved, (await f.Source.ApproveAsync())!.Status);
        using (var gap = await f.PostAsync("recover"))
        {
            Assert.Equal(HttpStatusCode.OK, gap.StatusCode);
            Assert.Equal("approved", (await JsonAsync(gap)).GetProperty("status").GetString());
        }
        await f.AssertCountsAsync(1, 0, 0);
        Assert.Equal(0, f.Ingestion.Appends); Assert.Equal(0, f.Ingestion.Resumes);

        JsonElement saved;
        using (var approved = await f.PostAsync("approve"))
        {
            Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
            saved = await JsonAsync(approved);
            Assert.Equal("saved", saved.GetProperty("status").GetString());
            Assert.Equal(2, saved.GetProperty("revisionSequence").GetInt64());
        }
        using (var replay = await f.PostAsync("approve"))
        {
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            Assert.Equal(saved.GetRawText(), (await JsonAsync(replay)).GetRawText());
        }
        using (var recover = await f.PostAsync("recover"))
        {
            Assert.Equal(HttpStatusCode.OK, recover.StatusCode);
            Assert.Equal(saved.GetRawText(), (await JsonAsync(recover)).GetRawText());
        }
        using (var reload = await f.Client.GetAsync(f.Path))
            Assert.Equal(saved.GetRawText(), (await JsonAsync(reload)).GetRawText());
        await f.AssertCountsAsync(1, 1, 1);
        Assert.Equal(1, f.Ingestion.Appends); Assert.Equal(0, f.Ingestion.Resumes);
        Assert.Equal(2L, await f.Source.Test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.mapping_revisions"));
    }

    [PostgresFact]
    public async Task HTTP_recovery_resumes_only_the_existing_exact_reservation_without_resupplying_bytes()
    {
        await using var f = await HttpFixture.StartAsync(fixture);
        f.Ingestion.HoldReady = true;
        using (var approve = await f.PostAsync("approve"))
        {
            Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
            Assert.Equal("pending", (await JsonAsync(approve)).GetProperty("status").GetString());
        }
        await f.AssertCountsAsync(1, 1, 0);
        using (var recover = await f.PostAsync("recover"))
        {
            Assert.Equal(HttpStatusCode.OK, recover.StatusCode);
            Assert.Equal("saved", (await JsonAsync(recover)).GetProperty("status").GetString());
        }
        await f.AssertCountsAsync(1, 1, 1);
        Assert.Equal(1, f.Ingestion.Appends); Assert.Equal(1, f.Ingestion.Resumes);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        return json.RootElement.Clone();
    }

    private sealed class HttpFixture(PostgresChatEditProposalTests.ProposalFixture source, PostgresChatDatabase reader,
        PostgresChatDatabase writer, PostgresChatDatabase approver, PostgresCatalogDatabase catalog, WebApplication app,
        HttpClient client, SqlIngestion ingestion, string token) : IAsyncDisposable
    {
        internal PostgresChatEditProposalTests.ProposalFixture Source => source;
        internal ChatMappingEditProposal Proposal => source.Proposal;
        internal HttpClient Client => client;
        internal SqlIngestion Ingestion => ingestion;
        internal string Path => $"/api/v1/organizations/{Proposal.Conversation.OrganizationId:D}/chat/conversations/{Proposal.Conversation.ConversationId:D}/turns/{Proposal.TurnId:D}/mapping-edit-proposals/{Proposal.ProposalId:D}";
        internal async Task<HttpResponseMessage> PostAsync(string action, bool csrf = true, string? fingerprint = null, string? origin = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Path + "/" + action)
            { Content = new StringContent(JsonSerializer.Serialize(new { expectedFingerprint = fingerprint ?? Proposal.Fingerprint.Value }), Encoding.UTF8, "application/json") };
            request.Headers.Add("Origin", origin ?? client.BaseAddress!.GetLeftPart(UriPartial.Authority));
            if (csrf) request.Headers.Add("X-XSRF-TOKEN", token);
            return await client.SendAsync(request);
        }
        internal async Task AssertCountsAsync(long approvals, long reservations, long commits)
        {
            Assert.Equal(approvals, await source.Test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.chat_edit_approvals"));
            Assert.Equal(approvals, await source.Test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.chat_mapping_write_intents"));
            Assert.Equal(reservations, await source.Test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.revision_ingestions WHERE operation_kind='append'"));
            Assert.Equal(commits, await source.Test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.chat_mapping_write_commits"));
        }
        internal static async Task<HttpFixture> StartAsync(PostgresFixture fixture)
        {
            var source = await PostgresChatEditProposalTests.ProposalFixture.CreateAsync(fixture);
            PostgresChatDatabase? reader = null, writer = null, approver = null;
            PostgresCatalogDatabase? catalog = null;
            WebApplication? app = null;
            HttpClient? client = null;
            try
            {
                var p = source.Proposal;
                Assert.Equal(ChatMappingEditCreateOutcome.Created, (await source.Writer.CreateAsync(source.Test.Actor, p, default)).Outcome);
                var turn = (await source.Writer.ReadTurnAsync(source.Test.Actor, p.Conversation, p.TurnId))!;
                Assert.Equal(ChatStoreOutcome.Applied, (await source.Writer.FinishAsync(source.Test.Actor, p.Conversation, p.TurnId, turn.Version, ChatTurnPhase.Cancelled)).Outcome);
                reader = new(source.Test.Case.ChatReaderConnectionString, ChatDatabaseProfile.Reader);
                writer = new(source.Test.Case.ChatWriterConnectionString, ChatDatabaseProfile.Writer);
                approver = new(fixture.ChatApproverConnectionString(source.Test.Case), ChatDatabaseProfile.Approver);
                catalog = new(source.Test.Case.CatalogConnectionString);
                var catalogReader = new PostgresCatalogReader(catalog);
                var store = new PostgresChatEditProposalStore(reader, writer, approver);
                var ingestion = new SqlIngestion(source.Test, p);
                using var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var origin = new Uri($"http://127.0.0.1:{port}");
                var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Development" });
                builder.Configuration.Sources.Clear(); builder.Logging.ClearProviders();
                builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, port));
                builder.Services.AddWeftApi();
                builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
                builder.Services.AddWeftCatalog(new(true, true, source.Test.Actor.PrincipalId.Value, p.Conversation.OrganizationId,
                    origin.GetLeftPart(UriPartial.Authority)), builder.Environment);
                builder.Services.AddSingleton<ICatalogReader>(catalogReader);
                builder.Services.AddSingleton<IProjectReader>(catalogReader);
                builder.Services.AddSingleton<IRevisionIngestionService>(ingestion);
                builder.Services.AddSingleton(new CatalogMappingEditOptions(true));
                builder.Services.AddSingleton(new ChatPersistencePorts(new PostgresChatStore(reader), new PostgresChatStore(reader),
                    new PostgresChatStore(writer), new PostgresChatStore(writer)));
                builder.Services.AddSingleton<IChatMappingEditProposalStore>(store);
                builder.Services.AddSingleton(new ChatMappingEditApprovalService(store, catalogReader, ingestion));
                builder.Services.AddSingleton<IAiCredentialResolver>(new NoCredentials());
                var profile = AiProfileSpec.Create(AiProfileKey.Create("approval-fixture", 1), AiDialect.ChatCompletionsSseV1,
                    "fixture-model", new Uri("https://fixture.invalid/v1/chat/completions"), false, AiAuthentication.None, null,
                    AiCapability.Text, "mapping-metadata", 1, true, false);
                builder.Services.AddWeftChat(new([new("Fixture", "fixture", profile)], TimeSpan.FromDays(1), TimeSpan.FromSeconds(60), 128));
                app = builder.Build(); app.MapWeftCatalog(); app.MapWeftChat();
                listener.Stop(); await app.StartAsync();
                client = new(new HttpClientHandler { UseProxy = false, UseCookies = true, AllowAutoRedirect = false }) { BaseAddress = origin };
                using var context = await client.GetAsync("/api/v1/catalog/context");
                Assert.Equal(HttpStatusCode.OK, context.StatusCode);
                var token = (await JsonAsync(context)).GetProperty("requestToken").GetString()!;
                return new(source, reader, writer, approver, catalog, app, client, ingestion, token);
            }
            catch
            {
                client?.Dispose();
                if (app is not null) await app.DisposeAsync();
                if (catalog is not null) await catalog.DisposeAsync();
                if (reader is not null) await reader.DisposeAsync();
                if (writer is not null) await writer.DisposeAsync();
                if (approver is not null) await approver.DisposeAsync();
                await source.DisposeAsync(); throw;
            }
        }
        public async ValueTask DisposeAsync()
        {
            client.Dispose(); await app.DisposeAsync(); await catalog.DisposeAsync();
            await reader.DisposeAsync(); await writer.DisposeAsync(); await approver.DisposeAsync(); await source.DisposeAsync();
        }
    }

    private sealed class SqlIngestion(PostgresIngestionTestSupport test, ChatMappingEditProposal proposal) : IRevisionIngestionService
    {
        internal int Appends, Resumes;
        internal bool HoldReady;
        private IngestionClaim? claim;
        public async Task<RevisionIngestionResult> AppendAsync(ActorContext actor, MappingScope target, Guid key, AppendRevisionRequest request,
            Stream source, CancellationToken cancellationToken = default)
        {
            Assert.Equal(test.Actor, actor);
            Assert.Equal(proposal.RequestKey, key);
            Assert.Equal(new MappingScope(proposal.Content.Base.OrganizationId, proposal.Content.Base.ProjectId, proposal.Content.Base.MappingId), target);
            Assert.Equal(new EqualHead(proposal.Content.Base.RevisionId), request.HeadCondition);
            using var bytes = new MemoryStream(); await source.CopyToAsync(bytes, cancellationToken);
            Assert.Equal(proposal.Content.Checkpoint.CopyBytes(), bytes.ToArray());
            Appends++;
            var reservation = await test.ReserveAsync(key, IngestionIntent.ForAppend(target, "ferrule", null, request.HeadCondition,
                proposal.Content.Checkpoint.Digest, proposal.Content.Checkpoint.ByteCount));
            claim = await test.ClaimAsync(reservation);
            var ready = await test.ReadyAsync(reservation, claim);
            if (HoldReady) return RevisionIngestionResult.ForReceipt(RevisionIngestionOutcome.InProgress, RevisionIngestionReceipt.FromMetadata(ready));
            await CommitAsync(actor, cancellationToken);
            // Deliberately lose the commit acknowledgement: the HTTP result must come from the durable store.
            return RevisionIngestionResult.WithoutReceipt(RevisionIngestionOutcome.ClaimLost);
        }
        public async Task<RevisionIngestionResult> ResumeAsync(ActorContext actor, ProjectScope project, Guid operationId, CancellationToken cancellationToken = default)
        {
            Assert.NotNull(claim);
            Assert.Equal(test.Actor, actor); Assert.Equal(test.Project, project); Assert.Equal(claim!.OperationId, operationId);
            Resumes++; await CommitAsync(actor, cancellationToken);
            return RevisionIngestionResult.WithoutReceipt(RevisionIngestionOutcome.ClaimLost);
        }
        private async Task CommitAsync(ActorContext actor, CancellationToken token)
        {
            var result = await test.Store.FinalizeAsync(actor, claim!, token);
            Assert.Equal(IngestionPhase.Committed, result.Receipt!.Phase);
        }
        public Task<RevisionIngestionResult> CreateAsync(ActorContext actor, ProjectScope project, Guid key, CreateRevisionRequest request, Stream source, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RevisionIngestionReceipt?> GetOwnReceiptAsync(ActorContext actor, ProjectScope project, Guid operationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class NoCredentials : IAiCredentialResolver
    {
        public ValueTask<AiCredentialResult> ResolveAsync(string reference, CancellationToken ct) => throw new InvalidOperationException("Approval and recovery must not call a model.");
    }
}
