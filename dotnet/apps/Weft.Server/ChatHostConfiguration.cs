using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Weft.Ai;
using Weft.Api.Catalog;
using Weft.Api.Chat;
using Weft.Application;
using Weft.Application.MappingTools;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Server;

/// <summary>
/// Explicit server-owned chat composition. Disabled chat registers only its unavailable routes and opens
/// no profile file, credential, pool or provider client; enabling it never changes other features.
/// </summary>
internal static class ChatHostConfiguration
{
    internal const int ProfileFileBytes = 65_536;
    internal const int CredentialFileBytes = 8_192;

    internal static void Configure(IServiceCollection services, Func<string, string?> read, bool enabled,
        bool catalogEnabled, CatalogRevisionReportOptions reports)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(reports);
        if (!enabled)
        {
            services.AddWeftChat(new ChatApiOptions());
            return;
        }
        // Conversations pin verified reports; chat without catalog and report reads is an invalid profile.
        if (!catalogEnabled || !reports.Enabled) throw ServerHostOptions.Failure();
        var configuration = Parse(HostPrivateFiles.ReadText(ServerHostOptions.Required(read, "WEFT_CHAT_PROFILES_FILE"), ProfileFileBytes));
        var readerConnection = ServerHostOptions.Required(read, "WEFT_CHAT_READER_CONNECTION_STRING");
        var writerConnection = ServerHostOptions.Required(read, "WEFT_CHAT_WRITER_CONNECTION_STRING");
        if (readerConnection == writerConnection) throw ServerHostOptions.Failure();
        services.AddSingleton(_ => new ChatHostDatabases(readerConnection, writerConnection));
        services.AddSingleton(provider =>
        {
            var databases = provider.GetRequiredService<ChatHostDatabases>();
            var reader = new PostgresChatStore(databases.Reader);
            var writer = new PostgresChatStore(databases.Writer);
            return new ChatPersistencePorts(reader, reader, writer, writer);
        });
        services.AddSingleton<IAiCredentialResolver>(new ChatHostCredentialResolver(configuration.Credentials));
        // One shared read-only tool service bounds its own concurrent report reads for the whole host.
        services.AddSingleton(provider => new ChatToolRegistry(MappingToolRegistry.Definitions, new MappingToolService(
            provider.GetRequiredService<IRevisionReportMetadataReader>(), provider.GetRequiredService<IRevisionReportReader>())));
        services.AddWeftChat(configuration.Options);
    }

    internal static ChatHostProfiles Parse(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                MaxDepth = 6,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            var root = Object(document.RootElement, ["version", "retentionDays", "turnSeconds", "maxOutputTokens", "profiles", "credentials"],
                ["version", "retentionDays", "profiles", "credentials"]);
            if (Int(root["version"]) != 1) throw ServerHostOptions.Failure();
            var retention = Int(root["retentionDays"]);
            var seconds = root.TryGetValue("turnSeconds", out var turn) ? Int(turn) : 120;
            var tokens = root.TryGetValue("maxOutputTokens", out var output) ? Int(output) : 1024;
            if (retention is < 1 or > 3650) throw ServerHostOptions.Failure();
            var credentials = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var item in Array(root["credentials"], 16))
            {
                var credential = Object(item, ["reference", "file"], ["reference", "file"]);
                var reference = ChatBounds.Identifier(String(credential["reference"]));
                var file = String(credential["file"]);
                if (!Path.IsPathFullyQualified(file) || file != Path.GetFullPath(file) || !credentials.TryAdd(reference, file))
                    throw ServerHostOptions.Failure();
            }
            var profiles = Array(root["profiles"], 16).Select(Profile).ToArray();
            var used = profiles.Select(profile => profile.Spec.CredentialReference).OfType<string>().ToHashSet(StringComparer.Ordinal);
            // Every bearer reference resolves, and no unused secret file is admitted.
            if (!used.SetEquals(credentials.Keys)) throw ServerHostOptions.Failure();
            return new(new ChatApiOptions(profiles, TimeSpan.FromDays(retention), TimeSpan.FromSeconds(seconds), tokens), credentials);
        }
        catch (Exception error) when (SessionHostResources.Nonfatal(error)) { throw ServerHostOptions.Failure(); }
    }

    private static ChatModelProfile Profile(JsonElement element)
    {
        var item = Object(element, ["id", "version", "displayName", "provider", "dialect", "model", "endpoint", "loopbackFixture",
            "credentialReference", "capabilities", "metadataPolicy", "requestThinkFalse"],
            ["id", "version", "displayName", "provider", "dialect", "model", "endpoint", "credentialReference", "capabilities", "metadataPolicy"]);
        var dialect = String(item["dialect"]) switch
        {
            "chatCompletionsSse" => AiDialect.ChatCompletionsSseV1,
            "ollamaChatNdjson" => AiDialect.OllamaChatNdjsonV1,
            _ => throw ServerHostOptions.Failure()
        };
        var capabilities = (AiCapability)0;
        foreach (var capability in Array(item["capabilities"], 2))
        {
            var flag = String(capability) switch
            {
                "text" => AiCapability.Text,
                "functionTools" => AiCapability.FunctionTools,
                _ => throw ServerHostOptions.Failure()
            };
            if ((capabilities & flag) != 0) throw ServerHostOptions.Failure();
            capabilities |= flag;
        }
        var policy = Object(item["metadataPolicy"], ["id", "version", "mappingMetadataAllowed"], ["id", "version", "mappingMetadataAllowed"]);
        string? reference = item["credentialReference"].ValueKind == JsonValueKind.Null ? null : ChatBounds.Identifier(String(item["credentialReference"]));
        var endpoint = new Uri(String(item["endpoint"]), UriKind.Absolute);
        var spec = AiProfileSpec.Create(AiProfileKey.Create(String(item["id"]), Int(item["version"])), dialect, String(item["model"]),
            endpoint, item.TryGetValue("loopbackFixture", out var fixture) && Bool(fixture),
            reference is null ? AiAuthentication.None : AiAuthentication.Bearer, reference, capabilities,
            String(policy["id"]), Int(policy["version"]), Bool(policy["mappingMetadataAllowed"]),
            item.TryGetValue("requestThinkFalse", out var think) && Bool(think));
        return new(String(item["displayName"]), String(item["provider"]), spec);
    }

    private static Dictionary<string, JsonElement> Object(JsonElement element, string[] allowed, string[] required)
    {
        if (element.ValueKind != JsonValueKind.Object) throw ServerHostOptions.Failure();
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !result.TryAdd(property.Name, property.Value))
                throw ServerHostOptions.Failure();
        if (required.Any(name => !result.ContainsKey(name))) throw ServerHostOptions.Failure();
        return result;
    }

    private static JsonElement[] Array(JsonElement element, int maximum) =>
        element.ValueKind == JsonValueKind.Array && element.GetArrayLength() <= maximum
            ? element.EnumerateArray().ToArray() : throw ServerHostOptions.Failure();
    private static string String(JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? element.GetString()! : throw ServerHostOptions.Failure();
    private static bool Bool(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw ServerHostOptions.Failure()
    };
    private static int Int(JsonElement element) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value) &&
            element.GetRawText() == value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            ? value : throw ServerHostOptions.Failure();
}

internal sealed record ChatHostProfiles(ChatApiOptions Options, IReadOnlyDictionary<string, string> Credentials)
{
    public override string ToString() => nameof(ChatHostProfiles);
}

/// <summary>Owns the two separately credentialed lazy chat pools; stores only borrow them.</summary>
internal sealed class ChatHostDatabases(string readerConnection, string writerConnection) : IAsyncDisposable
{
    internal PostgresChatDatabase Reader { get; } = new(readerConnection, ChatDatabaseProfile.Reader);
    internal PostgresChatDatabase Writer { get; } = new(writerConnection, ChatDatabaseProfile.Writer);

    public async ValueTask DisposeAsync()
    {
        try { await Reader.DisposeAsync(); }
        finally { await Writer.DisposeAsync(); }
    }

    public override string ToString() => nameof(ChatHostDatabases);
}

/// <summary>
/// Resolves a configured credential reference by reading its protected file at dispatch time, so rotation
/// needs no restart. Any refusal is categorical; the secret never enters configuration, logs or errors.
/// </summary>
internal sealed class ChatHostCredentialResolver(IReadOnlyDictionary<string, string> files) : IAiCredentialResolver
{
    public ValueTask<AiCredentialResult> ResolveAsync(string reference, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (reference is null || !files.TryGetValue(reference, out var path))
            return ValueTask.FromResult<AiCredentialResult>(CredentialUnavailable.Instance);
        try
        {
            var secret = HostPrivateFiles.ReadText(path, ChatHostConfiguration.CredentialFileBytes, removeTerminalLf: true);
            return ValueTask.FromResult<AiCredentialResult>(CredentialFound.Create(AiBearerSecret.CopyFrom(secret)));
        }
        catch (Exception error) when (SessionHostResources.Nonfatal(error))
        {
            return ValueTask.FromResult<AiCredentialResult>(CredentialUnavailable.Instance);
        }
    }

    public override string ToString() => nameof(ChatHostCredentialResolver);
}
