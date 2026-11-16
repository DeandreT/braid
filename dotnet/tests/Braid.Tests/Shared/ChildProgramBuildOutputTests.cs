using System.Text.Json;

namespace Braid.Tests;

public sealed class ChildProgramBuildOutputTests
{
    [Theory]
    [InlineData("PreviewWorker", "Braid.Worker")]
    [InlineData("FerruleService", "braid-ferrule-service")]
    [InlineData("CompiledRuntime", "braid-compiled-runtime")]
    [InlineData("StorageProbe", "Braid.StorageProbe")]
    [InlineData("StorageProtocolProbe", "Braid.StorageProtocolProbe")]
    [InlineData("IngestionProbe", "Braid.IngestionProbe")]
    [InlineData("StorageTool", "Braid.StorageTool")]
    [InlineData("RevisionReportMetadataProbe", "Braid.RevisionReportMetadataProbe")]
    [InlineData("DatabaseTool", "Braid.DatabaseTool")]
    [InlineData("RevisionReportWorkflowProbe", "Braid.RevisionReportWorkflowProbe")]
    [InlineData("MaintenanceProbe", "Braid.MaintenanceProbe")]
    [InlineData("MaintenanceTool", "Braid.MaintenanceTool")]
    [InlineData("SessionKeyTool", "Braid.SessionKeyTool")]
    [InlineData("SessionTicketProcessProbe", "Braid.SessionTicketProcessProbe")]
    [InlineData("HostPrivacyProbe", "Braid.HostPrivacyProbe")]
    [InlineData("WorkerShutdownProbe", "Braid.WorkerShutdownProbe")]
    public void Every_child_program_has_its_entrypoint_and_runtime_dependencies(string directory, string assembly)
    {
        var output = Path.Combine(AppContext.BaseDirectory, directory);
        Assert.True(File.Exists(Path.Combine(output, assembly + ".dll")), $"Missing {directory} entrypoint.");
        Assert.True(File.Exists(Path.Combine(output, assembly + ".runtimeconfig.json")), $"Missing {directory} runtime configuration.");
        using var dependencies = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, assembly + ".deps.json")));
        foreach (var target in dependencies.RootElement.GetProperty("targets").EnumerateObject())
        {
            foreach (var library in target.Value.EnumerateObject())
            {
                if (!library.Value.TryGetProperty("runtime", out var runtime)) continue;
                foreach (var dependency in runtime.EnumerateObject())
                {
                    var filename = Path.GetFileName(dependency.Name);
                    if (!filename.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                    Assert.True(File.Exists(Path.Combine(output, filename)), $"Missing {directory} runtime dependency {filename}.");
                }
            }
        }
    }
}
