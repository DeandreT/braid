using Braid.Deployment;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
public sealed class OperationsAutomationAdmissionTests(PostgresFixture fixture)
{
    [RegressionPostgresTheory]
    [InlineData("automation_release_event", "local_release_audit")]
    [InlineData("automation_endpoint_event", "machine_invocation_audit")]
    public async Task Shadow_notification_functions_refuse_operations_reads_and_writes_until_repaired(string function, string table)
    {
        await using var data = await LocalReleasePersistenceTests.Context.CreateAsync(fixture);
        await using var reader = new PostgresLocalReleaseStore(data.Case.RegressionConnectionString("braid_release_reader"), false);
        await using var administration = new PostgresAdministrationStore(data.Case.RegressionConnectionString("braid_administration"));
        var target = data.Target.Reference;
        var service = new ReleaseOperationsService(data.Store, reader, data.Qualifications);
        var operation = new ReleaseOperation(Guid.NewGuid(), "disable", 1);
        Assert.NotNull(await reader.ReadAsync(data.Actor, target.Mapping, null, new("targets")));
        Assert.Null(await administration.ReadOrganizationAsync(data.Actor, target.Mapping.OrganizationId, default));
        var definition = await data.Case.ScalarAsync<string>(
            "SELECT pg_get_triggerdef(oid) FROM pg_trigger WHERE tgrelid=$1::regclass AND tgname=$2", "braid." + table, function);
        var original = await data.Case.ScalarAsync<string>("SELECT pg_get_functiondef($1::regprocedure)", "braid." + function + "()");
        var call = "EXECUTE FUNCTION braid." + function + "()";
        Assert.Contains(call, definition, StringComparison.Ordinal);
        var shadow = definition.Replace("CREATE TRIGGER", "CREATE OR REPLACE TRIGGER", StringComparison.Ordinal)
            .Replace(call, "EXECUTE FUNCTION admission_shadow." + function + "()", StringComparison.Ordinal);
        await data.Case.ExecuteAsync($"""
            CREATE SCHEMA admission_shadow AUTHORIZATION braid_migrator;
            CREATE FUNCTION admission_shadow.{function}() RETURNS trigger LANGUAGE plpgsql AS $$BEGIN RETURN NEW; END$$;
            REVOKE ALL ON FUNCTION admission_shadow.{function}() FROM PUBLIC;
            {shadow};
            """);
        Assert.Equal(original, await data.Case.ScalarAsync<string>("SELECT pg_get_functiondef($1::regprocedure)", "braid." + function + "()"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync(data.Actor, target.Mapping, null, new("targets")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.OperateAsync(data.Actor, target, operation));
        await Assert.ThrowsAsync<InvalidOperationException>(() => administration.ReadOrganizationAsync(data.Actor, target.Mapping.OrganizationId, default));
        Assert.True(await data.Case.ScalarAsync<bool>("SELECT enabled FROM braid.local_release_targets WHERE target_id=$1", target.TargetId));
        Assert.Equal(0L, await data.Case.ScalarAsync<long>("SELECT count(*) FROM braid.release_target_operations"));

        await data.Case.ExecuteAsync($"DROP TRIGGER {function} ON braid.{table}; {definition}; DROP SCHEMA admission_shadow CASCADE;");
        Assert.NotNull(await reader.ReadAsync(data.Actor, target.Mapping, null, new("targets")));
        Assert.Null(await administration.ReadOrganizationAsync(data.Actor, target.Mapping.OrganizationId, default));
        Assert.Equal(ReleaseTransitionOutcome.Activated, await service.OperateAsync(data.Actor, target, operation));
        Assert.False(await data.Case.ScalarAsync<bool>("SELECT enabled FROM braid.local_release_targets WHERE target_id=$1", target.TargetId));
        Assert.Equal(1L, await data.Case.ScalarAsync<long>("SELECT count(*) FROM braid.release_target_operations"));
    }
}
