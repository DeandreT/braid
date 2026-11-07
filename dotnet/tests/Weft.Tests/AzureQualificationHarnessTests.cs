using System.Diagnostics;
using System.Runtime.Versioning;

namespace Weft.Tests;

/// <summary>
/// deploy/azure/qualify.sh through deploy/azure/tests/qualify-test.sh: each scenario runs the live qualification harness
/// from a committed copy against recording fakes of the Azure CLI, curl and Docker, with no Azure login, network or change.
/// The scenarios cover every guard refusal, the exact command sequence of a passing run, a failure partway through, the
/// receipt, teardown scoping and that no credential or token reaches the output, the receipt or any command's arguments.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class AzureQualificationHarnessTests
{
    [AzureQualificationTheory]
    [InlineData("refuses_missing_inputs")]
    [InlineData("refuses_without_confirmation")]
    [InlineData("refuses_credential_arguments")]
    [InlineData("refuses_wrong_subscription")]
    [InlineData("refuses_owner_account")]
    [InlineData("refuses_existing_group")]
    [InlineData("refuses_dirty_tree")]
    [InlineData("declined_confirmation")]
    [InlineData("successful_run")]
    [InlineData("failure_partway")]
    [InlineData("failed_check_recorded")]
    [InlineData("teardown_at_end_after_failure")]
    [InlineData("teardown_scoping")]
    public async Task Scenario_holds(string scenario)
    {
        var start = new ProcessStartInfo("bash")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine(Repository(), "deploy", "azure", "tests", "qualify-test.sh"));
        start.ArgumentList.Add(scenario);
        using var process = Process.Start(start)!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var standardOutput = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var standardError = process.StandardError.ReadToEndAsync(deadline.Token);
        try { await process.WaitForExitAsync(deadline.Token); }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
        Assert.True(process.ExitCode == 0, await standardOutput + await standardError);
    }

    private static string Repository()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "deploy", "azure", "qualify.sh")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository source is required for the harness checks.");
    }
}

public sealed class AzureQualificationTheoryAttribute : TheoryAttribute
{
    public AzureQualificationTheoryAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "The Azure qualification harness is a bash script qualified on Linux.";
    }
}
