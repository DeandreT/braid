using System.Text;
using Npgsql;
using Braid.Application;
using Braid.Application.Regression;
using Braid.Contracts.Operations;
using Braid.Domain;
using Braid.Persistence.Postgres;
using Context = Braid.Tests.RegressionRunPostgresTestSupport;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresRegressionRunTests(PostgresFixture fixture)
{
    [NativeShipmentPilotFact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task Canonical_XML_reopens_the_pinned_case_and_retains_the_original_observation()
    {
        var root = FerruleServiceTestSupport.TemporaryRoot();
        try
        {
            var options = FerruleCompiledServiceTests.Options(root);
            await using var runner = new Braid.MappingBackends.Workers.ProcessCompiledRuntimeRunner(new(options.NativeIdentity,
                options.RuntimeAssembly, options.LauncherExecutable, root, options.DotnetRoot)
                { Operation = Braid.MappingBackends.Workers.CompiledRuntimeOperation.XmlCanonicalComparison });
            var worker = new SavedXmlComparison(new Braid.MappingBackends.XmlCanonicalComparisonWorker(runner, new(new(8192, 8), new(4096, 4096, 8192))));
            var metadata = XmlDocumentMetadata.WithoutHints;
            RegressionOutputSet Output(string xml) => RegressionOutputSet.Capture([RegressionOutput.Capture(RegressionOutputKey.Primary,
                Encoding.UTF8.GetBytes(xml), mediaType: XmlDocumentPolicy.MediaType, xmlMetadata: metadata)]);
            var observed = Output("<Order code=\"A\"></Order>");
            await using var test = await Context.CreateAsync(fixture, nativeBytes: XmlPreviewWorkflowTests.Native, configure: old => new(
                old.CaseId, 1, old.Owner, old.Name, old.Revision, old.NativeDigest,
                new(old.Runtime.BackendId, old.Runtime.EngineRevision, old.Runtime.BackendBuild, Braid.MappingBackends.FerrulePreviewBackend.XmlAdapterBuild, XmlPreviewAdmission.Profile),
                new(PreviewTargetSelection.Primary, old.Replay.CurrentDateTime, PreviewLimits.Default,
                    new(null, XmlPreviewWorkflowTests.Input, xmlMetadata: metadata), [], []), SavedXmlComparison.Policy, Output("<Order code='A'/>")));
            Assert.Equal(RegressionRunSubmitOutcome.Created, await test.SubmitAsync());
            var comparison = await worker.CompareAsync(test.Value, MappingPreviewStatus.Succeeded, observed, default);
            Assert.True(await test.CompleteAsync(await test.ClaimAsync(), RegressionCaseExecution.Complete(MappingPreviewStatus.Succeeded, observed, comparison, XmlPreviewAdmission.Profile)));
            var reader = new RegressionRunReadService(test.Reader, test.Cases.Reader, worker);
            var result = (await reader.ReadAsync(test.Actor, test.Organization, test.RunId))!.Result!;
            Assert.Equal(SavedXmlComparison.Policy, result.Comparison!.Policy);
            Assert.Equal(RegressionComparisonOutcome.Passed, result.Comparison.Outcome);
            Assert.Equal(observed.Outputs[0].CopyBytes(), Assert.Single(result.Outputs!.Outputs).CopyBytes());
            Assert.Equal(RegressionRunSubmitOutcome.Replayed, await test.SubmitAsync());
            Assert.Null(await reader.ReadAsync(new(new PrincipalId(Guid.NewGuid())), test.Organization, test.RunId));
        }
        finally { Directory.Delete(root); }
    }
    [PostgresFact]
    public async Task Document_case_and_run_retain_media_options_and_recompute_after_storage()
    {
        await using var test = await Context.CreateAsync(fixture);
        var old = test.Value;
        var output = RegressionOutput.Capture(RegressionOutputKey.Primary, Encoding.UTF8.GetBytes(DocumentPreviewTestData.X12), mediaType: "application/edi-x12");
        var value = new RegressionCaseVersion(old.CaseId, 2, old.Owner, old.Name, old.Revision, old.NativeDigest,
            new(old.Runtime.BackendId, old.Runtime.EngineRevision, old.Runtime.BackendBuild, "document-adapter", DocumentPreviewPolicy.Profile),
            old.Replay, RegressionComparisonService.EdiSegmentsPolicy, RegressionOutputSet.Capture([output]), new(false, true, true));
        await test.Cases.Writer.SaveAsync(test.Actor, value);
        var reference = new RegressionCaseEnvelopeCodec().Encode(value).Reference;
        Assert.Equal(RegressionRunSubmitOutcome.Created, await test.Writer.SubmitAsync(new(test.Actor, test.RunId, reference)));
        var claim = await test.ClaimAsync();
        var comparison = new RegressionComparisonService().Compare(value.Expected, new(OperationStatusV1.Succeeded, value.Expected),
            value.ComparisonPolicy, ediOptions: value.EdiOptions);
        Assert.True(await test.Writer.CompleteAsync(claim, RegressionCaseExecution.Complete(MappingPreviewStatus.Succeeded, value.Expected, comparison)));
        var reopened = await new RegressionRunReadService(test.Reader, test.Cases.Reader).ReadAsync(test.Actor, test.Organization, test.RunId);
        Assert.Equal(RegressionComparisonOutcome.Passed, reopened!.Result!.Comparison!.Outcome);
        Assert.Equal("application/edi-x12", Assert.Single(reopened.Result.Outputs!.Outputs).MediaType);
        Assert.Equal(RegressionRunSubmitOutcome.Replayed, await test.Writer.SubmitAsync(new(test.Actor, test.RunId, reference)));
    }

    [PostgresFact]
    public async Task Schema_matches_the_exact_qualified_shape()
    {
        await using var test = await Context.CreateAsync(fixture);
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(test.Cases.Case.MigrationConnectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var settings = new NpgsqlCommand("SET LOCAL TimeZone='UTC'; SET LOCAL DateStyle='ISO, MDY'", connection, transaction))
            await settings.ExecuteNonQueryAsync();
        var fingerprint = await RegressionCaseSchemaContract.CaptureAsync(connection, transaction, ["regression_runs"], CancellationToken.None);
        Assert.True(fingerprint == RegressionRunRoleGuard.Fingerprint, $"Schema fingerprint mismatch: {fingerprint}");
    }

    [PostgresFact]
    public async Task Submission_completion_and_old_request_replay_keep_one_immutable_result()
    {
        await using var test = await Context.CreateAsync(fixture);
        Assert.Equal(RegressionRunSubmitOutcome.Created, await test.SubmitAsync());
        var claim = await test.ClaimAsync();
        Assert.True(await test.Writer.CompleteAsync(claim, test.Success()));
        Assert.Equal(RegressionRunSubmitOutcome.Replayed, await test.SubmitAsync());
        Assert.Equal(RegressionRunClaimOutcome.Finished, (await test.Writer.ClaimAsync(test.Organization, test.RunId, Guid.NewGuid())).Outcome);
        var snapshot = await test.Reader.ReadAsync(test.Actor, test.Organization, test.RunId);
        Assert.Equal(RegressionRunState.Completed, snapshot!.State);
        var view = await new RegressionRunReadService(test.Reader, test.Cases.Reader).ReadAsync(test.Actor, test.Organization, test.RunId);
        Assert.Equal(RegressionComparisonOutcome.Passed, view!.Result!.Comparison!.Outcome);
        Assert.False(await test.Writer.CompleteAsync(claim, RegressionCaseExecution.Refused(RegressionCaseExecutionOutcome.ResultRejected)));
        Assert.False(await test.Writer.ReleaseAsync(claim));
        Assert.Equal(1L, await test.Cases.Case.ScalarAsync<long>("SELECT count(*) FROM braid.regression_runs"));
    }

    [PostgresFact]
    public async Task Queued_and_completed_runs_survive_owned_server_restart()
    {
        await using var test = await Context.CreateAsync(fixture);
        await test.SubmitAsync();
        await test.Writer.DisposeAsync();
        await test.Reader.DisposeAsync();
        await fixture.RestartAsync();
        await using (var reader = new PostgresRegressionRunStore(test.ReaderConnection, writer: false))
        await using (var writer = new PostgresRegressionRunStore(test.WriterConnection, writer: true))
        {
            Assert.Equal(RegressionRunState.Queued, (await reader.ReadAsync(test.Actor, test.Organization, test.RunId))!.State);
            var claim = (await writer.ClaimAsync(test.Organization, test.RunId, Guid.NewGuid())).Claim!;
            Assert.True(await writer.CompleteAsync(claim, test.Success()));
        }
        await fixture.RestartAsync();
        await using var finalReader = new PostgresRegressionRunStore(test.ReaderConnection, writer: false);
        await using var finalWriter = new PostgresRegressionRunStore(test.WriterConnection, writer: true);
        await using var cases = new PostgresRegressionCaseStore(test.Cases.ReaderConnection, RegressionCaseDatabaseProfile.Reader);
        var view = await new RegressionRunReadService(finalReader, cases).ReadAsync(test.Actor, test.Organization, test.RunId);
        Assert.Equal(RegressionComparisonOutcome.Passed, view!.Result!.Comparison!.Outcome);
        Assert.Equal(RegressionRunClaimOutcome.Finished, (await finalWriter.ClaimAsync(test.Organization, test.RunId, Guid.NewGuid())).Outcome);
    }

    [PostgresFact]
    public async Task Cancellation_invalidates_running_claims_and_cannot_replace_a_completed_result()
    {
        await using var test = await Context.CreateAsync(fixture);
        await test.SubmitAsync();
        var claim = await test.ClaimAsync();
        Assert.True(await test.Writer.CancelAsync(test.Actor, test.Organization, test.RunId));
        Assert.False(await test.Writer.ObserveAsync(claim));
        Assert.False(await test.Writer.CompleteAsync(claim, test.Success()));
        Assert.Equal(RegressionRunState.Canceled, (await test.Reader.ReadAsync(test.Actor, test.Organization, test.RunId))!.State);
    }

    [PostgresFact]
    public async Task Revocation_stops_completion_and_private_reads_without_discarding_the_run()
    {
        await using var test = await Context.CreateAsync(fixture);
        await test.SubmitAsync();
        var claim = await test.ClaimAsync();
        await test.Cases.RoleAsync("viewer");
        Assert.False(await test.Writer.ObserveAsync(claim));
        Assert.False(await test.Writer.CompleteAsync(claim, test.Success()));
        Assert.Null(await test.Reader.ReadAsync(test.Actor, test.Organization, test.RunId));
        await test.Cases.RoleAsync("runner");
        Assert.Equal(RegressionRunState.AuthorityRevoked, (await test.Reader.ReadAsync(test.Actor, test.Organization, test.RunId))!.State);
    }

    [PostgresFact]
    public async Task Reader_role_cannot_write_and_neither_role_has_direct_row_access()
    {
        await using var test = await Context.CreateAsync(fixture);
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Reader.SubmitAsync(new(test.Actor, test.RunId, test.Reference)));
        foreach (var text in new[] { test.ReaderConnection, test.WriterConnection })
        {
            await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(text) { Pooling = false }.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT * FROM braid.regression_runs", connection);
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState);
        }
    }

    [PostgresFact]
    public async Task Changed_reference_and_other_submitters_never_adopt_an_existing_request()
    {
        await using var test = await Context.CreateAsync(fixture);
        await test.SubmitAsync();
        var next = test.Cases.Version(2);
        await test.Cases.Writer.SaveAsync(test.Actor, next);
        var reference = new RegressionCaseEnvelopeCodec().Encode(next).Reference;
        Assert.Equal(RegressionRunSubmitOutcome.Conflict, await test.Writer.SubmitAsync(new(test.Actor, test.RunId, reference)));
        var outsider = new ActorContext(new(test.Cases.Seed.Bob));
        Assert.Null(await test.Reader.ReadAsync(outsider, test.Organization, test.RunId));
        Assert.False(await test.Writer.CancelAsync(outsider, test.Organization, test.RunId));
        Assert.Null(await test.Reader.ReadAsync(test.Actor, test.Cases.Seed.OrganizationB, test.RunId));
    }

    [PostgresFact]
    public async Task Concurrent_retries_publish_once_and_only_one_worker_claims()
    {
        await using var test = await Context.CreateAsync(fixture);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => test.SubmitAsync()));
        Assert.Single(outcomes, result => result == RegressionRunSubmitOutcome.Created);
        Assert.Equal(5, outcomes.Count(result => result == RegressionRunSubmitOutcome.Replayed));
        var claims = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => test.Writer.ClaimAsync(test.Organization, test.RunId, Guid.NewGuid())));
        Assert.Single(claims, result => result.Outcome == RegressionRunClaimOutcome.Claimed);
        Assert.Equal(3, claims.Count(result => result.Outcome == RegressionRunClaimOutcome.Busy));
    }

    [PostgresFact]
    public async Task Active_admission_is_bounded_but_exact_replays_do_not_consume_another_slot()
    {
        await using var test = await Context.CreateAsync(fixture);
        await test.SubmitAsync();
        for (var index = 1; index < 32; index++)
            Assert.Equal(RegressionRunSubmitOutcome.Created, await test.Writer.SubmitAsync(new(test.Actor, Guid.NewGuid(), test.Reference)));
        Assert.Equal(RegressionRunSubmitOutcome.Busy, await test.Writer.SubmitAsync(new(test.Actor, Guid.NewGuid(), test.Reference)));
        Assert.Equal(RegressionRunSubmitOutcome.Replayed, await test.SubmitAsync());
        Assert.True(await test.Writer.CancelAsync(test.Actor, test.Organization, test.RunId));
        Assert.Equal(RegressionRunSubmitOutcome.Created, await test.Writer.SubmitAsync(new(test.Actor, Guid.NewGuid(), test.Reference)));
    }

    [PostgresFact]
    public async Task Expired_attempt_cannot_overwrite_a_reclaimed_run()
    {
        await using var test = await Context.CreateAsync(fixture);
        await test.SubmitAsync();
        var first = await test.ClaimAsync();
        Assert.Empty(await test.Queue.ListReadyAsync(16));
        // Exercise real database-clock expiry, without rewriting lease data or replacing the clock.
        await Task.Delay(TimeSpan.FromSeconds(91));
        Assert.Equal(test.RunId, Assert.Single(await test.Queue.ListReadyAsync(16)).RunId);
        var second = await test.ClaimAsync();
        Assert.Equal(2, second.Attempt);
        Assert.NotEqual(first.Token, second.Token);
        Assert.False(await test.Writer.CompleteAsync(first, test.Success()));
        Assert.True(await test.Writer.CompleteAsync(second, test.Success()));
    }

    [RegressionPostgresTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Failed_cases_retry_independently_and_successful_runs_are_not_executed_again(int failedIndex)
    {
        await using var test = await Context.CreateAsync(fixture);
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        foreach (var id in ids) await test.Writer.SubmitAsync(new(test.Actor, id, test.Reference));
        var backend = new ScriptedExecution(test.Success(), failOn: failedIndex + 1);
        var executor = new RegressionRunExecutor(test.Writer, backend);
        for (var index = 0; index < ids.Length; index++)
            Assert.Equal(index == failedIndex ? RegressionRunDispatchOutcome.Deferred : RegressionRunDispatchOutcome.Completed,
                await executor.ExecuteAsync(test.Organization, ids[index], Guid.NewGuid()));
        await Task.Delay(1100);
        foreach (var id in ids) await executor.ExecuteAsync(test.Organization, id, Guid.NewGuid());
        Assert.Equal(4, backend.Calls);
        Assert.All(await Task.WhenAll(ids.Select(id => test.Reader.ReadAsync(test.Actor, test.Organization, id))), run => Assert.Equal(RegressionRunState.Completed, run!.State));
    }

    [PostgresFact]
    public async Task Three_failed_attempts_finish_without_an_infinite_retry_loop()
    {
        await using var test = await Context.CreateAsync(fixture);
        await test.SubmitAsync();
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var claim = await test.ClaimAsync();
            Assert.Equal(attempt, claim.Attempt);
            Assert.True(await test.Writer.ReleaseAsync(claim));
            await Task.Delay(1100);
        }
        Assert.Equal(RegressionRunClaimOutcome.Finished, (await test.Writer.ClaimAsync(test.Organization, test.RunId, Guid.NewGuid())).Outcome);
        Assert.Equal(RegressionRunState.AttemptsExhausted, (await test.Reader.ReadAsync(test.Actor, test.Organization, test.RunId))!.State);
    }

    [PostgresFact]
    public async Task Lost_submission_acknowledgement_replays_the_same_run()
    {
        await using var test = await Context.CreateAsync(fixture);
        await using var relay = new ServerSessionCommitAckLossRelay(test.WriterConnection);
        await using var routed = new PostgresRegressionRunStore(relay.RoutedConnectionString, writer: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var pending = routed.SubmitAsync(new(test.Actor, test.RunId, test.Reference), deadline.Token);
        try
        {
            await Task.WhenAny(pending, relay.CommitAcknowledgementWithheld).WaitAsync(TimeSpan.FromSeconds(10), deadline.Token);
            Assert.True(relay.CommitAcknowledgementWithheld.IsCompletedSuccessfully && !pending.IsCompleted);
            Assert.True(relay.FrontendCommitSeen && relay.BackendCommitCompleteSeen && relay.BackendIdleReadySeen);
            Assert.NotNull(await test.Reader.ReadAsync(test.Actor, test.Organization, test.RunId, deadline.Token));
            relay.DropAcknowledgement();
            await Assert.ThrowsAnyAsync<NpgsqlException>(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5), deadline.Token));
            Assert.Equal(RegressionRunSubmitOutcome.Replayed, await test.SubmitAsync());
            Assert.Equal(1L, await test.Cases.Case.ScalarAsync<long>("SELECT count(*) FROM braid.regression_runs"));
        }
        finally { relay.DropAcknowledgement(); try { await pending; } catch (NpgsqlException) { } }
    }

    [PostgresFact]
    public async Task Maximum_output_is_retained_exactly_and_recompared_after_reopening()
    {
        await using var test = await Context.CreateAsync(fixture);
        await test.SubmitAsync();
        var bytes = Enumerable.Repeat((byte)255, PreviewPolicy.MaximumOutputBytes).ToArray();
        var outputs = RegressionOutputSet.Capture([RegressionOutput.Capture(RegressionOutputKey.Primary, bytes)]);
        var result = RegressionCaseExecution.Complete(MappingPreviewStatus.Succeeded, outputs,
            new RegressionComparisonService().Compare(test.Value.Expected, new(OperationStatusV1.Succeeded, outputs)));
        Assert.True(await test.Writer.CompleteAsync(await test.ClaimAsync(), result));
        var view = await new RegressionRunReadService(test.Reader, test.Cases.Reader).ReadAsync(test.Actor, test.Organization, test.RunId);
        Assert.Equal(RegressionComparisonOutcome.Failed, view!.Result!.Comparison!.Outcome);
        Assert.Equal(bytes, Assert.Single(view.Result.Outputs!.Outputs).CopyBytes());
    }

    [PostgresFact]
    public async Task Stored_result_corruption_is_not_interpreted_as_a_pass()
    {
        await using var test = await Context.CreateAsync(fixture);
        await test.SubmitAsync();
        Assert.True(await test.Writer.CompleteAsync(await test.ClaimAsync(), test.Success()));
        await test.Cases.Case.ExecuteAsync("""
            ALTER TABLE braid.regression_runs DISABLE TRIGGER USER;
            UPDATE braid.regression_runs SET result_body=convert_to('invalid','UTF8'),result_bytes=7,
                result_digest=encode(sha256(convert_to('invalid','UTF8')),'hex');
            ALTER TABLE braid.regression_runs ENABLE TRIGGER USER;
            """);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new RegressionRunReadService(test.Reader, test.Cases.Reader)
            .ReadAsync(test.Actor, test.Organization, test.RunId));
    }

    [PostgresFact]
    public async Task Column_privilege_changes_refuse_the_pool_before_exposing_private_content()
    {
        await using var test = await Context.CreateAsync(fixture);
        await test.Cases.Case.ExecuteAsync("GRANT SELECT(result_body) ON braid.regression_runs TO braid_run_reader");
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Reader.ReadAsync(test.Actor, test.Organization, test.RunId));
    }

    [PostgresFact]
    public async Task Mismatched_bound_organization_cannot_claim_cancel_release_or_complete_another_organizations_run()
    {
        await using var test = await Context.CreateAsync(fixture);
        await test.SubmitAsync();
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(test.WriterConnection) { Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var settings = new NpgsqlCommand("SELECT set_config('braid.organization_id',@org,true),set_config('braid.run_principal_id',@actor,true)", connection, transaction))
        {
            settings.Parameters.AddWithValue("org", test.Cases.Seed.OrganizationB.ToString("D"));
            settings.Parameters.AddWithValue("actor", test.Actor.PrincipalId.Value.ToString("D"));
            await settings.ExecuteNonQueryAsync();
        }
        await using (var command = new NpgsqlCommand("SELECT outcome FROM braid.claim_regression_run(@org,@id,@worker)", connection, transaction))
        {
            command.Parameters.AddWithValue("org", test.Organization);
            command.Parameters.AddWithValue("id", test.RunId);
            command.Parameters.AddWithValue("worker", Guid.NewGuid());
            Assert.Equal(3, await command.ExecuteScalarAsync());
        }
        await transaction.CommitAsync();
        Assert.Equal(RegressionRunState.Queued, (await test.Reader.ReadAsync(test.Actor, test.Organization, test.RunId))!.State);
        var claim = await test.ClaimAsync();
        await using var second = await connection.BeginTransactionAsync();
        await using (var settings = new NpgsqlCommand("SELECT set_config('braid.organization_id',@org,true),set_config('braid.run_principal_id',@actor,true)", connection, second))
        {
            settings.Parameters.AddWithValue("org", test.Cases.Seed.OrganizationB.ToString("D"));
            settings.Parameters.AddWithValue("actor", test.Actor.PrincipalId.Value.ToString("D"));
            await settings.ExecuteNonQueryAsync();
        }
        foreach (var expression in new[]
        {
            "braid.observe_regression_run(@org,@id,@worker,@token,@attempt)",
            "braid.release_regression_run(@org,@id,@worker,@token,@attempt)",
            "braid.finish_regression_run(@org,@id,@worker,@token,@attempt,NULL,NULL,NULL)",
            "braid.cancel_regression_run(@org,@id)"
        })
        {
            await using var command = new NpgsqlCommand("SELECT " + expression, connection, second);
            command.Parameters.AddWithValue("org", test.Organization);
            command.Parameters.AddWithValue("id", test.RunId);
            command.Parameters.AddWithValue("worker", claim.Worker);
            command.Parameters.AddWithValue("token", claim.Token);
            command.Parameters.AddWithValue("attempt", claim.Attempt);
            Assert.Equal(false, await command.ExecuteScalarAsync());
        }
        await second.CommitAsync();
        Assert.True(await test.Writer.ObserveAsync(claim));
    }

    [PostgresFact]
    public async Task Different_authorized_submitters_racing_for_one_run_get_a_conflict_not_a_database_error()
    {
        await using var test = await Context.CreateAsync(fixture);
        var other = new ActorContext(new(test.Cases.Seed.Admitted));
        await test.Cases.Seed.AddMembershipAsync(test.Cases.Case, test.Organization, test.Value.Revision.ProjectId, other.PrincipalId.Value, "runner");
        await using var otherWriter = new PostgresRegressionRunStore(test.WriterConnection, writer: true);
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(test.Cases.Case.MigrationConnectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        await using var barrier = await connection.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("LOCK TABLE braid.regression_runs IN SHARE MODE", connection, barrier))
            await command.ExecuteNonQueryAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var first = test.Writer.SubmitAsync(new(test.Actor, test.RunId, test.Reference), deadline.Token);
        var second = otherWriter.SubmitAsync(new(other, test.RunId, test.Reference), deadline.Token);
        var committed = false;
        try
        {
            var entered = false;
            for (var attempt = 0; attempt < 30; attempt++)
            {
                await using var count = new NpgsqlCommand("SELECT count(*) FROM pg_locks WHERE relation='braid.regression_runs'::regclass AND mode='RowExclusiveLock' AND NOT granted", connection, barrier);
                if (await count.ExecuteScalarAsync(deadline.Token) is long waiting && waiting == 2) { entered = true; break; }
                await Task.Delay(50, deadline.Token);
            }
            Assert.True(entered, "Both independent connections must reach the insert barrier.");
            await barrier.CommitAsync(deadline.Token);
            committed = true;
            var outcomes = await Task.WhenAll(first, second);
            Assert.Contains(RegressionRunSubmitOutcome.Created, outcomes);
            Assert.Contains(RegressionRunSubmitOutcome.Conflict, outcomes);
            Assert.Equal(1L, await test.Cases.Case.ScalarAsync<long>("SELECT count(*) FROM braid.regression_runs"));
        }
        finally
        {
            if (!committed) await barrier.RollbackAsync();
            deadline.Cancel();
            try { await Task.WhenAll(first, second); } catch (Exception error) when (error is NpgsqlException or OperationCanceledException) { }
        }
    }

    private sealed class ScriptedExecution(RegressionCaseExecution result, int failOn) : IRegressionCaseExecutor
    {
        internal int Calls { get; private set; }
        public Task<RegressionCaseExecution> ExecuteAsync(ActorContext actor, RegressionCaseReference expected, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Calls == failOn ? RegressionCaseExecution.Refused(RegressionCaseExecutionOutcome.InfrastructureFailure) : result);
        }
    }

}
