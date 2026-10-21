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
    internal const string BuildDispatchChecksum = "3f1262c5b3e538e472b5e015b5fdb8e683983505faebc24a9057816f4aee3cfc";
    internal const string BuildMetadataChecksum = "cfc43e570c53b8f2f830de4fcbe7b0e06ff0b8acc1a49c8eb7d7003d69110627";
    internal static readonly string[] Tables = ["regression_cases", "regression_case_versions", "regression_runs", .. BuildRoleGuard.Tables, .. BuildRunRoleGuard.Tables];
    internal static readonly string[] Functions = ["case_authorized", "guard_regression_case", "guard_regression_version",
        "save_regression_case", "read_regression_case", "list_regression_cases", "regression_run_authorized", "guard_regression_run",
        "submit_regression_run", "claim_regression_run", "observe_regression_run", "finish_regression_run", "release_regression_run",
        "cancel_regression_run", "read_regression_run", "list_ready_regression_runs", "list_regression_run_history", .. BuildRoleGuard.Names, .. BuildRunRoleGuard.Names, .. BuildMetadataRoleGuard.Names];
    internal static readonly string[] Roles = ["weft_case_reader", "weft_case_writer", "weft_run_reader", "weft_run_writer", "weft_build_reader", "weft_build_writer",
        "weft_build_run_reader", "weft_build_run_writer", "weft_build_dispatcher", "weft_build_metadata_reader"];

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
        foreach (var profile in new[] { BuildRunDatabaseProfile.Reader, BuildRunDatabaseProfile.Writer })
        {
            await using var store = new PostgresBuildRunStore(test.RegressionConnectionString(profile == BuildRunDatabaseProfile.Reader ? "weft_build_run_reader" : "weft_build_run_writer"), profile);
            Assert.Null(await store.ReadAsync(actor, mapping.OrganizationId, Guid.NewGuid(), token));
        }
        await using var dispatcher = new PostgresBuildRunStore(test.RegressionConnectionString("weft_build_dispatcher"), BuildRunDatabaseProfile.Dispatcher);
        Assert.True((await dispatcher.DiscoverAsync(1, token)).Count <= 1);
        await using var metadata = new PostgresBuildMetadataReader(test.RegressionConnectionString("weft_build_metadata_reader"));
        Assert.Null(await metadata.ReadEnvironmentAsync(actor, new(new(mapping.OrganizationId, mapping.ProjectId), Guid.NewGuid(), 1), token));
    }
}
