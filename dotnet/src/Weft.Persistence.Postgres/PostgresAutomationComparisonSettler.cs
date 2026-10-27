using System.Data;
using Npgsql;
using Weft.Application;
using Weft.Application.Automation;
using Weft.Application.Regression;
using Weft.Domain;

namespace Weft.Persistence.Postgres;

/// <summary>Separate credentialed authority; derives every decision from locked, stored bytes.</summary>
public sealed class PostgresAutomationComparisonSettler(string connectionString, RegressionRuntimeIdentity? documentRuntime = null) : IAutomationComparisonSettler, IAsyncDisposable
{
    private readonly NpgsqlDataSource source = NpgsqlDataSource.Create(PostgresConfiguration.Normalize(connectionString));

    public async Task<bool> SettleNextAsync(CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, token).ConfigureAwait(false);
        await using (var settings = new NpgsqlCommand("SET LOCAL search_path=pg_catalog; SET LOCAL TimeZone='UTC'; SET LOCAL statement_timeout='15s'; SET LOCAL lock_timeout='5s'", connection, transaction))
            await settings.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        await AutomationSchemaAdmission.ValidateComparisonRoleAsync(connection, transaction, token).ConfigureAwait(false);
        await AutomationSchemaAdmission.ValidateAsync(connection, transaction, token).ConfigureAwait(false);
        await RegressionRunRoleGuard.ValidateContractAsync(connection, transaction, token).ConfigureAwait(false);
        await using var read = new NpgsqlCommand("SELECT * FROM weft.automation_read_comparison()", connection, transaction);
        Guid org, suite, caseId, claim;
        int generation;
        string digest, policy, caseDigest;
        string decision;
        await using (var rows = await read.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            if (!await rows.ReadAsync(token).ConfigureAwait(false))
            {
                await rows.DisposeAsync().ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return false;
            }
            org = rows.GetGuid(0); suite = rows.GetGuid(1); caseId = rows.GetGuid(2);
            caseDigest = rows.GetString(9); digest = rows.GetString(13); claim = rows.GetGuid(14); generation = rows.GetInt32(15);
            policy = rows.GetString(16);
            decision = "notEvaluated";
            try
            {
                var reference = new RegressionCaseReference(caseId, rows.GetInt64(3), new(rows.GetGuid(4)),
                    new(org, rows.GetGuid(5), rows.GetGuid(6), rows.GetGuid(7)), Sha256Digest.Parse(rows.GetString(8)),
                    Sha256Digest.Parse(caseDigest), rows.GetInt32(10));
                var body = rows.GetFieldValue<byte[]>(12);
                if (Sha256Digest.FromBytes(body).Value == digest)
                    decision = Compare(suite, reference, rows.GetFieldValue<byte[]>(11), body, policy, documentRuntime, token);
            }
            catch (Exception error) when (error is RegressionCaseException or ArgumentException or InvalidOperationException)
            { /* An invalid stored case fails this item, not later independent items. */ }
            if (await rows.ReadAsync(token).ConfigureAwait(false)) throw new InvalidOperationException("Comparison claim was not singular.");
        }
        // Re-admit the inherited authorization routines after comparison too;
        // a source change cannot be committed as a trusted settlement.
        await RegressionRunRoleGuard.ValidateContractAsync(connection, transaction, token).ConfigureAwait(false);
        await using var finish = new NpgsqlCommand("SELECT weft.automation_complete_comparison($1,$2,$3,$4,$5,$6,$7,$8,$9)", connection, transaction);
        foreach (var value in new object[] { org, suite, caseId, claim, generation, caseDigest, digest, policy, decision })
            finish.Parameters.AddWithValue(value);
        _ = await finish.ExecuteScalarAsync(token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return true;
    }

    internal static string Compare(Guid suite, RegressionCaseReference reference, byte[] envelope, byte[] body, string policy, RegressionRuntimeIdentity? runtime, CancellationToken token)
    {
        try
        {
            var value = new RegressionCaseEnvelopeCodec().Decode(reference, envelope, token);
            if (value.Runtime.Profile != DocumentPreviewPolicy.Profile || value.Runtime != runtime || value.ComparisonPolicy != policy) return "notEvaluated";
            var observed = new RegressionRunResultCodec().Decode(suite, value, body, token);
            return observed.Comparison?.Outcome switch
            {
                RegressionComparisonOutcome.Passed => "passed",
                RegressionComparisonOutcome.Failed => "failed",
                _ => "notEvaluated"
            };
        }
        catch (Exception error) when (error is RegressionCaseException or RegressionComparisonException or InvalidOperationException or ArgumentException)
        { return "notEvaluated"; }
    }

    public ValueTask DisposeAsync() => source.DisposeAsync();
}
