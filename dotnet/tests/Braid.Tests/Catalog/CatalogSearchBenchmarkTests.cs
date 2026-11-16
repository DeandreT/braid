using System.Text.Json;
using Npgsql;
using Xunit.Abstractions;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
public sealed class CatalogSearchBenchmarkTests(PostgresFixture fixture, ITestOutputHelper output)
{
    [PostgresFact]
    public async Task RealisticSyntheticCatalogUsesTrigramIndexesWithSequentialNegativeControls()
    {
        await using var test = await fixture.CreateCaseAsync();
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "tools/catalog-search-benchmark.sql"))) root = root.Parent;
        Assert.NotNull(root);
        var sql = await File.ReadAllTextAsync(Path.Combine(root.FullName, "tools/catalog-search-benchmark.sql"));
        await using var connection = await test.Owner.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 180 };
        var plans = new List<string>();
        await using (var rows = await command.ExecuteReaderAsync())
        {
            do
            {
                while (await rows.ReadAsync()) if (rows.FieldCount == 1 && rows.GetName(0) == "QUERY PLAN") plans.Add(rows.GetString(0));
            } while (await rows.NextResultAsync());
        }
        Assert.Equal(4, plans.Count);
        Assert.Contains("search_benchmark_mapping_trgm", plans[0], StringComparison.Ordinal);
        Assert.Contains("search_benchmark_reports_trgm", plans[1], StringComparison.Ordinal);
        Assert.DoesNotContain("Bitmap Index Scan", plans[2], StringComparison.Ordinal);
        Assert.DoesNotContain("Bitmap Index Scan", plans[3], StringComparison.Ordinal);
        for (var index = 0; index < plans.Count; index++)
        {
            using var plan = JsonDocument.Parse(plans[index]);
            output.WriteLine($"Plan {index + 1}: {plan.RootElement[0].GetProperty("Execution Time").GetDouble()} ms");
            output.WriteLine(plans[index]);
        }
    }
}
