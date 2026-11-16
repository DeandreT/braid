using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Braid.Application.Automation;

public static class NotificationConfigurationFile
{
    public static NotificationConfiguration Read(Func<string, string?> environment, Func<string, int, string>? privateReader = null)
    {
        var path = environment("BRAID_NOTIFICATION_CONFIG_FILE");
        if (path is null) return NotificationConfiguration.Disabled;
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Notification configuration needs an absolute private file.");
        if (privateReader is null) throw new ArgumentException("Notification configuration requires an admitted private-file reader.");
        var text = privateReader(path, 65536);
        if (Encoding.UTF8.GetByteCount(text) is < 2 or > 65536) throw new ArgumentException("Notification configuration exceeds its bound.");
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 8 });
        Unique(document.RootElement);
        var result = document.RootElement.Deserialize<NotificationConfiguration>(new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { MaxDepth = 8, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true }) ?? throw new ArgumentException("Notification configuration missing.");
        result.Validate();
        if (!result.Hosted && environment("DOTNET_ENVIRONMENT") != "Development" && environment("ASPNETCORE_ENVIRONMENT") != "Development")
            throw new ArgumentException("Non-hosted notification targets require an explicit Development host.");
        return result;
    }

    private static void Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new ArgumentException("Duplicate notification configuration property.");
                Unique(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) Unique(item);
    }
}
