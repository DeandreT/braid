using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Braid.Domain;

namespace Braid.Tests;

/// <summary>Actual HTTP and PostgreSQL metadata edits; mapping admission uses synthetic C1 evidence only.</summary>
[Collection(PostgresCollection.Name)]
public sealed class MetadataHttpMutationTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task PublisherAndAdministratorEditsTrimNamePreserveDescriptionAndCheckedNoopPreservesTimeAndTag()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var receipt = await CatalogHttpTestSupport.CommitSyntheticAsync(test);
        var path = MetadataHttpTestSupport.Path(receipt.Facts.FutureRevision);
        await using var api = await MetadataHttpTestSupport.StartAsync(test.Case);
        var token = await api.TokenAsync();
        var immutable = await ImmutableFactsAsync(test.Case, receipt.Facts.FutureRevision);
        foreach (var role in new[] { "publisher", "administrator" })
        {
            await RoleAsync(test, role);
            var before = await api.MetadataAsync(path);
            var description = "  preserved\n\t" + role + "\u00e9\U0001f642  ";
            using var changed = await api.PatchAsync(path, token, before.Tag,
                MetadataHttpTestSupport.Edit("  " + role + " mapping  ", description));
            var metadata = await CatalogHttpTestSupport.JsonAsync(changed);
            Assert.Equal(role + " mapping", metadata.GetProperty("name").GetString());
            Assert.Equal(description, metadata.GetProperty("description").GetString());
            Assert.Equal((long.Parse(before.Metadata.GetProperty("version").GetString()!, CultureInfo.InvariantCulture) + 1)
                .ToString(CultureInfo.InvariantCulture), metadata.GetProperty("version").GetString());
            Assert.Equal(new[] { "description", "name", "scope", "version" }, metadata.EnumerateObject().Select(value => value.Name).Order(StringComparer.Ordinal));
            Assert.NotEqual(before.Tag, changed.Headers.ETag?.ToString());
            Assert.Equal(Tag(receipt.Facts.FutureRevision, metadata.GetProperty("version").GetString()!), changed.Headers.ETag?.ToString());
            Assert.True(changed.Headers.CacheControl?.NoStore);
            Assert.Equal(MetadataHttpTestSupport.MediaType, Assert.Single(changed.Headers.GetValues("Accept-Patch")));
            var after = await api.MetadataAsync(path);
            var timestamp = await test.Case.ScalarAsync<DateTime>("SELECT updated_at FROM braid.mappings WHERE organization_id=$1 AND project_id=$2 AND id=$3",
                test.Project.OrganizationId, test.Project.ProjectId, receipt.Facts.FutureRevision.MappingId);
            using var unchanged = await api.PatchAsync(path, token, after.Tag,
                MetadataHttpTestSupport.Edit("\t" + role + " mapping\n", description));
            Assert.Equal(HttpStatusCode.OK, unchanged.StatusCode);
            Assert.Equal(after.Tag, unchanged.Headers.ETag?.ToString());
            Assert.Equal(after.Bytes, await unchanged.Content.ReadAsByteArrayAsync());
            Assert.Equal(timestamp, await test.Case.ScalarAsync<DateTime>("SELECT updated_at FROM braid.mappings WHERE organization_id=$1 AND project_id=$2 AND id=$3",
                test.Project.OrganizationId, test.Project.ProjectId, receipt.Facts.FutureRevision.MappingId));
            using var staleNoop = await api.PatchAsync(path, token, before.Tag,
                MetadataHttpTestSupport.Edit(role + " mapping", description));
            await MetadataHttpTestSupport.AssertErrorAsync(staleNoop, HttpStatusCode.PreconditionFailed, "preconditionFailed");
            Assert.Equal(immutable, await ImmutableFactsAsync(test.Case, receipt.Facts.FutureRevision));
        }
    }

    [PostgresFact]
    public async Task EveryRoleUsesFreshEditAuthorityBeforeContentAndPreconditionEvaluation()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var receipt = await CatalogHttpTestSupport.CommitSyntheticAsync(test);
        var path = MetadataHttpTestSupport.Path(receipt.Facts.FutureRevision);
        await using var api = await MetadataHttpTestSupport.StartAsync(test.Case);
        var token = await api.TokenAsync();
        foreach (var role in new[] { "viewer", "runner", "publisher", "administrator" })
        {
            await RoleAsync(test, role);
            api.ResetObservations();
            using var response = await api.PatchAsync(path, token, null, "invalid JSON"u8.ToArray());
            var allowed = role is "publisher" or "administrator";
            await MetadataHttpTestSupport.AssertErrorAsync(response,
                allowed ? (HttpStatusCode)428 : HttpStatusCode.NotFound, allowed ? "preconditionRequired" : "notFound");
            Assert.Equal(0, api.BodyReads);
            Assert.Equal(0, api.WriterCalls);
            if (!allowed) Assert.False(response.Headers.Contains("Accept-Patch"));
        }
    }

    [PostgresFact]
    public async Task FullAncestryAndCanonicalRoutesCannotBeReplacedByTagsOrRequestIdentityFields()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var receipt = await CatalogHttpTestSupport.CommitSyntheticAsync(test);
        var scope = receipt.Facts.FutureRevision;
        await using var api = await MetadataHttpTestSupport.StartAsync(test.Case);
        var token = await api.TokenAsync();
        var original = await api.MetadataAsync(MetadataHttpTestSupport.Path(scope));
        foreach (var other in new[]
        {
            new RevisionScope(test.Seed.OrganizationB, scope.ProjectId, scope.MappingId, scope.RevisionId),
            new RevisionScope(scope.OrganizationId, test.Seed.HiddenProjectA, scope.MappingId, scope.RevisionId),
            new RevisionScope(scope.OrganizationId, scope.ProjectId, Guid.NewGuid(), scope.RevisionId)
        })
        {
            using var response = await api.PatchAsync(MetadataHttpTestSupport.Path(other), token, "malformed", [255], contentType: null);
            await MetadataHttpTestSupport.AssertErrorAsync(response, HttpStatusCode.NotFound, "notFound");
            Assert.False(response.Headers.Contains("Accept-Patch"));
        }
        foreach (var path in new[]
        {
            MetadataHttpTestSupport.Path(scope) + "?actor=administrator",
            MetadataHttpTestSupport.Path(scope).Replace(scope.MappingId.ToString("D"), scope.MappingId.ToString("N"), StringComparison.Ordinal),
            MetadataHttpTestSupport.Path(scope).Replace(scope.MappingId.ToString("D"), Guid.Empty.ToString("D"), StringComparison.Ordinal)
        })
        {
            using var response = await api.PatchAsync(path, token, original.Tag, MetadataHttpTestSupport.Edit("Rejected", ""));
            await MetadataHttpTestSupport.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalidRequest");
        }
        Assert.Equal(original.Bytes, (await api.MetadataAsync(MetadataHttpTestSupport.Path(scope))).Bytes);
    }

    [PostgresFact]
    public async Task MediaCodingAndKnownLengthErrorsPrecedeMissingTagsWithoutApplicationBodyReads()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var receipt = await CatalogHttpTestSupport.CommitSyntheticAsync(test);
        var path = MetadataHttpTestSupport.Path(receipt.Facts.FutureRevision);
        await using var api = await MetadataHttpTestSupport.StartAsync(test.Case);
        var token = await api.TokenAsync();
        foreach (var type in new string?[]
        {
            null, "application/json", "application/json-patch+json", "application/merge-patch+json",
            MetadataHttpTestSupport.MediaType + ";charset=utf-16", MetadataHttpTestSupport.MediaType + ";extra=true",
            MetadataHttpTestSupport.MediaType + ";charset=utf-8;charset=utf-8",
            MetadataHttpTestSupport.MediaType + "," + MetadataHttpTestSupport.MediaType
        })
        {
            api.ResetObservations();
            using var response = await api.PatchAsync(path, token, null, [255], contentType: type);
            await MetadataHttpTestSupport.AssertErrorAsync(response, HttpStatusCode.UnsupportedMediaType, "unsupportedMediaType");
            Assert.Equal(MetadataHttpTestSupport.MediaType, Assert.Single(response.Headers.GetValues("Accept-Patch")));
            Assert.Equal(0, api.BodyReads);
            Assert.Equal(0, api.WriterCalls);
        }
        foreach (var encoding in new[] { "gzip", "br", "identity,identity", "identity,gzip" })
        {
            api.ResetObservations();
            using var response = await api.PatchAsync(path, token, null, [255], contentEncoding: encoding);
            await MetadataHttpTestSupport.AssertErrorAsync(response, HttpStatusCode.UnsupportedMediaType, "unsupportedMediaType");
            Assert.Equal(0, api.BodyReads);
        }
        api.ResetObservations();
        using var overflow = await api.PatchAsync(path, token, null, new byte[MetadataHttpTestSupport.MaximumBodyBytes + 1]);
        await MetadataHttpTestSupport.AssertErrorAsync(overflow, HttpStatusCode.RequestEntityTooLarge, "bodyLimit");
        Assert.Equal(0, api.BodyReads);
        Assert.Equal(0, api.WriterCalls);
    }

    [PostgresFact]
    public async Task OneConcreteIfMatchDistinguishesSyntaxFromValidNonmatchingOpaqueTagsWithoutReadingBody()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var receipt = await CatalogHttpTestSupport.CommitSyntheticAsync(test);
        var path = MetadataHttpTestSupport.Path(receipt.Facts.FutureRevision);
        await using var api = await MetadataHttpTestSupport.StartAsync(test.Case);
        var token = await api.TokenAsync();
        var current = await api.MetadataAsync(path);
        foreach (var malformed in new[] { "", "*", "unquoted", "w/\"lowercase\"", "\"has space\"", current.Tag + "," + current.Tag, "\"" + new string('a', 255) + "\"" })
        {
            api.ResetObservations();
            using var response = await api.PatchAsync(path, token, malformed, [255]);
            await MetadataHttpTestSupport.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalidRequest");
            Assert.Equal(0, api.BodyReads);
            Assert.Equal(0, api.WriterCalls);
        }
        foreach (var mismatch in new[] { "W/" + current.Tag, "\"opaque,comma\"", "\"\"", current.Tag.Replace("metadata-v1:", "metadata-v2:", StringComparison.Ordinal), Tag(receipt.Facts.FutureRevision, "01"), Tag(receipt.Facts.FutureRevision, "0") })
        {
            api.ResetObservations();
            using var response = await api.PatchAsync(path, token, mismatch, [255]);
            await MetadataHttpTestSupport.AssertErrorAsync(response, HttpStatusCode.PreconditionFailed, "preconditionFailed");
            Assert.Equal(0, api.BodyReads);
        }
        using var secondCondition = await api.PatchAsync(path, token, current.Tag, [255], ifNoneMatch: "*");
        await MetadataHttpTestSupport.AssertErrorAsync(secondCondition, HttpStatusCode.BadRequest, "invalidRequest");
        using var validOws = await api.PatchAsync(path, token, "\t " + current.Tag + " \t", MetadataHttpTestSupport.Edit("OWS accepted", ""));
        Assert.Equal(HttpStatusCode.OK, validOws.StatusCode);
    }

    [PostgresFact]
    public async Task StrictJsonUtf8AndUnicodePoliciesRejectMalformedWireDocumentsWithoutChangingMetadata()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var receipt = await CatalogHttpTestSupport.CommitSyntheticAsync(test);
        var path = MetadataHttpTestSupport.Path(receipt.Facts.FutureRevision);
        await using var api = await MetadataHttpTestSupport.StartAsync(test.Case);
        var token = await api.TokenAsync();
        var before = await api.MetadataAsync(path);
        var malformed = new[]
        {
            "", "null", "[]", "{}", "{\"name\":\"N\"}", "{\"description\":\"D\"}",
            "{\"name\":null,\"description\":\"D\"}", "{\"name\":42,\"description\":\"D\"}",
            "{\"name\":\"N\",\"description\":null}", "{\"name\":\"N\",\"description\":[]}",
            "{\"name\":\"N\",\"description\":\"D\",\"role\":\"administrator\"}",
            "{\"Name\":\"N\",\"description\":\"D\"}",
            "{\"name\":\"N\",\"name\":\"Other\",\"description\":\"D\"}",
            "{\"name\":\"N\",\"description\":\"D\",\"description\":\"Other\"}",
            "{\"name\":\"N\",\"description\":\"D\",}",
            "{/*comment*/\"name\":\"N\",\"description\":\"D\"}",
            "{\"name\":\"\\uD800\",\"description\":\"D\"}",
            "{\"name\":\"N\",\"description\":\"\\uDC00\"}",
            "{\"name\":\"N\",\"description\":\"\\uD800x\"}",
            "{\"name\":\"N\",\"description\":\"\\u0000\"}",
            "{\"name\":\"  \",\"description\":\"D\"}",
            JsonSerializer.Serialize(new { name = new string('n', 201), description = "" }),
            JsonSerializer.Serialize(new { name = "N", description = new string('d', 4001) })
        }.Select(Encoding.UTF8.GetBytes).Concat(new byte[][]
        {
            [0xef, 0xbb, 0xbf, .. MetadataHttpTestSupport.Edit("N", "D")],
            [.. "{\"name\":\""u8.ToArray(), 0xc3, 0x28, .. "\",\"description\":\"D\"}"u8.ToArray()]
        });
        foreach (var body in malformed)
        {
            api.ResetObservations();
            using var response = await api.PatchAsync(path, token, before.Tag, body);
            await MetadataHttpTestSupport.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalidRequest");
            Assert.Equal(0, api.WriterCalls);
            Assert.Equal(before.Bytes, (await api.MetadataAsync(path)).Bytes);
        }
    }

    [PostgresFact]
    public async Task SupplementaryScalarLimitsAndFullDescriptionSurviveQuotedUtf8MediaAndIdentityCoding()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var receipt = await CatalogHttpTestSupport.CommitSyntheticAsync(test);
        var path = MetadataHttpTestSupport.Path(receipt.Facts.FutureRevision);
        await using var api = await MetadataHttpTestSupport.StartAsync(test.Case);
        var token = await api.TokenAsync();
        var name = string.Concat(Enumerable.Repeat("\U0001f642", 200));
        var description = string.Concat(Enumerable.Repeat("\U0001f642", 4000));
        foreach (var media in new[] { MetadataHttpTestSupport.MediaType, MetadataHttpTestSupport.MediaType.ToUpperInvariant(), MetadataHttpTestSupport.MediaType + "; charset=\"UTF-8\"" })
        {
            var current = await api.MetadataAsync(path);
            var bytes = MetadataHttpTestSupport.Edit(new string(' ', 300) + name + new string(' ', 300), description);
            Assert.True(bytes.Length < MetadataHttpTestSupport.MaximumBodyBytes);
            using var response = await api.PatchAsync(path, token, current.Tag, bytes, contentType: media, contentEncoding: "IDENTITY");
            var metadata = await CatalogHttpTestSupport.JsonAsync(response);
            Assert.Equal(name, metadata.GetProperty("name").GetString());
            Assert.Equal(description, metadata.GetProperty("description").GetString());
        }
        var last = await api.MetadataAsync(path);
        using var tooMany = await api.PatchAsync(path, token, last.Tag, MetadataHttpTestSupport.Edit(name + "\U0001f642", description));
        await MetadataHttpTestSupport.AssertErrorAsync(tooMany, HttpStatusCode.BadRequest, "invalidRequest");
    }

    [PostgresFact]
    public async Task ActualEditorRoleGuardFailureIsCategoricalAndRecoveryDoesNotFabricateDenialOrSuccess()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var receipt = await CatalogHttpTestSupport.CommitSyntheticAsync(test);
        var path = MetadataHttpTestSupport.Path(receipt.Facts.FutureRevision);
        await using var api = await MetadataHttpTestSupport.StartAsync(test.Case);
        var token = await api.TokenAsync();
        var original = await api.MetadataAsync(path);
        await test.Case.ExecuteAsync("GRANT UPDATE (head_revision_id) ON braid.mappings TO braid_catalog_editor");
        try
        {
            using var failed = await api.PatchAsync(path, token, original.Tag, MetadataHttpTestSupport.Edit("Must not persist", ""));
            await MetadataHttpTestSupport.AssertErrorAsync(failed, HttpStatusCode.InternalServerError, "persistenceFailure");
        }
        finally { await test.Case.ExecuteAsync("REVOKE UPDATE (head_revision_id) ON braid.mappings FROM braid_catalog_editor"); }
        Assert.Equal(original.Bytes, (await api.MetadataAsync(path)).Bytes);
        using var recovered = await api.PatchAsync(path, token, original.Tag, MetadataHttpTestSupport.Edit("Recovered", ""));
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }

    internal static Task RoleAsync(PostgresIngestionTestSupport test, string role) => test.Case.ExecuteAsync(
        "UPDATE braid.project_memberships SET role=$1,version=version+1 WHERE organization_id=$2 AND project_id=$3 AND principal_id=$4",
        role, test.Project.OrganizationId, test.Project.ProjectId, test.Seed.Alice);

    internal static string Tag(RevisionScope scope, string version) =>
        $"\"metadata-v1:{scope.OrganizationId:D}:{scope.ProjectId:D}:{scope.MappingId:D}:{version}\"";

    internal static Task<string> ImmutableFactsAsync(PostgresCase test, RevisionScope scope) => test.ScalarAsync<string>("""
        SELECT jsonb_build_object(
          'mapping', (SELECT to_jsonb(m) - ARRAY['name','description','version','updated_at'] FROM braid.mappings m WHERE m.organization_id=$1 AND m.project_id=$2 AND m.id=$3),
          'revisions', (SELECT jsonb_agg(to_jsonb(r) ORDER BY r.sequence) FROM braid.mapping_revisions r WHERE r.organization_id=$1 AND r.project_id=$2 AND r.mapping_id=$3),
          'ingestions', (SELECT jsonb_agg(to_jsonb(i) ORDER BY i.operation_id) FROM braid.revision_ingestions i WHERE i.organization_id=$1 AND i.project_id=$2 AND i.future_mapping_id=$3),
          'attempts', (SELECT jsonb_agg(to_jsonb(a) ORDER BY a.operation_id,a.generation) FROM braid.revision_ingestion_attempts a JOIN braid.revision_ingestions i ON i.organization_id=a.organization_id AND i.project_id=a.project_id AND i.operation_id=a.operation_id WHERE i.organization_id=$1 AND i.project_id=$2 AND i.future_mapping_id=$3)
        )::text
        """, scope.OrganizationId, scope.ProjectId, scope.MappingId);
}
