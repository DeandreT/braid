using Weft.Application;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
public sealed class HistoricalPreviewAccessTests(PostgresFixture fixture)
{
    [PostgresTheory]
    [InlineData(36)]
    [InlineData(40)]
    [InlineData(41)]
    public async Task Exact_prefix_functions_admit_reads_and_refuse_body_signature_acl_or_history_drift(int prefix)
    {
        await using var test = await fixture.CreateCaseAsync(migrate: false);
        await using (var runner = new MigrationRunner(test.MigrationConnectionString, MigrationRunner.EmbeddedMigrations.Take(2).ToArray()))
            await runner.MigrateAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        await test.ExecuteAsync("UPDATE weft.project_memberships SET role='publisher',version=version+1 WHERE principal_id=$1", seed.Alice);
        var actor = new ActorContext(new(seed.Alice));
        var receipt = await HistoricalIngestionSeed.CommitCreateAsync(test, actor, new(seed.OrganizationA, seed.ProjectA));
        await using (var runner = new MigrationRunner(test.MigrationConnectionString, MigrationRunner.EmbeddedMigrations.Take(prefix).ToArray()))
            await runner.MigrateAsync();
        var submission = new PreviewRunSubmission(receipt.Facts.FutureRevision, Guid.NewGuid(), PreviewTargetSelection.All, new(null, "{}"u8), [], []);
        var created = await HistoricalPreviewAccess.SubmitAsync(test, prefix, actor,
            new(submission, PreviewRequestDocument.Encode(submission, PreviewLimits.Default), "2026-11-12T00:00:00Z", 3, TimeSpan.FromHours(24)));
        var run = created.RunId!.Value;
        Assert.Equal(1, (await HistoricalPreviewAccess.ReadAsync(test, prefix, actor, seed.OrganizationA, run)).State);
        foreach (var mutation in new[]
        {
            "CREATE OR REPLACE FUNCTION weft.preview_authorized(p_org uuid,p_project uuid,p_principal uuid) RETURNS boolean LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp AS 'SELECT true'",
            "CREATE FUNCTION weft.preview_authorized() RETURNS boolean LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp AS 'SELECT true'",
            "GRANT EXECUTE ON FUNCTION weft.preview_authorized(uuid,uuid,uuid) TO PUBLIC",
            "DELETE FROM weft.schema_migrations WHERE version=" + prefix
        })
        {
            // Each mutation is reverted in the owned database, so the next check
            // starts from the exact successfully exercised historical contract.
            var before = await test.ScalarAsync<string>("SELECT pg_get_functiondef('weft.preview_authorized(uuid,uuid,uuid)'::regprocedure)");
            var checksum = await test.ScalarAsync<string>("SELECT checksum FROM weft.schema_migrations WHERE version=$1", prefix);
            await test.ExecuteAsync(mutation);
            await Assert.ThrowsAsync<InvalidOperationException>(() => HistoricalPreviewAccess.ReadAsync(test, prefix, actor, seed.OrganizationA, run));
            await test.ExecuteAsync(before);
            await test.ExecuteAsync("DROP FUNCTION IF EXISTS weft.preview_authorized(); REVOKE ALL ON FUNCTION weft.preview_authorized(uuid,uuid,uuid) FROM PUBLIC");
            await test.ExecuteAsync("INSERT INTO weft.schema_migrations(version,checksum) VALUES($1,$2) ON CONFLICT DO NOTHING", prefix, checksum);
            Assert.Equal(1, (await HistoricalPreviewAccess.ReadAsync(test, prefix, actor, seed.OrganizationA, run)).State);
        }
    }
}
