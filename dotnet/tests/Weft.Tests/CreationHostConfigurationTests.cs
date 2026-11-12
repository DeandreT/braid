using System.Net;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Weft.Api.Creation;
using Weft.Application;
using Weft.Domain;
using Weft.MappingBackends;
using Weft.MappingBackends.Workers;
using Weft.Persistence.Postgres;
using Weft.Server;
using Weft.WorkerTransport;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
public sealed class CreationHostConfigurationTests(PostgresFixture postgres)
{
    public sealed class CreationHostLinuxFactAttribute : FactAttribute
    {
        public CreationHostLinuxFactAttribute()
        { if (!OperatingSystem.IsLinux()) Skip = "Protected session credential-file admission requires Linux."; }
    }

    [CreationHostLinuxFact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task Protected_configuration_registers_concrete_factories_without_opening_connections_or_service_channels()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "weft-creation-host-" + Guid.NewGuid().ToString("N"))).FullName;
        File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var configuration = new Dictionary<string, string> { ["WEFT_CREATION_ENABLED"] = "true" };
            foreach (var (setting, role) in new[] { (CreationHostConfiguration.ReaderConnection, "weft_creation_reader"),
                (CreationHostConfiguration.WriterConnection, "weft_creation_writer"), (CreationHostConfiguration.ApproverConnection, "weft_creation_approver"),
                ("WEFT_CHAT_READER_CONNECTION_STRING", "weft_chat_reader"), ("WEFT_POSTGRES_SPEC_READER_CONNECTION_STRING", "weft_spec_reader") })
            {
                var file = Path.Combine(root, role);
                File.WriteAllText(file, $"Host=127.0.0.1;Port=1;Database=synthetic;Username={role};Password=synthetic-{role}");
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                configuration.Add(setting + "_FILE", file);
            }
            var services = new ServiceCollection(); var sourcePorts = new CreationRuntimeFixture();
            services.AddSingleton<ISpecBundleServiceFactory>(sourcePorts); services.AddSingleton<IRevisionIngestionService>(sourcePorts);
            services.AddSingleton<IIngestionWorkflowReader>(sourcePorts);
            services.AddSingleton<ChatHostDatabases>(_ => throw new InvalidOperationException("Unexpected pool resolution."));
            services.AddSingleton(new SpecStoreBinding(Guid.NewGuid(), SpecStoreKind.FileSystem, Sha256Digest.FromBytes("synthetic-store"u8)));
            services.AddKeyedSingleton<PostgresSpecBundleDatabase>(SpecDatabaseProfile.Reader, (_, _) => throw new InvalidOperationException("Unexpected pool resolution."));
            InspectionWorkerHostConfiguration.ConfigureConnector(services, _ => throw new InvalidOperationException("Unexpected connector resolution."), BackendHost.Identity);
            CreationHostConfiguration.Configure(services, key => configuration.GetValueOrDefault(key), session: true);
            await using var provider = services.BuildServiceProvider();
            Assert.True(provider.GetRequiredService<CreationRuntimeOptions>().Enabled);
            Assert.Contains(services, d => d.ServiceType == typeof(ICreationRuntimeAdmission) && d.ImplementationType == typeof(CreationHostAdmission));
            configuration[CreationHostConfiguration.ReaderConnection] = "unprotected";
            Assert.Throws<InvalidOperationException>(() => CreationHostConfiguration.Configure(services, key => configuration.GetValueOrDefault(key), session: true));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Disabled_composition_reads_only_flag_and_opens_no_dependencies()
    {
        var services = new ServiceCollection();
        CreationHostConfiguration.Configure(services, key => { Assert.Equal("WEFT_CREATION_ENABLED", key); return null; }, session: true);
        await using var provider = services.BuildServiceProvider();
        Assert.False(provider.GetRequiredService<CreationRuntimeOptions>().Enabled);
        Assert.Null(provider.GetService<ICreationRuntimeAdmission>());
        Assert.False(await CreationRuntime.AvailableAsync(provider, default));
    }

    [Fact]
    public void Enabled_non_session_host_refuses_before_reading_credentials()
    {
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() => CreationHostConfiguration.Configure(services,
            key => { Assert.Equal("WEFT_CREATION_ENABLED", key); return "true"; }, session: false));
    }

    [Theory]
    [InlineData("role")]
    [InlineData("password")]
    [InlineData("database")]
    [InlineData("reset")]
    public void Connection_admission_refuses_role_reuse_cross_database_and_unreset_pools(string fault)
    {
        string Connection(string role, string password, string database = "synthetic") => $"Host=127.0.0.1;Database={database};Username={role};Password={password}";
        var r = Connection("weft_creation_reader", "synthetic-reader");
        var w = Connection(fault == "role" ? "weft_creation_reader" : "weft_creation_writer", fault == "password" ? "synthetic-reader" : "synthetic-writer",
            fault == "database" ? "different" : "synthetic");
        var a = Connection("weft_creation_approver", "synthetic-approver") + (fault == "reset" ? ";No Reset On Close=true" : "");
        Assert.Throws<InvalidOperationException>(() => CreationHostConfiguration.AdmitConnections(r, w, a));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Localhost_pinned_service_probe_checks_build_and_exact_required_profile(bool previewServed)
    {
        await using var host = await BackendHost.Start(previewServed);
        await using var authoring = CreationRunner(host.Connector, FerruleServiceProtocol.AuthoringProfile);
        await using var preview = CreationRunner(host.Connector, FerruleServiceProtocol.AuthoredPreviewProfile);
        Assert.Equal(WorkerProcessFailure.None, (await authoring.RunAsync(CreationFrame(FerruleServiceProtocol.AuthoringProfile), default)).Failure);
        Assert.Equal(previewServed ? WorkerProcessFailure.None : WorkerProcessFailure.SandboxUnavailable,
            (await preview.RunAsync(CreationFrame(FerruleServiceProtocol.AuthoredPreviewProfile), default)).Failure);
        await using var wrong = new FerruleServiceRunner(new(host.Connector, FerruleServiceProtocol.AuthoringProfile,
            "sha256:" + new string('2', 64), requiredCreationEngineRevision: FerruleInspectionBackend.EngineRevision));
        Assert.Equal(WorkerProcessFailure.SandboxUnavailable, (await wrong.RunAsync(CreationFrame(FerruleServiceProtocol.AuthoringProfile), default)).Failure);
        Assert.Single(host.Authoring.Frames); Assert.Equal(previewServed ? 1 : 0, host.Preview.Frames.Count);
        await authoring.StopAsync();
        Assert.Equal(WorkerProcessFailure.SandboxUnavailable, (await authoring.RunAsync(CreationFrame(FerruleServiceProtocol.AuthoringProfile), default)).Failure);
    }

    [Fact]
    public void Placement_requires_the_existing_chat_and_spec_database_with_separate_credentials()
    {
        string C(string role, string database = "synthetic", string? password = null) => $"Host=127.0.0.1;Database={database};Username={role};Password={password ?? "synthetic-" + role}";
        var r = C("weft_creation_reader"); var w = C("weft_creation_writer"); var a = C("weft_creation_approver");
        CreationHostConfiguration.AdmitPlacement(r, w, a, C("weft_chat_reader"), C("weft_spec_reader"));
        Assert.Throws<InvalidOperationException>(() => CreationHostConfiguration.AdmitPlacement(r, w, a, C("weft_chat_reader", "different"), C("weft_spec_reader")));
        Assert.Throws<InvalidOperationException>(() => CreationHostConfiguration.AdmitPlacement(r, w, a, C("weft_chat_reader"), C("weft_spec_reader", password: "synthetic-weft_creation_reader")));
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("build")]
    [InlineData("duplicate")]
    [InlineData("replica")]
    [InlineData("engine")]
    [InlineData("malformed")]
    public async Task Probe_refuses_tampered_capability_transcripts_from_the_local_service(string change)
    {
        await using var host = await BackendHost.Start(true);
        var connector = new TamperedCapabilities(host.Connector, change);
        await using var authoring = CreationRunner(connector, FerruleServiceProtocol.AuthoringProfile);
        Assert.Equal(WorkerProcessFailure.SandboxUnavailable, (await authoring.RunAsync(CreationFrame(FerruleServiceProtocol.AuthoringProfile), default)).Failure);
        Assert.Empty(host.Authoring.Frames);
    }

    private static FerruleServiceRunner CreationRunner(IFerruleServiceConnector connector, string profile) =>
        new(new(connector, profile, BackendHost.Identity, requiredCreationEngineRevision: FerruleInspectionBackend.EngineRevision));

    private static byte[] CreationFrame(string profile)
    {
        var header = System.Text.Encoding.UTF8.GetBytes("{\"protocolVersion\":\"weft.ferrule-" + profile + ".v1\"}");
        var frame = new byte[4 + header.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)header.Length);
        header.CopyTo(frame, 4);
        return frame;
    }

    [Theory]
    [InlineData(FerruleServiceProtocol.AuthoringProfile, "profile", false)]
    [InlineData(FerruleServiceProtocol.AuthoringProfile, "engine", false)]
    [InlineData(FerruleServiceProtocol.AuthoringProfile, "profile", true)]
    [InlineData(FerruleServiceProtocol.AuthoringProfile, "engine", true)]
    [InlineData(FerruleServiceProtocol.AuthoredPreviewProfile, "profile", false)]
    [InlineData(FerruleServiceProtocol.AuthoredPreviewProfile, "engine", false)]
    [InlineData(FerruleServiceProtocol.AuthoredPreviewProfile, "profile", true)]
    [InlineData(FerruleServiceProtocol.AuthoredPreviewProfile, "engine", true)]
    public async Task Execution_and_retry_channels_require_their_own_capabilities(string profile, string change, bool retry)
    {
        await using var good = await BackendHost.Start(true);
        await using var bad = await BackendHost.Start(true);
        var connector = new SplitReplicaConnector(good.Connector, new TamperedCapabilities(bad.Connector, change, profile), retry);
        // A successful earlier proof must confer no authority on a later channel, even with the same build pin.
        await using (var first = CreationRunner(connector, profile))
            Assert.Equal(WorkerProcessFailure.None, (await first.RunAsync(CreationFrame(profile), default)).Failure);
        await using var executing = CreationRunner(connector, profile);
        Assert.Equal(WorkerProcessFailure.SandboxUnavailable, (await executing.RunAsync(CreationFrame(profile), default)).Failure);
        Assert.Equal(retry ? 3 : 2, connector.Opened);
        Assert.Equal(connector.Opened, connector.CapabilityRequests);
        Assert.Equal(retry ? 1 : 0, connector.BusyFrames);
        Assert.Equal(0, connector.BadPosts);
        Assert.Empty(bad.Authoring.Frames); Assert.Empty(bad.Preview.Frames);
        Assert.Single(profile == FerruleServiceProtocol.AuthoringProfile ? good.Authoring.Frames : good.Preview.Frames);
    }

    private sealed class SplitReplicaConnector(IFerruleServiceConnector good, IFerruleServiceConnector bad, bool retry) : IFerruleServiceConnector
    {
        internal int Opened, CapabilityRequests, BusyFrames, BadPosts;
        public IFerruleServiceChannel Open()
        {
            var index = ++Opened;
            var busy = retry && index == 2;
            var incompatible = index > 1 && !busy;
            return new Channel((incompatible ? bad : good).Open(), this, busy, incompatible);
        }
        private sealed class Channel(IFerruleServiceChannel inner, SplitReplicaConnector owner, bool busy, bool incompatible) : IFerruleServiceChannel
        {
            public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                if (request.RequestUri!.OriginalString == FerruleServiceProtocol.CapabilitiesPath) owner.CapabilityRequests++;
                if (request.Method == HttpMethod.Post)
                {
                    if (incompatible) owner.BadPosts++;
                    if (busy)
                    {
                        Assert.NotEmpty(await request.Content!.ReadAsByteArrayAsync(ct));
                        owner.BusyFrames++;
                        var refused = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                        refused.Headers.Add(FerruleServiceProtocol.ProtocolHeader, FerruleServiceProtocol.Version);
                        return refused;
                    }
                }
                return await inner.SendAsync(request, ct);
            }
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    private sealed class TamperedCapabilities(IFerruleServiceConnector inner, string change, string profile = FerruleServiceProtocol.AuthoringProfile) : IFerruleServiceConnector
    {
        public IFerruleServiceChannel Open() => new Channel(inner.Open(), change, profile);
        private sealed class Channel(IFerruleServiceChannel inner, string change, string profile) : IFerruleServiceChannel
        {
            public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var capabilities = request.RequestUri!.OriginalString == FerruleServiceProtocol.CapabilitiesPath;
                var response = await inner.SendAsync(request, ct);
                if (!capabilities) return response;
                var body = System.Text.Json.Nodes.JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
                if (change == "build") body["buildIdentity"] = "sha256:" + new string('2', 64);
                if (change == "profile") body["profiles"]!.AsArray().Single(p => p!["name"]!.GetValue<string>() == profile)!["maximumRequestBytes"] = 1;
                if (change == "engine") body["engineRevision"] = "incompatible-engine";
                if (change == "duplicate") body["profiles"]!.AsArray().Add(body["profiles"]![0]!.DeepClone());
                if (change == "replica")
                {
                    response.Headers.Remove(FerruleServiceProtocol.ReplicaHeader);
                    response.Headers.Add(FerruleServiceProtocol.ReplicaHeader, "different-synthetic-replica");
                }
                response.Content.Dispose();
                response.Content = new StringContent(change == "malformed" ? "{" : body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
                return response;
            }
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    [PostgresTheory]
    [InlineData("rounds")]
    [InlineData("audit")]
    [InlineData("citation")]
    public async Task Concrete_composition_admits_real_46_and_preserves_durable_admission_when_worker_and_store_are_unavailable(string changed)
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(postgres);
        await using var backend = await BackendHost.Start(true);
        var services = new ServiceCollection();
        var sourcePorts = new CreationRuntimeFixture(); // Unused physical-source/ingestion ports; this test qualifies concrete DB admission, not those stores.
        services.AddSingleton<ISpecBundleServiceFactory>(sourcePorts);
        services.AddSingleton<IRevisionIngestionService>(sourcePorts);
        services.AddSingleton<IIngestionWorkflowReader>(test.Store);
        services.AddSingleton(new CreationRuntimeOptions(true));
        var binding = new SpecStoreBinding(Guid.NewGuid(), SpecStoreKind.FileSystem, Sha256Digest.FromBytes("synthetic-store"u8));
        services.AddSingleton(binding);
        await test.Case.ExecuteAsync("INSERT INTO weft.spec_stores VALUES($1,'filesystem',$2,false)", binding.StoreId, binding.LocationDigest.Value);
        services.AddKeyedSingleton(SpecDatabaseProfile.Reader, (_, _) => new PostgresSpecBundleDatabase(
            test.Case.RegressionConnectionString("weft_spec_reader"), SpecDatabaseProfile.Reader));
        services.AddSingleton(_ => new ChatHostDatabases(test.Case.ChatReaderConnectionString,
            test.Case.ChatWriterConnectionString, postgres.ChatApproverConnectionString(test.Case)));
        InspectionWorkerHostConfiguration.ConfigureConnector(services, _ => backend.Connector, BackendHost.Identity);
        CreationHostConfiguration.Register(services, test.Case.RegressionConnectionString("weft_creation_reader"),
            test.Case.RegressionConnectionString("weft_creation_writer"), test.Case.RegressionConnectionString("weft_creation_approver"));
        await using var provider = services.BuildServiceProvider();
        Assert.True(CreationRuntime.Configured(provider));
        Assert.IsType<FerruleMappingCreationCompiler>(provider.GetRequiredService<IMappingCreationCompiler>());
        Assert.IsType<FerruleMappingCreationPreview>(provider.GetRequiredService<IMappingCreationPreview>());
        Assert.IsType<PostgresCreationDiscovery>(provider.GetRequiredService<ICreationDiscovery>());
        Assert.Same(provider.GetRequiredService<IMappingCreationProposalStore>(), provider.GetRequiredService<IMappingCreationApprovalStore>());
        Assert.True(await CreationRuntime.AvailableAsync(provider, default));
        await provider.GetRequiredKeyedService<FerruleServiceRunner>(FerruleServiceProtocol.AuthoringProfile).StopAsync();
        await provider.GetRequiredKeyedService<FerruleServiceRunner>(FerruleServiceProtocol.AuthoredPreviewProfile).StopAsync();
        Assert.True(await CreationRuntime.AvailableAsync(provider, default));
        await test.Case.ExecuteAsync(changed switch
        {
            "rounds" => "ALTER TABLE weft.chat_turns DROP CONSTRAINT chat_turns_max_rounds_check",
            "audit" => "ALTER TABLE weft.chat_payload_audit DROP CONSTRAINT chat_payload_audit_source_kind_check",
            _ => "CREATE OR REPLACE FUNCTION weft.chat_metadata_valid(kind integer,value text,expected_round uuid) RETURNS boolean LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE SECURITY INVOKER SET search_path=pg_catalog,weft,pg_temp AS 'BEGIN RETURN true; END'"
        });
        Assert.False(await CreationRuntime.AvailableAsync(provider, default));
    }

    [MappingAuthoredPreviewInteropTests.NativeAuthoredPreviewFact]
    public async Task Registered_compiler_and_preview_execute_actual_native_protocols_through_localhost_service()
    {
        var authoringPath = Environment.GetEnvironmentVariable("WEFT_AUTHORING_TEST_EXECUTABLE")!;
        var previewPath = Environment.GetEnvironmentVariable("WEFT_AUTHORED_PREVIEW_TEST_EXECUTABLE")!;
        var build = "sha256:" + Sha256Digest.FromBytes(File.ReadAllBytes(authoringPath).Concat(File.ReadAllBytes(previewPath)).ToArray()).Value;
        using var serverCertificate = InspectionTransportTests.TransportCase.Certificate("CN=Synthetic native creation service");
        using var clientCertificate = InspectionTransportTests.TransportCase.Certificate("CN=Synthetic native creation client");
        var authoring = new NativeProtocolRunner(authoringPath, build, DockerWorkerProfile.Authoring.MaximumOutputBytes);
        var preview = new NativeProtocolRunner(previewPath, build, DockerWorkerProfile.AuthoredPreview.MaximumOutputBytes);
        await using var server = new WorkerTransportServer(new WorkerTransportServerOptions(new IPEndPoint(IPAddress.Loopback, 0), serverCertificate,
            [InspectionTransportTests.TransportCase.Pin(clientCertificate)]) { ServiceRoutes = true, EngineRevision = FerruleInspectionBackend.EngineRevision },
            new Dictionary<string, IInspectionWorkerRunner> { [FerruleServiceProtocol.AuthoringProfile] = authoring, [FerruleServiceProtocol.AuthoredPreviewProfile] = preview });
        await server.StartAsync(default);
        var connector = new PinnedServiceConnector(new(new Uri("https://127.0.0.1:" + server.Port), InspectionTransportTests.TransportCase.Pin(serverCertificate), clientCertificate));
        var services = new ServiceCollection();
        InspectionWorkerHostConfiguration.ConfigureConnector(services, _ => connector, build);
        // No database connection is opened by registration or by backend execution.
        string Connection(string role) => $"Host=127.0.0.1;Port=1;Database=synthetic;Username={role};Password=synthetic-{role}";
        CreationHostConfiguration.Register(services, Connection("weft_creation_reader"), Connection("weft_creation_writer"), Connection("weft_creation_approver"));
        await using var provider = services.BuildServiceProvider();
        var f = new CreationRuntimeFixture(); var id = Guid.NewGuid();
        var compiled = await provider.GetRequiredService<IMappingCreationCompiler>().CompileAsync(f.Bundle.Project, id, f.Plan, f.Specification.Snapshot, default);
        Assert.Null(compiled.Failure); Assert.NotNull(compiled.Checkpoint);
        var proposal = new MappingCreationProposal(WorkspaceToolFixture.Actor.PrincipalId, id, Guid.NewGuid(), f.Specification, f.Plan,
            new([new(f.Source.Identity, 0)], [], []), compiled.Checkpoint!, new(new("Synthetic mapping", "Fixture"), "ferrule"));
        var result = await provider.GetRequiredService<IMappingCreationPreview>().RunAsync(proposal, "{\"a\":\"value\"}"u8.ToArray(), default);
        Assert.Equal(CreationPreviewOutcome.Succeeded, result.Outcome);
        Assert.Contains("value", System.Text.Encoding.UTF8.GetString(result.CopyOutput()));
        Assert.Equal(1, authoring.Calls); Assert.Equal(1, preview.Calls);
    }

    private sealed class NativeProtocolRunner(string path, string identity, int maximumOutput) : IInspectionWorkerRunner
    {
        public string BuildIdentity => identity;
        internal int Calls;
        public async Task<WorkerProcessResult> RunAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
        {
            Calls++;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(20));
            using var process = Process.Start(new ProcessStartInfo(path) { RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true, UseShellExecute = false })!;
            async Task<byte[]> Drain(Stream stream, int bound)
            {
                using var bytes = new MemoryStream(); var buffer = new byte[8192];
                while (true)
                {
                    var count = await stream.ReadAsync(buffer, deadline.Token); if (count == 0) return bytes.ToArray();
                    if (bytes.Length + count > bound) throw new InvalidOperationException("Synthetic protocol fixture exceeded its bound.");
                    bytes.Write(buffer, 0, count);
                }
            }
            try
            {
                var output = Drain(process.StandardOutput.BaseStream, maximumOutput); var error = Drain(process.StandardError.BaseStream, 65_536);
                await process.StandardInput.BaseStream.WriteAsync(frame, deadline.Token); process.StandardInput.Close();
                await Task.WhenAll(output, error, process.WaitForExitAsync(deadline.Token));
                Assert.Equal(0, process.ExitCode); Assert.Empty(await error);
                return new(await output, WorkerProcessFailure.None);
            }
            finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); } }
        }
    }

    internal sealed class BackendHost : IAsyncDisposable
    {
        internal const string Identity = "sha256:1111111111111111111111111111111111111111111111111111111111111111";
        private readonly System.Security.Cryptography.X509Certificates.X509Certificate2 serverCertificate = InspectionTransportTests.TransportCase.Certificate("CN=Synthetic creation service");
        private readonly System.Security.Cryptography.X509Certificates.X509Certificate2 clientCertificate = InspectionTransportTests.TransportCase.Certificate("CN=Synthetic creation client");
        internal readonly InspectionTransportTests.ScriptedRunner Authoring = new(), Preview = new();
        private WorkerTransportServer server = null!;
        internal PinnedServiceConnector Connector = null!;
        internal static async Task<BackendHost> Start(bool preview)
        {
            var host = new BackendHost();
            var runners = new Dictionary<string, IInspectionWorkerRunner> { [FerruleServiceProtocol.AuthoringProfile] = host.Authoring };
            if (preview) runners.Add(FerruleServiceProtocol.AuthoredPreviewProfile, host.Preview);
            host.server = new(new WorkerTransportServerOptions(new IPEndPoint(IPAddress.Loopback, 0), host.serverCertificate,
                [InspectionTransportTests.TransportCase.Pin(host.clientCertificate)]) { ServiceRoutes = true, EngineRevision = FerruleInspectionBackend.EngineRevision }, runners);
            await host.server.StartAsync(default);
            host.Connector = new(new(new Uri("https://127.0.0.1:" + host.server.Port), InspectionTransportTests.TransportCase.Pin(host.serverCertificate), host.clientCertificate));
            return host;
        }
        public async ValueTask DisposeAsync()
        {
            await server.DisposeAsync(); serverCertificate.Dispose(); clientCertificate.Dispose();
        }
    }
}
