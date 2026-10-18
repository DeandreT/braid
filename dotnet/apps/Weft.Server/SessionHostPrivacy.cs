using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Abstractions;
using Microsoft.IdentityModel.Logging;

namespace Weft.Server;

/// <summary>Process-owned, irreversible suppression for the explicit hosted session profile only.</summary>
internal static class SessionHostPrivacy
{
    private static readonly IIdentityLogger SilentIdentityLogger = new SilentLogger();

    internal static void BeforeBuilder(Func<string, string?> read)
    {
        if (new[] { "DOTNET_STARTUP_HOOKS", "ASPNETCORE_HOSTINGSTARTUPASSEMBLIES", "ASPNETCORE_HOSTINGSTARTUPEXCLUDEASSEMBLIES",
                "CORECLR_PROFILER", "COR_PROFILER", "OTEL_DOTNET_AUTO_HOME", "OTEL_DOTNET_AUTO_INTEGRATIONS_FILE" }
            .Any(name => read(name) is not null) ||
            read("CORECLR_ENABLE_PROFILING") is not (null or "0") ||
            read("COR_ENABLE_PROFILING") is not (null or "0") ||
            read("DOTNET_SYSTEM_NET_HTTP_DISABLEURIREDACTION") is not (null or "0" or "false") ||
            AppContext.TryGetSwitch("System.Net.Http.DisableUriRedaction", out var disabled) && disabled)
            throw ServerHostOptions.Failure();
        IdentityModelEventSource.ShowPII = false;
        IdentityModelEventSource.LogCompleteSecurityArtifact = false;
        LogHelper.Logger = SilentIdentityLogger;
        IdentityModelEventSource.Logger.Dispose();
    }

    internal static void BeforeStart(IServiceProvider services)
    {
        if (IdentityModelEventSource.ShowPII || IdentityModelEventSource.LogCompleteSecurityArtifact ||
            !ReferenceEquals(LogHelper.Logger, SilentIdentityLogger)) throw ServerHostOptions.Failure();
        var listeners = services.GetServices<DiagnosticListener>().ToArray();
        var sources = services.GetServices<ActivitySource>().ToArray();
        if (listeners.Length != 1 || sources.Length != 1 || listeners[0].Name != "Microsoft.AspNetCore" ||
            sources[0].Name != "Microsoft.AspNetCore" ||
            !ReferenceEquals(listeners[0], services.GetRequiredService<DiagnosticSource>()))
            throw ServerHostOptions.Failure();
        listeners[0].Dispose();
        sources[0].Dispose();
    }

    private sealed class SilentLogger : IIdentityLogger
    {
        public bool IsEnabled(EventLogLevel eventLogLevel) => false;
        public void Log(LogEntry entry) { }
    }
}
