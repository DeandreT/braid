using NpgsqlTypes;
using Braid.Application;
using Braid.Domain;

namespace Braid.Persistence.Postgres;

public sealed partial class PostgresChatStore
{
    private static async Task<ChatStoreOutcome> ReserveIdentityAsync(PostgresTransaction tx, ChatConversationKey key, int kind,
        Guid identityId, Guid? turnId, CancellationToken ct)
    {
        // This exact-scope routine owns INSERT and rechecks project authority after
        // global identity waits. The writer has no direct identity INSERT privilege.
        await using var command = Command(tx, "SELECT braid.chat_reserve_identity(@org,@creator,@conversation,@project,@kind,@identity,@identityTurn)", key);
        Add(command, "kind", NpgsqlDbType.Integer, kind); Add(command, "identity", NpgsqlDbType.Uuid, identityId);
        Add(command, "identityTurn", NpgsqlDbType.Uuid, turnId);
        return ReadOutcome(await command.ExecuteScalarAsync(ct));
    }
}
