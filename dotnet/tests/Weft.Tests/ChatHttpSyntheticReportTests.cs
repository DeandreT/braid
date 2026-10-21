using System.Runtime.Versioning;
using System.Text.Json;
using Weft.Application;
using Weft.Contracts.Examples;
using Weft.Contracts.Projections;
using Weft.Contracts.Validation;
using Weft.Domain;

namespace Weft.Tests;

/// <summary>
/// The same HTTP, catalog, chat-ledger and provider-wire scenarios without a native worker. Report rows are
/// actual PostgreSQL publications with explicitly synthetic completion facts; their verified bodies are
/// synthetic, so these cases do not qualify report derivation or storage.
/// </summary>
[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class ChatHttpSyntheticReportTests(PostgresFixture fixture)
{
    [PostgresFact]
    public Task Turn_streams_resumes_after_sequence_and_replays_without_a_second_dispatch() =>
        RunAsync(ChatHttpScenarios.StreamResumeAndReplay);

    [PostgresFact]
    public Task Cancel_closes_the_live_provider_stream_and_publishes_one_terminal() =>
        RunAsync(ChatHttpScenarios.CancelLiveTurn);

    [PostgresFact]
    public Task Report_pins_owner_privacy_and_revocation_precede_provider_work() =>
        RunAsync(ChatHttpScenarios.ReportPinsPrivacyAndRevocation);

    [PostgresFact]
    public Task Mapping_tool_round_streams_activity_and_feeds_the_next_round() =>
        RunAsync(ChatHttpScenarios.MappingToolRound);

    [PostgresFact]
    public Task Tool_results_stream_verified_citations_after_their_completion_once_per_turn() =>
        RunAsync(ChatHttpScenarios.VerifiedToolCitations);

    [PostgresFact]
    public Task Forged_and_foreign_citation_candidates_are_dropped_before_streaming() =>
        RunAsync(ChatHttpScenarios.ForgedCitationCandidatesDropped);

    [PostgresFact]
    public Task Abandoned_active_turn_is_interrupted_by_a_new_process_without_a_provider_request() =>
        RunAsync(ChatHttpScenarios.AbandonedTurnInterrupted);

    private async Task RunAsync(Func<ChatTestEnvironment, Task> scenario)
    {
        await using var reports = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
        var reader = new SyntheticReports(reports);
        await scenario(new(reports.Case, reports.Actor, reports.Revision, reports.Seed.Admitted, reader, reports.Reader, reader.PublishAsync));
    }

    /// <summary>Fresh metadata authority per read; verified bodies come from the synthetic codec fixtures.</summary>
    private sealed class SyntheticReports(PostgresRevisionDerivationTestSupport reports) : IRevisionReportReader
    {
        private readonly ProjectionCacheKey projection = reports.ProjectionKey();
        private readonly ValidationCacheKey validation = reports.ValidationKey();

        /// <summary>Publication facts match the synthetic bodies: an empty complete projection and a valid validation.</summary>
        internal async Task PublishAsync()
        {
            var claim = await reports.ClaimAsync(projection);
            var observation = new RevisionReportObservation(RevisionReportOperation.Projection, RevisionReportBackendStatus.Succeeded,
                PostgresRevisionDerivationTestSupport.SyntheticProvenance(claim.Key), new(RevisionReportCoverage.Complete, 0, 0, 0, 0), null);
            var finished = await reports.Store.FinishAsync(reports.Actor, claim, RevisionReportCompletion.ForUsable(claim.Key, observation,
                PostgresRevisionDerivationTestSupport.SyntheticDerivedReference(claim)));
            Assert.Equal(RevisionReportStoreOutcome.Applied, finished.Outcome);
            await reports.PublishSyntheticAsync(validation);
        }

        public async Task<RevisionReportReadResult> GetCurrentAsync(ActorContext actor, RevisionScope scope,
            RevisionReportOperation operation, Guid? projectionDerivationId = null, CancellationToken cancellationToken = default)
        {
            var source = await reports.Reader.GetSourceAsync(actor, scope, cancellationToken)
                ?? throw new InvalidOperationException("The synthetic report source is not authorized.");
            RevisionReportKey key = operation == RevisionReportOperation.Projection ? projection : validation;
            var snapshot = await reports.Reader.GetCurrentAsync(actor, key, cancellationToken)
                ?? throw new InvalidOperationException("The synthetic report is not authorized.");
            if (snapshot.SelectedResult is not { } result)
                return RevisionReportReadResult.ForSnapshot(snapshot);
            return RevisionReportReadResult.ForAvailable(source, Verified(operation, result, projectionDerivationId, cancellationToken));
        }

        public async Task<RevisionReportReadResult> GetRetainedAsync(ActorContext actor, RevisionScope scope, RevisionReportOperation operation,
            Guid derivationId, Guid? projectionDerivationId = null, CancellationToken cancellationToken = default)
        {
            var source = await reports.Reader.GetSourceAsync(actor, scope, cancellationToken);
            var retained = source is null ? null : await reports.Reader.GetRetainedAsync(actor, scope, operation, derivationId, cancellationToken);
            if (source is null || retained?.Result is not { } result)
                return RevisionReportReadResult.WithoutData(RevisionReportReadOutcome.NotAuthorizedOrNotFound);
            return RevisionReportReadResult.ForAvailable(source, Verified(operation, result, projectionDerivationId, cancellationToken));
        }

        // Trusted test code supplies the link: the synthetic valid validation has no diagnostics to locate.
        private VerifiedRevisionReport Verified(RevisionReportOperation operation, RevisionReportResult result,
            Guid? projectionDerivationId, CancellationToken cancellationToken)
        {
            var options = ProjectionJson.CreateOptions();
            return operation == RevisionReportOperation.Projection
                ? VerifiedProjectionReport.Create(result, JsonSerializer.Deserialize<ProjectionReportV1>(
                    RevisionReportCodecTestSupport.ProjectionJson(projection), options)!, new(), cancellationToken)
                : VerifiedValidationReport.Create(result, JsonSerializer.Deserialize<ValidationReportV1>(
                    RevisionReportCodecTestSupport.ValidationJson(validation), options)!, new(), projectionDerivationId, cancellationToken);
        }
    }
}
