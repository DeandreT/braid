using System.Runtime.Versioning;
using H = Weft.Tests.ChatHttpTestSupport;

namespace Weft.Tests;

/// <summary>Full stack: native ingestion and report derivation through the actual Ferrule worker.</summary>
[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class ChatHttpTests(PostgresFixture fixture)
{
    [RevisionIngestionFact]
    public Task Turn_streams_resumes_after_sequence_and_replays_without_a_second_dispatch() =>
        RunAsync(ChatHttpScenarios.StreamResumeAndReplay);

    [RevisionIngestionFact]
    public Task Cancel_closes_the_live_provider_stream_and_publishes_one_terminal() =>
        RunAsync(ChatHttpScenarios.CancelLiveTurn);

    [RevisionIngestionFact]
    public Task Report_pins_owner_privacy_and_revocation_precede_provider_work() =>
        RunAsync(ChatHttpScenarios.ReportPinsPrivacyAndRevocation);

    [RevisionIngestionFact]
    public Task Mapping_tool_round_streams_activity_and_feeds_the_next_round() =>
        RunAsync(ChatHttpScenarios.MappingToolRound);

    [RevisionIngestionFact]
    public Task Tool_results_stream_verified_citations_after_their_completion_once_per_turn() =>
        RunAsync(ChatHttpScenarios.VerifiedToolCitations);

    [RevisionIngestionFact]
    public Task Forged_and_foreign_citation_candidates_are_dropped_before_streaming() =>
        RunAsync(ChatHttpScenarios.ForgedCitationCandidatesDropped);

    [RevisionIngestionFact]
    public Task Abandoned_active_turn_is_interrupted_by_a_new_process_without_a_provider_request() =>
        RunAsync(ChatHttpScenarios.AbandonedTurnInterrupted);

    private async Task RunAsync(Func<ChatTestEnvironment, Task> scenario)
    {
        await using var test = await RevisionReportTestSupport.CreateAsync(fixture);
        await scenario(new(test.Case, test.Actor, test.Revision, test.Seed.Admitted, test.Service, test.Reader,
            () => H.EnsureReportsAsync(test), DerivedReports: true));
    }
}
