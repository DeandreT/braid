using Weft.Ai;
using Weft.Application;
using Weft.Domain;

namespace Weft.Tests;

public sealed class ChatSemanticSuccessorTests
{
    private static AiProfileSpec Profile(string host = "one.example", string credential = "fixture-reference", int policyVersion = 1) =>
        AiProfileSpec.Create(AiProfileKey.Create("fixture", 1), AiDialect.ChatCompletionsSseV1, "fixture-model",
            new Uri($"https://{host}/v1/chat/completions"), false, AiAuthentication.Bearer, credential,
            AiCapability.Text | AiCapability.FunctionTools, "mapping-metadata", policyVersion, true, false);
    private static ChatProviderBudget Budget(int output = 2048) => new(600_000_000, 3, 1048576, 4194304, 131072, 8, output);
    private static ChatConversationIntent Conversation()
    {
        var projection = CatalogRevisionReportTestFacts.VerifiedProjection(); var validation = CatalogRevisionReportTestFacts.VerifiedValidation();
        var p = projection.Metadata; var v = validation.Metadata;
        return new(new(p.Key.Scope, CatalogRevisionReportTestFacts.Actor.PrincipalId, Guid.NewGuid()),
            new(p.CacheEntryId, Guid.NewGuid(), p.Key.Fingerprint, p.DerivedObject.Digest),
            new(v.CacheEntryId, v.DerivationId, v.Key.Fingerprint, v.DerivedObject.Digest), ChatTurnPolicyTests.Now.AddHours(1));
    }

    [Fact]
    public void Profile_fingerprint_covers_each_nonsecret_configuration_change()
    {
        var pin = ChatAiBridge.PinProfile("provider", Profile());
        Assert.Equal(pin, ChatAiBridge.PinProfile("provider", Profile()));
        Assert.NotEqual(pin, ChatAiBridge.PinProfile("provider", Profile(host: "two.example")));
        Assert.NotEqual(pin, ChatAiBridge.PinProfile("provider", Profile(credential: "different-reference")));
        Assert.NotEqual(pin, ChatAiBridge.PinProfile("provider", Profile(policyVersion: 2)));
        Assert.NotEqual(pin, ChatAiBridge.PinProfile("other-provider", Profile()));
        Assert.Equal(nameof(ChatProfilePin), pin.ToString());
    }

    [Fact]
    public void Canonical_v1_has_independently_spelled_profile_and_original_byte_oracles()
    {
        const string expectedProfile = """
            {"schema":"weft.chat.profile.v1","provider":"provider","id":"fixture","version":1,"dialect":"ChatCompletionsSseV1","model":"fixture-model","endpoint":"https://one.example/v1/chat/completions","loopbackFixture":false,"authentication":"Bearer","credentialReference":"fixture-reference","capabilities":3,"metadataPolicy":"mapping-metadata","metadataPolicyVersion":1,"mappingMetadataAllowed":true,"requestThinkFalse":false}
            """;
        Assert.Equal(Sha256Digest.FromBytes(System.Text.Encoding.UTF8.GetBytes(expectedProfile)), ChatAiBridge.PinProfile("provider", Profile()).ConfigurationFingerprint);
        var original = new ChatOriginalEnvelope(Guid.Parse("11111111-2222-3333-4444-555555555555"), "text", [new(ChatCitationKind.Field, "f")], Budget(), ChatTurnPolicyTests.Grounding());
        var instructions = Sha256Digest.FromBytes(System.Text.Encoding.UTF8.GetBytes("trusted"));
        var tools = Sha256Digest.FromBytes(System.Text.Encoding.UTF8.GetBytes("{\"schema\":\"weft.chat.tools.v1\",\"tools\":[]}"));
        var expectedOriginal = $$$"""
            {"schema":"weft.chat.original.v1","request":"11111111-2222-3333-4444-555555555555","text":"text","selectors":[{"kind":2,"resource":"f","ordinal":null}],"groundingPolicy":{"id":"fixture","version":1,"instructionsFingerprint":"{{{instructions.Value}}}","toolsFingerprint":"{{{tools.Value}}}","retrievedNodes":64,"toolResultBytes":8192,"toolIterations":3},"providerBudget":{"durationTicks":600000000,"rounds":3,"requestBytes":1048576,"wireBytes":4194304,"textBytes":131072,"toolCalls":8,"outputTokens":2048}}
            """;
        Assert.True(string.Equals(expectedOriginal, original.CanonicalJson, StringComparison.Ordinal), "The canonical original v1 byte contract changed.");
    }

    [Fact]
    public void Original_canonical_identity_includes_selection_order_request_id_and_explicit_budgets()
    {
        var id = Guid.NewGuid(); var a = new ChatCitationTarget(ChatCitationKind.Field, "f"); var b = new ChatCitationTarget(ChatCitationKind.Rule, "r");
        var original = new ChatOriginalEnvelope(id, "private é\n", [a, b], Budget(), ChatTurnPolicyTests.Grounding());
        Assert.Equal(original, ChatOriginalEnvelope.Restore(original.CanonicalJson));
        Assert.NotEqual(original, new ChatOriginalEnvelope(id, original.UserText, [b, a], Budget(), original.GroundingPolicy));
        Assert.NotEqual(original, new ChatOriginalEnvelope(id, original.UserText, [a, b], Budget(1024), original.GroundingPolicy));
        Assert.NotEqual(original, new ChatOriginalEnvelope(Guid.NewGuid(), original.UserText, [a, b], Budget(), original.GroundingPolicy));
        Assert.NotEqual(original, new ChatOriginalEnvelope(id, original.UserText, [a, b], Budget(), ChatAiBridge.PinGroundingPolicy("fixture", 2, "changed", [], 64, 8192, 3)));
        Assert.Throws<ArgumentException>(() => ChatOriginalEnvelope.Restore(original.CanonicalJson.Replace("weft.chat.original.v1", "weft.chat.original.v2", StringComparison.Ordinal)));
        Assert.Throws<ArgumentException>(() => new ChatOriginalEnvelope(id, "text", [a, a], Budget(), original.GroundingPolicy));
        var conversation = Conversation(); var turn = new ChatTurnIntent(Guid.NewGuid(), original, ChatAiBridge.PinProfile("provider", Profile()), new(8, 10000, 3), ChatTurnPolicyTests.Now.AddMinutes(1));
        Assert.NotEqual(ChatCanonicalEnvelope.Fingerprint(ChatCanonicalEnvelope.Turn(conversation, turn)),
            ChatCanonicalEnvelope.Fingerprint(ChatCanonicalEnvelope.Turn(new(conversation.Key, conversation.PinnedProjection, conversation.PinnedValidation, conversation.RetainUntilUtc.AddSeconds(1)), turn)));
    }

    [Fact]
    public void Prepared_round_returns_the_exact_frozen_request_once_and_never_for_replay()
    {
        var conversation = Conversation(); var profile = Profile();
        var turn = new ChatTurnIntent(Guid.NewGuid(), new ChatOriginalEnvelope(Guid.NewGuid(), "question", [], Budget(), ChatTurnPolicyTests.Grounding()), ChatAiBridge.PinProfile("provider", profile), new(8, 10000, 3), ChatTurnPolicyTests.Now.AddMinutes(1));
        var request = AiRoundRequest.Create(Guid.NewGuid(), [AiSystemMessage.Create("trusted"), AiUserMessage.Create("question")], [], 2048);
        var prepared = ChatAiBridge.PrepareRound(conversation, turn, profile, turn.Envelope.GroundingPolicy, request, Guid.NewGuid(), Guid.NewGuid());
        var facts = ChatTurnPolicyTests.Turn(turn, version: 2, rounds: 1);
        var round = new ChatRoundFacts(prepared.Intent, 1, ChatRoundOutcome.Uncertain);
        Assert.Throws<InvalidOperationException>(() => prepared.TakeAfterAcknowledgement(new(ChatStoreOutcome.Replayed, facts, round)));
        var returned = prepared.TakeAfterAcknowledgement(new(ChatStoreOutcome.Applied, facts, round));
        Assert.Same(request, returned); Assert.Equal(prepared.Intent.RoundId, returned.RoundId);
        Assert.Throws<InvalidOperationException>(() => prepared.TakeAfterAcknowledgement(new(ChatStoreOutcome.Applied, facts, round)));
        Assert.Throws<ArgumentException>(() => ChatAiBridge.PrepareRound(conversation, turn, Profile(host: "changed.example"), turn.Envelope.GroundingPolicy, request, Guid.NewGuid(), Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => ChatAiBridge.PrepareRound(conversation, turn, profile, ChatAiBridge.PinGroundingPolicy("fixture", 2, "trusted", [], 64, 8192, 3), request, Guid.NewGuid(), Guid.NewGuid()));
        var changedInstructions = AiRoundRequest.Create(Guid.NewGuid(), [AiSystemMessage.Create("changed"), AiUserMessage.Create("question")], [], 2048);
        Assert.Throws<ArgumentException>(() => ChatAiBridge.PrepareRound(conversation, turn, profile, turn.Envelope.GroundingPolicy, changedInstructions, Guid.NewGuid(), Guid.NewGuid()));
        Assert.Equal(TimeSpan.FromSeconds(30), ChatAiBridge.OpenLimits(facts, ChatTurnPolicyTests.Now.AddSeconds(30)).Duration);
        Assert.Throws<ArgumentException>(() => ChatAiBridge.OpenLimits(facts, ChatTurnPolicyTests.Now.AddMinutes(1)));
    }

    [Fact]
    public void Usage_preserves_unknown_partial_known_zero_and_terminal_failure_provenance()
    {
        var round = Guid.NewGuid();
        var unknown = ChatAiBridge.Usage(round, AiTokenUsage.Unknown, ChatUsageOrigin.ProviderObservation);
        Assert.False(unknown.IsKnown); Assert.Null(unknown.TotalTokens);
        var partial = ChatAiBridge.Usage(round, AiTokenUsage.Create(0, 7, null), ChatUsageOrigin.ProviderRoundTerminal);
        Assert.True(partial.IsKnown); Assert.Equal(0L, partial.InputTokens); Assert.Null(partial.TotalTokens);
        Assert.Equal(partial, ChatMetadataCodec.ReadUsage(ChatMetadataCodec.Usage(partial)));
        Assert.Throws<ArgumentException>(() => ChatMetadataCodec.ReadUsage(ChatMetadataCodec.Usage(unknown).Replace("\"known\":false", "\"known\":true", StringComparison.Ordinal)));
        var completed = ChatAiBridge.Completion(round, AiRoundFailed.Create(AiFailure.Create(AiFailureCode.TruncatedResponse, AiDispatchState.Attempted), AiTokenUsage.Unknown));
        Assert.Equal(ChatRoundOutcome.Failed, completed.Outcome);
        Assert.Equal(ChatProviderFailureCode.TruncatedResponse, completed.Metadata!.Failure!.Code);
        Assert.Equal(ChatDispatchObservation.Attempted, completed.Metadata.Failure.Dispatch);
        Assert.False(completed.Metadata.Usage!.IsKnown);
        Assert.Equal(completed.Metadata, ChatMetadataCodec.ReadTerminal(ChatMetadataCodec.Terminal(completed.Metadata)));
        Assert.Throws<ArgumentException>(() => new ChatProviderFailureFact(round, (ChatProviderFailureCode)999, ChatDispatchObservation.Attempted));
        var rejected = ChatAiBridge.Rejection(round, AiFailure.Create(AiFailureCode.Busy, AiDispatchState.NotAttempted));
        Assert.Equal(ChatUsageOrigin.ProviderOpenRejection, rejected.Metadata!.Usage!.Origin);
        Assert.False(rejected.Metadata.Usage.IsKnown); Assert.Equal(ChatDispatchObservation.NotAttempted, rejected.Metadata.Failure!.Dispatch);
    }

    [Fact]
    public void Citation_proof_requires_exact_verified_report_and_emitted_diagnostic_ordinal()
    {
        var projection0 = CatalogRevisionReportTestFacts.VerifiedProjection();
        var projection = VerifiedProjectionReport.Create(CatalogRevisionReportTestFacts.Result(projection0.Metadata.Key, projection0.Metadata.Observation, CatalogRevisionReportTestFacts.ProjectionId), projection0.Report, new());
        var validation = CatalogRevisionReportTestFacts.VerifiedValidation();
        var p = projection.Metadata; var v = validation.Metadata;
        var conversation = new ChatConversationIntent(new(v.Key.Scope, CatalogRevisionReportTestFacts.Actor.PrincipalId, Guid.NewGuid()),
            new(p.CacheEntryId, p.DerivationId, p.Key.Fingerprint, p.DerivedObject.Digest), new(v.CacheEntryId, v.DerivationId, v.Key.Fingerprint, v.DerivedObject.Digest), ChatTurnPolicyTests.Now.AddHours(1));
        var proof = ChatValidatedCitation.Validate(conversation, validation, new(ChatCitationKind.ValidationDiagnostic, diagnosticOrdinal: 0));
        var reportProof = ChatValidatedCitation.Validate(conversation, validation, new(ChatCitationKind.ValidationReport));
        Assert.Equal(reportProof.Facts!, ChatMetadataCodec.ReadCitation(ChatMetadataCodec.Citation(reportProof.Facts!)));
        Assert.NotNull(ChatValidatedCitation.Validate(conversation, projection, new(ChatCitationKind.ProjectionReport)));
        Assert.Throws<ArgumentException>(() => new ChatCitationTarget(ChatCitationKind.ValidationReport, resourceId: "invented"));
        Assert.Throws<ArgumentException>(() => ChatValidatedCitation.Validate(conversation, validation, new(ChatCitationKind.ProjectionReport)));
        Assert.Equal(0, proof.Facts!.Target.DiagnosticOrdinal); Assert.Null(proof.Facts!.Target.ResourceId);
        Assert.Equal(proof.Facts!, ChatMetadataCodec.ReadCitation(ChatMetadataCodec.Citation(proof.Facts!)));
        Assert.Throws<ArgumentException>(() => ChatValidatedCitation.Validate(conversation, validation, new(ChatCitationKind.ValidationDiagnostic, diagnosticOrdinal: 1)));
        Assert.Throws<ArgumentException>(() => ChatValidatedCitation.Validate(conversation, projection, new(ChatCitationKind.Field, "invented")));
        Assert.Throws<ArgumentException>(() => ChatValidatedCitation.Validate(conversation, validation, new(ChatCitationKind.ProjectionDiagnostic, diagnosticOrdinal: 0)));
        var altered = new ChatConversationIntent(conversation.Key, conversation.PinnedProjection,
            new(v.CacheEntryId, v.DerivationId, v.Key.Fingerprint, Sha256Digest.FromBytes([9])), conversation.RetainUntilUtc);
        Assert.Throws<ArgumentException>(() => ChatValidatedCitation.Validate(altered, validation, proof.Facts!.Target));
    }
}
