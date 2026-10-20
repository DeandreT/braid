using System.Text.Json;
using Weft.Ai;
using Weft.Application;
using Weft.Application.MappingTools;
using Weft.Domain;

namespace Weft.Tests;

public sealed class MappingToolBoundaryTests
{
    [Theory]
    [InlineData("run_shell", "{}")]
    [InlineData("GET_MAPPING_SUMMARY", "{}")]
    [InlineData("get_mapping_summary", "{\"url\":\"file:///private\"}")]
    [InlineData("get_mapping_summary", "{\"organizationId\":\"11111111-1111-4111-8111-111111111111\"}")]
    [InlineData("get_mapping_summary", "{\"tool\":\"preview\"}")]
    [InlineData("get_mapping_summary", "{\"nested\":{\"deeper\":{}}}")]
    [InlineData("get_rule_details", "{}")]
    [InlineData("get_rule_details", "{\"ruleId\":true}")]
    [InlineData("get_rule_details", "{\"ruleId\":\"\"}")]
    [InlineData("get_rule_details", "{\"ruleId\":\"rule.first\",\"RuleId\":\"other\"}")]
    [InlineData("get_rule_details", "{\"ruleId\":\"rule\\u0007first\"}")]
    [InlineData("search_mapping", "{\"query\":\" \"}")]
    [InlineData("search_mapping", "{\"query\":\"field\",\"limit\":21}")]
    [InlineData("search_mapping", "{\"query\":\"field\",\"limit\":1.0}")]
    [InlineData("search_mapping", "{\"query\":\"field\",\"limit\":1e0}")]
    [InlineData("get_field_lineage", "{\"fieldId\":\"field.in\",\"maxNodes\":0}")]
    [InlineData("get_field_lineage", "{\"fieldId\":\"field.in\",\"limit\":1}")]
    [InlineData("get_validation_issues", "{\"offset\":-1}")]
    [InlineData("get_validation_issues", "{\"limit\":\"1\"}")]
    [InlineData("get_validation_issues", "{\"derivationId\":\"88888888-8888-4888-8888-888888888888\"}")]
    public async Task Injected_or_non_closed_calls_fail_without_report_reads(string tool, string args)
    {
        var fixture = new MappingToolFixture();
        var service = fixture.Service();
        var call = MappingToolFixture.Call(tool, args);
        Assert.False(await service.ValidateAsync(fixture.Context(), call, CancellationToken.None));
        Assert.Equal(0, fixture.AuthorityCalls);
        var result = await service.InvokeAsync(fixture.Context(), call);
        Assert.Equal(MappingToolOutcome.InvalidCall, result.Outcome);
        Assert.Empty(fixture.Reads);
        Assert.Equal(1, fixture.AuthorityCalls);
        Assert.Null(result.Data);
        Assert.Empty(result.Citations);
        Assert.Equal(0, result.RetrievedNodes);
        Assert.DoesNotContain("file:///private", result.Result.Json, StringComparison.Ordinal);
        Assert.DoesNotContain(MappingToolFixture.PrivateText, result.Result.Json, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(result.Result.Json);
        Assert.Equal("invalidCall", json.RootElement.GetProperty("outcome").GetString());
    }

    [Theory]
    [InlineData("{\"ruleId\":\"rule.first\",\"ruleId\":\"rule.first\"}")]
    [InlineData("{\"ruleId\":\"rule.first\",\"\\u0072uleId\":\"rule.first\"}")]
    [InlineData("{\"ruleId\":\"\\ud800\"}")]
    [InlineData("{\"ruleId\":\"\\udc00\"}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}{}")]
    public void Ambiguous_or_malformed_argument_json_cannot_become_a_provider_intent(string args) =>
        Assert.Throws<ArgumentException>(() => MappingToolFixture.Call("get_rule_details", args));

    [Fact]
    public async Task Excess_input_and_output_work_depth_and_citation_limits_are_categorical()
    {
        var fixture = new MappingToolFixture();
        var input = await fixture.InvokeAsync("search_mapping", "{\"query\":\"" + new string('x', 4_097) + "\"}");
        Assert.Equal(MappingToolOutcome.InvalidCall, input.Outcome);
        var work = await fixture.InvokeAsync(fixture.Service(new(workUnits: 1)), "get_field_lineage", """{"fieldId":"field.in"}""");
        Assert.Equal(MappingToolOutcome.ResourceLimit, work.Outcome);
        var big = new MappingToolFixture(largeSummary: new string('x', 2_000));
        var output = await big.InvokeAsync(big.Service(new(outputBytes: 1_024)), "get_rule_details", """{"ruleId":"rule.first"}""");
        Assert.Equal(MappingToolOutcome.ResourceLimit, output.Outcome);
        Assert.InRange(output.Result.ByteCount, 1, 1_024);
        Assert.Null(output.Data);
        Assert.Empty(output.Citations);
        Assert.DoesNotContain(MappingToolFixture.PrivateText, output.Result.Json, StringComparison.Ordinal);
        var deep = new MappingToolFixture(deepDetails: true);
        var depth = await deep.InvokeAsync("get_rule_details", """{"ruleId":"rule.first"}""");
        Assert.Equal(MappingToolOutcome.ResourceLimit, depth.Outcome);
        var tooManyCitations = await fixture.InvokeAsync(fixture.Service(new(citationCount: 1)), "search_mapping", """{"query":"field"}""");
        Assert.Equal(MappingToolOutcome.ResourceLimit, tooManyCitations.Outcome);
    }

    [Fact]
    public async Task Remaining_turn_result_and_node_budgets_refuse_whole_results_instead_of_cropping()
    {
        var fixture = new MappingToolFixture();
        var service = fixture.Service();
        var complete = await fixture.InvokeAsync(service, "get_rule_details", """{"ruleId":"rule.first"}""");
        Assert.Equal(MappingToolOutcome.Available, complete.Outcome);
        var exactBytes = await fixture.InvokeAsync(service, "get_rule_details", """{"ruleId":"rule.first"}""",
            fixture.Context(remainingBytes: complete.Result.ByteCount));
        Assert.Equal(complete.Result.Json, exactBytes.Result.Json);
        var bytes = await fixture.InvokeAsync(service, "get_rule_details", """{"ruleId":"rule.first"}""",
            fixture.Context(remainingBytes: complete.Result.ByteCount - 1));
        Assert.Equal(MappingToolOutcome.ResourceLimit, bytes.Outcome);
        Assert.Null(bytes.Data);
        var exactNodes = await fixture.InvokeAsync(service, "get_rule_details", """{"ruleId":"rule.first"}""",
            fixture.Context(remainingNodes: complete.RetrievedNodes));
        Assert.Equal(MappingToolOutcome.Available, exactNodes.Outcome);
        var nodes = await fixture.InvokeAsync(service, "get_rule_details", """{"ruleId":"rule.first"}""",
            fixture.Context(remainingNodes: complete.RetrievedNodes - 1));
        Assert.Equal(MappingToolOutcome.ResourceLimit, nodes.Outcome);
        Assert.Equal(0, nodes.RetrievedNodes);
        Assert.Empty(nodes.Citations);
        var none = await fixture.InvokeAsync(service, "get_rule_details", """{"ruleId":"absent"}""", fixture.Context(remainingNodes: 0));
        Assert.Equal(MappingToolOutcome.Unknown, none.Outcome);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Every_component_of_scope_is_authoritative(int component)
    {
        var fixture = new MappingToolFixture();
        var original = MappingToolFixture.Scope;
        var other = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
        var scope = new RevisionScope(component == 0 ? other : original.OrganizationId,
            component == 1 ? other : original.ProjectId, component == 2 ? other : original.MappingId,
            component == 3 ? other : original.RevisionId);
        var service = fixture.Service();
        var conversation = fixture.ConversationWith(scope);
        var result = await fixture.InvokeAsync(service, "get_mapping_summary", "{}", fixture.Context(conversation: conversation));
        Assert.Equal(MappingToolOutcome.NotAuthorizedOrNotFound, result.Outcome);
        Assert.Empty(fixture.Reads);
        var targets = new[] { new ChatCitationTarget(ChatCitationKind.ProjectionReport) };
        Assert.Equal(MappingCitationOutcome.NotAuthorizedOrNotFound,
            (await service.ValidateCitationsAsync(MappingToolFixture.Actor, conversation, targets)).Outcome);
        Assert.Empty(fixture.Reads);
    }

    [Fact]
    public async Task Another_principal_cannot_use_a_creator_private_conversation()
    {
        var fixture = new MappingToolFixture();
        var service = fixture.Service();
        var other = new ActorContext(new PrincipalId(Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd")));
        var call = MappingToolFixture.Call("get_mapping_summary", "{}");
        Assert.False(await service.ValidateAsync(fixture.Context(actor: other), call, CancellationToken.None));
        var result = await service.InvokeAsync(fixture.Context(actor: other), call);
        Assert.Equal(MappingToolOutcome.NotAuthorizedOrNotFound, result.Outcome);
        // Even when current project authority would admit the reader, the conversation's creator is not borrowed.
        var foreign = fixture.ConversationWith(creator: other.PrincipalId);
        Assert.Equal(MappingToolOutcome.NotAuthorizedOrNotFound,
            (await fixture.InvokeAsync(service, "get_mapping_summary", "{}", fixture.Context(conversation: foreign))).Outcome);
        Assert.Equal(0, fixture.AuthorityCalls);
        Assert.Empty(fixture.Reads);
    }

    [Fact]
    public async Task Revocation_admission_and_role_changes_are_fresh_for_each_call_and_before_delivery()
    {
        var fixture = new MappingToolFixture();
        var service = fixture.Service();
        Assert.Equal(MappingToolOutcome.Available, (await fixture.InvokeAsync(service, "get_mapping_summary", "{}")).Outcome);
        fixture.Role = null;
        Assert.Equal(MappingToolOutcome.NotAuthorizedOrNotFound, (await fixture.InvokeAsync(service, "get_mapping_summary", "{}")).Outcome);
        fixture.Role = ProjectRole.Administrator;
        fixture.Admitted = false;
        Assert.Equal(MappingToolOutcome.NotAuthorizedOrNotFound, (await fixture.InvokeAsync(service, "get_mapping_summary", "{}")).Outcome);
        fixture.Admitted = true;
        fixture.Active = false;
        Assert.Equal(MappingToolOutcome.NotAuthorizedOrNotFound, (await fixture.InvokeAsync(service, "get_mapping_summary", "{}")).Outcome);
        fixture.Active = true;
        fixture.OnRead = _ => fixture.Role = null;
        var revoked = await fixture.InvokeAsync(service, "get_mapping_summary", "{}");
        Assert.Equal(MappingToolOutcome.NotAuthorizedOrNotFound, revoked.Outcome);
        Assert.Null(revoked.Data);
        Assert.Empty(revoked.Citations);
        // A fresh fixture places revocation precisely at post-preparation authority.
        var delivery = new MappingToolFixture();
        delivery.OnAuthority = count => { if (count == 2) delivery.Active = false; };
        var final = await delivery.InvokeAsync("get_mapping_summary", "{}");
        Assert.Equal(MappingToolOutcome.NotAuthorizedOrNotFound, final.Outcome);
        Assert.Null(final.Data);
        Assert.Equal(2, delivery.AuthorityCalls);
        Assert.Single(delivery.Reads);
    }

    [Theory]
    [InlineData(RevisionReportReadOutcome.ObjectUnavailable)]
    [InlineData(RevisionReportReadOutcome.StorageIntegrity)]
    [InlineData(RevisionReportReadOutcome.UnsupportedFormat)]
    [InlineData(RevisionReportReadOutcome.SelectedProjectionUnavailable)]
    public async Task Missing_or_unreadable_reports_stay_explicit_without_derivation(RevisionReportReadOutcome failure)
    {
        var fixture = new MappingToolFixture { ReadFailure = failure };
        var result = await fixture.InvokeAsync("get_mapping_summary", "{}");
        Assert.Equal(MappingToolOutcome.ReportUnavailable, result.Outcome);
        Assert.Null(result.Data);
        Assert.Null(result.Coverage);
        Assert.Single(fixture.Reads);
        var hidden = new MappingToolFixture { Missing = true };
        Assert.Equal(MappingToolOutcome.NotAuthorizedOrNotFound, (await hidden.InvokeAsync("get_validation_issues", "{}")).Outcome);
        Assert.Single(hidden.Reads);
        // The constructor only accepts the two read ports; no Ensure, current-selection, provider, native or payload API exists.
        Assert.Equal(new[] { typeof(IRevisionReportMetadataReader), typeof(IRevisionReportReader), typeof(MappingToolLimits) },
            typeof(MappingToolService).GetConstructors().Single().GetParameters().Select(item => item.ParameterType).ToArray());
    }

    [Fact]
    public async Task A_matching_derivation_uuid_does_not_replace_the_full_report_identity_or_actor()
    {
        var fixture = new MappingToolFixture();
        var metadata = fixture.Projection.Metadata;
        var old = metadata.Key;
        var differentKey = new ProjectionCacheKey(old.Source, old.Profile, old.SchemaVersion, old.AlgorithmVersion, "changed-codec", old.Policy);
        fixture.SubstitutedProjectionMetadata = new(metadata.DerivationId, metadata.CacheEntryId, differentKey, metadata.Generation,
            metadata.OriginalActor, metadata.StartedAtUtc, metadata.FinishedAtUtc, metadata.DerivedObject, metadata.Observation);
        var changed = await fixture.InvokeAsync("get_mapping_summary", "{}");
        Assert.Equal(MappingToolOutcome.ReportUnavailable, changed.Outcome);
        Assert.Null(changed.Data);
        fixture.SubstitutedProjectionMetadata = null;
        fixture.WrongActor = true;
        Assert.Equal(MappingToolOutcome.ReportUnavailable, (await fixture.InvokeAsync("get_mapping_summary", "{}")).Outcome);
        fixture.WrongActor = false;
        var pin = fixture.Conversation.Projection;
        foreach (var forged in new[]
        {
            new ChatReportPin(Guid.Parse("eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee"), pin.DerivationId, pin.KeyFingerprint, pin.ObjectDigest),
            new ChatReportPin(pin.CacheEntryId, pin.DerivationId, Sha256Digest.FromBytes([9]), pin.ObjectDigest),
            new ChatReportPin(pin.CacheEntryId, pin.DerivationId, pin.KeyFingerprint, Sha256Digest.FromBytes([9]))
        })
        {
            var conversation = fixture.ConversationWith(projection: forged);
            Assert.Equal(MappingToolOutcome.ReportUnavailable,
                (await fixture.InvokeAsync(fixture.Service(), "get_mapping_summary", "{}", fixture.Context(conversation: conversation))).Outcome);
        }
    }

    [Fact]
    public async Task A_conversation_pinning_another_attempt_never_reads_a_different_or_current_report()
    {
        var fixture = new MappingToolFixture();
        var pin = fixture.Conversation.Projection;
        var historical = fixture.ConversationWith(projection: new(pin.CacheEntryId, Guid.Parse("f0f0f0f0-f0f0-40f0-80f0-f0f0f0f0f0f0"),
            pin.KeyFingerprint, pin.ObjectDigest));
        var result = await fixture.InvokeAsync(fixture.Service(), "get_mapping_summary", "{}", fixture.Context(conversation: historical));
        Assert.Equal(MappingToolOutcome.NotAuthorizedOrNotFound, result.Outcome);
        Assert.Equal(Guid.Parse("f0f0f0f0-f0f0-40f0-80f0-f0f0f0f0f0f0"), Assert.Single(fixture.Reads).Id);
        // A report pinned by a historical conversation stays readable even though another principal derived it.
        Assert.NotEqual(MappingToolFixture.Actor.PrincipalId, fixture.Projection.Metadata.OriginalActor);
        Assert.Equal(MappingToolOutcome.Available, (await fixture.InvokeAsync("get_mapping_summary", "{}")).Outcome);
    }

    [Fact]
    public async Task Caller_cancellation_and_categorical_dependency_faults_do_not_leak_private_text()
    {
        var fixture = new MappingToolFixture();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.InvokeAsync(fixture.Service(), "get_mapping_summary", "{}", cancellationToken: cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Service().ExecuteAsync(fixture.Context(), MappingToolFixture.Call("get_mapping_summary", "{}"), cancelled.Token));
        Assert.Equal(0, fixture.AuthorityCalls);
        using var during = new CancellationTokenSource();
        fixture.OnRead = token => { Assert.True(token.CanBeCanceled); during.Cancel(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.InvokeAsync(fixture.Service(), "get_mapping_summary", "{}", cancellationToken: during.Token));
        fixture.OnRead = _ => throw new InvalidOperationException(MappingToolFixture.PrivateText);
        var failure = await fixture.InvokeAsync("get_mapping_summary", "{}");
        Assert.Equal(MappingToolOutcome.DependencyFailure, failure.Outcome);
        Assert.DoesNotContain(MappingToolFixture.PrivateText, failure.Result.Json, StringComparison.Ordinal);
        Assert.DoesNotContain(MappingToolFixture.PrivateText, failure.ToString(), StringComparison.Ordinal);
        Assert.Equal("MappingToolReply:DependencyFailure", failure.ToString());
        Assert.Equal(nameof(AiJsonObject), failure.Result.ToString());
    }
}
