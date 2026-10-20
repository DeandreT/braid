using Npgsql;
using Weft.Application;
using Weft.Application.Regression;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresRegressionHistoryTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task Equal_timestamps_paginate_by_run_id_without_omissions_or_duplicates()
    {
        await using var test = await RegressionRunPostgresTestSupport.CreateAsync(fixture);
        var ids = new[] { Guid.Parse("10000000-0000-0000-0000-000000000001"),
            Guid.Parse("10000000-0000-0000-0000-000000000002"), Guid.Parse("10000000-0000-0000-0000-000000000003") };
        await using (var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(test.WriterConnection) { Pooling = false }.ConnectionString))
        {
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using (var settings = new NpgsqlCommand("SELECT set_config('weft.organization_id',@org,true),set_config('weft.run_principal_id',@actor,true)", connection, transaction))
            {
                settings.Parameters.AddWithValue("org", test.Organization.ToString("D"));
                settings.Parameters.AddWithValue("actor", test.Actor.PrincipalId.Value.ToString("D"));
                await settings.ExecuteNonQueryAsync();
            }
            await using var command = new NpgsqlCommand("""
                SELECT weft.submit_regression_run(@org,@project,@mapping,id,@case,@version,@owner,@revision,@native,@digest,@bytes)
                FROM unnest(@ids) AS id
                """, connection, transaction);
            command.Parameters.AddWithValue("org", test.Organization);
            command.Parameters.AddWithValue("project", test.Reference.Revision.ProjectId);
            command.Parameters.AddWithValue("mapping", test.Reference.Revision.MappingId);
            command.Parameters.AddWithValue("case", test.Reference.CaseId);
            command.Parameters.AddWithValue("version", test.Reference.Version);
            command.Parameters.AddWithValue("owner", test.Reference.Owner.Value);
            command.Parameters.AddWithValue("revision", test.Reference.Revision.RevisionId);
            command.Parameters.AddWithValue("native", test.Reference.NativeDigest.Value);
            command.Parameters.AddWithValue("digest", test.Reference.ContentDigest.Value);
            command.Parameters.AddWithValue("bytes", test.Reference.ByteCount);
            command.Parameters.AddWithValue("ids", ids);
            await using (var rows = await command.ExecuteReaderAsync())
                while (await rows.ReadAsync()) Assert.Equal(0, rows.GetInt32(0));
            await transaction.CommitAsync();
        }
        var first = await test.Reader.ListAsync(test.Actor, test.Cases.Mapping, 2);
        Assert.Equal(ids.Reverse().Take(2), first.Runs.Select(run => run.RunId));
        Assert.Single(first.Runs.Select(run => run.CreatedAt).Distinct());
        Assert.Equal(ids[1], first.NextRunId);
        var second = await test.Reader.ListAsync(test.Actor, test.Cases.Mapping, 2, first.NextRunId);
        Assert.Equal(ids[0], Assert.Single(second.Runs).RunId);
        Assert.Null(second.NextRunId);
    }

    [PostgresFact]
    public async Task History_and_cursors_do_not_cross_submitter_mapping_or_current_authority()
    {
        await using var test = await RegressionRunPostgresTestSupport.CreateAsync(fixture);
        await test.SubmitAsync();
        var other = new ActorContext(new(test.Cases.Seed.Admitted));
        await test.Cases.Seed.AddMembershipAsync(test.Cases.Case, test.Organization, test.Cases.Seed.ProjectA, other.PrincipalId.Value, "runner");
        var otherId = Guid.NewGuid();
        Assert.Equal(RegressionRunSubmitOutcome.Created, await test.Writer.SubmitAsync(new(other, otherId, test.Reference)));
        Assert.Equal(test.RunId, Assert.Single((await test.Reader.ListAsync(test.Actor, test.Cases.Mapping, 10)).Runs).RunId);
        Assert.Equal(otherId, Assert.Single((await test.Reader.ListAsync(other, test.Cases.Mapping, 10)).Runs).RunId);
        Assert.Empty((await test.Reader.ListAsync(other, test.Cases.Mapping, 10, test.RunId)).Runs);
        Assert.Empty((await test.Reader.ListAsync(test.Actor, test.Cases.Mapping, 10, otherId)).Runs);
        Assert.Empty((await test.Reader.ListAsync(test.Actor, new(test.Organization, test.Cases.Mapping.ProjectId, Guid.NewGuid()), 10, test.RunId)).Runs);
        await test.Cases.RoleAsync("viewer");
        Assert.Empty((await test.Reader.ListAsync(test.Actor, test.Cases.Mapping, 10)).Runs);
        Assert.Equal(otherId, Assert.Single((await test.Reader.ListAsync(other, test.Cases.Mapping, 10)).Runs).RunId);
    }
}
