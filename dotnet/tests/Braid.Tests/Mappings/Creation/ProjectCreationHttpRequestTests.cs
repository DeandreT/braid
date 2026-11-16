using System.Net;
using System.Text;
using System.Text.Json;
using Braid.Application;
using Braid.Domain;
using static Braid.Tests.ProjectCreationHttpBoundaryTests;

namespace Braid.Tests;

/// <summary>Actual HTTP parsing and bounded I/O; creator/admission ports are explicitly synthetic.</summary>
public sealed class ProjectCreationHttpRequestTests
{
    [Fact]
    public async Task IdempotencyHeaderRequiresOneCanonicalNonzeroKeyAfterAdvisoryAdmissionBeforeWithheldBody()
    {
        var ports = new SyntheticPorts();
        await using var api = await StartSyntheticAsync(ports);
        var token = await api.TokenAsync();
        var path = ProjectCreationHttpTestSupport.CreatePath(Organization);
        var key = Guid.Parse("abcdef34-5678-49ab-cdef-23456789abcd");
        foreach (var invalid in new string?[] { null, "", " ", "invalid", Guid.Empty.ToString("D"), key.ToString("N"),
            key.ToString("B"), key.ToString("P"), key.ToString("D").ToUpperInvariant(),
            key.ToString("D") + ", " + key.ToString("D"), new('a', 129), key.ToString("D")[..18] + " " + key.ToString("D")[18..] })
        {
            ports.Reset(); api.ResetObservations();
            var reply = await HeadersOnlyAsync(api, path, token, invalid, origin: api.Origin.GetLeftPart(UriPartial.Authority));
            AssertRawError(reply, HttpStatusCode.BadRequest, "invalidRequest");
            Assert.Equal(1, ports.CreationAdmissions);
            Assert.Equal(0, ports.Creates);
            Assert.Equal(0, api.BodyReads);
        }
        ports.Reset(); api.ResetObservations();
        var duplicate = await HeadersOnlyAsync(api, path, token, key.ToString("D"), origin: api.Origin.GetLeftPart(UriPartial.Authority),
            additionalHeaders: [new("Idempotency-Key", key.ToString("D"))]);
        AssertRawError(duplicate, HttpStatusCode.BadRequest, "invalidRequest");
        Assert.Equal(1, ports.CreationAdmissions);
        Assert.Equal(0, ports.Creates);
        Assert.Equal(0, api.BodyReads);
        using var request = CreateRequest(path, token, " \t" + key.ToString("D") + "\t ", Edit("OWS accepted", ""),
            api.Origin.GetLeftPart(UriPartial.Authority));
        using var accepted = await api.Client.SendAsync(request);
        await ResultAsync(accepted);
        Assert.Equal(key, Assert.Single(ports.Requests).Key);
    }

    [Fact]
    public async Task UnsupportedConditionalHeadersAreRejectedAfterAdvisoryReadBeforeBody()
    {
        var ports = new SyntheticPorts();
        await using var api = await StartSyntheticAsync(ports);
        var token = await api.TokenAsync();
        foreach (var condition in new[] { "If-Match", "If-None-Match" })
        {
            foreach (var value in new[] { "*", "\"unrelated\"", "" })
            {
                ports.Reset(); api.ResetObservations();
                var reply = await HeadersOnlyAsync(api, ProjectCreationHttpTestSupport.CreatePath(Organization), token,
                    Guid.NewGuid().ToString("D"), origin: api.Origin.GetLeftPart(UriPartial.Authority),
                    additionalHeaders: [new(condition, value)]);
                AssertRawError(reply, HttpStatusCode.BadRequest, "invalidRequest");
                Assert.Equal(1, ports.CreationAdmissions);
                Assert.Equal(0, ports.Creates);
                Assert.Equal(0, api.BodyReads);
            }
        }
    }

    [Fact]
    public async Task VendorMediaAndIdentityEncodingAreValidatedAfterAuthorityBeforeAnyDeclaredEntityByte()
    {
        var ports = new SyntheticPorts();
        await using var api = await StartSyntheticAsync(ports);
        var token = await api.TokenAsync();
        var path = ProjectCreationHttpTestSupport.CreatePath(Organization);
        foreach (var invalid in new string?[]
        {
            null, "", "application/json", "text/json", "application/x-www-form-urlencoded", "multipart/form-data; boundary=x",
            "application/vnd.braid.project-create.v2+json", MediaType + "; charset=iso-8859-1",
            MediaType + "; version=1", MediaType + "; charset=utf-8; charset=utf-8",
            MediaType + "; charset=utf-8; extra=value", MediaType + ", " + MediaType
        })
        {
            ports.Reset(); api.ResetObservations();
            var reply = await HeadersOnlyAsync(api, path, token, Guid.NewGuid().ToString("D"), contentType: invalid,
                origin: api.Origin.GetLeftPart(UriPartial.Authority));
            AssertRawError(reply, HttpStatusCode.UnsupportedMediaType, "unsupportedMediaType");
            Assert.Equal(1, ports.CreationAdmissions);
            Assert.Equal(0, ports.Creates);
            Assert.Equal(0, api.BodyReads);
        }
        foreach (var coding in new[] { "", "gzip", "br", "identity, identity", "identity, gzip" })
        {
            ports.Reset(); api.ResetObservations();
            var reply = await HeadersOnlyAsync(api, path, token, Guid.NewGuid().ToString("D"), contentEncoding: coding,
                origin: api.Origin.GetLeftPart(UriPartial.Authority));
            AssertRawError(reply, HttpStatusCode.UnsupportedMediaType, "unsupportedMediaType");
            Assert.Equal(1, ports.CreationAdmissions);
            Assert.Equal(0, ports.Creates);
            Assert.Equal(0, api.BodyReads);
        }
        foreach (var header in new[] { "Content-Type", "Content-Encoding" })
        {
            ports.Reset(); api.ResetObservations();
            var duplicate = await HeadersOnlyAsync(api, path, token, Guid.NewGuid().ToString("D"),
                contentEncoding: header == "Content-Encoding" ? "identity" : null,
                origin: api.Origin.GetLeftPart(UriPartial.Authority),
                additionalHeaders: [new(header, header == "Content-Type" ? MediaType : "identity")]);
            AssertRawError(duplicate, HttpStatusCode.UnsupportedMediaType, "unsupportedMediaType");
            Assert.Equal(1, ports.CreationAdmissions);
            Assert.Equal(0, ports.Creates);
            Assert.Equal(0, api.BodyReads);
        }
        foreach (var acceptedType in new[] { MediaType, MediaType + "; charset=utf-8", MediaType + "; charset=UTF-8",
            MediaType + "; charset=\"utf-8\"" })
        {
            using var accepted = await api.PostAsync(path, token, Guid.NewGuid(), Edit("UTF8 accepted", ""),
                contentType: acceptedType, encoding: "identity");
            await ResultAsync(accepted);
        }
    }

    [Theory]
    [MemberData(nameof(InvalidEntities))]
    public async Task InvalidJsonUtf8UnicodeAndShapeNeverReachTheFinalCreator(string reason, byte[] entity)
    {
        Assert.False(string.IsNullOrWhiteSpace(reason));
        var ports = new SyntheticPorts();
        await using var api = await StartSyntheticAsync(ports);
        var token = await api.TokenAsync();
        ports.Reset(); api.ResetObservations();
        using var response = await api.PostAsync(ProjectCreationHttpTestSupport.CreatePath(Organization), token,
            Guid.NewGuid(), entity);
        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalidRequest");
        Assert.Equal(1, ports.CreationAdmissions);
        Assert.Equal(0, ports.Creates);
        Assert.Equal(0, api.CreatorResolutions);
        Assert.True(api.BodyBytes <= MaximumBodyBytes);
    }

    public static IEnumerable<object[]> InvalidEntities()
    {
        foreach (var (reason, json) in new (string, string)[]
        {
            ("empty", ""), ("whitespace", " \t\r\n"), ("null root", "null"), ("array root", "[]"),
            ("scalar root", "\"value\""), ("missing both", "{}"), ("missing name", "{\"description\":\"\"}"),
            ("missing description", "{\"name\":\"Name\"}"), ("uppercase name", "{\"Name\":\"Name\",\"description\":\"\"}"),
            ("uppercase description", "{\"name\":\"Name\",\"Description\":\"\"}"),
            ("duplicate name", "{\"name\":\"Name\",\"name\":\"Name\",\"description\":\"\"}"),
            ("escaped duplicate name", "{\"name\":\"Name\",\"na\\u006de\":\"Name\",\"description\":\"\"}"),
            ("duplicate description", "{\"name\":\"Name\",\"description\":\"\",\"description\":\"\"}"),
            ("escaped duplicate description", "{\"name\":\"Name\",\"description\":\"\",\"descriptio\\u006e\":\"\"}"),
            ("null name", "{\"name\":null,\"description\":\"\"}"), ("null description", "{\"name\":\"Name\",\"description\":null}"),
            ("numeric name", "{\"name\":1,\"description\":\"\"}"), ("boolean description", "{\"name\":\"Name\",\"description\":true}"),
            ("object name", "{\"name\":{},\"description\":\"\"}"), ("array description", "{\"name\":\"Name\",\"description\":[]}"),
            ("depth three", "{\"name\":{\"nested\":{}},\"description\":\"\"}"),
            ("comment", "{/*comment*/\"name\":\"Name\",\"description\":\"\"}"),
            ("trailing comma", "{\"name\":\"Name\",\"description\":\"\",}"),
            ("trailing object", "{\"name\":\"Name\",\"description\":\"\"}{}"),
            ("trailing scalar", "{\"name\":\"Name\",\"description\":\"\"}true"),
            ("incomplete", "{\"name\":\"Name\",\"description\":"),
            ("bad escape", "{\"name\":\"\\x41\",\"description\":\"\"}"),
            ("high surrogate name", "{\"name\":\"\\ud800\",\"description\":\"\"}"),
            ("low surrogate description", "{\"name\":\"Name\",\"description\":\"\\udfff\"}"),
            ("wrong surrogate pair", "{\"name\":\"\\ud800\\ud800\",\"description\":\"\"}"),
            ("NUL name", "{\"name\":\"Name\\u0000\",\"description\":\"\"}"),
            ("NUL description", "{\"name\":\"Name\",\"description\":\"\\u0000\"}"),
            ("unescaped newline", "{\"name\":\"Name\n\",\"description\":\"\"}"),
            ("empty accepted name", "{\"name\":\"\",\"description\":\"\"}"),
            ("whitespace accepted name", "{\"name\":\" \\t\\r\\n \",\"description\":\"\"}")
        }) yield return [reason, Encoding.UTF8.GetBytes(json)];
        yield return ["UTF8 BOM", new byte[] { 0xef, 0xbb, 0xbf }.Concat(Edit("Name", "")).ToArray()];
        foreach (var (reason, bytes) in new (string, byte[])[]
        {
            ("isolated continuation", [0x80]), ("overlong ASCII", [0xc0, 0xaf]), ("truncated UTF8", [0xe2, 0x82]),
            ("UTF8 surrogate", [0xed, 0xa0, 0x80]), ("outside Unicode", [0xf4, 0x90, 0x80, 0x80])
        })
        {
            var prefix = Encoding.UTF8.GetBytes("{\"name\":\"Name\",\"description\":\"");
            yield return [reason, prefix.Concat(bytes).Concat(Encoding.UTF8.GetBytes("\"}")).ToArray()];
        }
        yield return ["201 name scalars", Edit(new string('a', 201), "")];
        yield return ["4001 description scalars", Edit("Name", new string('a', 4001))];
        yield return ["201 supplementary name scalars", Edit(Repeat("😀", 201), "")];
        yield return ["4001 supplementary description scalars", Edit("Name", Repeat("😀", 4001))];
    }

    [Theory]
    [InlineData("organizationId")]
    [InlineData("projectId")]
    [InlineData("memberId")]
    [InlineData("creatorPrincipalId")]
    [InlineData("actorId")]
    [InlineData("role")]
    [InlineData("canCreateProjects")]
    [InlineData("idempotencyKey")]
    [InlineData("fingerprint")]
    [InlineData("createdAtUtc")]
    [InlineData("initialProjectVersion")]
    [InlineData("settings")]
    public async Task CallerSuppliedIdentityRoleHistoryAndPolicyFieldsAreUnknownInput(string property)
    {
        var ports = new SyntheticPorts();
        await using var api = await StartSyntheticAsync(ports);
        var token = await api.TokenAsync();
        ports.Reset(); api.ResetObservations();
        var body = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["name"] = "Untrusted input",
            ["description"] = "",
            [property] = "injected"
        });
        using var response = await api.PostAsync(ProjectCreationHttpTestSupport.CreatePath(Organization), token,
            Guid.NewGuid(), body);
        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalidRequest");
        Assert.Equal(1, ports.CreationAdmissions);
        Assert.Equal(0, ports.Creates);
        Assert.Equal(0, api.CreatorResolutions);
    }

    [Fact]
    public async Task AcceptedUnicodeIsTrimmedOnceOnlyForNameAndRetainsExactDescriptionWithoutCaseOrNfcNormalization()
    {
        var ports = new SyntheticPorts();
        await using var api = await StartSyntheticAsync(ports);
        var token = await api.TokenAsync();
        var path = ProjectCreationHttpTestSupport.CreatePath(Organization);
        var name = "  " + new string('x', 200) + "  ";
        var description = " \tExact e\u0301 and É 😀\r\n ";
        using (var response = await api.PostAsync(path, token, Guid.NewGuid(), Edit(name, description)))
        {
            var result = await ResultAsync(response);
            Assert.Equal(name.Trim(), result.GetProperty("receipt").GetProperty("name").GetString());
            Assert.Equal(description, result.GetProperty("receipt").GetProperty("description").GetString());
        }
        Assert.Equal(name.Trim(), Assert.Single(ports.Requests).Metadata.Name);
        Assert.Equal(description, Assert.Single(ports.Requests).Metadata.Description);
        ports.Reset();
        var raw = Encoding.UTF8.GetBytes(" \r\n{\"descriptio\\u006e\":\"\",\"na\\u006de\":\"  e\\u0301  \"} \t");
        using (var response = await api.PostAsync(path, token, Guid.NewGuid(), raw, chunked: true))
            await ResultAsync(response);
        Assert.Equal("e\u0301", Assert.Single(ports.Requests).Metadata.Name);
        Assert.Equal("", Assert.Single(ports.Requests).Metadata.Description);
    }

    [Fact]
    public async Task MaximumSupplementaryScalarMetadataFitsTheEntityBudgetAndReachesCreatorExactly()
    {
        var ports = new SyntheticPorts();
        await using var api = await StartSyntheticAsync(ports);
        var token = await api.TokenAsync();
        ports.Reset(); api.ResetObservations();
        var name = Repeat("😀", 200);
        var description = Repeat("😀", 4000);
        var body = Edit(name, description);
        Assert.True(body.Length < MaximumBodyBytes);
        using var response = await api.PostAsync(ProjectCreationHttpTestSupport.CreatePath(Organization), token,
            Guid.NewGuid(), body, chunked: true);
        var result = await ResultAsync(response);
        Assert.Equal(name, result.GetProperty("receipt").GetProperty("name").GetString());
        Assert.Equal(description, result.GetProperty("receipt").GetProperty("description").GetString());
        Assert.Equal(name, Assert.Single(ports.Requests).Metadata.Name);
        Assert.Equal(description, Assert.Single(ports.Requests).Metadata.Description);
        Assert.Equal(body.Length, api.BodyBytes);
    }

    [Fact]
    public async Task KnownOversizedEntityIsRejectedWithoutReadingAndExactCeilingRemainsUsable()
    {
        var ports = new SyntheticPorts();
        await using var api = await StartSyntheticAsync(ports);
        var token = await api.TokenAsync();
        ports.Reset(); api.ResetObservations();
        var reply = await HeadersOnlyAsync(api, ProjectCreationHttpTestSupport.CreatePath(Organization), token,
            Guid.NewGuid().ToString("D"), contentLength: MaximumBodyBytes + 1L,
            origin: api.Origin.GetLeftPart(UriPartial.Authority));
        AssertRawError(reply, HttpStatusCode.RequestEntityTooLarge, "bodyLimit");
        Assert.Equal(1, ports.CreationAdmissions);
        Assert.Equal(0, ports.Creates);
        Assert.Equal(0, api.BodyReads);
        Assert.Equal(0, api.BodyBytes);
        var document = Edit("Exact ceiling", "");
        var body = document.Concat(Enumerable.Repeat((byte)' ', MaximumBodyBytes - document.Length)).ToArray();
        using var accepted = await api.PostAsync(ProjectCreationHttpTestSupport.CreatePath(Organization), token,
            Guid.NewGuid(), body);
        await ResultAsync(accepted);
        Assert.Equal(MaximumBodyBytes, api.BodyBytes);
        Assert.Equal(1, ports.Creates);
    }

    [Fact]
    public async Task ChunkedOverflowConsumesOnlyBudgetPlusOneWithoutDrainingAndPermitRecovers()
    {
        var ports = new SyntheticPorts();
        await using var api = await StartSyntheticAsync(ports, maximumConcurrentOperations: 1);
        var token = await api.TokenAsync();
        ports.Reset(); api.ResetObservations();
        using (var overflow = await api.PostAsync(ProjectCreationHttpTestSupport.CreatePath(Organization), token,
            Guid.NewGuid(), Enumerable.Repeat((byte)' ', MaximumBodyBytes * 3).ToArray(), chunked: true))
            await AssertErrorAsync(overflow, HttpStatusCode.RequestEntityTooLarge, "bodyLimit");
        Assert.Equal(MaximumBodyBytes + 1L, api.BodyBytes);
        Assert.Equal(0, ports.Creates);
        api.ResetObservations();
        var document = Edit("Exact chunked ceiling", "");
        var body = document.Concat(Enumerable.Repeat((byte)' ', MaximumBodyBytes - document.Length)).ToArray();
        using var recovered = await api.PostAsync(ProjectCreationHttpTestSupport.CreatePath(Organization), token,
            Guid.NewGuid(), body, chunked: true);
        await ResultAsync(recovered);
        Assert.Equal(MaximumBodyBytes, api.BodyBytes);
        Assert.Equal(1, ports.Creates);
    }

    [Theory]
    [InlineData("organization")]
    [InlineData("actor")]
    [InlineData("key")]
    [InlineData("name")]
    [InlineData("description")]
    public async Task SafeButWronglyBoundReturnedReceiptsRemainClosedInternalErrors(string mismatch)
    {
        var ports = new SyntheticPorts();
        ports.Create = (actor, org, key, metadata) =>
        {
            if (mismatch == "organization") org = Guid.NewGuid();
            if (mismatch == "actor") actor = new(new PrincipalId(Guid.NewGuid()));
            if (mismatch == "key") key = Guid.NewGuid();
            if (mismatch == "name") metadata = new("Another name", metadata.Description);
            if (mismatch == "description") metadata = new(metadata.Name, "Another description");
            return SyntheticPorts.Created(actor, org, key, metadata);
        };
        await using var api = await StartSyntheticAsync(ports);
        var token = await api.TokenAsync();
        using var response = await api.PostAsync(ProjectCreationHttpTestSupport.CreatePath(Organization), token,
            Guid.NewGuid(), Edit("Submitted name", "Submitted description"));
        await AssertErrorAsync(response, HttpStatusCode.InternalServerError, "internalFailure");
        Assert.Equal(1, ports.Creates);
    }

    [Theory]
    [InlineData(ProjectCreationOutcome.NotAdmitted, HttpStatusCode.NotFound, "notFound")]
    [InlineData(ProjectCreationOutcome.CapabilityDenied, HttpStatusCode.Forbidden, "permissionDenied")]
    [InlineData(ProjectCreationOutcome.IdempotencyConflict, HttpStatusCode.Conflict, "idempotencyConflict")]
    public async Task FinalAuthorityOutcomesOverrideTheEarlierPositiveAdvisoryAndExposeNoTargetFacts(
        ProjectCreationOutcome outcome, HttpStatusCode status, string code)
    {
        var ports = new SyntheticPorts { Create = (_, _, _, _) => ProjectCreationResult.WithoutReceipt(outcome) };
        await using var api = await StartSyntheticAsync(ports);
        var token = await api.TokenAsync();
        ports.Reset();
        using var response = await api.PostAsync(ProjectCreationHttpTestSupport.CreatePath(Organization), token,
            Guid.NewGuid(), Edit("Must remain opaque", "Must remain opaque"));
        await AssertErrorAsync(response, status, code);
        Assert.Equal(new[] { "creation-admission", "create" }, ports.Events.ToArray());
        Assert.Equal(1, ports.Creates);
    }

    private static string Repeat(string text, int count) => string.Concat(Enumerable.Repeat(text, count));
}
