using System.Globalization;
using System.Text;
using System.Text.Json;
using Braid.Application;
using Braid.Domain;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresIngestionEvidenceLimitTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task SyntheticCompactEvidenceWithinApplicationBudgetExceedsActualJsonbBudgetAndRecordsHonestRejection()
    {
        // Explicitly synthetic trusted C1 evidence against actual PostgreSQL. This is not
        // evidence that the qualified Ferrule worker can emit an eight-MiB document.
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var reserved = await test.ReserveAsync();
        var claim = await test.ClaimAsync(reserved);
        await test.StageAsync(reserved, claim);
        var validation = new IngestionValidationFacts(IngestionBackendStatus.Succeeded,
            new(reserved.FutureRevision, reserved.NativeObject.Digest, reserved.BackendProfile, true),
            true, DiagnosticCount, DiagnosticCount, false);
        var baseline = BuildSyntheticEvidence(validation, extraCharacters: 0);
        var targetCompactLength = IngestionEvidenceDocument.MaximumUtf8Bytes - 1024;
        var addedCharacters = targetCompactLength - baseline.CopyUtf8().Length;
        Assert.InRange(addedCharacters, 1, DiagnosticCount * ValuesPerDiagnostic * (2048 - BaseValueLength));
        var evidence = BuildSyntheticEvidence(validation, addedCharacters);
        var compact = evidence.CopyUtf8();
        Assert.Equal(targetCompactLength, compact.Length);
        Assert.Equal(IngestionAdmissionClassification.EligibleValid,
            IngestionAttemptCompletion.FromValidation(evidence).Classify(reserved));
        var normalizedLength = await test.Case.ScalarAsync<long>(
            "SELECT octet_length($1::jsonb::text)::bigint", Encoding.UTF8.GetString(compact));
        Assert.True(normalizedLength > IngestionEvidenceDocument.MaximumUtf8Bytes);

        var result = await test.Store.CompleteAttemptAsync(test.Actor, claim, IngestionAttemptCompletion.FromValidation(evidence));
        Assert.Equal(IngestionStoreOutcome.Applied, result.Outcome);
        var receipt = Assert.IsType<IngestionReceipt>(result.Receipt);
        Assert.Equal(IngestionPhase.Rejected, receipt.Phase);
        Assert.NotNull(receipt.CompletedAtUtc);
        Assert.Null(receipt.EligibleAttemptGeneration);
        Assert.Null(receipt.CommittedRevision);
        var attempt = Assert.Single(receipt.Attempts);
        Assert.Equal(IngestionAdmissionClassification.RejectedResourceLimit, attempt.Classification);
        var completion = Assert.IsType<IngestionAttemptCompletion>(attempt.Completion);
        Assert.Equal(IngestionAttemptCompletionKind.OperationalFailure, completion.Kind);
        Assert.Equal(IngestionFailureCode.EvidenceLimit, completion.Failure);
        Assert.Null(completion.Evidence);
        Assert.Equal(validation.Provenance, completion.ObservedProvenance);
        Assert.Equal(1L, await test.Case.ScalarAsync<long>("""
            SELECT count(*) FROM braid.revision_ingestion_attempts
            WHERE operation_id=$1 AND completion_kind='operational_failure'
              AND admission_classification='rejected_resource_limit' AND last_error='evidence_limit'
              AND backend_status IS NULL AND is_valid IS NULL
              AND total_issue_count IS NULL AND emitted_issue_count IS NULL AND issues_truncated IS NULL
              AND evidence_version IS NULL AND evidence IS NULL
            """, reserved.OperationId));
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.mappings"));
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.mapping_revisions"));
        var reopened = await test.Store.GetReceiptAsync(test.Actor, test.Project, reserved.OperationId);
        Assert.Equal(IngestionPhase.Rejected, reopened!.Phase);
        Assert.Equal(IngestionFailureCode.EvidenceLimit, reopened.Attempts.Single().Completion!.Failure);
        var safeResult = RevisionIngestionResult.ForReceipt(RevisionIngestionOutcome.Rejected, RevisionIngestionReceipt.FromMetadata(reopened));
        Assert.Equal(RevisionIngestionRejection.ResourceLimit, safeResult.Rejection);
    }

    private const int DiagnosticCount = 128;
    private const int ValuesPerDiagnostic = 32;
    private const int BaseValueLength = 2028;

    private static IngestionEvidenceDocument BuildSyntheticEvidence(IngestionValidationFacts validation, int extraCharacters)
    {
        var valueCount = DiagnosticCount * ValuesPerDiagnostic;
        var extraPerValue = extraCharacters / valueCount;
        var remainder = extraCharacters % valueCount;
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartArray();
            for (var diagnostic = 0; diagnostic < DiagnosticCount; diagnostic++)
            {
                writer.WriteStartObject();
                writer.WriteString("code", "c1.synthetic.jsonb-normalization-budget");
                for (var value = 0; value < ValuesPerDiagnostic; value++)
                {
                    var index = (diagnostic * ValuesPerDiagnostic) + value;
                    var length = BaseValueLength + extraPerValue + (index < remainder ? 1 : 0);
                    writer.WriteString("p" + value.ToString("D2", CultureInfo.InvariantCulture), new string('x', length));
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        using var diagnostics = JsonDocument.Parse(bytes.ToArray());
        // Create validates actual node/depth/text/diagnostic/UTF8 limits before this
        // document reaches the DAL. The added ASCII changes its compact size exactly.
        return IngestionEvidenceDocument.Create(validation, diagnostics.RootElement);
    }
}
