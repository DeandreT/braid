using System.Text;
using System.Text.Json.Nodes;
using Braid.Application;

namespace Braid.Tests;

internal sealed record ReleasedDocumentSample(string Format, string Media, byte[] Mapping, byte[] Input, byte[] Expected,
    byte[] Malformed, byte[] Longer, CsvDocumentDialect? Dialect)
{
    public static ReleasedDocumentSample Delimited(string delimiter)
    {
        var dialect = BuildCsvContractTests.Dialect(delimiter);
        var mapping = JsonNode.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "FerruleFixtures", "csv-flat.native.json")))!;
        foreach (var side in new[] { "source_options", "target_options" })
        {
            mapping[side]!["delimiter"] = delimiter;
            mapping[side]!["has_header_row"] = false;
            mapping[side]!["csv_preserve_empty_strings"] = true;
        }
        var row = Encoding.UTF8.GetBytes($"A{delimiter}B\n");
        return new(delimiter == "," ? "csv" : "tsv", CsvDialectJson.MediaType(dialect), Encoding.UTF8.GetBytes(mapping.ToJsonString()),
            row, row, [0xff], Encoding.UTF8.GetBytes($"A{delimiter}BB\n"), dialect);
    }

    public static ReleasedDocumentSample X12()
    {
        var mapping = JsonNode.Parse(DocumentPreviewTestData.Native("x12"))!;
        foreach (var side in new[] { "source", "target" })
        {
            var segments = mapping[side]!["kind"]!["children"]!.AsArray();
            segments.Single(n => (string?)n!["name"] == "ISA")!["kind"]!["children"]![11]!["fixed"] = "00501";
            segments.Single(n => (string?)n!["name"] == "GS")!["kind"]!["children"]![7]!["fixed"] = "005010";
        }
        const string wire = DocumentPreviewTestData.X12;
        return new("x12", "application/edi-x12", Encoding.UTF8.GetBytes(mapping.ToJsonString()), Encoding.ASCII.GetBytes(wire),
            Encoding.ASCII.GetBytes(wire.Replace("~", "~\n", StringComparison.Ordinal)),
            Encoding.ASCII.GetBytes(wire.Replace("SE*3*0330", "SE*3*0331", StringComparison.Ordinal)),
            Encoding.ASCII.GetBytes(wire.Replace("BRAID-330", "BRAID-3300", StringComparison.Ordinal)), null);
    }
}
