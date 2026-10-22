using System.Text.Json;

namespace Weft.Tests;

public sealed class ChildProgramBuildOutputTests
{
    [Theory]
    [InlineData("PreviewWorker", "Weft.Worker")]
    [InlineData("FerruleService", "weft-ferrule-service")]
    [InlineData("StorageProbe", "Weft.StorageProbe")]
    [InlineData("StorageProtocolProbe", "Weft.StorageProtocolProbe")]
    [InlineData("IngestionProbe", "Weft.IngestionProbe")]
    [InlineData("StorageTool", "Weft.StorageTool")]
    [InlineData("RevisionReportMetadataProbe", "Weft.RevisionReportMetadataProbe")]
    [InlineData("DatabaseTool", "Weft.DatabaseTool")]
    [InlineData("RevisionReportWorkflowProbe", "Weft.RevisionReportWorkflowProbe")]
    [InlineData("MaintenanceProbe", "Weft.MaintenanceProbe")]
    [InlineData("MaintenanceTool", "Weft.MaintenanceTool")]
    [InlineData("SessionKeyTool", "Weft.SessionKeyTool")]
    [InlineData("SessionTicketProcessProbe", "Weft.SessionTicketProcessProbe")]
    [InlineData("HostPrivacyProbe", "Weft.HostPrivacyProbe")]
    [InlineData("WorkerShutdownProbe", "Weft.WorkerShutdownProbe")]
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
