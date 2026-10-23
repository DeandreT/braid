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
        new(new Dictionary<string, Guid?> { [Reference] = clientId ?? Guid.Parse(ClientId) }, server.Endpoint("/msi/token"),
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
        using var resolver = new ManagedIdentityCredentialResolver(new Dictionary<string, Guid?> { [Reference] = null, ["second"] = null },
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

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
