using System.Runtime.InteropServices;
using Braid.Server;

namespace Braid.Tests;

public sealed class SpecDocumentHostLinuxFactAttribute : FactAttribute
{
    public SpecDocumentHostLinuxFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("BRAID_TEST_DOCUMENT_HOST_LINUX") != "1")
            Skip = "Requires explicit opt-in and an independently pinned Linux document installation.";
    }
}

public sealed class SpecDocumentHostLinuxTests
{
    public sealed class LinuxHardlinkFactAttribute : FactAttribute
    {
        public LinuxHardlinkFactAttribute()
        {
            if (!OperatingSystem.IsLinux()) Skip = "Requires actual Linux hardlink semantics.";
        }
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link([MarshalAs(UnmanagedType.LPUTF8Str)] string source, [MarshalAs(UnmanagedType.LPUTF8Str)] string target);

    [LinuxHardlinkFact]
    public void Real_hardlink_in_disjoint_directory_mutates_original_and_mount_policy_refuses_it()
    {
        var root = Path.Combine(Path.GetTempPath(), "document-hardlink-" + Guid.NewGuid().ToString("N"));
        try
        {
            var release = Directory.CreateDirectory(Path.Combine(root, "release")).FullName;
            var scratch = Directory.CreateDirectory(Path.Combine(root, "scratch")).FullName;
            var executable = Path.Combine(release, "worker"); var alias = Path.Combine(scratch, "alias");
            File.WriteAllText(executable, "approved");
            Assert.Equal(0, Link(executable, alias));
            File.WriteAllText(alias, "mutated");
            Assert.Equal("mutated", File.ReadAllText(executable));
            // Actual inode aliasing above; synthetic bind mounts below require no mount privileges.
            var mounts = SpecDocumentInstallation.ParseMounts([
                "1 0 0:1 / / ro - ext4 /dev/root ro",
                "2 1 0:2 /release /install ro - ext4 /dev/install ro",
                "3 1 0:2 /scratch /work/pdf rw - ext4 /dev/install rw"]);
            Assert.ThrowsAny<InvalidOperationException>(() => SpecDocumentInstallation.RequireReadOnlyTree("/install/worker", mounts));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [SpecDocumentHostLinuxFact]
    public async Task Installed_workers_pass_real_host_startup_and_revoke_admission_on_shutdown()
    {
        Assert.True(OperatingSystem.IsLinux(), "The opted-in qualification entrypoint must run on Linux.");
        var settings = SpecDocumentHostSettings.Read(Environment.GetEnvironmentVariable);
        Assert.NotNull(settings);
        await using var host = new SpecDocumentHostResources(settings);
        await host.StartAsync(default);
        Assert.Equal("admitted", host.Status);
        // The database is deliberately independent of this installation/procfs startup gate.
        // Actual actor-bound schema admission is covered by SpecDocumentHostPostgresTests.
        var admission = host.Bind(_ => Task.FromResult(true));
        Assert.Equal(6, (await admission.FormatsAsync(default)).Count);
        await host.StopAsync(default);
        Assert.Equal(3, (await admission.FormatsAsync(default)).Count);
        Assert.Empty(Directory.EnumerateFileSystemEntries(settings.OfficeWorkRoot));
        Assert.Empty(Directory.EnumerateFileSystemEntries(settings.PdfWorkRoot));
    }
}
