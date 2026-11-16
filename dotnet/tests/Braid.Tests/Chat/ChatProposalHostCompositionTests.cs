using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Braid.Api.Catalog;
using Braid.Api.Chat;
using Braid.Application;
using Braid.Domain;
using Braid.Persistence.Postgres;
using Braid.Server;

namespace Braid.Tests;

/// <summary>Portable production-registration tests: no private-file APIs, database, network or worker.</summary>
public sealed class ChatProposalHostCompositionTests
{
    private const string Reader = "BRAID_CHAT_READER_CONNECTION_STRING";
    private const string Writer = "BRAID_CHAT_WRITER_CONNECTION_STRING";
    private const string Approver = "BRAID_CHAT_APPROVER_CONNECTION_STRING";
    private static string Connection(string role) => $"Host=127.0.0.1;Port=1;Database=braid;Username=braid_chat_{role};Password=synthetic-{role}-never-connected";
    private static Dictionary<string, string?> Settings() => new() { [Reader] = Connection("reader"), [Writer] = Connection("writer"), [Approver] = Connection("approver") };

    [Fact]
    public async Task Real_store_and_service_resolve_with_three_independent_owned_pools_and_no_writer_approval()
    {
        var services = new ServiceCollection(); var settings = Settings();
        var dependencies = new ChatProposalRuntimeStore();
        services.AddSingleton<IProjectReader>(dependencies);
        services.AddSingleton<IRevisionIngestionService>(dependencies);
        var configuration = ChatProposalHostConfiguration.Configure(services, settings.GetValueOrDefault, false);
        Assert.Equal(nameof(ChatHostConnections), configuration.ToString());
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var databases = provider.GetRequiredService<ChatHostDatabases>();
        Assert.Equal(ChatDatabaseProfile.Reader, databases.Reader.Profile);
        Assert.Equal(ChatDatabaseProfile.Writer, databases.Writer.Profile);
        Assert.Equal(ChatDatabaseProfile.Approver, databases.Approver.Profile);
        Assert.NotSame(databases.Reader.DataSource, databases.Writer.DataSource);
        Assert.NotSame(databases.Writer.DataSource, databases.Approver.DataSource);
        Assert.NotSame(databases.Reader.DataSource, databases.Approver.DataSource);
        var store = Assert.IsType<PostgresChatEditProposalStore>(provider.GetRequiredService<IChatMappingEditProposalStore>());
        Assert.Same(store, provider.GetRequiredService<IChatMappingEditProposalStore>());
        var service = provider.GetRequiredService<ChatMappingEditApprovalService>();
        var p = Proposal();
        // The composed real store refuses reconstructed content without attempting a connection.
        Assert.Equal(ChatMappingEditCreateOutcome.Refused, (await service.CreateAsync(new(p.Conversation.Creator), p)).Outcome);
        Assert.Equal(0, dependencies.Creates); Assert.Equal(0, dependencies.Approvals); Assert.Equal(0, dependencies.Appends);
        var ports = provider.GetRequiredService<ChatPersistencePorts>();
        Assert.Same(ports.WriterConversations, ports.WriterTurns);
        foreach (var writer in new[] { Assert.IsType<PostgresChatStore>(ports.WriterTurns),
            Assert.IsType<PostgresChatStore>(provider.GetRequiredService<IChatMappingWriteJournal>()) })
            await Assert.ThrowsAsync<InvalidOperationException>(() => writer.ApproveAndPrepareAsync(new(p.Conversation.Creator),
                p.Conversation, p.TurnId, p.ProposalId, p.Fingerprint, default));
        await provider.DisposeAsync();
        foreach (var database in new[] { databases.Reader, databases.Writer, databases.Approver })
            Assert.Throws<ObjectDisposedException>(() => database.DataSource);
        // Repeated host cleanup is harmless for all three pools.
        await databases.DisposeAsync();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("writer-login")]
    [InlineData("reader-login")]
    [InlineData("owner-login")]
    [InlineData("same-password")]
    [InlineData("different-database")]
    [InlineData("different-host")]
    [InlineData("no-reset")]
    [InlineData("multiplex")]
    [InlineData("ambient-enlist")]
    [InlineData("malformed")]
    public void Missing_or_conflicting_approval_configuration_fails_before_registration_without_secret_diagnostics(string fault)
    {
        var values = Settings();
        values[Approver] = fault switch
        {
            "missing" => null,
            "empty" => "",
            "writer-login" => Connection("writer") + ";Application Name=disguise",
            "reader-login" => Connection("reader"),
            "owner-login" => Connection("approver").Replace("braid_chat_approver", "braid_migrator", StringComparison.Ordinal),
            "same-password" => Connection("approver").Replace("synthetic-approver", "synthetic-writer", StringComparison.Ordinal),
            "different-database" => Connection("approver") + ";Database=other",
            "different-host" => Connection("approver") + ";Host=other",
            "no-reset" => Connection("approver") + ";No Reset On Close=true",
            "multiplex" => Connection("approver") + ";Multiplexing=true",
            "ambient-enlist" => Connection("approver") + ";Enlist=true",
            _ => "synthetic-secret-malformed"
        };
        var services = new ServiceCollection();
        var error = Assert.Throws<InvalidOperationException>(() => ChatProposalHostConfiguration.Configure(services, values.GetValueOrDefault, false));
        Assert.DoesNotContain("synthetic", error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
        Assert.Empty(services);
    }

    [Theory]
    [InlineData("reader")]
    [InlineData("writer")]
    public void All_profile_roles_are_checked_at_composition(string role)
    {
        var values = Settings(); values[role == "reader" ? Reader : Writer] = Connection("approver");
        Assert.Throws<InvalidOperationException>(() => ChatProposalHostConfiguration.Configure(new ServiceCollection(), values.GetValueOrDefault, false));
    }

    [Fact]
    public void Session_mode_does_not_fallback_to_plain_or_missing_file_delivery()
    {
        foreach (var values in new[] { Settings(), new Dictionary<string, string?>() })
        {
            var services = new ServiceCollection();
            Assert.Throws<InvalidOperationException>(() => ChatProposalHostConfiguration.Configure(services, values.GetValueOrDefault, true));
            Assert.Empty(services);
        }
    }

    [Fact]
    public void Disabled_chat_does_not_read_approval_configuration_or_register_persistence()
    {
        var services = new ServiceCollection();
        ChatHostConfiguration.Configure(services, _ => throw new InvalidOperationException("No configuration read expected"), false, false, new CatalogRevisionReportOptions());
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(ChatHostDatabases) || d.ServiceType == typeof(IChatMappingEditProposalStore) ||
            d.ServiceType == typeof(ChatMappingEditApprovalService));
    }

    private static ChatMappingEditProposal Proposal()
    {
        var scope = new RevisionScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var conversation = ChatConversationKey.ForWorkspace(scope.OrganizationId, new(Guid.NewGuid()), Guid.NewGuid());
        return new(conversation, Guid.NewGuid(), Guid.NewGuid(), "composition-call", 1,
            new(NativeArtifact.Admit(scope, "{}"u8, 100), MappingEditScript.Parse(Encoding.UTF8.GetBytes(MappingEditScriptTests.Bind)), []));
    }
}
