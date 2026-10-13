using System.Globalization;
using System.Net;
using System.Text.Json;
using Npgsql;

namespace Weft.Tests;

/// <summary>Actual conditional metadata HTTP behavior; committed mappings use synthetic C1 admission evidence.</summary>
[Collection(PostgresCollection.Name)]
public sealed class MetadataHttpConditionalTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task ConditionalGetUsesStrongIfMatchThenWeakIfNoneMatchAnd304HasNoBody()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var receipt = await CatalogHttpTestSupport.CommitSyntheticAsync(test);
        var path = MetadataHttpTestSupport.Path(receipt.Facts.FutureRevision);
        await using var api = await MetadataHttpTestSupport.StartAsync(test.Case);
        var original = await api.MetadataAsync(path);
        foreach (var tag in new[] { original.Tag, "W/" + original.Tag, "*", "\"opaque,comma\", " + original.Tag })
        {
            using var response = await GetAsync(api.Client, path, ifNoneMatch: tag);
            Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
            Assert.Equal(original.Tag, response.Headers.ETag?.ToString());
            Assert.Empty(await response.Content.ReadAsByteArrayAsync());
            Assert.True(response.Headers.CacheControl?.NoStore);
            Assert.Equal(MetadataHttpTestSupport.MediaType, Assert.Single(response.Headers.GetValues("Accept-Patch")));
        }
        foreach (var tag in new[] { original.Tag, "*", "W/" + original.Tag + ", " + original.Tag })
        {
            using var response = await GetAsync(api.Client, path, ifMatch: tag);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(original.Bytes, await response.Content.ReadAsByteArrayAsync());
            Assert.Equal(original.Tag, response.Headers.ETag?.ToString());
        }
        foreach (var tag in new[] { "W/" + original.Tag, "\"opaque,comma\"", "\"unrelated\"" })
        {
            using var response = await GetAsync(api.Client, path, ifMatch: tag, ifNoneMatch: original.Tag);
            await MetadataHttpTestSupport.AssertErrorAsync(response, HttpStatusCode.PreconditionFailed, "preconditionFailed");
        }
        using var unknown = await GetAsync(api.Client, path, ifNoneMatch: "\"opaque,comma\"");
        Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);
        using var repeated = new HttpRequestMessage(HttpMethod.Get, path);
        repeated.Headers.TryAddWithoutValidation("If-Match", new[] { "\"other\"", original.Tag });
        repeated.Headers.TryAddWithoutValidation("If-None-Match", new[] { "W/\"other\"", "W/" + original.Tag });
        using var listReply = await api.Client.SendAsync(repeated);
        Assert.Equal(HttpStatusCode.NotModified, listReply.StatusCode);
        Assert.Empty(await listReply.Content.ReadAsByteArrayAsync());
        using var dateAndRange = new HttpRequestMessage(HttpMethod.Get, path);
        dateAndRange.Headers.TryAddWithoutValidation("If-Modified-Since", "Wed, 21 Oct 2099 07:28:00 GMT");
        dateAndRange.Headers.TryAddWithoutValidation("If-Unmodified-Since", "Wed, 21 Oct 1900 07:28:00 GMT");
        dateAndRange.Headers.TryAddWithoutValidation("Range", "bytes=0-1");
        using var full = await api.Client.SendAsync(dateAndRange);
        Assert.Equal(HttpStatusCode.OK, full.StatusCode);
        Assert.Equal(original.Bytes, await full.Content.ReadAsByteArrayAsync());
        Assert.Null(full.Content.Headers.ContentRange);
        Assert.Null(full.Content.Headers.LastModified);
    }

    [PostgresFact]
    public async Task BothGetConditionsAreBoundedAndParsedBeforeComparisonWhileFreshResourceDenialWins()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var receipt = await CatalogHttpTestSupport.CommitSyntheticAsync(test);
        var path = MetadataHttpTestSupport.Path(receipt.Facts.FutureRevision);
        await using var api = await MetadataHttpTestSupport.StartAsync(test.Case);
        var original = await api.MetadataAsync(path);
        var seventeen = string.Join(',', Enumerable.Range(0, 17).Select(number => "\"" + number.ToString(CultureInfo.InvariantCulture) + "\""));
        foreach (var invalid in new[] { "", "unquoted", "*," + original.Tag, original.Tag + ",", seventeen, "\"" + new string('x', 4095) + "\"" })
        {
            using var response = await GetAsync(api.Client, path, ifMatch: "\"already-stale\"", ifNoneMatch: invalid);
            await MetadataHttpTestSupport.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalidRequest");
        }
        using var overBoth = await GetAsync(api.Client, path,
            ifMatch: "\"" + new string('x', 2050) + "\"", ifNoneMatch: "\"" + new string('y', 2050) + "\"");
        await MetadataHttpTestSupport.AssertErrorAsync(overBoth, HttpStatusCode.BadRequest, "invalidRequest");
        var exactSixteen = string.Join(',', Enumerable.Range(0, 15).Select(number => "\"" + number.ToString(CultureInfo.InvariantCulture) + "\"").Append(original.Tag));
        using var accepted = await GetAsync(api.Client, path, ifMatch: exactSixteen);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var missingPath = path.Replace(receipt.Facts.FutureRevision.MappingId.ToString("D"), Guid.NewGuid().ToString("D"), StringComparison.Ordinal);
        using var missing = await GetAsync(api.Client, missingPath, ifMatch: "invalid", ifNoneMatch: "invalid");
        await MetadataHttpTestSupport.AssertErrorAsync(missing, HttpStatusCode.NotFound, "notFound");
        await test.Case.ExecuteAsync("UPDATE weft.project_memberships SET active=false,version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3",
            test.Project.OrganizationId, test.Project.ProjectId, test.Seed.Alice);
        foreach (var condition in new[] { original.Tag, "invalid", "*" })
        {
            using var denied = await GetAsync(api.Client, path, ifNoneMatch: condition);
            await MetadataHttpTestSupport.AssertErrorAsync(denied, HttpStatusCode.NotFound, "notFound");
            Assert.False(denied.Headers.Contains("Accept-Patch"));
        }
    }

    [PostgresFact]
    public async Task IdenticalMetadataHasIdenticalBytesAndTagAcrossActorsAndHeadAppendButEditsChangeBoth()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var receipt = await CatalogHttpTestSupport.CommitSyntheticAsync(test);
        var path = MetadataHttpTestSupport.Path(receipt.Facts.FutureRevision);
        await using var alice = await MetadataHttpTestSupport.StartAsync(test.Case);
        await using var other = await MetadataHttpTestSupport.StartAsync(test.Case, principal: test.Seed.Admitted);
        var initial = await alice.MetadataAsync(path);
        var otherInitial = await other.MetadataAsync(path);
        Assert.Equal(initial.Tag, otherInitial.Tag);
        Assert.Equal(initial.Bytes, otherInitial.Bytes);
        var appended = await CatalogHttpTestSupport.CommitSyntheticAsync(test, PostgresIngestionTestSupport.AppendIntent(receipt), valid: false);
        var afterAppend = await alice.MetadataAsync(path);
        Assert.Equal(initial.Tag, afterAppend.Tag);
        Assert.Equal(initial.Bytes, afterAppend.Bytes);
        using var mapping = await alice.Client.GetAsync(CatalogHttpTestSupport.MappingPath(receipt.Facts.FutureRevision));
        Assert.Null(mapping.Headers.ETag);
        var map = await CatalogHttpTestSupport.JsonAsync(mapping);
        Assert.Equal(appended.Facts.FutureRevision.RevisionId.ToString("D"), map.GetProperty("head").GetProperty("scope").GetProperty("revisionId").GetString());
        var token = await alice.TokenAsync();
        using var patch = await alice.PatchAsync(path, token, initial.Tag, MetadataHttpTestSupport.Edit("Changed metadata", "exact new description"));
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        var changed = await alice.MetadataAsync(path);
        var otherChanged = await other.MetadataAsync(path);
        Assert.NotEqual(initial.Tag, changed.Tag);
        Assert.NotEqual(Convert.ToHexString(initial.Bytes), Convert.ToHexString(changed.Bytes));
        Assert.Equal(changed.Tag, otherChanged.Tag);
        Assert.Equal(changed.Bytes, otherChanged.Bytes);
    }

    [PostgresFact]
    public async Task RestartKeepsCommittedMetadataRepresentationAndStrongTag()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var receipt = await CatalogHttpTestSupport.CommitSyntheticAsync(test);
        var path = MetadataHttpTestSupport.Path(receipt.Facts.FutureRevision);
        string tag;
        byte[] bytes;
        await using (var first = await MetadataHttpTestSupport.StartAsync(test.Case))
        {
            var token = await first.TokenAsync();
            var original = await first.MetadataAsync(path);
            using var patch = await first.PatchAsync(path, token, original.Tag, MetadataHttpTestSupport.Edit("Persisted restart", "Description survives"));
            Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
            var committed = await first.MetadataAsync(path);
            tag = committed.Tag;
            bytes = committed.Bytes;
        }
        await using var restarted = await MetadataHttpTestSupport.StartAsync(test.Case);
        var restored = await restarted.MetadataAsync(path);
        Assert.Equal(tag, restored.Tag);
        Assert.Equal(bytes, restored.Bytes);
        using var conditional = await GetAsync(restarted.Client, path, ifNoneMatch: tag);
        Assert.Equal(HttpStatusCode.NotModified, conditional.StatusCode);
        Assert.Empty(await conditional.Content.ReadAsByteArrayAsync());
    }

    [PostgresFact]
    public async Task ExactLargeVersionsAndMatchingNoopAtInt64MaximumDoNotOverflowOrLosePrecision()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var receipt = await CatalogHttpTestSupport.CommitSyntheticAsync(test);
        var scope = receipt.Facts.FutureRevision;
        var path = MetadataHttpTestSupport.Path(scope);
        await using var api = await MetadataHttpTestSupport.StartAsync(test.Case);
        var token = await api.TokenAsync();
        foreach (var version in new[] { 9_007_199_254_740_993L, long.MaxValue })
        {
            // Explicit owner-only boundary fixture: version alone is changed with the
            // transition guard restored atomically; all constraints remain enabled.
            await using (var connection = await test.Case.Owner.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await using var disable = new NpgsqlCommand("ALTER TABLE weft.mappings DISABLE TRIGGER mappings_transition_guard", connection, transaction);
                await disable.ExecuteNonQueryAsync();
                await using var update = new NpgsqlCommand("UPDATE weft.mappings SET version=$1 WHERE organization_id=$2 AND project_id=$3 AND id=$4", connection, transaction);
                update.Parameters.AddWithValue(version);
                update.Parameters.AddWithValue(scope.OrganizationId);
                update.Parameters.AddWithValue(scope.ProjectId);
                update.Parameters.AddWithValue(scope.MappingId);
                Assert.Equal(1, await update.ExecuteNonQueryAsync());
                await using var restore = new NpgsqlCommand("SET CONSTRAINTS ALL IMMEDIATE; ALTER TABLE weft.mappings ENABLE TRIGGER mappings_transition_guard", connection, transaction);
                await restore.ExecuteNonQueryAsync();
                await transaction.CommitAsync();
            }
            var original = await api.MetadataAsync(path);
            Assert.Equal(version.ToString(CultureInfo.InvariantCulture), original.Metadata.GetProperty("version").GetString());
            Assert.Equal(MetadataHttpMutationTests.Tag(scope, version.ToString(CultureInfo.InvariantCulture)), original.Tag);
            using var noop = await api.PatchAsync(path, token, original.Tag, MetadataHttpTestSupport.Edit(
                original.Metadata.GetProperty("name").GetString()!, original.Metadata.GetProperty("description").GetString()!));
            Assert.Equal(HttpStatusCode.OK, noop.StatusCode);
            Assert.Equal(original.Tag, noop.Headers.ETag?.ToString());
            Assert.Equal(original.Bytes, await noop.Content.ReadAsByteArrayAsync());
            if (version == long.MaxValue)
            {
                using var exhausted = await api.PatchAsync(path, token, original.Tag, MetadataHttpTestSupport.Edit("Changed at maximum", ""));
                await MetadataHttpTestSupport.AssertErrorAsync(exhausted, HttpStatusCode.Conflict, "versionExhausted");
                Assert.Equal(original.Bytes, (await api.MetadataAsync(path)).Bytes);
            }
        }
    }

    [PostgresFact]
    public async Task ReadOnlyConfigurationNeedsNoWriterAndDisabledPatchClosesBeforeTransportOrBody()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var receipt = await CatalogHttpTestSupport.CommitSyntheticAsync(test);
        var path = MetadataHttpTestSupport.Path(receipt.Facts.FutureRevision);
        await using var api = await MetadataHttpTestSupport.StartAsync(test.Case, editsEnabled: false, registerWriter: false);
        using var context = await api.Client.GetAsync("/api/v1/catalog/context");
        Assert.False((await CatalogHttpTestSupport.JsonAsync(context)).GetProperty("features").GetProperty("editMetadata").GetBoolean());
        using var metadata = await api.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, metadata.StatusCode);
        Assert.NotNull(metadata.Headers.ETag);
        Assert.False(metadata.Headers.Contains("Accept-Patch"));
        api.ResetObservations();
        var closed = await api.HeadersOnlyPatchAsync(path, null, null, contentLength: 65_537,
            contentType: "application/json", origin: "https://attacker.invalid", host: "attacker.invalid");
        Assert.Equal(HttpStatusCode.NotFound, closed.Status);
        using var error = JsonDocument.Parse(closed.Body);
        Assert.Equal("unavailable", error.RootElement.GetProperty("code").GetString());
        Assert.False(closed.Headers.ContainsKey("ETag"));
        Assert.False(closed.Headers.ContainsKey("Accept-Patch"));
        Assert.Equal(0, api.CompletedMappingReads);
        Assert.Equal(0, api.BodyReads);
        Assert.Equal(0, api.WriterCalls);
        using var openApi = await api.Client.GetAsync("/openapi/v1.json");
        var schema = await CatalogHttpTestSupport.JsonAsync(openApi);
        var route = schema.GetProperty("paths").GetProperty("/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/metadata");
        Assert.False(route.TryGetProperty("put", out _));
        var patch = route.GetProperty("patch");
        Assert.Equal(new[] { MetadataHttpTestSupport.MediaType }, patch.GetProperty("requestBody").GetProperty("content").EnumerateObject().Select(property => property.Name));
        foreach (var status in new[] { "200", "400", "403", "404", "409", "412", "413", "415", "428", "500" })
            Assert.True(patch.GetProperty("responses").TryGetProperty(status, out _));
        Assert.True(route.GetProperty("get").GetProperty("responses").TryGetProperty("304", out var notModified));
        Assert.False(notModified.TryGetProperty("content", out _));
        var input = schema.GetProperty("components").GetProperty("schemas").GetProperty("CatalogMappingMetadataEditV1");
        Assert.Equal(new[] { "description", "name" }, input.GetProperty("required").EnumerateArray().Select(property => property.GetString()).Order(StringComparer.Ordinal));
        Assert.False(input.GetProperty("additionalProperties").GetBoolean());
        if (input.GetProperty("properties").GetProperty("name").TryGetProperty("maxLength", out var limit))
            Assert.True(limit.GetInt32() > 200);
    }

    [PostgresFact]
    public async Task EnabledCapabilitiesAdvertiseMappingEditsOnlyForFreshPublisherOrAdministratorAndNeverProjectEdits()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var receipt = await CatalogHttpTestSupport.CommitSyntheticAsync(test);
        await using var api = await MetadataHttpTestSupport.StartAsync(test.Case);
        using var context = await api.Client.GetAsync("/api/v1/catalog/context");
        Assert.True((await CatalogHttpTestSupport.JsonAsync(context)).GetProperty("features").GetProperty("editMetadata").GetBoolean());
        foreach (var role in new[] { "viewer", "runner", "publisher", "administrator" })
        {
            await MetadataHttpMutationTests.RoleAsync(test, role);
            using var project = await api.Client.GetAsync(CatalogHttpTestSupport.ProjectPath(test.Project));
            Assert.False((await CatalogHttpTestSupport.JsonAsync(project)).GetProperty("access").GetProperty("actions").GetProperty("editMetadata").GetBoolean());
            using var mapping = await api.Client.GetAsync(CatalogHttpTestSupport.MappingPath(receipt.Facts.FutureRevision));
            Assert.Equal(role is "publisher" or "administrator", (await CatalogHttpTestSupport.JsonAsync(mapping))
                .GetProperty("access").GetProperty("actions").GetProperty("editMetadata").GetBoolean());
            using var page = await api.Client.GetAsync(CatalogHttpTestSupport.ProjectPath(test.Project) + "/mappings");
            Assert.Equal(role is "publisher" or "administrator", Assert.Single((await CatalogHttpTestSupport.JsonAsync(page))
                .GetProperty("mappings").EnumerateArray()).GetProperty("access").GetProperty("actions").GetProperty("editMetadata").GetBoolean());
        }
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string? ifMatch = null, string? ifNoneMatch = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        if (ifNoneMatch is not null) request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        return await client.SendAsync(request);
    }
}
