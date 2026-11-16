using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using Braid.Application;

namespace Braid.Tests;

[SupportedOSPlatform("linux")]
public sealed class PrivateStorageInitializationToolTests
{
    [Fact]
    public async Task OwnerToolInitializesOnePrivateRootAndRefusesEvenAnAlreadyValidExistingRoot()
    {
        using var root = new PrivateStorageTestRoot();
        var result = await RunAsync("initialize", root.RootPath, root.ApplicationRoot);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("Initialized", result.Output.Trim());
        Assert.Equal("", result.Error);
        Assert.Equal(PrivateStorageTestRoot.DirectoryMode, File.GetUnixFileMode(root.RootPath));
        Assert.Equal(PrivateStorageTestRoot.FinalMode, File.GetUnixFileMode(root.MarkerPath));
        var marker = File.ReadAllBytes(root.MarkerPath);
        var bytes = new byte[] { 0, 255, 128, 13, 10 };
        var reference = PrivateStorageData.Reference(bytes);
        using (var store = root.Open())
        using (var source = new MemoryStream(bytes))
            Assert.Equal(PrivateObjectPublishResult.Created, await store.PublishCreateOnlyAsync(reference.Id.Scope, reference, source));
        var repeat = await RunAsync("initialize", root.RootPath, root.ApplicationRoot);
        Assert.Equal(1, repeat.ExitCode);
        Assert.Equal("UnsafeStorage", repeat.Error.Trim());
        Assert.Equal("", repeat.Output);
        Assert.Equal(marker, File.ReadAllBytes(root.MarkerPath));
        using var reopened = root.Open();
        Assert.Equal(bytes, await reopened.ReadVerifiedAsync(reference.Id.Scope, reference));
        AssertNoPath(result, root);
        AssertNoPath(repeat, root);
    }

    [Fact]
    public async Task ExistingUnmarkedRootIsNeverAdoptedChmoddedOrSwept()
    {
        using var root = new PrivateStorageTestRoot();
        Directory.CreateDirectory(root.RootPath);
        var mode = PrivateStorageTestRoot.DirectoryMode | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        File.SetUnixFileMode(root.RootPath, mode);
        var sentinel = Path.Combine(root.RootPath, "owned-sentinel");
        var bytes = new byte[] { 255, 0, 7, 13, 10 };
        File.WriteAllBytes(sentinel, bytes);
        var result = await RunAsync("initialize", root.RootPath, root.ApplicationRoot);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("UnsafeStorage", result.Error.Trim());
        Assert.Equal(mode, File.GetUnixFileMode(root.RootPath));
        Assert.Equal(bytes, File.ReadAllBytes(sentinel));
        Assert.False(File.Exists(root.MarkerPath));
        Assert.Equal(new[] { sentinel }, Directory.GetFiles(root.RootPath));
        AssertNoPath(result, root);
    }

    [Fact]
    public async Task InvalidUsageAndProhibitedApplicationOrRepositoryRootsFailWithoutCreatingStorage()
    {
        using var root = new PrivateStorageTestRoot();
        foreach (var args in new[]
        {
            Array.Empty<string>(), new[] { "open", root.RootPath, root.ApplicationRoot },
            new[] { "initialize", "relative/root", root.ApplicationRoot },
            new[] { "initialize", Path.Combine(root.ApplicationRoot, "private"), root.ApplicationRoot },
            new[] { "initialize", root.RootPath, root.ApplicationRoot, root.BasePath }
        })
        {
            var result = await RunAsync(args);
            Assert.Equal(2, result.ExitCode);
            Assert.Equal("", result.Output);
            Assert.True(result.Error.Trim() is "InvalidConfiguration" or "Usage: Braid.StorageTool initialize ROOT APPLICATION_ROOT [REPOSITORY_ROOT]");
            Assert.False(Directory.Exists(root.RootPath));
            Assert.False(Directory.Exists(Path.Combine(root.ApplicationRoot, "private")));
            AssertNoPath(result, root);
        }
    }

    private static void AssertNoPath(ToolResult result, PrivateStorageTestRoot root)
    {
        Assert.DoesNotContain(root.BasePath, result.Output + result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(root.RootPath, result.Output + result.Error, StringComparison.Ordinal);
    }

    private static async Task<ToolResult> RunAsync(params string[] arguments)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "StorageTool", "Braid.StorageTool.dll");
        Assert.True(File.Exists(dll), "The owner storage tool and dependencies must be copied into the test output.");
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrWhiteSpace(host))
            host = Environment.ProcessPath is { } current && Path.GetFileName(current) == "dotnet" ? current : "dotnet";
        var start = new ProcessStartInfo(host) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add(dll);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The owned storage initialization tool did not start.");
        var output = ReadBoundedAsync(process.StandardOutput, deadline.Token);
        var error = ReadBoundedAsync(process.StandardError, deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            return new(process.ExitCode, await output, await error);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            }
            try { await Task.WhenAll(output, error); } catch (OperationCanceledException) { }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var buffer = new char[256];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) != 0)
        {
            if (text.Length + count > 1024) throw new InvalidOperationException("The owned initialization tool exceeded bounded categorical output.");
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }

    private sealed record ToolResult(int ExitCode, string Output, string Error);
}
