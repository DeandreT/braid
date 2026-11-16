using Npgsql;
using Braid.Application;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

public sealed class CsvPersistenceContractTests
{
    [Fact]
    public void Csv_qualification_keeps_every_prior_identity_and_exact_byte_guard()
    {
        var previous = BuildRoleGuard.LoadFunctions("0035_x12_interchange_builds.sql", 1)["qualification_valid"].Groups[7].Value;
        var current = BuildRoleGuard.CsvQualificationDefinition.Groups[7].Value;
        foreach (var guard in new[]
        {
            "a.actor_id=actor", "a.intent_body=x.intent_body", "b.publication_state<>2", "r.case_owner<>actor",
            "r.attempt NOT BETWEEN 1 AND 3", "x.revision_id=b.revision_id", "x.native_digest=b.native_digest",
            "q->>'evidenceDigest' IS DISTINCT FROM b.evidence_digest", "'braid.regression.exact-bytes.v1'",
            "header->'target' IS DISTINCT FROM '{\"kind\":\"all\",\"name\":null}'::jsonb",
            "q->>'replayDigest' IS DISTINCT FROM encode(sha256(convert_to(replay,'UTF8')),'hex')",
            "octet_length(raw) IS DISTINCT FROM (expected->>'byteCount')::integer",
            "encode(sha256(raw),'hex') IS DISTINCT FROM expected->>'sha256'",
            "RETURN convert_to(canonical||']}', 'UTF8')=completion"
        })
        {
            Assert.Contains(guard, previous, StringComparison.Ordinal);
            Assert.Contains(guard, current, StringComparison.Ordinal);
        }
        Assert.Contains("x->'csvDialect'=e->'csvDialect'", current, StringComparison.Ordinal);
        Assert.Contains("item.value::jsonb->'csvDialect' IS DISTINCT FROM boundary->'csvDialect'", current, StringComparison.Ordinal);
        Assert.Contains("braid.csv-regression-run-result.v1", current, StringComparison.Ordinal);
    }

    [Fact]
    public void Csv_migration_is_additive_and_binds_empty_presence_to_the_new_profile()
    {
        using var source = typeof(PostgresPreviewRunStore).Assembly.GetManifestResourceStream("Braid.Persistence.Postgres.Migrations.0041_csv_documents.sql")!;
        using var reader = new StreamReader(source);
        var text = reader.ReadToEnd();
        Assert.Contains("csv_profile boolean NOT NULL DEFAULT false", text, StringComparison.Ordinal);
        Assert.Contains("csv_input boolean NOT NULL DEFAULT false", text, StringComparison.Ordinal);
        Assert.Contains("request->'inputs' IS DISTINCT FROM jsonb_build_array", text, StringComparison.Ordinal);
        Assert.Contains("'present',true", text, StringComparison.Ordinal);
        Assert.Contains("NEW.csv_input IS DISTINCT FROM csv", text, StringComparison.Ordinal);
        Assert.Contains("r.submitter_id=p_actor", text, StringComparison.Ordinal);
        Assert.DoesNotContain("TRUNCATE", text, StringComparison.Ordinal);
        Assert.DoesNotContain("DELETE FROM", text, StringComparison.Ordinal);
        Assert.DoesNotContain("braid.regression.csv-rows.v1", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Native_dialects_are_resolved_from_explicit_options_not_filenames()
    {
        var scope = new Braid.Domain.RevisionScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var native = Braid.Domain.NativeArtifact.Admit(scope, CsvPreviewTestData.Native, PreviewPolicy.MaximumNativeBytes);
        var dialect = CsvNativeDialect.Read(native);
        Assert.Equal(CsvPreviewTestData.Dialect, dialect.Input);
        Assert.Equal(CsvPreviewTestData.Dialect, dialect.Output);
    }
}

[Collection(PostgresCollection.Name)]
public sealed class CsvDialectSqlTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task Sql_dialect_canonical_matches_the_default_writer_for_every_admitted_ascii_character()
    {
        await using var test = await fixture.CreateCaseAsync();
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(test.MigrationConnectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        for (var delimiter = 1; delimiter <= 127; delimiter++)
        {
            if (delimiter is 10 or 13) continue;
            var dialect = new CsvDocumentDialect(CsvDocumentPolicy.DialectSchema, ((char)delimiter).ToString(), null, true, false, false, true);
            await Check(dialect);
        }
        for (var quote = 33; quote <= 126; quote++)
        {
            var dialect = new CsvDocumentDialect(CsvDocumentPolicy.DialectSchema, quote == ',' ? ";" : ",", ((char)quote).ToString(), false, true, true, false);
            await Check(dialect);
        }
        async Task Check(CsvDocumentDialect dialect)
        {
            var expected = CsvDialectJson.Encode(dialect);
            await using var command = new NpgsqlCommand("SELECT braid.csv_dialect_canonical($1::jsonb)", connection);
            command.Parameters.AddWithValue(expected);
            Assert.Equal(expected, await command.ExecuteScalarAsync());
        }
    }
}
