using System.Text.RegularExpressions;
using Npgsql;

namespace Braid.Persistence.Postgres;

internal static class ReleaseOperationsRoleGuard
{
    internal const string ReleaseFingerprint = "47285d0ae4aa6ee0731f18226541cbb451df15b58ca6c330ddb6caed31ca50b4";
    internal const string OperationsFingerprint = "06d45fb26181851c86b5955b92c615fa5213f79fb96e7d8244aec7510d0b6fc8";
    internal static readonly string[] Tables = ["release_target_operations", "release_target_events", "invocation_operations_metadata"];
    internal static readonly string[] Names = ["guard_operations_target", "capture_release_target", "guard_release_operations", "operate_release_target", "read_release_operations", "record_invocation_metadata", "read_release_operation"];
    private static readonly Lazy<Dictionary<string, Match>> Definitions = new(() => BuildRoleGuard.LoadFunctions("0031_release_operations.sql", Names.Length));

    internal static async Task<bool> InstalledAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("SELECT to_regclass('braid.release_target_events') IS NOT NULL", connection, transaction);
        return await command.ExecuteScalarAsync(token) is true;
    }

    internal static async Task ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        await BuildRoleGuard.ValidateFunctionsAsync(connection, transaction, Names, Definitions.Value, token);
        if (await RegressionCaseSchemaContract.CaptureAsync(connection, transaction, Tables, token) != OperationsFingerprint ||
            await RegressionCaseSchemaContract.CaptureAsync(connection, transaction, LocalReleaseRoleGuard.Tables, token) != ReleaseFingerprint)
            throw new InvalidOperationException("Release operations database contract unavailable.");
        await MachineInvocationRoleGuard.ValidateLedgerContractAsync(connection, transaction, token);
    }
}
