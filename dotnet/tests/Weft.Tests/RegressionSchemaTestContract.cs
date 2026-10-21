using Weft.Application;
using Weft.Domain;
using Weft.Deployment;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

/// <summary>Exact additive regression suffix, admitted before older conservation snapshots exclude it.</summary>
internal static class RegressionSchemaTestContract
{
    internal const string CasesChecksum = "ec00cf3e9869eb59f89eec5d0e7d38d672112970e9cb2beae41c6a3a88f204a1";
    internal const string RunsChecksum = "943b1df0d034568fc0eba4f26c8a7c06c2936fad60e9c6a322054efc83925efc";
    internal const string DispatchChecksum = "51d8fdab1a338cc2c903223f0c45c0469f193638462dfc10f828762262510539";
    internal const string HistoryChecksum = "0ce99a72cc4822ec9b09710becf297436d36d966c23d88cb8212e0d04b0bc89a";
    internal const string BuildChecksum = "5c4f348a577ac36d7d4e2410318e26cee11521bcfb879e620af54705e39d03d2";
    internal static readonly string[] Tables = ["regression_cases", "regression_case_versions", "regression_runs", .. BuildRoleGuard.Tables];
    internal static readonly string[] Functions = ["case_authorized", "guard_regression_case", "guard_regression_version",
        "save_regression_case", "read_regression_case", "list_regression_cases", "regression_run_authorized", "guard_regression_run",
        "submit_regression_run", "claim_regression_run", "observe_regression_run", "finish_regression_run", "release_regression_run",
        "cancel_regression_run", "read_regression_run", "list_ready_regression_runs", "list_regression_run_history", .. BuildRoleGuard.Names];
    internal static readonly string[] Roles = ["weft_case_reader", "weft_case_writer", "weft_run_reader", "weft_run_writer", "weft_build_reader", "weft_build_writer"];

    internal static async Task AdmitAsync(PostgresCase test, CancellationToken token = default)
    {
        var actor = new ActorContext(new(Guid.NewGuid()));
        var mapping = new MappingScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        foreach (var profile in new[] { RegressionCaseDatabaseProfile.Reader, RegressionCaseDatabaseProfile.Writer })
        {
            var role = profile == RegressionCaseDatabaseProfile.Reader ? "weft_case_reader" : "weft_case_writer";
            await using var store = new PostgresRegressionCaseStore(test.RegressionConnectionString(role), profile);
            Assert.Null(await store.ReadAsync(actor, mapping, Guid.NewGuid(), 1, token));
        }
        foreach (var writer in new[] { false, true })
        {
            await using var store = new PostgresRegressionRunStore(test.RegressionConnectionString(writer ? "weft_run_writer" : "weft_run_reader"), writer);
            Assert.Null(await store.ReadAsync(actor, mapping.OrganizationId, Guid.NewGuid(), token));
        }
        foreach (var writer in new[] { false, true })
        {
            await using var store = new PostgresBuildStore(test.RegressionConnectionString(writer ? "weft_build_writer" : "weft_build_reader"), writer);
            Assert.Null(await store.ReadEnvironmentAsync(actor, new(new(mapping.OrganizationId, mapping.ProjectId), Guid.NewGuid(), 1), token));
        }
    }
}
