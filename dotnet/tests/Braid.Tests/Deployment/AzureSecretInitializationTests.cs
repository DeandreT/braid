using System.Diagnostics;
using System.Runtime.Versioning;

namespace Braid.Tests;

/// <summary>
/// deploy/azure/initialize-secrets.sh against a recording fake Azure CLI: no Azure login, network or write. Only a
/// Key Vault "not found" answer may lead to a write; any other lookup failure stops the script before it.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class AzureSecretInitializationTests : IDisposable
{
    // Every name the fake does not list as absent or failing exists; passwords read back as generated values.
    private const string FakeAz = """
        #!/bin/bash
        printf '%s\n' "$*" >>"$FAKE_AZ_LOG"
        name= file= query=
        arguments=("$@")
        for ((i = 0; i < ${#arguments[@]}; i++)); do
          case ${arguments[i]} in
            --name) name=${arguments[i + 1]} ;;
            --file) file=${arguments[i + 1]} ;;
            --query) query=${arguments[i + 1]} ;;
          esac
        done
        case "$2 $3" in
          'secret show' | 'certificate show')
            if [[ " $FAKE_AZ_FAIL " == *" $name "* ]]; then echo 'ERROR: Read timed out.' >&2; exit 1; fi
            if [[ " $FAKE_AZ_ABSENT " == *" $name "* ]]; then
              kind=${2^}
              echo "ERROR: (${kind}NotFound) A $2 with (name/id) $name was not found in this key vault." >&2
              exit 3
            fi
            [[ $query == value ]] && printf '%064d\n' 0
            exit 0 ;;
          'certificate download') printf 'fake-der' >"$file"; exit 0 ;;
          'secret set' | 'certificate create') exit 0 ;;
        esac
        echo 'Unexpected fake Azure CLI call.' >&2
        exit 2
        """;

    private readonly string root = Directory.CreateTempSubdirectory("braid-initialize-secrets-").FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    [AzureInitializerTheory]
    [InlineData("pg-migrator-password")]
    [InlineData("braid-ferrule-worker-client")]
    [InlineData("braid-chat-profiles")]
    [InlineData("braid-model-provider-credential")]
    public async Task An_unconfirmed_absence_stops_before_any_write_for_that_item(string item)
    {
        var (exit, error, calls) = await RunAsync(fail: item, absent: "");
        Assert.Equal(1, exit);
        Assert.Contains("Could not establish whether", error, StringComparison.Ordinal);
        // The failed lookup is the last call: nothing after it was attempted.
        Assert.Matches($"^keyvault (secret|certificate) show .*--name {item} ", calls[^1]);
        Assert.DoesNotContain(calls, call => call.Contains(" create ", StringComparison.Ordinal) && call.Contains("--name " + item + " ", StringComparison.Ordinal));
        Assert.DoesNotContain(calls, call => call.StartsWith("keyvault secret set", StringComparison.Ordinal) &&
            call.Contains("--name " + item + " ", StringComparison.Ordinal));
    }

    [AzureInitializerFact]
    public async Task Only_reported_absent_items_are_created_and_existing_ones_are_kept()
    {
        var (exit, _, calls) = await RunAsync(fail: "", absent: "braid-chat-profiles braid-ferrule-worker-client");
        Assert.Equal(0, exit);
        Assert.Equal(["braid-ferrule-worker-client"], Names(calls, "keyvault certificate create"));
        var written = Names(calls, "keyvault secret set");
        Assert.Contains("braid-chat-profiles", written);
        Assert.All(written, name => Assert.True(name == "braid-chat-profiles" || name.EndsWith("-connection", StringComparison.Ordinal),
            "An existing secret was replaced."));
        Assert.True(File.Exists(Path.Combine(root, "output", "braid-ferrule-worker-client.sha256")));
    }

    private static string[] Names(IEnumerable<string> calls, string prefix) => calls
        .Where(call => call.StartsWith(prefix, StringComparison.Ordinal))
        .Select(call => call.Split(' ')[Array.IndexOf(call.Split(' '), "--name") + 1]).ToArray();

    private async Task<(int Exit, string Error, string[] Calls)> RunAsync(string fail, string absent)
    {
        var bin = Directory.CreateDirectory(Path.Combine(root, "bin")).FullName;
        var output = Directory.CreateDirectory(Path.Combine(root, "output")).FullName;
        var az = Path.Combine(bin, "az");
        await File.WriteAllTextAsync(az, FakeAz.ReplaceLineEndings("\n") + "\n");
        File.SetUnixFileMode(az, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var log = Path.Combine(root, "calls.log");
        await File.WriteAllTextAsync(log, "");
        var start = new ProcessStartInfo("bash")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine(Repository(), "deploy", "azure", "initialize-secrets.sh"));
        start.ArgumentList.Add("braid-kv-fake");
        start.ArgumentList.Add("braid-pg.example.invalid");
        start.ArgumentList.Add(output);
        start.Environment["PATH"] = bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        start.Environment["FAKE_AZ_LOG"] = log;
        start.Environment["FAKE_AZ_FAIL"] = fail;
        start.Environment["FAKE_AZ_ABSENT"] = absent;
        using var process = Process.Start(start)!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var standardOutput = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var standardError = process.StandardError.ReadToEndAsync(deadline.Token);
        try { await process.WaitForExitAsync(deadline.Token); }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
        await standardOutput;
        return (process.ExitCode, await standardError, (await File.ReadAllLinesAsync(log)).Select(line => line + " ").ToArray());
    }

    private static string Repository()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "deploy", "azure", "initialize-secrets.sh")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository source is required for the initializer checks.");
    }
}

public sealed class AzureInitializerFactAttribute : FactAttribute
{
    public AzureInitializerFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "The Azure initializer is a bash script qualified on Linux.";
    }
}

public sealed class AzureInitializerTheoryAttribute : TheoryAttribute
{
    public AzureInitializerTheoryAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "The Azure initializer is a bash script qualified on Linux.";
    }
}
