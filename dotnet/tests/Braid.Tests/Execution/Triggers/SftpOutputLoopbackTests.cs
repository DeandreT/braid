using System.Diagnostics;
using System.Text.Json;
using Braid.Deployment.Triggers;
using Braid.Triggers.Sftp;

namespace Braid.Tests;

public sealed class SftpOutputFactAttribute : FactAttribute
{
    public SftpOutputFactAttribute()
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BRAID_SFTP_TEST_PYTHON")))
            Skip = "Requires local Python with Paramiko.";
    }
}

public sealed class SftpOutputLoopbackTests
{
    [SftpOutputFact]
    public async Task Pinned_loopback_publish_is_create_only_and_leaves_no_temporary_files()
    {
        var directory = Directory.CreateTempSubdirectory("braid-sftp-out-");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("BRAID_SFTP_TEST_PYTHON")!) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sftp_output_loopback.py")); start.ArgumentList.Add(directory.FullName);
        using var child = Process.Start(start)!;
        try
        {
            using var data = JsonDocument.Parse(await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)) ?? throw new IOException());
            var key = Path.Combine(directory.FullName, "key.pem");
            SftpOutputDestination Destination(string pin) => new(new(new("out", 6), "127.0.0.1", data.RootElement[0].GetInt32(), "fixture", pin, null, key, "/out", "", SftpPublishContract.NoReplaceRename), (file, _) => File.ReadAllText(file));
            var output = Destination(data.RootElement[1].GetString()!);
            var delivery = TriggerQueueProcessingTests.Delivery(version: 6);
            Assert.Equal(DeliveryObservationKind.ProvenKnownNotAccepted, await Destination(new string('A', 43)).SendAsync(delivery, default));
            Assert.Equal(DeliveryObservationKind.PermanentFailure, await output.SendAsync(TriggerQueueProcessingTests.Delivery(version: 5), default));
            var folder = Path.Combine(directory.FullName, "out");
            Assert.Empty(Directory.EnumerateFileSystemEntries(folder));
            Assert.Equal(DeliveryObservationKind.Accepted, await output.SendAsync(delivery, default));
            var final = Assert.Single(Directory.EnumerateFiles(folder));
            var written = File.GetLastWriteTimeUtc(final);
            Assert.Equal(DeliveryObservationKind.Accepted, await output.SendAsync(delivery, default));
            var conflict = TriggerQueueProcessingTests.Delivery(version: 6, content: "other", deliveryId: delivery.DeliveryId);
            Assert.Equal(DeliveryObservationKind.PermanentFailure, await output.SendAsync(conflict, default));
            Assert.Equal(written, File.GetLastWriteTimeUtc(final)); Assert.Equal(delivery.CopyContent(), File.ReadAllBytes(final));
            Assert.Equal(final, Assert.Single(Directory.EnumerateFileSystemEntries(folder)));
        }
        finally
        {
            child.StandardInput.Close(); if (!child.WaitForExit(5000)) { child.Kill(); await child.WaitForExitAsync(); }
            directory.Delete(recursive: true);
        }
    }
}
