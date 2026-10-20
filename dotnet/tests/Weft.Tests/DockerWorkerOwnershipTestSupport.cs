using System.Text.Json;

namespace Weft.Tests;

/// <summary>Independent test teardown oracle; no prefix, image or name alone authorizes removal.</summary>
internal static class DockerWorkerOwnershipTestSupport
{
    internal const string OperationLabel = "io.weft.inspection.operation.v1";
    internal const string InspectFormat = """{"Id":{{json .Id}},"Name":{{json .Name}},"Config":{"Labels":{{json .Config.Labels}}}}""";

    internal static string VerifiedId(string json, string name, string owner, string? expectedId = null)
    {
        Assert.Matches("^weft-inspect-[a-f0-9]{32}$", name);
        Assert.Matches("^[a-f0-9]{32}$", owner);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        var id = Property(root, "Id").GetString()!;
        Assert.Matches("^[a-f0-9]{64}$", id);
        Assert.Equal("/" + name, Property(root, "Name").GetString());
        var labels = Property(Property(root, "Config"), "Labels");
        Assert.Equal(owner, Property(labels, OperationLabel).GetString());
        if (expectedId is not null) Assert.Equal(expectedId, id);
        return id;
    }

    private static JsonElement Property(JsonElement parent, string name) =>
        Assert.Single(parent.EnumerateObject(), property => property.NameEquals(name)).Value;

    internal static bool IsMissing(string error, string target) => error.Trim() is var text &&
        (text == $"Error: No such object: {target}" || text == $"Error: No such container: {target}"
            || text == $"Error response from daemon: No such container: {target}");
}
