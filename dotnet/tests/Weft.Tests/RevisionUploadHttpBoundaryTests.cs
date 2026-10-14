using System.Net;
using System.Runtime.Versioning;
using System.Text;
using Weft.Domain;

namespace Weft.Tests;

/// <summary>Real HTTP/1 refusals with source bytes withheld, fresh PostgreSQL authority and cancellable C2 source gates.</summary>
[SupportedOSPlatform("linux")]
[Collection(PostgresCollection.Name)]
public sealed class RevisionUploadHttpBoundaryTests(PostgresFixture fixture)
{
    [RevisionIngestionFact]
    public async Task DisabledIngestionKeepsAllFourDocumentedRoutesClosedWithoutResolvingTheirDependencies()
    {
        await using var api = await RevisionUploadHttpTestSupport.StartAsync(registerIngestion: false);
        const string project = "/api/v1/organizations/invalid/projects/invalid";
        foreach (var path in new[]
        {
            project + "/mappings?name=%GG",
            project + "/mappings/invalid/revisions?head=equal",
            project + "/revision-ingestions/invalid/resume?unknown=true"
        })
        {
            var reply = await api.HeadersOnlyPostAsync(path, null, null,
                contentLength: RevisionUploadHttpTestSupport.MaximumNativeBytes + 1L,
                contentType: "application/json", origin: "https://attacker.invalid", host: "attacker.invalid");
            RevisionUploadHttpTestSupport.AssertRawError(reply, HttpStatusCode.NotFound, "unavailable");
        }
        using (var receipt = await api.Client.GetAsync(project + "/revision-ingestions/invalid?unknown=true"))
            await RevisionUploadHttpTestSupport.AssertErrorAsync(receipt, HttpStatusCode.NotFound, "unavailable");
        AssertUntouched(api);
        Assert.Equal(0, api.ServiceResolutions);
        using var response = await api.Client.GetAsync("/openapi/v1.json");
        var schema = await CatalogHttpTestSupport.JsonAsync(response);
        var paths = schema.GetProperty("paths");
        const string documented = "/api/v1/organizations/{organizationId}/projects/{projectId}";
        Assert.True(paths.GetProperty(documented + "/mappings").TryGetProperty("post", out _));
        Assert.True(paths.GetProperty(documented + "/mappings/{mappingId}/revisions").TryGetProperty("post", out _));
        Assert.True(paths.GetProperty(documented + "/revision-ingestions/{operationId}").TryGetProperty("get", out _));
        Assert.True(paths.GetProperty(documented + "/revision-ingestions/{operationId}/resume").TryGetProperty("post", out _));
    }

    [RevisionIngestionFact]
    public async Task ConfiguredCatalogWithDisabledIngestionDoesNotResolveAnUnregisteredServiceOrReadResources()
    {
        await using var test = await RevisionIngestionTestSupport.CreateAsync(fixture);
        await using var api = await RevisionUploadHttpTestSupport.StartAsync(test, enabled: false, registerIngestion: false);
        var path = RevisionUploadHttpTestSupport.CreatePath(test.Project) + "?name=%FF";
        var reply = await api.HeadersOnlyPostAsync(path, null, null, contentType: null,
            origin: "https://attacker.invalid");
        RevisionUploadHttpTestSupport.AssertRawError(reply, HttpStatusCode.NotFound, "unavailable");
        AssertUntouched(api);
        Assert.Equal(0, api.ServiceResolutions);
        AssertNoNativeWork(test);
    }

    [RevisionIngestionFact]
    public async Task ActualUnsafeTransportAndFrameworkTokensRejectBeforeResourcesOrWithheldSource()
    {
        await using var test = await RevisionIngestionTestSupport.CreateAsync(fixture);
        await using var api = await RevisionUploadHttpTestSupport.StartAsync(test);
        var token = await api.TokenAsync();
        var path = RevisionUploadHttpTestSupport.CreatePath(test.Project) + RevisionUploadHttpTestSupport.CreateQuery();
        var key = Guid.NewGuid().ToString("D");
        foreach (var item in new[]
        {
            (Token: (string?)null, Origin: (string?)null, Host: (string?)null, Cookie: (string?)null),
            (Token: (string?)"", Origin: (string?)null, Host: (string?)null, Cookie: (string?)null),
            (Token: (string?)"invalid", Origin: (string?)null, Host: (string?)null, Cookie: (string?)null),
            (Token: (string?)new string('a', 4097), Origin: (string?)null, Host: (string?)null, Cookie: (string?)null),
            (Token: (string?)token, Origin: (string?)"https://attacker.invalid", Host: (string?)null, Cookie: (string?)null),
            (Token: (string?)token, Origin: (string?)(api.Origin.GetLeftPart(UriPartial.Authority) + "/"), Host: (string?)null, Cookie: (string?)null),
            (Token: (string?)token, Origin: (string?)null, Host: (string?)"attacker.invalid", Cookie: (string?)null),
            (Token: (string?)token, Origin: (string?)null, Host: (string?)$"localhost:{api.Origin.Port}", Cookie: (string?)null),
            (Token: (string?)token, Origin: (string?)null, Host: (string?)null, Cookie: (string?)""),
            (Token: (string?)token, Origin: (string?)null, Host: (string?)null, Cookie: (string?)".Weft.Catalog.Antiforgery=malformed")
        })
        {
            api.ResetObservations();
            var reply = await api.HeadersOnlyPostAsync(path, item.Token, key,
                contentLength: RevisionUploadHttpTestSupport.MaximumNativeBytes + 1L,
                contentType: "application/json", origin: item.Origin, host: item.Host, cookie: item.Cookie,
                additionalHeaders: [new("X-Weft-Principal", test.Seed.Admitted.ToString("D")), new("X-Weft-Role", "administrator")]);
            RevisionUploadHttpTestSupport.AssertRawError(reply, HttpStatusCode.Forbidden, "identityRejected");
            AssertUntouched(api);
        }
        foreach (var header in new[]
        {
            new KeyValuePair<string, string>("X-XSRF-TOKEN", token),
            new KeyValuePair<string, string>("Origin", api.Origin.GetLeftPart(UriPartial.Authority))
        })
        {
            api.ResetObservations();
            var reply = await api.HeadersOnlyPostAsync(path, token, key, additionalHeaders: [header]);
            RevisionUploadHttpTestSupport.AssertRawError(reply, HttpStatusCode.Forbidden, "identityRejected");
            AssertUntouched(api);
        }
        Assert.Equal(0, api.ServiceResolutions);
        AssertNoNativeWork(test);
    }

    [RevisionIngestionFact]
    public async Task CanonicalRouteAndAllowedOrganizationValidationPrecedeResourceReadsAndSource()
    {
        await using var test = await RevisionIngestionTestSupport.CreateAsync(fixture);
        await using var api = await RevisionUploadHttpTestSupport.StartAsync(test);
        var token = await api.TokenAsync();
        var query = RevisionUploadHttpTestSupport.CreateQuery();
        foreach (var path in new[]
        {
            $"/api/v1/organizations/{test.Project.OrganizationId:D}/projects/{test.Project.ProjectId.ToString("D").ToUpperInvariant()}/mappings",
            $"/api/v1/organizations/{test.Project.OrganizationId:D}/projects/{Guid.Empty:D}/mappings",
            $"/api/v1/organizations/{test.Project.OrganizationId:D}/projects/{test.Project.ProjectId:N}/mappings",
            $"/api/v1/organizations/{test.Project.OrganizationId:D}/projects/not-a-uuid/mappings"
        })
        {
            api.ResetObservations();
            var reply = await api.HeadersOnlyPostAsync(path + query, token, Guid.NewGuid().ToString("D"));
            RevisionUploadHttpTestSupport.AssertRawError(reply, HttpStatusCode.BadRequest, "invalidRequest");
            AssertUntouched(api);
        }
        foreach (var path in new[]
        {
            RevisionUploadHttpTestSupport.CreatePath(test.Project) + $"/{Guid.Empty:D}/revisions",
            RevisionUploadHttpTestSupport.CreatePath(test.Project) + "/ABCDEFAB-CDEF-4ABC-8DEF-ABCDEFABCDEF/revisions",
            CatalogHttpTestSupport.ProjectPath(test.Project) + $"/revision-ingestions/{Guid.Empty:D}/resume",
            CatalogHttpTestSupport.ProjectPath(test.Project) + "/revision-ingestions/ABCDEFAB-CDEF-4ABC-8DEF-ABCDEFABCDEF/resume"
        })
        {
            api.ResetObservations();
            var key = path.EndsWith("/resume", StringComparison.Ordinal) ? null : Guid.NewGuid().ToString("D");
            var reply = await api.HeadersOnlyPostAsync(path, token, key);
            RevisionUploadHttpTestSupport.AssertRawError(reply, HttpStatusCode.BadRequest, "invalidRequest");
            AssertUntouched(api);
        }
        foreach (var operation in new[] { Guid.Empty.ToString("D"), "ABCDEFAB-CDEF-4ABC-8DEF-ABCDEFABCDEF", "invalid" })
        {
            api.ResetObservations();
            using var response = await api.Client.GetAsync(CatalogHttpTestSupport.ProjectPath(test.Project) + "/revision-ingestions/" + operation);
            await RevisionUploadHttpTestSupport.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalidRequest");
            AssertUntouched(api);
        }
        api.ResetObservations();
        var outside = await api.HeadersOnlyPostAsync(
            RevisionUploadHttpTestSupport.CreatePath(new(test.Seed.OrganizationB, test.Seed.ProjectA)) + "?unknown=%FF",
            token, "invalid", contentType: null);
        RevisionUploadHttpTestSupport.AssertRawError(outside, HttpStatusCode.NotFound, "notFound");
        AssertUntouched(api);
        Assert.Equal(0, api.ServiceResolutions);
        AssertNoNativeWork(test);
    }

    [RevisionIngestionFact]
    public async Task CreateQueryRejectsAmbiguousUnknownMalformedUtf8AndOutOfBoundTextBeforeResourcesOrSource()
    {
        await using var test = await RevisionIngestionTestSupport.CreateAsync(fixture);
        await using var api = await RevisionUploadHttpTestSupport.StartAsync(test);
        var token = await api.TokenAsync();
        var path = RevisionUploadHttpTestSupport.CreatePath(test.Project);
        foreach (var query in new[]
        {
            "", "?name=Name", "?backendId=ferrule", "?name=Name&backendId=ferrule&unknown=true",
            "?Name=Name&backendId=ferrule", "?name=Name&BackendId=ferrule",
            "?name=Name&backendId=ferrule&name=Other", "?name=Name&backendId=ferrule&na%6De=Other",
            "?name=Name&backendId=ferrule&description=one&description=two",
            "?name=Name&backendId=ferrule&originalFilename=one&originalFilename=two",
            "?name=Name&backendId=ferrule&backendId=ferrule", "?name=Name&backendId=ferrule&",
            "?name=%&backendId=ferrule", "?name=%0&backendId=ferrule", "?name=%GG&backendId=ferrule",
            "?name=%FF&backendId=ferrule", "?name=%C0%AF&backendId=ferrule", "?name=%ED%A0%80&backendId=ferrule",
            "?name=%F4%90%80%80&backendId=ferrule", "?name=%E2%82&backendId=ferrule",
            "?na%FFme=Name&backendId=ferrule", "?name=%00&backendId=ferrule", "?name=+++&backendId=ferrule",
            "?name=Name&backendId=", "?name=Name&backendId=+++", "?name=Name&backendId=ferrule%00",
            "?name=Name&backendId=ferrule&description=%00",
            RevisionUploadHttpTestSupport.CreateQuery(name: Repeat("😀", 201)),
            RevisionUploadHttpTestSupport.CreateQuery(description: Repeat("😀", 4001)),
            RevisionUploadHttpTestSupport.CreateQuery(backendId: new string('x', 129)),
            RevisionUploadHttpTestSupport.CreateQuery(backendId: Repeat("😀", 33)),
            RevisionUploadHttpTestSupport.CreateQuery(filename: ""),
            RevisionUploadHttpTestSupport.CreateQuery(filename: "."),
            RevisionUploadHttpTestSupport.CreateQuery(filename: ".."),
            RevisionUploadHttpTestSupport.CreateQuery(filename: "folder/entry.json"),
            RevisionUploadHttpTestSupport.CreateQuery(filename: "folder\\entry.json"),
            RevisionUploadHttpTestSupport.CreateQuery(filename: "entry\n.json"),
            RevisionUploadHttpTestSupport.CreateQuery(filename: "entry\0.json"),
            RevisionUploadHttpTestSupport.CreateQuery(filename: new string('x', 256)),
            RevisionUploadHttpTestSupport.CreateQuery(filename: Repeat("😀", 64))
        })
        {
            api.ResetObservations();
            var reply = await api.HeadersOnlyPostAsync(path + query, token, Guid.NewGuid().ToString("D"));
            RevisionUploadHttpTestSupport.AssertRawError(reply, HttpStatusCode.BadRequest, "invalidRequest");
            AssertUntouched(api);
        }
        Assert.Equal(0, api.ServiceResolutions);
        AssertNoNativeWork(test);
    }

    [RevisionIngestionFact]
    public async Task IdempotencyKeysAndUnsupportedConditionsAreCanonicalAndUnambiguousBeforeResourcesOrSource()
    {
        await using var test = await RevisionIngestionTestSupport.CreateAsync(fixture);
        await using var api = await RevisionUploadHttpTestSupport.StartAsync(test);
        var token = await api.TokenAsync();
        var path = RevisionUploadHttpTestSupport.CreatePath(test.Project) + RevisionUploadHttpTestSupport.CreateQuery();
        var key = Guid.Parse("abcdefab-cdef-4abc-8def-abcdefabcdef");
        foreach (var text in new string?[]
        {
            null, "", "invalid", Guid.Empty.ToString("D"), key.ToString("D").ToUpperInvariant(),
            key.ToString("N"), key.ToString("B"), key.ToString("D") + "," + key.ToString("D"),
            key.ToString("D").Insert(8, " ")
        })
        {
            api.ResetObservations();
            var reply = await api.HeadersOnlyPostAsync(path, token, text);
            RevisionUploadHttpTestSupport.AssertRawError(reply, HttpStatusCode.BadRequest, "invalidRequest");
            AssertUntouched(api);
        }
        foreach (var header in new[]
        {
            new KeyValuePair<string, string>("Idempotency-Key", key.ToString("D")),
            new KeyValuePair<string, string>("If-Match", "\"head\""),
            new KeyValuePair<string, string>("If-None-Match", "*")
        })
        {
            api.ResetObservations();
            var reply = await api.HeadersOnlyPostAsync(path, token, key.ToString("D"), additionalHeaders: [header]);
            RevisionUploadHttpTestSupport.AssertRawError(reply, HttpStatusCode.BadRequest, "invalidRequest");
            AssertUntouched(api);
        }
        Assert.Equal(0, api.ServiceResolutions);
        AssertNoNativeWork(test);
    }

    [RevisionIngestionFact]
    public async Task AppendHeadContradictionsAndCallerBackendOrMetadataAreRejectedBeforeLookingUpAnyTarget()
    {
        await using var test = await RevisionIngestionTestSupport.CreateAsync(fixture);
        await using var api = await RevisionUploadHttpTestSupport.StartAsync(test);
        var token = await api.TokenAsync();
        var target = new MappingScope(test.Project.OrganizationId, test.Project.ProjectId, Guid.NewGuid());
        var expected = Guid.Parse("abcdefab-cdef-4abc-8def-abcdefabcdef");
        var path = RevisionUploadHttpTestSupport.AppendPath(target);
        foreach (var query in new[]
        {
            "?head=equal", "?head=Equal", "?head=ANY", "?head=", "?head=latest", "?head=any&head=any",
            $"?expectedHeadRevisionId={expected:D}", $"?head=any&expectedHeadRevisionId={expected:D}",
            "?head=equal&expectedHeadRevisionId=invalid", $"?head=equal&expectedHeadRevisionId={Guid.Empty:D}",
            $"?head=equal&expectedHeadRevisionId={expected.ToString("D").ToUpperInvariant()}",
            $"?head=equal&expectedHeadRevisionId={expected:N}",
            $"?head=equal&expectedHeadRevisionId={expected:D}&expectedHeadRevisionId={expected:D}",
            "?name=New", "?description=New", "?backendId=ferrule", "?actor=caller", "?role=administrator",
            "?digest=caller", "?privateReference=caller", "?profile=caller", "?policy=caller", "?version=1",
            "?head=any&originalFilename=folder%2Fentry.json", "?head=%FF", "?he%61d=any&head=any"
        })
        {
            api.ResetObservations();
            var reply = await api.HeadersOnlyPostAsync(path + query, token, Guid.NewGuid().ToString("D"));
            RevisionUploadHttpTestSupport.AssertRawError(reply, HttpStatusCode.BadRequest, "invalidRequest");
            AssertUntouched(api);
        }
        foreach (var query in new[] { "", "?head=any", $"?head=equal&expectedHeadRevisionId={expected:D}" })
        {
            api.ResetObservations();
            var reply = await api.HeadersOnlyPostAsync(path + query, token, Guid.NewGuid().ToString("D"), contentType: null);
            RevisionUploadHttpTestSupport.AssertRawError(reply, HttpStatusCode.NotFound, "notFound");
            AssertNoSourceOrService(api, 1);
        }
        Assert.Equal(0, api.ServiceResolutions);
        AssertNoNativeWork(test);
    }

    [RevisionIngestionFact]
    public async Task ReceiptAndResumeRejectQueriesAndResumeKeysBeforeResolvingC2OrReadingWithheldBody()
    {
        await using var test = await RevisionIngestionTestSupport.CreateAsync(fixture);
        await using var api = await RevisionUploadHttpTestSupport.StartAsync(test);
        var token = await api.TokenAsync();
        var receipt = RevisionUploadHttpTestSupport.ReceiptPath(test.Project, Guid.NewGuid());
        foreach (var query in new[] { "?unknown=1", "?operationId=caller", "?actor=caller", "?name=one&name=two" })
        {
            api.ResetObservations();
            using (var response = await api.Client.GetAsync(receipt + query))
                await RevisionUploadHttpTestSupport.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalidRequest");
            AssertUntouched(api);
            var reply = await api.HeadersOnlyPostAsync(receipt + "/resume" + query, token, null);
            RevisionUploadHttpTestSupport.AssertRawError(reply, HttpStatusCode.BadRequest, "invalidRequest");
            AssertUntouched(api);
        }
        foreach (var header in new[]
        {
            new KeyValuePair<string, string>("Idempotency-Key", Guid.NewGuid().ToString("D")),
            new KeyValuePair<string, string>("If-Match", "\"head\""),
            new KeyValuePair<string, string>("If-None-Match", "*")
        })
        {
            api.ResetObservations();
            var reply = await api.HeadersOnlyPostAsync(receipt + "/resume", token, null, additionalHeaders: [header]);
            RevisionUploadHttpTestSupport.AssertRawError(reply, HttpStatusCode.BadRequest, "invalidRequest");
            AssertUntouched(api);
        }
        Assert.Equal(0, api.ServiceResolutions);
        AssertNoNativeWork(test);
    }

    [RevisionIngestionFact]
    public async Task CurrentViewerRunnerAndHiddenAncestorPreflightDenyBeforeMediaOrSource()
    {
        await using var test = await RevisionIngestionTestSupport.CreateAsync(fixture);
        await using var api = await RevisionUploadHttpTestSupport.StartAsync(test);
        var token = await api.TokenAsync();
        var query = RevisionUploadHttpTestSupport.CreateQuery();
        foreach (var role in new[] { "viewer", "runner", "publisher", "administrator", "viewer" })
        {
            await RoleAsync(test, role);
            api.ResetObservations();
            var reply = await api.HeadersOnlyPostAsync(RevisionUploadHttpTestSupport.CreatePath(test.Project) + query,
                token, Guid.NewGuid().ToString("D"), contentType: null);
            var allowed = role is "publisher" or "administrator";
            RevisionUploadHttpTestSupport.AssertRawError(reply,
                allowed ? HttpStatusCode.UnsupportedMediaType : HttpStatusCode.NotFound,
                allowed ? "unsupportedMediaType" : "notFound");
            AssertNoSourceOrService(api, 1);
        }
        await RoleAsync(test, "publisher");
        foreach (var project in new[]
        {
            new ProjectScope(test.Project.OrganizationId, test.Seed.HiddenProjectA),
            new ProjectScope(test.Project.OrganizationId, Guid.NewGuid())
        })
        {
            api.ResetObservations();
            var reply = await api.HeadersOnlyPostAsync(RevisionUploadHttpTestSupport.CreatePath(project) + query,
                token, Guid.NewGuid().ToString("D"), contentType: null);
            RevisionUploadHttpTestSupport.AssertRawError(reply, HttpStatusCode.NotFound, "notFound");
            AssertNoSourceOrService(api, 1);
        }
        await using var otherAdmin = await RevisionUploadHttpTestSupport.StartAsync(test, principal: test.Seed.OtherProjectAdmin);
        var otherToken = await otherAdmin.TokenAsync();
        otherAdmin.ResetObservations();
        var ancestor = await otherAdmin.HeadersOnlyPostAsync(RevisionUploadHttpTestSupport.CreatePath(test.Project) + query,
            otherToken, Guid.NewGuid().ToString("D"), contentType: null);
        RevisionUploadHttpTestSupport.AssertRawError(ancestor, HttpStatusCode.NotFound, "notFound");
        AssertNoSourceOrService(otherAdmin, 1);
        Assert.Equal(0, api.ServiceResolutions);
        Assert.Equal(0, otherAdmin.ServiceResolutions);
        AssertNoNativeWork(test);
    }

    [RevisionIngestionFact]
    public async Task RawMediaCodingAndKnownHardLimitAreCheckedAfterAuthorityWithoutReadingDeclaredBytes()
    {
        await using var test = await RevisionIngestionTestSupport.CreateAsync(fixture);
        await using var api = await RevisionUploadHttpTestSupport.StartAsync(test);
        var token = await api.TokenAsync();
        var path = RevisionUploadHttpTestSupport.CreatePath(test.Project) + RevisionUploadHttpTestSupport.CreateQuery();
        foreach (var item in new[]
        {
            (Type: (string?)null, Encoding: (string?)null, Length: 128L, Status: HttpStatusCode.UnsupportedMediaType, Code: "unsupportedMediaType"),
            (Type: (string?)"application/json", Encoding: (string?)null, Length: 128L, Status: HttpStatusCode.UnsupportedMediaType, Code: "unsupportedMediaType"),
            (Type: (string?)"multipart/form-data; boundary=example", Encoding: (string?)null, Length: 128L, Status: HttpStatusCode.UnsupportedMediaType, Code: "unsupportedMediaType"),
            (Type: (string?)"application/octet-stream; charset=utf-8", Encoding: (string?)null, Length: 128L, Status: HttpStatusCode.UnsupportedMediaType, Code: "unsupportedMediaType"),
            (Type: (string?)"application/octet-stream, application/octet-stream", Encoding: (string?)null, Length: 128L, Status: HttpStatusCode.UnsupportedMediaType, Code: "unsupportedMediaType"),
            (Type: (string?)RevisionUploadHttpTestSupport.MediaType, Encoding: (string?)"gzip", Length: 128L, Status: HttpStatusCode.UnsupportedMediaType, Code: "unsupportedMediaType"),
            (Type: (string?)RevisionUploadHttpTestSupport.MediaType, Encoding: (string?)"identity, identity", Length: 128L, Status: HttpStatusCode.UnsupportedMediaType, Code: "unsupportedMediaType"),
            (Type: (string?)RevisionUploadHttpTestSupport.MediaType, Encoding: (string?)"", Length: 128L, Status: HttpStatusCode.UnsupportedMediaType, Code: "unsupportedMediaType"),
            (Type: (string?)RevisionUploadHttpTestSupport.MediaType, Encoding: (string?)null, Length: RevisionUploadHttpTestSupport.MaximumNativeBytes + 1L, Status: HttpStatusCode.RequestEntityTooLarge, Code: "bodyLimit")
        })
        {
            api.ResetObservations();
            var reply = await api.HeadersOnlyPostAsync(path, token, Guid.NewGuid().ToString("D"),
                contentLength: item.Length, contentType: item.Type, encoding: item.Encoding);
            RevisionUploadHttpTestSupport.AssertRawError(reply, item.Status, item.Code);
            AssertNoSourceOrService(api, 1);
        }
        foreach (var header in new[]
        {
            new KeyValuePair<string, string>("Content-Type", RevisionUploadHttpTestSupport.MediaType),
            new KeyValuePair<string, string>("Content-Encoding", "identity")
        })
        {
            api.ResetObservations();
            var reply = await api.HeadersOnlyPostAsync(path, token, Guid.NewGuid().ToString("D"),
                encoding: "identity", additionalHeaders: [header]);
            RevisionUploadHttpTestSupport.AssertRawError(reply, HttpStatusCode.UnsupportedMediaType, "unsupportedMediaType");
            AssertNoSourceOrService(api, 1);
        }
        Assert.Equal(0, api.ServiceResolutions);
        AssertNoNativeWork(test);
    }

    [RevisionIngestionFact]
    public async Task PublisherAndAdministratorReachTheActualC2SourceOnlyAfterPreflightHasFinished()
    {
        await using var test = await RevisionIngestionTestSupport.CreateAsync(fixture);
        await using var api = await RevisionUploadHttpTestSupport.StartAsync(test);
        var token = await api.TokenAsync();
        var path = RevisionUploadHttpTestSupport.CreatePath(test.Project) + RevisionUploadHttpTestSupport.CreateQuery();
        foreach (var role in new[] { "publisher", "administrator" })
        {
            await RoleAsync(test, role);
            await ReachSourceThenCancelAsync(test, api, path, token);
        }
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.revision_ingestions"));
        AssertNoNativeWork(test);
    }

    [RevisionIngestionFact]
    public async Task MaximumSupplementaryScalarQueryFitsTheActualHttp1LimitAndReachesSourceWithoutReplacementDecoding()
    {
        await using var test = await RevisionIngestionTestSupport.CreateAsync(fixture);
        await using var api = await RevisionUploadHttpTestSupport.StartAsync(test);
        var token = await api.TokenAsync();
        var query = RevisionUploadHttpTestSupport.CreateQuery(name: Repeat("😀", 200),
            description: Repeat("😀", 4000), filename: Repeat("😀", 63) + "abc");
        var path = RevisionUploadHttpTestSupport.CreatePath(test.Project) + query;
        var framedBytes = Encoding.ASCII.GetByteCount($"POST {path} HTTP/1.1\r\n");
        Assert.InRange(framedBytes, 48_000, RevisionUploadHttpTestSupport.MaximumRequestLineBytes);
        await ReachSourceThenCancelAsync(test, api, path, token);
        // Missing optional fields, protocol OWS, literal plus and once-only percent decoding remain accepted.
        await ReachSourceThenCancelAsync(test, api,
            RevisionUploadHttpTestSupport.CreatePath(test.Project) + "?name=Name&backendId=ferrule", token);
        await ReachSourceThenCancelAsync(test, api,
            RevisionUploadHttpTestSupport.CreatePath(test.Project) +
            "?name=+Name%2B%252F+&backendId=ferrule&description=%F0%9F%98%80+%2B%2525&originalFilename=a%252F.native.json",
            token, protocolOws: true);
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.revision_ingestions"));
        AssertNoNativeWork(test);
    }

    [RevisionIngestionFact]
    public async Task MalformedAndOversizedHttp1RequestLinesAreRejectedByTransportBeforeApiAdmission()
    {
        await using var api = await RevisionUploadHttpTestSupport.StartAsync(registerIngestion: false);
        var malformed = await api.HeadersOnlyPostAsync("/api/v1/catalog/context?name=two words", null, null, cookie: "");
        Assert.Equal(HttpStatusCode.BadRequest, malformed.Status);
        Assert.Empty(malformed.Body);
        Assert.False(malformed.Headers.ContainsKey("Location"));
        AssertUntouched(api);
        var path = "/api/v1/catalog/context?name=" + new string('x', RevisionUploadHttpTestSupport.MaximumRequestLineBytes);
        Assert.True(Encoding.ASCII.GetByteCount($"POST {path} HTTP/1.1\r\n") > RevisionUploadHttpTestSupport.MaximumRequestLineBytes);
        var oversized = await api.HeadersOnlyPostAsync(path, null, null, cookie: "");
        Assert.Equal(HttpStatusCode.RequestUriTooLong, oversized.Status);
        Assert.Empty(oversized.Body);
        Assert.False(oversized.Headers.ContainsKey("Location"));
        AssertUntouched(api);
        Assert.Equal(0, api.ServiceResolutions);
    }

    private static async Task ReachSourceThenCancelAsync(RevisionIngestionTestSupport test,
        RevisionUploadHttpTestSupport api, string path, string token, bool protocolOws = false)
    {
        api.ResetObservations();
        var gate = api.GateBody();
        using var cancellation = new CancellationTokenSource();
        var key = Guid.NewGuid();
        var pending = api.PostAsync(path, token, protocolOws ? null : key, [255],
            contentType: "Application/Octet-Stream", encoding: "IDENTITY", cancellationToken: cancellation.Token,
            additionalHeaders: protocolOws ? [new("Idempotency-Key", " \t" + key.ToString("D") + "\t ")] : null);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, api.ResourceReads);
            Assert.Equal(1, api.ServiceCalls);
            Assert.Equal(0, api.CompletedServiceCalls);
            Assert.Equal(1, api.BodyReads);
            Assert.Equal(0L, api.BodyBytes);
            Assert.Equal(new[] { "project-read-completed", "service-entered", "body-read-entered" }, api.Events.ToArray());
            await api.AssertNoHeldCatalogTransactionAsync();
            await test.AssertNoHeldTransactionsAsync();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            await gate.RequestEnded.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, api.CompletedServiceCalls);
            Assert.Equal(0L, api.BodyBytes);
            AssertNoNativeWork(test);
        }
        finally
        {
            cancellation.Cancel();
            gate.Release.TrySetResult();
            try
            {
                using var response = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException) { }
            finally { api.ClearBodyGate(); }
        }
    }

    private static Task RoleAsync(RevisionIngestionTestSupport test, string role) => test.Case.ExecuteAsync(
        "UPDATE weft.project_memberships SET role=$1,version=version+1 WHERE organization_id=$2 AND project_id=$3 AND principal_id=$4",
        role, test.Project.OrganizationId, test.Project.ProjectId, test.Seed.Alice);

    private static string Repeat(string text, int count) => string.Concat(Enumerable.Repeat(text, count));

    private static void AssertUntouched(RevisionUploadHttpTestSupport api) => AssertNoSourceOrService(api, 0);

    private static void AssertNoSourceOrService(RevisionUploadHttpTestSupport api, int resourceReads)
    {
        Assert.Equal(resourceReads, api.ResourceReads);
        Assert.Equal(0, api.ServiceCalls);
        Assert.Equal(0, api.ReceiptCalls);
        Assert.Equal(0, api.BodyReads);
        Assert.Equal(0L, api.BodyBytes);
    }

    private static void AssertNoNativeWork(RevisionIngestionTestSupport test)
    {
        Assert.Equal(0, test.StorePort.PublishCalls);
        Assert.Equal(0, test.StorePort.ReadCalls);
        Assert.Equal(0, test.BackendPort.Calls);
        Assert.Empty(test.Runner.NativeBodies);
        Assert.Empty(test.Launcher.Commands);
    }
}
