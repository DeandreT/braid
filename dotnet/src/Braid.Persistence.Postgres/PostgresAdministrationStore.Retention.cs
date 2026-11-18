using System.Text.Json;
using NpgsqlTypes;
using Braid.Application;
using Braid.Application.Retention;
using Braid.Contracts.Administration;
using Braid.Contracts.Projections;

namespace Braid.Persistence.Postgres;

public sealed partial class PostgresAdministrationStore : IRetentionPolicyStore
{
    public async Task<RetentionPoliciesV1?> ReadRetentionAsync(ActorContext actor, Guid organizationId, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using var tx = await BeginAsync(organizationId, token, readOnly: true);
        await using var command = tx.CreateCommand("SELECT braid.read_retention_policies(@org,@actor)::text");
        command.Parameters.AddWithValue("org", organizationId);
        command.Parameters.AddWithValue("actor", actor.PrincipalId.Value);
        if (await command.ExecuteScalarAsync(token) is not string text) return null;
        if (text.Length > 4096) throw InvalidRetention();
        var result = JsonSerializer.Deserialize<RetentionPoliciesV1>(text, ProjectionJson.CreateOptions()) ?? throw InvalidRetention();
        try
        {
            if (result.OrganizationId != organizationId || RetentionFamilyPolicy.ParseCount(result.Version) is null || result.Policies is null)
                throw InvalidRetention();
            RetentionFamilyPolicy.Complete(result.Policies.Select(item =>
            {
                var mode = RetentionFamilyPolicy.ParseMode(item.Mode);
                var configured = mode != RetentionMode.NotConfigured;
                if (configured != (RetentionFamilyPolicy.ParseCount(item.PolicyVersion) is not null) || configured != (item.EffectiveAtUtc is not null))
                    throw InvalidRetention();
                return new RetentionFamilyPolicy(RetentionFamilyPolicy.ParseFamily(item.Family), mode,
                    RetentionFamilyPolicy.ParseCount(item.DurationSeconds), item.AppliesToExisting);
            }));
        }
        catch (ArgumentException) { throw InvalidRetention(); }
        await tx.CommitAsync(token);
        return result;
    }

    public async Task<RetentionPolicyOutcome> ReplaceRetentionAsync(ActorContext actor, Guid organizationId, long expectedVersion,
        IReadOnlyList<RetentionFamilyPolicy> policies, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedVersion, 1);
        var complete = RetentionFamilyPolicy.Complete(policies);
        await using var tx = await BeginAsync(organizationId, token, exclusiveLifecycle: true);
        await using var command = tx.CreateCommand("SELECT braid.replace_retention_policies(@org,@actor,@version,@families,@modes,@durations,@existing)");
        command.Parameters.AddWithValue("org", organizationId);
        command.Parameters.AddWithValue("actor", actor.PrincipalId.Value);
        command.Parameters.AddWithValue("version", expectedVersion);
        command.Parameters.AddWithValue("families", complete.Select(p => RetentionFamilyPolicy.FamilyText(p.Family)).ToArray());
        command.Parameters.AddWithValue("modes", complete.Select(p => RetentionFamilyPolicy.ModeText(p.Mode)).ToArray());
        command.Parameters.AddWithValue("durations", NpgsqlDbType.Array | NpgsqlDbType.Bigint, complete.Select(p => p.DurationSeconds).ToArray());
        command.Parameters.AddWithValue("existing", complete.Select(p => p.ApplyToExisting).ToArray());
        var outcome = await command.ExecuteScalarAsync(token) switch
        {
            "updated" => RetentionPolicyOutcome.Updated,
            "unchanged" => RetentionPolicyOutcome.Unchanged,
            "refused" => RetentionPolicyOutcome.NotAuthorizedOrNotFound,
            "preconditionFailed" => RetentionPolicyOutcome.PreconditionFailed,
            "conflict" => RetentionPolicyOutcome.Conflict,
            "invalidRequest" => RetentionPolicyOutcome.InvalidRequest,
            _ => throw InvalidRetention()
        };
        // A refusal never commits partially appended versions.
        if (outcome is RetentionPolicyOutcome.Updated or RetentionPolicyOutcome.Unchanged) await tx.CommitAsync(token);
        return outcome;
    }

    private static InvalidOperationException InvalidRetention() => new("Retention policy metadata is unavailable.");
}
