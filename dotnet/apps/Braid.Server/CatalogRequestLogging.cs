using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Braid.Server;

/// <summary>Request-start diagnostics contain raw queries and must stay off even under provider overrides.</summary>
public static class CatalogRequestLogging
{
    public const string HostingDiagnosticsCategory = "Microsoft.AspNetCore.Hosting.Diagnostics";

    public static IServiceCollection SuppressRequestDiagnostics(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.PostConfigure<LoggerFilterOptions>(options =>
        {
            var providers = options.Rules.Select(rule => rule.ProviderName).Append(null).Distinct().ToArray();
            // A more specific category rule beats a parent rule regardless of
            // insertion order. Remove matching descendants before appending None.
            for (var index = options.Rules.Count - 1; index >= 0; index--)
            {
                var category = options.Rules[index].CategoryName;
                if (category is not null && category.StartsWith(HostingDiagnosticsCategory, StringComparison.OrdinalIgnoreCase))
                    options.Rules.RemoveAt(index);
            }
            foreach (var provider in providers)
                options.Rules.Add(new LoggerFilterRule(provider, HostingDiagnosticsCategory, LogLevel.None, (_, _, _) => false));
        });
        return services;
    }

    // Hosted session mode emits only the entrypoint's fixed categorical status lines.
    // Provider overrides cannot reopen framework/auth/network exception or request logs.
    internal static IServiceCollection SuppressSessionDiagnostics(this IServiceCollection services)
    {
        services.PostConfigure<LoggerFilterOptions>(options =>
        {
            var providers = options.Rules.Select(rule => rule.ProviderName).Append(null).Distinct().ToArray();
            options.Rules.Clear();
            options.MinLevel = LogLevel.None;
            foreach (var provider in providers)
                options.Rules.Add(new LoggerFilterRule(provider, null, LogLevel.None, (_, _, _) => false));
        });
        return services;
    }
}
