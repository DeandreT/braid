using System.Text.Json;
using Braid.Application;
using Braid.Application.Regression;
using Braid.Deployment;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

// Standalone test process. No network listener, bearer credentials, native code or external mapping runtime.
internal static class HostOrderReplicaProbe
{
    internal sealed record Setup(Dictionary<string, string> Stores, ReleaseTargetReference Target, MachinePrincipal Principal, Guid Application, string Image);
    internal sealed record Order(Guid Target, Guid Id, bool Stall = false);
    internal sealed record Result(MachineInvocationStatus Status, Guid? Release, string? Digest, bool Replay);

    private static async Task<int> Main(string[] args)
    {
        if (args is not ["host-replica-probe"] || !Console.IsInputRedirected || !Console.IsOutputRedirected) return 2;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var token = deadline.Token;
        try
        {
            var setup = JsonSerializer.Deserialize<Setup>((await Console.In.ReadLineAsync(token))!)!;
            var db = setup.Stores;
            await using var ledger = new PostgresInvocationLedger(db["braid_invocation_writer"]);
            await using var releases = new PostgresLocalReleaseStore(db["braid_release_reader"], false);
            await using var runs = new PostgresBuildRunStore(db["braid_build_run_reader"], BuildRunDatabaseProfile.Reader);
            await using var builds = new PostgresBuildStore(db["braid_build_reader"], false);
            await using var access = new PostgresDatabase(db["braid_runtime"]);
            await using var reports = new PostgresRevisionReportDatabase(db["braid_report_reader"]);
            await using var evidence = new PostgresRegressionRunStore(db["braid_run_reader"], false);
            await using var cases = new PostgresRegressionCaseStore(db["braid_case_reader"], RegressionCaseDatabaseProfile.Reader);
            var admission = new BuildControlService(builds, new PostgresProjectReader(access), new PostgresRevisionReportMetadataReader(reports), new RegressionRunReadService(evidence, cases));
            var runtime = new SyntheticRuntime(setup.Image);
            var service = new MachineInvocationService(ledger, releases, actor => new(releases,
                new(runs, admission, builds, new PostgresBuildObjectStore(builds, new NoNative(), actor), runtime)), TimeProvider.System,
                new(1024 * 1024, 1024 * 1024, PreviewPolicy.MaximumArtifacts, PreviewPolicy.MaximumTextBytes));
            var request = new MachineInvocationRequest(new(null, DocumentPreviewTestData.Input("x12")), [], [], [new(null, "application/edi-x12")]);
            var caller = new MachineCaller(setup.Principal, setup.Application, DateTimeOffset.UtcNow.AddMinutes(5));
            Console.WriteLine("ready");
            while (await Console.In.ReadLineAsync(token) is { } line)
            {
                var order = JsonSerializer.Deserialize<Order>(line)!;
                runtime.Stall = order.Stall;
                var result = await service.InvokeAsync(new(setup.Target.Mapping, setup.Target.Environment, order.Target), caller, order.Id, request, token);
                Console.WriteLine(JsonSerializer.Serialize(new Result(result.Status, result.ReleaseId,
                    result.Response is null ? null : Sha256Digest.FromBytes(result.Response).Value, result.Replayed)));
            }
            return 0;
        }
        catch (Exception) { return 1; } // Do not log private fixture credentials or payloads.
    }

    private sealed class SyntheticRuntime(string image) : ICompiledBuildRuntime
    {
        public string RuntimeImage => image;
        internal bool Stall;
        public async Task<CompiledBuildResult> ExecuteAsync(BuildArtifactReference artifact, BuildBytes bytes, RegressionReplay replay, CancellationToken token)
        {
            if (Stall)
            {
                Console.WriteLine("dispatched"); // Pin and claim have committed before dispatch.
                await Task.Delay(Timeout.Infinite, token);
            }
            return new(CompiledBuildOutcome.Completed, RegressionOutputSet.Capture([RegressionOutput.Capture(RegressionOutputKey.Primary,
                replay.Primary.CopyBytes(), token, mediaType: "application/edi-x12")]));
        }
    }

    private sealed class NoNative : IPrivateObjectStore
    {
        public Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(RevisionScope scope, PrivateObjectReference reference, Stream bytes, CancellationToken token = default) => throw new NotSupportedException();
        public Task<byte[]> ReadVerifiedAsync(RevisionScope scope, PrivateObjectReference reference, CancellationToken token = default) => throw new NotSupportedException();
    }
}
