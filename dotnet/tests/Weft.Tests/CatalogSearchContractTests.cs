using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

public sealed class CatalogSearchContractTests
{
    [Fact]
    public void EmbeddedHistoryKeepsDisplayAndSearchAsSeparateAdditiveMigrations()
    {
        var migrations = MigrationRunner.EmbeddedMigrations;
        Assert.Equal(Enumerable.Range(1, 45), migrations.Select(migration => migration.Version));
        Assert.Equal(PostgresMigrationTests.CurrentChecksums, migrations.Select(migration => migration.Checksum));
        Assert.Equal(XmlPreviewSchemaAdmission.MigrationChecksum, migrations[41].Checksum);
        Assert.Equal(AccountDisplayMetadataTestContract.MigrationChecksum, migrations[25].Checksum);
        Assert.Equal(RegressionSchemaTestContract.AdministrationChecksum, migrations[26].Checksum);
        Assert.Equal(InterchangeSchemaTestContract.MigrationChecksum, migrations[27].Checksum);
        Assert.Equal(CatalogSearchSchemaAdmission.MigrationChecksum, migrations[28].Checksum);
        Assert.Equal(RegressionSchemaTestContract.AutomationChecksum, migrations[29].Checksum);
        Assert.Equal(RegressionSchemaTestContract.ReleaseOperationsChecksum, migrations[30].Checksum);
        Assert.Equal(ChatSchemaContract.WorkspaceMigrationChecksum, migrations[31].Checksum);
        Assert.Equal(PreviewSchemaTestContract.ReadsMigrationChecksum, migrations[32].Checksum);
        Assert.Equal(RegressionSchemaTestContract.DocumentPreviewsChecksum, migrations[33].Checksum);
        Assert.Equal(RegressionSchemaTestContract.X12InterchangeBuildsChecksum, migrations[34].Checksum);
        Assert.Equal(RegressionSchemaTestContract.ComparisonSettlementChecksum, migrations[35].Checksum);
        Assert.Equal(MappingDraftSchemaTestContract.DraftChecksum, migrations[36].Checksum);
        Assert.Equal(MappingDraftSchemaTestContract.PreviewChecksum, migrations[37].Checksum);
        Assert.Equal(ChatSchemaContract.MappingWriteMigrationChecksum, migrations[38].Checksum);
        Assert.Equal(ChatSchemaContract.WriteJournalMigrationChecksum, migrations[39].Checksum);
        Assert.Equal(RegressionSchemaTestContract.CsvDocumentsChecksum, migrations[40].Checksum);
    }

    [Fact]
    public void SearchConservationStopsAtTheExactSearchMigration()
    {
        var search = CatalogSearchUpgradeTestSupport.SearchMigrations;
        Assert.Equal(Enumerable.Range(1, 29), search.Select(script => script.Version));
        Assert.Equal(CatalogSearchSchemaAdmission.MigrationChecksum, search[^1].Checksum);
        Assert.Equal(PostgresMigrationTests.CurrentChecksums.Skip(search.Count), MigrationRunner.EmbeddedMigrations.Skip(search.Count).Select(script => script.Checksum));
    }

    private static readonly Guid Organization = Guid.Parse("10000000-0000-4000-8000-000000000001");
    private static readonly Guid Project = Guid.Parse("20000000-0000-4000-8000-000000000001");
    private static readonly Guid Mapping = Guid.Parse("30000000-0000-4000-8000-000000000001");
    private static readonly Guid Revision = Guid.Parse("40000000-0000-4000-8000-000000000001");
    private static CatalogSearchKey Key(CatalogSearchKind kind = CatalogSearchKind.Project, string? resource = null) =>
        new(kind, new ProjectScope(Organization, Project), kind > CatalogSearchKind.Project ? Mapping : null,
            kind > CatalogSearchKind.Mapping ? Revision : null, resource,
            kind is CatalogSearchKind.Field or CatalogSearchKind.Rule ? Revision : null);

    [Theory]
    [InlineData("")]
    [InlineData(" \t ")]
    [InlineData("order\nnumber")]
    [InlineData("order\0number")]
    public void EmptyAndControlQueriesAreRefused(string text) =>
        Assert.Throws<ArgumentException>(() => new CatalogSearchQuery(text));

    [Fact]
    public void LiteralQueriesAreBoundedByUtf8BytesAndNotInterpreted()
    {
        Assert.Equal("%_[]'", new CatalogSearchQuery("  %_[]'  ").Text);
        Assert.Equal(new CatalogSearchQuery("Orders").Fingerprint, new CatalogSearchQuery(" Orders ").Fingerprint);
        Assert.NotEqual(new CatalogSearchQuery("Orders").Fingerprint, new CatalogSearchQuery("orders").Fingerprint);
        _ = new CatalogSearchQuery(new string('é', 128));
        Assert.Throws<ArgumentException>(() => new CatalogSearchQuery(new string('é', 129)));
        Assert.Throws<ArgumentException>(() => new CatalogSearchQuery("\ud800"));
    }

    [Fact]
    public void ResourceKeysRequireCompleteExactAncestry()
    {
        foreach (var kind in Enum.GetValues<CatalogSearchKind>())
            _ = Key(kind, kind is CatalogSearchKind.Field or CatalogSearchKind.Rule ? "resource" : null);
        Assert.Throws<ArgumentException>(() => new CatalogSearchKey(CatalogSearchKind.Field, new(Organization, Project), Mapping, null, "field"));
        Assert.Throws<ArgumentException>(() => new CatalogSearchKey(CatalogSearchKind.Mapping, new(Organization, Project), Mapping, Revision));
        Assert.Throws<ArgumentException>(() => new CatalogSearchKey(CatalogSearchKind.Project, new(Organization, Project), Mapping));
        Assert.Throws<ArgumentException>(() => Key(CatalogSearchKind.Rule, ""));
    }

    [Fact]
    public void MappingHitsRequireTheirObservedHeadButKeepTheLogicalResourceIdentity()
    {
        var key = Key(CatalogSearchKind.Mapping);
        Assert.Throws<ArgumentException>(() => new CatalogSearchHit(key, new("Mapping"), new("")));
        var hit = new CatalogSearchHit(key, new("Mapping"), new(""), Revision);
        Assert.Null(hit.Key.RevisionId);
        Assert.Equal(Revision, hit.TargetRevisionId);
        Assert.Throws<ArgumentException>(() => new CatalogSearchHit(Key(), new("Project"), new(""), Revision));
    }

    [Fact]
    public void HighlightsNeverSplitScalarsOverlapOrEscapeTheirText()
    {
        var value = new CatalogSearchText("A😀B<script>", [new(1, 2), new(4, 8)]);
        Assert.Equal("A😀B<script>", value.Text);
        Assert.Throws<ArgumentException>(() => new CatalogSearchText("A😀B", [new(1, 1)]));
        Assert.Throws<ArgumentException>(() => new CatalogSearchText("abcd", [new(0, 2), new(1, 1)]));
        Assert.Throws<ArgumentException>(() => new CatalogSearchText("abcd", [new(4, 1)]));
        Assert.Throws<ArgumentException>(() => new CatalogSearchText("abcd", [new(-1, 1)]));
        Assert.Throws<ArgumentException>(() => new CatalogSearchText("abcd", [new(1, int.MaxValue)]));
    }

    [Fact]
    public void ContinuationsBindQueryAndOrganizationAndPagesRejectDuplicates()
    {
        var query = new CatalogSearchQuery("order");
        var hit = new CatalogSearchHit(Key(), new("Orders"), new("Order project"));
        var admission = new CatalogOrganizationAdmission(Organization, new PrincipalId(Guid.NewGuid()), "Workspace");
        var page = new CatalogSearchPage(admission, query, [hit], hasMore: true);
        Assert.NotNull(page.NextCursor);
        page.NextCursor.RequireContext(Organization, query);
        Assert.Throws<ArgumentException>(() => page.NextCursor.RequireContext(Guid.NewGuid(), query));
        Assert.Throws<ArgumentException>(() => page.NextCursor.RequireContext(Organization, new("invoice")));
        Assert.Throws<ArgumentException>(() => new CatalogSearchPage(admission, query, [hit, hit], hasMore: false));
        Assert.Throws<ArgumentException>(() => new CatalogSearchPage(admission, query, [], hasMore: true));
    }

    [Fact]
    public void ResourceOrderingMatchesBytewiseDatabaseOrdering()
    {
        var earlier = Key(CatalogSearchKind.Field, "\ue000");
        var later = Key(CatalogSearchKind.Field, "😀");
        Assert.True(earlier.CompareTo(later) < 0);
        Assert.True(Key().CompareTo(Key(CatalogSearchKind.Mapping)) < 0);
    }
}
