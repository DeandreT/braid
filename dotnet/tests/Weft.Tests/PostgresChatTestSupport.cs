using System.Text.Json;
using Weft.Application;
using Weft.Contracts.Validation;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

/// <summary>Owned actual metadata fixtures with synthetic report publication; never invokes a model or worker.</summary>
internal sealed class PostgresChatTestSupport : IAsyncDisposable
{
    private readonly PostgresRevisionDerivationTestSupport _reports;
    internal PostgresCase Case => _reports.Case;
    internal ActorContext Actor => _reports.Actor;
    internal ChatConversationIntent Conversation { get; }
    internal ChatConversationKey Key => Conversation.Key;
    internal PostgresChatDatabase ReaderDatabase { get; }
    internal PostgresChatDatabase WriterDatabase { get; }
    internal PostgresChatStore Reader { get; }
    internal PostgresChatStore Writer { get; }
    internal ActorContext OtherActor => _reports.AdmittedActor;
    internal PostgresSeedData Seed => _reports.Seed;
    internal RevisionScope Revision => _reports.Revision;
    internal ChatErasurePolicy ErasurePolicy => new("fixture-retention", 1, Conversation.RetainUntilUtc.AddHours(1));
    internal VerifiedValidationReport VerifiedValidation { get; }

    private PostgresChatTestSupport(PostgresRevisionDerivationTestSupport reports, ChatConversationIntent conversation, VerifiedValidationReport validation)
    {
        _reports = reports; Conversation = conversation; VerifiedValidation = validation;
        ReaderDatabase = new(Case.ChatReaderConnectionString, ChatDatabaseProfile.Reader);
        WriterDatabase = new(Case.ChatWriterConnectionString, ChatDatabaseProfile.Writer);
        Reader = new(ReaderDatabase); Writer = new(WriterDatabase);
    }
    internal static async Task<PostgresChatTestSupport> CreateAsync(PostgresFixture fixture, TimeSpan? retention = null)
    {
        var reports = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
        PostgresChatTestSupport? support = null;
        try
        {
            var projectionKey = reports.ProjectionKey(); var validationKey = reports.ValidationKey();
            await reports.PublishSyntheticAsync(projectionKey);
            var validationMetadata = await reports.PublishSyntheticAsync(validationKey, valid: false);
            var validationReport = JsonSerializer.Deserialize<ValidationReportV1>(RevisionReportCodecTestSupport.ValidationJson(validationKey, valid: false), Weft.Contracts.Projections.ProjectionJson.CreateOptions())!;
            var verifiedValidation = VerifiedValidationReport.Create(validationMetadata, validationReport, new());
            // Read exact immutable IDs/digests; use no report body or native object bytes.
            async Task<ChatReportPin> PinAsync(Guid derivationId) => new(
                await reports.Case.ScalarAsync<Guid>("SELECT cache_entry_id FROM weft.revision_derivation_attempts WHERE derivation_id=$1", derivationId), derivationId,
                await KeyAsync(derivationId), Sha256Digest.Parse(await reports.Case.ScalarAsync<string>("SELECT derived_digest FROM weft.revision_derivation_attempts WHERE derivation_id=$1", derivationId)));
            async Task<Sha256Digest> KeyAsync(Guid id) => Sha256Digest.Parse(await reports.Case.ScalarAsync<string>("""
                SELECT d.key_fingerprint FROM weft.revision_derivations d JOIN weft.revision_derivation_attempts a
                    USING(organization_id,project_id,mapping_id,revision_id,cache_entry_id) WHERE a.derivation_id=$1
                """, id));
            var projectionId = await reports.Case.ScalarAsync<Guid>("SELECT derivation_id FROM weft.revision_derivation_attempts WHERE cache_entry_id=(SELECT cache_entry_id FROM weft.revision_derivations WHERE key_fingerprint=$1) AND result_usable", projectionKey.Fingerprint.Value);
            var validationId = await reports.Case.ScalarAsync<Guid>("SELECT derivation_id FROM weft.revision_derivation_attempts WHERE cache_entry_id=(SELECT cache_entry_id FROM weft.revision_derivations WHERE key_fingerprint=$1) AND result_usable", validationKey.Fingerprint.Value);
            var now = new DateTimeOffset(await reports.Case.ScalarAsync<DateTime>("SELECT statement_timestamp()"));
            var conversation = new ChatConversationIntent(new(reports.Revision, reports.Actor.PrincipalId, Guid.NewGuid()),
                await PinAsync(projectionId), await PinAsync(validationId), now + (retention ?? TimeSpan.FromHours(1)));
            support = new(reports, conversation, verifiedValidation);
            Assert.Equal(ChatStoreOutcome.Applied, await support.Writer.CreateAsync(support.Actor, conversation));
            return support;
        }
        catch { if (support is null) await reports.DisposeAsync(); else await support.DisposeAsync(); throw; }
    }
    internal async Task<ChatTurnIntent> IntentAsync(Guid? id = null, string original = "private original", ChatTurnBudget? budget = null)
    {
        var now = new DateTimeOffset(await Case.ScalarAsync<DateTime>("SELECT statement_timestamp()"));
        var deadline = now.AddMinutes(1);
        var turnId = id ?? Guid.NewGuid();
        return new(turnId, new ChatOriginalEnvelope(turnId, original, [], ChatTurnPolicyTests.ProviderBudget(), ChatTurnPolicyTests.Grounding()), ChatTurnPolicyTests.Profile(), budget ?? new(16, 65536, 3),
            deadline < Conversation.RetainUntilUtc ? deadline : Conversation.RetainUntilUtc);
    }
    internal async Task<ChatTurnFacts> StartAsync(ChatTurnIntent? intent = null)
    {
        var result = await Writer.StartAsync(Actor, Key, intent ?? await IntentAsync());
        Assert.Equal(ChatStoreOutcome.Applied, result.Outcome);
        return Assert.IsType<ChatTurnFacts>(result.Facts);
    }
    internal static ChatRoundIntent Round(Guid? instanceId = null) => new(Guid.NewGuid(), Guid.NewGuid(), instanceId ?? Guid.NewGuid(), "canonical private provider request");
    public async ValueTask DisposeAsync()
    {
        try { await ReaderDatabase.DisposeAsync(); }
        finally { try { await WriterDatabase.DisposeAsync(); } finally { await _reports.DisposeAsync(); } }
    }
}
