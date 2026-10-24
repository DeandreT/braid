using System.CommandLine;
using System.Security.Cryptography;
using System.Text;
using Weft.Cli.Http;
using Weft.Cli.Output;
using Weft.Contracts.Builds;
using static Weft.Cli.Commands.CommandSupport;

namespace Weft.Cli.Commands;

internal static class BuildCommands
{
    public static Command Builds(CliGlobals globals)
    {
        var project = Required("--project", "The project ID");
        var mapping = Required("--mapping", "The mapping ID");

        var capabilities = new Command("capabilities", "Show the build backend and what you may do with builds of a mapping").With(project, mapping);
        capabilities.Handle(globals, async invocation =>
        {
            var value = await invocation.Api.ReadAsync<BuildCapabilitiesV1>(
                await MappingRequestAsync(invocation, ApiOperations.GetBuildCapabilities, project, mapping), invocation.Cancellation);
            invocation.Output.Write(value, output => output.Fields(("Backend", value.Backend.BackendId), ("Engine", value.Backend.EngineRevision),
                ("Runtime", $"{value.Profile.Runtime} {value.Profile.RuntimeVersion}"), ("Profile", $"{value.Profile.Id} v{value.Profile.Version}"),
                ("Read own metadata", value.ReadOwnMetadata), ("Approve builds", value.ApproveBuilds), ("Submit builds", value.SubmitBuilds)));
        });

        var environmentCapabilities = new Command("environment-capabilities", "Show the build environment profile and whether you may add versions")
            .With(project);
        environmentCapabilities.Handle(globals, async invocation =>
        {
            var request = (await invocation.RequestAsync(ApiOperations.GetBuildEnvironmentCapabilities)).Path("projectId", invocation.Id(project));
            var value = await invocation.Api.ReadAsync<BuildEnvironmentCapabilitiesV1>(request, invocation.Cancellation);
            invocation.Output.Write(value, output => output.Fields(("Profile", $"{value.Profile.Id} v{value.Profile.Version}"),
                ("Runtime", $"{value.Profile.Runtime} {value.Profile.RuntimeVersion}"), ("Create versions", value.CanCreateVersions)));
        });

        return new Command("builds", "Compiled builds, their environments and approvals").With(capabilities, environmentCapabilities,
            Environments(globals, project), Approvals(globals, project, mapping), Runs(globals, project, mapping));
    }

    private static async Task<ApiRequest> MappingRequestAsync(CliInvocation invocation, ApiOperation operation, Option<string> project,
        Option<string> mapping) =>
        (await invocation.RequestAsync(operation)).Path("projectId", invocation.Id(project)).Path("mappingId", invocation.Id(mapping));

    private static Command Environments(CliGlobals globals, Option<string> project)
    {
        var limit = RegressionCommands.Limit();
        var all = new Option<bool>("--all") { Description = "Read every page" };
        var afterEnvironment = Optional("--after-environment", "Continue after this environment ID; requires --after-version");
        var afterVersion = new Option<long?>("--after-version") { Description = "Continue after this environment version; requires --after-environment" };
        var list = new Command("list", "List the project's build environment versions").With(project, limit, all, afterEnvironment, afterVersion);
        list.Handle(globals, async invocation =>
        {
            var environments = new List<BuildEnvironmentV1>();
            var afterId = invocation.OptionalId(afterEnvironment);
            var versionValue = invocation.Value(afterVersion);
            if ((afterId is null) != (versionValue is null))
                throw CliException.Usage("Give both --after-environment and --after-version to continue a build environment list.");
            (string Id, string Version)? cursor = afterId is not null && versionValue is { } number
                ? (afterId, CliInvocation.Positive(number, "--after-version")) : null;
            BuildEnvironmentPageV1 page;
            var traversal = new PageTraversal(cursor is { } initial ? initial.Id + "/" + initial.Version : null);
            do
            {
                var request = (await invocation.RequestAsync(ApiOperations.GetBuildEnvironments)).Path("projectId", invocation.Id(project))
                    .Query("limit", RegressionCommands.LimitValue(invocation, limit))
                    .Query("afterEnvironmentId", cursor?.Id).Query("afterVersion", cursor?.Version);
                page = await invocation.Api.ReadAsync<BuildEnvironmentPageV1>(request, invocation.Cancellation);
                environments.AddRange(page.Environments);
                cursor = page is { NextEnvironmentId: { } id, NextVersion: { } version } ? (id, version) : null;
            }
            while (traversal.Continue(invocation.Value(all), cursor is { } next ? next.Id + "/" + next.Version : null));
            var document = invocation.Value(all) ? new BuildEnvironmentPageV1(environments, null, null) : page;
            invocation.Output.Write(document, output =>
            {
                output.Table(["ENVIRONMENT", "VERSION", "PROFILE", "ACTIVE", "CREATED"], environments.Select(item =>
                    (IReadOnlyList<object?>)[item.Reference.EnvironmentId, item.Reference.Version, $"{item.Profile.Id} v{item.Profile.Version}",
                        item.Active, item.CreatedAtUtc]));
                if (document.NextEnvironmentId is not null)
                    output.Line($"More results: add --after-environment {document.NextEnvironmentId} --after-version {document.NextVersion} (or --all).");
            });
        });

        var environmentId = Argument("environmentId", "The environment ID");
        var version = new Argument<long>("version") { Description = "The environment version" };
        var get = new Command("get", "Show one environment version").With(project, environmentId, version);
        get.Handle(globals, async invocation =>
        {
            var request = (await invocation.RequestAsync(ApiOperations.GetBuildEnvironment)).Path("projectId", invocation.Id(project))
                .Path("environmentId", invocation.Id(environmentId)).Path("version", CliInvocation.Positive(invocation.Value(version), "version"));
            var environment = await invocation.Api.ReadAsync<BuildEnvironmentV1>(request, invocation.Cancellation);
            invocation.Output.Write(environment, output => WriteEnvironment(output, environment));
        });

        var profileId = Required("--profile-id", "The build profile ID");
        var profileVersion = new Option<long>("--profile-version") { Description = "The build profile version", Required = true };
        var create = new Command("create", "Create an immutable environment version").With(project, environmentId, version, profileId, profileVersion);
        create.Handle(globals, async invocation =>
        {
            var request = (await invocation.RequestAsync(ApiOperations.PutBuildEnvironment)).Path("projectId", invocation.Id(project))
                .Path("environmentId", invocation.Id(environmentId)).Path("version", CliInvocation.Positive(invocation.Value(version), "version"))
                .Body(CatalogCommands.JsonBody(new BuildEnvironmentPutV1(invocation.Value(profileId)!,
                    CliInvocation.Positive(invocation.Value(profileVersion), "--profile-version")), ApiOperations.PutBuildEnvironment));
            var receipt = await invocation.Api.ReadAsync<BuildEnvironmentReceiptV1>(request, invocation.Cancellation);
            invocation.Output.Write(receipt, output => output.Line(
                $"Environment {receipt.Environment.Reference.EnvironmentId} version {receipt.Environment.Reference.Version}: {receipt.Outcome}."));
        });

        return new Command("environments", "Versioned build environments of a project").With(list, get, create);
    }

    private static Command Approvals(CliGlobals globals, Option<string> project, Option<string> mapping)
    {
        var limit = RegressionCommands.Limit();
        var after = Optional("--after", "List approvals after this approval ID");
        var all = new Option<bool>("--all") { Description = "Read every page" };
        var list = new Command("list", "List the mapping's build approvals").With(project, mapping, limit, after, all);
        list.Handle(globals, async invocation =>
        {
            var approvals = new List<BuildApprovalV1>();
            var cursor = invocation.OptionalId(after);
            BuildApprovalPageV1 page;
            var traversal = new PageTraversal(cursor);
            do
            {
                var request = (await MappingRequestAsync(invocation, ApiOperations.GetBuildApprovals, project, mapping))
                    .Query("limit", RegressionCommands.LimitValue(invocation, limit)).Query("afterApprovalId", cursor);
                page = await invocation.Api.ReadAsync<BuildApprovalPageV1>(request, invocation.Cancellation);
                approvals.AddRange(page.Approvals);
                cursor = page.NextApprovalId;
            }
            while (traversal.Continue(invocation.Value(all), cursor));
            var document = invocation.Value(all) ? new BuildApprovalPageV1(approvals, null) : page;
            invocation.Output.Write(document, output =>
            {
                output.Table(["APPROVAL", "BUILD", "REVISION", "ENVIRONMENT", "REVOKED", "CREATED"], approvals.Select(item =>
                    (IReadOnlyList<object?>)[item.ApprovalId, item.BuildId, item.Revision.RevisionId,
                        $"{item.Environment.EnvironmentId} v{item.Environment.Version}", item.Revoked, item.CreatedAtUtc]));
                if (document.NextApprovalId is { } next)
                    output.Line($"More results: add --after {next} (or --all).");
            });
        });

        var approvalId = Argument("approvalId", "The approval ID");
        var get = new Command("get", "Show one build approval").With(project, mapping, approvalId);
        get.Handle(globals, async invocation =>
        {
            var request = (await MappingRequestAsync(invocation, ApiOperations.GetBuildApproval, project, mapping))
                .Path("approvalId", invocation.Id(approvalId));
            var approval = await invocation.Api.ReadAsync<BuildApprovalV1>(request, invocation.Cancellation);
            invocation.Output.Write(approval, output => WriteApproval(output, approval));
        });

        var build = Optional("--build", "The build ID the approval admits (default: a new build ID)");
        var revision = Required("--revision", "The revision ID to build");
        var environment = Required("--environment", "The build environment ID");
        var environmentVersion = new Option<long>("--environment-version") { Description = "The environment version", Required = true };
        var evidence = Required("--evidence", "The regression evidence ID (a passed regression run)");
        var newApproval = Optional("--approval-id", "The approval ID; reuse it to retry safely (default: new)");
        var create = new Command("create", "Approve one exact build of a revision")
            .With(project, mapping, build, revision, environment, environmentVersion, evidence, newApproval);
        create.Handle(globals, async invocation =>
        {
            var approvalIdValue = invocation.OptionalId(newApproval) ?? CatalogCommands.NewId();
            var body = new BuildApprovalPutV1(invocation.OptionalId(build) ?? BuildForApproval(approvalIdValue), invocation.Id(revision),
                invocation.Id(environment), CliInvocation.Positive(invocation.Value(environmentVersion), "--environment-version"),
                invocation.Id(evidence));
            var request = (await MappingRequestAsync(invocation, ApiOperations.PutBuildApproval, project, mapping))
                .Path("approvalId", approvalIdValue)
                .Body(CatalogCommands.JsonBody(body, ApiOperations.PutBuildApproval));
            var receipt = await invocation.Api.ReadAsync<BuildApprovalReceiptV1>(request, invocation.Cancellation);
            invocation.Output.Write(receipt, output =>
                output.Line($"Approval {receipt.Approval.ApprovalId} for build {receipt.Approval.BuildId}: {receipt.Outcome}."));
        });

        var revoke = new Command("revoke", "Revoke a build approval").With(project, mapping, approvalId);
        revoke.Handle(globals, async invocation =>
        {
            var request = (await MappingRequestAsync(invocation, ApiOperations.RevokeBuildApproval, project, mapping))
                .Path("approvalId", invocation.Id(approvalId));
            var approval = await invocation.Api.ReadAsync<BuildApprovalV1>(request, invocation.Cancellation);
            invocation.Output.Write(approval, output => output.Line($"Approval {approval.ApprovalId} revoked: {Text(approval.Revoked)}."));
        });

        return new Command("approvals", "Approvals of exact builds").With(list, get, create, revoke);
    }

    /// <summary>A domain-separated UUIDv8, so restarting an approval command reuses its original default build.</summary>
    internal static string BuildForApproval(string approvalId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes("weft:build-for-approval:v1\n" + approvalId));
        digest[6] = (byte)((digest[6] & 0x0f) | 0x80);
        digest[8] = (byte)((digest[8] & 0x3f) | 0x80);
        return new Guid(digest.AsSpan(0, 16), bigEndian: true).ToString("D");
    }

    private static Command Runs(CliGlobals globals, Option<string> project, Option<string> mapping)
    {
        var limit = RegressionCommands.Limit();
        var after = Optional("--after", "List builds after this build ID");
        var all = new Option<bool>("--all") { Description = "Read every page" };
        var list = new Command("list", "List the mapping's build runs").With(project, mapping, limit, after, all);
        list.Handle(globals, async invocation =>
        {
            var runs = new List<BuildRunV1>();
            var cursor = invocation.OptionalId(after);
            BuildRunPageV1 page;
            var traversal = new PageTraversal(cursor);
            do
            {
                var request = (await MappingRequestAsync(invocation, ApiOperations.GetBuildRuns, project, mapping))
                    .Query("limit", RegressionCommands.LimitValue(invocation, limit)).Query("afterBuildId", cursor);
                page = await invocation.Api.ReadAsync<BuildRunPageV1>(request, invocation.Cancellation);
                runs.AddRange(page.Runs);
                cursor = page.NextBuildId;
            }
            while (traversal.Continue(invocation.Value(all), cursor));
            var document = invocation.Value(all) ? new BuildRunPageV1(runs, null) : page;
            invocation.Output.Write(document, output =>
            {
                output.Table(["BUILD", "APPROVAL", "REVISION", "STATE", "OUTCOME", "CREATED"], runs.Select(run =>
                    (IReadOnlyList<object?>)[run.BuildId, run.ApprovalId, run.Revision.RevisionId, run.State, run.Outcome, run.CreatedAtUtc]));
                if (document.NextBuildId is { } next)
                    output.Line($"More results: add --after {next} (or --all).");
            });
        });

        var approval = Required("--approval", "The approval that admits the build");
        var build = Optional("--build", "The build ID (default: the build ID the approval names)");
        var wait = new Option<bool>("--wait") { Description = "Wait until the build finishes" };
        var timeout = Timeout("How long to wait for the build", TimeSpan.FromMinutes(30));
        var create = new Command("create", "Submit the approved build").With(project, mapping, approval, build, wait, timeout);
        create.Handle(globals, async invocation =>
        {
            var approvalId = invocation.Id(approval);
            var buildId = invocation.OptionalId(build);
            if (buildId is null)
            {
                var read = (await MappingRequestAsync(invocation, ApiOperations.GetBuildApproval, project, mapping)).Path("approvalId", approvalId);
                buildId = (await invocation.Api.ReadAsync<BuildApprovalV1>(read, invocation.Cancellation)).BuildId;
            }
            var request = (await MappingRequestAsync(invocation, ApiOperations.PutBuildRun, project, mapping)).Path("buildId", buildId)
                .Body(CatalogCommands.JsonBody(new BuildRunPutV1(approvalId), ApiOperations.PutBuildRun));
            var receipt = await invocation.Api.ReadAsync<BuildRunReceiptV1>(request, invocation.Cancellation);
            if (!invocation.Value(wait))
            {
                invocation.Output.Write(receipt, output => output.Line($"Build {receipt.Run.BuildId}: {receipt.Outcome} ({receipt.Run.State})."));
                return (int)CliExitCode.Success;
            }
            var run = await PollAsync(invocation, invocation.Value(timeout), "build", async () =>
            {
                var current = await ReadRunAsync(invocation, project, mapping, receipt.Run.BuildId);
                return current.State is "queued" or "running" ? null : current;
            });
            invocation.Output.Write(run, output => WriteRun(output, run));
            return run.State == "succeeded" ? (int)CliExitCode.Success : (int)CliExitCode.OperationFailed;
        });

        var buildId = Argument("buildId", "The build ID");
        var get = new Command("get", "Show a build run").With(project, mapping, buildId);
        get.Handle(globals, async invocation =>
        {
            var run = await ReadRunAsync(invocation, project, mapping, invocation.Id(buildId));
            invocation.Output.Write(run, output => WriteRun(output, run));
        });

        var cancel = new Command("cancel", "Ask a build to stop").With(project, mapping, buildId);
        cancel.Handle(globals, async invocation =>
        {
            var request = (await MappingRequestAsync(invocation, ApiOperations.CancelBuildRun, project, mapping)).Path("buildId", invocation.Id(buildId));
            var run = await invocation.Api.ReadAsync<BuildRunV1>(request, invocation.Cancellation);
            invocation.Output.Write(run, output => WriteRun(output, run));
        });

        return new Command("runs", "Build runs").With(list, create, get, cancel);
    }

    private static async Task<BuildRunV1> ReadRunAsync(CliInvocation invocation, Option<string> project, Option<string> mapping, string buildId)
    {
        var request = (await MappingRequestAsync(invocation, ApiOperations.GetBuildRun, project, mapping)).Path("buildId", buildId);
        return await invocation.Api.ReadAsync<BuildRunV1>(request, invocation.Cancellation);
    }

    private static string Text(bool value) => value ? "yes" : "no";

    private static void WriteEnvironment(CliOutput output, BuildEnvironmentV1 environment) => output.Fields(
        ("Environment", environment.Reference.EnvironmentId), ("Version", environment.Reference.Version),
        ("Profile", $"{environment.Profile.Id} v{environment.Profile.Version}"),
        ("Runtime", $"{environment.Profile.Runtime} {environment.Profile.RuntimeVersion}"),
        ("Artifact", environment.Profile.ArtifactFormat), ("Active", environment.Active), ("Created", environment.CreatedAtUtc));

    private static void WriteApproval(CliOutput output, BuildApprovalV1 approval) => output.Fields(("Approval", approval.ApprovalId),
        ("Build", approval.BuildId), ("Revision", approval.Revision.RevisionId), ("Native digest", approval.NativeDigest),
        ("Backend", approval.Backend.BackendId), ("Environment", $"{approval.Environment.EnvironmentId} v{approval.Environment.Version}"),
        ("Environment active", approval.EnvironmentActive), ("Evidence", approval.RegressionEvidenceId), ("Revoked", approval.Revoked),
        ("Created", approval.CreatedAtUtc));

    private static void WriteRun(CliOutput output, BuildRunV1 run) => output.Fields(("Build", run.BuildId), ("Approval", run.ApprovalId),
        ("Revision", run.Revision.RevisionId), ("State", run.State), ("Attempt", run.Attempt), ("Outcome", run.Outcome),
        ("Artifact SHA-256", run.Artifact?.Sha256), ("Artifact bytes", run.Artifact?.ByteCount), ("Created", run.CreatedAtUtc));
}
