using System.Text;
using System.Text.Json;

namespace Weft.Cli.Configuration;

/// <summary>Profiles and the default profile name, kept in the owner-only <c>config.json</c>. Holds no secret.</summary>
internal sealed record CliConfiguration(string? DefaultProfile, IReadOnlyDictionary<string, CliProfile> Profiles)
{
    public static CliConfiguration Empty { get; } = new(null, new SortedDictionary<string, CliProfile>(StringComparer.Ordinal));
}

/// <summary>Reads and writes the strict configuration document. Unknown or duplicate members are refused.</summary>
internal sealed class CliConfigurationStore
{
    internal const string FileName = "config.json";
    private const int MaximumBytes = 65_536;

    public CliConfigurationStore(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        Directory = directory;
    }

    public string Directory { get; }
    public string Path => System.IO.Path.Combine(Directory, FileName);

    public CliConfiguration Load()
    {
        if (!File.Exists(Path))
            return CliConfiguration.Empty;
        OwnerOnlyFiles.RequirePrivateFile(Path);
        var bytes = File.ReadAllBytes(Path);
        if (bytes.Length > MaximumBytes)
            throw Invalid();
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            RequireObject(root, ["version", "defaultProfile", "profiles"]);
            if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || version.GetInt32() != 1)
                throw Invalid();
            string? defaultProfile = null;
            if (root.TryGetProperty("defaultProfile", out var selected) && selected.ValueKind != JsonValueKind.Null)
                defaultProfile = CliProfile.RequireName(selected.GetString()!);
            var profiles = new SortedDictionary<string, CliProfile>(StringComparer.Ordinal);
            if (root.TryGetProperty("profiles", out var entries))
            {
                if (entries.ValueKind != JsonValueKind.Object)
                    throw Invalid();
                foreach (var entry in entries.EnumerateObject())
                {
                    var value = entry.Value;
                    RequireObject(value, ["server", "tenantId", "clientId", "scope"]);
                    var profile = new CliProfile(entry.Name, CliProfile.ParseServer(Text(value, "server")),
                        CliProfile.ParseId(Text(value, "tenantId"), "tenant"), CliProfile.ParseId(Text(value, "clientId"), "client ID"),
                        Text(value, "scope"));
                    if (!profiles.TryAdd(profile.Name, profile))
                        throw Invalid();
                }
            }
            if (defaultProfile is not null && !profiles.ContainsKey(defaultProfile))
                throw Invalid();
            return new CliConfiguration(defaultProfile, profiles);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or ArgumentException)
        {
            throw Invalid();
        }
    }

    public void Save(CliConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 1);
            if (configuration.DefaultProfile is { } selected)
                writer.WriteString("defaultProfile", selected);
            else
                writer.WriteNull("defaultProfile");
            writer.WriteStartObject("profiles");
            foreach (var profile in configuration.Profiles.Values.OrderBy(profile => profile.Name, StringComparer.Ordinal))
            {
                writer.WriteStartObject(profile.Name);
                writer.WriteString("server", profile.Server.GetLeftPart(UriPartial.Authority));
                writer.WriteString("tenantId", profile.TenantId.ToString("D"));
                writer.WriteString("clientId", profile.ClientId.ToString("D"));
                writer.WriteString("scope", profile.Scope);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        OwnerOnlyFiles.WriteAllText(Path, Encoding.UTF8.GetString(buffer.ToArray()) + "\n");
    }

    /// <summary>The explicit profile, then <c>WEFT_PROFILE</c>, then the configured default.</summary>
    public static CliProfile Select(CliConfiguration configuration, string? requested)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var name = requested ?? configuration.DefaultProfile;
        if (name is null)
            throw CliException.Usage(configuration.Profiles.Count == 0
                ? "No profile is configured. Create one with: weft profile set <name> --server <url> --tenant <id> --client-id <id> --scope <scope>"
                : "No default profile is selected. Pass --profile <name> or run: weft profile use <name>");
        return configuration.Profiles.TryGetValue(CliProfile.RequireName(name), out var profile)
            ? profile
            : throw CliException.Usage($"The profile '{name}' does not exist. List profiles with: weft profile list");
    }

    private static void RequireObject(JsonElement element, string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new FormatException();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
                throw new FormatException();
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new FormatException();

    private CliException Invalid() =>
        CliException.Usage($"The Weft configuration file {Path} is not a valid version 1 configuration.");
}
