using System.Globalization;
using System.Net;
using System.Text.Json;
using Npgsql;
using Weft.Application;
using Weft.Domain;

namespace Weft.Tests;

/// <summary>Actual database/API transport; committed setup uses synthetic admission evidence only.</summary>
[Collection(PostgresCollection.Name)]
public sealed class CatalogHttpReadTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task EveryRoleReadsPublicMetadataAndHistoryWithoutWorkerAndOnlyAdvertisesImplementedActions()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var first = await CatalogHttpTestSupport.CommitSyntheticAsync(test,
            PostgresIngestionTestSupport.CreateIntent(name: "Visible mapping", description: "Catalog description", filename: "original.json"));
        var second = await CatalogHttpTestSupport.CommitSyntheticAsync(test,
            PostgresIngestionTestSupport.AppendIntent(first), valid: false);
        await using var api = await CatalogHttpTestSupport.StartAsync(test.Case);
        foreach (var role in new[] { "viewer", "runner", "publisher", "administrator" })
        {
            await test.Case.ExecuteAsync("UPDATE weft.project_memberships SET role=$1,version=version+1 WHERE organization_id=$2 AND project_id=$3 AND principal_id=$4",
                role, test.Project.OrganizationId, test.Project.ProjectId, test.Seed.Alice);
            using var contextResponse = await api.Client.GetAsync("/api/v1/catalog/context");
            var context = await CatalogHttpTestSupport.JsonAsync(contextResponse);
            Assert.Equal("development", context.GetProperty("mode").GetString());
            Assert.Equal(test.Seed.Alice.ToString("D"), context.GetProperty("actorPrincipalId").GetString());
            Assert.Equal(test.Project.OrganizationId.ToString("D"), context.GetProperty("allowedOrganizationId").GetString());
            Assert.True(context.GetProperty("features").GetProperty("readCatalog").GetBoolean());
            Assert.All(context.GetProperty("features").EnumerateObject().Where(value => value.Name != "readCatalog"),
                value => Assert.False(value.Value.GetBoolean()));
            Assert.False(string.IsNullOrWhiteSpace(context.GetProperty("requestToken").GetString()));
            Assert.True(contextResponse.Headers.CacheControl?.NoStore);

            using var projectResponse = await api.Client.GetAsync(CatalogHttpTestSupport.ProjectPath(test.Project));
            var project = await CatalogHttpTestSupport.JsonAsync(projectResponse);
            Assert.Equal(role, project.GetProperty("access").GetProperty("role").GetString());
            AssertActionsReadOnly(project.GetProperty("access").GetProperty("actions"));
            Assert.Equal("Project A", project.GetProperty("name").GetString());
            AssertPublic(project);

            using var mappingResponse = await api.Client.GetAsync(CatalogHttpTestSupport.MappingPath(first.Facts.FutureRevision));
            var mapping = await CatalogHttpTestSupport.JsonAsync(mappingResponse);
            Assert.Equal("Visible mapping", mapping.GetProperty("name").GetString());
            Assert.Equal("Catalog description", mapping.GetProperty("description").GetString());
            Assert.Equal("ferrule", mapping.GetProperty("backendId").GetString());
            Assert.Equal("2", mapping.GetProperty("head").GetProperty("sequence").GetString());
            Assert.Equal("decodedInvalid", mapping.GetProperty("head").GetProperty("admission").GetString());
            Assert.Equal(second.Facts.FutureRevision.RevisionId.ToString("D"),
                mapping.GetProperty("head").GetProperty("scope").GetProperty("revisionId").GetString());
            AssertActionsReadOnly(mapping.GetProperty("access").GetProperty("actions"));
            AssertPublic(mapping);
            Assert.True(mappingResponse.Headers.CacheControl?.NoStore);

            using var metadataResponse = await api.Client.GetAsync($"{CatalogHttpTestSupport.MappingPath(first.Facts.FutureRevision)}/metadata");
            var metadata = await CatalogHttpTestSupport.JsonAsync(metadataResponse);
            Assert.Equal(new[] { "description", "name", "scope", "version" },
                metadata.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
            Assert.Equal("1", metadata.GetProperty("version").GetString());
            Assert.Null(metadataResponse.Headers.ETag); // Conditional metadata is the next increment.

            using var historyResponse = await api.Client.GetAsync($"{CatalogHttpTestSupport.MappingPath(first.Facts.FutureRevision)}/revisions");
            var history = await CatalogHttpTestSupport.JsonAsync(historyResponse);
            Assert.Equal(new[] { "2", "1" }, history.GetProperty("revisions").EnumerateArray()
                .Select(revision => revision.GetProperty("sequence").GetString()));
            AssertPublic(history);

            using var revisionResponse = await api.Client.GetAsync(CatalogHttpTestSupport.RevisionPath(first.Facts.FutureRevision));
            var revision = await CatalogHttpTestSupport.JsonAsync(revisionResponse);
            Assert.Equal("valid", revision.GetProperty("admission").GetString());
            Assert.Equal("original.json", revision.GetProperty("originalFilename").GetString());
            Assert.Equal("entry.native.json", revision.GetProperty("logicalDocument").GetString());
            Assert.Equal(first.Facts.NativeObject.Digest.Value, revision.GetProperty("nativeDigest").GetProperty("value").GetString());
            Assert.Equal("sha256", revision.GetProperty("nativeDigest").GetProperty("algorithm").GetString());
            Assert.Equal(first.Facts.NativeObject.ByteCount, revision.GetProperty("nativeByteCount").GetInt64());
            Assert.Equal(0, DateTimeOffset.Parse(revision.GetProperty("committedAtUtc").GetString()!, CultureInfo.InvariantCulture).Offset.Ticks);
            AssertPublic(revision);
        }
    }

    [PostgresFact]
    public async Task MissingDeniedAndEveryWrongAncestorShare404WhileVisibleEmptyCollectionsRemainSuccessful()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var mapping = await CatalogHttpTestSupport.CommitSyntheticAsync(test);
        await test.Seed.AddMembershipAsync(test.Case, test.Seed.OrganizationA, test.Seed.HiddenProjectA, test.Seed.Alice, "viewer");
        await using var api = await CatalogHttpTestSupport.StartAsync(test.Case);
        using var emptyResponse = await api.Client.GetAsync($"{CatalogHttpTestSupport.ProjectPath(new(test.Seed.OrganizationA, test.Seed.HiddenProjectA))}/mappings");
        Assert.Empty((await CatalogHttpTestSupport.JsonAsync(emptyResponse)).GetProperty("mappings").EnumerateArray());
        var scope = mapping.Facts.FutureRevision;
        foreach (var wrong in new[]
        {
            new RevisionScope(test.Seed.OrganizationB, scope.ProjectId, scope.MappingId, scope.RevisionId),
            new RevisionScope(scope.OrganizationId, test.Seed.HiddenProjectA, scope.MappingId, scope.RevisionId),
            new RevisionScope(scope.OrganizationId, scope.ProjectId, Guid.NewGuid(), scope.RevisionId),
            new RevisionScope(scope.OrganizationId, scope.ProjectId, scope.MappingId, Guid.NewGuid())
        })
        {
            using var response = await api.Client.GetAsync(CatalogHttpTestSupport.RevisionPath(wrong));
            AssertClosedError(await CatalogHttpTestSupport.JsonAsync(response, HttpStatusCode.NotFound), "notFound");
        }
        await test.Case.ExecuteAsync("UPDATE weft.project_memberships SET active=false,version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3",
            test.Project.OrganizationId, test.Project.ProjectId, test.Seed.Alice);
        foreach (var path in ResourcePaths(test.Project, scope))
        {
            using var response = await api.Client.GetAsync(path);
            AssertClosedError(await CatalogHttpTestSupport.JsonAsync(response, HttpStatusCode.NotFound), "notFound");
        }
        using var unknown = await api.Client.GetAsync("/api/v1/catalog/unknown");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.DoesNotContain("<html", await unknown.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [PostgresFact]
    public async Task AdmittedPrincipalAndAdministratorOfAnotherProjectDoNotGainVisibility()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var mapping = await CatalogHttpTestSupport.CommitSyntheticAsync(test);
        await test.Case.ExecuteAsync("UPDATE weft.project_memberships SET active=false,version=version+1 WHERE principal_id=$1", test.Seed.Admitted);
        foreach (var actor in new[] { test.Seed.Admitted, test.Seed.OtherProjectAdmin })
        {
            await using var api = await CatalogHttpTestSupport.StartAsync(test.Case, actor);
            using var context = await api.Client.GetAsync("/api/v1/catalog/context");
            Assert.Equal(HttpStatusCode.OK, context.StatusCode);
            using var hidden = await api.Client.GetAsync(CatalogHttpTestSupport.MappingPath(mapping.Facts.FutureRevision));
            AssertClosedError(await CatalogHttpTestSupport.JsonAsync(hidden, HttpStatusCode.NotFound), "notFound");
            using var projects = await api.Client.GetAsync($"/api/v1/organizations/{test.Project.OrganizationId:D}/projects");
            var projectPage = await CatalogHttpTestSupport.JsonAsync(projects);
            var visible = projectPage.GetProperty("projects").EnumerateArray().ToArray();
            if (actor == test.Seed.Admitted)
                Assert.Empty(visible);
            else
                Assert.Equal(test.Seed.HiddenProjectA.ToString("D"), Assert.Single(visible).GetProperty("scope").GetProperty("projectId").GetString());
        }
    }

    [PostgresFact]
    public async Task EveryRequestObservesPrincipalAdmissionAndMembershipRevocationAndRestoration()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var mapping = await CatalogHttpTestSupport.CommitSyntheticAsync(test);
        await using var api = await CatalogHttpTestSupport.StartAsync(test.Case);
        foreach (var (revoke, restore, values, contextDenied) in new[]
        {
            ("UPDATE weft.principals SET active=false WHERE id=$1", "UPDATE weft.principals SET active=true WHERE id=$1", new object[] { test.Seed.Alice }, true),
            ("UPDATE weft.organization_memberships SET active=false WHERE organization_id=$1 AND principal_id=$2", "UPDATE weft.organization_memberships SET active=true WHERE organization_id=$1 AND principal_id=$2", new object[] { test.Project.OrganizationId, test.Seed.Alice }, true),
            ("UPDATE weft.project_memberships SET active=false,version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3", "UPDATE weft.project_memberships SET active=true,version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3", new object[] { test.Project.OrganizationId, test.Project.ProjectId, test.Seed.Alice }, false)
        })
        {
            await test.Case.ExecuteAsync(revoke, values);
            try
            {
                foreach (var path in ResourcePaths(test.Project, mapping.Facts.FutureRevision))
                {
                    using var denied = await api.Client.GetAsync(path);
                    Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
                }
                using var context = await api.Client.GetAsync("/api/v1/catalog/context");
                Assert.Equal(contextDenied ? HttpStatusCode.NotFound : HttpStatusCode.OK, context.StatusCode);
                if (contextDenied)
                    Assert.False(context.Headers.Contains("Set-Cookie"));
            }
            finally { await test.Case.ExecuteAsync(restore, values); }
            foreach (var path in ResourcePaths(test.Project, mapping.Facts.FutureRevision))
            {
                using var restored = await api.Client.GetAsync(path);
                Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
            }
        }
    }

    [PostgresFact]
    public async Task ProjectMappingAndRevisionCursorsContinueInStableOrderAndRecheckAuthority()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        await test.Seed.AddMembershipAsync(test.Case, test.Seed.OrganizationA, test.Seed.HiddenProjectA, test.Seed.Alice, "viewer");
        var mappingA = await CatalogHttpTestSupport.CommitSyntheticAsync(test);
        var mappingB = await CatalogHttpTestSupport.CommitSyntheticAsync(test);
        var mappingC = await CatalogHttpTestSupport.CommitSyntheticAsync(test);
        var revision2 = await CatalogHttpTestSupport.CommitSyntheticAsync(test, PostgresIngestionTestSupport.AppendIntent(mappingA));
        var revision3 = await CatalogHttpTestSupport.CommitSyntheticAsync(test, PostgresIngestionTestSupport.AppendIntent(mappingA), valid: false);
        await using var api = await CatalogHttpTestSupport.StartAsync(test.Case);
        var projectsPath = $"/api/v1/organizations/{test.Project.OrganizationId:D}/projects";
        var mappingsPath = $"{CatalogHttpTestSupport.ProjectPath(test.Project)}/mappings";
        var revisionsPath = $"{CatalogHttpTestSupport.MappingPath(mappingA.Facts.FutureRevision)}/revisions";
        Assert.Equal(new[] { test.Seed.ProjectA.ToString("D"), test.Seed.HiddenProjectA.ToString("D") },
            await WalkAsync(api.Client, projectsPath, "projects", "projectId"));
        Assert.Equal(new[] { mappingA, mappingB, mappingC }.Select(receipt => receipt.Facts.FutureRevision.MappingId.ToString("D")).Order(StringComparer.Ordinal),
            await WalkAsync(api.Client, mappingsPath, "mappings", "mappingId"));
        Assert.Equal(new[] { revision3, revision2, mappingA }.Select(receipt => receipt.Facts.FutureRevision.RevisionId.ToString("D")),
            await WalkAsync(api.Client, revisionsPath, "revisions", "revisionId"));

        using var first = await api.Client.GetAsync($"{revisionsPath}?limit=1");
        var cursor = (await CatalogHttpTestSupport.JsonAsync(first)).GetProperty("nextCursor").GetString();
        Assert.NotNull(cursor);
        await test.Case.ExecuteAsync("UPDATE weft.project_memberships SET active=false,version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3",
            test.Project.OrganizationId, test.Project.ProjectId, test.Seed.Alice);
        using var denied = await api.Client.GetAsync($"{revisionsPath}?limit=1&cursor={Uri.EscapeDataString(cursor)}");
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
    }

    [PostgresFact]
    public async Task StrictIdsQueriesAndCursorAncestryRejectMalformedRequestsInsteadOfCoercingThem()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var mappingA = await CatalogHttpTestSupport.CommitSyntheticAsync(test);
        var mappingB = await CatalogHttpTestSupport.CommitSyntheticAsync(test);
        await using var api = await CatalogHttpTestSupport.StartAsync(test.Case);
        var collection = $"{CatalogHttpTestSupport.ProjectPath(test.Project)}/mappings";
        using var first = await api.Client.GetAsync($"{collection}?limit=1");
        var cursor = (await CatalogHttpTestSupport.JsonAsync(first)).GetProperty("nextCursor").GetString()!;
        foreach (var query in new[]
        {
            "limit=0", "limit=101", "limit=-1", "limit=1.0", "limit=01", "limit=%2B1", "limit=",
            "limit=1&limit=2", "LIMIT=1", "unknown=value", "cursor=", "cursor=%%%", "cursor=AA==",
            "cursor=" + new string('A', 700), "cursor=" + Uri.EscapeDataString(cursor) + "&cursor=" + Uri.EscapeDataString(cursor)
        })
        {
            using var malformed = await api.Client.GetAsync($"{collection}?{query}");
            AssertClosedError(await CatalogHttpTestSupport.JsonAsync(malformed, HttpStatusCode.BadRequest), "invalidRequest");
        }
        var projectPath = CatalogHttpTestSupport.ProjectPath(test.Project);
        foreach (var badId in new[] { "00000000-0000-0000-0000-000000000000", test.Project.ProjectId.ToString("N"), test.Project.ProjectId.ToString("D").ToUpperInvariant(), "not-a-uuid" })
        {
            using var malformed = await api.Client.GetAsync(projectPath.Replace(test.Project.ProjectId.ToString("D"), badId, StringComparison.Ordinal));
            AssertClosedError(await CatalogHttpTestSupport.JsonAsync(malformed, HttpStatusCode.BadRequest), "invalidRequest");
        }
        foreach (var mismatchedPath in new[]
        {
            $"/api/v1/organizations/{test.Project.OrganizationId:D}/projects",
            $"{CatalogHttpTestSupport.ProjectPath(new(test.Project.OrganizationId, test.Seed.HiddenProjectA))}/mappings",
            $"{CatalogHttpTestSupport.MappingPath(mappingA.Facts.FutureRevision)}/revisions",
            $"{CatalogHttpTestSupport.MappingPath(mappingB.Facts.FutureRevision)}/revisions"
        })
        {
            using var mismatch = await api.Client.GetAsync($"{mismatchedPath}?cursor={Uri.EscapeDataString(cursor)}");
            Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
        }
        using var resourceQuery = await api.Client.GetAsync($"{projectPath}?role=administrator");
        Assert.Equal(HttpStatusCode.BadRequest, resourceQuery.StatusCode);
        using var contextQuery = await api.Client.GetAsync($"/api/v1/catalog/context?principalId={test.Seed.OtherProjectAdmin:D}");
        Assert.Equal(HttpStatusCode.BadRequest, contextQuery.StatusCode);
    }

    [PostgresFact]
    public async Task LargeVersionsAndSequencesRemainExactDecimalStringsAcrossApiRestart()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var mapping = await CatalogHttpTestSupport.CommitSyntheticAsync(test);
        const long exact = 9_007_199_254_740_993;
        // Explicitly synthetic immutable-counter fixture manipulation. All constraints remain
        // enabled and both transition guards are restored atomically before any public read.
        await using (var connection = await test.Case.Owner.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using var disable = new NpgsqlCommand("""
                ALTER TABLE weft.mappings DISABLE TRIGGER mappings_transition_guard;
                ALTER TABLE weft.mapping_revisions DISABLE TRIGGER mapping_revisions_transition_guard;
                """, connection, transaction);
            await disable.ExecuteNonQueryAsync();
            foreach (var sql in new[]
            {
                "UPDATE weft.mapping_revisions SET sequence=$1 WHERE organization_id=$2 AND project_id=$3 AND mapping_id=$4 AND id=$5",
                "UPDATE weft.mappings SET version=$1,head_sequence=$1 WHERE organization_id=$2 AND project_id=$3 AND id=$4"
            })
            {
                await using var command = new NpgsqlCommand(sql, connection, transaction);
                command.Parameters.AddWithValue(exact);
                command.Parameters.AddWithValue(test.Project.OrganizationId);
                command.Parameters.AddWithValue(test.Project.ProjectId);
                command.Parameters.AddWithValue(mapping.Facts.FutureRevision.MappingId);
                if (sql.Contains("id=$5", StringComparison.Ordinal))
                    command.Parameters.AddWithValue(mapping.Facts.FutureRevision.RevisionId);
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }
            // The head foreign key is initially deferred so both counters can change
            // together. Validate and flush those pending events before restoring guards;
            // PostgreSQL refuses ALTER TABLE while its deferred trigger events remain.
            await using (var verifyConstraints = new NpgsqlCommand("SET CONSTRAINTS ALL IMMEDIATE", connection, transaction))
                await verifyConstraints.ExecuteNonQueryAsync();
            await using var enable = new NpgsqlCommand("""
                ALTER TABLE weft.mapping_revisions ENABLE TRIGGER mapping_revisions_transition_guard;
                ALTER TABLE weft.mappings ENABLE TRIGGER mappings_transition_guard;
                """, connection, transaction);
            await enable.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }
        string? before = null;
        for (var generation = 0; generation < 2; generation++)
        {
            await using var api = await CatalogHttpTestSupport.StartAsync(test.Case);
            using var response = await api.Client.GetAsync(CatalogHttpTestSupport.MappingPath(mapping.Facts.FutureRevision));
            var value = await CatalogHttpTestSupport.JsonAsync(response);
            Assert.Equal(exact.ToString(CultureInfo.InvariantCulture), value.GetProperty("version").GetString());
            Assert.Equal(exact.ToString(CultureInfo.InvariantCulture), value.GetProperty("head").GetProperty("sequence").GetString());
            var raw = value.GetRawText();
            if (before is null) before = raw;
            else Assert.Equal(before, raw);
            using var revisionResponse = await api.Client.GetAsync(CatalogHttpTestSupport.RevisionPath(mapping.Facts.FutureRevision));
            Assert.Equal(exact.ToString(CultureInfo.InvariantCulture),
                (await CatalogHttpTestSupport.JsonAsync(revisionResponse)).GetProperty("sequence").GetString());
        }
    }

    [PostgresFact]
    public async Task ActualNarrowRoleGuardFailureBecomesClosedPersistenceErrorWithoutPrivateDetails()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        await using var api = await CatalogHttpTestSupport.StartAsync(test.Case);
        await test.Case.ExecuteAsync("GRANT UPDATE (name) ON weft.mappings TO weft_catalog_reader");
        try
        {
            using var failed = await api.Client.GetAsync(CatalogHttpTestSupport.ProjectPath(test.Project));
            AssertClosedError(await CatalogHttpTestSupport.JsonAsync(failed, HttpStatusCode.InternalServerError), "persistenceFailure");
            Assert.True(failed.Headers.CacheControl?.NoStore);
        }
        finally { await test.Case.ExecuteAsync("REVOKE UPDATE (name) ON weft.mappings FROM weft_catalog_reader"); }
        using var restored = await api.Client.GetAsync(CatalogHttpTestSupport.ProjectPath(test.Project));
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
    }

    [PostgresFact]
    public async Task ActualClientCancellationInterruptsAnObservedDatabaseWaitAndTheSameCatalogPoolRecovers()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        await using var api = await CatalogHttpTestSupport.StartAsync(test.Case);
        var path = CatalogHttpTestSupport.ProjectPath(test.Project);
        using (var warmed = await api.Client.GetAsync(path))
            Assert.Equal(HttpStatusCode.OK, warmed.StatusCode);
        // The ownership pool has maximum size one. A separate explicitly unpooled owner
        // connection observes public lock facts while the ordinary owner lease holds the lock.
        var probeOptions = new NpgsqlConnectionStringBuilder(test.Case.MigrationConnectionString) { Pooling = false };
        await using var probe = new NpgsqlConnection(probeOptions.ConnectionString);
        await probe.OpenAsync();
        await using var blocker = await test.Case.Owner.OpenConnectionAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        using var requestCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task<HttpResponseMessage>? pending = null;
        try
        {
            await using (var acquire = new NpgsqlCommand("LOCK TABLE weft.projects IN ACCESS EXCLUSIVE MODE", blocker, transaction))
                await acquire.ExecuteNonQueryAsync();
            pending = api.Client.GetAsync(path, requestCancellation.Token);
            using var observation = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            var observedWait = false;
            while (!observedWait)
            {
                await using var inspect = new NpgsqlCommand("""
                    SELECT EXISTS (
                        SELECT 1 FROM pg_catalog.pg_locks waiting
                        JOIN pg_catalog.pg_stat_activity activity ON activity.pid = waiting.pid
                        WHERE activity.datname = current_database() AND activity.usename = 'weft_catalog_reader'
                          AND waiting.relation = 'weft.projects'::regclass
                          AND waiting.mode = 'AccessShareLock' AND NOT waiting.granted
                    )
                    """, probe);
                observedWait = (bool)(await inspect.ExecuteScalarAsync(observation.Token))!;
                if (!observedWait)
                {
                    Assert.False(pending.IsCompleted, "The request finished before its catalog database lock wait was observed.");
                    await Task.Delay(TimeSpan.FromMilliseconds(20), observation.Token);
                }
            }
            requestCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                using var unexpected = await pending!.WaitAsync(TimeSpan.FromSeconds(3));
            });
        }
        finally
        {
            requestCancellation.Cancel();
            await transaction.RollbackAsync();
            if (pending is { IsCompleted: false })
            {
                try { using var canceled = await pending.WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (OperationCanceledException) { }
            }
        }
        using var recovered = await api.Client.GetAsync(path);
        var project = await CatalogHttpTestSupport.JsonAsync(recovered);
        Assert.Equal(test.Project.ProjectId.ToString("D"), project.GetProperty("scope").GetProperty("projectId").GetString());
        using var contextResponse = await api.Client.GetAsync("/api/v1/catalog/context");
        Assert.Equal(HttpStatusCode.OK, contextResponse.StatusCode);
    }

    private static IEnumerable<string> ResourcePaths(ProjectScope project, RevisionScope revision)
    {
        yield return CatalogHttpTestSupport.ProjectPath(project);
        yield return $"{CatalogHttpTestSupport.ProjectPath(project)}/mappings";
        yield return CatalogHttpTestSupport.MappingPath(revision);
        yield return $"{CatalogHttpTestSupport.MappingPath(revision)}/metadata";
        yield return $"{CatalogHttpTestSupport.MappingPath(revision)}/revisions";
        yield return CatalogHttpTestSupport.RevisionPath(revision);
    }

    private static async Task<IReadOnlyList<string>> WalkAsync(HttpClient client, string path, string collection, string idName)
    {
        var ids = new List<string>();
        string? cursor = null;
        for (var page = 0; page < 10; page++)
        {
            using var response = await client.GetAsync($"{path}?limit=1" + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}"));
            var json = await CatalogHttpTestSupport.JsonAsync(response);
            var rows = json.GetProperty(collection).EnumerateArray().ToArray();
            Assert.Single(rows);
            ids.Add(rows[0].GetProperty("scope").GetProperty(idName).GetString()!);
            cursor = json.GetProperty("nextCursor").ValueKind == JsonValueKind.Null ? null : json.GetProperty("nextCursor").GetString();
            if (cursor is null)
            {
                Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
                return ids;
            }
        }
        throw new InvalidOperationException("A bounded catalog traversal did not terminate.");
    }

    private static void AssertActionsReadOnly(JsonElement actions)
    {
        Assert.True(actions.GetProperty("view").GetBoolean());
        Assert.All(actions.EnumerateObject().Where(value => value.Name != "view"), value => Assert.False(value.Value.GetBoolean()));
    }

    private static void AssertClosedError(JsonElement error, string code)
    {
        Assert.Equal("code", Assert.Single(error.EnumerateObject()).Name);
        Assert.Equal(code, error.GetProperty("code").GetString());
    }

    private static void AssertPublic(JsonElement value)
    {
        var forbidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "nativeObject", "nativeObjectId", "objectId", "privateReference", "path", "lease", "leaseToken", "claimToken",
            "backendProfile", "engineRevision", "backendBuild", "adapterBuild", "evidence", "attempts", "image", "connectionString",
            "ingestionOperationId", "dependencyManifestId", "exception", "message", "nativeBytes"
        };
        void Visit(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
                foreach (var property in element.EnumerateObject())
                {
                    Assert.DoesNotContain(property.Name, forbidden);
                    Visit(property.Value);
                }
            else if (element.ValueKind == JsonValueKind.Array)
                foreach (var child in element.EnumerateArray()) Visit(child);
        }
        Visit(value);
    }
}
