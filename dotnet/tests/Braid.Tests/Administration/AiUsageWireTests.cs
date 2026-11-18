using System.Text.Json;
using Braid.Contracts.Administration;

namespace Braid.Tests;

public sealed class AiUsageWireTests
{
    [Fact]
    public void Counts_larger_than_JavaScript_integer_precision_remain_exact_decimal_strings()
    {
        var value = new AiUsageTotalsV1("9007199254740993", "0", "0", "0", "0", "0", "0", "0", "0", "0", "0", "0", "0", []);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(JsonValueKind.String, document.RootElement.GetProperty("turns").ValueKind);
        Assert.Equal("9007199254740993", document.RootElement.GetProperty("turns").GetString());
    }

    [Fact]
    public void Unknown_totals_stay_null_and_known_subtotals_are_distinct_strings()
    {
        var value = new AiUsageTotalsV1("1", "1", "0", "0", null, null, null, "0", "0", "0", "1", "1", "1", []);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("totalTokens").ValueKind);
        Assert.Equal("0", document.RootElement.GetProperty("knownTotalTokens").GetString());
        Assert.Equal("1", document.RootElement.GetProperty("unknownTotalRounds").GetString());
    }
}
