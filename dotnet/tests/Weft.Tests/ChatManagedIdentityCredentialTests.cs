using System.Text;
using System.Text.Json;
using Weft.Ai;
using Weft.Server;

namespace Weft.Tests;

/// <summary>Keyless Azure OpenAI credentials against an owned stub of the Container Apps managed-identity endpoint.</summary>
public sealed class ChatManagedIdentityCredentialTests
{
    private const string Header = "PRIVATE-identity-header";
    private const string Reference = "azure-openai";
    private const string ClientId = "11111111-2222-3333-4444-555555555555";
    private static readonly DateTimeOffset Start = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Target = "/msi/token?resource=https%3A%2F%2Fai.azure.com&api-version=2019-08-01&client_id=" + ClientId;

    private static string Token(int ordinal) => "PRIVATE-entra-token-" + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string Seconds(DateTimeOffset time) => time.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static byte[] TokenResponse(string token, DateTimeOffset expires, bool numeric = false) => Encoding.UTF8.GetBytes(
        "{\"access_token\":" + JsonSerializer.Serialize(token) + ",\"expires_on\":" + (numeric ? Seconds(expires) : "\"" + Seconds(expires) + "\"") +
        ",\"resource\":\"https://ai.azure.com\",\"token_type\":\"Bearer\",\"client_id\":\"" + ClientId + "\"}");

    private static ManagedIdentityCredentialResolver Resolver(AiHttpFixture server, TimeProvider clock, Guid? clientId = null,
        Func<string, string?>? read = null) =>
        new(new Dictionary<string, ChatManagedIdentity> { [Reference] = new(clientId ?? Guid.Parse(ClientId), ChatTokenResource.AzureAi) }, server.Endpoint("/msi/token"),
            read ?? (name => name == ManagedIdentityCredentialResolver.HeaderSetting ? Header : null), clock);

    private static async Task<string?> Resolve(IAiCredentialResolver resolver, string reference = Reference)
    {
        var result = await resolver.ResolveAsync(reference, CancellationToken.None);
        if (result is not CredentialFound found) return null;
        using var secret = found.Secret;
        return secret.HeaderValue();
    }

    [Fact]
    public async Task Tokens_are_requested_with_the_platform_header_and_reused_until_their_bounded_refresh()
    {
        var clock = new Clock(Start);
        var responses = new Queue<byte[]>([TokenResponse(Token(1), Start.AddHours(24)), TokenResponse(Token(2), Start.AddHours(25), numeric: true)]);
        await using var server = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("application/json; charset=utf-8", responses.Dequeue(), ct));
        using var resolver = Resolver(server, clock);

        var first = Assert.IsType<CredentialFound>(await resolver.ResolveAsync(Reference, CancellationToken.None));
        var second = Assert.IsType<CredentialFound>(await resolver.ResolveAsync(Reference, CancellationToken.None));
        Assert.NotSame(first.Secret, second.Secret);
        first.Secret.Dispose();
        Assert.Equal(Token(1), second.Secret.HeaderValue());
        second.Secret.Dispose();
        Assert.Equal(nameof(ManagedIdentityCredentialResolver), resolver.ToString());
        var request = Assert.Single(server.Requests);
        Assert.True(request.IsGetAt(Target), "Token request changed.");
        Assert.True(request.HeaderIs("X-IDENTITY-HEADER", Header), "Platform header was not sent.");

        clock.Now = Start.AddMinutes(59);
        Assert.Equal(Token(1), await Resolve(resolver));
        Assert.Equal(1, server.RequestCount);
        // A day-long token is still refreshed after an hour.
        clock.Now = Start.AddMinutes(61);
        Assert.Equal(Token(2), await Resolve(resolver));
        Assert.Equal(2, server.RequestCount);
    }

    [Fact]
    public async Task Refresh_starts_before_expiry_and_a_failed_refresh_serves_the_current_token_until_it_nearly_expires()
    {
        var clock = new Clock(Start);
        var responses = new Queue<(int Status, byte[] Body)>([(200, TokenResponse(Token(1), Start.AddMinutes(30))), (500, "{}"u8.ToArray()),
            (500, "{}"u8.ToArray()), (200, TokenResponse(Token(2), Start.AddMinutes(90)))]);
        await using var server = new AiHttpFixture((exchange, ct) =>
        {
            var (status, body) = responses.Dequeue();
            return exchange.RespondAsync("application/json", body, ct, status: status);
        });
        using var resolver = Resolver(server, clock);

        Assert.Equal(Token(1), await Resolve(resolver));
        clock.Now = Start.AddMinutes(24);
        Assert.Equal(Token(1), await Resolve(resolver));
        Assert.Equal(1, server.RequestCount);
        clock.Now = Start.AddMinutes(26);
        Assert.Equal(Token(1), await Resolve(resolver));
        Assert.Equal(2, server.RequestCount);
        clock.Now = Start.AddMinutes(29).AddSeconds(1);
        Assert.Null(await Resolve(resolver));
        Assert.Equal(3, server.RequestCount);
        Assert.Equal(Token(2), await Resolve(resolver));
        Assert.Equal(4, server.RequestCount);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("media-type")]
    [InlineData("malformed")]
    [InlineData("missing-token")]
    [InlineData("missing-expiry")]
    [InlineData("duplicate-token")]
    [InlineData("token-type")]
    [InlineData("expiring")]
    [InlineData("fraction")]
    [InlineData("whitespace-token")]
    [InlineData("oversized")]
    [InlineData("trailing")]
    public async Task Invalid_token_responses_are_unavailable_and_never_cached(string variant)
    {
        var clock = new Clock(Start);
        var expires = Seconds(Start.AddHours(1));
        var body = variant switch
        {
            "malformed" => "{\"access_token\":",
            "missing-token" => "{\"expires_on\":\"" + expires + "\"}",
            "missing-expiry" => "{\"access_token\":\"PRIVATE-token\"}",
            "duplicate-token" => "{\"access_token\":\"PRIVATE-a\",\"access_token\":\"PRIVATE-b\",\"expires_on\":\"" + expires + "\"}",
            "token-type" => "{\"access_token\":\"PRIVATE-token\",\"expires_on\":\"" + expires + "\",\"token_type\":\"pop\"}",
            "expiring" => "{\"access_token\":\"PRIVATE-token\",\"expires_on\":\"" + Seconds(Start.AddMinutes(4)) + "\"}",
            "fraction" => "{\"access_token\":\"PRIVATE-token\",\"expires_on\":" + expires + ".5}",
            "whitespace-token" => "{\"access_token\":\"PRIVATE token\",\"expires_on\":\"" + expires + "\"}",
            "oversized" => "{\"access_token\":\"PRIVATE-token\",\"expires_on\":\"" + expires + "\",\"padding\":\"" + new string('x', 16_384) + "\"}",
            "trailing" => "{\"access_token\":\"PRIVATE-token\",\"expires_on\":\"" + expires + "\"}{}",
            _ => "{\"access_token\":\"PRIVATE-token\",\"expires_on\":\"" + expires + "\"}"
        };
        await using var server = new AiHttpFixture((exchange, ct) => exchange.RespondAsync(variant == "media-type" ? "text/plain" : "application/json",
            Encoding.UTF8.GetBytes(body), ct, status: variant == "status" ? 403 : 200));
        using var resolver = Resolver(server, clock);
        Assert.IsType<CredentialUnavailable>(await resolver.ResolveAsync(Reference, CancellationToken.None));
        Assert.IsType<CredentialUnavailable>(await resolver.ResolveAsync(Reference, CancellationToken.None));
        Assert.Equal(2, server.RequestCount);
    }

    [Fact]
    public async Task Unknown_references_and_a_missing_platform_header_make_no_request()
    {
        var clock = new Clock(Start);
        await using var server = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("application/json", TokenResponse(Token(1), Start.AddHours(1)), ct));
        using (var resolver = Resolver(server, clock, read: _ => null))
            Assert.IsType<CredentialUnavailable>(await resolver.ResolveAsync(Reference, CancellationToken.None));
        using (var resolver = Resolver(server, clock))
        {
            Assert.IsType<CredentialUnavailable>(await resolver.ResolveAsync("other-reference", CancellationToken.None));
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await resolver.ResolveAsync(Reference, cancelled.Token));
        }
        Assert.Equal(0, server.RequestCount);
    }

    [Fact]
    public async Task Concurrent_dispatches_share_one_token_request_and_a_system_identity_names_no_client()
    {
        var clock = new Clock(Start);
        var release = AiHttpFixture.NewSignal();
        await using var server = new AiHttpFixture(async (exchange, ct) =>
        {
            await release.Task.WaitAsync(ct);
            await exchange.RespondAsync("application/json", TokenResponse(Token(1), Start.AddHours(1)), ct);
        });
        using var resolver = new ManagedIdentityCredentialResolver(new Dictionary<string, ChatManagedIdentity>
        { [Reference] = new(null, ChatTokenResource.AzureAi), ["second"] = new(null, ChatTokenResource.AzureAi) },
            server.Endpoint("/msi/token"), name => name == ManagedIdentityCredentialResolver.HeaderSetting ? Header : null, clock);
        var pending = Enumerable.Range(0, 4).Select(index => Resolve(resolver, index % 2 == 0 ? Reference : "second")).ToArray();
        await server.WaitForRequestsAsync(1);
        release.TrySetResult();
        Assert.All(await Task.WhenAll(pending), token => Assert.Equal(Token(1), token));
        var request = Assert.Single(server.Requests);
        Assert.True(request.IsGetAt("/msi/token?resource=https%3A%2F%2Fai.azure.com&api-version=2019-08-01"), "System identity request changed.");
    }

    [Theory]
    [InlineData("http://localhost:42356/msi/token", true)]
    [InlineData("http://127.0.0.1:41000/msi/token", true)]
    [InlineData("http://[::1]:41000/msi/token", true)]
    [InlineData("https://localhost:42356/msi/token", false)]
    [InlineData("http://10.0.0.4:42356/msi/token", false)]
    [InlineData("http://identity.example/msi/token", false)]
    [InlineData("http://localhost:42356/msi/token?resource=other", false)]
    [InlineData("http://user:secret@localhost:42356/msi/token", false)]
    [InlineData("msi/token", false)]
    [InlineData(null, false)]
    public void Only_a_loopback_platform_endpoint_with_its_header_is_admitted(string? endpoint, bool admitted)
    {
        string? Read(string name) => name switch
        {
            ManagedIdentityCredentialResolver.EndpointSetting => endpoint,
            ManagedIdentityCredentialResolver.HeaderSetting => Header,
            _ => null
        };
        if (admitted)
        {
            Assert.Equal(new Uri(endpoint!), ManagedIdentityCredentialResolver.ReadEndpoint(Read));
            Assert.Throws<InvalidOperationException>(() => ManagedIdentityCredentialResolver.ReadEndpoint(name =>
                name == ManagedIdentityCredentialResolver.EndpointSetting ? endpoint : null));
        }
        else Assert.Throws<InvalidOperationException>(() => ManagedIdentityCredentialResolver.ReadEndpoint(Read));
    }

    [Fact]
    public async Task Keyless_rounds_send_the_managed_identity_token_to_azure_openai_and_nothing_names_it()
    {
        var clock = new Clock(Start);
        await using var identity = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("application/json", TokenResponse(Token(7), Start.AddHours(1)), ct));
        await using var model = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("text/event-stream",
            AiHttpFixture.Utf8(AiProviderTests.AzureDefaultFilterWire), ct));
        using var credentials = new ChatHostCredentialResolver(new Dictionary<string, string>(), Resolver(identity, clock));
        var key = AiProfileKey.Create("azure", 1);
        var profile = AiProfileSpec.Create(key, AiDialect.ChatCompletionsSseV1, "deployment", model.Endpoint("/openai/v1/chat/completions"), true,
            AiAuthentication.Bearer, Reference, AiCapability.Text, "fixture.metadata", 1, true, false);
        await using var client = new AiRoundClient(AiProfileCatalog.Create([profile]), credentials, TimeProvider.System);
        await using var turn = Assert.IsType<TurnOpened>(client.OpenTurn(key, AiTurnLimits.Default, CancellationToken.None)).Turn;
        var opened = Assert.IsType<RoundOpened>(await client.OpenRoundAsync(turn,
            AiRoundRequest.Create(Guid.NewGuid(), [AiUserMessage.Create("Where does invoice.total come from?")], [], 128), CancellationToken.None));
        await using var stream = opened.Stream;
        var events = new List<AiRoundEvent>();
        await foreach (var value in stream.ReadAllAsync(CancellationToken.None)) events.Add(value);
        Assert.Equal(AiFinishReason.Stop, Assert.IsType<AiRoundFinished>(events[^1]).Reason);
        Assert.True(Assert.Single(model.Requests).HeaderIs("Authorization", "Bearer " + Token(7)), "The managed-identity token was not sent.");
        foreach (var text in new[] { credentials.ToString(), stream.ToString() })
            Assert.DoesNotContain("PRIVATE", text, StringComparison.Ordinal);
    }

    private const string DatabricksResource = "2ff814a6-3304-4ab8-85cb-cd0e6f879c1d";

    private static ManagedIdentityCredentialResolver DatabricksResolver(AiHttpFixture server, TimeProvider clock, Guid? clientId = null) =>
        new(new Dictionary<string, ChatManagedIdentity> { ["databricks"] = new(clientId, ChatTokenResource.AzureDatabricks) },
            server.Endpoint("/msi/token"), name => name == ManagedIdentityCredentialResolver.HeaderSetting ? Header : null, clock);

    [Fact]
    public async Task Databricks_tokens_are_requested_for_the_azure_databricks_resource_cached_and_refreshed_before_expiry()
    {
        var clock = new Clock(Start);
        var responses = new Queue<(int Status, byte[] Body)>([(200, TokenResponse(Token(1), Start.AddMinutes(60))),
            (401, "{}"u8.ToArray()), (200, TokenResponse(Token(2), Start.AddMinutes(120)))]);
        await using var server = new AiHttpFixture((exchange, ct) =>
        {
            var (status, body) = responses.Dequeue();
            return exchange.RespondAsync("application/json", body, ct, status: status);
        });
        using var resolver = DatabricksResolver(server, clock, Guid.Parse(ClientId));

        var first = Assert.IsType<CredentialFound>(await resolver.ResolveAsync("databricks", CancellationToken.None));
        var second = Assert.IsType<CredentialFound>(await resolver.ResolveAsync("databricks", CancellationToken.None));
        Assert.NotSame(first.Secret, second.Secret);
        first.Secret.Dispose();
        Assert.Equal(Token(1), second.Secret.HeaderValue());
        second.Secret.Dispose();
        Assert.True(Assert.Single(server.Requests).IsGetAt("/msi/token?resource=" + DatabricksResource + "&api-version=2019-08-01&client_id=" + ClientId),
            "Databricks token request changed.");

        // Reused until five minutes before expiry; a failed refresh keeps the current token until a minute before it expires.
        clock.Now = Start.AddMinutes(54);
        Assert.Equal(Token(1), await Resolve(resolver, "databricks"));
        Assert.Equal(1, server.RequestCount);
        clock.Now = Start.AddMinutes(56);
        Assert.Equal(Token(1), await Resolve(resolver, "databricks"));
        Assert.Equal(2, server.RequestCount);
        Assert.Equal(Token(2), await Resolve(resolver, "databricks"));
        Assert.Equal(3, server.RequestCount);
        foreach (var text in new[] { resolver.ToString(), new ChatManagedIdentity(Guid.Parse(ClientId), ChatTokenResource.AzureDatabricks).ToString() })
            Assert.DoesNotContain("PRIVATE", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(500)]
    public async Task Databricks_token_failures_are_unavailable(int status)
    {
        var clock = new Clock(Start);
        await using var server = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("application/json",
            Encoding.UTF8.GetBytes("{\"error\":\"PRIVATE-detail\"}"), ct, status: status));
        using var resolver = DatabricksResolver(server, clock);
        var result = await resolver.ResolveAsync("databricks", CancellationToken.None);
        Assert.IsType<CredentialUnavailable>(result);
        Assert.Equal(nameof(AiCredentialResult), result.ToString());
        Assert.True(Assert.Single(server.Requests).IsGetAt("/msi/token?resource=" + DatabricksResource + "&api-version=2019-08-01"),
            "System identity Databricks request changed.");
    }

    [Fact]
    public async Task One_identity_holds_a_separate_token_for_each_resource()
    {
        var clock = new Clock(Start);
        await using var server = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("application/json",
            TokenResponse(exchange.Request.IsGetAt("/msi/token?resource=" + DatabricksResource + "&api-version=2019-08-01&client_id=" + ClientId)
                ? Token(2) : Token(1), Start.AddHours(1)), ct));
        using var resolver = new ManagedIdentityCredentialResolver(new Dictionary<string, ChatManagedIdentity>
        {
            ["azure-openai"] = new(Guid.Parse(ClientId), ChatTokenResource.AzureAi),
            ["databricks"] = new(Guid.Parse(ClientId), ChatTokenResource.AzureDatabricks),
            ["databricks-again"] = new(Guid.Parse(ClientId), ChatTokenResource.AzureDatabricks)
        }, server.Endpoint("/msi/token"), name => name == ManagedIdentityCredentialResolver.HeaderSetting ? Header : null, clock);
        Assert.Equal(Token(1), await Resolve(resolver, "azure-openai"));
        Assert.Equal(Token(2), await Resolve(resolver, "databricks"));
        Assert.Equal(Token(2), await Resolve(resolver, "databricks-again"));
        Assert.Equal(2, server.RequestCount);
    }

    [Fact]
    public void Databricks_profiles_parse_with_their_own_token_resource_and_never_share_one_with_other_providers()
    {
        static string Document(string dialect, string endpoint, string identity) =>
            "{\"version\":1,\"retentionDays\":30,\"profiles\":[{\"id\":\"databricks\",\"version\":1,\"displayName\":\"Databricks\"," +
            "\"provider\":\"azure-databricks\",\"dialect\":\"" + dialect + "\",\"model\":\"weft-chat\",\"endpoint\":\"" + endpoint + "\"," +
            "\"credentialReference\":\"databricks\",\"capabilities\":[\"text\",\"functionTools\"]," +
            "\"metadataPolicy\":{\"id\":\"mapping-metadata\",\"version\":1,\"mappingMetadataAllowed\":true}}]," +
            "\"credentials\":[{\"reference\":\"databricks\",\"managedIdentity\":" + identity + "}]}";
        const string serving = "https://adb-1234567890123456.7.azuredatabricks.net/serving-endpoints/chat/completions";
        const string azure = "https://resource.openai.azure.com/openai/v1/chat/completions";
        var databricks = "{\"clientId\":\"" + ClientId + "\",\"resource\":\"azureDatabricks\"}";

        var parsed = ChatHostConfiguration.Parse(Document("databricksChatCompletionsSse", serving, databricks));
        Assert.Equal(new ChatManagedIdentity(Guid.Parse(ClientId), ChatTokenResource.AzureDatabricks), parsed.ManagedIdentities["databricks"]);
        var profile = Assert.Single(parsed.Options.Profiles).Spec;
        Assert.Equal(AiDialect.DatabricksChatCompletionsSseV1, profile.Dialect);
        Assert.Equal(new Uri(serving), profile.Endpoint);
        Assert.Null(ChatHostConfiguration.Parse(Document("databricksChatCompletionsSse", serving, "{\"resource\":\"azureDatabricks\"}"))
            .ManagedIdentities["databricks"].ClientId);
        Assert.Equal(ChatTokenResource.AzureAi, ChatHostConfiguration.Parse(Document("chatCompletionsSse", azure,
            "{\"resource\":\"azureAi\"}")).ManagedIdentities["databricks"].Resource);

        foreach (var invalid in new[]
        {
            Document("databricksChatCompletionsSse", serving, "{}"),
            Document("databricksChatCompletionsSse", serving, "{\"resource\":\"azureAi\"}"),
            Document("chatCompletionsSse", azure, databricks),
            Document("databricksChatCompletionsSse", serving, "{\"resource\":\"2ff814a6-3304-4ab8-85cb-cd0e6f879c1d\"}"),
            Document("databricksChatCompletionsSse", serving, "{\"resource\":null}"),
            Document("databricksChatCompletionsSse", azure, databricks),
            Document("chatCompletionsSse", serving, "{}"),
            Document("databricksChatCompletionsSse", "https://example.cloud.databricks.com/serving-endpoints/chat/completions", databricks),
            Document("databricksChatCompletionsSse", "https://adb-1234567890123456.7.azuredatabricks.net/ai-gateway/mlflow/v1/chat/completions", databricks)
        })
            Assert.Throws<InvalidOperationException>(() => ChatHostConfiguration.Parse(invalid));
    }

    [Fact]
    public void A_file_credential_never_serves_both_databricks_and_another_provider()
    {
        static string Profile(string id, string dialect, string endpoint, string reference) =>
            "{\"id\":\"" + id + "\",\"version\":1,\"displayName\":\"" + id + "\",\"provider\":\"" + id + "\",\"dialect\":\"" + dialect + "\"," +
            "\"model\":\"weft-chat\",\"endpoint\":\"" + endpoint + "\",\"credentialReference\":\"" + reference + "\"," +
            "\"capabilities\":[\"text\"],\"metadataPolicy\":{\"id\":\"mapping-metadata\",\"version\":1,\"mappingMetadataAllowed\":true}}";
        static string Document(string profiles, string credentials) =>
            "{\"version\":1,\"retentionDays\":30,\"profiles\":[" + profiles + "],\"credentials\":[" + credentials + "]}";
        var file = Path.Combine(Path.GetTempPath(), "weft-never-read-credential");
        string Credential(string reference) => "{\"reference\":\"" + reference + "\",\"file\":" + JsonSerializer.Serialize(file) + "}";
        var databricks = Profile("databricks", "databricksChatCompletionsSse",
            "https://adb-1234567890123456.7.azuredatabricks.net/serving-endpoints/chat/completions", "shared");
        var other = Profile("hosted", "chatCompletionsSse", "https://models.example.net/v1/chat/completions", "shared");
        var secondDatabricks = Profile("databricks-two", "databricksChatCompletionsSse",
            "https://adb-1234567890123456.7.azuredatabricks.net/serving-endpoints/chat/completions", "shared");

        Assert.Throws<InvalidOperationException>(() => ChatHostConfiguration.Parse(Document(databricks + "," + other, Credential("shared"))));
        Assert.Throws<InvalidOperationException>(() => ChatHostConfiguration.Parse(Document(other + "," + databricks, Credential("shared"))));
        Assert.Equal(2, ChatHostConfiguration.Parse(Document(databricks + "," + secondDatabricks, Credential("shared"))).Options.Profiles.Count);
        Assert.Equal(2, ChatHostConfiguration.Parse(Document(databricks + "," + other.Replace("\"shared\"", "\"own\"", StringComparison.Ordinal),
            Credential("shared") + "," + Credential("own"))).Credentials.Count);
    }

    [Fact]
    public async Task Keyless_rounds_send_the_databricks_token_to_the_serving_endpoint()
    {
        var clock = new Clock(Start);
        await using var identity = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("application/json", TokenResponse(Token(9), Start.AddHours(1)), ct));
        await using var model = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("text/event-stream", AiHttpFixture.Utf8(
            "data: {\"object\":\"chat.completion.chunk\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]," +
            "\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":1,\"total_tokens\":6}}\n\ndata: [DONE]\n\n"), ct));
        using var credentials = new ChatHostCredentialResolver(new Dictionary<string, string>(), DatabricksResolver(identity, clock));
        var key = AiProfileKey.Create("databricks", 1);
        var profile = AiProfileSpec.Create(key, AiDialect.DatabricksChatCompletionsSseV1, "weft-chat", model.Endpoint("/serving-endpoints/chat/completions"), true,
            AiAuthentication.Bearer, "databricks", AiCapability.Text, "fixture.metadata", 1, true, false);
        await using var client = new AiRoundClient(AiProfileCatalog.Create([profile]), credentials, TimeProvider.System);
        await using var turn = Assert.IsType<TurnOpened>(client.OpenTurn(key, AiTurnLimits.Default, CancellationToken.None)).Turn;
        var opened = Assert.IsType<RoundOpened>(await client.OpenRoundAsync(turn,
            AiRoundRequest.Create(Guid.NewGuid(), [AiUserMessage.Create("Where does invoice.total come from?")], [], 128), CancellationToken.None));
        await using var stream = opened.Stream;
        var events = new List<AiRoundEvent>();
        await foreach (var value in stream.ReadAllAsync(CancellationToken.None)) events.Add(value);
        Assert.Equal(6, Assert.IsType<AiRoundFinished>(events[^1]).Usage.TotalTokens);
        Assert.True(Assert.Single(model.Requests).HeaderIs("Authorization", "Bearer " + Token(9)), "The Databricks token was not sent.");
        Assert.True(Assert.Single(identity.Requests).IsGetAt("/msi/token?resource=" + DatabricksResource + "&api-version=2019-08-01"),
            "The Databricks token was requested for another resource.");
        foreach (var text in new[] { credentials.ToString(), stream.ToString() })
            Assert.DoesNotContain("PRIVATE", text, StringComparison.Ordinal);
    }

    private const string MetadataPath = "/metadata/identity/oauth2/token";

    private static string MetadataTarget(string resource, string? clientId = ClientId) =>
        MetadataPath + "?api-version=2018-02-01&resource=" + Uri.EscapeDataString(resource) + (clientId is null ? "" : "&client_id=" + clientId);

    // The documented IMDS response: string expires_in and expires_on, and fields Weft ignores
    // (https://learn.microsoft.com/entra/identity/managed-identities-azure-resources/how-to-use-vm-token).
    private static byte[] MetadataResponse(string token, DateTimeOffset expires, DateTimeOffset now, bool absolute = true) => Encoding.UTF8.GetBytes(
        "{\"access_token\":" + JsonSerializer.Serialize(token) + ",\"refresh_token\":\"\",\"expires_in\":\"" +
        ((long)(expires - now).TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture) + "\"" +
        (absolute ? ",\"expires_on\":\"" + Seconds(expires) + "\"" : "") + ",\"not_before\":\"" + Seconds(now) +
        "\",\"resource\":\"" + DatabricksResource + "\",\"token_type\":\"Bearer\"}");

    private static ManagedIdentityCredentialResolver MetadataResolver(AiHttpFixture server, TimeProvider clock, Guid? clientId,
        List<TimeSpan>? waits = null, ChatTokenResource resource = ChatTokenResource.AzureDatabricks) =>
        new(new Dictionary<string, ChatManagedIdentity> { ["databricks"] = new(clientId, resource, ChatIdentitySource.InstanceMetadata) },
            null, _ => null, clock, instanceMetadata: server.Endpoint(MetadataPath),
            delay: (wait, _) => { waits?.Add(wait); return Task.CompletedTask; });

    [Fact]
    public async Task Instance_metadata_tokens_are_requested_with_the_metadata_header_cached_and_refreshed_before_expiry()
    {
        var clock = new Clock(Start);
        var responses = new Queue<byte[]>([MetadataResponse(Token(1), Start.AddMinutes(60), Start),
            MetadataResponse(Token(2), Start.AddMinutes(116), Start.AddMinutes(56))]);
        await using var server = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("application/json; charset=utf-8", responses.Dequeue(), ct));
        // No Container Apps endpoint or header is needed for this source.
        using var resolver = MetadataResolver(server, clock, Guid.Parse(ClientId));

        var first = Assert.IsType<CredentialFound>(await resolver.ResolveAsync("databricks", CancellationToken.None));
        var second = Assert.IsType<CredentialFound>(await resolver.ResolveAsync("databricks", CancellationToken.None));
        Assert.NotSame(first.Secret, second.Secret);
        first.Secret.Dispose();
        Assert.Equal(Token(1), second.Secret.HeaderValue());
        second.Secret.Dispose();
        var request = Assert.Single(server.Requests);
        Assert.True(request.IsGetAt(MetadataTarget(DatabricksResource)), "Instance metadata token request changed.");
        Assert.True(request.HeaderIs("Metadata", "true"), "The metadata header was not sent.");
        Assert.False(request.HasHeader("X-IDENTITY-HEADER"), "A Container Apps header reached the metadata service.");

        clock.Now = Start.AddMinutes(54);
        Assert.Equal(Token(1), await Resolve(resolver, "databricks"));
        Assert.Equal(1, server.RequestCount);
        clock.Now = Start.AddMinutes(56);
        Assert.Equal(Token(2), await Resolve(resolver, "databricks"));
        Assert.Equal(2, server.RequestCount);
        Assert.DoesNotContain("PRIVATE", resolver.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Instance_metadata_system_identity_names_no_client_and_relative_expiry_is_read()
    {
        var clock = new Clock(Start);
        await using var server = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("application/json",
            MetadataResponse(Token(3), Start.AddMinutes(30), Start, absolute: false), ct));
        using var resolver = MetadataResolver(server, clock, null, resource: ChatTokenResource.AzureAi);
        Assert.Equal(Token(3), await Resolve(resolver, "databricks"));
        Assert.True(Assert.Single(server.Requests).IsGetAt(MetadataTarget(AzureAiResource, null)), "System identity metadata request changed.");
        // expires_in alone: refreshed five minutes before the 30-minute lifetime ends.
        clock.Now = Start.AddMinutes(24);
        Assert.Equal(Token(3), await Resolve(resolver, "databricks"));
        Assert.Equal(1, server.RequestCount);
    }

    private const string AzureAiResource = "https://ai.azure.com";

    [Fact]
    public async Task Instance_metadata_cancellation_closes_the_owned_request_without_retry_and_releases_refresh()
    {
        var closed = AiHttpFixture.NewSignal();
        await using var server = new AiHttpFixture(async (exchange, ct) =>
        {
            if (exchange.Ordinal == 1)
            {
                await exchange.ObserveClientCloseAsync(ct);
                closed.TrySetResult();
            }
            else await exchange.RespondAsync("application/json", MetadataResponse(Token(8), Start.AddHours(1), Start), ct);
        });
        var waits = new List<TimeSpan>();
        using var resolver = MetadataResolver(server, new Clock(Start), null, waits);
        using var cancellation = new CancellationTokenSource();
        var pending = resolver.ResolveAsync("databricks", cancellation.Token).AsTask();
        try
        {
            await server.WaitForRequestsAsync(1);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)));
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, server.RequestCount);
            Assert.Empty(waits);
            Assert.Equal(Token(8), await Resolve(resolver, "databricks"));
            Assert.Equal(2, server.RequestCount);
        }
        finally { cancellation.Cancel(); }
    }

    [Fact]
    public async Task Instance_metadata_cancellation_during_backoff_prevents_the_next_request()
    {
        var waiting = AiHttpFixture.NewSignal();
        await using var server = new AiHttpFixture((exchange, ct) => exchange.Ordinal == 1
            ? exchange.RespondAsync("application/json", "{}"u8.ToArray(), ct, status: 503)
            : exchange.RespondAsync("application/json", MetadataResponse(Token(8), Start.AddHours(1), Start), ct));
        using var resolver = new ManagedIdentityCredentialResolver(new Dictionary<string, ChatManagedIdentity>
        { ["databricks"] = new(null, ChatTokenResource.AzureDatabricks, ChatIdentitySource.InstanceMetadata) }, null, _ => null, new Clock(Start),
            instanceMetadata: server.Endpoint(MetadataPath), delay: (_, ct) =>
            {
                waiting.TrySetResult();
                return Task.Delay(Timeout.InfiniteTimeSpan, ct);
            });
        using var cancellation = new CancellationTokenSource();
        var pending = resolver.ResolveAsync("databricks", cancellation.Token).AsTask();
        try
        {
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, server.RequestCount);
            Assert.Equal(Token(8), await Resolve(resolver, "databricks"));
            Assert.Equal(2, server.RequestCount);
        }
        finally { cancellation.Cancel(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Instance_metadata_transport_and_attempt_timeout_failures_use_bounded_retries(bool timeout)
    {
        var attempts = 0;
        var waits = new List<TimeSpan>();
        using var resolver = new ManagedIdentityCredentialResolver(new Dictionary<string, ChatManagedIdentity>
        { ["databricks"] = new(null, ChatTokenResource.AzureDatabricks, ChatIdentitySource.InstanceMetadata) }, null, _ => null, new Clock(Start),
            handler: new MetadataHandler((request, _) =>
            {
                Assert.Equal(ManagedIdentityCredentialResolver.InstanceMetadataEndpoint.AbsoluteUri + "?api-version=2018-02-01&resource=" + DatabricksResource,
                    request.RequestUri!.AbsoluteUri);
                if (++attempts < 3)
                {
                    if (timeout) throw new TaskCanceledException("PRIVATE attempt timeout");
                    throw new HttpRequestException("PRIVATE transport failure");
                }
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(MetadataResponse(Token(8), Start.AddHours(1), Start))
                    { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") } }
                });
            }), delay: (wait, _) => { waits.Add(wait); return Task.CompletedTask; });
        Assert.Equal(Token(8), await Resolve(resolver, "databricks"));
        Assert.Equal(3, attempts);
        Assert.Equal(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) }, waits);
    }

    [Fact]
    public async Task Instance_metadata_disposal_closes_refresh_and_never_serves_a_late_token()
    {
        var closed = AiHttpFixture.NewSignal();
        await using var server = new AiHttpFixture(async (exchange, ct) =>
        {
            await exchange.ObserveClientCloseAsync(ct);
            closed.TrySetResult();
            await exchange.RespondAsync("application/json", MetadataResponse(Token(8), Start.AddHours(1), Start), ct);
        });
        using var resolver = MetadataResolver(server, new Clock(Start), null);
        var pending = resolver.ResolveAsync("databricks", CancellationToken.None).AsTask();
        await server.WaitForRequestsAsync(1);
        resolver.Dispose();
        Assert.IsType<CredentialUnavailable>(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsType<CredentialUnavailable>(await resolver.ResolveAsync("databricks", CancellationToken.None));
        Assert.Equal(1, server.RequestCount);
    }

    [Theory]
    [InlineData(301)]
    [InlineData(307)]
    public async Task Instance_metadata_redirects_never_forward_metadata_headers(int status)
    {
        await using var sink = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("application/json", MetadataResponse(Token(8), Start.AddHours(1), Start), ct));
        await using var server = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("application/json", "{}"u8.ToArray(), ct,
            status: status, extra: "Location: " + sink.Endpoint("/private-target").AbsoluteUri + "\r\n"));
        var waits = new List<TimeSpan>();
        using var resolver = MetadataResolver(server, new Clock(Start), null, waits);
        Assert.IsType<CredentialUnavailable>(await resolver.ResolveAsync("databricks", CancellationToken.None));
        Assert.Equal(1, server.RequestCount);
        Assert.Equal(0, sink.ConnectionCount);
        Assert.Empty(waits);
    }

    private sealed class MetadataHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    [Theory]
    [InlineData(404)]
    [InlineData(410)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task Instance_metadata_transient_statuses_are_retried_with_backoff_then_succeed(int status)
    {
        var clock = new Clock(Start);
        var waits = new List<TimeSpan>();
        await using var server = new AiHttpFixture((exchange, ct) => exchange.Ordinal < 3
            ? exchange.RespondAsync("application/json", Encoding.UTF8.GetBytes("{\"error\":\"PRIVATE-detail\"}"), ct, status: status)
            : exchange.RespondAsync("application/json", MetadataResponse(Token(4), Start.AddHours(1), Start), ct));
        using var resolver = MetadataResolver(server, clock, Guid.Parse(ClientId), waits);
        Assert.Equal(Token(4), await Resolve(resolver, "databricks"));
        Assert.Equal(3, server.RequestCount);
        Assert.Equal(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) }, waits);
    }

    [Theory]
    [InlineData(410, 3)]
    [InlineData(429, 3)]
    [InlineData(500, 3)]
    [InlineData(400, 1)]
    [InlineData(401, 1)]
    [InlineData(403, 1)]
    public async Task Instance_metadata_failures_are_bounded_and_unavailable(int status, int requests)
    {
        var clock = new Clock(Start);
        var waits = new List<TimeSpan>();
        await using var server = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("application/json",
            Encoding.UTF8.GetBytes("{\"error\":\"invalid_request\",\"error_description\":\"PRIVATE-detail\"}"), ct, status: status));
        using var resolver = MetadataResolver(server, clock, Guid.Parse(ClientId), waits);
        var result = await resolver.ResolveAsync("databricks", CancellationToken.None);
        Assert.IsType<CredentialUnavailable>(result);
        Assert.Equal(nameof(AiCredentialResult), result.ToString());
        Assert.Equal(requests, server.RequestCount);
        Assert.Equal(requests - 1, waits.Count);
    }

    [Theory]
    [InlineData("oversized")]
    [InlineData("malformed")]
    [InlineData("media-type")]
    [InlineData("missing-expiry")]
    [InlineData("expiring")]
    [InlineData("token-type")]
    public async Task Instance_metadata_invalid_bodies_are_unavailable_and_not_retried(string variant)
    {
        var clock = new Clock(Start);
        var waits = new List<TimeSpan>();
        var body = variant switch
        {
            "oversized" => "{\"access_token\":\"PRIVATE-token\",\"expires_in\":\"3600\",\"padding\":\"" + new string('x', 16_384) + "\"}",
            "malformed" => "{\"access_token\":",
            "missing-expiry" => "{\"access_token\":\"PRIVATE-token\",\"token_type\":\"Bearer\"}",
            "expiring" => "{\"access_token\":\"PRIVATE-token\",\"expires_in\":\"240\"}",
            "token-type" => "{\"access_token\":\"PRIVATE-token\",\"expires_in\":\"3600\",\"token_type\":\"pop\"}",
            _ => "{\"access_token\":\"PRIVATE-token\",\"expires_in\":\"3600\"}"
        };
        await using var server = new AiHttpFixture((exchange, ct) => exchange.RespondAsync(variant == "media-type" ? "text/plain" : "application/json",
            Encoding.UTF8.GetBytes(body), ct));
        using var resolver = MetadataResolver(server, clock, Guid.Parse(ClientId), waits);
        Assert.IsType<CredentialUnavailable>(await resolver.ResolveAsync("databricks", CancellationToken.None));
        Assert.Equal(1, server.RequestCount);
        Assert.Empty(waits);
    }

    [Fact]
    public async Task Container_apps_responses_still_require_an_absolute_expiry()
    {
        var clock = new Clock(Start);
        await using var server = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("application/json",
            MetadataResponse(Token(5), Start.AddHours(1), Start, absolute: false), ct));
        using var resolver = Resolver(server, clock);
        Assert.IsType<CredentialUnavailable>(await resolver.ResolveAsync(Reference, CancellationToken.None));
        Assert.Equal(1, server.RequestCount);
    }

    [Fact]
    public async Task Sources_are_explicit_and_hold_separate_tokens_for_the_same_identity()
    {
        var clock = new Clock(Start);
        await using var apps = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("application/json", TokenResponse(Token(6), Start.AddHours(1)), ct));
        await using var metadata = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("application/json", MetadataResponse(Token(7), Start.AddHours(1), Start), ct));
        using var resolver = new ManagedIdentityCredentialResolver(new Dictionary<string, ChatManagedIdentity>
        {
            ["apps"] = new(Guid.Parse(ClientId), ChatTokenResource.AzureDatabricks),
            ["vm"] = new(Guid.Parse(ClientId), ChatTokenResource.AzureDatabricks, ChatIdentitySource.InstanceMetadata)
        }, apps.Endpoint("/msi/token"), name => name == ManagedIdentityCredentialResolver.HeaderSetting ? Header : null, clock,
            instanceMetadata: metadata.Endpoint(MetadataPath));
        Assert.Equal(Token(6), await Resolve(resolver, "apps"));
        Assert.Equal(Token(7), await Resolve(resolver, "vm"));
        Assert.True(Assert.Single(apps.Requests).IsGetAt("/msi/token?resource=" + DatabricksResource + "&api-version=2019-08-01&client_id=" + ClientId),
            "Container Apps request changed.");
        Assert.True(Assert.Single(metadata.Requests).IsGetAt(MetadataTarget(DatabricksResource)), "Instance metadata request changed.");
    }

    [Fact]
    public void The_metadata_address_is_fixed_and_container_apps_still_needs_its_endpoint()
    {
        var identities = new Dictionary<string, ChatManagedIdentity> { ["vm"] = new(null, ChatTokenResource.AzureAi, ChatIdentitySource.InstanceMetadata) };
        using (var production = new ManagedIdentityCredentialResolver(identities, null, _ => null, TimeProvider.System))
            Assert.True(production.Resolves("vm"));
        Assert.Equal(new Uri("http://169.254.169.254/metadata/identity/oauth2/token"), ManagedIdentityCredentialResolver.InstanceMetadataEndpoint);
        foreach (var substitute in new[] { "http://10.0.0.4/metadata/identity/oauth2/token", "https://127.0.0.1:4100/metadata/identity/oauth2/token",
            "http://127.0.0.1:4100/metadata/identity/oauth2/token?api-version=x", "http://169.254.169.254:8080/metadata/identity/oauth2/token" })
            Assert.Throws<InvalidOperationException>(() => new ManagedIdentityCredentialResolver(identities, null, _ => null, TimeProvider.System,
                instanceMetadata: new Uri(substitute)));
        Assert.Throws<InvalidOperationException>(() => new ManagedIdentityCredentialResolver(
            new Dictionary<string, ChatManagedIdentity> { ["apps"] = new(null, ChatTokenResource.AzureAi) }, null, _ => null, TimeProvider.System));
    }

    [Fact]
    public void Profile_documents_name_the_identity_source_explicitly()
    {
        static string Document(string dialect, string endpoint, string identity) =>
            "{\"version\":1,\"retentionDays\":30,\"profiles\":[{\"id\":\"model\",\"version\":1,\"displayName\":\"Model\"," +
            "\"provider\":\"azure\",\"dialect\":\"" + dialect + "\",\"model\":\"weft-chat\",\"endpoint\":\"" + endpoint + "\"," +
            "\"credentialReference\":\"keyless\",\"capabilities\":[\"text\"]," +
            "\"metadataPolicy\":{\"id\":\"mapping-metadata\",\"version\":1,\"mappingMetadataAllowed\":true}}]," +
            "\"credentials\":[{\"reference\":\"keyless\",\"managedIdentity\":" + identity + "}]}";
        const string serving = "https://adb-1234567890123456.7.azuredatabricks.net/serving-endpoints/chat/completions";
        const string azure = "https://resource.openai.azure.com/openai/v1/chat/completions";

        Assert.Equal(new ChatManagedIdentity(Guid.Parse(ClientId), ChatTokenResource.AzureDatabricks, ChatIdentitySource.InstanceMetadata),
            ChatHostConfiguration.Parse(Document("databricksChatCompletionsSse", serving,
                "{\"clientId\":\"" + ClientId + "\",\"resource\":\"azureDatabricks\",\"source\":\"instanceMetadata\"}")).ManagedIdentities["keyless"]);
        Assert.Equal(ChatIdentitySource.ContainerApps, ChatHostConfiguration.Parse(Document("chatCompletionsSse", azure, "{}")).ManagedIdentities["keyless"].Source);
        Assert.Equal(ChatIdentitySource.ContainerApps, ChatHostConfiguration.Parse(Document("chatCompletionsSse", azure,
            "{\"source\":\"containerApps\"}")).ManagedIdentities["keyless"].Source);
        Assert.Equal(ChatIdentitySource.InstanceMetadata, ChatHostConfiguration.Parse(Document("chatCompletionsSse", azure,
            "{\"source\":\"instanceMetadata\"}")).ManagedIdentities["keyless"].Source);

        foreach (var invalid in new[]
        {
            Document("chatCompletionsSse", azure, "{\"source\":\"auto\"}"),
            Document("chatCompletionsSse", azure, "{\"source\":\"InstanceMetadata\"}"),
            Document("chatCompletionsSse", azure, "{\"source\":null}"),
            Document("chatCompletionsSse", azure, "{\"source\":\"instanceMetadata\",\"endpoint\":\"http://169.254.169.254\"}"),
            // The cross-dialect refusal holds for this source too.
            Document("chatCompletionsSse", azure, "{\"resource\":\"azureDatabricks\",\"source\":\"instanceMetadata\"}"),
            Document("databricksChatCompletionsSse", serving, "{\"source\":\"instanceMetadata\"}")
        })
            Assert.Throws<InvalidOperationException>(() => ChatHostConfiguration.Parse(invalid));
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
