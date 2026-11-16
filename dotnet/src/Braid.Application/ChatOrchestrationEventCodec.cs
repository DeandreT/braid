using System.Text;
using System.Text.Json;
using Braid.Domain;

namespace Braid.Application;

/// <summary>Private TextDelta bodies use a canonical envelope so whitespace and escaped NUL survive
/// the ledger's nonblank-text invariant. Public delivery must decode, not display this envelope.</summary>
public static class ChatOrchestrationEventCodec
{
    public static string ReadText(string json)
    {
        try
        {
            ChatBounds.Text(json, 8192, false);
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
            var text = document.RootElement.GetProperty("text").GetString()!;
            if (text.Length == 0 || WriteText(text) != json) throw ChatBounds.Invalid();
            return text;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or NullReferenceException)
        { throw ChatBounds.Invalid(); }
    }

    internal static string WriteText(string text) => ChatCanonicalJson.Write(w =>
    { w.WriteStartObject(); w.WriteNumber("v", 1); w.WriteString("text", text); w.WriteEndObject(); }, 8192);

    internal static IEnumerable<string> TextPieces(string text)
    {
        var buffer = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            // At most 1024 UTF-16 code units, at most 6144 JSON-escaped bytes plus envelope.
            if (buffer.Length + rune.Utf16SequenceLength > 1024)
            { yield return WriteText(buffer.ToString()); buffer.Clear(); }
            buffer.Append(rune.ToString());
        }
        if (buffer.Length > 0) yield return WriteText(buffer.ToString());
    }
}
