using System.Globalization;
using Npgsql;
using Braid.Application;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.DatabaseTool;

/// <summary>Explicit owner commands, separate from legacy Development provisioning.</summary>
internal static class ExternalIdentityOwnerCommands
{
    internal static bool IsCommand(string[] arguments) => arguments.Length > 0 && arguments[0] is
        "ensure-principal" or "ensure-organization-admission" or "bind-external-identity" or "disable-external-identity" or
        "bind-entra-user" or "disable-entra-user";

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
            ExternalIdentityKey? key = null;
            if (command.Kind is CommandKind.Bind or CommandKind.Disable)
            {
                using var input = Console.OpenStandardInput();
                key = command.EntraUser ? await ExternalIdentityOwnerInput.ReadEntraUserAsync(input, cancellation.Token)
                    : await ExternalIdentityOwnerInput.ReadAsync(input, cancellation.Token);
                // Entra keys are bound only through the Entra user commands, which fix their canonical layout. A
                // retired ("entra", issuer, sub) key can still be disabled, but never bound again: nothing resolves it.
                if (!command.EntraUser && command.Kind == CommandKind.Bind &&
                    key.Provider is EntraUserIdentity.Provider or EntraUserIdentity.RetiredSubjectProvider)
                    throw InvalidArguments();
            }
            cancellation.Token.ThrowIfCancellationRequested();
            CommandOutcome outcome;
            await using (var provisioning = new ExternalIdentityBindingProvisioner(connectionString))
            {
                outcome = command.Kind switch
                {
                    CommandKind.EnsurePrincipal => PrincipalOutcome(
                        await provisioning.EnsurePrincipalAsync(command.PrincipalId, cancellation.Token)),
                    CommandKind.EnsureOrganizationAdmission => AdmissionOutcome(
                        await provisioning.EnsureOrganizationAdmissionAsync(command.OrganizationId, command.PrincipalId, cancellation.Token)),
                    CommandKind.Bind => BindingOutcome(
                        await provisioning.BindAsync(key!, command.PrincipalId, cancellation.Token)),
                    CommandKind.Disable => BindingOutcome(
                        await provisioning.DisableAsync(key!, command.PrincipalId, command.Version, cancellation.Token)),
                    _ => throw new InvalidOperationException("The owner command kind is invalid.")
                };
            }
            // The acknowledged DAL operation and its owned pool settle before any outcome is printed.
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
        ["ensure-principal", var principal] =>
            new(CommandKind.EnsurePrincipal, Principal(principal), Guid.Empty, 0, false),
        ["ensure-organization-admission", var organization, var principal] =>
            new(CommandKind.EnsureOrganizationAdmission, Principal(principal), Identifier(organization), 0, false),
        ["bind-external-identity", var principal] =>
            new(CommandKind.Bind, Principal(principal), Guid.Empty, 0, false),
        ["disable-external-identity", var principal, var version] =>
            new(CommandKind.Disable, Principal(principal), Guid.Empty, Version(version), false),
        ["bind-entra-user", var principal] =>
            new(CommandKind.Bind, Principal(principal), Guid.Empty, 0, true),
        ["disable-entra-user", var principal, var version] =>
            new(CommandKind.Disable, Principal(principal), Guid.Empty, Version(version), true),
        _ => throw InvalidArguments()
    };

    private static PrincipalId Principal(string value) => new(Identifier(value));

    private static Guid Identifier(string value)
    {
        if (!Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty ||
            !string.Equals(value, id.ToString("D"), StringComparison.Ordinal))
            throw InvalidArguments();
        return id;
    }

    private static long Version(string value)
    {
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version < 1 ||
            !string.Equals(value, version.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            throw InvalidArguments();
        return version;
    }

    private static CommandOutcome PrincipalOutcome(ExternalIdentityPrincipalPreparationOutcome outcome)
        => outcome switch
        {
            ExternalIdentityPrincipalPreparationOutcome.Created => new("created", true),
            ExternalIdentityPrincipalPreparationOutcome.Existing => new("existing", true),
            ExternalIdentityPrincipalPreparationOutcome.Inactive => new("inactive", false),
            _ => throw new InvalidOperationException("The owner operation returned an unknown outcome.")
        };

    private static CommandOutcome AdmissionOutcome(ExternalIdentityOrganizationAdmissionPreparationOutcome outcome)
        => outcome switch
        {
            ExternalIdentityOrganizationAdmissionPreparationOutcome.Created => new("created", true),
            ExternalIdentityOrganizationAdmissionPreparationOutcome.Existing => new("existing", true),
            ExternalIdentityOrganizationAdmissionPreparationOutcome.OrganizationMissing => new("organizationMissing", false),
            ExternalIdentityOrganizationAdmissionPreparationOutcome.PrincipalMissing => new("principalMissing", false),
            ExternalIdentityOrganizationAdmissionPreparationOutcome.PrincipalInactive => new("principalInactive", false),
            ExternalIdentityOrganizationAdmissionPreparationOutcome.AdmissionInactive => new("admissionInactive", false),
            _ => throw new InvalidOperationException("The owner operation returned an unknown outcome.")
        };

    private static CommandOutcome BindingOutcome(ExternalIdentityBindingOwnerResult result)
        => result.Outcome switch
        {
            ExternalIdentityBindingOwnerOutcome.Created => new("created", true),
            ExternalIdentityBindingOwnerOutcome.Existing => new("existing", true),
            ExternalIdentityBindingOwnerOutcome.Disabled => new("disabled", true),
            ExternalIdentityBindingOwnerOutcome.AlreadyDisabled => new("alreadyDisabled", true),
            ExternalIdentityBindingOwnerOutcome.UnknownBinding => new("unknownBinding", false),
            ExternalIdentityBindingOwnerOutcome.BindingConflict => new("bindingConflict", false),
            ExternalIdentityBindingOwnerOutcome.PrincipalMissing => new("principalMissing", false),
            ExternalIdentityBindingOwnerOutcome.PrincipalInactive => new("principalInactive", false),
            ExternalIdentityBindingOwnerOutcome.OwnerConflict => new("ownerConflict", false),
            ExternalIdentityBindingOwnerOutcome.VersionConflict => new("versionConflict", false),
            ExternalIdentityBindingOwnerOutcome.VersionExhausted => new("versionExhausted", false),
            _ => throw new InvalidOperationException("The owner operation returned an unknown outcome.")
        };

    private static int Error(string category, int exitCode)
    {
        Console.Error.WriteLine("{\"error\":\"" + category + "\"}");
        return exitCode;
    }

    private static ArgumentException InvalidArguments()
        => new("The external identity owner command arguments are invalid.");

    private enum CommandKind { EnsurePrincipal, EnsureOrganizationAdmission, Bind, Disable }
    private readonly record struct CommandOutcome(string Category, bool Success);
    private sealed record Arguments(CommandKind Kind, PrincipalId PrincipalId, Guid OrganizationId, long Version, bool EntraUser);
}
