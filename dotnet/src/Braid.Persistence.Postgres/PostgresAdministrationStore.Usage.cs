using System.Globalization;
using System.Text.Json;
using NpgsqlTypes;
using Braid.Application;
using Braid.Contracts.Administration;
using Braid.Contracts.Projections;
using Braid.Domain;

namespace Braid.Persistence.Postgres;

public sealed partial class PostgresAdministrationStore
{
    public async Task<AiUsagePageV1?> ReadUsageAsync(ActorContext actor, AiUsageQuery query, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(actor); ArgumentNullException.ThrowIfNull(query);
        await using var tx = await BeginAsync(query.OrganizationId, token, readOnly: true);
        await using var command = tx.CreateCommand("SELECT braid.read_ai_usage(@org,@actor,@project,@principal,@profile,@version,@grouping,@since,@until,@after,@limit)::text");
        Scope(command, query.OrganizationId, actor.PrincipalId.Value, query.ProjectId);
        command.Parameters.AddWithValue("principal", NpgsqlDbType.Uuid, (object?)query.PrincipalId ?? DBNull.Value);
        command.Parameters.AddWithValue("profile", NpgsqlDbType.Text, (object?)query.ProfileId ?? DBNull.Value);
        command.Parameters.AddWithValue("version", NpgsqlDbType.Integer, (object?)query.ProfileVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("grouping", query.Grouping switch
        {
            AiUsageGrouping.Organization => "organization",
            AiUsageGrouping.Project => "project",
            AiUsageGrouping.Principal => "principal",
            AiUsageGrouping.ModelProfile => "modelProfile",
            _ => throw new ArgumentException("Invalid usage grouping.")
        });
        command.Parameters.AddWithValue("since", query.Since); command.Parameters.AddWithValue("until", query.Until);
        command.Parameters.AddWithValue("after", NpgsqlDbType.Text, (object?)query.Position ?? DBNull.Value);
        command.Parameters.AddWithValue("limit", query.Limit);
        var value = await command.ExecuteScalarAsync(token);
        if (value is not string text) return null;
        if (text.Length > 512 * 1024) throw InvalidUsage();
        var page = JsonSerializer.Deserialize<StoredUsagePage>(text, ProjectionJson.CreateOptions()) ?? throw InvalidUsage();
        if (page.Groups.Count > query.Limit || page.HasMore && page.Groups.Count != query.Limit) throw InvalidUsage();
        if (page.ObservedAtUtc.Offset != TimeSpan.Zero || page.CompleteHistorySince.Offset != TimeSpan.Zero ||
            page.CompleteHistorySince > page.ObservedAtUtc || page.IncludesPartialHistory != (query.Since < page.CompleteHistorySince)) throw InvalidUsage();
        var previous = query.Position;
        foreach (var group in page.Groups)
        {
            string key;
            try { key = query.GroupKey(group); }
            catch (ArgumentException) { throw InvalidUsage(); }
            if (previous is not null && string.CompareOrdinal(key, previous) <= 0 ||
                query.ProjectId is { } project && group.ProjectId != project ||
                query.PrincipalId is { } principal && group.PrincipalId != principal ||
                query.ProfileId is { } profile && group.ProfileId != profile ||
                query.ProfileVersion is { } version && group.ProfileVersion != version) throw InvalidUsage();
            ValidateUsage(group.Usage);
            previous = key;
        }
        if (page.HasMore && page.LastKey != previous) throw InvalidUsage();
        await tx.CommitAsync(token);
        return new(query.Since, query.Until, page.Groups, page.HasMore ? query.Cursor(previous!) : null, page.OrganizationWide,
            page.ObservedAtUtc, page.CompleteHistorySince, page.IncludesPartialHistory);
    }

    private static void ValidateUsage(AiUsageTotalsV1 usage)
    {
        static bool Tokens(string? complete, string known, string unknown, long rounds) => Count(unknown) <= rounds &&
            (Count(unknown) > 0 ? complete is null : complete is not null && Count(complete) == Count(known));
        var rounds = Count(usage.Rounds);
        if (Count(usage.Turns) < 1 || Count(usage.FailedTurns) > Count(usage.Turns) ||
            !Tokens(usage.InputTokens, usage.KnownInputTokens, usage.UnknownInputRounds, rounds) ||
            !Tokens(usage.OutputTokens, usage.KnownOutputTokens, usage.UnknownOutputRounds, rounds) ||
            !Tokens(usage.TotalTokens, usage.KnownTotalTokens, usage.UnknownTotalRounds, rounds) ||
            usage.Failures.Count > 20 || usage.Failures.Select(item => item.Category).Distinct(StringComparer.Ordinal).Count() != usage.Failures.Count ||
            usage.Failures.Any(item => Count(item.Count) < 1 ||
                (item.Category is "TurnFailed" or "TurnCancelled" or "TurnInterrupted"
                    ? Count(item.Count) > Count(usage.FailedTurns)
                    : Count(item.Count) > rounds || !Enum.TryParse<ChatProviderFailureCode>(item.Category, out var code) ||
                        !Enum.IsDefined(code) || item.Category != code.ToString())))
            throw InvalidUsage();
        _ = Count(usage.ToolCalls);
        _ = Count(usage.KnownInputTokens); _ = Count(usage.KnownOutputTokens); _ = Count(usage.KnownTotalTokens);
    }

    private static long Count(string value) => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count >= 0 &&
        value == count.ToString(CultureInfo.InvariantCulture) ? count : throw InvalidUsage();

    private sealed record StoredUsagePage(IReadOnlyList<AiUsageGroupV1> Groups, bool HasMore, string? LastKey, bool OrganizationWide,
        DateTimeOffset ObservedAtUtc, DateTimeOffset CompleteHistorySince, bool IncludesPartialHistory);
    private static InvalidOperationException InvalidUsage() => new("Stored usage metadata is invalid.");
}
