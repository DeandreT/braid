using System.Text.RegularExpressions;
using Npgsql;

namespace Weft.Persistence.Postgres;

internal static class ReleaseOperationsRoleGuard
{
    internal const string ReleaseFingerprint = "b212d983206cf44edd68cb446588fcf21bf5ad7c89f6c32347429b9cf5f6a25a";
    internal const string OperationsFingerprint = "2600b39004920e6388def0a3c3dffa96942108017594bf145cc1210a424bd554";
    internal static readonly string[] Tables = ["release_target_operations", "release_target_events", "invocation_operations_metadata"];
    internal static readonly string[] Names = ["guard_operations_target", "capture_release_target", "guard_release_operations", "operate_release_target", "read_release_operations", "record_invocation_metadata", "read_release_operation"];
    private static readonly Lazy<Dictionary<string, Match>> Definitions = new(() => BuildRoleGuard.LoadFunctions("0031_release_operations.sql", Names.Length));

    internal static async Task<bool> InstalledAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("SELECT to_regclass('weft.release_target_events') IS NOT NULL", connection, transaction);
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
