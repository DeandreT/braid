using System.Text.Json;
using NpgsqlTypes;
using Braid.Application;
using Braid.Contracts.Administration;
using Braid.Contracts.Projections;
using Braid.Domain;

namespace Braid.Persistence.Postgres;

public sealed partial class PostgresAdministrationStore
{
    public async Task<AiQuotaSnapshotV1?> ReadQuotasAsync(ActorContext actor, Guid organizationId, Guid? principalId, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (principalId == Guid.Empty) throw new ArgumentException("Invalid quota principal.");
        await using var tx = await BeginAsync(organizationId, token, readOnly: true);
        await using var command = tx.CreateCommand("SELECT braid.read_ai_quotas(@org,@actor,@principal)::text");
        command.Parameters.AddWithValue("org", organizationId);
        command.Parameters.AddWithValue("actor", actor.PrincipalId.Value);
        command.Parameters.AddWithValue("principal", NpgsqlDbType.Uuid, (object?)principalId ?? DBNull.Value);
        var value = await command.ExecuteScalarAsync(token);
        if (value is not string text) return null;
        if (text.Length > 4096) throw InvalidQuota();
        var result = JsonSerializer.Deserialize<AiQuotaSnapshotV1>(text, ProjectionJson.CreateOptions()) ?? throw InvalidQuota();
        if (result.OrganizationId != organizationId || result.PrincipalId != principalId || AiQuotaPolicy.ParseLimit(result.Version) is null ||
            result.Limits.Count > 3 || result.Limits.Select(item => item.Period).Distinct(StringComparer.Ordinal).Count() != result.Limits.Count)
            throw InvalidQuota();
        foreach (var limit in result.Limits)
        {
            var policy = new AiQuotaPolicy(principalId, AiQuotaPolicy.ParsePeriod(limit.Period),
                AiQuotaPolicy.ParseLimit(limit.MaximumTurns), AiQuotaPolicy.ParseLimit(limit.MaximumTokens));
            if (policy.Disabled) throw InvalidQuota();
        }
        await tx.CommitAsync(token);
        return result;
    }

    public async Task<string> WriteQuotaAsync(ActorContext actor, Guid organizationId, long expectedVersion, AiQuotaPolicy policy, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(actor); ArgumentNullException.ThrowIfNull(policy);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedVersion, 1);
        await using var tx = await BeginAsync(organizationId, token);
        await using var command = tx.CreateCommand("SELECT braid.write_ai_quota(@org,@actor,@version,@principal,@period,@turns,@tokens)");
        command.Parameters.AddWithValue("org", organizationId);
        command.Parameters.AddWithValue("actor", actor.PrincipalId.Value);
        command.Parameters.AddWithValue("version", expectedVersion);
        command.Parameters.AddWithValue("principal", NpgsqlDbType.Uuid, (object?)policy.PrincipalId ?? DBNull.Value);
        command.Parameters.AddWithValue("period", (int)policy.Period);
        command.Parameters.AddWithValue("turns", NpgsqlDbType.Bigint, (object?)policy.MaximumTurns ?? DBNull.Value);
        command.Parameters.AddWithValue("tokens", NpgsqlDbType.Bigint, (object?)policy.MaximumTokens ?? DBNull.Value);
        var outcome = await command.ExecuteScalarAsync(token) as string;
        if (outcome is not ("updated" or "unchanged" or "refused" or "preconditionFailed" or "invalidRequest")) throw InvalidQuota();
        if (outcome is "updated" or "unchanged") await tx.CommitAsync(token);
        return outcome;
    }

    private static InvalidOperationException InvalidQuota() => new("AI quota metadata is unavailable.");
}
