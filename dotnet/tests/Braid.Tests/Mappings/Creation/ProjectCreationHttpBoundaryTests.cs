using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Braid.Application;
using Braid.Domain;

namespace Braid.Tests;

/// <summary>
/// Actual loopback Kestrel, framework tokens and bounded request I/O. Application
/// ports here are explicitly synthetic; these cases make no PostgreSQL or commit claim.
/// </summary>
public sealed class ProjectCreationHttpBoundaryTests
{
    internal const string MediaType = "application/vnd.braid.project-create.v1+json";
    internal const int MaximumBodyBytes = 65_536;
    internal static readonly Guid Organization = Guid.Parse("abcdef12-3456-4789-abcd-0123456789ab");
    internal static readonly Guid Principal = Guid.Parse("abcdef23-4567-489a-bcde-123456789abc");
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task DisabledCreationRejectsMalformedHeadersBeforeBodyOrUnregisteredCreatorAndRetainsItsDocumentedRoute()
    {
        await using var api = await ProjectCreationHttpTestSupport.StartAsync(enabled: false, registerCreator: false);
        foreach (var path in new[]
        {
            "/api/v1/organizations/invalid/projects",
            ProjectCreationHttpTestSupport.CreatePath(Organization) + "?creatorPrincipalId=forged"
        })
        {
            var reply = await HeadersOnlyAsync(api, path, null, null, contentLength: MaximumBodyBytes + 1L,
                contentType: "application/json", origin: "https://attacker.invalid", host: "attacker.invalid");
            AssertRawError(reply, HttpStatusCode.NotFound, "unavailable");
        }
        Assert.Equal(0, api.BodyReads);
        Assert.Equal(0, api.BodyBytes);
        Assert.Equal(0, api.CreatorResolutions);
        using var response = await api.Client.GetAsync("/openapi/v1.json");
        var document = await CatalogHttpTestSupport.JsonAsync(response);
        var route = document.GetProperty("paths")
            .GetProperty("/api/v1/organizations/{organizationId}/projects");
        Assert.True(route.TryGetProperty("post", out _));
    }

    [Fact]
    public async Task DisabledCreationContextReportsFalseWithoutCallingCreationPortsOrResolvingCreator()
    {
        var ports = new SyntheticPorts();
        await using var api = await StartSyntheticAsync(ports, enabled: false, registerCreator: false);
        using var response = await api.Client.GetAsync("/api/v1/catalog/context");
        var context = await CatalogHttpTestSupport.JsonAsync(response);
        Assert.False(context.GetProperty("features").GetProperty("createProjects").GetBoolean());
        Assert.False(context.GetProperty("organizationActions").GetProperty("createProjects").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(context.GetProperty("requestToken").GetString()));
        Assert.Equal(1, ports.CatalogAdmissions);
        Assert.Equal(0, ports.CreationAdmissions);
        Assert.Equal(0, ports.Creates);
        Assert.Equal(0, api.CreatorResolutions);
    }

    [Fact]
    public async Task RealUnsafeTransportRejectsForeignHostAndOriginBeforeAdmissionOrAnyWithheldEntityByte()
    {
        var ports = new SyntheticPorts();
        await using var api = await StartSyntheticAsync(ports);
        var token = await api.TokenAsync();
        var path = ProjectCreationHttpTestSupport.CreatePath(Organization);
        var origin = api.Origin.GetLeftPart(UriPartial.Authority);
        var foreignPort = api.Origin.Port + (api.Origin.Port == 65535 ? -1 : 1);
        foreach (var rejectedOrigin in new string?[]
        {
            null, "", "null", "https://attacker.invalid", $"http://localhost:{api.Origin.Port}",
            origin + "/", origin.ToUpperInvariant(), origin + "/path", origin + "?query=true",
            origin + ", " + origin, $"http://127.0.0.1:{foreignPort}"
        })
        {
            ports.Reset(); api.ResetObservations();
            var reply = await HeadersOnlyAsync(api, path, token, Guid.NewGuid().ToString("D"), origin: rejectedOrigin);
            AssertRawError(reply, HttpStatusCode.Forbidden, "identityRejected");
            AssertUntouched(api, ports);
        }
        foreach (var rejectedHost in new[] { "attacker.invalid", $"localhost:{api.Origin.Port}", $"127.0.0.1:{foreignPort}" })
        {
            ports.Reset(); api.ResetObservations();
            var reply = await HeadersOnlyAsync(api, path, token, Guid.NewGuid().ToString("D"), origin: origin,
                host: rejectedHost, additionalHeaders: [
                    new("Forwarded", $"for=127.0.0.1;host={api.Origin.Authority};proto=http"),
                    new("X-Forwarded-Host", api.Origin.Authority), new("X-Forwarded-For", "127.0.0.1"),
                    new("X-Forwarded-Proto", "http")]);
            AssertRawError(reply, HttpStatusCode.Forbidden, "identityRejected");
            AssertUntouched(api, ports);
        }
        ports.Reset(); api.ResetObservations();
        var duplicate = await HeadersOnlyAsync(api, path, token, Guid.NewGuid().ToString("D"), origin: origin,
            additionalHeaders: [new("Origin", origin)]);
        AssertRawError(duplicate, HttpStatusCode.Forbidden, "identityRejected");
        AssertUntouched(api, ports);
    }

    [Fact]
    public async Task MissingInvalidDuplicateAndOtherSessionFrameworkTokensRejectBeforeAdvisoryReadOrBody()
    {
        var ports = new SyntheticPorts();
        await using var api = await StartSyntheticAsync(ports);
        var token = await api.TokenAsync();
        var path = ProjectCreationHttpTestSupport.CreatePath(Organization);
        var origin = api.Origin.GetLeftPart(UriPartial.Authority);
        foreach (var invalid in new string?[] { null, "", "invalid", new('a', 4097) })
        {
            ports.Reset(); api.ResetObservations();
            var reply = await HeadersOnlyAsync(api, path, invalid, Guid.NewGuid().ToString("D"), origin: origin);
            AssertRawError(reply, HttpStatusCode.Forbidden, "identityRejected");
            AssertUntouched(api, ports);
        }
        ports.Reset(); api.ResetObservations();
        var duplicate = await HeadersOnlyAsync(api, path, token, Guid.NewGuid().ToString("D"), origin: origin,
            additionalHeaders: [new("X-XSRF-TOKEN", token)]);
        AssertRawError(duplicate, HttpStatusCode.Forbidden, "identityRejected");
        AssertUntouched(api, ports);
        using var noCookie = api.NewClient();
        using (var request = CreateRequest(path, token, Guid.NewGuid().ToString("D"), Edit("No cookie", ""), origin))
        using (var response = await noCookie.SendAsync(request))
            await AssertErrorAsync(response, HttpStatusCode.Forbidden, "identityRejected");
        AssertUntouched(api, ports);
        using var otherSession = api.NewClient(new CookieContainer());
        using (var context = await otherSession.GetAsync("/api/v1/catalog/context"))
            Assert.Equal(HttpStatusCode.OK, context.StatusCode);
        ports.Reset(); api.ResetObservations();
        using (var request = CreateRequest(path, token, Guid.NewGuid().ToString("D"), Edit("Other cookie", ""), origin))
        using (var response = await otherSession.SendAsync(request))
            await AssertErrorAsync(response, HttpStatusCode.Forbidden, "identityRejected");
        AssertUntouched(api, ports);
    }

    [Fact]
    public async Task TokensRemainBoundToTheConfiguredActorAndOrganizationAcrossHostsWithSharedTestKeys()
    {
        var firstPorts = new SyntheticPorts();
        await using var first = await StartSyntheticAsync(firstPorts);
        var token = await first.TokenAsync();
        foreach (var (principal, organization) in new[]
        {
            (Guid.NewGuid(), Organization), (Principal, Guid.NewGuid())
        })
        {
            var ports = new SyntheticPorts();
            await using var second = await StartSyntheticAsync(ports, principal: principal, organization: organization);
            using var copiedCookies = second.NewClient(first.Cookies);
            using var request = CreateRequest(ProjectCreationHttpTestSupport.CreatePath(organization), token,
                Guid.NewGuid().ToString("D"), Edit("Bound identity", ""), second.Origin.GetLeftPart(UriPartial.Authority));
            using var response = await copiedCookies.SendAsync(request);
            await AssertErrorAsync(response, HttpStatusCode.Forbidden, "identityRejected");
            AssertUntouched(second, ports);
        }
    }

    [Fact]
    public async Task SpoofedIdentityAndForwardedHeadersCannotChooseTheActorPassedToTheFinalCreator()
    {
        var ports = new SyntheticPorts();
        await using var api = await StartSyntheticAsync(ports);
        var token = await api.TokenAsync();
        ports.Reset(); api.ResetObservations();
        using var request = CreateRequest(ProjectCreationHttpTestSupport.CreatePath(Organization), token,
            Guid.NewGuid().ToString("D"), Edit("Configured actor", ""), api.Origin.GetLeftPart(UriPartial.Authority));
        request.Headers.TryAddWithoutValidation("X-Braid-Principal", Guid.NewGuid().ToString("D"));
        request.Headers.TryAddWithoutValidation("X-MS-CLIENT-PRINCIPAL-ID", Guid.NewGuid().ToString("D"));
        request.Headers.TryAddWithoutValidation("X-Role", "administrator");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer forged-development-identity");
        request.Headers.TryAddWithoutValidation("Forwarded", "for=203.0.113.1;host=attacker.invalid;proto=https");
        using var response = await api.Client.SendAsync(request);
        var result = await ResultAsync(response);
        Assert.Equal(Principal, Assert.Single(ports.Requests).Actor.PrincipalId.Value);
        Assert.Equal(Organization, Assert.Single(ports.Requests).OrganizationId);
        Assert.Equal(Principal, result.GetProperty("receipt").GetProperty("creatorPrincipalId").GetGuid());
    }

    [Fact]
    public async Task CanonicalRouteAllowedOrganizationAndEveryQueryFailurePrecedeAdvisoryReadOrWithheldBody()
    {
        var ports = new SyntheticPorts();
        await using var api = await StartSyntheticAsync(ports);
        var token = await api.TokenAsync();
        var canonical = Organization.ToString("D");
        foreach (var id in new[] { "invalid", Guid.Empty.ToString("D"), Organization.ToString("N"),
            Organization.ToString("B"), canonical.ToUpperInvariant(), "%20" + canonical, canonical + "%20" })
        {
            ports.Reset(); api.ResetObservations();
            var reply = await HeadersOnlyAsync(api, $"/api/v1/organizations/{id}/projects", token,
                Guid.NewGuid().ToString("D"), origin: api.Origin.GetLeftPart(UriPartial.Authority));
            AssertRawError(reply, HttpStatusCode.BadRequest, "invalidRequest");
            AssertUntouched(api, ports);
        }
        ports.Reset(); api.ResetObservations();
        var foreign = await HeadersOnlyAsync(api, ProjectCreationHttpTestSupport.CreatePath(Guid.NewGuid()), token,
            Guid.NewGuid().ToString("D"), origin: api.Origin.GetLeftPart(UriPartial.Authority));
        AssertRawError(foreign, HttpStatusCode.NotFound, "notFound");
        AssertUntouched(api, ports);
        foreach (var query in new[] { "?name=Injected", "?description=Injected", "?organizationId=" + canonical,
            "?projectId=" + Guid.NewGuid().ToString("D"), "?creatorPrincipalId=" + Principal.ToString("D"),
            "?role=administrator", "?capability=true", "?idempotencyKey=" + Guid.NewGuid().ToString("D"),
            "?fingerprint=forged", "?createdAtUtc=2000-01-01", "?version=1", "?unknown=", "?unknown=one&unknown=two" })
        {
            ports.Reset(); api.ResetObservations();
            var reply = await HeadersOnlyAsync(api, ProjectCreationHttpTestSupport.CreatePath(Organization) + query, token,
                Guid.NewGuid().ToString("D"), origin: api.Origin.GetLeftPart(UriPartial.Authority));
            AssertRawError(reply, HttpStatusCode.BadRequest, "invalidRequest");
            AssertUntouched(api, ports);
        }
    }

    [Fact]
    public async Task FreshAdvisoryDenialPrecedesMalformedKeyMediaAndKnownOversizedBodyWithoutFinalCreator()
    {
        var ports = new SyntheticPorts();
        await using var api = await StartSyntheticAsync(ports);
        var token = await api.TokenAsync();
        foreach (var capability in new bool?[] { null, false })
        {
            ports.Admission = (actor, org) => capability is null ? null : new(org, actor.PrincipalId, capability.Value);
            ports.Reset(); api.ResetObservations();
            var reply = await HeadersOnlyAsync(api, ProjectCreationHttpTestSupport.CreatePath(Organization), token,
                "malformed", contentLength: MaximumBodyBytes + 1L, contentType: "application/json",
                origin: api.Origin.GetLeftPart(UriPartial.Authority));
            AssertRawError(reply, capability is null ? HttpStatusCode.NotFound : HttpStatusCode.Forbidden,
                capability is null ? "notFound" : "permissionDenied");
            Assert.Equal(1, ports.CreationAdmissions);
            Assert.Equal(0, ports.Creates);
            Assert.Equal(0, api.BodyReads);
            Assert.Equal(0, api.BodyBytes);
        }
    }

    [Fact]
    public async Task ContextObservesFreshCapabilityAndNeverIssuesTokensForFailedOrInconsistentReads()
    {
        var ports = new SyntheticPorts();
        await using var api = await StartSyntheticAsync(ports);
        foreach (var allowed in new[] { true, false, true })
        {
            ports.Admission = (actor, org) => new(org, actor.PrincipalId, allowed);
            ports.Reset();
            using var response = await api.Client.GetAsync("/api/v1/catalog/context");
            var context = await CatalogHttpTestSupport.JsonAsync(response);
            Assert.True(context.GetProperty("features").GetProperty("createProjects").GetBoolean());
            Assert.Equal(allowed, context.GetProperty("organizationActions").GetProperty("createProjects").GetBoolean());
            Assert.False(string.IsNullOrWhiteSpace(context.GetProperty("requestToken").GetString()));
            Assert.Equal(new[] { "catalog-admission", "creation-admission" }, ports.Events.ToArray());
        }
        foreach (var (failure, status, code) in new (Func<ActorContext, Guid, OrganizationProjectCreationAdmission?>,
            HttpStatusCode, string)[]
        {
            ((_, _) => null, HttpStatusCode.NotFound, "notFound"),
            ((_, _) => throw new InvalidOperationException("Synthetic source failure"), HttpStatusCode.InternalServerError, "persistenceFailure"),
            ((actor, _) => new(Guid.NewGuid(), actor.PrincipalId, true), HttpStatusCode.InternalServerError, "internalFailure"),
            ((_, org) => new(org, new PrincipalId(Guid.NewGuid()), true), HttpStatusCode.InternalServerError, "internalFailure")
        })
        {
            ports.Admission = failure;
            ports.Reset();
            using var response = await api.Client.GetAsync("/api/v1/catalog/context");
            var error = await CatalogHttpTestSupport.JsonAsync(response, status);
            Assert.Equal("code", Assert.Single(error.EnumerateObject()).Name);
            Assert.Equal(code, error.GetProperty("code").GetString());
            Assert.False(response.Headers.Contains("Set-Cookie"));
            Assert.False(error.TryGetProperty("requestToken", out _));
            Assert.False(error.TryGetProperty("organizationActions", out _));
        }
    }

    [Fact]
    public async Task ContextRequiresNormalOrganizationAdmissionBeforeAnyCreationCapabilityReadOrToken()
    {
        var ports = new SyntheticPorts();
        await using var api = await StartSyntheticAsync(ports);
        foreach (var (read, status, code) in new (Func<ActorContext, Guid, CatalogOrganizationAdmission?>,
            HttpStatusCode, string)[]
        {
            ((_, _) => null, HttpStatusCode.NotFound, "notFound"),
            ((_, _) => throw new InvalidOperationException("Synthetic catalog source failure"), HttpStatusCode.InternalServerError, "persistenceFailure"),
            ((actor, _) => new(Guid.NewGuid(), actor.PrincipalId, "Foreign organization"), HttpStatusCode.InternalServerError, "internalFailure"),
            ((_, org) => new(org, new PrincipalId(Guid.NewGuid()), "Foreign actor"), HttpStatusCode.InternalServerError, "internalFailure")
        })
        {
            ports.CatalogAdmission = read;
            ports.Reset();
            using var response = await api.Client.GetAsync("/api/v1/catalog/context");
            await AssertErrorAsync(response, status, code);
            Assert.False(response.Headers.Contains("Set-Cookie"));
            Assert.Equal(1, ports.CatalogAdmissions);
            Assert.Equal(0, ports.CreationAdmissions);
            Assert.Equal(0, ports.Creates);
            Assert.Equal(0, api.CreatorResolutions);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task SlowAcceptedBodiesOccupyOnlyTheImmediateCreationPermitsWhileContextReadsRemainAvailable(int limit)
    {
        var ports = new SyntheticPorts();
        await using var api = await StartSyntheticAsync(ports, maximumConcurrentOperations: limit);
        var token = await api.TokenAsync();
        ports.Reset(); api.ResetObservations();
        var gates = api.GateBodies(limit);
        var pending = new List<Task<HttpResponseMessage>>();
        try
        {
            for (var index = 0; index < limit; index++)
            {
                pending.Add(api.PostAsync(ProjectCreationHttpTestSupport.CreatePath(Organization), token,
                    Guid.NewGuid(), Edit("Gated " + index, "")));
                await gates[index].Entered.Task.WaitAsync(Deadline);
            }
            Assert.Equal(0, ports.Creates);
            Assert.Equal(0, api.BodyBytes);
            var reads = api.BodyReads;
            var busy = await HeadersOnlyAsync(api, ProjectCreationHttpTestSupport.CreatePath(Organization), token,
                Guid.NewGuid().ToString("D"), origin: api.Origin.GetLeftPart(UriPartial.Authority));
            AssertRawError(busy, HttpStatusCode.ServiceUnavailable, "projectCreationAdmissionBusy");
            Assert.Equal(reads, api.BodyReads);
            Assert.Equal(0, ports.Creates);
            using var context = await api.Client.GetAsync("/api/v1/catalog/context");
            Assert.Equal(HttpStatusCode.OK, context.StatusCode);
            Assert.True((await CatalogHttpTestSupport.JsonAsync(context))
                .GetProperty("organizationActions").GetProperty("createProjects").GetBoolean());
            foreach (var gate in gates) gate.Release.TrySetResult();
            foreach (var request in pending)
            {
                using var response = await request.WaitAsync(Deadline);
                await ResultAsync(response);
            }
            foreach (var gate in gates) await gate.RequestEnded.Task.WaitAsync(Deadline);
        }
        finally
        {
            foreach (var gate in gates) gate.Release.TrySetResult();
            foreach (var request in pending) { using var completion = await request.WaitAsync(Deadline); }
        }
        Assert.Equal(limit, ports.Creates);
        using var recovered = await api.PostAsync(ProjectCreationHttpTestSupport.CreatePath(Organization), token,
            Guid.NewGuid(), Edit("Recovered", ""));
        await ResultAsync(recovered);
        Assert.Equal(limit + 1, ports.Creates);
    }

    [Fact]
    public async Task SingleCreationPermitIsRecoveredAfterInvalidEntityAndFinalCreatorFailure()
    {
        var ports = new SyntheticPorts();
        await using var api = await StartSyntheticAsync(ports, maximumConcurrentOperations: 1);
        var token = await api.TokenAsync();
        using (var invalid = await api.PostAsync(ProjectCreationHttpTestSupport.CreatePath(Organization), token,
            Guid.NewGuid(), Encoding.UTF8.GetBytes("{\"name\":\"Missing description\"}")))
            await AssertErrorAsync(invalid, HttpStatusCode.BadRequest, "invalidRequest");
        ports.Create = (_, _, _, _) => throw new InvalidOperationException("Synthetic persistence failure");
        using (var failed = await api.PostAsync(ProjectCreationHttpTestSupport.CreatePath(Organization), token,
            Guid.NewGuid(), Edit("Will fail", "")))
            await AssertErrorAsync(failed, HttpStatusCode.InternalServerError, "persistenceFailure");
        ports.Create = SyntheticPorts.Created;
        using var recovered = await api.PostAsync(ProjectCreationHttpTestSupport.CreatePath(Organization), token,
            Guid.NewGuid(), Edit("Recovered", ""));
        await ResultAsync(recovered);
    }

    [Fact]
    public async Task ActualClientCancellationDuringGatedBodyEndsTheRequestWithoutCreatorAndRecoversItsSinglePermit()
    {
        var ports = new SyntheticPorts();
        var transport = new RevisionUploadTransportObserver();
        await using var api = await StartSyntheticAsync(ports, maximumConcurrentOperations: 1, transportObserver: transport);
        var token = await api.TokenAsync();
        ports.Reset(); api.ResetObservations();
        var gate = api.GateBody();
        using var request = await api.StartWithheldBodyPostAsync(ProjectCreationHttpTestSupport.CreatePath(Organization),
            token, Guid.NewGuid());
        try
        {
            await gate.Entered.Task.WaitAsync(Deadline);
            Assert.Equal(1, ports.CreationAdmissions);
            Assert.Equal(0, ports.Creates);
            Assert.Equal(0, api.BodyBytes);
            gate.Release.TrySetResult();
            Assert.Equal(3, await gate.FirstReadCompleted.Task.WaitAsync(Deadline));
            await gate.SecondReadPending.Task.WaitAsync(Deadline);
            Assert.Equal(2, gate.InnerReadEntries);
            Assert.Equal(3L, gate.SuccessfulBytes);
            Assert.Equal(3L, api.BodyBytes);
            Assert.Equal(0, ports.Creates);
            request.Abort();
            await gate.RequestAborted.Task.WaitAsync(Deadline);
            await gate.RequestEnded.Task.WaitAsync(Deadline);
            var finish = await gate.Finished.Task.WaitAsync(Deadline);
            using var settlementDeadline = new CancellationTokenSource(Deadline);
            var settlement = await request.WaitForTransportSettlementAsync(settlementDeadline.Token);
            var diagnostic = JsonSerializer.Serialize(new
            {
                Request = JsonSerializer.Deserialize<JsonElement>(gate.DiagnosticSnapshot()),
                Transport = settlement
            });
            Assert.False(finish.ResponseHasStarted, diagnostic);
            Assert.True(settlement.ConnectionPipelineSettled, diagnostic);
            Assert.Equal(0L, settlement.EncodedBytes);
            Assert.Null(settlement.DeliveredBytes);
            Assert.Equal(0, ports.Creates);
            Assert.Equal(3L, api.BodyBytes);
            Assert.Equal(3L, gate.SuccessfulBytes);
            Console.WriteLine(diagnostic);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException(JsonSerializer.Serialize(new
            {
                Request = JsonSerializer.Deserialize<JsonElement>(gate.DiagnosticSnapshot()),
                Transport = new
                {
                    EncodedBytes = request.Transport?.EncodedBytes,
                    ConnectionPipelineSettled = request.Transport?.PipelineSettled.Task.IsCompletedSuccessfully ?? false,
                    DeliveredBytes = (long?)null
                }
            }), exception);
        }
        finally
        {
            request.Abort(); gate.Release.TrySetResult();
        }
        using var recovered = await api.PostAsync(ProjectCreationHttpTestSupport.CreatePath(Organization), token,
            Guid.NewGuid(), Edit("Recovered after disconnect", ""));
        await ResultAsync(recovered);
        Assert.Equal(1, ports.Creates);
    }

    internal static Task<ProjectCreationHttpTestSupport> StartSyntheticAsync(SyntheticPorts ports,
        bool enabled = true, int maximumConcurrentOperations = 2, Guid? principal = null,
        Guid? organization = null, bool registerCreator = true,
        RevisionUploadTransportObserver? transportObserver = null) => ProjectCreationHttpTestSupport.StartAsync(
            enabled: enabled, maximumConcurrentOperations: maximumConcurrentOperations,
            principal: principal ?? Principal, organization: organization ?? Organization,
            creator: ports, admissionReader: ports, catalogReader: ports, registerCreator: registerCreator,
            transportObserver: transportObserver);

    internal static byte[] Edit(string name, string description) => JsonSerializer.SerializeToUtf8Bytes(new { name, description });

    internal static HttpRequestMessage CreateRequest(string path, string? token, string? key, byte[] body, string? origin,
        string? contentType = MediaType)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new ByteArrayContent(body) };
        if (token is not null) request.Headers.TryAddWithoutValidation("X-XSRF-TOKEN", token);
        if (key is not null) request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        if (origin is not null) request.Headers.TryAddWithoutValidation("Origin", origin);
        if (contentType is not null) request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        return request;
    }

    internal static async Task<JsonElement> ResultAsync(HttpResponseMessage response)
    {
        var result = await CatalogHttpTestSupport.JsonAsync(response, HttpStatusCode.Created);
        Assert.Equal("created", result.GetProperty("outcome").GetString());
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Null(response.Headers.ETag);
        return result;
    }

    internal static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await CatalogHttpTestSupport.JsonAsync(response, status);
        Assert.Equal("code", Assert.Single(body.EnumerateObject()).Name);
        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Null(response.Headers.Location);
        Assert.Null(response.Headers.ETag);
    }

    internal static void AssertUntouched(ProjectCreationHttpTestSupport api, SyntheticPorts ports)
    {
        Assert.Equal(0, ports.CreationAdmissions);
        Assert.Equal(0, ports.Creates);
        Assert.Equal(0, api.BodyReads);
        Assert.Equal(0, api.BodyBytes);
        Assert.Equal(0, api.CreatorResolutions);
    }

    /// <summary>Send only real HTTP/1 headers and withhold every declared entity byte until a complete reply.</summary>
    internal static async Task<RawReply> HeadersOnlyAsync(ProjectCreationHttpTestSupport api, string path,
        string? token, string? key, long contentLength = 128, string? contentType = MediaType,
        string? origin = null, string? host = null, string? contentEncoding = null,
        IEnumerable<KeyValuePair<string, string>>? additionalHeaders = null)
    {
        using var deadline = new CancellationTokenSource(Deadline);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, api.Origin.Port, deadline.Token);
        await using var stream = client.GetStream();
        var headers = new StringBuilder($"POST {path} HTTP/1.1\r\nHost: {host ?? api.Origin.Authority}\r\nConnection: close\r\nContent-Length: {contentLength.ToString(CultureInfo.InvariantCulture)}\r\n");
        headers.Append("Cookie: ").Append(api.Cookies.GetCookieHeader(new Uri(api.Origin, path))).Append("\r\n");
        if (origin is not null) headers.Append("Origin: ").Append(origin).Append("\r\n");
        if (token is not null) headers.Append("X-XSRF-TOKEN: ").Append(token).Append("\r\n");
        if (key is not null) headers.Append("Idempotency-Key: ").Append(key).Append("\r\n");
        if (contentType is not null) headers.Append("Content-Type: ").Append(contentType).Append("\r\n");
        if (contentEncoding is not null) headers.Append("Content-Encoding: ").Append(contentEncoding).Append("\r\n");
        if (additionalHeaders is not null)
            foreach (var header in additionalHeaders) headers.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
        headers.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers.ToString()), deadline.Token);
        var first = Encoding.ASCII.GetString(await ReadLineAsync(stream, deadline.Token)).TrimEnd('\r', '\n').Split(' ', 3);
        Assert.StartsWith("HTTP/1.", first[0], StringComparison.Ordinal);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var line = Encoding.ASCII.GetString(await ReadLineAsync(stream, deadline.Token)).TrimEnd('\r', '\n');
            if (line.Length == 0) break;
            var separator = line.IndexOf(':');
            Assert.True(separator > 0);
            values.Add(line[..separator], line[(separator + 1)..].Trim(' ', '\t'));
        }
        using var body = new MemoryStream();
        if (values.TryGetValue("Content-Length", out var length))
            await ReadExactAsync(stream, int.Parse(length, CultureInfo.InvariantCulture), body, deadline.Token);
        else if (values.TryGetValue("Transfer-Encoding", out var transfer))
        {
            Assert.Equal("chunked", transfer);
            while (true)
            {
                var size = Encoding.ASCII.GetString(await ReadLineAsync(stream, deadline.Token)).TrimEnd('\r', '\n').Split(';', 2)[0];
                var count = int.Parse(size, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                if (count == 0) { Assert.Equal(new byte[] { 13, 10 }, await ReadLineAsync(stream, deadline.Token)); break; }
                await ReadExactAsync(stream, count, body, deadline.Token);
                Assert.Equal(new byte[] { 13, 10 }, await ReadLineAsync(stream, deadline.Token));
            }
        }
        return new((HttpStatusCode)int.Parse(first[1], CultureInfo.InvariantCulture), values, body.ToArray());
    }

    internal static void AssertRawError(RawReply reply, HttpStatusCode status, string code)
    {
        Assert.Equal(status, reply.Status);
        Assert.False(reply.Headers.ContainsKey("ETag"));
        Assert.False(reply.Headers.ContainsKey("Location"));
        Assert.Contains("no-store", reply.Headers["Cache-Control"], StringComparison.Ordinal);
        using var body = JsonDocument.Parse(reply.Body);
        Assert.Equal("code", Assert.Single(body.RootElement.EnumerateObject()).Name);
        Assert.Equal(code, body.RootElement.GetProperty("code").GetString());
    }

    private static async Task<byte[]> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var line = new MemoryStream();
        var one = new byte[1];
        while (await stream.ReadAsync(one, cancellationToken) != 0)
        {
            line.WriteByte(one[0]);
            if (line.Length > 8192) throw new InvalidOperationException("The owned HTTP response line exceeded its bound.");
            if (line.Length >= 2 && one[0] == 10 && line.GetBuffer()[(int)line.Length - 2] == 13) return line.ToArray();
        }
        throw new IOException("The owned HTTP response ended before its line.");
    }

    private static async Task ReadExactAsync(Stream stream, int count, MemoryStream result, CancellationToken cancellationToken)
    {
        if (count < 0 || result.Length + count > MaximumBodyBytes)
            throw new InvalidOperationException("The owned HTTP response exceeded its bound.");
        var bytes = new byte[count];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        await result.WriteAsync(bytes, cancellationToken);
    }

    internal sealed record RawReply(HttpStatusCode Status, IReadOnlyDictionary<string, string> Headers, byte[] Body);

    /// <summary>Only a synthetic application boundary; no durable creation, authority or native admission is asserted.</summary>
    internal sealed class SyntheticPorts : ICatalogReader, IOrganizationProjectCreationReader, IProjectCreator
    {
        private int catalogAdmissions;
        private int creationAdmissions;
        private int creates;
        internal int CatalogAdmissions => Volatile.Read(ref catalogAdmissions);
        internal int CreationAdmissions => Volatile.Read(ref creationAdmissions);
        internal int Creates => Volatile.Read(ref creates);
        internal ConcurrentQueue<string> Events { get; } = new();
        internal ConcurrentQueue<CreationRequest> Requests { get; } = new();
        internal Func<ActorContext, Guid, OrganizationProjectCreationAdmission?> Admission { get; set; } =
            (actor, org) => new(org, actor.PrincipalId, true);
        internal Func<ActorContext, Guid, CatalogOrganizationAdmission?> CatalogAdmission { get; set; } =
            (actor, org) => new(org, actor.PrincipalId, "Synthetic boundary organization");
        internal Func<ActorContext, Guid, Guid, ProjectCreationMetadata, ProjectCreationResult> Create { get; set; } = Created;

        internal void Reset()
        {
            Volatile.Write(ref catalogAdmissions, 0); Volatile.Write(ref creationAdmissions, 0); Volatile.Write(ref creates, 0);
            Events.Clear(); Requests.Clear();
        }

        internal static ProjectCreationResult Created(ActorContext actor, Guid organizationId, Guid key, ProjectCreationMetadata metadata)
        {
            var project = new ProjectScope(organizationId, Guid.NewGuid());
            var fingerprint = ProjectCreationFingerprint.Create(organizationId, actor.PrincipalId, key, metadata);
            var receipt = new ProjectCreationReceipt(project, key, actor.PrincipalId, metadata, fingerprint,
                new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
            return ProjectCreationResult.ForReceipt(ProjectCreationOutcome.Created, receipt);
        }

        public Task<OrganizationProjectCreationAdmission?> GetAdmissionAsync(ActorContext actor, Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Interlocked.Increment(ref creationAdmissions);
            Events.Enqueue("creation-admission");
            return Task.FromResult(Admission(actor, organizationId));
        }

        public Task<ProjectCreationResult> CreateAsync(ActorContext actor, Guid organizationId, Guid idempotencyKey,
            ProjectCreationMetadata metadata, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Interlocked.Increment(ref creates);
            Events.Enqueue("create"); Requests.Enqueue(new(actor, organizationId, idempotencyKey, metadata));
            return Task.FromResult(Create(actor, organizationId, idempotencyKey, metadata));
        }

        public Task<CatalogOrganizationAdmission?> GetOrganizationAdmissionAsync(ActorContext actor, Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Interlocked.Increment(ref catalogAdmissions);
            Events.Enqueue("catalog-admission");
            return Task.FromResult(CatalogAdmission(actor, organizationId));
        }

        public Task<CatalogProjectPage?> ListProjectsAsync(ActorContext actor, Guid organizationId, int limit,
            CatalogProjectCursor? cursor = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CatalogProject?> GetProjectAsync(ActorContext actor, ProjectScope scope,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CatalogMappingPage?> ListMappingsAsync(ActorContext actor, ProjectScope scope, int limit,
            CatalogMappingCursor? cursor = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CatalogMapping?> GetMappingAsync(ActorContext actor, MappingScope scope,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CatalogMappingMetadata?> GetMappingMetadataAsync(ActorContext actor, MappingScope scope,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CatalogRevisionPage?> ListRevisionsAsync(ActorContext actor, MappingScope scope, int limit,
            CatalogRevisionCursor? cursor = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CatalogRevision?> GetRevisionAsync(ActorContext actor, RevisionScope scope,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    internal sealed record CreationRequest(ActorContext Actor, Guid OrganizationId, Guid Key, ProjectCreationMetadata Metadata);
}
