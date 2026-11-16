using Npgsql;
using Braid.Application;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresProjectPayloadTransactionTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task EachAuthorityRevocationCommittedBeforeAnObservedOwnerLockRefusesGrantUsingCurrentFacts()
    {
        foreach (var authority in new[] { "principal", "organization", "project" })
        {
            await using var test = await PostgresProjectPayloadTestSupport.CreateAsync(fixture);
            await using var blocker = PostgresProjectPayloadTestSupport.Unpooled(test.Case.MigrationConnectionString);
            await blocker.OpenAsync();
            await using var transaction = await blocker.BeginTransactionAsync();
            await using (var revoke = Revocation(blocker, transaction, test, authority))
                Assert.Equal(1, await revoke.ExecuteNonQueryAsync());
            var application = $"braid-payload-authority-{Guid.NewGuid():N}";
            await using var owner = new ProjectPayloadGrantProvisioner(WithApplication(test.Case.MigrationConnectionString, application));
            await using var observer = PostgresProjectPayloadTestSupport.Unpooled(test.Case.MigrationConnectionString);
            await observer.OpenAsync();
            await using var prepared = await PostgresProjectPayloadTestSupport.PrepareBlockedAsync(observer, application, blocker.ProcessID);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var pending = owner.SetAsync(test.Scope, test.Actor.PrincipalId, true, 0, cancellation.Token);
            try
            {
                await PostgresProjectPayloadTestSupport.WaitBlockedAsync(prepared, pending);
                await transaction.CommitAsync();
                var authorityAfterRevocation = await test.AuthorityAsync();
                PostgresProjectPayloadTestSupport.Refused(await pending.WaitAsync(TimeSpan.FromSeconds(5)),
                    ProjectPayloadGrantOwnerOutcome.AuthorityUnavailable);
                PostgresProjectPayloadTestSupport.SamePrivate(authorityAfterRevocation, await test.AuthorityAsync());
            }
            finally
            {
                cancellation.Cancel();
                await transaction.DisposeAsync();
                await PostgresProjectPayloadTestSupport.DrainAsync(pending);
            }
            Assert.Null(await test.ObserveAsync());
            PostgresProjectPayloadTestSupport.SamePrivate("[]", await test.GrantsAsync());
        }
    }

    [PostgresFact]
    public async Task ObservedUncommittedRevocationRollbackAllowsGrantWithoutChangingAuthority()
    {
        await using var test = await PostgresProjectPayloadTestSupport.CreateAsync(fixture);
        var authority = await test.AuthorityAsync();
        await using var blocker = PostgresProjectPayloadTestSupport.Unpooled(test.Case.MigrationConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using (var revoke = Revocation(blocker, transaction, test, "project"))
            Assert.Equal(1, await revoke.ExecuteNonQueryAsync());
        var application = $"braid-payload-rollback-{Guid.NewGuid():N}";
        await using var owner = new ProjectPayloadGrantProvisioner(WithApplication(test.Case.MigrationConnectionString, application));
        await using var observer = PostgresProjectPayloadTestSupport.Unpooled(test.Case.MigrationConnectionString);
        await observer.OpenAsync();
        await using var prepared = await PostgresProjectPayloadTestSupport.PrepareBlockedAsync(observer, application, blocker.ProcessID);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pending = owner.SetAsync(test.Scope, test.Actor.PrincipalId, true, 0, cancellation.Token);
        try
        {
            await PostgresProjectPayloadTestSupport.WaitBlockedAsync(prepared, pending);
            await transaction.RollbackAsync();
            PostgresProjectPayloadTestSupport.Grant(await pending.WaitAsync(TimeSpan.FromSeconds(5)),
                ProjectPayloadGrantOwnerOutcome.Created, test.Scope, test.Actor.PrincipalId, true, 1);
        }
        finally
        {
            cancellation.Cancel();
            await transaction.DisposeAsync();
            await PostgresProjectPayloadTestSupport.DrainAsync(pending);
        }
        PostgresProjectPayloadTestSupport.SamePrivate(authority, await test.AuthorityAsync());
        PostgresProjectPayloadTestSupport.Exact(await test.ObserveAsync(), test.Scope, test.Actor.PrincipalId);
    }

    [PostgresFact]
    public async Task CancellationAtObservedGrantRowLockRollsBackAndTheSameOwnerCanThenRevoke()
    {
        await using var test = await PostgresProjectPayloadTestSupport.CreateAsync(fixture);
        await test.SetAsync();
        var before = await test.GrantsAsync();
        var authority = await test.AuthorityAsync();
        await using var blocker = PostgresProjectPayloadTestSupport.Unpooled(test.Case.MigrationConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await LockGrantAsync(blocker, transaction, test);
        var application = $"braid-payload-cancel-owner-{Guid.NewGuid():N}";
        await using var owner = new ProjectPayloadGrantProvisioner(WithApplication(test.Case.MigrationConnectionString, application));
        await using var observer = PostgresProjectPayloadTestSupport.Unpooled(test.Case.MigrationConnectionString);
        await observer.OpenAsync();
        await using var prepared = await PostgresProjectPayloadTestSupport.PrepareBlockedAsync(observer, application, blocker.ProcessID);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pending = owner.SetAsync(test.Scope, test.Actor.PrincipalId, false, 1, cancellation.Token);
        try
        {
            await PostgresProjectPayloadTestSupport.WaitBlockedAsync(prepared, pending);
            cancellation.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(cancellation.Token, error.CancellationToken);
            // A fresh reader while the row blocker remains held cannot see a canceled write.
            PostgresProjectPayloadTestSupport.Exact(await test.ObserveAsync(), test.Scope, test.Actor.PrincipalId);
            PostgresProjectPayloadTestSupport.SamePrivate(before, await test.GrantsAsync());
        }
        finally
        {
            cancellation.Cancel();
            await transaction.RollbackAsync();
            await PostgresProjectPayloadTestSupport.DrainAsync(pending);
        }
        PostgresProjectPayloadTestSupport.Grant(await owner.SetAsync(test.Scope, test.Actor.PrincipalId, false, 1),
            ProjectPayloadGrantOwnerOutcome.Updated, test.Scope, test.Actor.PrincipalId, false, 2);
        Assert.Null(await test.ObserveAsync());
        PostgresProjectPayloadTestSupport.SamePrivate(authority, await test.AuthorityAsync());
    }

    [PostgresFact]
    public async Task CancellationAtAnObservedReaderRelationLockIsFailureAndTheSamePoolRecovers()
    {
        await using var test = await PostgresProjectPayloadTestSupport.CreateAsync(fixture);
        await test.SetAsync();
        var before = await test.GrantsAsync();
        await using var blocker = PostgresProjectPayloadTestSupport.Unpooled(test.Case.MigrationConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using (var gate = new NpgsqlCommand("LOCK TABLE ONLY braid.project_payload_read_grants IN ACCESS EXCLUSIVE MODE", blocker, transaction))
            await gate.ExecuteNonQueryAsync();
        var application = $"braid-payload-cancel-reader-{Guid.NewGuid():N}";
        await using var database = new PostgresPayloadPermissionDatabase(WithApplication(test.Case.PayloadPermissionConnectionString, application));
        var reader = new PostgresProjectPayloadPermissionReader(database);
        await using var observer = PostgresProjectPayloadTestSupport.Unpooled(test.Case.MigrationConnectionString);
        await observer.OpenAsync();
        await using var prepared = await PostgresProjectPayloadTestSupport.PrepareBlockedAsync(observer, application, blocker.ProcessID);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pending = reader.ObserveAsync(test.Actor, test.Scope, cancellation.Token);
        try
        {
            await PostgresProjectPayloadTestSupport.WaitBlockedAsync(prepared, pending);
            cancellation.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(cancellation.Token, error.CancellationToken);
        }
        finally
        {
            cancellation.Cancel();
            await transaction.RollbackAsync();
            await PostgresProjectPayloadTestSupport.DrainAsync(pending);
        }
        PostgresProjectPayloadTestSupport.Exact(await reader.ObserveAsync(test.Actor, test.Scope), test.Scope, test.Actor.PrincipalId);
        PostgresProjectPayloadTestSupport.SamePrivate(before, await test.GrantsAsync());
    }

    [PostgresFact]
    public async Task OriginalOwnerLockTimeoutIsRedactedFailureWithoutWriteAndSameOwnerRecoversAfterRelease()
    {
        await using var test = await PostgresProjectPayloadTestSupport.CreateAsync(fixture);
        await test.SetAsync();
        var before = await test.GrantsAsync();
        await using var blocker = PostgresProjectPayloadTestSupport.Unpooled(test.Case.MigrationConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await LockGrantAsync(blocker, transaction, test);
        var application = $"braid-payload-timeout-{Guid.NewGuid():N}";
        await using var owner = new ProjectPayloadGrantProvisioner(WithApplication(test.Case.MigrationConnectionString, application));
        await using var observer = PostgresProjectPayloadTestSupport.Unpooled(test.Case.MigrationConnectionString);
        await observer.OpenAsync();
        await using var prepared = await PostgresProjectPayloadTestSupport.PrepareBlockedAsync(observer, application, blocker.ProcessID);
        using var fallback = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pending = owner.SetAsync(test.Scope, test.Actor.PrincipalId, false, 1, fallback.Token);
        try
        {
            await PostgresProjectPayloadTestSupport.WaitBlockedAsync(prepared, pending);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(string.Equals(error.Message, "Project payload grant owner operation failed.", StringComparison.Ordinal),
                "The owner lock failure returned an unexpected diagnostic category.");
            Assert.Null(error.InnerException);
            PostgresProjectPayloadTestSupport.SamePrivate(before, await test.GrantsAsync());
        }
        finally
        {
            fallback.Cancel();
            try { await transaction.RollbackAsync(); }
            finally
            {
                // Settle this exact owned call before disposing its pool/case.
                // Its timeout result is asserted above; cleanup must not replace
                // an earlier observation/assertion failure with a pending-call error.
                try { await pending; }
                catch (Exception) { }
            }
        }
        PostgresProjectPayloadTestSupport.Grant(await owner.SetAsync(test.Scope, test.Actor.PrincipalId, false, 1),
            ProjectPayloadGrantOwnerOutcome.Updated, test.Scope, test.Actor.PrincipalId, false, 2);
    }

    [PostgresFact]
    public async Task ConcurrentAbsentGrantsAndSameVersionRevokesHaveOneWinnerAndOneVersionRefusal()
    {
        await using var test = await PostgresProjectPayloadTestSupport.CreateAsync(fixture);
        var authority = await test.AuthorityAsync();
        await using var other = new ProjectPayloadGrantProvisioner(test.Case.MigrationConnectionString);
        // Independent pools dispatch before either is awaited. The test asserts
        // durable one-winner/CAS facts, not a scheduling order or lock timing.
        var first = test.SetAsync();
        var second = other.SetAsync(test.Scope, test.Actor.PrincipalId, true, 0);
        var created = await Task.WhenAll(first, second);
        PostgresProjectPayloadTestSupport.Grant(Assert.Single(created, result => result.Outcome == ProjectPayloadGrantOwnerOutcome.Created),
            ProjectPayloadGrantOwnerOutcome.Created, test.Scope, test.Actor.PrincipalId, true, 1);
        PostgresProjectPayloadTestSupport.Refused(Assert.Single(created, result => result.Outcome == ProjectPayloadGrantOwnerOutcome.VersionConflict),
            ProjectPayloadGrantOwnerOutcome.VersionConflict);
        PostgresProjectPayloadTestSupport.Exact(await test.ObserveAsync(), test.Scope, test.Actor.PrincipalId);
        first = test.SetAsync(false, 1);
        second = other.SetAsync(test.Scope, test.Actor.PrincipalId, false, 1);
        var revoked = await Task.WhenAll(first, second);
        PostgresProjectPayloadTestSupport.Grant(Assert.Single(revoked, result => result.Outcome == ProjectPayloadGrantOwnerOutcome.Updated),
            ProjectPayloadGrantOwnerOutcome.Updated, test.Scope, test.Actor.PrincipalId, false, 2);
        PostgresProjectPayloadTestSupport.Refused(Assert.Single(revoked, result => result.Outcome == ProjectPayloadGrantOwnerOutcome.VersionConflict),
            ProjectPayloadGrantOwnerOutcome.VersionConflict);
        Assert.Null(await test.ObserveAsync());
        Assert.Equal(1, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.project_payload_read_grants"));
        PostgresProjectPayloadTestSupport.SamePrivate(authority, await test.AuthorityAsync());
    }

    private static string WithApplication(string connectionString, string application) =>
        new NpgsqlConnectionStringBuilder(connectionString) { ApplicationName = application }.ConnectionString;

    private static NpgsqlCommand Revocation(NpgsqlConnection connection, NpgsqlTransaction transaction,
        PostgresProjectPayloadTestSupport test, string authority)
    {
        var sql = authority switch
        {
            "principal" => "UPDATE braid.principals SET active=false WHERE id=$1",
            "organization" => "UPDATE braid.organization_memberships SET active=false WHERE organization_id=$1 AND principal_id=$2",
            "project" => "UPDATE braid.project_memberships SET active=false WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3",
            _ => throw new InvalidOperationException("Unknown owned authority-lock probe.")
        };
        var command = new NpgsqlCommand(sql, connection, transaction);
        if (authority is "organization" or "project") command.Parameters.AddWithValue(test.Scope.OrganizationId);
        if (authority == "project") command.Parameters.AddWithValue(test.Scope.ProjectId);
        command.Parameters.AddWithValue(test.Actor.PrincipalId.Value);
        return command;
    }

    private static async Task LockGrantAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        PostgresProjectPayloadTestSupport test)
    {
        await using var command = new NpgsqlCommand("""
            SELECT version FROM braid.project_payload_read_grants
            WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3 FOR UPDATE
            """, connection, transaction);
        command.Parameters.AddWithValue(test.Scope.OrganizationId);
        command.Parameters.AddWithValue(test.Scope.ProjectId);
        command.Parameters.AddWithValue(test.Actor.PrincipalId.Value);
        Assert.Equal(1, Assert.IsType<long>(await command.ExecuteScalarAsync()));
    }
}
