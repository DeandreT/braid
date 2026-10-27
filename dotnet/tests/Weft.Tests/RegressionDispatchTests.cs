using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Weft.Application;
using Weft.Application.Regression;
using Weft.Jobs;

namespace Weft.Tests;

public sealed class RegressionDispatchTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task One_failed_dispatch_does_not_block_independent_ready_runs(int failure)
    {
        var items = Enumerable.Range(0, 3).Select(_ => new RegressionRunReady(Guid.NewGuid(), Guid.NewGuid())).ToArray();
        var calls = new List<Guid>();
        var pump = new RegressionRunPump(new Queue(items), new Dispatcher((_, id, _) =>
        {
            calls.Add(id);
            if (id == items[failure].RunId) throw new InvalidOperationException("private dependency details");
            return Task.FromResult(RegressionRunDispatchOutcome.Completed);
        }));
        var result = await pump.DrainOnceAsync();
        Assert.Equal(new RegressionDispatchBatch(3, 2, 0, 1), result);
        Assert.Equal(items.Select(item => item.RunId), calls);
    }

    [Fact]
    public async Task Cancellation_is_not_converted_to_a_successful_batch()
    {
        using var stop = new CancellationTokenSource();
        var pump = new RegressionRunPump(new Queue([new(Guid.NewGuid(), Guid.NewGuid())]), new Dispatcher((_, _, token) =>
        {
            stop.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(RegressionRunDispatchOutcome.Completed);
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pump.DrainOnceAsync(stop.Token));
    }

    [Fact]
    public void Disabled_worker_reads_no_credentials_and_registers_no_services()
    {
        var services = new ServiceCollection();
        services.AddWeftRegressionWorker(name => name == "WEFT_REGRESSION_WORKER_ENABLED" ? null : throw new InvalidOperationException(),
            () => throw new InvalidOperationException());
        Assert.Empty(services);
    }

    [Theory]
    [InlineData("TRUE")]
    [InlineData("1")]
    [InlineData("")]
    public void Malformed_enablement_does_not_read_credentials(string flag)
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentException>(() => services.AddWeftRegressionWorker(
            name => name == "WEFT_REGRESSION_WORKER_ENABLED" ? flag : throw new InvalidOperationException(),
            () => throw new InvalidOperationException()));
        Assert.Empty(services);
    }

    [Fact]
    public void Enabled_worker_requires_all_four_explicit_connection_profiles()
    {
        foreach (var missing in Variables)
        {
            var services = new ServiceCollection();
            Assert.Throws<ArgumentException>(() => services.AddWeftRegressionWorker(name => name switch
            {
                "WEFT_REGRESSION_WORKER_ENABLED" => "true",
                _ when name == missing => null,
                _ => "private-synthetic"
            }, () => throw new InvalidOperationException()));
            Assert.Empty(services);
        }
    }

    [Fact]
    public void Enabled_worker_registers_regression_and_automation_without_opening_a_pool()
    {
        var services = new ServiceCollection();
        services.AddWeftRegressionWorker(name => name == "WEFT_REGRESSION_WORKER_ENABLED" ? "true" : Variables.Contains(name) ? "private-synthetic" : null,
            () => new("ferrule", "engine", "build", "adapter", PreviewPolicy.Profile),
            () => new("ferrule", "engine", "build", "document-adapter", DocumentPreviewPolicy.Profile));
        Assert.Equal(new[] { typeof(AutomationWorker), typeof(RegressionWorkerService) },
            services.Where(service => service.ServiceType == typeof(IHostedService)).Select(service => service.ImplementationType));
        Assert.Single(services, service => service.ServiceType == typeof(IRegressionCaseExecutor));
        Assert.Single(services, service => service.ServiceType == typeof(IRegressionEvaluationExecutor));
        Assert.Single(services, service => service.ServiceType == typeof(IRegressionRunQueue));
        Assert.Single(services, service => service.ServiceType == typeof(IRegressionRunDispatcher));
    }

    private static readonly string[] Variables = ["WEFT_REGRESSION_RUN_WRITER_CONNECTION_STRING", "WEFT_REGRESSION_CASE_READER_CONNECTION_STRING",
        "WEFT_REGRESSION_ACCESS_CONNECTION_STRING", "WEFT_REGRESSION_REPORT_READER_CONNECTION_STRING", "WEFT_AUTOMATION_COMPARISON_CONNECTION_STRING"];

    private sealed class Queue(IReadOnlyList<RegressionRunReady> items) : IRegressionRunQueue
    {
        public Task<IReadOnlyList<RegressionRunReady>> ListReadyAsync(int limit, CancellationToken cancellationToken = default) => Task.FromResult(items);
    }
    private sealed class Dispatcher(Func<Guid, Guid, CancellationToken, Task<RegressionRunDispatchOutcome>> action) : IRegressionRunDispatcher
    {
        public Task<RegressionRunDispatchOutcome> ExecuteAsync(Guid organizationId, Guid runId, Guid worker, CancellationToken token = default) => action(organizationId, runId, token);
    }
}
