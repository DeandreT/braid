using System.Security.Cryptography;
using Braid.Application;
using Braid.As2;
using Braid.Deployment;
using Braid.Deployment.Triggers;
using Braid.Persistence.Postgres;
using Npgsql;

namespace Braid.ReleaseTool;

/// <summary>Offline operator recovery only. Read the trusted fence from an already-open, local recovery database;
/// use the normal administrator port on that same database for the only mutation. Never register as an endpoint.</summary>
public static class LocalAs2ReceiptRecovery
{
    public static async Task<TriggerResolutionOutcome> AcceptAsync(NpgsqlConnection evidence, string releaseWriterConnectionString, ActorContext actor,
        ReleaseTargetReference target, Guid delivery, Guid claim, string from, string to, string contentType, ReadOnlyMemory<byte> receipt, CancellationToken token)
    {
        if (delivery == Guid.Empty || claim == Guid.Empty || receipt.Length is 0 or > As2Connector.MaxReceiptBytes ||
            contentType.Length > 1024 || from.Length > 128 || to.Length > 128) return TriggerResolutionOutcome.Refused;
        // Reject remote hosts, ambiguous DNS and independently selected databases, even if their delivery IDs match.
        var write = new NpgsqlConnectionStringBuilder(releaseWriterConnectionString);
        if (evidence.Host is not ("127.0.0.1" or "::1") || write.Host != evidence.Host || write.Port != evidence.Port || write.Database != evidence.Database ||
            write.Username != "braid_release_writer") return TriggerResolutionOutcome.Refused;
        // A later send reuses the message ID and MIC. Such a receipt cannot distinguish the attempts.
        As2Attempt expectation;
        await using (var tx = await evidence.BeginTransactionAsync(token))
        {
            await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", evidence, tx)) await readOnly.ExecuteNonQueryAsync(token);
            await using var cmd = new NpgsqlCommand("""
                SELECT f.binding_version,f.payload_sha256,f.message_id,f.mic,f.sender,f.recipient,f.receipt_cert_sha256
                FROM ONLY braid.as2_send_fences f JOIN ONLY braid.trigger_deliveries d USING(organization_id,delivery_id)
                JOIN ONLY braid.local_release_targets t USING(organization_id,target_id)
                JOIN ONLY braid.trigger_receipts r USING(organization_id,receipt_id)
                WHERE f.organization_id=$1 AND t.project_id=$2 AND t.mapping_id=$3 AND t.environment_id=$4 AND t.environment_version=$5
                    AND d.target_id=$6 AND f.delivery_id=$7 AND f.claim_id=$8 AND d.destination_kind='as2' AND d.phase IN ('unknown','accepted')
                    AND f.send_number=1 AND d.sends=1 AND r.direction='output' AND r.work_id=d.work_id AND r.digest=f.payload_sha256
                    AND f.binding_version=d.destination_binding_id::text||'/'||d.connector_id||'/'||d.connector_version::text
                """, evidence, tx);
            foreach (var value in new object[] { target.Mapping.OrganizationId, target.Mapping.ProjectId, target.Mapping.MappingId, target.Environment.EnvironmentId,
                target.Environment.Version, target.TargetId, delivery, claim }) cmd.Parameters.AddWithValue(value);
            await using var rows = await cmd.ExecuteReaderAsync(token);
            if (!await rows.ReadAsync(token)) return TriggerResolutionOutcome.Refused;
            expectation = new(delivery, rows.GetString(0), rows.GetString(1), rows.GetString(2), rows.GetString(3), rows.GetString(4), rows.GetString(5), rows.GetString(6));
        }
        var bytes = receipt.ToArray();
        if (!As2Mdn.Verify(expectation, from, to, contentType, bytes)) return TriggerResolutionOutcome.Refused;
        // Same fenced receipt yields the same audit operation, including after a lost settlement acknowledgement.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("braid.as2.delayed-mdn.v1"u8); hash.AppendData(actor.PrincipalId.Value.ToByteArray());
        hash.AppendData(claim.ToByteArray()); hash.AppendData(bytes);
        var operation = new Guid(hash.GetHashAndReset().AsSpan(0, 16));
        await using var writer = new PostgresLocalReleaseStore(releaseWriterConnectionString, true);
        return await writer.ResolveDeliveryAsync(actor, target, delivery, operation, 1, TriggerDeliveryResolution.Accepted, token);
    }
}
