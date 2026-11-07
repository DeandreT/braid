using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Weft.Ai;
using Weft.Api.Catalog;
using Weft.Api.Chat;
using Weft.Api.Chat.Workspace;
using Weft.Application;
using Weft.Persistence.Postgres;
using Weft.Server;

namespace Weft.Tests;

/// <summary>
/// Session composition of chat and previews: strict scalar admission before any delivery, and
/// credentials only through protected private files. No database, provider or message host is opened.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class SessionFeatureHostConfigurationTests
{
    private const string Secret = "synthetic-never-connected-session-feature-secret";
    private const string PreviewWriter = "WEFT_POSTGRES_PREVIEW_WRITER_CONNECTION_STRING";
    private const string PreviewReader = "WEFT_POSTGRES_PREVIEW_READER_CONNECTION_STRING";
    private const string ChatReader = "WEFT_CHAT_READER_CONNECTION_STRING";
    private const string ChatWriter = "WEFT_CHAT_WRITER_CONNECTION_STRING";

    [Theory]
    [InlineData("WEFT_PREVIEWS_ENABLED", "True")]
    [InlineData("WEFT_PREVIEWS_ENABLED", "1")]
    [InlineData("WEFT_PREVIEWS_ENABLED", "")]
    [InlineData("WEFT_PREVIEWS_ENABLED", " true")]
    [InlineData("WEFT_CHAT_ENABLED", "true")]
    public void SessionScalarsRefuseInvalidPreviewFlagsAndChatWithoutReportsBeforeAnyDelivery(string name, string value)
    {
        var values = SessionScalars();
        var accepted = ServerHostOptions.Read(values.GetValueOrDefault);
        Assert.False(accepted.Previews.Enabled);
        Assert.False(accepted.Chat);
        values[name] = value;
        var deliveries = 0;
        Assert.Throws<InvalidOperationException>(() => ServerHostOptions.Read(key =>
        {
            if (key.EndsWith("_FILE", StringComparison.Ordinal)) deliveries++;
            return values.GetValueOrDefault(key);
        }));
        Assert.Equal(0, deliveries);
    }

    [Fact]
    public void SessionScalarsRetainOnePreviewAndChatSnapshot()
    {
        var values = SessionScalars();
        values["WEFT_PREVIEWS_ENABLED"] = "true";
        values["WEFT_CHAT_ENABLED"] = "true";
        values["WEFT_REVISION_REPORTS_ENABLED"] = "true";
        var admitted = ServerHostOptions.Read(values.GetValueOrDefault);
        values["WEFT_PREVIEWS_ENABLED"] = "invalid-after-admission";
        values["WEFT_CHAT_ENABLED"] = "invalid-after-admission";
        Assert.True(admitted.Previews.Enabled);
        Assert.True(admitted.Chat);
        Assert.True(admitted.Reports.Enabled);
    }

    [Fact]
    public void SessionPreviewsAdmitOnlyTheTwoProtectedFileDeliveries()
    {
        using var files = new ServerHostFiles();
        var configuration = PreviewDeliveries(files);
        var requested = new List<string>();
        var services = CatalogServices();
        CatalogPreviewHostConfiguration.Configure(services, new CatalogPreviewOptions(true),
            name => { requested.Add(name); return configuration.GetValueOrDefault(name); }, session: true);
        Assert.Contains(PreviewWriter + "_FILE", requested);
        Assert.Contains(PreviewReader + "_FILE", requested);
        Assert.Single(services, item => item.ServiceType == typeof(PreviewRunService));
        Assert.Equal(2, services.Count(item => item.ServiceType == typeof(PostgresPreviewDatabase) && item.IsKeyedService));
        Assert.True(Assert.IsType<CatalogPreviewOptions>(Assert.Single(services,
            item => item.ServiceType == typeof(CatalogPreviewOptions)).ImplementationInstance).Enabled);
    }

    [Theory]
    [InlineData("plain-writer")]
    [InlineData("plain-reader")]
    [InlineData("missing-writer")]
    [InlineData("missing-reader")]
    [InlineData("loose-reader")]
    [InlineData("symbolic-writer")]
    [InlineData("passwordless-writer")]
    [InlineData("passfile-reader")]
    [InlineData("ambient-password")]
    [InlineData("same-login")]
    public void SessionPreviewCredentialFaultsFailClosedBeforeRegisteringPools(string kind)
    {
        using var files = new ServerHostFiles();
        var configuration = PreviewDeliveries(files);
        Fault(files, configuration, kind, PreviewWriter, PreviewReader, "weft_preview");
        var services = CatalogServices();
        var error = Assert.Throws<InvalidOperationException>(() => CatalogPreviewHostConfiguration.Configure(services,
            new CatalogPreviewOptions(true), configuration.GetValueOrDefault, session: true));
        Assert.Equal("The server host profile is invalid.", error.Message);
        Assert.DoesNotContain(Secret, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(services, item => item.ServiceType == typeof(PreviewRunService) || item.ServiceType == typeof(PostgresPreviewDatabase));
    }

    [Fact]
    public void DisabledSessionPreviewsReadNoDeliveries()
    {
        var services = CatalogServices();
        CatalogPreviewHostConfiguration.Configure(services, new CatalogPreviewOptions(false),
            name => throw new InvalidOperationException("Disabled previews read " + name), session: true);
        Assert.False(Assert.IsType<CatalogPreviewOptions>(Assert.Single(services,
            item => item.ServiceType == typeof(CatalogPreviewOptions)).ImplementationInstance).Enabled);
        Assert.DoesNotContain(services, item => item.ServiceType == typeof(PreviewRunService));
    }

    [Fact]
    public void SessionChatAdmitsOnlyProtectedReaderAndWriterFiles()
    {
        using var files = new ServerHostFiles();
        var configuration = ChatDeliveries(files);
        var requested = new List<string>();
        var services = new ServiceCollection();
        ChatHostConfiguration.Configure(services, name => { requested.Add(name); return configuration.GetValueOrDefault(name); },
            true, true, new CatalogRevisionReportOptions(true), session: true);
        Assert.Contains(ChatReader + "_FILE", requested);
        Assert.Contains(ChatWriter + "_FILE", requested);
        Assert.True(Assert.IsType<ChatApiOptions>(Assert.Single(services, item => item.ServiceType == typeof(ChatApiOptions))
            .ImplementationInstance).Enabled);
        foreach (var type in new[] { typeof(ChatHostDatabases), typeof(ChatPersistencePorts), typeof(IAiCredentialResolver) })
            Assert.Single(services, item => item.ServiceType == type);
        Assert.DoesNotContain(services, item => item.ServiceType == typeof(ChatHostDatabases) && item.ImplementationInstance is not null);
    }

    [Theory]
    [InlineData("plain-writer")]
    [InlineData("plain-reader")]
    [InlineData("missing-writer")]
    [InlineData("missing-reader")]
    [InlineData("loose-reader")]
    [InlineData("symbolic-writer")]
    [InlineData("passwordless-writer")]
    [InlineData("passfile-reader")]
    [InlineData("ambient-password")]
    [InlineData("same-login")]
    public void SessionChatCredentialFaultsFailClosedBeforeRegisteringPools(string kind)
    {
        using var files = new ServerHostFiles();
        var configuration = ChatDeliveries(files);
        Fault(files, configuration, kind, ChatWriter, ChatReader, "weft_chat");
        var services = new ServiceCollection();
        var error = Assert.Throws<InvalidOperationException>(() => ChatHostConfiguration.Configure(services,
            configuration.GetValueOrDefault, true, true, new CatalogRevisionReportOptions(true), session: true));
        Assert.Equal("The server host profile is invalid.", error.Message);
        Assert.DoesNotContain(Secret, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(services, item => item.ServiceType == typeof(ChatHostDatabases) || item.ServiceType == typeof(ChatApiOptions));
    }

    [Fact]
    public async Task Session_chat_payload_profile_and_private_grant_reader_produce_the_payload_manifest()
    {
        using var files = new ServerHostFiles();
        var configuration = ChatDeliveries(files);
        configuration["WEFT_CHAT_PROFILES_FILE"] = files.Text("payload-profiles.json",
            File.ReadAllText(configuration["WEFT_CHAT_PROFILES_FILE"]).Replace(
                "\"mappingMetadataAllowed\":true", "\"mappingMetadataAllowed\":true,\"payloadsAllowed\":true", StringComparison.Ordinal));
        configuration["WEFT_CHAT_PAYLOAD_PERMISSION_CONNECTION_STRING_FILE"] = files.Text("chat-payload-permission",
            Connection("weft_payload_permission_reader"));
        var catalog = new WorkspaceToolFixture();
        var services = new ServiceCollection();
        services.AddSingleton<IRevisionReportReader>(catalog);
        services.AddSingleton<IRevisionReportMetadataReader>(catalog);
        services.AddSingleton(new CatalogPreviewOptions(true));
        ChatHostConfiguration.Configure(services, configuration.GetValueOrDefault, true, true, new CatalogRevisionReportOptions(true), session: true);
        await using var provider = services.BuildServiceProvider();
        Assert.True(Assert.Single(provider.GetRequiredService<ChatApiOptions>().Profiles).Spec.PayloadsAllowed);
        Assert.NotNull(provider.GetRequiredService<IProjectPayloadGrantPresence>());
        Assert.Contains(provider.GetRequiredService<WorkspaceToolService>().PayloadManifest!.Value, tool => tool.Name == "get_preview_input");
    }

    private static void Fault(ServerHostFiles files, Dictionary<string, string> configuration, string kind,
        string writer, string reader, string role)
    {
        switch (kind)
        {
            case "plain-writer": configuration[writer] = Connection(role + "_writer"); break;
            case "plain-reader": configuration[reader] = Connection(role + "_reader"); break;
            case "missing-writer": configuration.Remove(writer + "_FILE"); break;
            case "missing-reader": configuration.Remove(reader + "_FILE"); break;
            case "loose-reader":
                File.SetUnixFileMode(configuration[reader + "_FILE"], ServerHostFiles.PrivateFile | UnixFileMode.OtherRead);
                break;
            case "symbolic-writer":
                var link = Path.Combine(files.Root, "writer-link.txt");
                File.CreateSymbolicLink(link, configuration[writer + "_FILE"]);
                configuration[writer + "_FILE"] = link;
                break;
            case "passwordless-writer":
                configuration[writer + "_FILE"] = files.Text("passwordless.txt", "Host=127.0.0.1;Database=weft;Username=" + role + "_writer\n");
                break;
            case "passfile-reader":
                configuration[reader + "_FILE"] = files.Text("passfile.txt", Connection(role + "_reader") + ";Passfile=/owned-unopened-passfile\n");
                break;
            case "ambient-password": configuration["PGPASSWORD"] = Secret; break;
            case "same-login": configuration[reader + "_FILE"] = configuration[writer + "_FILE"]; break;
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    private static string Connection(string role) => "Host=127.0.0.1;Database=weft;Username=" + role + ";Password=" + Secret;

    private static Dictionary<string, string> PreviewDeliveries(ServerHostFiles files) => new()
    {
        [PreviewWriter + "_FILE"] = files.Text("preview-writer.txt", Connection("weft_preview_writer") + "\n"),
        [PreviewReader + "_FILE"] = files.Text("preview-reader.txt", Connection("weft_preview_reader") + "\n")
    };

    private static Dictionary<string, string> ChatDeliveries(ServerHostFiles files)
    {
        var credential = files.Text("provider-credential", "synthetic-provider-credential\n");
        return new()
        {
            ["WEFT_CHAT_PROFILES_FILE"] = files.Text("chat-profiles.json",
                "{\"version\":1,\"retentionDays\":30,\"profiles\":[{\"id\":\"hosted-fixture\",\"version\":1," +
                "\"displayName\":\"Hosted fixture\",\"provider\":\"fixture\",\"dialect\":\"chatCompletionsSse\",\"model\":\"fixture-model\"," +
                "\"endpoint\":\"http://127.0.0.1:9/v1/chat/completions\",\"loopbackFixture\":true,\"credentialReference\":\"hosted-key\"," +
                "\"capabilities\":[\"text\",\"functionTools\"],\"metadataPolicy\":{\"id\":\"mapping-metadata\",\"version\":1,\"mappingMetadataAllowed\":true}}]," +
                "\"credentials\":[{\"reference\":\"hosted-key\",\"file\":\"" + credential + "\"}]}"),
            [ChatReader + "_FILE"] = files.Text("chat-reader.txt", Connection("weft_chat_reader") + "\n"),
            [ChatWriter + "_FILE"] = files.Text("chat-writer.txt", Connection("weft_chat_writer") + "\n")
        };
    }

    // The catalog boundary registration is the only prerequisite; it is never resolved here.
    private static ServiceCollection CatalogServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<CatalogRequestBoundary>(_ => throw new InvalidOperationException("Unresolved fixture boundary."));
        return services;
    }

    private static Dictionary<string, string?> SessionScalars() => new(StringComparer.Ordinal)
    {
        ["WEFT_CATALOG_ENABLED"] = "true",
        ["WEFT_CATALOG_AUTH_MODE"] = "session",
        ["WEFT_PUBLIC_ORIGIN"] = "https://127.0.0.1:58413",
        ["WEFT_LISTEN_ADDR"] = "127.0.0.1:58413",
        ["WEFT_HOST_TRANSPORT"] = "directHttps",
        ["WEFT_SESSION_ORGANIZATION_ID"] = "97b79dcc-d078-4994-9a06-f75147842811",
        ["WEFT_ENTRA_TENANT_ID"] = "e6492421-de0b-4626-87a1-773d7f77e0aa",
        ["WEFT_ENTRA_CLIENT_ID"] = "39ceba91-5e4d-4ead-9be2-ddbf843be1c6",
        ["WEFT_SESSION_APPLICATION_NAME"] = "Owned scalar admission",
        ["WEFT_SESSION_KEY_DIRECTORY"] = "/owned-unopened-key-ring"
    };
}
