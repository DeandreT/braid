using System.CommandLine;
using Weft.Cli.Http;
using Weft.Contracts;

namespace Weft.Cli.Commands;

internal static class HealthCommands
{
    public static Command Health(CliGlobals globals)
    {
        var ready = new Option<bool>("--ready") { Description = "Check whether the host can accept work, not only whether it is alive" };
        var command = new Command("health", "Read the configured server's public health without signing in").With(ready);
        command.Handle(globals, async invocation =>
        {
            var operation = invocation.Value(ready) ? ApiOperations.GetReadiness : ApiOperations.GetHealth;
            using var response = await invocation.Api.SendAsync(new ApiRequest(operation), invocation.Cancellation);
            if (response.Status != 200 && !(invocation.Value(ready) && response.Status == 503))
                throw await response.FailureAsync(invocation.Cancellation);
            var health = await response.ReadJsonAsync<HealthResponseV1>(invocation.Cancellation);
            if (health.Service != "weft-api" || health.ApiVersion != "v1" ||
                health.Status != (response.Status == 200 ? "ok" : "unavailable"))
                throw new CliException(CliExitCode.Transport, "The server's health response is invalid.", "invalidResponse");
            invocation.Output.Write(health, output => output.Fields(("Status", health.Status), ("Service", health.Service),
                ("Version", health.Version), ("API", health.ApiVersion)));
            return (int)(response.Status == 200 ? CliExitCode.Success : CliExitCode.Unavailable);
        });
        return command;
    }
}
