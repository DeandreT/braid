using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Weft.Ai;
using Weft.Api.Catalog;
using Weft.Api.Chat;
using Weft.Api.Chat.Workspace;
using Weft.Api.Help;
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
        bool catalogEnabled, CatalogRevisionReportOptions reports, bool session = false)
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
        var readerConnection = Connection("WEFT_CHAT_READER_CONNECTION_STRING");
        var writerConnection = Connection("WEFT_CHAT_WRITER_CONNECTION_STRING");
        if (readerConnection == writerConnection) throw ServerHostOptions.Failure();
        services.AddSingleton(_ => new ChatHostDatabases(readerConnection, writerConnection));
        services.AddSingleton(provider =>
        {
            var databases = provider.GetRequiredService<ChatHostDatabases>();
            var reader = new PostgresChatStore(databases.Reader);
            var writer = new PostgresChatStore(databases.Writer);
            return new ChatPersistencePorts(reader, reader, writer, writer);
        });
        services.AddSingleton<IChatMappingWriteJournal>(provider => new PostgresChatStore(provider.GetRequiredService<ChatHostDatabases>().Writer));
        services.AddSingleton<ChatMappingWriteRecovery>();
        // Keyless profiles read the Container Apps managed-identity endpoint, admitted only when a credential uses it, or
        // the fixed Instance Metadata Service address.
        var tokenEndpoint = configuration.ManagedIdentities.Values.Any(identity => identity.Source == ChatIdentitySource.ContainerApps)
            ? ManagedIdentityCredentialResolver.ReadEndpoint(read) : null;
        services.AddSingleton<IAiCredentialResolver>(_ => new ChatHostCredentialResolver(configuration.Credentials,
            configuration.ManagedIdentities.Count == 0 ? null
                : new ManagedIdentityCredentialResolver(configuration.ManagedIdentities, tokenEndpoint, read, TimeProvider.System)));
        // One shared read-only tool service bounds its own concurrent report reads and citation checks for the whole host.
        services.AddSingleton(provider =>
        {
            var tools = new MappingToolService(provider.GetRequiredService<IRevisionReportMetadataReader>(),
                provider.GetRequiredService<IRevisionReportReader>());
            return new ChatToolRegistry(MappingToolRegistry.Definitions, tools, tools);
        });
        services.TryAddSingleton<IUserGuideReader>(_ => UserGuide.Embedded);
        services.TryAddSingleton<IWorkspaceUserGuide>(provider => new WorkspaceUserGuide(provider.GetRequiredService<IUserGuideReader>()));
        // Payload data reaches a model only through the restricted grant reader; without its connection no payload tool exists.
        if (OptionalConnection("WEFT_CHAT_PAYLOAD_PERMISSION_CONNECTION_STRING") is { } payloadConnection)
        {
            if (payloadConnection == readerConnection || payloadConnection == writerConnection) throw ServerHostOptions.Failure();
            services.AddSingleton(_ => new PostgresPayloadPermissionDatabase(payloadConnection));
            services.AddSingleton(provider => new PostgresProjectPayloadPermissionReader(provider.GetRequiredService<PostgresPayloadPermissionDatabase>()));
            services.AddSingleton<IProjectPayloadPermissionReader>(provider => provider.GetRequiredService<PostgresProjectPayloadPermissionReader>());
            services.AddSingleton<IProjectPayloadGrantPresence>(provider => provider.GetRequiredService<PostgresProjectPayloadPermissionReader>());
        }
        // Workspace conversations read the families this host serves, as the signed-in user, through their own routes' reads.
        services.AddSingleton(provider =>
        {
            var guide = provider.GetService<IWorkspaceUserGuide>();
            return new WorkspaceToolService(provider, configuration.Options, new WorkspaceToolFeatures(
                provider.GetService<CatalogPreviewOptions>()?.Enabled == true,
                provider.GetService<CatalogRegressionCaseOptions>()?.Enabled == true,
                provider.GetService<CatalogBuildOptions>()?.Enabled == true,
                guide is not null, provider.GetService<MappingDirectEditService>() is not null), guide);
        });
        services.AddWeftChat(configuration.Options);

        string Connection(string name) => session
            ? SessionHostResources.ReadProtectedConnection(read, name) : ServerHostOptions.Required(read, name);
        string? OptionalConnection(string name) => session
            ? read(name) is null && read(name + "_FILE") is null ? null : SessionHostResources.ReadProtectedConnection(read, name)
            : read(name) is null ? null : ServerHostOptions.Required(read, name);
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
            var identities = new Dictionary<string, ChatManagedIdentity>(StringComparer.Ordinal);
            foreach (var item in Array(root["credentials"], 16))
            {
                // A credential is either a private bearer file or a managed-identity token, never both.
                var credential = Object(item, ["reference", "file", "managedIdentity"], ["reference"]);
                var reference = ChatBounds.Identifier(String(credential["reference"]));
                if (credential.ContainsKey("file") == credential.ContainsKey("managedIdentity") || credentials.ContainsKey(reference) ||
                    identities.ContainsKey(reference))
                    throw ServerHostOptions.Failure();
                if (credential.TryGetValue("managedIdentity", out var managed))
                {
                    var identity = Object(managed, ["clientId", "resource", "source"], []);
                    // The issuing endpoint is always explicit configuration; there is no detection or fallback between them.
                    var source = !identity.TryGetValue("source", out var origin) ? ChatIdentitySource.ContainerApps : String(origin) switch
                    {
                        "containerApps" => ChatIdentitySource.ContainerApps,
                        "instanceMetadata" => ChatIdentitySource.InstanceMetadata,
                        _ => throw ServerHostOptions.Failure()
                    };
                    var resource = !identity.TryGetValue("resource", out var named) ? ChatTokenResource.AzureAi : String(named) switch
                    {
                        "azureAi" => ChatTokenResource.AzureAi,
                        "azureDatabricks" => ChatTokenResource.AzureDatabricks,
                        _ => throw ServerHostOptions.Failure()
                    };
                    identities.Add(reference, new(identity.TryGetValue("clientId", out var clientId) ? ClientId(String(clientId)) : null, resource, source));
                    continue;
                }
                var file = String(credential["file"]);
                if (!Path.IsPathFullyQualified(file) || file != Path.GetFullPath(file)) throw ServerHostOptions.Failure();
                credentials.Add(reference, file);
            }
            var profiles = Array(root["profiles"], 16).Select(Profile).ToArray();
            var used = profiles.Select(profile => profile.Spec.CredentialReference).OfType<string>().ToHashSet(StringComparer.Ordinal);
            // Every bearer reference resolves, and no unused secret file or identity is admitted.
            if (!used.SetEquals(credentials.Keys.Concat(identities.Keys))) throw ServerHostOptions.Failure();
            // A Databricks credential goes only to Databricks profiles and another credential never does: a file is never
            // shared across that line, and a managed-identity token serves only the service it was issued for.
            var databricks = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var profile in profiles)
            {
                if (profile.Spec.CredentialReference is not { } reference) continue;
                var target = profile.Spec.Dialect == AiDialect.DatabricksChatCompletionsSseV1;
                if ((databricks.TryGetValue(reference, out var seen) && seen != target) ||
                    (identities.TryGetValue(reference, out var identity) && (identity.Resource == ChatTokenResource.AzureDatabricks) != target))
                    throw ServerHostOptions.Failure();
                databricks[reference] = target;
            }
            return new(new ChatApiOptions(profiles, TimeSpan.FromDays(retention), TimeSpan.FromSeconds(seconds), tokens), credentials,
                identities);
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
            "databricksChatCompletionsSse" => AiDialect.DatabricksChatCompletionsSseV1,
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
        var policy = Object(item["metadataPolicy"], ["id", "version", "mappingMetadataAllowed", "payloadsAllowed"], ["id", "version", "mappingMetadataAllowed"]);
        string? reference = item["credentialReference"].ValueKind == JsonValueKind.Null ? null : ChatBounds.Identifier(String(item["credentialReference"]));
        var endpoint = new Uri(String(item["endpoint"]), UriKind.Absolute);
        var spec = AiProfileSpec.Create(AiProfileKey.Create(String(item["id"]), Int(item["version"])), dialect, String(item["model"]),
            endpoint, item.TryGetValue("loopbackFixture", out var fixture) && Bool(fixture),
            reference is null ? AiAuthentication.None : AiAuthentication.Bearer, reference, capabilities,
            String(policy["id"]), Int(policy["version"]), Bool(policy["mappingMetadataAllowed"]),
            item.TryGetValue("requestThinkFalse", out var think) && Bool(think),
            policy.TryGetValue("payloadsAllowed", out var payloads) && Bool(payloads));
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

    private static Guid ClientId(string text) =>
        Guid.TryParseExact(text, "D", out var id) && id != Guid.Empty && text == id.ToString("D") ? id : throw ServerHostOptions.Failure();

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

/// <summary>The admitted profiles; each bearer reference names a private file or a managed identity and its token resource.</summary>
internal sealed record ChatHostProfiles(ChatApiOptions Options, IReadOnlyDictionary<string, string> Credentials,
    IReadOnlyDictionary<string, ChatManagedIdentity> ManagedIdentities)
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
/// needs no restart, or through the managed-identity resolver that owns its token. Any refusal is categorical;
/// the secret never enters configuration, logs or errors.
/// </summary>
internal sealed class ChatHostCredentialResolver(IReadOnlyDictionary<string, string> files,
    ManagedIdentityCredentialResolver? tokens = null) : IAiCredentialResolver, IDisposable
{
    public ValueTask<AiCredentialResult> ResolveAsync(string reference, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (reference is not null && tokens is not null && tokens.Resolves(reference)) return tokens.ResolveAsync(reference, ct);
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

    public void Dispose() => tokens?.Dispose();

    public override string ToString() => nameof(ChatHostCredentialResolver);
}
