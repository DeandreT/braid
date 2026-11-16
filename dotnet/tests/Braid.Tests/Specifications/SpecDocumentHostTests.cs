using Braid.Application;
using Braid.Domain;
using Braid.MappingBackends.Workers;
using Braid.Server;

namespace Braid.Tests;

public sealed class SpecDocumentHostTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Pre_registration_filesystem_probe_cleanup_fault_survives_start_and_repeated_stop(bool runnerProbe, bool denied)
    {
        var root = Path.Combine(Path.GetTempPath(), "document-probe-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string? created = null;
        void Delete(string path)
        {
            created = path;
            Assert.True(Directory.Exists(path));
            File.WriteAllText(Path.Combine(path, "synthetic-blocker"), "fixture");
            if (denied) throw new UnauthorizedAccessException("Synthetic deletion denial");
            Directory.Delete(path); // Actual filesystem deletion fails because the directory is nonempty.
        }
        var host = new SpecDocumentHostResources(Read(), (_, _) =>
        {
            // These are the exact production probe routines, before any runner/pair has been registered.
            if (runnerProbe) ProcessWorkerRunner.ProbeTemporaryRoot(root, Delete);
            else SpecDocumentInstallation.ProbeWorkRoot(root, Delete);
            throw new InvalidOperationException("Deletion failure must prevent reaching worker creation");
        });
        try
        {
            var start = await Assert.ThrowsAsync<AggregateException>(() => host.StartAsync(default));
            var fault = Assert.IsType<WorkerDirectoryProbe.CleanupFailure>(Assert.Single(start.InnerExceptions));
            if (denied) Assert.IsType<UnauthorizedAccessException>(fault.InnerException);
            else Assert.IsType<IOException>(fault.InnerException);
            Assert.NotNull(created);
            Assert.True(Directory.Exists(created));
            var stop = await Assert.ThrowsAsync<AggregateException>(() => host.StopAsync(default));
            Assert.Same(fault, Assert.Single(stop.InnerExceptions));
            var dispose = await Assert.ThrowsAsync<AggregateException>(() => host.DisposeAsync().AsTask());
            Assert.Same(fault, Assert.Single(dispose.InnerExceptions));
            Assert.Equal(3, (await host.Bind(_ => Task.FromResult(true)).FormatsAsync(default)).Count);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Filesystem_probe_success_removes_its_directory(bool runnerProbe)
    {
        var root = Path.Combine(Path.GetTempPath(), "document-probe-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            if (runnerProbe) ProcessWorkerRunner.ProbeTemporaryRoot(root);
            else SpecDocumentInstallation.ProbeWorkRoot(root);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    internal static Dictionary<string, string> Settings()
    {
        var build = Sha256Digest.FromBytes("synthetic approved PDF build"u8);
        return new()
        {
            ["BRAID_SPEC_DOCUMENT_WORKERS"] = "optional",
            ["BRAID_SPEC_DOCUMENT_OFFICE_ASSEMBLY"] = "/opt/documents/office/Braid.DocumentWorker.dll",
            ["BRAID_SPEC_DOCUMENT_OFFICE_BUILD_SHA256"] = new string('a', 64),
            ["BRAID_SPEC_DOCUMENT_OFFICE_RUNTIME_SHA256"] = new string('b', 64),
            ["BRAID_SPEC_DOCUMENT_OFFICE_PROFILE_SHA256"] = new SpecOfficeExtractionLimits().Digest.Value,
            ["BRAID_SPEC_DOCUMENT_OFFICE_LAUNCHER"] = "/opt/documents/launcher/compile-sandbox",
            ["BRAID_SPEC_DOCUMENT_DOTNET_ROOT"] = "/opt/dotnet",
            ["BRAID_SPEC_DOCUMENT_OFFICE_WORK_ROOT"] = "/work/office",
            ["BRAID_SPEC_DOCUMENT_PDF_EXECUTABLE"] = "/opt/documents/pdf/braid-spec-pdf-worker",
            ["BRAID_SPEC_DOCUMENT_PDF_WORK_ROOT"] = "/work/pdf",
            ["BRAID_SPEC_DOCUMENT_PDF_BUILD_SHA256"] = build.Value,
            ["BRAID_SPEC_DOCUMENT_PDF_PROFILE_SHA256"] = new SpecPdfExtractionProfile(build).Digest.Value
        };
    }
    internal static SpecDocumentHostSettings Read() { var values = Settings(); return SpecDocumentHostSettings.Read(k => values.GetValueOrDefault(k))!; }

    [Fact]
    public async Task Disabled_startup_reads_no_worker_settings_and_opens_nothing()
    {
        Assert.Null(SpecDocumentHostSettings.Read(k => k == "BRAID_SPEC_DOCUMENT_WORKERS" ? null : throw new InvalidOperationException()));
        var calls = 0;
        await using var host = new SpecDocumentHostResources(null, (_, _) => { calls++; throw new InvalidOperationException(); });
        await host.StartAsync(default);
        Assert.Equal(0, calls); Assert.Equal("disabled", host.Status);
        Assert.Equal(3, (await host.Bind(_ => throw new InvalidOperationException()).FormatsAsync(default)).Count);
    }

    [Theory]
    [InlineData("relative/file")]
    [InlineData("/a/../b")]
    [InlineData("/a//b")]
    [InlineData("C:\\tool.exe")]
    [InlineData("/a\nb")]
    public void Paths_are_canonical_absolute_unix_installation_facts(string path) =>
        Assert.ThrowsAny<Exception>(() => SpecDocumentHostSettings.UnixPath(path));

    [Theory]
    [InlineData("OFFICE_PROFILE_SHA256")]
    [InlineData("PDF_PROFILE_SHA256")]
    [InlineData("PDF_BUILD_SHA256")]
    public void Independently_configured_profile_and_executable_pins_cannot_disagree(string field)
    {
        var values = Settings(); values["BRAID_SPEC_DOCUMENT_" + field] = new string('c', 64);
        Assert.ThrowsAny<Exception>(() => SpecDocumentHostSettings.Read(k => values.GetValueOrDefault(k)));
    }

    [Fact]
    public async Task Portable_platform_cannot_admit_workers_from_well_formed_configuration()
    {
        if (OperatingSystem.IsLinux()) return;
        await using var host = new SpecDocumentHostResources(Read());
        await host.StartAsync(default);
        Assert.Equal("unavailable", host.Status);
        Assert.Equal(3, (await host.Bind(_ => throw new InvalidOperationException()).FormatsAsync(default)).Count);
    }

    [Theory]
    [InlineData("BRAID_SPEC_DOCUMENT_WORKERS", "true")]
    [InlineData("BRAID_SPEC_DOCUMENT_OFFICE_BUILD_SHA256", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("BRAID_SPEC_DOCUMENT_OFFICE_RUNTIME_SHA256", "sha256:abcd")]
    public void Malformed_modes_and_pins_fail_configuration(string key, string value)
    {
        var values = Settings(); values[key] = value;
        Assert.ThrowsAny<Exception>(() => SpecDocumentHostSettings.Read(k => values.GetValueOrDefault(k)));
    }

    [Fact]
    public async Task Missing_native_installation_support_leaves_optional_workers_unavailable()
    {
        await using var host = new SpecDocumentHostResources(Read(), (_, _) => throw new EntryPointNotFoundException());
        await host.StartAsync(default);
        Assert.Equal("unavailable", host.Status);
        Assert.Equal(3, (await host.Bind(_ => throw new InvalidOperationException()).FormatsAsync(default)).Count);
    }

    [Fact]
    public async Task Owned_worker_pair_requires_fresh_schema_and_liveness_and_is_disposed_once()
    {
        var disposed = 0; var opened = 0; var live = true; var schema = true; var schemaChecks = 0;
        var fixture = new WorkerFixture();
        await using var host = new SpecDocumentHostResources(Read(), (_, _) =>
        { opened++; return Task.FromResult<SpecDocumentHostResources.Workers?>(new(fixture, fixture, () => live, () => { disposed++; return ValueTask.CompletedTask; })); });
        await host.StartAsync(default); await host.StartAsync(default);
        var admission = host.Bind(_ => { schemaChecks++; return Task.FromResult(schema); });
        Assert.Equal(6, (await admission.FormatsAsync(default)).Count);
        schema = false; Assert.Equal(3, (await admission.FormatsAsync(default)).Count);
        schema = true; live = false; Assert.Equal(3, (await admission.FormatsAsync(default)).Count);
        Assert.Equal(3, schemaChecks); Assert.Equal(1, opened);
        await host.StopAsync(default); await host.StopAsync(default);
        live = true; Assert.Equal(3, (await admission.FormatsAsync(default)).Count);
        Assert.Equal(1, disposed);
    }

    [Fact]
    public async Task Partial_startup_failure_and_cancellation_do_not_leak_workers()
    {
        var disposed = 0; var fixture = new WorkerFixture();
        await using var host = new SpecDocumentHostResources(Read(), (_, _) => Task.FromResult<SpecDocumentHostResources.Workers?>(
            new(fixture, fixture, () => false, () => { disposed++; return ValueTask.CompletedTask; })));
        await host.StartAsync(default); Assert.Equal(1, disposed); Assert.Equal("unavailable", host.Status);
        using var cancel = new CancellationTokenSource();
        await using var canceled = new SpecDocumentHostResources(Read(), (_, _) =>
        {
            cancel.Cancel();
            return Task.FromResult<SpecDocumentHostResources.Workers?>(new(fixture, fixture, () => true, () => { disposed++; return ValueTask.CompletedTask; }));
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled.StartAsync(cancel.Token));
        Assert.Equal(2, disposed);
    }

    [Fact]
    public async Task Shutdown_cancels_a_pending_start_before_any_admission()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = new SpecDocumentHostResources(Read(), async (_, token) =>
        { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return null; });
        var start = host.StartAsync(default); await entered.Task;
        await host.StopAsync(default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.Equal("stopped", host.Status);
    }

    [Fact]
    public void Writable_overmounts_and_ambiguous_mounts_cannot_claim_immutable_installation()
    {
        static string Line(int id, string path, string mode) => $"{id} 0 0:{id} / {path} {mode},nosuid - ext4 /dev/synthetic {mode}";
        var mounts = SpecDocumentInstallation.ParseMounts([Line(1, "/", "ro"), Line(2, "/work", "rw")]);
        SpecDocumentInstallation.RequireReadOnlyTree("/opt/worker", mounts);
        Assert.ThrowsAny<InvalidOperationException>(() => SpecDocumentInstallation.RequireReadOnlyTree("/work/worker", mounts));
        mounts = SpecDocumentInstallation.ParseMounts([Line(1, "/", "ro"), Line(2, "/opt/worker/dependency", "rw")]);
        Assert.ThrowsAny<InvalidOperationException>(() => SpecDocumentInstallation.RequireReadOnlyTree("/opt/worker", mounts));
        Assert.ThrowsAny<InvalidOperationException>(() => SpecDocumentInstallation.ParseMounts([Line(1, "/", "ro"), Line(2, "/", "rw")]));
        mounts = SpecDocumentInstallation.ParseMounts([Line(1, "/", "ro"), "2 0 0:1 / /writable-alias rw - ext4 /dev/synthetic rw"]);
        Assert.ThrowsAny<InvalidOperationException>(() => SpecDocumentInstallation.RequireReadOnlyTree("/opt/worker", mounts));
    }

    [Fact]
    public void Disjoint_backing_roots_on_same_device_can_alias_executable_inodes()
    {
        var mounts = SpecDocumentInstallation.ParseMounts([
            "1 0 0:1 / / ro - ext4 /dev/root ro",
            "2 1 0:2 /release /install ro - ext4 /dev/install ro",
            "3 1 0:2 /scratch /work/pdf rw - ext4 /dev/install rw"]);
        Assert.ThrowsAny<InvalidOperationException>(() => SpecDocumentInstallation.RequireReadOnlyTree("/install/worker", mounts));
    }

    [Fact]
    public async Task Partial_factory_owner_is_retained_after_unconfirmed_cleanup()
    {
        var attempts = 0;
        var fixture = new WorkerFixture();
        var owner = new SpecDocumentHostResources.Workers(fixture, fixture, () => false, () =>
        { attempts++; throw new InvalidOperationException("Unconfirmed cleanup"); });
        var host = new SpecDocumentHostResources(Read(), (_, own, _) =>
        {
            own(owner); // Just as the real factory registers the runner before its probe.
            throw new IOException("Probe failed before the worker pair was returned");
        });
        await Assert.ThrowsAsync<AggregateException>(() => host.StartAsync(default));
        await Assert.ThrowsAsync<AggregateException>(() => host.StopAsync(default));
        await Assert.ThrowsAsync<AggregateException>(() => host.DisposeAsync().AsTask());
        Assert.Equal(3, attempts);
        Assert.Equal(3, (await host.Bind(_ => Task.FromResult(true)).FormatsAsync(default)).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Returned_worker_cleanup_failure_is_preserved_across_shutdown_calls(bool admitted)
    {
        var attempts = 0; var fixture = new WorkerFixture();
        var host = new SpecDocumentHostResources(Read(), (_, _) => Task.FromResult<SpecDocumentHostResources.Workers?>(
            new(fixture, fixture, () => admitted, () => { attempts++; throw new InvalidOperationException("Unconfirmed cleanup"); })));
        if (admitted) await host.StartAsync(default);
        else await Assert.ThrowsAsync<AggregateException>(() => host.StartAsync(default));
        var admission = host.Bind(_ => Task.FromResult(true));
        await Assert.ThrowsAsync<AggregateException>(() => host.StopAsync(default));
        await Assert.ThrowsAsync<AggregateException>(() => host.StopAsync(default));
        await Assert.ThrowsAsync<AggregateException>(() => host.DisposeAsync().AsTask());
        Assert.Equal(admitted ? 3 : 4, attempts);
        Assert.Equal(3, (await admission.FormatsAsync(default)).Count);
    }

    [Fact]
    public async Task Unknown_invalid_operation_from_startup_is_not_optional_refusal()
    {
        await using var host = new SpecDocumentHostResources(Read(), (_, _) => throw new InvalidOperationException("Unconfirmed operation"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(default));
    }

    internal sealed class WorkerFixture : ISpecOfficeExtractionWorker, ISpecPdfExtractionWorker
    {
        public Task<SpecOfficeExtractionResult> ExtractAsync(SpecCapturedSource source, SpecOfficeExtractionLimits limits, CancellationToken token) => throw new NotSupportedException();
        public Task<SpecPdfExtractionResult> ExtractAsync(SpecCapturedSource source, CancellationToken token) => throw new NotSupportedException();
    }
}
