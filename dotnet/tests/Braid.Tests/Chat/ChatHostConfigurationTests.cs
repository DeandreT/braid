using System.Net;
using System.Runtime.Versioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Braid.Ai;
using Braid.Api.Chat;
using Braid.Server;

namespace Braid.Tests;

/// <summary>Pure host composition and private-file admission; no database, provider or network is opened.</summary>
[SupportedOSPlatform("linux")]
public sealed class ChatHostConfigurationTests : IDisposable
{
    private const string Connection = "Host=127.0.0.1;Database=braid;Username=braid_chat_reader;Password=synthetic-never-connected";
    private const string WriterConnection = "Host=127.0.0.1;Database=braid;Username=braid_chat_writer;Password=synthetic-writer-never-connected";
    private const string ApproverConnection = "Host=127.0.0.1;Database=braid;Username=braid_chat_approver;Password=synthetic-approver-never-connected";
    private static readonly string[] ChatDependencies =
        ["BRAID_CHAT_PROFILES_FILE", "BRAID_CHAT_READER_CONNECTION_STRING", "BRAID_CHAT_WRITER_CONNECTION_STRING", "BRAID_CHAT_APPROVER_CONNECTION_STRING"];
    private readonly string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "braid-chat-host-" + Guid.NewGuid().ToString("N"),
        "private")).FullName;

    public ChatHostConfigurationTests() => File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(root)!, recursive: true);

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public void Disabled_chat_reads_no_dependencies_and_registers_only_unavailable_routes(string? flag)
    {
        var configuration = Catalog();
        if (flag is not null) configuration["BRAID_CHAT_ENABLED"] = flag;
        var read = new List<string>();
        var builder = Builder();
        CatalogHostConfiguration.Configure(builder, new IPEndPoint(IPAddress.Loopback, 4100), variable =>
        {
            read.Add(variable);
            return configuration.GetValueOrDefault(variable);
        });
        Assert.False(Options(builder.Services).Enabled);
        Assert.DoesNotContain(read, ChatDependencies.Contains);
        foreach (var type in new[] { typeof(ChatTurnHost), typeof(ChatPersistencePorts), typeof(IAiCredentialResolver), typeof(ChatHostDatabases),
            typeof(ChatToolRegistry) })
            Assert.DoesNotContain(builder.Services, item => item.ServiceType == type);
    }

    [Theory]
    [InlineData("TRUE", true, true)]
    [InlineData("true", false, true)]
    [InlineData("true", true, false)]
    public void Invalid_or_inconsistent_chat_enablement_fails_closed(string flag, bool catalog, bool reports)
    {
        var configuration = catalog ? Catalog() : new Dictionary<string, string>();
        if (reports) configuration["BRAID_REVISION_REPORTS_ENABLED"] = "true";
        if (reports) configuration["BRAID_POSTGRES_REPORT_CONNECTION_STRING"] = Connection;
        configuration["BRAID_CHAT_ENABLED"] = flag;
        Enabled(configuration);
        Assert.ThrowsAny<Exception>(() => Configure(configuration));
    }

    [Fact]
    public void Enabled_chat_admits_private_profiles_and_registers_lazy_ports_without_opening_them()
    {
        var configuration = Catalog();
        configuration["BRAID_REVISION_REPORTS_ENABLED"] = "true";
        configuration["BRAID_POSTGRES_REPORT_CONNECTION_STRING"] = Connection;
        configuration["BRAID_CHAT_ENABLED"] = "true";
        Enabled(configuration);
        var builder = Configure(configuration);
        var options = Options(builder.Services);
        Assert.True(options.Enabled);
        var profile = Assert.Single(options.Profiles);
        Assert.Equal("Hosted fixture", profile.DisplayName);
        Assert.Equal("fixture", profile.ProviderId);
        Assert.Equal(AiAuthentication.Bearer, profile.Spec.Authentication);
        Assert.Equal(TimeSpan.FromDays(30), options.Retention);
        Assert.Equal(TimeSpan.FromSeconds(90), options.TurnDuration);
        Assert.Equal(512, options.MaxOutputTokens);
        foreach (var type in new[] { typeof(ChatTurnHost), typeof(ChatPersistencePorts), typeof(IAiCredentialResolver), typeof(ChatHostDatabases),
            typeof(ChatToolRegistry) })
            Assert.Single(builder.Services, item => item.ServiceType == type);
        Assert.DoesNotContain(builder.Services, item => item.ServiceType == typeof(ChatHostDatabases) && item.ImplementationInstance is not null);

        File.SetUnixFileMode(configuration["BRAID_CHAT_PROFILES_FILE"], UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        Assert.Throws<InvalidOperationException>(() => Configure(configuration));
    }

    [Fact]
    public void Profile_documents_are_strict_and_bind_every_bearer_reference()
    {
        var secret = Secret("token");
        var valid = Profiles(secret);
        Assert.True(ChatHostConfiguration.Parse(valid).Options.Enabled);
        foreach (var invalid in new[]
        {
            valid.Replace("\"version\":1,\"retentionDays\"", "\"version\":2,\"retentionDays\"", StringComparison.Ordinal),
            valid.Replace("\"retentionDays\":30", "\"retentionDays\":0", StringComparison.Ordinal),
            valid.Replace("\"turnSeconds\":90", "\"turnSeconds\":9", StringComparison.Ordinal),
            valid.Replace("\"maxOutputTokens\":512", "\"maxOutputTokens\":4096", StringComparison.Ordinal),
            valid.Replace("\"retentionDays\":30", "\"retentionDays\":30,\"extra\":true", StringComparison.Ordinal),
            valid.Replace("\"retentionDays\":30", "\"retentionDays\":30,\"retentionDays\":30", StringComparison.Ordinal),
            valid.Replace("\"credentialReference\":\"hosted-key\"", "\"credentialReference\":\"other-key\"", StringComparison.Ordinal),
            valid.Replace("\"credentialReference\":\"hosted-key\"", "\"credentialReference\":null", StringComparison.Ordinal),
            valid.Replace("\"loopbackFixture\":true", "\"loopbackFixture\":false", StringComparison.Ordinal),
            valid.Replace("\"chatCompletionsSse\"", "\"vendorSdk\"", StringComparison.Ordinal),
            valid.Replace("[\"text\",\"functionTools\"]", "[\"text\",\"text\"]", StringComparison.Ordinal),
            valid.Replace("[\"text\",\"functionTools\"]", "[\"functionTools\"]", StringComparison.Ordinal),
            valid.Replace("/v1/chat/completions", "/v1/other", StringComparison.Ordinal),
            valid.Replace(secret, "relative/secret", StringComparison.Ordinal),
            valid.Replace("\"displayName\":\"Hosted fixture\"", "\"displayName\":\" padded \"", StringComparison.Ordinal),
            "[]"
        })
            Assert.Throws<InvalidOperationException>(() => ChatHostConfiguration.Parse(invalid));
        var profile = valid[valid.IndexOf("{\"id\"", StringComparison.Ordinal)..(valid.IndexOf("}}]", StringComparison.Ordinal) + 2)];
        Assert.Throws<InvalidOperationException>(() => ChatHostConfiguration.Parse(valid.Replace(profile, profile + "," + profile, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Credentials_resolve_only_from_admitted_private_files_at_dispatch_time()
    {
        var path = Secret("bearer-value\n");
        var resolver = new ChatHostCredentialResolver(new Dictionary<string, string> { ["hosted-key"] = path });
        var found = Assert.IsType<CredentialFound>(await resolver.ResolveAsync("hosted-key", CancellationToken.None));
        found.Secret.Dispose();
        Assert.IsType<CredentialUnavailable>(await resolver.ResolveAsync("unknown-key", CancellationToken.None));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
        Assert.IsType<CredentialUnavailable>(await resolver.ResolveAsync("hosted-key", CancellationToken.None));
        File.Delete(path);
        Assert.IsType<CredentialUnavailable>(await resolver.ResolveAsync("hosted-key", CancellationToken.None));
        Assert.Equal(nameof(ChatHostCredentialResolver), resolver.ToString());
    }

    [Fact]
    public void Managed_identity_credentials_are_strict_and_exclusive_of_files()
    {
        const string clientId = "11111111-2222-3333-4444-55555555abcd";
        var user = ChatHostConfiguration.Parse(ManagedProfiles("{\"reference\":\"hosted-key\",\"managedIdentity\":{\"clientId\":\"" + clientId + "\"}}"));
        Assert.Equal(new ChatManagedIdentity(Guid.Parse(clientId), ChatTokenResource.AzureAi), user.ManagedIdentities["hosted-key"]);
        Assert.Empty(user.Credentials);
        Assert.Null(ChatHostConfiguration.Parse(ManagedProfiles("{\"reference\":\"hosted-key\",\"managedIdentity\":{}}")).ManagedIdentities["hosted-key"].ClientId);
        var file = "{\"reference\":\"hosted-key\",\"file\":\"" + Secret("token") + "\"}";
        foreach (var credentials in new[]
        {
            "{\"reference\":\"hosted-key\",\"file\":\"/run/braid/secrets/key\",\"managedIdentity\":{}}",
            "{\"reference\":\"hosted-key\"}",
            "{\"reference\":\"hosted-key\",\"managedIdentity\":{\"clientId\":\"" + clientId.ToUpperInvariant() + "\"}}",
            "{\"reference\":\"hosted-key\",\"managedIdentity\":{\"clientId\":\"00000000-0000-0000-0000-000000000000\"}}",
            "{\"reference\":\"hosted-key\",\"managedIdentity\":{\"clientId\":\"" + clientId + "\",\"scope\":\"https://other.example/.default\"}}",
            "{\"reference\":\"hosted-key\",\"managedIdentity\":\"" + clientId + "\"}",
            file + ",{\"reference\":\"hosted-key\",\"managedIdentity\":{}}",
            file + ",{\"reference\":\"unused-key\",\"managedIdentity\":{}}"
        })
            Assert.Throws<InvalidOperationException>(() => ChatHostConfiguration.Parse(ManagedProfiles(credentials)));
    }

    [Fact]
    public void Keyless_chat_admits_the_platform_identity_endpoint_and_composes_one_resolver()
    {
        var configuration = Catalog();
        configuration["BRAID_REVISION_REPORTS_ENABLED"] = "true";
        configuration["BRAID_POSTGRES_REPORT_CONNECTION_STRING"] = Connection;
        configuration["BRAID_CHAT_ENABLED"] = "true";
        Enabled(configuration);
        configuration["BRAID_CHAT_PROFILES_FILE"] = Private("keyless.json", ManagedProfiles("{\"reference\":\"hosted-key\",\"managedIdentity\":{}}"));
        Assert.Throws<InvalidOperationException>(() => Configure(configuration));
        configuration["IDENTITY_ENDPOINT"] = "http://localhost:42356/msi/token";
        configuration["IDENTITY_HEADER"] = "synthetic-identity-header";
        var builder = Configure(configuration);
        var descriptor = Assert.Single(builder.Services, item => item.ServiceType == typeof(IAiCredentialResolver));
        Assert.Null(descriptor.ImplementationInstance);
        IServiceCollection services = new ServiceCollection();
        services.Add(descriptor);
        using var provider = services.BuildServiceProvider();
        Assert.IsType<ChatHostCredentialResolver>(provider.GetRequiredService<IAiCredentialResolver>());
    }

    private static string ManagedProfiles(string credentials) =>
        "{\"version\":1,\"retentionDays\":30,\"profiles\":[{\"id\":\"azure-openai\",\"version\":1,\"displayName\":\"Azure OpenAI\"," +
        "\"provider\":\"azure-openai\",\"dialect\":\"chatCompletionsSse\",\"model\":\"deployment-name\"," +
        "\"endpoint\":\"https://resource.openai.azure.com/openai/v1/chat/completions\",\"credentialReference\":\"hosted-key\"," +
        "\"capabilities\":[\"text\",\"functionTools\"],\"metadataPolicy\":{\"id\":\"mapping-metadata\",\"version\":1,\"mappingMetadataAllowed\":true}}]," +
        "\"credentials\":[" + credentials + "]}";

    private void Enabled(Dictionary<string, string> configuration)
    {
        configuration["BRAID_CHAT_PROFILES_FILE"] = Private("profiles.json", Profiles(Secret("token")));
        configuration["BRAID_CHAT_READER_CONNECTION_STRING"] = Connection;
        configuration["BRAID_CHAT_WRITER_CONNECTION_STRING"] = WriterConnection;
        configuration["BRAID_CHAT_APPROVER_CONNECTION_STRING"] = ApproverConnection;
    }

    private static string Profiles(string secretPath) =>
        "{\"version\":1,\"retentionDays\":30,\"turnSeconds\":90,\"maxOutputTokens\":512,\"profiles\":[{\"id\":\"hosted-fixture\",\"version\":1," +
        "\"displayName\":\"Hosted fixture\",\"provider\":\"fixture\",\"dialect\":\"chatCompletionsSse\",\"model\":\"fixture-model\"," +
        "\"endpoint\":\"http://127.0.0.1:9/v1/chat/completions\",\"loopbackFixture\":true,\"credentialReference\":\"hosted-key\"," +
        "\"capabilities\":[\"text\",\"functionTools\"],\"metadataPolicy\":{\"id\":\"mapping-metadata\",\"version\":1,\"mappingMetadataAllowed\":true}}]," +
        "\"credentials\":[{\"reference\":\"hosted-key\",\"file\":\"" + secretPath + "\"}]}";

    private string Secret(string value) => Private("secret-" + Guid.NewGuid().ToString("N"), value);

    private string Private(string name, string value)
    {
        var path = Path.Combine(root, name);
        File.WriteAllText(path, value);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return path;
    }

    private static WebApplicationBuilder Configure(Dictionary<string, string> configuration)
    {
        var builder = Builder();
        CatalogHostConfiguration.Configure(builder, new IPEndPoint(IPAddress.Loopback, 4100), configuration.GetValueOrDefault);
        return builder;
    }

    private static Dictionary<string, string> Catalog() => new()
    {
        ["BRAID_CATALOG_ENABLED"] = "true",
        ["BRAID_DEV_IDENTITY_ENABLED"] = "true",
        ["BRAID_DEV_PRINCIPAL_ID"] = "33333333-3333-3333-3333-333333333333",
        ["BRAID_DEV_ORGANIZATION_ID"] = "11111111-1111-1111-1111-111111111111",
        ["BRAID_DEV_BROWSER_ORIGIN"] = "http://127.0.0.1:4100",
        ["BRAID_CATALOG_CONNECTION_STRING"] = Connection
    };

    private static ChatApiOptions Options(IServiceCollection services) =>
        Assert.IsType<ChatApiOptions>(Assert.Single(services, item => item.ServiceType == typeof(ChatApiOptions)).ImplementationInstance);

    private static WebApplicationBuilder Builder() => WebApplication.CreateBuilder(new WebApplicationOptions
    { EnvironmentName = "Development", Args = [] });
}
