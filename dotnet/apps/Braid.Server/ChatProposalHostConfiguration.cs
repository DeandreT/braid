using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Braid.Api.Chat;
using Braid.Application;
using Braid.Persistence.Postgres;

namespace Braid.Server;

/// <summary>Production proposal composition, shared by file-backed session and local configuration.
/// Connection admission is eager; all database connections remain lazy and receive runtime role/schema admission.</summary>
internal static class ChatProposalHostConfiguration
{
    internal static ChatHostConnections Configure(IServiceCollection services, Func<string, string?> read, bool session)
    {
        var connections = Admit(Connection("BRAID_CHAT_READER_CONNECTION_STRING"), Connection("BRAID_CHAT_WRITER_CONNECTION_STRING"),
            Connection("BRAID_CHAT_APPROVER_CONNECTION_STRING"));
        services.AddSingleton(_ => new ChatHostDatabases(connections.Reader, connections.Writer, connections.Approver));
        services.AddSingleton(provider =>
        {
            var databases = provider.GetRequiredService<ChatHostDatabases>();
            var reader = new PostgresChatStore(databases.Reader);
            var writer = new PostgresChatStore(databases.Writer);
            return new ChatPersistencePorts(reader, reader, writer, writer);
        });
        services.AddSingleton<IChatMappingWriteJournal>(provider => new PostgresChatStore(provider.GetRequiredService<ChatHostDatabases>().Writer));
        services.AddSingleton<ChatMappingWriteRecovery>();
        services.AddSingleton<IChatMappingEditProposalStore>(provider =>
        {
            var databases = provider.GetRequiredService<ChatHostDatabases>();
            return new PostgresChatEditProposalStore(databases.Reader, databases.Writer, databases.Approver);
        });
        services.AddSingleton(provider => new ChatMappingEditApprovalService(provider.GetRequiredService<IChatMappingEditProposalStore>(),
            provider.GetRequiredService<IProjectReader>(), provider.GetRequiredService<IRevisionIngestionService>()));
        return connections;

        string Connection(string name) => session
            ? SessionHostResources.ReadProtectedConnection(read, name) : ServerHostOptions.Required(read, name);
    }

    internal static ChatHostConnections Admit(string reader, string writer, string approver)
    {
        try
        {
            var r = Profile(reader, "braid_chat_reader");
            var w = Profile(writer, "braid_chat_writer");
            var a = Profile(approver, "braid_chat_approver");
            // Different spelling/options cannot disguise reuse of the writer credential or a different catalog.
            if (r.Password == w.Password || r.Password == a.Password || w.Password == a.Password ||
                !SameDatabase(r, w) || !SameDatabase(r, a)) throw ServerHostOptions.Failure();
            // Retain admitted delivery strings for the host's cross-feature isolation checks.
            // Each PostgresChatDatabase normalizes its transaction/reset defaults independently.
            return new(reader, writer, approver);
        }
        catch (Exception error) when (SessionHostResources.Nonfatal(error)) { throw ServerHostOptions.Failure(); }
    }

    private static NpgsqlConnectionStringBuilder Profile(string value, string role)
    {
        var connection = new NpgsqlConnectionStringBuilder(value);
        var supplied = new DbConnectionStringBuilder { ConnectionString = value };
        if (connection.Username != role || string.IsNullOrWhiteSpace(connection.Host) || string.IsNullOrWhiteSpace(connection.Database) ||
            string.IsNullOrEmpty(connection.Password) || connection.Passfile is not null || connection.SslCertificate is not null ||
            connection.SslKey is not null || connection.SslPassword is not null || connection.NoResetOnClose || connection.Multiplexing ||
            (supplied.ContainsKey("Enlist") && connection.Enlist)) throw ServerHostOptions.Failure();
        return connection;
    }

    private static bool SameDatabase(NpgsqlConnectionStringBuilder left, NpgsqlConnectionStringBuilder right) =>
        string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase) && left.Port == right.Port && left.Database == right.Database;
}

internal sealed record ChatHostConnections(string Reader, string Writer, string Approver)
{
    public override string ToString() => nameof(ChatHostConnections);
}
