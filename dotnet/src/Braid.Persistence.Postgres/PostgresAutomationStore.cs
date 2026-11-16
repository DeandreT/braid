using System.Data;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Braid.Application;
using Braid.Application.Automation;
using Braid.Application.Regression;
using Braid.Domain;

namespace Braid.Persistence.Postgres;

/// <summary>Uses only the admitted run profile and closed automation functions; never direct table privileges.</summary>
public sealed class PostgresAutomationStore(string connectionString, bool writer) : IAutomationStore, IAutomationQueue, IAsyncDisposable
{
    private readonly NpgsqlDataSource source = NpgsqlDataSource.Create(PostgresConfiguration.Normalize(connectionString));

    public Task<JsonElement?> ReadInboxAsync(ActorContext actor, Guid organizationId, Guid? after, int limit, CancellationToken token = default)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        return CallAsync("SELECT braid.automation_inbox(@org,@after,@maximum)", actor, organizationId, command =>
        {
            command.Parameters.AddWithValue("org", organizationId);
            Add(command, "after", NpgsqlDbType.Uuid, after); command.Parameters.AddWithValue("maximum", limit);
        }, false, token);
    }

    public async Task<bool> MarkInboxReadAsync(ActorContext actor, Guid organizationId, Guid eventId, CancellationToken token = default)
    {
        var result = await CallAsync("SELECT to_jsonb(braid.automation_read_notification(@org,@id))", actor, organizationId, command =>
        {
            command.Parameters.AddWithValue("org", organizationId); command.Parameters.AddWithValue("id", eventId);
        }, true, token).ConfigureAwait(false);
        return result?.ValueKind == JsonValueKind.True;
    }

    public async Task<JsonElement?> ReadAsync(ActorContext actor, ProjectScope project, string operation, Guid? resource,
        Guid? after, int limit, CancellationToken token = default)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        return await CallAsync("SELECT braid.automation_read(@org,@project,@operation,@resource,@after,@maximum)", actor,
            project.OrganizationId, command =>
            {
                Scope(command, project, operation, resource);
                Add(command, "after", NpgsqlDbType.Uuid, after); command.Parameters.AddWithValue("maximum", limit);
            }, false, token).ConfigureAwait(false);
    }

    public async Task<bool> WriteAsync(ActorContext actor, ProjectScope project, string operation, Guid? resource,
        JsonElement value, CancellationToken token = default)
    {
        var result = await CallAsync("SELECT to_jsonb(braid.automation_write(@org,@project,@operation,@resource,@body))", actor,
            project.OrganizationId, command =>
            {
                Scope(command, project, operation, resource);
                command.Parameters.AddWithValue("body", NpgsqlDbType.Jsonb, value.GetRawText());
            }, true, token).ConfigureAwait(false);
        return result?.ValueKind == JsonValueKind.True;
    }

    public async Task<AutomationClaim?> ClaimEvaluationAsync(CancellationToken token)
    {
        var result = await CallAsync("SELECT braid.automation_claim_evaluation()", null, null, _ => { }, true, token).ConfigureAwait(false);
        if (result is not { } row) return null;
        var org = Id(row, "organizationId"); var project = Id(row, "projectId"); var mapping = Id(row, "mappingId");
        return new(org, Id(row, "suiteId"), Id(row, "caseId"), row.GetProperty("version").GetInt64(), new(Id(row, "actorId")),
            new(Id(row, "caseId"), row.GetProperty("version").GetInt64(), new(Id(row, "creatorId")),
                new(org, project, mapping, Id(row, "originalRevisionId")), Sha256Digest.Parse(Text(row, "nativeDigest")),
                Sha256Digest.Parse(Text(row, "caseDigest")), row.GetProperty("caseBytes").GetInt32()),
            new(new(org, project, mapping, Id(row, "targetRevisionId")), Sha256Digest.Parse(Text(row, "targetDigest"))),
            Id(row, "token"), row.GetProperty("attempt").GetInt32());
    }

    public async Task<bool> FinishEvaluationAsync(AutomationClaim claim, RegressionCaseExecution? result, CancellationToken token)
    {
        var bytes = result is null ? null : new RegressionRunResultCodec().Encode(claim.SuiteId, claim.Original, result, token);
        var response = await CallAsync("SELECT to_jsonb(braid.automation_finish_evaluation(@org,@suite,@case,@claim,@generation,@body))",
            null, claim.OrganizationId, command =>
            {
                command.Parameters.AddWithValue("org", claim.OrganizationId); command.Parameters.AddWithValue("suite", claim.SuiteId);
                command.Parameters.AddWithValue("case", claim.CaseId); command.Parameters.AddWithValue("claim", claim.Token);
                command.Parameters.AddWithValue("generation", claim.Attempt); Add(command, "body", NpgsqlDbType.Bytea, bytes);
            }, true, token).ConfigureAwait(false);
        return response?.ValueKind == JsonValueKind.True;
    }

    public async Task<NotificationDeliveryClaim?> ClaimDeliveryAsync(CancellationToken token)
    {
        var result = await CallAsync("SELECT braid.automation_claim_delivery()", null, null, _ => { }, true, token).ConfigureAwait(false);
        if (result is not { } row) return null;
        var message = row.GetProperty("message");
        return new(Id(row, "organizationId"), Id(row, "id"), Id(row, "projectId"), new(Id(row, "recipientId")), Text(row, "destination"),
            new(Id(message, "id"), Id(message, "organizationId"), Id(message, "projectId"), Text(message, "event"),
                NullableId(message, "mappingId"), NullableId(message, "revisionId"), Id(message, "resourceId"), Text(message, "link"),
                message.GetProperty("createdAt").GetDateTimeOffset()), Id(row, "token"), row.GetProperty("attempt").GetInt32());
    }

    public async Task<bool> FinishDeliveryAsync(NotificationDeliveryClaim claim, NotificationDeliveryOutcome outcome, CancellationToken token)
    {
        var response = await CallAsync("SELECT to_jsonb(braid.automation_finish_delivery(@org,@id,@claim,@generation,@outcome))", null,
            claim.OrganizationId, command =>
            {
                command.Parameters.AddWithValue("org", claim.OrganizationId); command.Parameters.AddWithValue("id", claim.Id);
                command.Parameters.AddWithValue("claim", claim.Token); command.Parameters.AddWithValue("generation", claim.Attempt);
                command.Parameters.AddWithValue("outcome", JsonNamingPolicy.CamelCase.ConvertName(outcome.ToString()));
            }, true, token).ConfigureAwait(false);
        return response?.ValueKind == JsonValueKind.True;
    }

    private async Task<JsonElement?> CallAsync(string sql, ActorContext? actor, Guid? org, Action<NpgsqlCommand> arguments, bool write, CancellationToken token)
    {
        if (write && !writer) throw new InvalidOperationException("Automation writer required.");
        await using var connection = await source.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, token).ConfigureAwait(false);
        await using (var settings = new NpgsqlCommand("SET LOCAL search_path=pg_catalog; SET LOCAL TimeZone='UTC'; SET LOCAL statement_timeout='15s'; SET LOCAL lock_timeout='5s'", connection, transaction))
            await settings.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        await RegressionRunRoleGuard.ValidateAsync(connection, transaction, writer, token).ConfigureAwait(false);
        await AutomationSchemaAdmission.ValidateAsync(connection, transaction, token).ConfigureAwait(false);
        await using (var settings = new NpgsqlCommand("SELECT set_config('braid.organization_id',@org,true),set_config('braid.run_principal_id',@actor,true)", connection, transaction))
        {
            settings.Parameters.AddWithValue("org", org?.ToString("D") ?? ""); settings.Parameters.AddWithValue("actor", actor?.PrincipalId.Value.ToString("D") ?? "");
            await settings.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        arguments(command);
        var value = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
        JsonElement? result = null;
        if (value is string text)
        {
            using var document = JsonDocument.Parse(text);
            result = document.RootElement.Clone();
        }
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return result;
    }

    private static void Scope(NpgsqlCommand command, ProjectScope project, string operation, Guid? resource)
    {
        command.Parameters.AddWithValue("org", project.OrganizationId); command.Parameters.AddWithValue("project", project.ProjectId);
        command.Parameters.AddWithValue("operation", operation); Add(command, "resource", NpgsqlDbType.Uuid, resource);
    }
    private static void Add(NpgsqlCommand command, string name, NpgsqlDbType type, object? value) => command.Parameters.AddWithValue(name, type, value ?? DBNull.Value);
    private static string Text(JsonElement row, string name) => row.GetProperty(name).GetString() ?? throw new InvalidOperationException("Automation metadata missing.");
    private static Guid Id(JsonElement row, string name) => row.GetProperty(name).GetGuid();
    private static Guid? NullableId(JsonElement row, string name) => row.GetProperty(name).ValueKind == JsonValueKind.Null ? null : Id(row, name);
    public ValueTask DisposeAsync() => source.DisposeAsync();
}
