using System.Globalization;
using System.Text.Json;
using Npgsql;
using Braid.Application;
using Braid.Deployment;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.DatabaseTool;

/// <summary>
/// Machine invocation grant administration. Commands run through the restricted grant-writer login as one named
/// principal, and the database requires that principal's current administrator role on the target's project.
/// </summary>
internal static class InvocationGrantCommands
{
    private const string ConnectionSetting = "BRAID_INVOCATION_GRANT_WRITER_CONNECTION_STRING";

    internal static bool IsCommand(string[] arguments) => arguments.Length > 0 && arguments[0] is
        "grant-machine-invocation" or "revoke-machine-invocation" or "list-machine-invocation-grants";

    internal static async Task<int> RunAsync(string[] arguments)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, signal) =>
        {
            signal.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancel;
        try
        {
            var (command, administrator, target, values) = Parse(arguments);
            var connectionString = Environment.GetEnvironmentVariable(ConnectionSetting);
            if (string.IsNullOrWhiteSpace(connectionString)) return Error("configurationInvalid", 2);
            await using var store = new PostgresInvocationGrantStore(connectionString);
            if (command == "list-machine-invocation-grants")
            {
                var grants = await store.ReadAsync(administrator, target, cancellation.Token);
                Console.Out.WriteLine(JsonSerializer.Serialize(grants.Select(grant => new
                {
                    grantId = grant.GrantId.ToString("D"),
                    provider = grant.Principal.Provider,
                    tenantId = grant.Principal.TenantId.ToString("D"),
                    objectId = grant.Principal.ObjectId.ToString("D"),
                    grantedBy = grant.GrantedBy.Value.ToString("D"),
                    grantedAt = grant.GrantedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                    revokedBy = grant.RevokedBy?.Value.ToString("D"),
                    revokedAt = grant.RevokedAt?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)
                })));
                return 0;
            }
            var outcome = command == "grant-machine-invocation"
                ? await store.GrantAsync(administrator, new(values[0], target, new(MachinePrincipal.Entra, values[1], values[2])), cancellation.Token)
                : await store.RevokeAsync(administrator, target, values[0], cancellation.Token);
            // The acknowledged operation settles before any outcome is printed.
            Console.Out.WriteLine("{\"outcome\":\"" + outcome switch
            {
                InvocationGrantOutcome.Applied => command == "grant-machine-invocation" ? "granted" : "revoked",
                InvocationGrantOutcome.Replayed => "replayed",
                InvocationGrantOutcome.Conflict => "conflict",
                _ => "refused"
            } + "\"}");
            return outcome is InvocationGrantOutcome.Applied or InvocationGrantOutcome.Replayed ? 0 : 3;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return Error("canceled", 130); }
        catch (ArgumentException) { return Error("invalidRequest", 2); }
        catch (Exception error) when (error is NpgsqlException or InvalidOperationException or OperationCanceledException)
        { return Error("persistenceFailure", 1); }
        finally { Console.CancelKeyPress -= cancel; }
    }

    private static (string Command, ActorContext Administrator, ReleaseTargetReference Target, Guid[] Values) Parse(string[] arguments)
    {
        var extra = arguments.Length > 0 ? arguments[0] switch
        {
            "grant-machine-invocation" => 3,
            "revoke-machine-invocation" => 1,
            "list-machine-invocation-grants" => 0,
            _ => -1
        } : -1;
        // COMMAND ADMINISTRATOR ORGANIZATION PROJECT MAPPING ENVIRONMENT VERSION TARGET [GRANT [TENANT OBJECT]]
        if (extra < 0 || arguments.Length != 8 + extra) throw new ArgumentException("Invalid grant command arguments.");
        var organization = Identifier(arguments[2]);
        var project = Identifier(arguments[3]);
        if (!long.TryParse(arguments[6], NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version < 1 ||
            arguments[6] != version.ToString(CultureInfo.InvariantCulture))
            throw new ArgumentException("Invalid environment version.");
        var target = new ReleaseTargetReference(new(organization, project, Identifier(arguments[4])),
            new(new(organization, project), Identifier(arguments[5]), version), Identifier(arguments[7]));
        return (arguments[0], new(new PrincipalId(Identifier(arguments[1]))), target, arguments.Skip(8).Select(Identifier).ToArray());
    }

    private static Guid Identifier(string value) =>
        Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty && value == id.ToString("D") ? id : throw new ArgumentException("Invalid identifier.");

    private static int Error(string category, int exitCode)
    {
        Console.Error.WriteLine("{\"error\":\"" + category + "\"}");
        return exitCode;
    }
}
