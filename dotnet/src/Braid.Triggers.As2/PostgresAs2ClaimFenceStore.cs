using Braid.As2;
using Braid.Deployment;
using Braid.Deployment.Triggers;
using Npgsql;

namespace Braid.Triggers.As2;

public sealed class PostgresAs2ClaimFenceStore(NpgsqlDataSource triggerWorkerDataSource) : IAs2ClaimFenceStore
{
    public async ValueTask<bool> TryBeginAsync(ReleaseTargetReference target, TriggerDeliveryClaim claim, As2Attempt attempt, CancellationToken token)
    {
        if (claim.Delivery is not { } delivery || claim.Route is not { } route || delivery.DeliveryId != attempt.DeliveryId) return false;
        await using var connection = await triggerWorkerDataSource.OpenConnectionAsync(token);
        await using var tx = await connection.BeginTransactionAsync(token);
        // Refuse a preset tenant or different login. The function repeats the org/role check inside the transaction.
        await using (var bind = new NpgsqlCommand("""
            SELECT set_config('braid.organization_id',$1,true) WHERE current_user=session_user AND current_user='braid_trigger_worker'
                AND nullif(current_setting('braid.organization_id',true),'') IS NULL
            """, connection, tx))
        {
            var org = target.Mapping.OrganizationId.ToString("D");
            bind.Parameters.AddWithValue(org);
            if (!Equals(await bind.ExecuteScalarAsync(token), org)) throw new InvalidOperationException("AS2 fence session refused.");
        }
        await using var cmd = new NpgsqlCommand("SELECT braid.begin_as2_send($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,$20)", connection, tx);
        foreach (var value in new object[] { target.Mapping.OrganizationId, target.Mapping.ProjectId, target.Mapping.MappingId,
            target.Environment.EnvironmentId, target.Environment.Version, target.TargetId, attempt.DeliveryId, claim.ClaimId, claim.Send,
            delivery.OutputReceiptId, delivery.DestinationBindingId, route.Connector.ConnectorId, route.Connector.Version,
            attempt.BindingVersion, attempt.PayloadSha256, attempt.MessageId, attempt.Mic, attempt.From, attempt.To, attempt.ReceiptCertificateSha256 })
            cmd.Parameters.AddWithValue(value);
        var authorized = await cmd.ExecuteScalarAsync(token) is true;
        await tx.CommitAsync(token); // An uncertain commit must not reach the HTTP client.
        return authorized;
    }
}
