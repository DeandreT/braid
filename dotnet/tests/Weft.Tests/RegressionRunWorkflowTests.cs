using System.Text;
using System.Text.Json.Nodes;
using Weft.Application;
using Weft.Application.Regression;
using Weft.Contracts.Operations;
using Weft.Domain;

namespace Weft.Tests;

public sealed class RegressionRunWorkflowTests
{
    private static readonly ActorContext Actor = new(new(Guid.NewGuid()));
    private static readonly Guid RunId = Guid.NewGuid();
    private static readonly RegressionCaseVersion Value = new(Guid.NewGuid(), 1, Actor.PrincipalId, "sample",
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), Sha256Digest.FromBytes([1]),
        new("ferrule", "engine", "build", "adapter", PreviewPolicy.Profile),
        new(PreviewTargetSelection.All, "2026-10-20T12:00:00Z", PreviewLimits.Default, new(null, [1]), [], []),
        RegressionComparisonService.ExactBytesPolicy, RegressionOutputSet.Capture([RegressionOutput.Capture(RegressionOutputKey.Primary, [255, 0, 128])]));
    private static readonly RegressionCaseReference Reference = new RegressionCaseEnvelopeCodec().Encode(Value).Reference;
    private readonly RegressionRunResultCodec codec = new();

    [Fact]
    public void Exact_binary_results_are_recompared_not_trusted_from_a_stored_pass_flag()
    {
        var bytes = codec.Encode(RunId, Reference, Success());
        var read = codec.Decode(RunId, Value, bytes);
        Assert.Equal(RegressionComparisonOutcome.Passed, read.Comparison!.Outcome);
        Assert.Equal(new byte[] { 255, 0, 128 }, Assert.Single(read.Outputs!.Outputs).CopyBytes());
        Assert.Equal(bytes, codec.Encode(RunId, Reference, read));
        Assert.DoesNotContain("passed", Encoding.UTF8.GetString(bytes), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("case")]
    [InlineData("outcome")]
    [InlineData("status")]
    [InlineData("padding")]
    [InlineData("unknown")]
    [InlineData("duplicate-output")]
    [InlineData("missing-primary")]
    public void Malformed_or_substituted_result_documents_are_refused(string mutation)
    {
        var json = JsonNode.Parse(codec.Encode(RunId, Reference, Success()))!.AsObject();
        switch (mutation)
        {
            case "run": json["runId"] = Guid.NewGuid().ToString("D"); break;
            case "case": json["caseDigest"] = Sha256Digest.FromBytes([2]).Value; break;
            case "outcome": json["outcome"] = 99; break;
            case "status": json["previewStatus"] = 99; break;
            case "padding": json["outputs"]![0]!["content"] = "_wCA="; break;
            case "unknown": json["passed"] = true; break;
            case "duplicate-output": json["outputs"]!.AsArray().Add(json["outputs"]![0]!.DeepClone()); break;
            case "missing-primary": json["outputs"]![0]!["name"] = "named"; break;
        }
        Assert.Throws<InvalidOperationException>(() => codec.Decode(RunId, Value, Encoding.UTF8.GetBytes(json.ToJsonString())));
    }

    [Fact]
    public void Duplicate_document_members_and_noncanonical_formatting_are_rejected()
    {
        var json = Encoding.UTF8.GetString(codec.Encode(RunId, Reference, Success()));
        Assert.Throws<InvalidOperationException>(() => codec.Decode(RunId, Value, Encoding.UTF8.GetBytes("{" + json[1..^1] + ",\"outcome\":0}")));
        Assert.Throws<InvalidOperationException>(() => codec.Decode(RunId, Value, Encoding.UTF8.GetBytes(" " + json)));
    }

    [Fact]
    public void Refusals_keep_no_payload_or_successful_comparison()
    {
        var result = RegressionCaseExecution.Refused(RegressionCaseExecutionOutcome.InfrastructureFailure);
        var restored = codec.Decode(RunId, Value, codec.Encode(RunId, Reference, result));
        Assert.Equal(result.Outcome, restored.Outcome);
        Assert.Null(restored.Outputs);
        Assert.Null(restored.Comparison);
    }

    [Fact]
    public async Task Redelivery_of_a_terminal_run_never_dispatches_the_case_again()
    {
        var store = new Runs { ClaimResult = new(RegressionRunClaimOutcome.Finished, null) };
        var executor = new CaseExecutor(_ => throw new InvalidOperationException("Must not execute."));
        Assert.Equal(RegressionRunDispatchOutcome.Finished, await new RegressionRunExecutor(store, executor).ExecuteAsync(Reference.Revision.OrganizationId, RunId, store.Worker));
        Assert.Equal(0, executor.Calls);
    }

    [Fact]
    public async Task A_run_is_released_after_an_infrastructure_failure_instead_of_recording_success()
    {
        var store = new Runs();
        var executor = new CaseExecutor(_ => Task.FromResult(RegressionCaseExecution.Refused(RegressionCaseExecutionOutcome.InfrastructureFailure)));
        Assert.Equal(RegressionRunDispatchOutcome.Deferred, await new RegressionRunExecutor(store, executor).ExecuteAsync(Reference.Revision.OrganizationId, RunId, store.Worker));
        Assert.Equal(1, store.Releases);
        Assert.Equal(0, store.Completions);
    }

    [Fact]
    public async Task A_requested_shutdown_cancels_execution_and_releases_the_owned_attempt()
    {
        var store = new Runs();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();
        var executor = new CaseExecutor(async token =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Success();
        });
        var pending = new RegressionRunExecutor(store, executor).ExecuteAsync(Reference.Revision.OrganizationId, RunId, store.Worker, stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, store.Releases);
        Assert.Equal(0, store.Completions);
    }

    [Fact]
    public async Task Claim_loss_cancels_execution_without_a_stale_completion_or_release()
    {
        var store = new Runs { Observed = false };
        var executor = new CaseExecutor(async token => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return Success(); });
        var result = await new RegressionRunExecutor(store, executor).ExecuteAsync(Reference.Revision.OrganizationId, RunId, store.Worker).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RegressionRunDispatchOutcome.ClaimLost, result);
        Assert.Equal(0, store.Releases);
        Assert.Equal(0, store.Completions);
    }

    private static RegressionCaseExecution Success() => RegressionCaseExecution.Complete(MappingPreviewStatus.Succeeded, Value.Expected,
        new RegressionComparisonService().Compare(Value.Expected, new(OperationStatusV1.Succeeded, Value.Expected)));

    private sealed class CaseExecutor(Func<CancellationToken, Task<RegressionCaseExecution>> action) : IRegressionCaseExecutor
    {
        internal int Calls { get; private set; }
        public Task<RegressionCaseExecution> ExecuteAsync(ActorContext actor, RegressionCaseReference expected, CancellationToken cancellationToken = default)
        {
            Calls++;
            return action(cancellationToken);
        }
    }

    private sealed class Runs : IRegressionRunStore
    {
        internal Guid Worker { get; } = Guid.NewGuid();
        internal RegressionRunClaimResult ClaimResult { get; set; }
        internal bool Observed { get; init; } = true;
        internal int Releases { get; private set; }
        internal int Completions { get; private set; }
        internal Runs() => ClaimResult = new(RegressionRunClaimOutcome.Claimed,
            new(RunId, Actor.PrincipalId, Reference, 1, Worker, Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(90)));
        public Task<RegressionRunClaimResult> ClaimAsync(Guid organizationId, Guid runId, Guid worker, CancellationToken token = default) => Task.FromResult(ClaimResult);
        public Task<bool> ObserveAsync(RegressionRunClaim claim, CancellationToken token = default) => Task.FromResult(Observed);
        public Task<bool> CompleteAsync(RegressionRunClaim claim, RegressionCaseExecution completion, CancellationToken token = default) { Completions++; return Task.FromResult(true); }
        public Task<bool> ReleaseAsync(RegressionRunClaim claim, CancellationToken token = default) { Releases++; return Task.FromResult(true); }
        public Task<RegressionRunSubmitOutcome> SubmitAsync(RegressionRunSubmission submission, CancellationToken token = default) => throw new NotSupportedException();
        public Task<bool> CancelAsync(ActorContext actor, Guid organizationId, Guid runId, CancellationToken token = default) => throw new NotSupportedException();
        public Task<RegressionRunSnapshot?> ReadAsync(ActorContext actor, Guid organizationId, Guid runId, CancellationToken token = default) => throw new NotSupportedException();
    }
}
