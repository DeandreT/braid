using System.Net;
using System.Text.Json;
using Braid.Application;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

/// <summary>Actual transport/body ordering and fresh atomic authority; admission evidence remains synthetic.</summary>
[Collection(PostgresCollection.Name)]
public sealed class MetadataHttpBoundaryTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task RealHeadersOnlyRequestsReceiveEarlyFailuresBeforeAnyDeclaredBodyByteIsSent()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var receipt = await CatalogHttpTestSupport.CommitSyntheticAsync(test);
        var path = MetadataHttpTestSupport.Path(receipt.Facts.FutureRevision);
        await using var api = await MetadataHttpTestSupport.StartAsync(test.Case);
        var token = await api.TokenAsync();
        var current = await api.MetadataAsync(path);
        foreach (var item in new[]
        {
            (Token: (string?)null, Tag: (string?)current.Tag, Type: (string?)MetadataHttpTestSupport.MediaType, Origin: (string?)null, Host: (string?)null, Length: 128L, Status: HttpStatusCode.Forbidden, Code: "identityRejected"),
            (Token: (string?)token, Tag: (string?)current.Tag, Type: (string?)MetadataHttpTestSupport.MediaType, Origin: (string?)"https://attacker.invalid", Host: (string?)null, Length: 128L, Status: HttpStatusCode.Forbidden, Code: "identityRejected"),
            (Token: (string?)token, Tag: (string?)current.Tag, Type: (string?)MetadataHttpTestSupport.MediaType, Origin: (string?)null, Host: (string?)"attacker.invalid", Length: 128L, Status: HttpStatusCode.Forbidden, Code: "identityRejected"),
            (Token: (string?)token, Tag: (string?)null, Type: (string?)"application/json", Origin: (string?)null, Host: (string?)null, Length: 128L, Status: HttpStatusCode.UnsupportedMediaType, Code: "unsupportedMediaType"),
            (Token: (string?)token, Tag: (string?)null, Type: (string?)MetadataHttpTestSupport.MediaType, Origin: (string?)null, Host: (string?)null, Length: 65_537L, Status: HttpStatusCode.RequestEntityTooLarge, Code: "bodyLimit"),
            (Token: (string?)token, Tag: (string?)null, Type: (string?)MetadataHttpTestSupport.MediaType, Origin: (string?)null, Host: (string?)null, Length: 128L, Status: (HttpStatusCode)428, Code: "preconditionRequired"),
            (Token: (string?)token, Tag: (string?)"\"unrelated\"", Type: (string?)MetadataHttpTestSupport.MediaType, Origin: (string?)null, Host: (string?)null, Length: 128L, Status: HttpStatusCode.PreconditionFailed, Code: "preconditionFailed")
        })
        {
            api.ResetObservations();
            var reply = await api.HeadersOnlyPatchAsync(path, item.Token, item.Tag,
                item.Length, item.Type, item.Origin, item.Host);
            AssertRawError(reply, item.Status, item.Code);
            Assert.Equal(0, api.BodyReads);
            Assert.Equal(0, api.WriterCalls);
            Assert.Equal(item.Status == HttpStatusCode.Forbidden ? 0 : 1, api.CompletedMappingReads);
        }
        await MetadataHttpMutationTests.RoleAsync(test, "viewer");
        api.ResetObservations();
        var denied = await api.HeadersOnlyPatchAsync(path, token, "malformed", contentType: null);
        AssertRawError(denied, HttpStatusCode.NotFound, "notFound");
        Assert.False(denied.Headers.ContainsKey("Accept-Patch"));
        Assert.Equal(0, api.BodyReads);
        Assert.Equal(0, api.WriterCalls);
    }

    [PostgresFact]
    public async Task ActualCookieAndRequestTokenFailuresRejectRealPatchBeforeReaderOrBodyWithoutCallerIdentityFallback()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var receipt = await CatalogHttpTestSupport.CommitSyntheticAsync(test);
        var path = MetadataHttpTestSupport.Path(receipt.Facts.FutureRevision);
        await using var api = await MetadataHttpTestSupport.StartAsync(test.Case);
        var token = await api.TokenAsync();
        var current = await api.MetadataAsync(path);
        using var noCookie = api.NewClient();
        foreach (var invalid in new string?[] { null, "", "invalid", new('a', 4097) })
        {
            api.ResetObservations();
            using var response = await api.PatchAsync(path, invalid, current.Tag, [255]);
            await MetadataHttpTestSupport.AssertErrorAsync(response, HttpStatusCode.Forbidden, "identityRejected");
            Assert.Equal(0, api.CompletedMappingReads);
            Assert.Equal(0, api.BodyReads);
        }
        api.ResetObservations();
        using var cookieMissing = await api.PatchAsync(path, token, current.Tag, [255], client: noCookie);
        await MetadataHttpTestSupport.AssertErrorAsync(cookieMissing, HttpStatusCode.Forbidden, "identityRejected");
        Assert.Equal(0, api.CompletedMappingReads);
        Assert.Equal(0, api.BodyReads);
        Assert.Equal(0, api.WriterCalls);
    }

    [PostgresFact]
    public async Task RevocationDuringActualBodyGateIsRecheckedByAtomicWriterAfterTheReadTransactionHasEnded()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var receipt = await CatalogHttpTestSupport.CommitSyntheticAsync(test);
        var path = MetadataHttpTestSupport.Path(receipt.Facts.FutureRevision);
        await using var api = await MetadataHttpTestSupport.StartAsync(test.Case);
        var token = await api.TokenAsync();
        foreach (var (revoke, restore, values) in new[]
        {
            ("UPDATE braid.principals SET active=false WHERE id=$1", "UPDATE braid.principals SET active=true WHERE id=$1", new object[] { test.Seed.Alice }),
            ("UPDATE braid.organization_memberships SET active=false WHERE organization_id=$1 AND principal_id=$2", "UPDATE braid.organization_memberships SET active=true WHERE organization_id=$1 AND principal_id=$2", new object[] { test.Project.OrganizationId, test.Seed.Alice }),
            ("UPDATE braid.project_memberships SET active=false,version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3", "UPDATE braid.project_memberships SET active=true,version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3", new object[] { test.Project.OrganizationId, test.Project.ProjectId, test.Seed.Alice }),
            ("UPDATE braid.project_memberships SET role='viewer',version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3", "UPDATE braid.project_memberships SET role='publisher',version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3", new object[] { test.Project.OrganizationId, test.Project.ProjectId, test.Seed.Alice })
        })
        {
            var original = await api.MetadataAsync(path);
            var facts = await MetadataHttpMutationTests.ImmutableFactsAsync(test.Case, receipt.Facts.FutureRevision);
            api.ResetObservations();
            var gate = api.GateBody();
            var pending = api.PatchAsync(path, token, original.Tag, MetadataHttpTestSupport.Edit("Must not persist", ""));
            try
            {
                await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(1, api.CompletedMappingReads);
                Assert.Equal(0, api.WriterCalls);
                Assert.Equal(0, api.BodyBytes);
                Assert.Equal("mapping-read-completed", api.Events.First());
                Assert.False(await HasCatalogSourceLocksAsync(test.Case));
                await test.Case.ExecuteAsync(revoke, values);
                gate.Release.TrySetResult();
                using var denied = await pending.WaitAsync(TimeSpan.FromSeconds(5));
                await MetadataHttpTestSupport.AssertErrorAsync(denied, HttpStatusCode.NotFound, "notFound");
                Assert.False(denied.Headers.Contains("Accept-Patch"));
                Assert.Equal(1, api.WriterCalls);
                await gate.RequestEnded.Task.WaitAsync(TimeSpan.FromSeconds(3));
            }
            finally
            {
                gate.Release.TrySetResult();
                try { using var completion = await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
                finally { api.ClearBodyGate(); await test.Case.ExecuteAsync(restore, values); }
            }
            Assert.Equal(original.Bytes, (await api.MetadataAsync(path)).Bytes);
            Assert.Equal(facts, await MetadataHttpMutationTests.ImmutableFactsAsync(test.Case, receipt.Facts.FutureRevision));
        }
    }

    [PostgresFact]
    public async Task MetadataChangeDuringBodyGateMakesTheOriginalTagStaleEvenWhenSubmittedValuesMatchLatestMetadata()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var receipt = await CatalogHttpTestSupport.CommitSyntheticAsync(test);
        var revision = receipt.Facts.FutureRevision;
        var scope = new MappingScope(revision.OrganizationId, revision.ProjectId, revision.MappingId);
        var path = MetadataHttpTestSupport.Path(revision);
        await using var api = await MetadataHttpTestSupport.StartAsync(test.Case);
        var token = await api.TokenAsync();
        var current = await api.MetadataAsync(path);
        api.ResetObservations();
        var gate = api.GateBody();
        var pending = api.PatchAsync(path, token, current.Tag, MetadataHttpTestSupport.Edit("Concurrent winner", "latest exact description"));
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, api.CompletedMappingReads);
            Assert.Equal(0, api.WriterCalls);
            Assert.False(await HasCatalogSourceLocksAsync(test.Case));
            await using var independentPool = new PostgresMappingMetadataDatabase(test.Case.MetadataConnectionString);
            var writer = new PostgresMappingMetadataWriter(independentPool);
            var winner = await writer.ReplaceAsync(test.Actor, scope, new(scope, 1), new("Concurrent winner", "latest exact description"));
            Assert.Equal(MappingMetadataWriteOutcome.Updated, winner.Outcome);
            gate.Release.TrySetResult();
            using var stale = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            await MetadataHttpTestSupport.AssertErrorAsync(stale, HttpStatusCode.PreconditionFailed, "preconditionFailed");
            Assert.Equal(1, api.WriterCalls);
        }
        finally
        {
            gate.Release.TrySetResult();
            try { using var completion = await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { api.ClearBodyGate(); }
        }
        var latest = await api.MetadataAsync(path);
        Assert.Equal("Concurrent winner", latest.Metadata.GetProperty("name").GetString());
        Assert.Equal("2", latest.Metadata.GetProperty("version").GetString());
        Assert.NotEqual(current.Tag, latest.Tag);
    }

    [PostgresFact]
    public async Task UnknownLengthOverflowReadsAtMostBudgetPlusOneAndNeverEntersWriter()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var receipt = await CatalogHttpTestSupport.CommitSyntheticAsync(test);
        var path = MetadataHttpTestSupport.Path(receipt.Facts.FutureRevision);
        await using var api = await MetadataHttpTestSupport.StartAsync(test.Case);
        var token = await api.TokenAsync();
        var original = await api.MetadataAsync(path);
        api.ResetObservations();
        using var overflow = await api.PatchAsync(path, token, original.Tag,
            Enumerable.Repeat((byte)' ', MetadataHttpTestSupport.MaximumBodyBytes * 3).ToArray(), chunked: true);
        await MetadataHttpTestSupport.AssertErrorAsync(overflow, HttpStatusCode.RequestEntityTooLarge, "bodyLimit");
        Assert.Equal(MetadataHttpTestSupport.MaximumBodyBytes + 1L, api.BodyBytes);
        Assert.Equal(0, api.WriterCalls);
        Assert.Equal(original.Bytes, (await api.MetadataAsync(path)).Bytes);
        api.ResetObservations();
        using var valid = await api.PatchAsync(path, token, original.Tag, MetadataHttpTestSupport.Edit("Chunked accepted", ""), chunked: true);
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        Assert.Equal(1, api.WriterCalls);
    }

    [PostgresFact]
    public async Task ActualClientCancellationWhileBodyReadIsGatedAbortsBeforeMutationAndLeavesTheSamePoolsReusable()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var receipt = await CatalogHttpTestSupport.CommitSyntheticAsync(test);
        var path = MetadataHttpTestSupport.Path(receipt.Facts.FutureRevision);
        await using var api = await MetadataHttpTestSupport.StartAsync(test.Case);
        var token = await api.TokenAsync();
        var original = await api.MetadataAsync(path);
        api.ResetObservations();
        var gate = api.GateBody();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = api.PatchAsync(path, token, original.Tag, MetadataHttpTestSupport.Edit("Canceled", ""), cancellationToken: cancellation.Token);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, api.CompletedMappingReads);
            Assert.False(await HasCatalogSourceLocksAsync(test.Case));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { using var unexpected = await pending; });
            await gate.RequestEnded.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, api.WriterCalls);
        }
        finally
        {
            cancellation.Cancel();
            gate.Release.TrySetResult();
            api.ClearBodyGate();
            try { using var completion = await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
        }
        Assert.Equal(original.Bytes, (await api.MetadataAsync(path)).Bytes);
        using var recovered = await api.PatchAsync(path, token, original.Tag, MetadataHttpTestSupport.Edit("Recovered", ""));
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }

    private static Task<bool> HasCatalogSourceLocksAsync(PostgresCase test) => test.ScalarAsync<bool>("""
        SELECT EXISTS (
          SELECT 1 FROM pg_catalog.pg_locks held
          JOIN pg_catalog.pg_stat_activity activity ON activity.pid=held.pid
          JOIN pg_catalog.pg_class source ON source.oid=held.relation
          JOIN pg_catalog.pg_namespace schema ON schema.oid=source.relnamespace
          WHERE activity.datname=current_database() AND activity.usename='braid_catalog_reader'
            AND held.granted AND schema.nspname='braid'
            AND source.relname IN ('organizations','principals','organization_memberships','projects','project_memberships','mappings','mapping_revisions','revision_ingestions')
        )
        """);

    private static void AssertRawError(MetadataHttpTestSupport.RawReply reply, HttpStatusCode status, string code)
    {
        Assert.Equal(status, reply.Status);
        Assert.False(reply.Headers.ContainsKey("ETag"));
        Assert.Contains("no-store", reply.Headers["Cache-Control"], StringComparison.Ordinal);
        using var body = JsonDocument.Parse(reply.Body);
        Assert.Equal("code", Assert.Single(body.RootElement.EnumerateObject()).Name);
        Assert.Equal(code, body.RootElement.GetProperty("code").GetString());
    }
}
