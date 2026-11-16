using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Braid.Api;
using Braid.Api.Catalog;
using Braid.Application;
using Braid.Deployment;
using Braid.Deployment.Sandbox;
using Braid.Domain;
using Braid.MappingBackends.Workers;
using Braid.Server;

namespace Braid.Tests;

public sealed class LocalSandboxTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "braid-sandbox-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task Disabled_routes_are_opaque_without_resolving_any_sandbox_or_catalog_credentials()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddBraidApi();
        await using var app = builder.Build();
        app.MapBraidLocalSandbox();
        await app.StartAsync();
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new(app.Urls.Single()) };
        var mapping = $"/api/v1/organizations/{Guid.NewGuid():D}/projects/{Guid.NewGuid():D}/mappings/{Guid.NewGuid():D}/sandbox";
        foreach (var (method, suffix) in new[]
        {
            (HttpMethod.Get, ""), (HttpMethod.Post, "/deployments/" + Guid.NewGuid()),
            (HttpMethod.Get, "/" + Guid.NewGuid() + "/messages"),
            (HttpMethod.Get, "/" + Guid.NewGuid() + "/messages/" + Guid.NewGuid()),
            (HttpMethod.Post, "/" + Guid.NewGuid() + "/messages/" + Guid.NewGuid())
        })
        {
            using var request = new HttpRequestMessage(method, mapping + suffix);
            using var response = await client.SendAsync(request);
            Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
            Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        }
        await app.StopAsync();
    }

    [Fact]
    public void Derived_identities_are_stable_distinct_canonical_uuids()
    {
        var organization = Guid.NewGuid();
        var first = SandboxIdentity.Derive("braid.test", organization.ToString("D"), 1);
        Assert.Equal(first, SandboxIdentity.Derive("braid.test", organization.ToString("D"), 1));
        Assert.NotEqual(first, SandboxIdentity.Derive("braid.test", organization.ToString("D"), 2));
        Assert.NotEqual(first, SandboxIdentity.Derive("braid.other", organization.ToString("D"), 1));
        Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-8[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", first.ToString("D"));
        var caller = SandboxIdentity.Caller(organization, DateTimeOffset.UnixEpoch);
        Assert.Equal(caller.Principal, SandboxIdentity.Caller(organization, DateTimeOffset.UtcNow).Principal);
        Assert.NotEqual(caller.Principal, SandboxIdentity.Caller(Guid.NewGuid(), DateTimeOffset.UnixEpoch).Principal);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddMinutes(5), caller.ExpiresAt);
    }

    [Fact]
    public async Task File_history_is_bounded_newest_first_and_replaces_a_retried_message()
    {
        var history = new FileSandboxHistory(root);
        var target = Guid.NewGuid();
        var start = DateTimeOffset.UnixEpoch.AddYears(56);
        var ids = Enumerable.Range(0, FileSandboxHistory.MaximumPerTarget + 2).Select(_ => Guid.NewGuid()).ToArray();
        for (var index = 0; index < ids.Length; index++)
            await history.DeliverAsync(Message(ids[index], target, start.AddSeconds(index), index == 0 || index == ids.Length - 1 ? "unavailable" : "completed"));
        Assert.Equal(FileSandboxHistory.MaximumPerTarget, Directory.GetFiles(Path.Combine(root, target.ToString("D"))).Length);
        Assert.Null(await history.ReadAsync(target, ids[0]));
        Assert.Null(await history.ReadAsync(target, ids[1]));
        var page = await history.ListAsync(target, 3);
        Assert.Equal(ids[^3..].Reverse(), page.Select(value => value.MessageId));
        var stored = Assert.IsType<SandboxMessage>(await history.ReadAsync(target, ids[^1]));
        Assert.Equal("{\"in\":1}"u8.ToArray(), stored.Inputs.Single().Content);
        Assert.Equal(PreviewParameterKind.Integer, stored.Parameters.Single().Kind);
        Assert.False(stored.Final);
        await history.DeliverAsync(Message(ids[^1], target, start.AddDays(1), "failed"));
        Assert.Equal("failed", (await history.ReadAsync(target, ids[^1]))!.Status);
        Assert.Equal(FileSandboxHistory.MaximumPerTarget, Directory.GetFiles(Path.Combine(root, target.ToString("D"))).Length);
        Assert.Empty(await history.ListAsync(Guid.NewGuid(), 10));
        Assert.Throws<ArgumentException>(() => new FileSandboxHistory("relative/history"));
    }

    [Fact]
    public async Task History_reopens_after_restart_and_concurrent_readers_observe_complete_documents()
    {
        var history = new FileSandboxHistory(root);
        var target = Guid.NewGuid();
        var id = Guid.NewGuid();
        await history.DeliverAsync(Message(id, target, DateTimeOffset.UtcNow, "unavailable"));
        await Task.WhenAll(Enumerable.Range(0, 12).Select(async index =>
        {
            await history.DeliverAsync(Message(id, target, DateTimeOffset.UtcNow.AddSeconds(index), "completed"));
            Assert.NotNull(await history.ReadAsync(target, id));
            Assert.Single(await history.ListAsync(target, 10));
        }));
        var reopened = new FileSandboxHistory(root);
        Assert.Equal("completed", (await reopened.ReadAsync(target, id))!.Status);
        Assert.Null(await reopened.ReadAsync(Guid.NewGuid(), id));
        await Assert.ThrowsAsync<ArgumentException>(() => reopened.ListAsync(target, 51));
    }

    [Fact]
    public async Task A_message_runs_through_the_machine_ledger_is_kept_and_a_repeated_id_returns_it_without_running()
    {
        var test = new SendFixture(root);
        var first = await test.SendAsync(Guid.NewGuid());
        Assert.NotNull(first);
        Assert.Equal("completed", first.Status);
        Assert.Equal(test.Invocation.Release!.ReleaseId, first.ReleaseId);
        Assert.Equal(test.Invocation.Data.Build.Intent.BuildId, first.BuildId);
        Assert.Equal(test.Invocation.Data.Result.Outputs!.Outputs.Single().CopyBytes(), Assert.Single(first.Outputs).Content);
        Assert.Equal(1, test.Invocation.RuntimeCalls);
        var again = await test.SendAsync(first.MessageId);
        Assert.Equal(first.SentAt, again!.SentAt);
        Assert.Equal(1, test.Invocation.RuntimeCalls);
        Assert.Single(await test.Service.ListAsync(test.Actor, test.Mapping, test.TargetId, 10) ?? []);
        var finish = Assert.Single(test.Invocation.Finishes);
        Assert.Equal(MachineInvocationOutcome.Completed, finish.Outcome);
    }

    [Fact]
    public async Task A_replay_sends_the_stored_inputs_again_to_the_current_release()
    {
        var test = new SendFixture(root);
        var original = (await test.SendAsync(Guid.NewGuid()))!;
        var sample = await test.Service.ReplaySampleAsync(test.Actor, test.Mapping, test.TargetId, original.MessageId);
        Assert.NotNull(sample);
        Assert.Equal(original.Inputs.Single(input => input.Name is null).Content, sample.Primary.CopyBytes());
        var replay = await test.Service.SendAsync(test.Actor, test.Mapping, test.TargetId, Guid.NewGuid(), sample, original.MessageId);
        Assert.Equal(original.MessageId, replay!.ReplayOf);
        Assert.Equal(original.Outputs.Single().Sha256, replay.Outputs.Single().Sha256);
        Assert.Equal(2, test.Invocation.RuntimeCalls);
    }

    [Fact]
    public async Task A_release_change_during_a_call_is_kept_as_retryable_and_a_retry_runs_again()
    {
        var test = new SendFixture(root);
        test.Invocation.ChangePointerAt = "runtime";
        var id = Guid.NewGuid();
        var first = (await test.SendAsync(id))!;
        Assert.Equal("unavailable", first.Status);
        Assert.False(first.Final);
        Assert.Empty(first.Outputs);
        test.Invocation.ChangePointerAt = null;
        await test.Invocation.Activate();
        test.Target = test.Target with { ActiveReleaseId = test.Invocation.Release!.ReleaseId };
        Assert.Equal("completed", (await test.SendAsync(id))!.Status);
        Assert.Equal(2, test.Invocation.RuntimeCalls);
    }

    [Fact]
    public async Task Another_target_or_mapping_is_not_a_sandbox_target()
    {
        var test = new SendFixture(root);
        Assert.Null(await test.Service.SendAsync(test.Actor, test.Mapping, Guid.NewGuid(), Guid.NewGuid(), test.Sample()));
        test.RuntimeImage = "sha256:" + new string('9', 64);
        Assert.Null(await test.SendAsync(Guid.NewGuid()));
        Assert.Null(await test.Service.ListAsync(test.Actor, test.Mapping, test.TargetId, 10));
        Assert.Equal(0, test.Invocation.RuntimeCalls);
    }

    [Fact]
    public async Task Another_authorized_publisher_sends_to_a_document_release_under_the_pinned_publishers_boundaries()
    {
        var test = new SendFixture(root, "x12");
        var publisher = test.Invocation.Data.Actor;
        test.Sender = new(new PrincipalId(Guid.NewGuid()));
        var sent = (await test.SendAsync(Guid.NewGuid()))!;
        Assert.Equal("completed", sent.Status);
        Assert.Equal("application/json", Assert.Single(sent.Inputs).MediaType);
        Assert.Equal("application/edi-x12", Assert.Single(sent.Outputs).MediaType);
        Assert.Equal(test.Invocation.Data.Build.Intent.BuildId, sent.BuildId);
        // Boundaries come from the release's own approval, read as its publisher, never as the sender.
        Assert.Equal([publisher], test.Builds.Readers);
        Assert.Equal(1, test.Invocation.RuntimeCalls);
    }

    [Fact]
    public async Task Unverifiable_boundaries_or_an_undeclared_input_leave_no_claim_and_no_receipt()
    {
        var test = new SendFixture(root, "x12");
        test.Builds.Unreadable = true;
        var id = Guid.NewGuid();
        var unavailable = (await test.SendAsync(id))!;
        Assert.Equal("unavailable", unavailable.Status);
        Assert.False(unavailable.Final);
        Assert.Null(await test.Service.ReadAsync(test.Actor, test.Mapping, test.TargetId, id));
        var extra = new SandboxSample(test.Invocation.Data.Case.Replay.Primary, [new("undeclared", "{}"u8)], [], "inline");
        test.Builds.Unreadable = false;
        await Assert.ThrowsAsync<ArgumentException>(() => test.Service.SendAsync(test.Actor, test.Mapping, test.TargetId, id, extra));
        Assert.Equal(0, test.Invocation.Claims);
        Assert.Empty((await test.Service.ListAsync(test.Actor, test.Mapping, test.TargetId, 10))!);
        // The same ID then runs normally once its declaration resolves.
        Assert.Equal("completed", (await test.SendAsync(id))!.Status);
        Assert.Equal(1, test.Invocation.Claims);
    }

    [Fact]
    public async Task A_repeated_id_with_other_bytes_is_a_conflict_without_outputs_and_keeps_the_original()
    {
        var test = new SendFixture(root);
        var first = (await test.SendAsync(Guid.NewGuid()))!;
        var other = new SandboxSample(new(null, "{\"other\":true}"u8), [], [], "inline");
        var conflict = (await test.Service.SendAsync(test.Actor, test.Mapping, test.TargetId, first.MessageId, other))!;
        Assert.Equal("conflict", conflict.Status);
        Assert.Empty(conflict.Outputs);
        Assert.Equal(1, test.Invocation.RuntimeCalls);
        var kept = (await test.Service.ReadAsync(test.Actor, test.Mapping, test.TargetId, first.MessageId))!;
        Assert.Equal("completed", kept.Status);
        Assert.Equal(first.Inputs.Single().Content, kept.Inputs.Single().Content);
    }

    [Fact]
    public async Task A_repeat_after_disable_or_revocation_is_refused_and_an_expired_result_returns_no_outputs()
    {
        var test = new SendFixture(root);
        var first = (await test.SendAsync(Guid.NewGuid()))!;
        // The ledger refuses a disabled target or revoked grant before it returns any recorded outcome.
        test.Invocation.ForcedClaim = MachineInvocationClaimStatus.Refused;
        var refused = (await test.SendAsync(first.MessageId))!;
        Assert.Equal("refused", refused.Status);
        Assert.Empty(refused.Outputs);
        test.Invocation.ForcedClaim = null;
        test.Invocation.ExpireResponses();
        var expired = (await test.SendAsync(first.MessageId))!;
        Assert.Equal("expired", expired.Status);
        Assert.True(expired.Replayed);
        Assert.Empty(expired.Outputs);
        Assert.Equal(1, test.Invocation.RuntimeCalls);
        // Reading history is a separate operation and still shows the retained original.
        var kept = (await test.Service.ReadAsync(test.Actor, test.Mapping, test.TargetId, first.MessageId))!;
        Assert.Equal("completed", kept.Status);
        Assert.Single(kept.Outputs);
    }

    [Fact]
    public async Task A_retry_keeps_its_original_media_declaration_when_the_release_boundaries_cannot_be_read_again()
    {
        var test = new SendFixture(root, "x12");
        var first = (await test.SendAsync(Guid.NewGuid()))!;
        Assert.Equal("completed", first.Status);
        test.Builds.Unreadable = true;
        var again = (await test.SendAsync(first.MessageId))!;
        Assert.Equal("completed", again.Status);
        Assert.True(again.Replayed);
        Assert.Equal(first.Outputs.Single().Sha256, again.Outputs.Single().Sha256);
        Assert.Equal(1, test.Builds.Reads);
        Assert.Equal(1, test.Invocation.RuntimeCalls);
    }

    [LinuxFact]
    public void Session_hosts_refuse_every_sandbox_setting()
    {
        foreach (var setting in LocalSandboxHostConfiguration.Settings)
        {
            var values = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["BRAID_CATALOG_ENABLED"] = "true",
                ["BRAID_CATALOG_AUTH_MODE"] = "session",
                ["BRAID_PUBLIC_ORIGIN"] = "https://127.0.0.1:58413",
                ["BRAID_LISTEN_ADDR"] = "127.0.0.1:58413",
                ["BRAID_HOST_TRANSPORT"] = "directHttps",
                ["BRAID_SESSION_ORGANIZATION_ID"] = "97b79dcc-d078-4994-9a06-f75147842811",
                ["BRAID_ENTRA_TENANT_ID"] = "e6492421-de0b-4626-87a1-773d7f77e0aa",
                ["BRAID_ENTRA_CLIENT_ID"] = "39ceba91-5e4d-4ead-9be2-ddbf843be1c6",
                ["BRAID_SESSION_APPLICATION_NAME"] = "Sandbox refusal",
                ["BRAID_SESSION_KEY_DIRECTORY"] = "/owned-unopened-key-ring"
            };
            _ = ServerHostOptions.Read(values.GetValueOrDefault);
            values[setting] = setting == LocalSandboxHostConfiguration.Enabled ? "false" : "x";
            Assert.Throws<InvalidOperationException>(() => ServerHostOptions.Read(values.GetValueOrDefault));
        }
    }

    [Theory]
    [InlineData("Production", "true", null)]
    [InlineData("Development", null, LocalSandboxHostConfiguration.History)]
    [InlineData("Development", "TRUE", null)]
    [InlineData("Development", "true", "BRAID_LOCAL_SANDBOX_BUILD_READER_CONNECTION_STRING")]
    public void Development_hosts_require_an_explicit_complete_local_configuration(string environment, string? enabled, string? wrong)
    {
        var values = DevelopmentSettings();
        values[LocalSandboxHostConfiguration.Enabled] = enabled;
        // A reader login named for another role, or a stray setting without the opt-in.
        if (wrong is not null) values[wrong] = wrong.EndsWith("_CONNECTION_STRING", StringComparison.Ordinal)
            ? "Host=127.0.0.1;Username=braid_build_writer;Password=unused" : "/tmp/history";
        Assert.ThrowsAny<Exception>(() => LocalSandboxHostConfiguration.Configure(new ServiceCollection(), Host(environment), values.GetValueOrDefault, true));
        Assert.Throws<ArgumentException>(() => LocalSandboxHostConfiguration.Configure(new ServiceCollection(), Host("Development"),
            DevelopmentSettings().GetValueOrDefault, false));
    }

    [LinuxFact]
    public void A_complete_development_configuration_registers_the_sandbox_without_connecting()
    {
        var services = new ServiceCollection();
        LocalSandboxHostConfiguration.Configure(services, Host("Development"), DevelopmentSettings().GetValueOrDefault, true);
        Assert.Contains(services, service => service.ServiceType == typeof(LocalSandboxService));
        Assert.Contains(services, service => service.ServiceType == typeof(Braid.Api.Catalog.LocalSandboxOptions));
    }

    [LinuxFact]
    public async Task The_sandbox_runner_uses_the_host_docker_budgets_and_is_drained_on_host_stop()
    {
        var values = DevelopmentSettings();
        values[DockerRunnerBudgets.CreateSetting] = "30";
        values[DockerRunnerBudgets.CleanupSetting] = "20";
        var services = new ServiceCollection();
        LocalSandboxHostConfiguration.Configure(services, Host("Development"), values.GetValueOrDefault, true);
        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredKeyedService<DockerInspectionWorkerOptions>(LocalSandboxHostConfiguration.Key);
        Assert.Equal(TimeSpan.FromSeconds(30), options.CreateTimeout);
        Assert.Equal(TimeSpan.FromSeconds(20), options.CleanupTimeout);
        Assert.Equal(DockerWorkerProfile.Compiled, options.Profile);
        var runner = provider.GetRequiredKeyedService<DockerInspectionWorkerRunner>(LocalSandboxHostConfiguration.Key);
        Assert.True(runner.Available);
        var lifetime = Assert.Single(provider.GetServices<IHostedService>().OfType<InspectionWorkerHostLifetime>());
        await lifetime.StopAsync(CancellationToken.None);
        Assert.False(runner.Available);

        // Unset budgets keep the runner defaults; an invalid budget refuses the configuration.
        var defaults = new ServiceCollection();
        LocalSandboxHostConfiguration.Configure(defaults, Host("Development"), DevelopmentSettings().GetValueOrDefault, true);
        await using var defaultProvider = defaults.BuildServiceProvider();
        var defaultOptions = defaultProvider.GetRequiredKeyedService<DockerInspectionWorkerOptions>(LocalSandboxHostConfiguration.Key);
        Assert.Null(defaultOptions.CreateTimeout);
        Assert.Equal(DockerInspectionWorkerOptions.DefaultCleanupTimeout, defaultOptions.CleanupTimeout);
        values[DockerRunnerBudgets.CleanupSetting] = "121";
        Assert.Throws<InvalidOperationException>(() =>
            LocalSandboxHostConfiguration.Configure(new ServiceCollection(), Host("Development"), values.GetValueOrDefault, true));
    }

    private static Microsoft.AspNetCore.Hosting.IWebHostEnvironment Host(string environment) =>
        WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment }).Environment;

    private static Dictionary<string, string?> DevelopmentSettings()
    {
        string Login(string role) => $"Host=127.0.0.1;Port=1;Database=braid;Username={role};Password=unused";
        return new(StringComparer.Ordinal)
        {
            ["BRAID_BUILDS_ENABLED"] = "true",
            ["BRAID_FERRULE_WORKER_IMAGE"] = "sha256:" + new string('a', 64),
            ["BRAID_PRIVATE_STORAGE_ROOT"] = "/braid-unused-storage",
            ["BRAID_RELEASE_OPERATIONS_ENABLED"] = "true",
            [LocalSandboxHostConfiguration.Enabled] = "true",
            [LocalSandboxHostConfiguration.History] = Path.Combine(Path.GetTempPath(), "unused-history"),
            [LocalSandboxHostConfiguration.RuntimeImage] = "sha256:" + new string('b', 64),
            ["BRAID_LOCAL_SANDBOX_QUALIFICATION_WRITER_CONNECTION_STRING"] = Login("braid_qualification_writer"),
            ["BRAID_LOCAL_SANDBOX_BUILD_READER_CONNECTION_STRING"] = Login("braid_build_reader"),
            ["BRAID_LOCAL_SANDBOX_INVOCATION_WRITER_CONNECTION_STRING"] = Login("braid_invocation_writer"),
            ["BRAID_LOCAL_SANDBOX_INVOCATION_GRANT_WRITER_CONNECTION_STRING"] = Login("braid_invocation_grant_writer")
        };
    }

    private static SandboxMessage Message(Guid id, Guid target, DateTimeOffset sentAt, string status) =>
        new(id, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), target, sentAt, 3, status, status, false, Guid.NewGuid(), Guid.NewGuid(), "inline", null,
            [new(null, null, "{\"in\":1}"u8.ToArray())], [new("count", PreviewParameterKind.Integer, "2")], [new(null, null, "{\"out\":1}"u8.ToArray())]);

    /// <summary>The send path over the machine invocation fixture: its ledger, release pointer and scripted compiled runtime.</summary>
    private sealed class SendFixture : IReleaseOperationsStore
    {
        internal SendFixture(string root, string? format = null)
        {
            Invocation = new(targetId: (intent, runtime) => SandboxIdentity.TargetId(
                new(intent.Revision.OrganizationId, intent.Revision.ProjectId, intent.Revision.MappingId), intent.Environment, intent.Profile.Id,
                intent.Profile.Version, runtime), format: format)
            { PublisherOnlyReleaseReads = true };
            Invocation.Activate().GetAwaiter().GetResult();
            Builds = new(this);
            Sender = Invocation.Actor;
            Mapping = Invocation.Target.Reference.Mapping;
            RuntimeImage = Invocation.Data.Runtime;
            Target = new(TargetId, Invocation.Target.Reference.Environment.EnvironmentId, Invocation.Target.Reference.Environment.Version, true,
                Invocation.Release!.ReleaseId, 1, RuntimeImage);
            Service = new(this, new ReleaseOperationsService(this, Invocation, new NoQualifications()), Invocation, new NoGrants(), Builds,
                _ => throw new NotSupportedException(), Invocation.Service, Invocation, new FileSandboxHistory(root), [], [], Invocation.Data.Runtime, TimeProvider.System);
        }

        internal MachineInvocationServiceTests.Fixture Invocation { get; }
        internal LocalSandboxService Service { get; }
        internal MappingScope Mapping { get; }
        internal ActorContext Actor => Sender;
        /// <summary>The sending actor; the release's publisher unless a test selects another authorized publisher.</summary>
        internal ActorContext Sender { get; set; }
        internal ApprovalReader Builds { get; }
        internal Guid TargetId => Invocation.Target.Reference.TargetId;
        internal SandboxTarget Target { get; set; }
        internal string RuntimeImage { get; set; }

        internal SandboxSample Sample() => new(Invocation.Data.Case.Replay.Primary, Invocation.Data.Case.Replay.NamedInputs,
            Invocation.Data.Case.Replay.Parameters, "case");
        internal Task<SandboxMessage?> SendAsync(Guid id) => Service.SendAsync(Actor, Mapping, TargetId, id, Sample());

        public Task<JsonElement?> ReadAsync(ActorContext actor, MappingScope mapping, Guid? target, ReleaseOperationsQuery query, CancellationToken token = default)
        {
            var profile = Invocation.Target.Profile;
            var rows = query.Section == "targets" && mapping == Mapping && (target is null || target == TargetId)
                ? new[]
                {
                    new
                    {
                        target_id = Target.TargetId, environment_id = Target.EnvironmentId, environment_version = Target.EnvironmentVersion.ToString(),
                        enabled = Target.Enabled, active_release_id = Target.ActiveReleaseId, version = Target.Version.ToString(),
                        runtime_image = RuntimeImage, profile = new { id = profile.Id, version = profile.Version },
                        active = Target.ActiveReleaseId is { } release && Invocation.Release is { } intent && intent.ReleaseId == release
                            ? new
                            {
                                releaseId = release, actorId = intent.Actor.Value, qualificationId = intent.Qualification.Id,
                                buildId = intent.Qualification.Artifact.Intent.BuildId, approvalId = intent.Qualification.Artifact.Intent.ApprovalId,
                                approval = new { approvalId = intent.Qualification.Artifact.Intent.ApprovalId, revoked = false }
                            }
                            : null
                    }
                }
                : [];
            return Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(rows));
        }
        public Task<ReleaseTransitionOutcome> OperateAsync(ActorContext actor, ReleaseTargetReference target, ReleaseOperation operation, ReleaseIntent? intent,
            CancellationToken token = default) => throw new NotSupportedException();
        public Task<ReleaseTransitionOutcome?> RecoverAsync(ActorContext actor, ReleaseTargetReference target, ReleaseOperation operation,
            CancellationToken token = default) => throw new NotSupportedException();
    }

    /// <summary>Linux-only host settings: canonical Unix storage roots and key directories.</summary>
    private sealed class LinuxFactAttribute : FactAttribute
    {
        public LinuxFactAttribute()
        {
            if (!OperatingSystem.IsLinux()) Skip = "Requires the Linux host path rules.";
        }
    }

    private sealed class NoQualifications : IBuildQualificationStore
    {
        public Task<BuildQualification?> ReadAsync(ActorContext actor, MappingScope mapping, Guid qualificationId, CancellationToken token = default) =>
            throw new NotSupportedException();
        public Task<BuildQualificationWriteOutcome> SaveAsync(ActorContext actor, BuildQualification qualification, CancellationToken token = default) =>
            throw new NotSupportedException();
    }

    private sealed class NoGrants : IInvocationGrantStore
    {
        public Task<InvocationGrantOutcome> GrantAsync(ActorContext administrator, InvocationGrant grant, CancellationToken token = default) => throw new NotSupportedException();
        public Task<InvocationGrantOutcome> RevokeAsync(ActorContext administrator, ReleaseTargetReference target, Guid grantId, CancellationToken token = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<InvocationGrantSnapshot>> ReadAsync(ActorContext administrator, ReleaseTargetReference target, CancellationToken token = default) =>
            throw new NotSupportedException();
    }

    /// <summary>Approval metadata as the store returns it: only to the approving publisher itself.</summary>
    private sealed class ApprovalReader(SendFixture fixture) : IBuildMetadataReader
    {
        internal int Reads;
        internal bool Unreadable;
        internal List<PrincipalId> Readers { get; } = [];
        public Task<RetainedBuildApproval?> ReadApprovalAsync(ActorContext actor, MappingScope mapping, Guid approvalId, CancellationToken token = default)
        {
            Reads++;
            Readers.Add(actor.PrincipalId);
            var intent = fixture.Invocation.Data.Build.Intent;
            return Task.FromResult(!Unreadable && actor.PrincipalId == fixture.Invocation.Data.Actor && mapping == fixture.Mapping && approvalId == intent.ApprovalId
                ? new RetainedBuildApproval(actor.PrincipalId, intent, false, true, DateTimeOffset.UtcNow) : null);
        }
        public Task<RetainedBuildEnvironment?> ReadEnvironmentAsync(ActorContext actor, BuildEnvironmentReference environment, CancellationToken token = default) =>
            throw new NotSupportedException();
        public Task<BuildEnvironmentPage?> ListEnvironmentsAsync(ActorContext actor, ProjectScope project, int maximum, BuildEnvironmentCursor? after = null,
            CancellationToken token = default) => throw new NotSupportedException();
        public Task<BuildApprovalPage?> ListApprovalsAsync(ActorContext actor, MappingScope mapping, int maximum, Guid? afterApprovalId = null,
            CancellationToken token = default) => throw new NotSupportedException();
        public Task<BuildRunPage?> ListRunsAsync(ActorContext actor, MappingScope mapping, int maximum, Guid? afterBuildId = null, CancellationToken token = default) =>
            throw new NotSupportedException();
    }
}
