using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Xunit.Abstractions;

namespace Weft.Tests;

public sealed class OpenApiIncrementalFactAttribute : FactAttribute
{
    public OpenApiIncrementalFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("WEFT_OPENAPI_INCREMENTAL_TESTS") != "1")
            Skip = "Set WEFT_OPENAPI_INCREMENTAL_TESTS=1 to build all three real hosts in an isolated source copy.";
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class OpenApiIncrementalCollection
{
    public const string Name = "Isolated OpenAPI incremental builds";
}

/// <summary>Real SDK exports, without modifying the checkout, its build outputs or its committed specifications.</summary>
[Collection(OpenApiIncrementalCollection.Name)]
public sealed class OpenApiIncrementalBuildTests(ITestOutputHelper output)
{
    private static readonly (string Host, string Document, string Generator)[] Hosts =
    [
        ("Weft.Server", "weft-v1.json", "dotnet/src/Weft.Api/ApiConfiguration.cs"),
        ("Weft.MachineEndpoint", "weft-endpoint-v1.json", "dotnet/apps/Weft.MachineEndpoint/MappingEndpointOpenApi.cs"),
        ("Weft.FerruleService", "ferrule-service-v1.json", "dotnet/apps/Weft.FerruleService/FerruleServiceOpenApi.cs")
    ];

    [OpenApiIncrementalFact]
    public async Task Referenced_implementation_changes_invalidate_all_three_exports_without_recompiling_the_hosts()
    {
        var root = RepositoryRoot();
        var fixture = Path.Combine(Path.GetTempPath(), "weft-openapi-incremental-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            CopySources(root, fixture);
            var targets = Path.Combine(fixture, "dotnet", "Directory.Build.targets");
            var invalidation = await File.ReadAllTextAsync(targets);
            // Negative control: prove the pinned SDK's original incremental behavior is stale.
            var sdkOnly = XDocument.Parse(invalidation);
            sdkOnly.Root!.Elements("Target").Single(target =>
                (string?)target.Attribute("Name") == "WeftInvalidateOpenApiForChangedReferences").Remove();
            await File.WriteAllTextAsync(targets, sdkOnly.ToString());
            var targetsTimestamp = File.GetLastWriteTimeUtc(targets);
            var probe = Path.Combine(fixture, "dotnet", "src", "Weft.Application", "OpenApiIncrementalProbe.cs");
            const string before = "openapi-incremental-before";
            const string after = "openapi-incremental-after";
            await File.WriteAllTextAsync(probe, Probe(before));
            foreach (var (_, _, generator) in Hosts)
            {
                var source = Path.Combine(fixture, generator);
                var text = await File.ReadAllTextAsync(source);
                const string anchor = "document.Servers = [];";
                Assert.Equal(1, text.Split(anchor, StringSplitOptions.None).Length - 1);
                // Only fixture hosts get this observation. A method-body-only library edit changes the
                // real document but not the reference assembly, host source, or compiled host bytes.
                await File.WriteAllTextAsync(source, text.Replace(anchor,
                    "document.Info.Description += Weft.Application.OpenApiIncrementalProbe.Description; " + anchor,
                    StringComparison.Ordinal));
            }

            foreach (var (host, _, _) in Hosts)
                await BuildAsync(fixture, host, restore: true);
            var baseline = new Dictionary<string, string>();
            var hostBytes = new Dictionary<string, string>();
            var hostTimes = new Dictionary<string, DateTime>();
            foreach (var (host, document, _) in Hosts)
            {
                baseline[document] = await DocumentAsync(fixture, document, before);
                hostBytes[host] = Hash(Assembly(fixture, host));
                hostTimes[host] = File.GetLastWriteTimeUtc(Assembly(fixture, host));
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
            await File.WriteAllTextAsync(probe, Probe(after));
            foreach (var (host, document, _) in Hosts)
            {
                await BuildAsync(fixture, host);
                Assert.Equal(baseline[document], await DocumentAsync(fixture, document, before));
                Assert.Equal(hostBytes[host], Hash(Assembly(fixture, host)));
                Assert.Equal(hostTimes[host], File.GetLastWriteTimeUtc(Assembly(fixture, host)));
            }
            output.WriteLine("Negative control: all three SDK exports remained stale after a library-body-only edit.");

            await File.WriteAllTextAsync(targets, invalidation);
            // Enabling the hook must not itself trigger recompilation through MSBuildAllProjects.
            // Only the already-built library implementation is newer than the SDK's export cache.
            File.SetLastWriteTimeUtc(targets, targetsTimestamp);
            var updated = new Dictionary<string, string>();
            foreach (var (host, document, _) in Hosts)
            {
                await BuildAsync(fixture, host);
                updated[document] = await DocumentAsync(fixture, document, after);
                Assert.Equal(hostBytes[host], Hash(Assembly(fixture, host)));
                Assert.Equal(hostTimes[host], File.GetLastWriteTimeUtc(Assembly(fixture, host)));
                var old = Path.Combine(fixture, document + ".before");
                await File.WriteAllTextAsync(old, baseline[document]);
                // The same diff engine used by CI must refuse the stale contract snapshot.
                await RunAsync(fixture, "git", ["diff", "--no-index", "--exit-code", "--", old,
                    Path.Combine(fixture, "contracts", document)], expectedExit: 1);
            }

            foreach (var (host, document, _) in Hosts)
            {
                var path = Path.Combine(fixture, "contracts", document);
                var timestamp = File.GetLastWriteTimeUtc(path);
                var log = await BuildAsync(fixture, host);
                Assert.DoesNotContain("Generating document named", log, StringComparison.Ordinal);
                Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
                Assert.Equal(updated[document], await File.ReadAllTextAsync(path));
            }
            output.WriteLine("Fixed exports changed without host recompilation; stale drift failed; unchanged builds did not export.");

            foreach (var (host, _, _) in Hosts)
                await RunAsync(fixture, Dotnet, ["clean", Project(host), "-c", "Release", "-m:2", "-nr:false", "-v:q"]);
            foreach (var (host, document, _) in Hosts)
            {
                await BuildAsync(fixture, host);
                Assert.True(JsonNode.DeepEquals(JsonNode.Parse(updated[document]),
                    JsonNode.Parse(await DocumentAsync(fixture, document, after))), host + " changed its contract after a clean build.");
            }
            output.WriteLine("Clean builds reproduced all three incrementally generated contracts.");
        }
        finally { Directory.Delete(fixture, recursive: true); }
    }

    private static string Probe(string value) =>
        "namespace Weft.Application; public static class OpenApiIncrementalProbe { public static string Description => \"" + value + "\"; }";

    private static string Dotnet => Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
    private static string Project(string host) => Path.Combine("dotnet", "apps", host, host + ".csproj");
    private static string Assembly(string root, string host)
    {
        var name = XDocument.Load(Path.Combine(root, Project(host))).Descendants("AssemblyName").SingleOrDefault()?.Value ?? host;
        return Path.Combine(root, "dotnet", "apps", host, "bin", "Release", "net10.0", name + ".dll");
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private async Task<string> BuildAsync(string root, string host, bool restore = false) => await RunAsync(root, Dotnet,
        ["build", Project(host), "-c", "Release", restore ? "-p:RestoreLockedMode=true" : "--no-restore", "-m:2", "-nr:false", "-v:m"]);

    private static async Task<string> DocumentAsync(string root, string name, string marker)
    {
        var text = await File.ReadAllTextAsync(Path.Combine(root, "contracts", name));
        Assert.Contains(marker, JsonNode.Parse(text)!["info"]!["description"]!.GetValue<string>(), StringComparison.Ordinal);
        return text;
    }

    private async Task<string> RunAsync(string root, string executable, string[] arguments, int expectedExit = 0)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["DOTNET_PROCESSOR_COUNT"] = "2";
        start.Environment["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "false";
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start the build regression child.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw;
        }
        var log = await stdout + await stderr;
        output.WriteLine(executable + " " + string.Join(' ', arguments) + Environment.NewLine + log);
        Assert.True(process.ExitCode == expectedExit, $"Expected exit {expectedExit}, got {process.ExitCode}: {log}");
        return log;
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "dotnet", "Weft.slnx"))) return directory.FullName;
        throw new InvalidOperationException("The repository sources are required for the incremental build regression.");
    }

    private static void CopySources(string source, string destination)
    {
        foreach (var name in new[] { "global.json", ".editorconfig" })
            File.Copy(Path.Combine(source, name), Path.Combine(destination, name));
        foreach (var name in new[] { "dotnet", "migrations", "contracts", "docs", "engines" })
            CopyDirectory(Path.Combine(source, name), Path.Combine(destination, name));
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var directory in Directory.EnumerateDirectories(source))
            if (Path.GetFileName(directory) is not ("bin" or "obj" or "target" or "node_modules" or ".git"))
                CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }
}
