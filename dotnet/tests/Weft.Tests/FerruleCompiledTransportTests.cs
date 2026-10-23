using System.Net;
using Weft.MappingBackends.Workers;
using static Weft.Tests.FerruleServiceTransportTests;

namespace Weft.Tests;

public sealed class FerruleCompiledTransportTests
{
    [Fact]
    public async Task A_runtime_pin_is_required_before_execution_and_returned_with_the_result()
    {
        await using var service = await ServiceCase.StartAsync(execute: true);
        using var client = service.Client();
        using var missing = Operation(FerruleServiceProtocol.CompiledProfile, [1]);
        missing.Headers.Remove(FerruleServiceProtocol.CompiledRuntimeHeader);
        using var refused = await client.SendAsync(missing);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Empty(service.Runtime.Frames);
        using var accepted = await client.SendAsync(Operation(FerruleServiceProtocol.CompiledProfile, [1]));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(ServiceCase.RuntimeIdentity, Assert.Single(accepted.Headers.GetValues(FerruleServiceProtocol.CompiledRuntimeHeader)));
        Assert.Single(service.Runtime.Frames);
    }

    [Fact]
    public async Task A_changed_runtime_with_the_same_native_image_receives_no_frame()
    {
        await using var service = await ServiceCase.StartAsync(execute: true);
        var connector = new PinnedServiceConnector(new(service.Endpoint, service.ServerPin, service.ClientCertificate));
        await using var runner = new FerruleServiceRunner(new(connector, FerruleServiceProtocol.CompiledProfile, service.Runtime.BuildIdentity,
            compiledRuntimeIdentity: "sha256:" + new string('a', 64)));
        Assert.Equal(WorkerProcessFailure.SandboxUnavailable, (await runner.RunAsync(new byte[] { 1 }, default)).Failure);
        Assert.Empty(service.Runtime.Frames);
    }

    [Fact]
    public async Task The_execution_runner_reports_runtime_identity_not_compiler_identity()
    {
        await using var service = await ServiceCase.StartAsync(execute: true);
        var connector = new PinnedServiceConnector(new(service.Endpoint, service.ServerPin, service.ClientCertificate));
        await using var runner = new FerruleServiceRunner(new(connector, FerruleServiceProtocol.CompiledProfile, service.Runtime.BuildIdentity,
            compiledRuntimeIdentity: ServiceCase.RuntimeIdentity));
        Assert.Equal(ServiceCase.RuntimeIdentity, runner.BuildIdentity);
        Assert.Null(runner.CompilationBuildIdentity);
        Assert.Equal(WorkerProcessFailure.None, (await runner.RunAsync(new byte[] { 3, 4 }, default)).Failure);
        Assert.Equal(new byte[] { 3, 4 }, Assert.Single(service.Runtime.Frames));
    }

    [Fact]
    public async Task A_changed_result_runtime_is_not_accepted_as_settled_execution()
    {
        await using var service = await ServiceCase.StartAsync(execute: true);
        var connector = new ChangedAnswer(new PinnedServiceConnector(new(service.Endpoint, service.ServerPin, service.ClientCertificate)));
        var runner = new FerruleServiceRunner(new(connector, FerruleServiceProtocol.CompiledProfile, service.Runtime.BuildIdentity,
            compiledRuntimeIdentity: ServiceCase.RuntimeIdentity));
        Assert.Equal(WorkerProcessFailure.CleanupFailed, (await runner.RunAsync(new byte[] { 1 }, default)).Failure);
        Assert.Single(service.Runtime.Frames);
        await Assert.ThrowsAsync<InvalidOperationException>(runner.StopAsync);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await runner.DisposeAsync());
    }

    private sealed class ChangedAnswer(IFerruleServiceConnector inner) : IFerruleServiceConnector
    {
        public IFerruleServiceChannel Open() => new Channel(inner.Open());
        private sealed class Channel(IFerruleServiceChannel inner) : IFerruleServiceChannel
        {
            public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                var response = await inner.SendAsync(request, token);
                if (request.Method == HttpMethod.Post && response.StatusCode == HttpStatusCode.OK)
                {
                    response.Headers.Remove(FerruleServiceProtocol.CompiledRuntimeHeader);
                    response.Headers.Add(FerruleServiceProtocol.CompiledRuntimeHeader, "sha256:" + new string('a', 64));
                }
                return response;
            }
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
