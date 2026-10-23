using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Weft.Application;
using Weft.Application.Regression;
using Weft.Deployment;
using Weft.Domain;
using Weft.Identity;
using Weft.MachineEndpoint;
using Weft.MappingBackends;
using Weft.MappingBackends.Workers;
using Weft.Persistence.Postgres;
using Xunit.Abstractions;

namespace Weft.Tests;

/// <summary>
/// Actual native admission, regression evidence, compiler worker, isolated compiled runtime and PostgreSQL, called
/// through the endpoint host's own composition. Only the token validator's HTTPS transport reaches the owned issuer.
/// </summary>
[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class MappingEndpointLiveTests(PostgresFixture fixture, ITestOutputHelper output)
{
    private static readonly byte[] Input = Encoding.UTF8.GetBytes("{\"code\":\"A1\",\"a.b\":\"dot\",\"nested\":{\"b\":\"x\"}}");
    private static readonly byte[] Expected = Encoding.UTF8.GetBytes("{\n  \"code\": \"A1\",\n  \"dotted\": \"dot\",\n  \"nestedOutput\": {\n    \"b\": \"x\"\n  }\n}\n");

    [CompiledRuntimeDispatchFact]
    public Task Machine_callers_invoke_the_current_release_retry_roll_back_and_lose_access_on_revocation() => RunAsync(hosted: false);

    [HostedMachineEndpointFact]
    public Task Hosted_machine_callers_use_the_pinned_service_for_invocation_retry_rollback_and_revocation() => RunAsync(hosted: true);

    private async Task RunAsync(bool hosted)
    {
        var image = Environment.GetEnvironmentVariable("WEFT_BUILD_WORKER_IMAGE")!;
        Assert.Equal(image, Environment.GetEnvironmentVariable("WEFT_FERRULE_WORKER_IMAGE"));
        var runtimeImage = Environment.GetEnvironmentVariable("WEFT_COMPILED_RUNTIME_IMAGE")!;
        await using var native = await PostgresClaimAwareStorageIntegrationSupport.CreateAsync(fixture);
        var mapping = JsonNode.Parse(RevisionIngestionTestSupport.Fixture("valid-basic"))!;
        mapping["source_options"] = new JsonObject { ["json_document"] = true };
        mapping["target_options"] = new JsonObject { ["json_document"] = true };
        var created = await native.CreateNativeAsync(JsonSerializer.SerializeToUtf8Bytes(mapping));
        Assert.Equal(RevisionIngestionOutcome.CommittedValid, created.Outcome);
        var receipt = await native.ReceiptAsync(created);
        var revision = receipt.Facts.FutureRevision;
        var scope = new MappingScope(revision.OrganizationId, revision.ProjectId, revision.MappingId);
        await native.Case.ExecuteAsync("UPDATE weft.project_memberships SET role='administrator',version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3",
            revision.OrganizationId, revision.ProjectId, native.Actor.PrincipalId.Value);

        // Saved regression evidence with an independently authored baseline.
        var runtime = new RegressionRuntimeIdentity("ferrule", FerruleInspectionBackend.EngineRevision, image, FerrulePreviewBackend.AdapterBuild, PreviewPolicy.Profile);
        var value = new RegressionCaseVersion(Guid.NewGuid(), 1, native.Actor.PrincipalId, "Endpoint evidence", revision, receipt.Facts.NativeObject.Digest, runtime,
            new(PreviewTargetSelection.All, "2026-10-20T12:00:00Z", PreviewLimits.Default, new(null, Input), [], []),
            RegressionComparisonService.ExactBytesPolicy, RegressionOutputSet.Capture([RegressionOutput.Capture(RegressionOutputKey.Primary, Expected)]));
        await using var caseWriter = new PostgresRegressionCaseStore(native.Case.RegressionConnectionString("weft_case_writer"), RegressionCaseDatabaseProfile.Writer);
        await using var caseReader = new PostgresRegressionCaseStore(native.Case.RegressionConnectionString("weft_case_reader"), RegressionCaseDatabaseProfile.Reader);
        await caseWriter.SaveAsync(native.Actor, value);
        await using var runWriter = new PostgresRegressionRunStore(native.Case.RegressionConnectionString("weft_run_writer"), true);
        await using var runReader = new PostgresRegressionRunStore(native.Case.RegressionConnectionString("weft_run_reader"), false);
        var runId = Guid.NewGuid();
        Assert.Equal(RegressionRunSubmitOutcome.Created, await runWriter.SubmitAsync(new(native.Actor, runId, new RegressionCaseEnvelopeCodec().Encode(value).Reference)));
        await using (var previewRunner = new DockerInspectionWorkerRunner(new("/usr/bin/docker", image, profile: DockerWorkerProfile.Preview)))
        {
            var regression = new RegressionCaseExecutionService(caseReader, new PostgresProjectReader(native.Native.ReaderDatabase),
                native.ReportReader, native.Physical, new FerrulePreviewBackend(previewRunner), runtime);
            Assert.Equal(RegressionRunDispatchOutcome.Completed, await new RegressionRunExecutor(runWriter, regression).ExecuteAsync(revision.OrganizationId, runId, Guid.NewGuid()));
        }
        var evidence = new RegressionRunReadService(runReader, caseReader);

        // Two approved builds compiled by the ordinary worker.
        await using var controls = new PostgresBuildStore(native.Case.RegressionConnectionString("weft_build_writer"), true);
        var environment = new BuildEnvironmentReference(new(revision.OrganizationId, revision.ProjectId), Guid.NewGuid(), 1);
        Assert.Equal(BuildControlOutcome.Created, await controls.SaveEnvironmentAsync(native.Actor, new(environment, FerruleBuildCompiler.Profile, true)));
        const int reportLimit = 1024 * 1024;
        var reports = native.NewReports(registry: FerruleRevisionReportBackendFactory.CreateRegistry(
            FerruleRevisionReportBackendFactory.CreateProfile(image), new(storedObjectByteLimit: reportLimit), () => native.Native.Runner));
        _ = RevisionReportTestSupport.Available(await reports.EnsureAsync(native.Actor, revision, RevisionReportOperation.Projection));
        await using var api = await BuildApiLiveHost.StartAsync(native, image, reportLimit);
        BuildIntent Intent() => new(Guid.NewGuid(), revision, value.NativeDigest,
            new("ferrule", FerruleInspectionBackend.EngineRevision, image, FerrulePreviewBackend.AdapterBuild), FerruleBuildCompiler.Profile,
            [new(BuildBoundaryKind.PrimaryInput, null, "json"), new(BuildBoundaryKind.PrimaryOutput, null, "json")], environment, Guid.NewGuid(), runId);
        await using var history = new PostgresBuildRunStore(native.Case.RegressionConnectionString("weft_build_run_reader"), BuildRunDatabaseProfile.Reader);
        var first = Intent(); var second = Intent();
        foreach (var intent in new[] { first, second })
        {
            await api.ApproveAsync(intent);
            await api.SubmitAsync(intent);
        }
        await using (var worker = BuildDispatchLiveTests.Child.Start(native, image))
            foreach (var intent in new[] { first, second })
            {
                var deadline = DateTimeOffset.UtcNow.AddMinutes(6);
                BuildRunSnapshot? run;
                while ((run = await history.ReadAsync(native.Actor, revision.OrganizationId, intent.BuildId))?.State is null or BuildRunState.Queued or BuildRunState.Running)
                {
                    Assert.False(worker.HasExited, "The ordinary build worker exited before completion.");
                    Assert.True(DateTimeOffset.UtcNow < deadline, "The ordinary build worker did not complete its queued build.");
                    await Task.Delay(250);
                }
                Assert.True(run!.State == BuildRunState.Succeeded, $"Build {run.State} after {run.Attempt} attempts: {run.Outcome}.");
            }

        // Qualify both builds by actual isolated execution, then register the target and activate the first.
        var objects = new PostgresBuildObjectStore(controls, native.Physical, native.Actor);
        await using var hostedRuntime = hosted ? await HostedCompiledRuntimeTestSupport.StartAsync() : null;
        if (hostedRuntime is not null) runtimeImage = hostedRuntime.RuntimeIdentity;
        await using var hostedRunner = hostedRuntime?.CreateRunner(image);
        await using var dockerRunner = hostedRunner is null
            ? new DockerInspectionWorkerRunner(new("/usr/bin/docker", runtimeImage, profile: DockerWorkerProfile.Compiled)) : null;
        var qualifyRunner = hostedRunner?.Runner ?? dockerRunner!;
        var execution = new CompiledBuildExecution(history, new BuildControlService(controls, new PostgresProjectReader(native.Native.ReaderDatabase),
            native.ReportReader, evidence), controls, objects, new FerruleCompiledBuildRuntime(qualifyRunner, first.Backend));
        await using var qualifications = new PostgresBuildQualificationStore(native.Case.RegressionConnectionString("weft_qualification_writer"), true);
        async Task<BuildQualification> Qualify(BuildIntent intent)
        {
            var result = await execution.ExecuteAsync(native.Actor, scope, intent.BuildId, value.Replay);
            Assert.Equal(CompiledBuildOutcome.Completed, result.Outcome);
            var qualification = BuildQualification.Create(Guid.NewGuid(), native.Actor.PrincipalId, (await history.ReadAsync(native.Actor, revision.OrganizationId, intent.BuildId))!,
                (await runReader.ReadAsync(native.Actor, revision.OrganizationId, runId))!, value, execution.RuntimeImage, result);
            Assert.Equal(BuildQualificationWriteOutcome.Created, await qualifications.SaveAsync(native.Actor, qualification));
            return qualification;
        }
        var firstProof = await Qualify(first); var secondProof = await Qualify(second);
        await using var releases = new PostgresLocalReleaseStore(native.Case.RegressionConnectionString("weft_release_writer"), true);
        var target = new LocalReleaseTarget(new(scope, environment, Guid.NewGuid()), first.Profile, runtimeImage);
        Assert.Equal(ReleaseWriteOutcome.Created, await releases.RegisterAsync(native.Actor, target));
        var service = new LocalReleaseService(releases, qualifications);
        async Task<Guid> Activate(BuildQualification proof, Guid? expected, Guid? rollbackOf = null)
        {
            var id = Guid.NewGuid();
            Assert.Equal(ReleaseWriteOutcome.Created, (await service.RequestAsync(native.Actor, target.Reference,
                new(id, proof.Id, expected, rollbackOf is null ? ReleaseAction.Activate : ReleaseAction.Rollback, rollbackOf))).Outcome);
            Assert.Equal(ReleaseTransitionOutcome.Activated, await service.ApplyAsync(native.Actor, target.Reference, id));
            return id;
        }
        var firstRelease = await Activate(firstProof, null);

        // One machine principal with a grant; the endpoint host for this target.
        await using var issuerCrypto = new DisposableCrypto();
        await using var issuer = new EntraProtocolHttpsIssuer(issuerCrypto.Value);
        await issuer.StartAsync();
        var caller = new MachinePrincipal(MachinePrincipal.Entra, EntraProtocolFixturePolicy.Tenant, Guid.NewGuid());
        await using var grants = new PostgresInvocationGrantStore(native.Case.RegressionConnectionString("weft_invocation_grant_writer"));
        var grant = new InvocationGrant(Guid.NewGuid(), target.Reference, caller);
        Assert.Equal(InvocationGrantOutcome.Applied, await grants.GrantAsync(native.Actor, grant));
        using var files = new PrivateFiles(native.Root.NewDirectory("endpoint-" + Guid.NewGuid().ToString("N")));
        var port = FreePort();
        var endpointValues = new Dictionary<string, string>
        {
            [MappingEndpointSettings.Listen] = "127.0.0.1:" + port,
            [MappingEndpointSettings.Transport] = "platformIngress",
            [MappingEndpointSettings.Concurrency] = "1",
            ["WEFT_ENDPOINT_ORGANIZATION_ID"] = scope.OrganizationId.ToString("D"),
            ["WEFT_ENDPOINT_PROJECT_ID"] = scope.ProjectId.ToString("D"),
            ["WEFT_ENDPOINT_MAPPING_ID"] = scope.MappingId.ToString("D"),
            ["WEFT_ENDPOINT_ENVIRONMENT_ID"] = environment.EnvironmentId.ToString("D"),
            ["WEFT_ENDPOINT_ENVIRONMENT_VERSION"] = "1",
            ["WEFT_ENDPOINT_TARGET_ID"] = target.Reference.TargetId.ToString("D"),
            [MappingEndpointSettings.TenantSetting] = EntraProtocolFixturePolicy.Tenant.ToString("D"),
            [MappingEndpointSettings.AudienceSetting] = EntraProtocolFixturePolicy.Client.ToString("D"),
            [MappingEndpointSettings.RuntimeImageSetting] = runtimeImage,
            [MappingEndpointSettings.CompilerImageSetting] = image
        };
        hostedRuntime?.Configure(endpointValues, image);
        var settings = MappingEndpointSettings.Read(files.Environment(endpointValues, MappingEndpointSettings.Logins.ToDictionary(login => login.Setting, login => login.Role switch
        {
            "weft_runtime" => native.Case.RuntimeConnectionString,
            "weft_report_reader" => native.Case.ReportConnectionString,
            var role => native.Case.RegressionConnectionString(role)
        })));
        var validator = new EntraMachineTokenValidator(new(settings.TenantId, settings.Audience), new EntraHttpsFixtureTransport(issuer.Port, issuerCrypto.Value.Root));
        var resources = new MappingEndpointResources(settings, validator, TimeProvider.System);
        using var host = MappingEndpointHost.Build(resources);
        await host.StartAsync();
        try
        {
            var routes = (MappingEndpointRoutes)host.Services.GetService(typeof(MappingEndpointRoutes))!;
            using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + port), Timeout = TimeSpan.FromMinutes(3) };
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync(MappingEndpointRoutes.ReadyPath)).StatusCode);
            routes.MarkReady();
            Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync(MappingEndpointRoutes.LivePath)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync(MappingEndpointRoutes.ReadyPath)).StatusCode);
            var token = issuerCrypto.Value.SignClaims(MachineTokenValidationTests.Claims(caller.ObjectId).ToJsonString());
            async Task<HttpResponseMessage> Call(Guid request, byte[]? input = null, string? bearer = null, string? requestId = null)
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, MappingEndpointRoutes.InvocationsPath)
                {
                    Content = new ByteArrayContent(Body(input ?? Input))
                };
                message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                message.Headers.Authorization = bearer == "" ? null : new AuthenticationHeaderValue("Bearer", bearer ?? token);
                message.Headers.Add(MappingEndpointRoutes.RequestIdHeader, requestId ?? request.ToString("D"));
                return await client.SendAsync(message);
            }
            async Task<JsonElement> Completed(HttpResponseMessage response, Guid release, BuildIntent build, bool replayed)
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal(replayed ? "true" : "false", response.Headers.GetValues(MappingEndpointRoutes.ReplayedHeader).Single());
                var root = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync()).RootElement;
                Assert.Equal(release.ToString("D"), root.GetProperty("releaseId").GetString());
                Assert.Equal(build.BuildId.ToString("D"), root.GetProperty("buildId").GetString());
                Assert.Equal(revision.RevisionId.ToString("D"), root.GetProperty("revisionId").GetString());
                Assert.Equal(Expected, Assert.Single(root.GetProperty("outputs").EnumerateArray().ToArray()).GetProperty("content").GetBytesFromBase64());
                return root;
            }

            // Authentication and request admission refuse before anything runs.
            var anonymous = await Call(Guid.NewGuid(), bearer: "");
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            Assert.Equal("Bearer", anonymous.Headers.WwwAuthenticate.Single().Scheme);
            var foreign = MachineTokenValidationTests.Claims(caller.ObjectId); foreign["aud"] = Guid.NewGuid().ToString("D");
            Assert.Equal(HttpStatusCode.Unauthorized, (await Call(Guid.NewGuid(), bearer: issuerCrypto.Value.SignClaims(foreign.ToJsonString()))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await Call(Guid.NewGuid(), requestId: "not-an-id")).StatusCode);

            // Call, then retry the same request: the original bytes return without a second execution.
            var r1 = Guid.NewGuid();
            var original = await Call(r1);
            var originalBytes = await original.Content.ReadAsByteArrayAsync();
            await Completed(new(HttpStatusCode.OK) { Content = new ByteArrayContent(originalBytes), Headers = { { MappingEndpointRoutes.ReplayedHeader, "false" } } },
                firstRelease, first, replayed: false);
            var retry = await Call(r1);
            Assert.Equal(originalBytes, await retry.Content.ReadAsByteArrayAsync());
            Assert.Equal("true", retry.Headers.GetValues(MappingEndpointRoutes.ReplayedHeader).Single());
            Assert.Equal(2L, await native.Case.ScalarAsync<long>("SELECT count(*) FROM weft.machine_invocation_audit WHERE request_id=$1", r1));
            Assert.Equal(HttpStatusCode.Conflict, (await Call(r1, Encoding.UTF8.GetBytes("{\"code\":\"B2\"}"))).StatusCode);

            // A saturated endpoint answers 503 with Retry-After while one call holds the only slot.
            using (var held = await HoldSlotAsync(port, token))
            {
                var busy = await Call(Guid.NewGuid());
                Assert.Equal(HttpStatusCode.ServiceUnavailable, busy.StatusCode);
                Assert.Equal(TimeSpan.FromSeconds(1), busy.Headers.RetryAfter!.Delta);
            }

            // A new activation takes effect on the next call; so does a rollback to the qualified first artifact.
            // The held slot returns once the server observes the closed connection; a busy refusal records nothing.
            var secondRelease = await Activate(secondProof, firstRelease);
            var afterHold = Guid.NewGuid();
            var next = await Call(afterHold);
            for (var attempt = 0; next.StatusCode == HttpStatusCode.ServiceUnavailable && attempt < 40; attempt++)
            {
                await Task.Delay(250);
                next = await Call(afterHold);
            }
            await Completed(next, secondRelease, second, replayed: false);
            var rollback = await Activate(firstProof, secondRelease, firstRelease);
            var rolledBack = await Completed(await Call(Guid.NewGuid()), rollback, first, replayed: false);
            Assert.Equal(firstProof.Artifact.ContentDigest.Value, rolledBack.GetProperty("artifactDigest").GetString());

            // Revocation refuses the next call and even a replay of an earlier result.
            Assert.Equal(InvocationGrantOutcome.Applied, await grants.RevokeAsync(native.Actor, target.Reference, grant.GrantId));
            Assert.Equal(HttpStatusCode.Forbidden, (await Call(Guid.NewGuid())).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await Call(r1)).StatusCode);
            Assert.Equal(InvocationGrantOutcome.Applied, await grants.GrantAsync(native.Actor, new(Guid.NewGuid(), target.Reference, caller)));
            await Completed(await Call(r1), firstRelease, first, replayed: true);
            Assert.True(await releases.DisableAsync(native.Actor, target.Reference));
            Assert.Equal(HttpStatusCode.Forbidden, (await Call(Guid.NewGuid())).StatusCode);

            // Audit evidence: exact release and build provenance with digests, never payload bytes.
            var audit = await native.Case.ScalarAsync<string>("""
                SELECT jsonb_agg(jsonb_build_array(release_id,build_id,revision_id,artifact_digest,runtime_image,output_digest) ORDER BY recorded_at)::text
                FROM weft.machine_invocation_audit WHERE kind='completed'
                """);
            foreach (var release in new[] { firstRelease, secondRelease, rollback })
                Assert.Contains(release.ToString("D"), audit, StringComparison.Ordinal);
            Assert.Contains(runtimeImage, audit, StringComparison.Ordinal);
            Assert.DoesNotContain(Convert.ToBase64String(Expected), audit, StringComparison.Ordinal);
            output.WriteLine("Completed invocations: " + await native.Case.ScalarAsync<long>("SELECT count(*) FROM weft.machine_invocation_audit WHERE kind='completed'"));
        }
        finally
        {
            await host.StopAsync();
            await resources.DisposeAsync();
        }
    }

    internal static byte[] Body(byte[] primary) => Encoding.UTF8.GetBytes(new JsonObject
    {
        ["inputs"] = new JsonArray(new JsonObject { ["content"] = Convert.ToBase64String(primary) }),
        ["parameters"] = new JsonArray()
    }.ToJsonString());

    // Sends the headers and part of a declared body, then waits: the handler holds its slot while reading.
    private static async Task<IDisposable> HoldSlotAsync(int port, string token)
    {
        var socket = new TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, port);
        var stream = socket.GetStream();
        var head = $"POST {MappingEndpointRoutes.InvocationsPath} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nAuthorization: Bearer {token}\r\n" +
            $"{MappingEndpointRoutes.RequestIdHeader}: {Guid.NewGuid():D}\r\nContent-Type: application/json\r\nContent-Length: 1000\r\n\r\n{{\"inputs\":";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await stream.FlushAsync();
        await Task.Delay(TimeSpan.FromSeconds(2)); // Token validation completes and the body read begins.
        return socket;
    }

    internal static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class DisposableCrypto : IAsyncDisposable
    {
        internal EntraProtocolCrypto Value { get; } = new();
        public ValueTask DisposeAsync() { Value.Dispose(); return ValueTask.CompletedTask; }
    }

    /// <summary>Protected 0600 credential files in a private 0700 directory, as the hosted profile requires.</summary>
    internal sealed class PrivateFiles : IDisposable
    {
        private readonly string directory;
        internal PrivateFiles(string root)
        {
            directory = Path.Combine(root, "credentials");
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        internal Func<string, string?> Environment(IReadOnlyDictionary<string, string> values, IReadOnlyDictionary<string, string> connections)
        {
            var all = new Dictionary<string, string>(values, StringComparer.Ordinal);
            var delivery = Path.Combine(directory, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(delivery, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            foreach (var (setting, connection) in connections)
            {
                var path = Path.Combine(delivery, setting.ToLowerInvariant());
                File.WriteAllText(path, connection);
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                all[setting + "_FILE"] = path;
            }
            return name => all.TryGetValue(name, out var value) ? value : null;
        }
        public void Dispose() { try { Directory.Delete(directory, recursive: true); } catch (IOException) { } }
    }
}
