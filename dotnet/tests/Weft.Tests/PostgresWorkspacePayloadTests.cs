using System.Text.Json;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Weft.Ai;
using Weft.Api.Chat;
using Weft.Api.Chat.Workspace;
using Weft.Application;
using Weft.Application.Regression;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

/// <summary>Payload tools over the actual restricted grant reader, catalog and regression case store.</summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresWorkspacePayloadTests(PostgresFixture fixture)
{
    [RegressionPostgresTheory]
    [InlineData("x12", "application/edi-x12")]
    [InlineData("edifact", "application/edifact")]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task Preview_edi_outputs_need_real_current_grants_and_preserve_strict_text_checks(string family, string media)
    {
        await using var test = await PreviewRunTestSupport.CreateAsync(fixture, DocumentPreviewTestData.Native(family));
        var content = DocumentPreviewTestData.Input(family);
        var submitted = await test.Service().SubmitAsync(test.Alice, new(test.Revision, Guid.NewGuid(), PreviewTargetSelection.All,
            new(null, content), [], [])
        { Profile = PreviewExecutionProfile.Document });
        var run = submitted.RunId!.Value;
        var claim = (await test.Store.ClaimAsync(test.Revision.OrganizationId, run, Guid.NewGuid(), TimeSpan.FromSeconds(90))).Claim!;
        Assert.True(await test.Store.CompleteAsync(claim, new(PreviewRunOutcome.Succeeded, null,
            new(true, "engine", "image", "adapter", DocumentPreviewPolicy.Profile), 1,
            [new("primary-output", "Interchange", false, media, 1, content),
             new("named-output:0", "binary", false, media, 1, [0xff, 0xfe]),
             new("named-output:1", "control", false, media, 1, Encoding.UTF8.GetBytes(new string('X', WorkspaceChatGrounding.PayloadBytes + 1) + "\u0007"))])));
        await using var payloadDatabase = new PostgresPayloadPermissionDatabase(test.Case.PayloadPermissionConnectionString);
        var grants = new PostgresProjectPayloadPermissionReader(payloadDatabase);
        var reports = new MappingToolFixture();
        await using var services = new ServiceCollection()
            .AddSingleton<ICatalogReader>(test.Catalog.Reader).AddSingleton<ICatalogResourceLocator>(test.Catalog.Reader)
            .AddSingleton(test.Service()).AddSingleton<IRevisionReportReader>(reports).AddSingleton<IRevisionReportMetadataReader>(reports)
            .AddSingleton<IProjectPayloadPermissionReader>(grants).AddSingleton<IProjectPayloadGrantPresence>(grants).BuildServiceProvider();
        var service = new WorkspaceToolService(services, new ChatApiOptions(), new WorkspaceToolFeatures(true, false, false, false));
        var conversation = ChatConversationIntent.ForWorkspace(ChatConversationKey.ForWorkspace(test.Revision.OrganizationId, test.Alice.PrincipalId, Guid.NewGuid()),
            null, new DateTimeOffset(DateTimeOffset.UtcNow.Ticks / 10 * 10, TimeSpan.Zero).AddHours(1));
        var context = new ChatReadOnlyToolContext(test.Alice, conversation, WorkspaceToolFixture.Turn(service, payloads: true), Guid.NewGuid(), 1, 32_768, 512);
        async Task<WorkspaceToolFixture.ToolReply> Read(int ordinal) => new(await service.ExecuteAsync(context,
            AiToolIntent.Create("call-1", "get_preview_output", AiJsonObject.CopyFrom(JsonSerializer.Serialize(new { runId = run.ToString("D"), ordinal }))), CancellationToken.None));
        Assert.Equal("notPermitted", (await Read(0)).Outcome);
        await test.Case.ExecuteAsync("INSERT INTO weft.project_payload_read_grants(organization_id,project_id,principal_id,active,version) VALUES($1,$2,$3,true,1)",
            test.Revision.OrganizationId, test.Revision.ProjectId, test.Alice.PrincipalId.Value);
        var shared = await Read(0);
        Assert.Equal("available", shared.Outcome);
        Assert.Equal(media, shared.Data.GetProperty("mediaType").GetString());
        Assert.Equal(Encoding.UTF8.GetString(content), shared.Data.GetProperty("text").GetString());
        Assert.Equal(content.Length, shared.Result.DataShared!.ByteCount);
        foreach (var ordinal in new[] { 1, 2 })
        {
            var invalid = await Read(ordinal);
            Assert.Equal("unsupportedContent", invalid.Outcome);
            Assert.Null(invalid.Result.DataShared);
        }
        await test.Case.ExecuteAsync("UPDATE weft.project_payload_read_grants SET active=false,version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3",
            test.Revision.OrganizationId, test.Revision.ProjectId, test.Alice.PrincipalId.Value);
        var denied = await Read(0);
        Assert.Equal("notPermitted", denied.Outcome);
        Assert.Null(denied.Result.DataShared);
    }

    [PostgresFact]
    public async Task Saved_case_data_is_shared_only_while_the_restricted_reader_observes_an_active_grant()
    {
        await using var test = await RegressionCasePostgresTestSupport.CreateAsync(fixture);
        Assert.Equal(RegressionCaseSaveOutcome.Created, (await test.Writer.SaveAsync(test.Actor, test.Version(1))).Outcome);
        await using var catalogDatabase = new PostgresCatalogDatabase(test.Case.CatalogConnectionString);
        var catalog = new PostgresCatalogReader(catalogDatabase);
        await using var payloadDatabase = new PostgresPayloadPermissionDatabase(test.Case.PayloadPermissionConnectionString);
        var grants = new PostgresProjectPayloadPermissionReader(payloadDatabase);
        var reports = new MappingToolFixture();
        await using var services = new ServiceCollection()
            .AddSingleton<ICatalogReader>(catalog).AddSingleton<ICatalogResourceLocator>(catalog)
            .AddSingleton<IRegressionCaseReader>(test.Reader)
            .AddSingleton<IRegressionCaseMetadataReader>(test.Reader)
            .AddSingleton<IRevisionReportReader>(reports).AddSingleton<IRevisionReportMetadataReader>(reports)
            .AddSingleton<IProjectPayloadPermissionReader>(grants).AddSingleton<IProjectPayloadGrantPresence>(grants)
            .BuildServiceProvider();
        var service = new WorkspaceToolService(services, new ChatApiOptions(), new WorkspaceToolFeatures(false, true, false, false));
        var organization = test.Seed.OrganizationA;
        var profile = new ChatModelProfile("Fixture model", "fixture", AiProfileSpec.Create(AiProfileKey.Create("payload-fixture", 1),
            AiDialect.ChatCompletionsSseV1, "fixture-model", new Uri("http://127.0.0.1:9/v1/chat/completions"), true, AiAuthentication.Bearer,
            "fixture-credential", AiCapability.Text | AiCapability.FunctionTools, "mapping-metadata", 1, true, false, payloadsAllowed: true));
        var conversation = ChatConversationIntent.ForWorkspace(ChatConversationKey.ForWorkspace(organization, test.Actor.PrincipalId, Guid.NewGuid()),
            null, new DateTimeOffset(DateTimeOffset.UtcNow.Ticks / 10 * 10, TimeSpan.Zero).AddHours(1));
        var context = new ChatReadOnlyToolContext(test.Actor, conversation, WorkspaceToolFixture.Turn(service, payloads: true), Guid.NewGuid(), 1, 32_768, 512);
        var policy = new ChatProfilePolicy(new ChatApiOptions([profile], TimeSpan.FromDays(1), TimeSpan.FromSeconds(60), 128), catalog, service.AllowsAsync);
        var policyTurn = new ChatTurnIntent(context.Turn.TurnId, context.Turn.Envelope, profile.Pin, context.Turn.Budget, context.Turn.DeadlineUtc);
        async Task<WorkspaceToolFixture.ToolReply> ReadAsync(string part, long version = 1) => new(await service.ExecuteAsync(context,
            AiToolIntent.Create("call-1", "get_regression_case_data", AiJsonObject.CopyFrom(JsonSerializer.Serialize(new
            {
                mappingId = test.Mapping.MappingId.ToString("D"),
                caseId = test.CaseId.ToString("D"),
                version,
                part,
                ordinal = 0
            }))), CancellationToken.None));

        // Without any grant the payload tools are not offered and a payload turn's call shares nothing.
        Assert.Equal(service.Grounding, await service.GroundingForAsync(test.Actor, organization, profile, CancellationToken.None));
        Assert.False(await policy.AllowsAsync(test.Actor, conversation, policyTurn, profile.Spec, CancellationToken.None));
        Assert.Equal("notPermitted", (await ReadAsync("input")).Outcome);

        // A grant in another organization grants nothing here.
        await test.Case.ExecuteAsync("INSERT INTO weft.project_payload_read_grants(organization_id,project_id,principal_id,active,version) VALUES($1,$2,$3,true,1)",
            test.Seed.OrganizationB, test.Seed.ProjectB, test.Seed.Alice);
        Assert.Equal(service.Grounding, await service.GroundingForAsync(test.Actor, organization, profile, CancellationToken.None));
        Assert.True(await grants.AnyAsync(test.Actor, test.Seed.OrganizationB));
        Assert.Equal("notPermitted", (await ReadAsync("input")).Outcome);

        await test.Case.ExecuteAsync("INSERT INTO weft.project_payload_read_grants(organization_id,project_id,principal_id,active,version) VALUES($1,$2,$3,true,1)",
            organization, test.Seed.ProjectA, test.Seed.Alice);
        Assert.Equal(service.PayloadGrounding, await service.GroundingForAsync(test.Actor, organization, profile, CancellationToken.None));
        Assert.True(await policy.AllowsAsync(test.Actor, conversation, policyTurn, profile.Spec, CancellationToken.None));
        var input = await ReadAsync("input");
        Assert.Equal("available", input.Outcome);
        Assert.Equal("private-input", input.Data.GetProperty("text").GetString());
        Assert.Equal(new ChatPayloadDisclosureFact(test.Seed.ProjectA, ChatPayloadSource.RegressionCase, test.CaseId, 1, "private-input".Length),
            input.Result.DataShared);
        Assert.Equal(new[] { ChatWorkspaceCitationFact.RegressionCase(test.Mapping, test.CaseId, 1) }, input.Result.WorkspaceCitations);
        var expected = await ReadAsync("expectedOutput");
        Assert.Equal("expected-1", expected.Data.GetProperty("text").GetString());
        Assert.Equal("notFound", (await ReadAsync("input", version: 2)).Outcome);

        // Revoking the grant takes effect on the next call and stops the payload manifest for new turns.
        await test.Case.ExecuteAsync("UPDATE weft.project_payload_read_grants SET active=false,version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3",
            organization, test.Seed.ProjectA, test.Seed.Alice);
        var revoked = await ReadAsync("input");
        Assert.Equal("notPermitted", revoked.Outcome);
        Assert.Null(revoked.Result.DataShared);
        Assert.DoesNotContain("private-input", revoked.Json, StringComparison.Ordinal);
        Assert.Equal(service.Grounding, await service.GroundingForAsync(test.Actor, organization, profile, CancellationToken.None));
        Assert.False(await policy.AllowsAsync(test.Actor, conversation, policyTurn, profile.Spec, CancellationToken.None));

        // An active grant without current project membership is no authority either.
        await test.Case.ExecuteAsync("UPDATE weft.project_payload_read_grants SET active=true,version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3",
            organization, test.Seed.ProjectA, test.Seed.Alice);
        await test.Case.ExecuteAsync("UPDATE weft.project_memberships SET active=false,version=version+1 WHERE principal_id=$1 AND project_id=$2",
            test.Seed.Alice, test.Seed.ProjectA);
        Assert.False(await grants.AnyAsync(test.Actor, organization));
        Assert.Equal("notFound", (await ReadAsync("input")).Outcome);
    }

    [PostgresFact]
    public async Task A_completed_regression_runs_actual_output_is_shared_only_under_the_grant()
    {
        await using var test = await RegressionRunPostgresTestSupport.CreateAsync(fixture);
        Assert.Equal(RegressionRunSubmitOutcome.Created, await test.SubmitAsync());
        Assert.True(await test.Writer.CompleteAsync(await test.ClaimAsync(), test.Success()));
        var cases = test.Cases;
        await using var catalogDatabase = new PostgresCatalogDatabase(cases.Case.CatalogConnectionString);
        var catalog = new PostgresCatalogReader(catalogDatabase);
        await using var payloadDatabase = new PostgresPayloadPermissionDatabase(cases.Case.PayloadPermissionConnectionString);
        var grants = new PostgresProjectPayloadPermissionReader(payloadDatabase);
        var reports = new MappingToolFixture();
        await using var services = new ServiceCollection()
            .AddSingleton<ICatalogReader>(catalog).AddSingleton<ICatalogResourceLocator>(catalog)
            .AddSingleton<IRegressionCaseReader>(cases.Reader).AddSingleton<IRegressionRunReader>(test.Reader)
            .AddSingleton<IRegressionCaseMetadataReader>(cases.Reader)
            .AddSingleton<IRegressionRunMetadataReader>(test.Reader)
            .AddSingleton(new RegressionRunReadService(test.Reader, cases.Reader))
            .AddSingleton<IRevisionReportReader>(reports).AddSingleton<IRevisionReportMetadataReader>(reports)
            .AddSingleton<IProjectPayloadPermissionReader>(grants).AddSingleton<IProjectPayloadGrantPresence>(grants)
            .BuildServiceProvider();
        var service = new WorkspaceToolService(services, new ChatApiOptions(), new WorkspaceToolFeatures(false, true, false, false));
        var conversation = ChatConversationIntent.ForWorkspace(ChatConversationKey.ForWorkspace(test.Organization, test.Actor.PrincipalId, Guid.NewGuid()),
            null, new DateTimeOffset(DateTimeOffset.UtcNow.Ticks / 10 * 10, TimeSpan.Zero).AddHours(1));
        var context = new ChatReadOnlyToolContext(test.Actor, conversation, WorkspaceToolFixture.Turn(service, payloads: true), Guid.NewGuid(), 1, 32_768, 512);
        async Task<WorkspaceToolFixture.ToolReply> CallAsync(string name, string arguments) => new(await service.ExecuteAsync(context,
            AiToolIntent.Create("call-1", name, AiJsonObject.CopyFrom(arguments)), CancellationToken.None));
        var output = "{\"runId\":\"" + test.RunId.ToString("D") + "\",\"ordinal\":0}";

        // Metadata and citation reads still work when this login cannot call the
        // private completion reader at all. A denied payload call must not need it either.
        await cases.Case.ExecuteAsync("REVOKE EXECUTE ON FUNCTION weft.read_regression_run(uuid,uuid) FROM weft_run_reader");
        try
        {
            var run = await CallAsync("get_run", "{\"kind\":\"regressionRun\",\"runId\":\"" + test.RunId.ToString("D") + "\"}");
            Assert.Equal("available", run.Outcome);
            Assert.Equal("completed", run.Data.GetProperty("state").GetString());
            Assert.False(run.Data.TryGetProperty("comparison", out _));
            Assert.Contains("completion includes sample data", run.Data.GetProperty("completionDetails").GetString(), StringComparison.Ordinal);
            Assert.DoesNotContain("expected-1", run.Json, StringComparison.Ordinal);
            Assert.Single(await service.VerifyAsync(test.Actor, conversation,
                [ChatWorkspaceCitationFact.RegressionRun(cases.Mapping, test.RunId)], CancellationToken.None));
            var metadata = Assert.IsType<RegressionRunSummary>(await test.Reader.ReadMetadataAsync(test.Actor, test.Organization, test.RunId));
            Assert.Equal(test.RunId, metadata.RunId);
            Assert.Equal(RegressionRunState.Completed, metadata.State);
            Assert.Null(await test.Reader.ReadMetadataAsync(new ActorContext(new(cases.Seed.Bob)), test.Organization, test.RunId));
            Assert.Null(await test.Reader.ReadMetadataAsync(test.Actor, Guid.NewGuid(), test.RunId));
            Assert.Equal("notPermitted", (await CallAsync("get_regression_run_output", output)).Outcome);
        }
        finally
        {
            await cases.Case.ExecuteAsync("GRANT EXECUTE ON FUNCTION weft.read_regression_run(uuid,uuid) TO weft_run_reader");
        }

        await cases.Case.ExecuteAsync("INSERT INTO weft.project_payload_read_grants(organization_id,project_id,principal_id,active,version) VALUES($1,$2,$3,true,1)",
            test.Organization, cases.Seed.ProjectA, cases.Seed.Alice);
        var shared = await CallAsync("get_regression_run_output", output);
        Assert.Equal("available", shared.Outcome);
        Assert.Equal("expected-1", shared.Data.GetProperty("text").GetString());
        Assert.Equal(new ChatPayloadDisclosureFact(cases.Seed.ProjectA, ChatPayloadSource.RegressionRunOutput, test.RunId, null, "expected-1".Length),
            shared.Result.DataShared);
        Assert.Equal(new[] { ChatWorkspaceCitationFact.RegressionRun(cases.Mapping, test.RunId) }, shared.Result.WorkspaceCitations);
        Assert.Equal("notFound", (await CallAsync("get_regression_run_output", "{\"runId\":\"" + test.RunId.ToString("D") + "\",\"ordinal\":1}")).Outcome);
        await cases.Case.ExecuteAsync("UPDATE weft.project_memberships SET active=false,version=version+1 WHERE principal_id=$1 AND project_id=$2",
            cases.Seed.Alice, cases.Seed.ProjectA);
        Assert.Null(await test.Reader.ReadMetadataAsync(test.Actor, test.Organization, test.RunId));
    }
}
