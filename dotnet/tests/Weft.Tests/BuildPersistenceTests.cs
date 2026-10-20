using System.Text;
using System.Text.Json.Nodes;
using Npgsql;
using Weft.Application;
using Weft.Contracts.Operations;
using Weft.Deployment;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
public sealed class BuildPersistenceTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task Build_schema_matches_the_qualified_shape()
    {
        await using var test = await fixture.CreateCaseAsync();
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(test.MigrationConnectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var settings = new NpgsqlCommand("SET LOCAL TimeZone='UTC'; SET LOCAL DateStyle='ISO, MDY'", connection, transaction)) await settings.ExecuteNonQueryAsync();
        var fingerprint = await RegressionCaseSchemaContract.CaptureAsync(connection, transaction, BuildRoleGuard.Tables, CancellationToken.None);
        Assert.True(fingerprint == BuildRoleGuard.Fingerprint, "Build schema fingerprint: " + fingerprint);
    }

    [PostgresFact]
    public async Task SQL_canonical_identity_matches_the_application_for_escaped_and_UTF16_ordered_names()
    {
        await using var test = await fixture.CreateCaseAsync();
        var scope = new Weft.Domain.RevisionScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var intent = new BuildIntent(Guid.NewGuid(), scope, Weft.Domain.Sha256Digest.FromBytes([1]), FixtureBuildCompiler.Backend,
            FixtureBuildCompiler.Profile, [new(BuildBoundaryKind.PrimaryInput, null, "fixture-bytes"),
                new(BuildBoundaryKind.NamedOutput, "\ue000", "fixture-bytes"), new(BuildBoundaryKind.NamedOutput, "\U00010000", "fixture-bytes"),
                new(BuildBoundaryKind.NamedOutput, "quote\"+&'`\\é", "fixture-bytes")], new(new(scope.OrganizationId, scope.ProjectId), Guid.NewGuid(), 1), Guid.NewGuid(), Guid.NewGuid());
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(test.MigrationConnectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT convert_to(weft.build_intent_canonical(@body),'UTF8')", connection);
        command.Parameters.AddWithValue("body", NpgsqlTypes.NpgsqlDbType.Jsonb, Encoding.UTF8.GetString(intent.CopyCanonicalBytes()));
        Assert.Equal(intent.CopyCanonicalBytes(), (byte[])(await command.ExecuteScalarAsync())!);
    }

    [PostgresFact]
    public async Task Missing_build_roles_leave_the_previous_manifest_unchanged()
    {
        var isolated = new PostgresFixture(provisionMetadataEditor: true, provisionBuildProfiles: false);
        await isolated.InitializeAsync();
        try
        {
            await using var test = await isolated.CreateCaseAsync(migrate: false);
            await using (var prefix = new MigrationRunner(test.MigrationConnectionString, MigrationRunner.EmbeddedMigrations.Take(17).ToArray())) await prefix.MigrateAsync();
            var history = await test.ScalarAsync<string>("SELECT jsonb_agg(to_jsonb(r) ORDER BY version)::text FROM weft.schema_migrations r");
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Assert.ThrowsAsync<PostgresException>(() => test.MigrateAsync())).SqlState);
            Assert.Equal(history, await test.ScalarAsync<string>("SELECT jsonb_agg(to_jsonb(r) ORDER BY version)::text FROM weft.schema_migrations r"));
            Assert.True(await test.ScalarAsync<bool>("SELECT to_regclass('weft.build_approvals') IS NULL AND to_regclass('weft.build_publications') IS NULL"));
        }
        finally { await isolated.DisposeAsync(); }
    }

    [PostgresFact]
    public async Task Reader_cannot_mutate_and_neither_profile_has_direct_table_access()
    {
        await using var test = await Context.CreateAsync(fixture);
        await using var reader = new PostgresBuildStore(test.Runs.Cases.Case.RegressionConnectionString("weft_build_reader"), false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ApproveAsync(test.Actor, new(test.Actor.PrincipalId, test.Intent, test.Native, test.Evidence)));
        foreach (var role in new[] { "weft_build_reader", "weft_build_writer" })
        {
            await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(test.Runs.Cases.Case.RegressionConnectionString(role)) { Pooling = false }.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT * FROM weft.build_publications", connection);
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState);
        }
    }

    [PostgresFact]
    public async Task A_broader_role_grant_or_changed_table_refuses_private_reads()
    {
        await using var test = await Context.CreateAsync(fixture);
        await test.Approve(); await test.Stage([1]);
        await test.Runs.Cases.Case.BootstrapAsync("GRANT SELECT ON weft.build_publications TO weft_build_writer");
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Store.ReadStagedAsync(test.Actor, test.Intent.Revision.OrganizationId, test.Intent.BuildId));
        await test.Runs.Cases.Case.BootstrapAsync("REVOKE SELECT ON weft.build_publications FROM weft_build_writer");
        await test.Runs.Cases.Case.BootstrapAsync("ALTER TABLE weft.build_publications ADD COLUMN unqualified integer");
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Store.ReadStagedAsync(test.Actor, test.Intent.Revision.OrganizationId, test.Intent.BuildId));
    }

    [PostgresFact]
    public async Task Approval_stage_and_exact_artifact_survive_a_fresh_store()
    {
        await using var test = await Context.CreateAsync(fixture);
        Assert.Equal(BuildControlOutcome.Created, await test.Approve());
        Assert.Equal(BuildControlOutcome.Replayed, await test.Approve());
        var bytes = new BuildBytes([1, 2, 3, 255], test.Intent.Profile.MaximumArtifactBytes);
        var reference = BuildArtifactReference.Restore(test.Intent, bytes.Digest, bytes.Length);
        var publication = new BuildPublication(test.Actor.PrincipalId, reference,
            new(Weft.Domain.PrivateObjectId.Restore(test.Intent.Revision, test.Intent.BuildId, Weft.Domain.PrivateObjectClass.DerivedArtifact), bytes.Digest, bytes.Length));
        var staged = await test.Store.StageAsync(test.Actor, new(publication, bytes));
        Assert.NotNull(staged);
        var objectStore = new PostgresBuildObjectStore(test.Store, new NoNative(), test.Actor);
        using var stream = new MemoryStream(bytes.Copy());
        Assert.Equal(PrivateObjectPublishResult.Created, await objectStore.PublishCreateOnlyAsync(test.Intent.Revision, publication.Object, stream));
        Assert.Equal(BuildPublicationOutcome.Created, await test.Store.PublishAsync(test.Actor, publication));
        await using var fresh = new PostgresBuildStore(test.Connection, true);
        var recovered = await fresh.ReadAsync(test.Actor, test.Intent.Revision.OrganizationId, test.Intent.BuildId);
        Assert.Equal(test.Intent.Fingerprint, recovered!.Artifact.Intent.Fingerprint);
        Assert.Equal(bytes.Copy(), await new PostgresBuildObjectStore(fresh, new NoNative(), test.Actor).ReadVerifiedAsync(test.Intent.Revision, recovered.Object));
    }

    [PostgresFact]
    public async Task Application_workflow_uses_real_admission_staging_and_restart_safe_catalog()
    {
        await using var test = await Context.CreateAsync(fixture);
        await using var projects = new PostgresDatabase(test.Runs.Cases.Case.RuntimeConnectionString);
        await using var sources = new PostgresRevisionReportDatabase(test.Runs.Cases.Case.ReportConnectionString);
        var admission = new BuildControlService(test.Store, new PostgresProjectReader(projects), new PostgresRevisionReportMetadataReader(sources),
            new Weft.Application.Regression.RegressionRunReadService(test.Runs.Reader, test.Runs.Cases.Reader));
        Assert.Equal(BuildControlOutcome.Created, await admission.ApproveAsync(test.Actor, test.Intent));
        Assert.NotNull(await admission.ReadAsync(test.Actor, test.Intent));
        var compiler = new DatabaseFixtureCompiler();
        var workflow = new BuildWorkflow(admission, compiler, test.Store, new PostgresBuildObjectStore(test.Store, new SyntheticNative(), test.Actor));
        Assert.Equal(BuildExecutionOutcome.Created, (await workflow.ExecuteAsync(test.Actor, test.Intent)).Outcome);
        await using var fresh = new PostgresBuildStore(test.Connection, true);
        var restarted = new BuildWorkflow(admission, compiler, fresh, new PostgresBuildObjectStore(fresh, new SyntheticNative(), test.Actor));
        Assert.Equal(BuildExecutionOutcome.Replayed, (await restarted.ExecuteAsync(test.Actor, test.Intent)).Outcome);
        Assert.Equal(1, compiler.Calls);
        await test.Runs.Cases.RoleAsync("viewer");
        Assert.Equal(BuildExecutionOutcome.Refused, (await restarted.ExecuteAsync(test.Actor, test.Intent)).Outcome);
    }

    [PostgresFact]
    public async Task First_stage_wins_and_revocation_refuses_publication_without_losing_bytes()
    {
        await using var test = await Context.CreateAsync(fixture);
        await test.Approve();
        var one = await test.Stage([1, 2, 3]);
        var two = await test.Stage([9, 8, 7]);
        Assert.Equal(one!.Bytes.Copy(), two!.Bytes.Copy());
        Assert.True(await test.Store.RevokeAsync(test.Actor, test.Intent.Revision.OrganizationId, test.Intent.Revision.ProjectId, test.Intent.ApprovalId));
        Assert.Null(await test.Store.ReadStagedAsync(test.Actor, test.Intent.Revision.OrganizationId, test.Intent.BuildId));
        Assert.Equal(BuildPublicationOutcome.Refused, await test.Store.PublishAsync(test.Actor, one.Publication));
        Assert.Equal(1L, await test.Runs.Cases.Case.ScalarAsync<long>("SELECT count(*) FROM weft.build_publications"));
    }

    [PostgresFact]
    public async Task A_new_environment_version_invalidates_old_approval()
    {
        await using var test = await Context.CreateAsync(fixture);
        await test.Approve();
        var old = test.Intent.Environment;
        Assert.Equal(BuildControlOutcome.Created, await test.Store.SaveEnvironmentAsync(test.Actor, new(new(old.Project, old.EnvironmentId, 2), test.Intent.Profile, true)));
        Assert.Null(await test.Store.ReadApprovalAsync(test.Actor, test.Intent));
        Assert.Null(await test.Stage([1, 2, 3]));
    }

    [PostgresFact]
    public async Task Staged_bytes_survive_database_restart_without_recompilation()
    {
        await using var test = await Context.CreateAsync(fixture);
        await test.Approve();
        var staged = await test.Stage([1, 2, 3, 255]);
        await test.Store.DisposeAsync();
        await fixture.RestartAsync();
        await using var fresh = new PostgresBuildStore(test.Connection, true);
        var recovered = await fresh.ReadStagedAsync(test.Actor, test.Intent.Revision.OrganizationId, test.Intent.BuildId);
        Assert.Equal(staged!.Bytes.Copy(), recovered!.Bytes.Copy());
        Assert.Equal(staged.Publication.Artifact.ContentDigest, recovered.Publication.Artifact.ContentDigest);
    }

    [PostgresFact]
    public async Task Another_active_publisher_cannot_read_or_adopt_private_staging()
    {
        await using var test = await Context.CreateAsync(fixture);
        await test.Approve(); var staged = await test.Stage([1, 2, 3]);
        var other = new ActorContext(new(test.Runs.Cases.Seed.Admitted));
        await test.Runs.Cases.Seed.AddMembershipAsync(test.Runs.Cases.Case, test.Runs.Organization, test.Intent.Revision.ProjectId, other.PrincipalId.Value, "publisher");
        Assert.Null(await test.Store.ReadApprovalAsync(other, test.Intent));
        Assert.Null(await test.Store.ReadStagedAsync(other, test.Intent.Revision.OrganizationId, test.Intent.BuildId));
        Assert.Equal(BuildPublicationOutcome.Refused, await test.Store.PublishAsync(other, staged!.Publication with { Actor = other.PrincipalId }));
    }

    [PostgresFact]
    public async Task Failed_regression_bytes_cannot_be_approved_even_with_a_successful_execution_status()
    {
        await using var test = await Context.CreateAsync(fixture, pass: false);
        Assert.Equal(BuildControlOutcome.Refused, await test.Approve());
        Assert.Null(await test.Store.ReadApprovalAsync(test.Actor, test.Intent));
    }

    [PostgresFact]
    public async Task Null_output_content_is_not_passing_evidence()
    {
        await using var test = await Context.CreateAsync(fixture, nullContent: true);
        Assert.Equal(BuildControlOutcome.Refused, await test.Approve());
    }

    [RegressionPostgresTheory]
    [InlineData("engine")]
    [InlineData("build")]
    [InlineData("adapter")]
    public async Task Different_runtime_identity_cannot_reuse_regression_evidence(string field)
    {
        await using var test = await Context.CreateAsync(fixture);
        var value = test.Intent;
        var backend = new BuildBackendIdentity(value.Backend.BackendId, field == "engine" ? "different" : value.Backend.EngineRevision,
            field == "build" ? "different" : value.Backend.EngineBuild, field == "adapter" ? "different" : value.Backend.AdapterVersion);
        var changed = new BuildIntent(value.BuildId, value.Revision, value.NativeDigest, backend, value.Profile, value.Boundaries, value.Environment, value.ApprovalId, value.RegressionEvidenceId);
        Assert.Equal(BuildControlOutcome.Refused, await test.Store.ApproveAsync(test.Actor, new(test.Actor.PrincipalId, changed, test.Native, test.Evidence)));
    }

    [RegressionPostgresTheory]
    [InlineData("schema")]
    [InlineData("organizationId")]
    [InlineData("projectId")]
    [InlineData("approvalId")]
    public async Task Missing_approval_identity_fields_are_refused_before_insert(string field)
    {
        await using var test = await Context.CreateAsync(fixture);
        var body = JsonNode.Parse(test.Intent.CopyCanonicalBytes())!;
        body[field] = null;
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(test.Connection) { Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT set_config('weft.organization_id',@orgtext,true),set_config('weft.build_principal_id',@actor,true);
            SELECT weft.write_build_record('approve',@org,@project,@key,0,@info,@body,NULL)
            """, connection, tx);
        command.Parameters.AddWithValue("orgtext", test.Intent.Revision.OrganizationId.ToString("D"));
        command.Parameters.AddWithValue("actor", test.Actor.PrincipalId.Value.ToString("D"));
        command.Parameters.AddWithValue("org", test.Intent.Revision.OrganizationId);
        command.Parameters.AddWithValue("project", test.Intent.Revision.ProjectId);
        command.Parameters.AddWithValue("key", test.Intent.ApprovalId);
        command.Parameters.AddWithValue("info", NpgsqlTypes.NpgsqlDbType.Jsonb, "{\"evidenceDigest\":\"" + test.Evidence.Value + "\"}");
        command.Parameters.AddWithValue("body", Encoding.UTF8.GetBytes(body.ToJsonString()));
        await using var result = await command.ExecuteReaderAsync();
        Assert.True(await result.NextResultAsync()); Assert.True(await result.ReadAsync()); Assert.Equal(3, result.GetInt32(0));
    }

    [PostgresFact]
    public async Task Revoked_stages_remain_retained_without_consuming_active_admission_forever()
    {
        await using var test = await Context.CreateAsync(fixture);
        for (var i = 0; i < 34; i++)
        {
            var value = test.Intent;
            var intent = new BuildIntent(Guid.NewGuid(), value.Revision, value.NativeDigest, value.Backend, value.Profile, value.Boundaries, value.Environment, Guid.NewGuid(), value.RegressionEvidenceId);
            Assert.Equal(BuildControlOutcome.Created, await test.Store.ApproveAsync(test.Actor, new(test.Actor.PrincipalId, intent, test.Native, test.Evidence)));
            var bytes = new BuildBytes([1, 2, 3], intent.Profile.MaximumArtifactBytes);
            var artifact = BuildArtifactReference.Restore(intent, bytes.Digest, bytes.Length);
            Assert.NotNull(await test.Store.StageAsync(test.Actor, new(new(test.Actor.PrincipalId, artifact,
                new(Weft.Domain.PrivateObjectId.Restore(intent.Revision, intent.BuildId, Weft.Domain.PrivateObjectClass.DerivedArtifact), bytes.Digest, bytes.Length)), bytes)));
            Assert.True(await test.Store.RevokeAsync(test.Actor, intent.Revision.OrganizationId, intent.Revision.ProjectId, intent.ApprovalId));
        }
        Assert.Equal(34L, await test.Runs.Cases.Case.ScalarAsync<long>("SELECT count(*) FROM weft.build_publications"));
    }

    [PostgresFact]
    public async Task Actor_quota_is_serialized_across_two_projects()
    {
        await using var test = await Context.CreateAsync(fixture);
        BuildIntent Next() => new(Guid.NewGuid(), test.Intent.Revision, test.Intent.NativeDigest, test.Intent.Backend,
            test.Intent.Profile, test.Intent.Boundaries, test.Intent.Environment, Guid.NewGuid(), test.Intent.RegressionEvidenceId);
        async Task<StagedBuild?> Stage(BuildIntent intent, Weft.Domain.PrivateObjectReference native, Weft.Domain.Sha256Digest evidence)
        {
            Assert.Equal(BuildControlOutcome.Created, await test.Store.ApproveAsync(test.Actor, new(test.Actor.PrincipalId, intent, native, evidence)));
            var bytes = new BuildBytes([1], intent.Profile.MaximumArtifactBytes);
            return await test.Store.StageAsync(test.Actor, new(new(test.Actor.PrincipalId, BuildArtifactReference.Restore(intent, bytes.Digest, 1),
                new(Weft.Domain.PrivateObjectId.Restore(intent.Revision, intent.BuildId, Weft.Domain.PrivateObjectClass.DerivedArtifact), bytes.Digest, 1)), bytes));
        }
        for (var i = 0; i < 31; i++) Assert.NotNull(await Stage(Next(), test.Native, test.Evidence));

        var cases = test.Runs.Cases;
        var project = cases.Seed.HiddenProjectA;
        await cases.Seed.AddMembershipAsync(cases.Case, test.Runs.Organization, project, test.Actor.PrincipalId.Value, "administrator");
        await using var ingestionDatabase = new PostgresIngestionDatabase(cases.Case.IngestionConnectionString);
        var ingestion = new PostgresRevisionIngestionStore(ingestionDatabase);
        var reservation = await ingestion.ReserveAsync(test.Actor, new(test.Runs.Organization, project), Guid.NewGuid(),
            PostgresIngestionTestSupport.CreateIntent(), PostgresIngestionTestSupport.Profile, PostgresIngestionTestSupport.Policy);
        var sourceFacts = reservation.Receipt!.Facts;
        var ingestionClaim = (await ingestion.ClaimAsync(test.Actor, sourceFacts.Project, sourceFacts.OperationId, Guid.NewGuid())).Claim!;
        await ingestion.MarkStagedAsync(test.Actor, ingestionClaim, sourceFacts.NativeObject);
        await ingestion.CompleteAttemptAsync(test.Actor, ingestionClaim, PostgresIngestionTestSupport.SyntheticCompletion(sourceFacts));
        var source = (await ingestion.FinalizeAsync(test.Actor, ingestionClaim)).Receipt!;
        var value = new Weft.Application.Regression.RegressionCaseVersion(Guid.NewGuid(), 1, test.Actor.PrincipalId, "other-project",
            source.Facts.FutureRevision, source.Facts.NativeObject.Digest, cases.Runtime, test.Runs.Value.Replay,
            test.Runs.Value.ComparisonPolicy, test.Runs.Value.Expected);
        await cases.Writer.SaveAsync(test.Actor, value);
        var runId = Guid.NewGuid();
        var reference = new Weft.Application.Regression.RegressionCaseEnvelopeCodec().Encode(value).Reference;
        await test.Runs.Writer.SubmitAsync(new(test.Actor, runId, reference));
        var claim = (await test.Runs.Writer.ClaimAsync(test.Runs.Organization, runId, Guid.NewGuid())).Claim!;
        await test.Runs.Writer.CompleteAsync(claim, test.Runs.Success());
        var evidence = (await test.Runs.Reader.ReadAsync(test.Actor, test.Runs.Organization, runId))!.CompletionDigest!;
        var environment = new BuildEnvironmentReference(new(test.Runs.Organization, project), Guid.NewGuid(), 1);
        await test.Store.SaveEnvironmentAsync(test.Actor, new(environment, test.Intent.Profile, true));
        var other = new BuildIntent(Guid.NewGuid(), value.Revision, value.NativeDigest, test.Intent.Backend, test.Intent.Profile,
            test.Intent.Boundaries, environment, Guid.NewGuid(), runId);
        var first = Next();
        await test.Store.ApproveAsync(test.Actor, new(test.Actor.PrincipalId, first, test.Native, test.Evidence));
        await test.Store.ApproveAsync(test.Actor, new(test.Actor.PrincipalId, other, source.Facts.NativeObject, evidence));
        await using var blocker = PostgresServerSessionTestSupport.Unpooled(test.Connection);
        await blocker.OpenAsync();
        await using var tx = await blocker.BeginTransactionAsync();
        await using (var settings = new NpgsqlCommand("SELECT set_config('weft.organization_id',@org,true),set_config('weft.build_principal_id',@actor,true)", blocker, tx))
        {
            settings.Parameters.AddWithValue("org", test.Runs.Organization.ToString("D"));
            settings.Parameters.AddWithValue("actor", test.Actor.PrincipalId.Value.ToString("D"));
            await settings.ExecuteNonQueryAsync();
        }
        var data = new BuildBytes([1], first.Profile.MaximumArtifactBytes);
        await using (var command = new NpgsqlCommand("SELECT weft.write_build_record('stage',@org,@project,@key,0,@info,@intent,@bytes)", blocker, tx))
        {
            command.Parameters.AddWithValue("org", first.Revision.OrganizationId); command.Parameters.AddWithValue("project", first.Revision.ProjectId);
            command.Parameters.AddWithValue("key", first.BuildId); command.Parameters.AddWithValue("intent", first.CopyCanonicalBytes());
            command.Parameters.AddWithValue("info", NpgsqlTypes.NpgsqlDbType.Jsonb, "{\"artifactDigest\":\"" + data.Digest.Value + "\"}");
            command.Parameters.AddWithValue("bytes", data.Copy()); Assert.Equal(0, await command.ExecuteScalarAsync());
        }
        var application = "build-quota-" + Guid.NewGuid().ToString("N");
        await using var observer = PostgresServerSessionTestSupport.Unpooled(cases.Case.MigrationConnectionString);
        await observer.OpenAsync();
        await using var edge = await PostgresServerSessionTestSupport.PrepareBlockedAsync(observer, application, blocker.ProcessID);
        await using var competing = new PostgresBuildStore(PostgresServerSessionTestSupport.Application(test.Connection, application), true);
        var secondArtifact = BuildArtifactReference.Restore(other, data.Digest, data.Length);
        var pending = competing.StageAsync(test.Actor, new(new(test.Actor.PrincipalId, secondArtifact,
            new(Weft.Domain.PrivateObjectId.Restore(other.Revision, other.BuildId, Weft.Domain.PrivateObjectClass.DerivedArtifact), data.Digest, data.Length)), data));
        try
        {
            await PostgresServerSessionTestSupport.WaitBlockedAsync(edge, pending);
            await tx.CommitAsync();
            Assert.Null(await pending);
        }
        finally
        {
            if (!pending.IsCompleted) { await tx.RollbackAsync(); await PostgresServerSessionTestSupport.SettleAsync(pending); }
        }
        Assert.Equal(32L, await cases.Case.ScalarAsync<long>("SELECT count(*) FROM weft.build_publications"));
    }

    [RegressionPostgresTheory]
    [InlineData("trailing-space")]
    [InlineData("duplicate")]
    [InlineData("reordered")]
    public async Task Noncanonical_approval_bytes_are_refused_before_retention(string mutation)
    {
        await using var test = await Context.CreateAsync(fixture);
        var text = Encoding.UTF8.GetString(test.Intent.CopyCanonicalBytes());
        text = mutation switch
        {
            "trailing-space" => text + "\n",
            "duplicate" => text.Replace("{\"schema\":", "{\"schema\":\"weft.build-intent.v1\",\"schema\":", StringComparison.Ordinal),
            _ => text.Replace("{\"schema\":\"weft.build-intent.v1\",", "{", StringComparison.Ordinal)[..^1] + ",\"schema\":\"weft.build-intent.v1\"}"
        };
        Assert.Equal(3, await RawWrite(test, "approve", test.Intent.ApprovalId, 0,
            "{\"evidenceDigest\":\"" + test.Evidence.Value + "\"}", Encoding.UTF8.GetBytes(text)));
        Assert.Equal(0L, await test.Runs.Cases.Case.ScalarAsync<long>("SELECT count(*) FROM weft.build_approvals"));
    }

    [RegressionPostgresTheory]
    [InlineData("numeric-string")]
    [InlineData("whitespace")]
    public async Task Malformed_environment_profiles_are_refused(string mutation)
    {
        await using var test = await Context.CreateAsync(fixture);
        var profile = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(test.Intent.Profile, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)))!;
        if (mutation == "numeric-string") profile["maximumArtifactBytes"] = "4096";
        else profile["id"] = "   ";
        Assert.Equal(3, await RawWrite(test, "environment", Guid.NewGuid(), 1, profile.ToJsonString(), null));
        Assert.Equal(1L, await test.Runs.Cases.Case.ScalarAsync<long>("SELECT count(*) FROM weft.build_environments"));
    }

    private static async Task<int> RawWrite(Context test, string operation, Guid key, long version, string info, byte[]? body)
    {
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(test.Connection) { Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT set_config('weft.organization_id',@orgtext,true),set_config('weft.build_principal_id',@actor,true);
            SELECT weft.write_build_record(@op,@org,@project,@key,@version,@info,@body,NULL)
            """, connection, tx);
        command.Parameters.AddWithValue("orgtext", test.Intent.Revision.OrganizationId.ToString("D"));
        command.Parameters.AddWithValue("actor", test.Actor.PrincipalId.Value.ToString("D"));
        command.Parameters.AddWithValue("op", operation); command.Parameters.AddWithValue("org", test.Intent.Revision.OrganizationId);
        command.Parameters.AddWithValue("project", test.Intent.Revision.ProjectId); command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("version", version); command.Parameters.AddWithValue("info", NpgsqlTypes.NpgsqlDbType.Jsonb, info);
        command.Parameters.AddWithValue("body", NpgsqlTypes.NpgsqlDbType.Bytea, (object?)body ?? DBNull.Value);
        int value;
        await using (var result = await command.ExecuteReaderAsync())
        { await result.NextResultAsync(); await result.ReadAsync(); value = result.GetInt32(0); }
        await tx.CommitAsync();
        return value;
    }

    private sealed class Context : IAsyncDisposable
    {
        internal required RegressionRunPostgresTestSupport Runs { get; init; }
        internal required PostgresBuildStore Store { get; init; }
        internal required string Connection { get; init; }
        internal required BuildIntent Intent { get; init; }
        internal required Weft.Domain.PrivateObjectReference Native { get; init; }
        internal required Weft.Domain.Sha256Digest Evidence { get; init; }
        internal ActorContext Actor => Runs.Actor;

        internal static async Task<Context> CreateAsync(PostgresFixture fixture, bool pass = true, bool nullContent = false)
        {
            var runs = await RegressionRunPostgresTestSupport.CreateAsync(fixture);
            PostgresBuildStore? store = null;
            try
            {
                await runs.Cases.RoleAsync("administrator");
                await runs.SubmitAsync();
                var claim = await runs.ClaimAsync();
                var result = pass ? runs.Success() : Weft.Application.Regression.RegressionCaseExecution.Complete(MappingPreviewStatus.Succeeded,
                    Weft.Application.Regression.RegressionOutputSet.Capture([Weft.Application.Regression.RegressionOutput.Capture(Weft.Application.Regression.RegressionOutputKey.Primary, [1])]),
                    new Weft.Application.Regression.RegressionComparisonService().Compare(runs.Value.Expected,
                        new(OperationStatusV1.Succeeded, Weft.Application.Regression.RegressionOutputSet.Capture([Weft.Application.Regression.RegressionOutput.Capture(Weft.Application.Regression.RegressionOutputKey.Primary, [1])]))));
                if (!nullContent) await runs.Writer.CompleteAsync(claim, result);
                else
                {
                    var document = JsonNode.Parse(new Weft.Application.Regression.RegressionRunResultCodec().Encode(runs.RunId, runs.Reference, result))!;
                    document["outputs"]![0]!["content"] = null;
                    var bytes = Encoding.UTF8.GetBytes(document.ToJsonString());
                    await using var runConnection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(runs.WriterConnection) { Pooling = false }.ConnectionString);
                    await runConnection.OpenAsync();
                    await using var transaction = await runConnection.BeginTransactionAsync();
                    await using var command = new NpgsqlCommand("""
                    SELECT set_config('weft.organization_id',@orgtext,true);
                    SELECT weft.finish_regression_run(@org,@id,@worker,@token,@generation,@body,@sha,@length)
                    """, runConnection, transaction);
                    command.Parameters.AddWithValue("orgtext", runs.Organization.ToString("D")); command.Parameters.AddWithValue("org", runs.Organization);
                    command.Parameters.AddWithValue("id", runs.RunId); command.Parameters.AddWithValue("worker", claim.Worker);
                    command.Parameters.AddWithValue("token", claim.Token); command.Parameters.AddWithValue("generation", claim.Attempt);
                    command.Parameters.AddWithValue("body", bytes); command.Parameters.AddWithValue("sha", Weft.Domain.Sha256Digest.FromBytes(bytes).Value);
                    command.Parameters.AddWithValue("length", bytes.Length);
                    await command.ExecuteNonQueryAsync(); await transaction.CommitAsync();
                }
                var snapshot = (await runs.Reader.ReadAsync(runs.Actor, runs.Organization, runs.RunId))!;
                var connection = runs.Cases.Case.RegressionConnectionString("weft_build_writer");
                store = new PostgresBuildStore(connection, true);
                var scope = runs.Value.Revision;
                var environment = new BuildEnvironmentReference(new(scope.OrganizationId, scope.ProjectId), Guid.NewGuid(), 1);
                var profile = FixtureBuildCompiler.Profile;
                Assert.Equal(BuildControlOutcome.Created, await store.SaveEnvironmentAsync(runs.Actor, new(environment, profile, true)));
                var intent = new BuildIntent(Guid.NewGuid(), scope, runs.Value.NativeDigest,
                    new("ferrule", "engine", "build", "adapter"), profile,
                    [new(BuildBoundaryKind.PrimaryInput, null, "fixture-bytes"), new(BuildBoundaryKind.PrimaryOutput, null, "fixture-bytes")], environment, Guid.NewGuid(), runs.RunId);
                var nativeId = await runs.Cases.Case.ScalarAsync<Guid>("SELECT native_object_id FROM weft.mapping_revisions LIMIT 1");
                var count = await runs.Cases.Case.ScalarAsync<long>("SELECT native_byte_count FROM weft.mapping_revisions LIMIT 1");
                return new()
                {
                    Runs = runs,
                    Store = store,
                    Connection = connection,
                    Intent = intent,
                    Evidence = snapshot.CompletionDigest!,
                    Native = new(Weft.Domain.PrivateObjectId.Restore(scope, nativeId, Weft.Domain.PrivateObjectClass.NativeArtifact), runs.Value.NativeDigest, count)
                };
            }
            catch
            {
                if (store is not null) await store.DisposeAsync();
                await runs.DisposeAsync();
                throw;
            }
        }
        internal Task<BuildControlOutcome> Approve() => Store.ApproveAsync(Actor, new(Actor.PrincipalId, Intent, Native, Evidence));
        internal Task<StagedBuild?> Stage(byte[] bytes)
        {
            var data = new BuildBytes(bytes, Intent.Profile.MaximumArtifactBytes);
            var artifact = BuildArtifactReference.Restore(Intent, data.Digest, data.Length);
            return Store.StageAsync(Actor, new(new(Actor.PrincipalId, artifact,
                new(Weft.Domain.PrivateObjectId.Restore(Intent.Revision, Intent.BuildId, Weft.Domain.PrivateObjectClass.DerivedArtifact), data.Digest, data.Length)), data));
        }
        public async ValueTask DisposeAsync() { await Store.DisposeAsync(); await Runs.DisposeAsync(); }
    }
    private sealed class NoNative : IPrivateObjectStore
    {
        public Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(Weft.Domain.RevisionScope expectedScope, Weft.Domain.PrivateObjectReference reference, Stream source, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<byte[]> ReadVerifiedAsync(Weft.Domain.RevisionScope expectedScope, Weft.Domain.PrivateObjectReference reference, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class SyntheticNative : IPrivateObjectStore
    {
        public Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(Weft.Domain.RevisionScope expectedScope, Weft.Domain.PrivateObjectReference reference, Stream source, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<byte[]> ReadVerifiedAsync(Weft.Domain.RevisionScope expectedScope, Weft.Domain.PrivateObjectReference reference, CancellationToken cancellationToken = default) =>
            Task.FromResult(PostgresIngestionTestSupport.SyntheticBody.ToArray());
    }
    private sealed class DatabaseFixtureCompiler : IBuildCompiler
    {
        internal int Calls { get; private set; }
        public Task<BuildCapabilitySnapshot> InspectAsync(BuildIntent intent, CancellationToken token = default) => Task.FromResult(new BuildCapabilitySnapshot(intent.Revision,
            intent.NativeDigest, intent.Backend, intent.Profile, intent.Boundaries, Weft.Contracts.Projections.CapabilityStatus.Supported));
        public Task<BuildBytes> BuildAsync(BuildIntent intent, BuildBytes native, CancellationToken token = default)
        {
            Calls++;
            return Task.FromResult(new BuildBytes(Encoding.UTF8.GetBytes("synthetic-build-" + intent.Fingerprint.Value), intent.Profile.MaximumArtifactBytes));
        }
    }
}
