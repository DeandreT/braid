using System.Text.Json;
using NpgsqlTypes;
using Braid.Application;
using Braid.Domain;

namespace Braid.Persistence.Postgres;

public sealed partial class PostgresChatStore : IChatMappingWriteJournal
{
    public Task<bool> PrepareAsync(ActorContext actor, ChatMappingWriteIntent intent, CancellationToken ct) =>
        ExecuteAsync(actor, intent.Conversation, true, true, async tx =>
        {
            await using var command = Command(tx, "SELECT braid.chat_mapping_write_prepare(@org,@creator,@conversation,@turn,@round,@call,@key,@proposal,@native)", intent.Conversation, intent.TurnId);
            Add(command, "round", NpgsqlDbType.Uuid, intent.RoundId); Add(command, "call", NpgsqlDbType.Text, intent.CallId);
            Add(command, "key", NpgsqlDbType.Uuid, intent.Key); Add(command, "proposal", NpgsqlDbType.Text, ChatMetadataCodec.MappingWrite(intent.Proposal));
            Add(command, "native", NpgsqlDbType.Bytea, intent.Native.CopyBytes());
            return await command.ExecuteScalarAsync(ct) is true;
        }, false, ct);

    public Task<IReadOnlyList<ChatMappingWriteReceipt>> ReadAsync(ActorContext actor, ChatConversationKey conversation, Guid turnId, CancellationToken ct) =>
        ExecuteAsync<IReadOnlyList<ChatMappingWriteReceipt>>(actor, conversation, false, false, async tx =>
        {
            await using var command = Command(tx, "SELECT braid.chat_mapping_write_read(@org,@creator,@conversation,@turn)", conversation, turnId);
            if (await command.ExecuteScalarAsync(ct) is not string json) throw Invalid();
            using var document = JsonDocument.Parse(json);
            var result = new List<ChatMappingWriteReceipt>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var proposal = ChatMetadataCodec.ReadMappingWrite(item.GetProperty("proposal").GetString()!);
                var native = item.GetProperty("native").ValueKind == JsonValueKind.Null ? null
                    : NativeArtifact.Admit(proposal.Base, Convert.FromBase64String(item.GetProperty("native").GetString()!), MappingEditPolicy.MaximumNativeBytes);
                result.Add(new(item.GetProperty("key").GetGuid(), item.GetProperty("round").GetGuid(), item.GetProperty("status").GetString()!, proposal,
                    Id("operation"), Id("revision"), item.GetProperty("sequence").ValueKind == JsonValueKind.Null ? null : item.GetProperty("sequence").GetInt64(), native));
                Guid? Id(string name) => item.GetProperty(name).ValueKind == JsonValueKind.Null ? null : item.GetProperty(name).GetGuid();
            }
            if (result.Count > 8) throw Invalid();
            return result;
        }, [], ct);
}
