using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Weft.BrowserQualification;
using static Weft.Tests.ChatOrchestrationFixture;

namespace Weft.Tests;

public sealed class OwnedWorkspaceChatBrowserFactAttribute : FactAttribute
{
    public OwnedWorkspaceChatBrowserFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("WEFT_WORKSPACE_CHAT_BROWSER") != "true")
            Skip = "Opt in with WEFT_WORKSPACE_CHAT_BROWSER=true and an explicit owned browser admission record.";
    }
}

/// <summary>Actual Development browser boundary; no API interception, Session claim, native worker or live model.</summary>
[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class ChatWorkspaceBrowserTests(PostgresFixture fixture)
{
    [OwnedWorkspaceChatBrowserFact]
    public async Task Admitted_browser_uses_actual_workspace_http_tools_persistence_and_current_authority()
    {
        var root = Environment.GetEnvironmentVariable("WEFT_AUTH_BROWSER_ADMISSION_ROOT") ?? throw new QualificationFailure();
        var record = Environment.GetEnvironmentVariable("WEFT_AUTH_BROWSER_ADMISSION_FILE") ?? throw new QualificationFailure();
        var admission = PrivateQualificationFiles.Input<QualificationAdmission>(root, record);
        Assert.True(admission.Version == 1 && admission.Approved && admission.Root == root);
        Assert.True(Path.IsPathFullyQualified(admission.SourceRoot) && new DirectoryInfo(admission.SourceRoot).LinkTarget is null);
        Check(admission.Node, admission.NodeSha256); Check(admission.Npm, admission.NpmSha256);
        Check(admission.Chromium, admission.ChromiumSha256); Check(admission.ServerAssembly, admission.ServerSha256);
        var web = Path.Combine(Path.GetDirectoryName(admission.ServerAssembly)!, "wwwroot");
        Assert.Contains("index.html", admission.Assets.Keys);
        foreach (var asset in admission.Assets)
        {
            var path = Path.GetFullPath(Path.Combine(web, asset.Key));
            Assert.StartsWith(web + Path.DirectorySeparatorChar, path, StringComparison.Ordinal);
            Check(path, asset.Value);
        }
        await using var reports = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
        var reader = new ChatHttpSyntheticReportTests.SyntheticReports(reports);
        var other = Guid.Parse("00000002-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        await reports.Case.ExecuteAsync("""
            INSERT INTO weft.project_memberships(organization_id,project_id,principal_id,role,active)
            VALUES ($1,$2,$3,'viewer',true)
            """, reports.Revision.OrganizationId, other, reports.Actor.PrincipalId.Value);
        await using var provider = new AiHttpFixture(async (exchange, ct) =>
        {
            if (exchange.Ordinal == 3)
                await reports.Case.ExecuteAsync("""
                    UPDATE weft.project_memberships SET active=false,version=version+1
                    WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3
                    """, reports.Revision.OrganizationId, other, reports.Actor.PrincipalId.Value);
            var answer = exchange.Ordinal % 2 == 1
                ? Call(0, "project-" + exchange.Ordinal, "{\"projectId\":\"" + other.ToString("D") + "\"}", "get_project") +
                    Call(1, "guide-" + exchange.Ordinal, "{\"sectionId\":\"chat\"}", "get_user_guide_section") + Finish("tool_calls") + Done
                : Text("Workspace sources checked.") + Finish("stop") + Done;
            await exchange.RespondAsync("text/event-stream", AiHttpFixture.Utf8(answer), ct);
        });
        await using var api = await ChatHttpTestSupport.StartAsync(reports.Case, reports.Actor, reports.Revision,
            ChatHttpTestSupport.Options(provider), reader, reports.Reader, workspace: true, browserWebRoot: web);
        using var child = new Process
        {
            StartInfo = new ProcessStartInfo(admission.Npm)
            {
                WorkingDirectory = admission.SourceRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        child.StartInfo.Environment.Clear();
        foreach (var name in new[] { "PATH", "LANG", "LC_ALL", "DISPLAY", "XAUTHORITY" })
            if (Environment.GetEnvironmentVariable(name) is { } value) child.StartInfo.Environment[name] = value;
        child.StartInfo.Environment["HOME"] = root;
        child.StartInfo.Environment["TMPDIR"] = root;
        child.StartInfo.Environment["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "false";
        child.StartInfo.Environment["WEFT_CHROMIUM_EXECUTABLE"] = admission.Chromium;
        child.StartInfo.Environment["WEFT_WORKSPACE_CHAT_ORIGIN"] = api.Origin.GetLeftPart(UriPartial.Authority);
        child.StartInfo.Environment["WEFT_WORKSPACE_CHAT_OUTPUT"] = Path.Combine(root, "chat-browser-output");
        child.StartInfo.Environment["WEFT_WORKSPACE_CHAT_REVISION_PATH"] = CatalogRevisionReportHttpTestSupport.RevisionPath(reports.Revision)["/api/v1".Length..].Insert(0, "/catalog");
        foreach (var argument in new[] { "--workspace", "@weft/web", "run", "test:e2e", "--", "--config", "playwright.chat-real.config.ts" })
            child.StartInfo.ArgumentList.Add(argument);
        Assert.True(child.Start());
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(4)); }
        catch
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync();
            throw;
        }
        await Task.WhenAll(stdout, stderr);
        // Only test-owned synthetic content can appear in this output; API request bodies and tokens are never logged.
        Assert.True(child.ExitCode == 0, "Owned chat browser failed: " + stdout.Result + stderr.Result);
        Assert.Equal(4, provider.RequestCount);
        Assert.True(provider.Requests[1].ContainsUtf8("Hidden project"));
        Assert.True(provider.Requests[3].ContainsUtf8("notFound"));
        foreach (var request in provider.Requests)
            foreach (var tool in new[] { "get_preview_input", "get_preview_output", "get_regression_case_data", "get_regression_run_output" })
                Assert.False(request.ContainsUtf8("\"name\":\"" + tool + "\""));
    }

    private static void Check(string path, string hash)
    {
        Assert.True(Path.IsPathFullyQualified(path) && new FileInfo(path).LinkTarget is null);
        Assert.Equal(hash, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
    }
}
