using Weft.Application;
using Weft.Application.MappingTools;
using Weft.Domain;

namespace Weft.Tests;

public sealed class MappingCitationTests
{
    [Fact]
    public async Task Valid_targets_become_exact_report_bound_persistable_citations()
    {
        var fixture = new MappingToolFixture();
        var service = fixture.Service();
        var targets = new[]
        {
            new ChatCitationTarget(ChatCitationKind.Boundary, "out"), new ChatCitationTarget(ChatCitationKind.Field, "field.in"),
            new ChatCitationTarget(ChatCitationKind.Parameter, "parameter"), new ChatCitationTarget(ChatCitationKind.Rule, "rule.first"),
            new ChatCitationTarget(ChatCitationKind.Control, "control.filter"), new ChatCitationTarget(ChatCitationKind.ProjectionReport),
            new ChatCitationTarget(ChatCitationKind.ValidationReport), new ChatCitationTarget(ChatCitationKind.ValidationDiagnostic, diagnosticOrdinal: 1)
        };
        var check = await service.ValidateCitationsAsync(MappingToolFixture.Actor, fixture.Conversation, targets);
        Assert.Equal(MappingCitationOutcome.Valid, check.Outcome);
        Assert.Equal(targets, check.Citations.Select(citation => citation.Facts!.Target));
        Assert.All(check.Citations, citation => Assert.Equal(MappingToolFixture.Scope, citation.Facts!.Revision));
        Assert.All(check.Citations.Take(6), citation => Assert.Equal(fixture.Conversation.PinnedProjection, citation.Facts!.Report));
        Assert.All(check.Citations.Skip(6), citation => Assert.Equal(fixture.Conversation.PinnedValidation, citation.Facts!.Report));
        // Each pinned report is read once per check; authority is fresh before reading and before delivery.
        Assert.Equal(new[] { RevisionReportOperation.Projection, RevisionReportOperation.Validation, RevisionReportOperation.Projection },
            fixture.Reads.Select(read => read.Operation));
        Assert.Equal(2, fixture.AuthorityCalls);
        _ = new ChatCitationEventData(Guid.Parse("13131313-1313-4313-8313-131313131313"), check.Citations[0]);
        Assert.Equal("MappingCitationCheck:Valid", check.ToString());
    }

    [Fact]
    public async Task Forged_kind_identifier_and_diagnostic_ordinal_are_rejected_as_a_whole()
    {
        var fixture = new MappingToolFixture();
        var service = fixture.Service();
        var valid = new ChatCitationTarget(ChatCitationKind.Rule, "rule.first");
        foreach (var forged in new[]
        {
            new ChatCitationTarget(ChatCitationKind.Field, "rule.first"),
            new ChatCitationTarget(ChatCitationKind.Rule, "absent"),
            new ChatCitationTarget(ChatCitationKind.Rule, "RULE.FIRST"),
            new ChatCitationTarget(ChatCitationKind.Control, "rule.first"),
            new ChatCitationTarget(ChatCitationKind.Boundary, "field.in"),
            new ChatCitationTarget(ChatCitationKind.ValidationDiagnostic, diagnosticOrdinal: 2),
            new ChatCitationTarget(ChatCitationKind.ProjectionDiagnostic, diagnosticOrdinal: 0)
        })
        {
            var check = await service.ValidateCitationsAsync(MappingToolFixture.Actor, fixture.Conversation, new[] { valid, forged });
            Assert.Equal(MappingCitationOutcome.Invalid, check.Outcome);
            Assert.Empty(check.Citations);
        }
        // Same code/message does not create an identity; only the exact report and emitted ordinal pass.
        Assert.Equal(MappingCitationOutcome.Valid, (await service.ValidateCitationsAsync(MappingToolFixture.Actor, fixture.Conversation,
            new[] { new ChatCitationTarget(ChatCitationKind.ValidationDiagnostic, diagnosticOrdinal: 0) })).Outcome);
    }

    [Fact]
    public async Task Filtering_omits_only_unverifiable_targets_and_keeps_candidate_order()
    {
        var fixture = new MappingToolFixture();
        var service = fixture.Service();
        var targets = new[]
        {
            new ChatCitationTarget(ChatCitationKind.Rule, "absent"), new ChatCitationTarget(ChatCitationKind.ValidationDiagnostic, diagnosticOrdinal: 1),
            new ChatCitationTarget(ChatCitationKind.Field, "rule.first"), new ChatCitationTarget(ChatCitationKind.Rule, "rule.first"),
            new ChatCitationTarget(ChatCitationKind.ValidationDiagnostic, diagnosticOrdinal: 2), new ChatCitationTarget(ChatCitationKind.ProjectionReport)
        };
        var check = await service.FilterCitationsAsync(MappingToolFixture.Actor, fixture.Conversation, targets);
        Assert.Equal(MappingCitationOutcome.Valid, check.Outcome);
        Assert.Equal(new[] { targets[1], targets[3], targets[5] }, check.Citations.Select(citation => citation.Facts!.Target));
        Assert.Equal(2, fixture.AuthorityCalls);
        // The port form returns the same verified subset and nothing for an unauthorized reader.
        IChatCitationPort port = service;
        Assert.Equal(check.Citations.Select(citation => citation.Facts!),
            (await port.VerifyAsync(MappingToolFixture.Actor, fixture.Conversation, targets, default)).Select(citation => citation.Facts!));
        Assert.Empty(await port.VerifyAsync(MappingToolFixture.Actor, fixture.Conversation, [], default));
        Assert.Equal(MappingCitationOutcome.Invalid, (await service.FilterCitationsAsync(MappingToolFixture.Actor, fixture.Conversation,
            Enumerable.Repeat(targets[5], 65).ToArray())).Outcome);
        Assert.Equal(MappingCitationOutcome.Invalid, (await service.FilterCitationsAsync(MappingToolFixture.Actor, fixture.Conversation,
            new ChatCitationTarget[] { targets[5], null! })).Outcome);
        fixture.Role = null;
        Assert.Empty(await port.VerifyAsync(MappingToolFixture.Actor, fixture.Conversation, targets, default));
        var pin = fixture.Conversation.PinnedValidation;
        var forged = fixture.ConversationWith(validation: new(pin.CacheEntryId, pin.DerivationId, pin.KeyFingerprint, Sha256Digest.FromBytes([7])));
        fixture.Role = ProjectRole.Viewer;
        Assert.Equal(MappingCitationOutcome.ReportUnavailable, (await service.FilterCitationsAsync(MappingToolFixture.Actor, forged,
            new[] { new ChatCitationTarget(ChatCitationKind.ValidationReport) })).Outcome);
    }

    [Fact]
    public async Task Forged_pins_and_counts_fail_closed_and_delivery_requires_fresh_authority()
    {
        var fixture = new MappingToolFixture();
        var service = fixture.Service();
        var report = new ChatCitationTarget(ChatCitationKind.ProjectionReport);
        var pin = fixture.Conversation.PinnedProjection;
        var forged = fixture.ConversationWith(projection: new(pin.CacheEntryId, pin.DerivationId, pin.KeyFingerprint, Sha256Digest.FromBytes([7])));
        Assert.Equal(MappingCitationOutcome.ReportUnavailable,
            (await service.ValidateCitationsAsync(MappingToolFixture.Actor, forged, new[] { report })).Outcome);
        var excessive = Enumerable.Repeat(report, 65).ToArray();
        Assert.Equal(MappingCitationOutcome.Invalid,
            (await service.ValidateCitationsAsync(MappingToolFixture.Actor, fixture.Conversation, excessive)).Outcome);
        Assert.Equal(MappingCitationOutcome.Invalid,
            (await service.ValidateCitationsAsync(MappingToolFixture.Actor, fixture.Conversation, new ChatCitationTarget[] { null! })).Outcome);
        Assert.Equal(MappingCitationOutcome.Invalid,
            (await service.ValidateCitationsAsync(MappingToolFixture.Actor, fixture.Conversation, null!)).Outcome);
        fixture.Role = null;
        var before = fixture.Reads.Count;
        Assert.Equal(MappingCitationOutcome.NotAuthorizedOrNotFound,
            (await service.ValidateCitationsAsync(MappingToolFixture.Actor, fixture.Conversation, new[] { report })).Outcome);
        Assert.Equal(before, fixture.Reads.Count);
        var delivery = new MappingToolFixture();
        delivery.OnAuthority = count => { if (count == 2) delivery.Active = false; };
        var revoked = await delivery.Service().ValidateCitationsAsync(MappingToolFixture.Actor, delivery.Conversation, new[] { report });
        Assert.Equal(MappingCitationOutcome.NotAuthorizedOrNotFound, revoked.Outcome);
        Assert.Empty(revoked.Citations);
        Assert.Equal(2, delivery.AuthorityCalls);
    }

    [Fact]
    public async Task Foreign_actor_or_missing_report_does_not_gain_a_citation_grant()
    {
        var fixture = new MappingToolFixture();
        var service = fixture.Service();
        var targets = new[] { new ChatCitationTarget(ChatCitationKind.ValidationDiagnostic, diagnosticOrdinal: 0) };
        var actor = new ActorContext(new PrincipalId(Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd")));
        Assert.Equal(MappingCitationOutcome.NotAuthorizedOrNotFound,
            (await service.ValidateCitationsAsync(actor, fixture.Conversation, targets)).Outcome);
        Assert.Equal(0, fixture.AuthorityCalls);
        fixture.ReadFailure = RevisionReportReadOutcome.ObjectUnavailable;
        Assert.Equal(MappingCitationOutcome.ReportUnavailable,
            (await service.ValidateCitationsAsync(MappingToolFixture.Actor, fixture.Conversation, targets)).Outcome);
    }

    [Fact]
    public async Task A_valid_check_owns_its_snapshot_not_the_callers_mutable_list()
    {
        var fixture = new MappingToolFixture();
        var service = fixture.Service();
        var reply = await fixture.InvokeAsync(service, "get_rule_details", """{"ruleId":"rule.first"}""");
        var mutable = reply.Citations.ToList();
        var check = await service.ValidateCitationsAsync(MappingToolFixture.Actor, fixture.Conversation, mutable);
        Assert.Equal(MappingCitationOutcome.Valid, check.Outcome);
        mutable[1] = new ChatCitationTarget(ChatCitationKind.Rule, "forged");
        Assert.Equal(new ChatCitationTarget(ChatCitationKind.Rule, "rule.first"), check.Citations[1].Facts!.Target);
        Assert.NotSame(mutable, check.Citations);
        var invalid = await service.ValidateCitationsAsync(MappingToolFixture.Actor, fixture.Conversation, mutable);
        Assert.Equal(MappingCitationOutcome.Invalid, invalid.Outcome);
        Assert.Empty(invalid.Citations);
    }
}

public sealed class MappingToolCapacityTests
{
    [Fact]
    public async Task Local_capacity_and_cancellation_are_bounded_without_starting_extra_reads()
    {
        var fixture = new MappingToolFixture();
        var gate = new HeldReportRead(fixture);
        var service = new MappingToolService(fixture, gate);
        using var cancellation = new CancellationTokenSource();
        var first = fixture.InvokeAsync(service, "get_mapping_summary", "{}", cancellationToken: cancellation.Token);
        var second = fixture.InvokeAsync(service, "get_mapping_summary", "{}", cancellationToken: cancellation.Token);
        await gate.TwoEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(MappingToolOutcome.Busy, (await fixture.InvokeAsync(service, "get_mapping_summary", "{}")).Outcome);
        Assert.Equal(MappingCitationOutcome.Busy, (await service.ValidateCitationsAsync(MappingToolFixture.Actor, fixture.Conversation,
            new[] { new ChatCitationTarget(ChatCitationKind.ProjectionReport) })).Outcome);
        Assert.Equal(2, gate.Entries);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.Empty(fixture.Reads);
        gate.Release.TrySetResult();
        Assert.Equal(MappingToolOutcome.Available, (await fixture.InvokeAsync(service, "get_mapping_summary", "{}")).Outcome);
    }

    [Fact]
    public async Task Internal_deadline_is_categorical_and_does_not_release_any_late_report_result()
    {
        var fixture = new MappingToolFixture();
        var gate = new HeldReportRead(fixture);
        var service = new MappingToolService(fixture, gate, new(duration: TimeSpan.FromMilliseconds(10)));
        var result = await fixture.InvokeAsync(service, "get_mapping_summary", "{}");
        Assert.Equal(MappingToolOutcome.TimedOut, result.Outcome);
        Assert.Empty(result.Citations);
        Assert.Null(result.Data);
        Assert.Empty(fixture.Reads);
        var citations = await service.ValidateCitationsAsync(MappingToolFixture.Actor, fixture.Conversation,
            new[] { new ChatCitationTarget(ChatCitationKind.ProjectionReport) });
        Assert.Equal(MappingCitationOutcome.TimedOut, citations.Outcome);
        Assert.Empty(citations.Citations);
    }

    /// <summary>Only delays an already-authorized read-port contract; it grants no authority.</summary>
    private sealed class HeldReportRead(IRevisionReportReader inner) : IRevisionReportReader
    {
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource TwoEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int entries;
        internal int Entries => Volatile.Read(ref entries);
        public async Task<RevisionReportReadResult> GetRetainedAsync(ActorContext actor, RevisionScope scope,
            RevisionReportOperation operation, Guid derivationId, Guid? projectionDerivationId = null,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref entries) == 2) TwoEntered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return await inner.GetRetainedAsync(actor, scope, operation, derivationId, projectionDerivationId, cancellationToken);
        }

        public Task<RevisionReportReadResult> GetCurrentAsync(ActorContext actor, RevisionScope scope,
            RevisionReportOperation operation, Guid? projectionDerivationId = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Pinned tools cannot ask for current selection.");
    }
}
