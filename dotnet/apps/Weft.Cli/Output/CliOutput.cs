using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Weft.Cli.Http;

namespace Weft.Cli.Output;

/// <summary>
/// Human tables and fields by default, or indented JSON of the exact response contract with <c>--json</c>.
/// Text from the server is shown with control characters replaced, so stored names cannot drive the terminal.
/// </summary>
internal sealed class CliOutput(TextWriter writer, bool json)
{
    // Browser-session request tokens are credentials for the cookie boundary; they never reach output.
    private static readonly HashSet<string> Withheld = new(["requestToken"], StringComparer.Ordinal);
    private static readonly JsonSerializerOptions Indented = new(WeftApiClient.Json) { WriteIndented = true };

    public bool Json => json;
    public TextWriter Writer => writer;

    /// <summary>Writes <paramref name="value"/> as JSON in JSON mode, otherwise runs <paramref name="human"/>.</summary>
    public void Write<T>(T value, Action<CliOutput> human)
    {
        if (json)
            WriteJson(value);
        else
            human(this);
    }

    public void WriteJson<T>(T value)
    {
        var node = JsonSerializer.SerializeToNode(value, WeftApiClient.Json);
        Strip(node);
        writer.WriteLine(node is null ? "null" : node.ToJsonString(Indented));
    }

    public void Fields(params (string Name, object? Value)[] fields)
    {
        var width = fields.Max(field => field.Name.Length);
        foreach (var (name, value) in fields)
            writer.WriteLine(name.PadRight(width) + "  " + Show(value));
    }

    public void Table(IReadOnlyList<string> columns, IEnumerable<IReadOnlyList<object?>> rows, string? empty = null)
    {
        var cells = rows.Select(row => row.Select(Show).ToArray()).ToList();
        if (cells.Count == 0)
        {
            writer.WriteLine(empty ?? "(none)");
            return;
        }
        var widths = columns.Select((column, index) => Math.Max(column.Length, cells.Max(row => row[index].Length))).ToArray();
        writer.WriteLine(Row(columns, widths));
        foreach (var row in cells)
            writer.WriteLine(Row(row, widths));
    }

    public void Line(string text = "") => writer.WriteLine(text);

    /// <summary>Server-supplied text with control and bidirectional-override characters replaced.</summary>
    internal static string Safe(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        var builder = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
            builder.Append(Rune.IsControl(rune) || rune.Value is >= 0x202A and <= 0x202E or >= 0x2066 and <= 0x2069
                ? "�" : rune.ToString());
        return builder.ToString();
    }

    private static string Show(object? value) => value switch
    {
        null => "-",
        bool flag => flag ? "yes" : "no",
        DateTimeOffset time => time.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        Enum item => JsonNamingPolicy.CamelCase.ConvertName(item.ToString()),
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
        _ => Safe(value.ToString())
    };

    private static string Row(IReadOnlyList<string> cells, int[] widths)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < cells.Count; i++)
        {
            if (i > 0)
                builder.Append("  ");
            builder.Append(i == cells.Count - 1 ? cells[i] : cells[i].PadRight(widths[i]));
        }
        return builder.ToString();
    }

    private static void Strip(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject item:
                foreach (var name in item.Select(property => property.Key).Where(Withheld.Contains).ToArray())
                    item.Remove(name);
                foreach (var property in item)
                    Strip(property.Value);
                break;
            case JsonArray list:
                foreach (var element in list)
                    Strip(element);
                break;
        }
    }
}
