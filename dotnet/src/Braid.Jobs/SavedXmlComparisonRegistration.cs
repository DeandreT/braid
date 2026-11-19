using Braid.Application;
using Braid.Application.Regression;
using Braid.MappingBackends;
using Braid.MappingBackends.Workers;
using Microsoft.Extensions.DependencyInjection;

namespace Braid.Jobs;

public static class SavedXmlComparisonRegistration
{
    public static void Register(IServiceCollection services, Func<string, string?> env, bool xmlEnabled)
    {
        const string prefix = "BRAID_REGRESSIONS_XML_C14N_";
        if (!XmlPreviewAdmission.Enabled(env, prefix + "ENABLED", xmlEnabled)) return;
        string PathSetting(string name) => env(prefix + name) is { Length: > 0 } path && Path.IsPathFullyQualified(path)
            ? path : throw new ArgumentException(prefix + name + " requires an absolute path.");
        var assembly = PathSetting("ASSEMBLY");
        var launcher = PathSetting("LAUNCHER");
        var root = PathSetting("TEMP_ROOT");
        var toolchain = PathSetting("DOTNET_ROOT");
        var pin = InspectionTransport.RequireImage(env(prefix + "IDENTITY") ?? "");
        services.AddSingleton(_ => new LocalXmlComparisonHost(pin, assembly, launcher, root, toolchain));
        services.AddSingleton(provider =>
        {
            var host = provider.GetRequiredService<LocalXmlComparisonHost>();
            if (host.RuntimeIdentity != pin) throw new InvalidOperationException("Installed XML comparison runtime identity mismatch.");
            return host.Comparison;
        });
    }
}
