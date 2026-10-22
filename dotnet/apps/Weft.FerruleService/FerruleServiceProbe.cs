using System.Net;
using Weft.MappingBackends.Workers;

namespace Weft.FerruleService;

/// <summary>
/// Asks the running service on this host for readiness through its plain probe listener (or, in platform-sandbox
/// mode, its only listener) on loopback. Exit code 0 means ready.
/// </summary>
internal static class FerruleServiceProbe
{
    internal static async Task<int> RunAsync(Func<string, string?> environment)
    {
        var listener = environment(FerruleServiceSettings.HealthListen) ??
            (environment(FerruleServiceSettings.Authentication) == "platformSandbox" ? environment(FerruleServiceSettings.Listen) : null);
        if (listener is null || !IPEndPoint.TryParse(listener, out var endpoint) || endpoint.Port == 0) return 2;
        try
        {
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
            using var response = await client.GetAsync(new Uri("http://127.0.0.1:" + endpoint.Port + FerruleServiceProtocol.ReadinessPath));
            return response.StatusCode == HttpStatusCode.NoContent ? 0 : 1;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            return 1;
        }
    }
}
