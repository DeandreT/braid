using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Weft.Application;
using Weft.Deployment;
using Weft.Jobs;
using Weft.Worker;

namespace Weft.Tests;

/// <summary>Worker database credentials through protected private files; nothing connects.</summary>
[SupportedOSPlatform("linux")]
public sealed class WorkerCredentialSettingsTests : IDisposable
{
    private const string Connection = "Host=127.0.0.1;Database=weft;Username=weft_build_writer;Password=synthetic-never-connected";
    private readonly string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "weft-worker-credentials-" + Guid.NewGuid().ToString("N"),
        "private")).FullName;

    public WorkerCredentialSettingsTests() => File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(root)!, recursive: true);

    [Fact]
    public void Every_worker_credential_is_read_from_its_protected_file_without_the_terminal_line_feed()
    {
        var values = new Dictionary<string, string>();
        foreach (var name in WorkerCredentialSettings.Connections)
            values[name + WorkerCredentialSettings.FileSuffix] = Private(name.ToLowerInvariant(), Connection + ";Application Name=" + name + "\n");
        var read = WorkerCredentialSettings.Resolve(values.GetValueOrDefault);
        foreach (var name in WorkerCredentialSettings.Connections)
            Assert.Equal(Connection + ";Application Name=" + name, read(name));
    }

    [Fact]
    public void A_plain_credential_is_still_admitted_and_other_settings_pass_through_unchanged()
    {
        var path = Private("unrelated", "never read");
        var values = new Dictionary<string, string>
        {
            ["WEFT_BUILD_WRITER_CONNECTION_STRING"] = Connection,
            ["WEFT_FERRULE_WORKER_IMAGE"] = "sha256:" + new string('a', 64),
            ["WEFT_FERRULE_WORKER_IMAGE_FILE"] = path
        };
        var read = WorkerCredentialSettings.Resolve(values.GetValueOrDefault);
        Assert.Equal(Connection, read("WEFT_BUILD_WRITER_CONNECTION_STRING"));
        Assert.Equal(values["WEFT_FERRULE_WORKER_IMAGE"], read("WEFT_FERRULE_WORKER_IMAGE"));
        Assert.Equal(path, read("WEFT_FERRULE_WORKER_IMAGE_FILE"));
        Assert.Null(read("WEFT_BUILD_DISPATCHER_CONNECTION_STRING"));
    }

    [Fact]
    public void A_credential_with_both_deliveries_is_refused_without_reading_the_file()
    {
        var values = new Dictionary<string, string>
        {
            ["WEFT_PREVIEW_WRITER_CONNECTION_STRING"] = Connection,
            ["WEFT_PREVIEW_WRITER_CONNECTION_STRING_FILE"] = Path.Combine(root, "absent")
        };
        var error = Assert.Throws<InvalidOperationException>(() => WorkerCredentialSettings.Resolve(values.GetValueOrDefault)("WEFT_PREVIEW_WRITER_CONNECTION_STRING"));
        Assert.Equal("The worker requires WEFT_PREVIEW_WRITER_CONNECTION_STRING or WEFT_PREVIEW_WRITER_CONNECTION_STRING_FILE, not both.", error.Message);
        Assert.DoesNotContain("synthetic", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("group-readable")]
    [InlineData("empty")]
    [InlineData("relative")]
    [InlineData("missing")]
    [InlineData("blank-setting")]
    public void An_unprotected_or_unusable_credential_file_refuses_without_echoing_its_contents(string kind)
    {
        var path = kind switch
        {
            "group-readable" => Private("group-readable", Connection),
            "empty" => Private("empty", ""),
            "relative" => "private/credential",
            "missing" => Path.Combine(root, "missing"),
            _ => ""
        };
        if (kind == "group-readable") File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        var values = new Dictionary<string, string> { ["WEFT_REGRESSION_RUN_WRITER_CONNECTION_STRING_FILE"] = path };
        var error = Assert.Throws<InvalidOperationException>(() => WorkerCredentialSettings.Resolve(values.GetValueOrDefault)("WEFT_REGRESSION_RUN_WRITER_CONNECTION_STRING"));
        Assert.Equal("The private host file is invalid.", error.Message);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void Disabled_components_read_no_credential_file()
    {
        var reads = new List<string>();
        var read = WorkerCredentialSettings.Resolve(name =>
        {
            reads.Add(name);
            return name.EndsWith(WorkerCredentialSettings.FileSuffix, StringComparison.Ordinal) ? Path.Combine(root, "absent") : null;
        });
        var services = new ServiceCollection();
        services.AddWeftBuildWorkerWithRunner(read, () => throw new InvalidOperationException("Unexpected runner creation."));
        services.AddWeftRegressionWorker(read, () => throw new InvalidOperationException("Unexpected runtime read."));
        // The XML child switch is read even when its parent is disabled, so an
        // enabled child cannot be silently ignored. It is a plain flag, never a credential.
        Assert.Equal(["WEFT_BUILD_WORKER_ENABLED", "WEFT_REGRESSION_WORKER_ENABLED", XmlPreviewAdmission.RegressionEnabledSetting], reads);
        Assert.DoesNotContain(reads, name => name.EndsWith(WorkerCredentialSettings.FileSuffix, StringComparison.Ordinal));
        Assert.Empty(services);
    }

    [Fact]
    public void An_enabled_xml_regression_child_refuses_a_disabled_worker_before_any_credential_file()
    {
        var reads = new List<string>();
        var read = WorkerCredentialSettings.Resolve(name =>
        {
            reads.Add(name);
            if (name == XmlPreviewAdmission.RegressionEnabledSetting) return "true";
            return name.EndsWith(WorkerCredentialSettings.FileSuffix, StringComparison.Ordinal) ? Path.Combine(root, "absent") : null;
        });
        var services = new ServiceCollection();
        var error = Assert.Throws<ArgumentException>(() =>
            services.AddWeftRegressionWorker(read, () => throw new InvalidOperationException("Unexpected runtime read.")));
        Assert.Contains("requires its parent workflow", error.Message, StringComparison.Ordinal);
        Assert.Equal(["WEFT_REGRESSION_WORKER_ENABLED", XmlPreviewAdmission.RegressionEnabledSetting], reads);
        Assert.Empty(services);
    }

    [BuildWorkerLinuxFact]
    public void The_build_component_registers_from_file_delivered_credentials()
    {
        var values = new Dictionary<string, string> { ["WEFT_BUILD_WORKER_ENABLED"] = "true" };
        foreach (var name in WorkerCredentialSettings.Connections.Where(name => name.StartsWith("WEFT_BUILD_", StringComparison.Ordinal)))
            values[name + WorkerCredentialSettings.FileSuffix] = Private(name.ToLowerInvariant(), Connection + "\n");
        var services = new ServiceCollection();
        services.AddWeftBuildWorkerWithRunner(WorkerCredentialSettings.Resolve(values.GetValueOrDefault),
            () => throw new InvalidOperationException("Registration must not create the runner."));
        Assert.Single(services, item => item.ServiceType == typeof(IBuildRunStore));
        Assert.Contains(services, item => item.ServiceType == typeof(IHostedService) && item.ImplementationType == typeof(BuildWorkerService));
    }

    [Theory]
    [InlineData("both", "not both")]
    [InlineData("unprotected", "The private host file is invalid.")]
    public async Task The_worker_process_reads_its_credentials_through_the_file_settings(string kind, string refusal)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "PreviewWorker", "Weft.Worker.dll");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(dll)!
        };
        start.ArgumentList.Add(dll);
        start.Environment.Clear();
        foreach (var name in new[] { "DOTNET_ROOT", "PATH", "HOME" })
            if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        start.Environment["WEFT_FERRULE_WORKER_IMAGE"] = "sha256:" + new string('a', 64);
        start.Environment["WEFT_PRIVATE_STORAGE_ROOT"] = Path.Combine(root, "storage");
        var file = Private("preview-writer", Connection);
        if (kind == "both") start.Environment["WEFT_PREVIEW_WRITER_CONNECTION_STRING"] = Connection;
        else File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
        start.Environment["WEFT_PREVIEW_WRITER_CONNECTION_STRING_FILE"] = file;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90)); }
        finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
        Assert.NotEqual(0, process.ExitCode);
        var output = await stdout + await stderr;
        Assert.Contains(refusal, output, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-never-connected", output, StringComparison.Ordinal);
    }

    private string Private(string name, string text)
    {
        var path = Path.Combine(root, name);
        File.WriteAllText(path, text);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return path;
    }
}
