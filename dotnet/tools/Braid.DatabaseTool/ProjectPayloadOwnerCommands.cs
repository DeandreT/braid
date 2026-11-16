using System.Globalization;
using Npgsql;
using Braid.Application;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.DatabaseTool;

/// <summary>One explicit payload grant owner operation; no role, login or payload I/O inference.</summary>
internal static class ProjectPayloadOwnerCommands
{
    internal static bool IsCommand(string[] arguments) => arguments.Length > 0 &&
        arguments[0] == "set-project-payload-read";

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
            var command = ParseArguments(arguments);
            cancellation.Token.ThrowIfCancellationRequested();
            var connectionString = Environment.GetEnvironmentVariable("BRAID_MIGRATION_CONNECTION_STRING");
            if (string.IsNullOrWhiteSpace(connectionString)) return Error("configurationInvalid", 2);
            CommandOutcome outcome;
            await using (var provisioning = new ProjectPayloadGrantProvisioner(connectionString))
            {
                var result = await provisioning.SetAsync(command.Scope, command.PrincipalId, command.Active,
                    command.ExpectedVersion, cancellation.Token);
                outcome = OwnerOutcome(result.Outcome);
            }
            // The acknowledged owner operation and its pool settle before any outcome is printed.
            Console.Out.WriteLine("{\"outcome\":\"" + outcome.Category + "\"}");
            return outcome.Success ? 0 : 3;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { return Error("canceled", 130); }
        catch (OperationCanceledException)
        { return Error("persistenceFailure", 1); }
        catch (ArgumentException)
        { return Error("invalidRequest", 2); }
        catch (NpgsqlException)
        { return Error("persistenceFailure", 1); }
        catch (InvalidOperationException)
        { return Error("persistenceFailure", 1); }
        catch (Exception)
        { return Error("persistenceFailure", 1); }
        finally { Console.CancelKeyPress -= cancel; }
    }

    private static Arguments ParseArguments(string[] arguments) => arguments switch
    {
        ["set-project-payload-read", var organization, var project, var principal, var version, var active] =>
            new(new ProjectScope(Identifier(organization), Identifier(project)), new PrincipalId(Identifier(principal)),
                Version(version), Active(active)),
        _ => throw InvalidArguments()
    };

    private static Guid Identifier(string value)
    {
        if (!Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty ||
            !string.Equals(value, id.ToString("D"), StringComparison.Ordinal))
            throw InvalidArguments();
        return id;
    }

    private static long Version(string value)
    {
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version < 0 ||
            !string.Equals(value, version.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            throw InvalidArguments();
        return version;
    }

    private static bool Active(string value) => value switch
    {
        "true" => true,
        "false" => false,
        _ => throw InvalidArguments()
    };

    private static CommandOutcome OwnerOutcome(ProjectPayloadGrantOwnerOutcome outcome) => outcome switch
    {
        ProjectPayloadGrantOwnerOutcome.Created => new("created", true),
        ProjectPayloadGrantOwnerOutcome.Updated => new("updated", true),
        ProjectPayloadGrantOwnerOutcome.Unchanged => new("unchanged", true),
        ProjectPayloadGrantOwnerOutcome.AuthorityUnavailable => new("authorityUnavailable", false),
        ProjectPayloadGrantOwnerOutcome.VersionConflict => new("versionConflict", false),
        ProjectPayloadGrantOwnerOutcome.VersionExhausted => new("versionExhausted", false),
        _ => throw new InvalidOperationException("The payload grant owner operation returned an unknown outcome.")
    };

    private static int Error(string category, int exitCode)
    {
        Console.Error.WriteLine("{\"error\":\"" + category + "\"}");
        return exitCode;
    }

    private static ArgumentException InvalidArguments()
        => new("The project payload grant owner command arguments are invalid.");

    private readonly record struct CommandOutcome(string Category, bool Success);
    private sealed record Arguments(ProjectScope Scope, PrincipalId PrincipalId, long ExpectedVersion, bool Active)
    {
        public override string ToString() => "ProjectPayloadOwnerArguments";
    }
}
