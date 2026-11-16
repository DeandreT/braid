using System.Text.Json;
using Npgsql;
using Braid.Application;
using Braid.Application.Regression;
using Braid.Deployment;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
public sealed class AutomationSchemaAdmissionTests(PostgresFixture fixture)
{
    [RegressionPostgresTheory]
    [InlineData("automation_build_guard", "build_approvals", "BEFORE INSERT", "automation_build_guard")]
    [InlineData("automation_qualification_guard", "build_qualifications", "BEFORE INSERT", "automation_build_guard")]
    [InlineData("automation_invitation_event", "organization_invitations", "AFTER INSERT OR UPDATE", "automation_invitation_event")]
    public async Task Same_named_shadow_functions_are_not_the_admitted_trigger_identity(string trigger, string table, string events, string function)
    {
        await using var test = await fixture.CreateCaseAsync();
        var definition = await test.ScalarAsync<string>("SELECT pg_get_triggerdef(oid) FROM pg_trigger WHERE tgrelid=$1::regclass AND tgname=$2", "braid." + table, trigger);
        var original = await test.ScalarAsync<string>("SELECT pg_get_functiondef($1::regprocedure)", "braid." + function + "()");
        await test.ExecuteAsync($"""
            CREATE SCHEMA admission_shadow AUTHORIZATION braid_migrator;
            CREATE FUNCTION admission_shadow.{function}() RETURNS trigger LANGUAGE plpgsql AS $$BEGIN RETURN NEW; END$$;
            REVOKE ALL ON FUNCTION admission_shadow.{function}() FROM PUBLIC;
            CREATE OR REPLACE TRIGGER {trigger} {events} ON braid.{table}
                FOR EACH ROW EXECUTE FUNCTION admission_shadow.{function}();
            """);
        Assert.Equal(original, await test.ScalarAsync<string>("SELECT pg_get_functiondef($1::regprocedure)", "braid." + function + "()"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ValidateSourcesAsync(test));
        await using var automation = new PostgresAutomationStore(test.RegressionConnectionString("braid_run_writer"), true);
        await using var administration = new PostgresAdministrationStore(test.RegressionConnectionString("braid_administration"));
        var actor = new ActorContext(new(Guid.NewGuid()));
        var project = new ProjectScope(Guid.NewGuid(), Guid.NewGuid());
        await Assert.ThrowsAsync<InvalidOperationException>(() => automation.ReadAsync(actor, project, "policy", null, null, 1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => administration.ReadOrganizationAsync(actor, project.OrganizationId, default));
        await test.ExecuteAsync($"DROP TRIGGER {trigger} ON braid.{table}; {definition}; DROP SCHEMA admission_shadow CASCADE;");
        await ValidateSourcesAsync(test);
        Assert.Null(await automation.ReadAsync(actor, project, "policy", null, null, 1));
        Assert.Null(await administration.ReadOrganizationAsync(actor, project.OrganizationId, default));
    }

    [RegressionPostgresTheory]
    [InlineData("caseTrigger")]
    [InlineData("disabledTrigger")]
    [InlineData("wrongTableTrigger")]
    [InlineData("invitationTrigger")]
    [InlineData("function")]
    [InlineData("overloadedGate")]
    [InlineData("table")]
    public async Task Historical_schema_rejects_every_partial_installation_before_excluding_trigger_names(string partial)
    {
        await using var data = await RegressionCasePostgresTestSupport.CreateAsync(fixture, 29);
        Assert.Equal(RegressionCaseSaveOutcome.Created, (await data.SaveAsync(data.Actor, data.Version(1))).Outcome);
        var (mutation, repair) = PartialInstallation(partial);
        await data.Case.ExecuteAsync(mutation);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ValidateSourcesAsync(data.Case));
        await Assert.ThrowsAsync<InvalidOperationException>(() => data.ReadAsync(data.Actor, data.Mapping, data.CaseId, 1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => data.SaveAsync(data.Actor, data.Version(2)));
        Assert.Equal(1L, await data.Case.ScalarAsync<long>("SELECT count(*) FROM braid.regression_case_versions"));
        await data.Case.ExecuteAsync(repair);
        await ValidateSourcesAsync(data.Case);
        Assert.NotNull(await data.ReadAsync(data.Actor, data.Mapping, data.CaseId, 1));
        Assert.Equal(RegressionCaseSaveOutcome.Appended, (await data.SaveAsync(data.Actor, data.Version(2))).Outcome);
    }

    [RegressionPostgresTheory]
    [InlineData(17)]
    [InlineData(27)]
    [InlineData(28)]
    [InlineData(29)]
    [InlineData(30)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(34)]
    [InlineData(35)]
    [InlineData(36)]
    public async Task Unchanged_historical_and_current_sources_keep_their_exact_fingerprints(int prefix)
    {
        await using var data = await RegressionCasePostgresTestSupport.CreateAsync(fixture, prefix);
        await ValidateSourcesAsync(data.Case, validateMaintenance: prefix >= 28);
        Assert.Equal(RegressionCaseSaveOutcome.Created, (await data.SaveAsync(data.Actor, data.Version(1))).Outcome);
        Assert.NotNull(await data.ReadAsync(data.Actor, data.Mapping, data.CaseId, 1));
        await using var connection = await data.Case.Owner.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        Assert.Equal(prefix >= 30, await AutomationSchemaAdmission.InstalledAsync(connection, transaction, default));
        if (prefix >= 30)
            Assert.Equal(prefix >= 36 ? AutomationSchemaAdmission.ComparisonFingerprint : AutomationSchemaAdmission.Fingerprint,
                await RegressionCaseSchemaContract.CaptureAsync(
                    connection, transaction, AutomationSchemaAdmission.Tables, default, validateAutomation: false));
    }

    [PostgresFact]
    public async Task Shadow_build_guard_cannot_bypass_policy_and_repaired_requests_are_independent()
    {
        await using var test = await BuildPersistenceTests.Context.CreateAsync(fixture);
        var data = test.Runs.Cases;
        await using var automation = new PostgresAutomationStore(data.Case.RegressionConnectionString("braid_run_writer"), true);
        Assert.True(await automation.WriteAsync(test.Actor, test.Intent.Environment.Project, "policy", null,
            JsonSerializer.SerializeToElement(new { requirePassingRegression = true, errorRatePercent = 20, minimumRequests = 10, windowSeconds = 300 })));
        Assert.Equal(BuildControlOutcome.RegressionRequired, await test.Approve());
        await data.Case.ExecuteAsync("""
            CREATE SCHEMA admission_shadow AUTHORIZATION braid_migrator;
            CREATE FUNCTION admission_shadow.automation_build_guard() RETURNS trigger LANGUAGE plpgsql AS $$BEGIN RETURN NEW; END$$;
            REVOKE ALL ON FUNCTION admission_shadow.automation_build_guard() FROM PUBLIC;
            CREATE OR REPLACE TRIGGER automation_build_guard BEFORE INSERT ON braid.build_approvals
                FOR EACH ROW EXECUTE FUNCTION admission_shadow.automation_build_guard();
            """);
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Approve());
        Assert.Equal(0L, await data.Case.ScalarAsync<long>("SELECT count(*) FROM braid.build_approvals"));
        await data.Case.ExecuteAsync("""
            CREATE OR REPLACE TRIGGER automation_build_guard BEFORE INSERT ON braid.build_approvals
                FOR EACH ROW EXECUTE FUNCTION braid.automation_build_guard();
            DROP SCHEMA admission_shadow CASCADE;
            """);
        Assert.Equal(BuildControlOutcome.RegressionRequired, await test.Approve());
        Assert.True(await automation.WriteAsync(test.Actor, test.Intent.Environment.Project, "recheck", Guid.NewGuid(),
            JsonSerializer.SerializeToElement(new { mappingId = test.Intent.Revision.MappingId, revisionId = test.Intent.Revision.RevisionId })));
        var claim = Assert.IsType<Braid.Application.Automation.AutomationClaim>(await automation.ClaimEvaluationAsync(default));
        Assert.True(await automation.FinishEvaluationAsync(claim, test.Runs.Success(), default));
        Assert.Equal(BuildControlOutcome.Created, await test.Approve());
        Assert.Equal(BuildControlOutcome.Replayed, await test.Approve());
        var original = test.Intent;
        var independent = new BuildIntent(Guid.NewGuid(), original.Revision, original.NativeDigest, original.Backend,
            original.Profile, original.Boundaries, original.Environment, Guid.NewGuid(), original.RegressionEvidenceId);
        Assert.Equal(BuildControlOutcome.Created, await test.Store.ApproveAsync(test.Actor, new(test.Actor.PrincipalId, independent, test.Native, test.Evidence)));
        Assert.Equal(2L, await data.Case.ScalarAsync<long>("SELECT count(*) FROM braid.build_approvals"));
    }

    [RegressionPostgresTheory]
    [InlineData("source")]
    [InlineData("owner")]
    [InlineData("grant")]
    [InlineData("overload")]
    public async Task Exact_function_source_owner_grants_and_signature_remain_required(string change)
    {
        await using var test = await fixture.CreateCaseAsync();
        var definition = await test.ScalarAsync<string>("SELECT pg_get_functiondef('braid.automation_build_guard()'::regprocedure)");
        var (mutation, repair) = change switch
        {
            "source" => ("CREATE OR REPLACE FUNCTION braid.automation_build_guard() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp AS $$BEGIN RETURN NEW; END$$", definition),
            "owner" => ("ALTER FUNCTION braid.automation_build_guard() OWNER TO braid_run_writer", "ALTER FUNCTION braid.automation_build_guard() OWNER TO braid_migrator"),
            "grant" => ("GRANT EXECUTE ON FUNCTION braid.automation_build_guard() TO braid_run_writer", "REVOKE EXECUTE ON FUNCTION braid.automation_build_guard() FROM braid_run_writer"),
            _ => ("CREATE FUNCTION braid.automation_build_guard(integer) RETURNS integer LANGUAGE sql AS 'SELECT $1'; REVOKE ALL ON FUNCTION braid.automation_build_guard(integer) FROM PUBLIC", "DROP FUNCTION braid.automation_build_guard(integer)")
        };
        await test.BootstrapAsync(mutation);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ValidateSourcesAsync(test));
        await using var store = new PostgresAutomationStore(test.RegressionConnectionString("braid_run_writer"), true);
        var actor = new ActorContext(new(Guid.NewGuid()));
        var project = new ProjectScope(Guid.NewGuid(), Guid.NewGuid());
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadAsync(actor, project, "policy", null, null, 1));
        await test.BootstrapAsync(repair);
        await ValidateSourcesAsync(test);
        Assert.Null(await store.ReadAsync(actor, project, "policy", null, null, 1));
    }

    [RegressionPostgresTheory]
    [InlineData("missing")]
    [InlineData("disabled")]
    [InlineData("arguments")]
    [InlineData("condition")]
    [InlineData("wrongTable")]
    public async Task Current_installation_requires_the_complete_exact_trigger_shape(string change)
    {
        await using var test = await fixture.CreateCaseAsync();
        var definition = await test.ScalarAsync<string>("SELECT pg_get_triggerdef(oid) FROM pg_trigger WHERE tgrelid='braid.build_approvals'::regclass AND tgname='automation_build_guard'");
        await test.ExecuteAsync("DROP TRIGGER automation_build_guard ON braid.build_approvals");
        if (change != "missing")
            await test.ExecuteAsync(change switch
            {
                "disabled" => definition + "; ALTER TABLE braid.build_approvals DISABLE TRIGGER automation_build_guard",
                "arguments" => "CREATE TRIGGER automation_build_guard BEFORE INSERT ON braid.build_approvals FOR EACH ROW EXECUTE FUNCTION braid.automation_build_guard('unexpected')",
                "condition" => "CREATE TRIGGER automation_build_guard BEFORE INSERT ON braid.build_approvals FOR EACH ROW WHEN (false) EXECUTE FUNCTION braid.automation_build_guard()",
                _ => "CREATE TRIGGER automation_build_guard BEFORE INSERT ON braid.build_qualifications FOR EACH ROW EXECUTE FUNCTION braid.automation_build_guard()"
            });
        await Assert.ThrowsAsync<InvalidOperationException>(() => ValidateSourcesAsync(test));
        if (change != "missing") await test.ExecuteAsync("DROP TRIGGER automation_build_guard ON braid." + (change == "wrongTable" ? "build_qualifications" : "build_approvals"));
        await test.ExecuteAsync(definition);
        await ValidateSourcesAsync(test);
    }

    [PostgresFact]
    public async Task Pre_automation_case_transition_guards_are_still_exact()
    {
        await using var data = await RegressionCasePostgresTestSupport.CreateAsync(fixture, 29);
        var definition = await data.Case.ScalarAsync<string>("SELECT pg_get_functiondef('braid.guard_regression_case()'::regprocedure)");
        await data.Case.ExecuteAsync("CREATE OR REPLACE FUNCTION braid.guard_regression_case() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp AS $$BEGIN RETURN NEW; END$$");
        await Assert.ThrowsAsync<InvalidOperationException>(() => data.SaveAsync(data.Actor, data.Version(1)));
        await data.Case.ExecuteAsync(definition);
        Assert.Equal(RegressionCaseSaveOutcome.Created, (await data.SaveAsync(data.Actor, data.Version(1))).Outcome);
    }

    private static async Task ValidateSourcesAsync(PostgresCase test, bool validateMaintenance = true)
    {
        await using var connection = await test.Owner.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await AutomationSchemaAdmission.ValidateTriggersAsync(connection, transaction, default);
        await RegressionCaseSchemaContract.ValidateAsync(connection, transaction, default);
        if (validateMaintenance) await SupplementalMaintenanceSchemaAdmission.ValidateAsync(connection, transaction, default);
    }

    private static (string Mutation, string Repair) PartialInstallation(string partial)
    {
        if (partial == "table") return ("CREATE TABLE braid.automation_projects(id integer)", "DROP TABLE braid.automation_projects");
        if (partial == "function") return (
            "CREATE FUNCTION braid.automation_build_guard() RETURNS trigger LANGUAGE plpgsql AS $$BEGIN RETURN NEW; END$$; REVOKE ALL ON FUNCTION braid.automation_build_guard() FROM PUBLIC",
            "DROP FUNCTION braid.automation_build_guard()");
        if (partial == "overloadedGate") return (
            "CREATE FUNCTION braid.automation_gate(integer) RETURNS boolean LANGUAGE sql AS 'SELECT true'; REVOKE ALL ON FUNCTION braid.automation_gate(integer) FROM PUBLIC",
            "DROP FUNCTION braid.automation_gate(integer)");
        var table = partial == "invitationTrigger" ? "organization_invitations" : partial == "wrongTableTrigger" ? "regression_case_versions" : "regression_cases";
        var trigger = partial == "invitationTrigger" ? "automation_invitation_event" : "automation_case_guard";
        return ($"""
            CREATE SCHEMA admission_shadow AUTHORIZATION braid_migrator;
            CREATE FUNCTION admission_shadow.probe() RETURNS trigger LANGUAGE plpgsql AS $$BEGIN RETURN NEW; END$$;
            REVOKE ALL ON FUNCTION admission_shadow.probe() FROM PUBLIC;
            CREATE TRIGGER {trigger} BEFORE INSERT ON braid.{table} FOR EACH ROW EXECUTE FUNCTION admission_shadow.probe();
            {(partial == "disabledTrigger" ? $"ALTER TABLE braid.{table} DISABLE TRIGGER {trigger};" : "")}
            """, $"DROP TRIGGER {trigger} ON braid.{table}; DROP SCHEMA admission_shadow CASCADE;");
    }
}
