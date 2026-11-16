using System.Text;
using Braid.Application;
using Braid.Application.Regression;
using Braid.Contracts.Operations;
using Braid.Deployment;
using Braid.MappingBackends;
using Braid.MappingBackends.Workers;
using static Braid.Tests.FerruleServiceTestSupport;

namespace Braid.Tests;

/// <summary>
/// Real read-only runtime transport and dependency checks. Pre-decode cancellation is deliberately
/// separate from qualification of interruption during the eager canonicalizer's synchronous calls.
/// </summary>
[Collection(FerruleServiceProcessCollection.Name)]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class XmlComparisonLiveRuntimeTests
{
    [ServiceCompileFact]
    public async Task Installed_XML_runtime_loads_its_canonicalizer_inside_the_existing_sandbox()
    {
        var root = TemporaryRoot();
        try
        {
            await using var runner = new ProcessCompiledRuntimeRunner(Options(root));
            Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(FerruleCompiledServiceTests.Assembly)!, "System.Security.Cryptography.Xml.dll")));
            Assert.True(await runner.ProbeAsync(default));
            Assert.True(runner.Available);
            Assert.Empty(CompileChildren());
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally { RemoveSettledEmptyRoot(root); }
    }

    [ServiceCompileFact]
    public async Task Real_XML_child_compares_complete_sets_refuses_DTDs_and_recovers_without_leaking_content()
    {
        var root = TemporaryRoot();
        try
        {
            await using var runner = new ProcessCompiledRuntimeRunner(Options(root));
            var worker = new XmlCanonicalComparisonWorker(runner, ComparisonOptions());
            var expected = Set((null, "<r b='2' a='1'>private-sentinel</r>"), ("extra", "<s/>"));
            var actual = Set((null, "<r a=\"1\" b=\"2\">private-sentinel</r>"), ("extra", "<s></s>"));
            var compared = await worker.CompareAsync(expected, actual, OperationStatusV1.Succeeded);
            Assert.Equal(XmlComparisonWorkerFailure.None, compared.Failure);
            Assert.Equal(RegressionComparisonOutcome.Passed, compared.Comparison!.Outcome);
            Assert.Equal(expected.ByteCount, compared.Comparison.ExpectedOriginalBytes);
            Assert.Equal(actual.ByteCount, compared.Comparison.ActualOriginalBytes);

            var unsafeSet = Set((null, "<!DOCTYPE r [<!ENTITY secret SYSTEM 'file:///private-sentinel'>]><r>&secret;</r>"));
            var request = XmlComparisonProtocol.For(unsafeSet, unsafeSet, OperationStatusV1.Succeeded, ComparisonOptions());
            var wire = await runner.RunAsync(XmlComparisonProtocol.EncodeRequest(request, unsafeSet, unsafeSet), default);
            Assert.Equal(WorkerProcessFailure.None, wire.Failure);
            Assert.DoesNotContain("private-sentinel", Encoding.UTF8.GetString(wire.Output), StringComparison.Ordinal);
            var refused = XmlComparisonProtocol.DecodeResponse(wire.Output, request, unsafeSet, unsafeSet);
            Assert.Equal(RegressionComparisonOutcome.NotEvaluated, refused.Outcome);
            Assert.Empty(refused.Differences);
            Assert.Null(refused.ExpectedCanonical);
            Assert.Equal(RegressionComparisonOutcome.Passed,
                (await worker.CompareAsync(expected, actual, OperationStatusV1.Succeeded)).Comparison!.Outcome);
            Assert.True(runner.Available);
            Assert.Empty(CompileChildren());
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally { RemoveSettledEmptyRoot(root); }
    }

    [ServiceCompileFact]
    public async Task Real_XML_child_enforces_caller_selected_canonical_budgets_without_partial_results()
    {
        var root = TemporaryRoot();
        try
        {
            await using var runner = new ProcessCompiledRuntimeRunner(Options(root));
            // These are deliberately small test allowances, not production canonicalizer defaults.
            var worker = new XmlCanonicalComparisonWorker(runner, new(new(4096, 4), new(4096, 64, 128)));
            var set = Set((null, "<r>" + new string('>', 128) + "</r>"));
            var result = await worker.CompareAsync(set, set, OperationStatusV1.Succeeded);
            Assert.Equal(XmlComparisonWorkerFailure.None, result.Failure);
            Assert.Equal(RegressionComparisonOutcome.NotEvaluated, result.Comparison!.Outcome);
            Assert.Equal(XmlCanonicalFailure.ResourceLimit, result.Comparison.NotEvaluatedReason);
            Assert.Empty(result.Comparison.Differences);
            Assert.Null(result.Comparison.ExpectedCanonical);
            Assert.Null(result.Comparison.ActualCanonical);
            Assert.True(runner.Available);
            Assert.Empty(CompileChildren());
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally { RemoveSettledEmptyRoot(root); }
    }

    [ServiceCompileTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task XML_predecode_deadline_and_cancellation_reap_the_actual_child_and_settle_aggregate_cleanup(bool cancel)
    {
        var root = TemporaryRoot();
        try
        {
            await using var runner = new ProcessCompiledRuntimeRunner(Options(root, TimeSpan.FromSeconds(cancel ? 20 : 5), withhold: true));
            using var cancellation = new CancellationTokenSource();
            var pending = runner.RunAsync(new byte[] { 0 }, cancellation.Token);
            int child = 0;
            for (var attempt = 0; attempt < 200 && child == 0; attempt++)
            {
                // The launcher hosts the runtime in-process through hostfxr; there is no separate dotnet child.
                child = Children(Path.GetFileName(Launcher!)).FirstOrDefault();
                if (child == 0) await Task.Delay(10);
            }
            Assert.NotEqual(0, child);
            if (cancel) cancellation.Cancel();
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(cancel ? WorkerProcessFailure.Canceled : WorkerProcessFailure.TimedOut, result.Failure);
            Assert.Empty(result.Output);
            Assert.False(Directory.Exists("/proc/" + child));
            Assert.Empty(CompileChildren());
            Assert.True(runner.Available);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally { RemoveSettledEmptyRoot(root); }
    }

    private static ProcessCompiledRuntimeOptions Options(string root, TimeSpan? wall = null, bool withhold = false) =>
        new(PackagedFerruleService.Identity, FerruleCompiledServiceTests.Assembly, Launcher!, root, Toolchain, wall)
        { Operation = CompiledRuntimeOperation.XmlCanonicalComparison, WithholdEndOfInput = withhold };

    private static XmlComparisonWorkerOptions ComparisonOptions() => new(new(4096, 4), new(4096, 4096, 8192));

    private static XmlComparisonOutputSet Set(params (string? Name, string Xml)[] documents) => XmlComparisonOutputSet.Capture(documents.Select(value =>
        XmlComparisonOutput.Capture(value.Name is null ? RegressionOutputKey.Primary : RegressionOutputKey.Named(value.Name),
            Encoding.UTF8.GetBytes(value.Xml), XmlDocumentMetadata.WithoutHints)).ToArray());

    private static void RemoveSettledEmptyRoot(string root)
    {
        // Preserve a failed cleanup's directory and ownership evidence, never recursively erase it.
        if (CompileChildren().Length == 0 && Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any())
            Directory.Delete(root);
    }
}
