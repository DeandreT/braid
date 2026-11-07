using System.Text;

namespace Weft.Application;

/// <summary>Matches PostgreSQL lower(text COLLATE "C"): ASCII case folds; other Unicode is literal.</summary>
public static class CatalogSearchSnippets
{
    public static string Fold(string text) => string.Create(text.Length, text, static (result, source) =>
    {
        for (var index = 0; index < result.Length; index++)
            result[index] = source[index] is >= 'A' and <= 'Z' ? (char)(source[index] + 32) : source[index];
    });

    public static CatalogSearchText Create(string text, CatalogSearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(query);
        var needle = Fold(query.Text);
        var match = Fold(text).IndexOf(needle, StringComparison.Ordinal);
        // A bounded context around the first match, with complete Unicode scalars.
        var start = Math.Max(0, match - 160);
        if (start > 0 && char.IsLowSurrogate(text[start])) start--;
        var end = Math.Min(text.Length, start + 900);
        if (end < text.Length && char.IsLowSurrogate(text[end])) end--;
        var snippet = (start > 0 ? "…" : "") + text[start..end] + (end < text.Length ? "…" : "");
        var folded = Fold(snippet);
        var spans = new List<CatalogSearchHighlight>();
        var offset = 0;
        while (spans.Count < 64 && (offset = folded.IndexOf(needle, offset, StringComparison.Ordinal)) >= 0)
        {
            spans.Add(new(offset, query.Text.Length));
            offset += query.Text.Length;
        }
        return new(snippet, spans);
    }
}
