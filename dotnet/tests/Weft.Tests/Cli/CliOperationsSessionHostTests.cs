using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Weft.Api.Catalog;
using Weft.Cli;
using Weft.Contracts.Operations;
using Weft.Deployment;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests.Cli;

/// <summary>The real CLI, owned HTTPS issuer and Operations routes over restricted PostgreSQL; no native worker.</summary>
[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class CliOperationsSessionHostTests(PostgresFixture postgres)
{
    [PostgresFact]
    public Task Bearer_cli_reads_mutates_and_replays_the_exact_scoped_operation() => RunAsync(async test =>
    {
        var target = await RegisterAsync(test, test.Data.Scope);
        var records = new ConcurrentQueue<AuthHttpRecord>();
        using var cli = CliSessionHostTests.Harness(test, records);
        await cli.ConfigureAsync(); await cli.LoginAsync();

        var page = Read<ReleaseOperationsPageV1>(await cli.RunAsync(ReadArgs(target)));
        var shown = Assert.IsType<ReleaseTargetMetadataV1>(Assert.Single(page.Items));
        Assert.Equal(target.TargetId, shown.TargetId);
        Assert.True(shown.Enabled);
        Assert.Equal("1", shown.Version);

        var operation = Guid.NewGuid();
        var command = MutationArgs(target, "disable", 1, operation);
        var applied = Read<ReleaseOperationReceiptV1>(await cli.RunAsync(command));
        Assert.Equal(operation.ToString("D"), applied.OperationId);
        Assert.Equal("applied", applied.Outcome);
        Assert.Equal(applied, Read<ReleaseOperationReceiptV1>(await cli.RunAsync(command)));
        Assert.False(await EnabledAsync(test, target));
        Assert.Equal(1L, await OperationsAsync(test));

        var stale = await cli.RunAsync(MutationArgs(target, "enable", 1, Guid.NewGuid()));
        Assert.Equal(CliExitCode.PreconditionFailed, stale.Code);
        Assert.False(await EnabledAsync(test, target));
        Assert.Equal("applied", Read<ReleaseOperationReceiptV1>(await cli.RunAsync(
            MutationArgs(target, "enable", 2, Guid.NewGuid()))).Outcome);
        Assert.True(await EnabledAsync(test, target));
        Assert.Equal(3L, await test.Data.Case.ScalarAsync<long>(
            "SELECT max(version) FROM weft.release_target_events WHERE target_id=$1", target.TargetId));
        Assert.Equal(3L, await OperationsAsync(test)); // The refused stale operation is retained too.
        Assert.True(records.Sum(record => record.Audits) >= 4);
        await AssertBearerOnlyAsync(test, records);
    });

    [PostgresFact]
    public Task A_real_foreign_target_is_denied_even_when_the_principal_has_that_project_role() => RunAsync(async test =>
    {
        var own = await RegisterAsync(test, test.Data.Scope);
        var project = new ProjectScope(test.Data.Seed.OrganizationB, test.Data.Seed.ProjectB);
        await AdministratorAsync(test, project);
        var foreign = await test.Data.CommitCreateAsync(project);
        var target = await RegisterAsync(test, new(project.OrganizationId, project.ProjectId, foreign.Facts.FutureRevision.MappingId));
        var records = new ConcurrentQueue<AuthHttpRecord>();
        using var cli = CliSessionHostTests.Harness(test, records);
        await cli.ConfigureAsync(); await cli.LoginAsync();
        Assert.Single(Read<ReleaseOperationsPageV1>(await cli.RunAsync(ReadArgs(own))).Items);

        var read = await cli.RunAsync(ReadArgs(target));
        Assert.Equal(CliExitCode.NotFound, read.Code);
        Assert.Empty(read.Out);
        var mutation = await cli.RunAsync(MutationArgs(target, "disable", 1, Guid.NewGuid()));
        Assert.Equal(CliExitCode.NotFound, mutation.Code);
        Assert.Empty(mutation.Out);
        Assert.True(await EnabledAsync(test, target));
        Assert.True(await EnabledAsync(test, own));
        Assert.Equal(0L, await OperationsAsync(test));
        await AssertBearerOnlyAsync(test, records);
    });

    [RegressionPostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Expired_bearer_read_and_mutation_fail_after_one_refresh_without_business_work(bool mutation) => RunAsync(async test =>
    {
        var target = await RegisterAsync(test, test.Data.Scope);
        var records = new ConcurrentQueue<AuthHttpRecord>();
        using var cli = CliSessionHostTests.Harness(test, records);
        test.Issuer.PublicAccessClaims = client =>
        {
            var claims = UserTokenValidationTests.Claims(application: client);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            claims["iat"] = now - 1200;
            claims["nbf"] = now - 1200;
            claims["exp"] = now - 600;
            return claims;
        };
        await cli.ConfigureAsync(); await cli.LoginAsync();
        var failed = await cli.RunAsync(mutation ? MutationArgs(target, "disable", 1, Guid.NewGuid()) : ReadArgs(target));
        Assert.Equal(CliExitCode.SignInFailed, failed.Code);
        Assert.Empty(failed.Out);
        Assert.Equal(1, test.Issuer.PublicRefreshGrants);
        Assert.Equal(2, records.Count);
        Assert.All(records, record =>
        {
            Assert.Equal(0, record.BodyReads);
            Assert.Equal(0, record.CatalogReads);
            Assert.Equal(0, record.Audits);
        });
        Assert.True(await EnabledAsync(test, target));
        Assert.Equal(0L, await OperationsAsync(test));
        await AssertBearerOnlyAsync(test, records);
    });

    [PostgresFact]
    public Task Current_project_role_revocation_refuses_cached_bearer_reads_and_mutations() => RunAsync(async test =>
    {
        var target = await RegisterAsync(test, test.Data.Scope);
        var records = new ConcurrentQueue<AuthHttpRecord>();
        using var cli = CliSessionHostTests.Harness(test, records);
        await cli.ConfigureAsync(); await cli.LoginAsync();
        Assert.Single(Read<ReleaseOperationsPageV1>(await cli.RunAsync(ReadArgs(target))).Items);
        await test.Data.Case.ExecuteAsync("UPDATE weft.project_memberships SET role='viewer',version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3",
            target.Mapping.OrganizationId, target.Mapping.ProjectId, test.Data.Actor.PrincipalId.Value);
        Assert.Equal(CliExitCode.NotFound, (await cli.RunAsync(ReadArgs(target))).Code);
        Assert.Equal(CliExitCode.NotFound, (await cli.RunAsync(MutationArgs(target, "disable", 1, Guid.NewGuid()))).Code);
        Assert.True(await EnabledAsync(test, target));
        Assert.Equal(0L, await OperationsAsync(test));
        await AssertBearerOnlyAsync(test, records);
    });

    private Task RunAsync(Func<AuthHttpFixture, Task> body) => AuthHttpFixture.RunAsync(postgres, body,
        bearerBurst: 100, operations: true, configureCatalogServices: (test, services) =>
        {
            services.AddSingleton(new ReleaseOperationsOptions(true));
            services.AddKeyedSingleton<PostgresLocalReleaseStore>("operations-reader", (_, _) => new(test.Data.Case.RegressionConnectionString("weft_release_reader"), false));
            services.AddKeyedSingleton<PostgresLocalReleaseStore>("operations-writer", (_, _) => new(test.Data.Case.RegressionConnectionString("weft_release_writer"), true));
            services.AddKeyedSingleton<PostgresBuildQualificationStore>("operations", (_, _) => new(test.Data.Case.RegressionConnectionString("weft_qualification_reader"), false));
            services.AddSingleton<IReleaseOperationsStore>(provider => provider.GetRequiredKeyedService<PostgresLocalReleaseStore>("operations-reader"));
            services.AddSingleton(provider => new ReleaseOperationsService(provider.GetRequiredKeyedService<PostgresLocalReleaseStore>("operations-writer"),
                provider.GetRequiredKeyedService<PostgresLocalReleaseStore>("operations-reader"), provider.GetRequiredKeyedService<PostgresBuildQualificationStore>("operations")));
        });

    private static Task AdministratorAsync(AuthHttpFixture test, ProjectScope project) =>
        test.Data.Case.ExecuteAsync("UPDATE weft.project_memberships SET role='administrator',version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3",
            project.OrganizationId, project.ProjectId, test.Data.Actor.PrincipalId.Value);

    private static async Task<ReleaseTargetReference> RegisterAsync(AuthHttpFixture test, MappingScope mapping)
    {
        var project = new ProjectScope(mapping.OrganizationId, mapping.ProjectId);
        await AdministratorAsync(test, project);
        var environment = new BuildEnvironmentReference(project, Guid.NewGuid(), 1);
        var profile = new BuildRuntimeProfile("cli-operations", 1, "dotnet", "10.0.12", "dotnet-library", 1024);
        await using var builds = new PostgresBuildStore(test.Data.Case.RegressionConnectionString("weft_build_writer"), true);
        Assert.Equal(BuildControlOutcome.Created, await builds.SaveEnvironmentAsync(test.Data.Actor, new(environment, profile, true)));
        var target = new ReleaseTargetReference(mapping, environment, Guid.NewGuid());
        await using var releases = new PostgresLocalReleaseStore(test.Data.Case.RegressionConnectionString("weft_release_writer"), true);
        Assert.Equal(ReleaseWriteOutcome.Created, await releases.RegisterAsync(test.Data.Actor, new(target, profile, "sha256:" + new string('f', 64))));
        return target;
    }

    private static string[] ReadArgs(ReleaseTargetReference target) => ["--organization", target.Mapping.OrganizationId.ToString("D"),
        "operations", "targets", "--project", target.Mapping.ProjectId.ToString("D"), "--mapping", target.Mapping.MappingId.ToString("D"), "--json"];

    private static string[] MutationArgs(ReleaseTargetReference target, string action, long version, Guid operation) =>
        ["--organization", target.Mapping.OrganizationId.ToString("D"), "operations", action,
         "--project", target.Mapping.ProjectId.ToString("D"), "--mapping", target.Mapping.MappingId.ToString("D"),
         "--target", target.TargetId.ToString("D"), "--environment", target.Environment.EnvironmentId.ToString("D"),
         "--environment-version", "1", "--version", version.ToString(System.Globalization.CultureInfo.InvariantCulture),
         "--operation", operation.ToString("D"), "--json"];

    private static T Read<T>(CliResult result)
    {
        Assert.True(result.Code == CliExitCode.Success, result.ToString());
        return JsonSerializer.Deserialize<T>(result.Out, Weft.Cli.Http.WeftApiClient.Json)!;
    }

    private static Task<bool> EnabledAsync(AuthHttpFixture test, ReleaseTargetReference target) => test.Data.Case.ScalarAsync<bool>(
        "SELECT enabled FROM weft.local_release_targets WHERE organization_id=$1 AND project_id=$2 AND mapping_id=$3 AND target_id=$4",
        target.Mapping.OrganizationId, target.Mapping.ProjectId, target.Mapping.MappingId, target.TargetId);

    private static Task<long> OperationsAsync(AuthHttpFixture test) => test.Data.Case.ScalarAsync<long>("SELECT count(*) FROM weft.release_target_operations");

    private static async Task AssertBearerOnlyAsync(AuthHttpFixture test, ConcurrentQueue<AuthHttpRecord> records)
    {
        Assert.NotEmpty(records);
        await Task.WhenAll(records.Select(record => record.Ended.Task)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(records, record =>
        {
            Assert.True(record.Entered);
            Assert.False(record.Unexpected);
            Assert.Equal(0, record.SessionReads);
        });
        Assert.Equal(0L, await test.RowsAsync());
        Assert.Equal(1, test.Issuer.PublicDeviceGrants);
    }
}
