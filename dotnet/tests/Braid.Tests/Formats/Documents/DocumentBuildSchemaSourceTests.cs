using Braid.Persistence.Postgres;

namespace Braid.Tests;

public sealed class DocumentBuildSchemaSourceTests
{
    [Fact]
    public void Interchange_qualification_retains_all_existing_guards_and_adds_only_explicit_profile_admission()
    {
        var previous = BuildRoleGuard.LoadFunctions("0034_document_previews.sql", 2)["qualification_valid"];
        var current = BuildRoleGuard.LoadFunctions("0035_x12_interchange_builds.sql", 1)["qualification_valid"];
        for (var group = 1; group <= 6; group++) Assert.Equal(previous.Groups[group].Value, current.Groups[group].Value);
        const string oldProfile = "    documents:=intent->>'profileId'='braid.ferrule-csharp-document.v1';";
        string[] added = [
            "    documents:=intent->>'profileId' IN ('braid.ferrule-csharp-document.v1','braid.ferrule-csharp-x12-interchanges.v1');",
            "    IF intent->>'profileId'='braid.ferrule-csharp-x12-interchanges.v1' AND NOT EXISTS(",
            "        SELECT 1 FROM jsonb_array_elements(intent->'boundaries') e WHERE e->>'format'='x12') THEN RETURN false; END IF;"
        ];
        Assert.Contains(oldProfile, previous.Groups[7].Value.Split('\n'));
        foreach (var line in added) Assert.Contains(line, current.Groups[7].Value.Split('\n'));
        Assert.Equal(previous.Groups[7].Value.Split('\n').Where(line => line != oldProfile),
            current.Groups[7].Value.Split('\n').Where(line => !added.Contains(line, StringComparer.Ordinal)));
    }
    [Fact]
    public void Document_build_guards_load_the_exact_replacement_bodies_without_changing_the_original_contract()
    {
        var original = BuildRoleGuard.LoadFunctions("0018_build_publication.sql", BuildRoleGuard.Names.Length);
        var documents = BuildRoleGuard.LoadFunctions("0034_document_previews.sql", 2);
        Assert.Equal(new[] { "build_evidence_passes", "qualification_valid" }, documents.Keys.Order(StringComparer.Ordinal));
        Assert.NotEqual(original["build_evidence_passes"].Groups[7].Value, documents["build_evidence_passes"].Groups[7].Value);
        foreach (var definition in documents.Values)
        {
            Assert.Equal("boolean", definition.Groups[3].Value);
            Assert.Equal("plpgsql", definition.Groups[4].Value);
            Assert.Equal("STABLE", definition.Groups[5].Value);
            Assert.Equal("INVOKER", definition.Groups[6].Value);
            Assert.Contains("mediaType", definition.Groups[7].Value);
            Assert.Contains("braid.regression.exact-bytes.v1", definition.Groups[7].Value);
        }
    }

    [Fact]
    public void Combined_prefix_preserves_every_migration_before_the_document_suffix()
    {
        var scripts = MigrationRunner.EmbeddedMigrations;
        Assert.Equal(Enumerable.Range(1, 47), scripts.Select(script => script.Version));
        Assert.Equal(XmlPreviewSchemaAdmission.MigrationChecksum, scripts[41].Checksum);
        Assert.Equal(PostgresMigrationTests.CurrentChecksums, scripts.Select(script => script.Checksum));
        Assert.Equal(RegressionSchemaTestContract.AccountDisplayChecksum, scripts[25].Checksum);
        Assert.Equal(RegressionSchemaTestContract.AdministrationChecksum, scripts[26].Checksum);
        Assert.Equal(InterchangeSchemaTestContract.MigrationChecksum, scripts[27].Checksum);
        Assert.Equal(CatalogSearchSchemaAdmission.MigrationChecksum, scripts[28].Checksum);
        Assert.Equal(RegressionSchemaTestContract.AutomationChecksum, scripts[29].Checksum);
        Assert.Equal(RegressionSchemaTestContract.ReleaseOperationsChecksum, scripts[30].Checksum);
        Assert.Equal(ChatSchemaContract.WorkspaceMigrationChecksum, scripts[31].Checksum);
        Assert.Equal(PreviewSchemaTestContract.ReadsMigrationChecksum, scripts[32].Checksum);
        Assert.Equal(RegressionSchemaTestContract.DocumentPreviewsChecksum, scripts[33].Checksum);
        Assert.Equal(RegressionSchemaTestContract.X12InterchangeBuildsChecksum, scripts[34].Checksum);
        Assert.Equal(RegressionSchemaTestContract.ComparisonSettlementChecksum, scripts[35].Checksum);
    }

    [Fact]
    public void Document_suffix_replaces_only_evidence_functions_and_retains_automation_gate_triggers()
    {
        var scripts = MigrationRunner.EmbeddedMigrations;
        var automation = scripts[29].Sql;
        Assert.Contains("CREATE TRIGGER automation_build_guard BEFORE INSERT ON braid.build_approvals", automation);
        Assert.Contains("CREATE TRIGGER automation_qualification_guard BEFORE INSERT ON braid.build_qualifications", automation);
        Assert.Contains("IF braid.automation_gate(NEW.organization_id,NEW.project_id,mapping,revision) IS NOT TRUE", automation);
        Assert.Contains("CONSTRAINT='automation_regression_gate'", automation);
        foreach (var script in scripts.Skip(33).Take(2))
        {
            Assert.DoesNotMatch(@"(?i)\b(?:DROP|ALTER)\s+(?:TRIGGER|FUNCTION|TABLE)\b.*?automation", script.Sql);
            Assert.DoesNotMatch(@"(?i)\bDROP\s+(?:TRIGGER|FUNCTION|TABLE)\b", script.Sql);
            Assert.DoesNotContain("automation_", script.Sql);
        }
        Assert.Equal(new[] { "build_evidence_passes", "qualification_valid" },
            BuildRoleGuard.LoadFunctions("0034_document_previews.sql", 2).Keys.Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "qualification_valid" }, BuildRoleGuard.LoadFunctions("0035_x12_interchange_builds.sql", 1).Keys);
    }
}
