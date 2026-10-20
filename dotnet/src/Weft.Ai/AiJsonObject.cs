using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Weft.Ai;

public sealed class AiJsonObject
{
    private AiJsonObject(string json, int bytes) { Json = json; ByteCount = bytes; }
    public string Json { get; }
    public int ByteCount { get; }
    public static AiJsonObject CopyFrom(string json)
    {
        int bytes = AiGuard.Bytes(json, AiCeilings.ResultBytes, false);
        try
        {
            using JsonDocument document = AiJson.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw AiGuard.Invalid();
            return new AiJsonObject(json, bytes);
        }
        catch (Exception exception) when (exception is JsonException or AiProtocolException or ArgumentException)
        { throw AiGuard.Invalid(); }
    }
    internal static AiJsonObject ParseArguments(string json)
    {
        int bytes = AiGuard.Utf8.GetByteCount(json);
        if (bytes > AiCeilings.ArgumentBytes) throw AiGuard.Protocol(AiFailureCode.BudgetExceeded);
        using JsonDocument document = AiJson.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw AiGuard.Protocol();
        return new AiJsonObject(json, bytes);
    }
    public override string ToString() => nameof(AiJsonObject);
}

internal static class AiJson
{
    internal static JsonDocument Parse(string json)
    {
        try
        {
            AiGuard.Bytes(json, AiCeilings.JsonBytes, false);
            CheckEscapedScalars(json);
            byte[] utf8 = AiGuard.Utf8.GetBytes(json);
            try
            {
                var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
                { MaxDepth = AiCeilings.JsonDepth + 1, CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
                var names = new Stack<HashSet<string>>();
                var containers = new Stack<JsonTokenType>();
                int tokens = 0, elements = 0;
                while (reader.Read())
                {
                    if (++tokens > AiCeilings.JsonTokens) throw AiGuard.Protocol(AiFailureCode.BudgetExceeded);
                    JsonTokenType type = reader.TokenType;
                    if ((type is JsonTokenType.StartObject or JsonTokenType.StartArray) && reader.CurrentDepth >= AiCeilings.JsonDepth)
                        throw AiGuard.Protocol(AiFailureCode.BudgetExceeded);
                    if (type is JsonTokenType.EndObject or JsonTokenType.EndArray)
                    {
                        containers.Pop();
                        if (type == JsonTokenType.EndObject) names.Pop();
                        continue;
                    }
                    if (type == JsonTokenType.PropertyName)
                    {
                        if (++elements > AiCeilings.JsonElements) throw AiGuard.Protocol(AiFailureCode.BudgetExceeded);
                        if (!names.Peek().Add(reader.GetString()!)) throw AiGuard.Protocol();
                    }
                    else if (containers.Count > 0 && containers.Peek() == JsonTokenType.StartArray)
                    {
                        if (++elements > AiCeilings.JsonElements) throw AiGuard.Protocol(AiFailureCode.BudgetExceeded);
                    }
                    if (type is JsonTokenType.StartObject or JsonTokenType.StartArray)
                    {
                        containers.Push(type);
                        if (type == JsonTokenType.StartObject) names.Push(new HashSet<string>(StringComparer.Ordinal));
                    }
                }
                return JsonDocument.Parse(json, new JsonDocumentOptions
                { MaxDepth = AiCeilings.JsonDepth, CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
            }
            finally { Array.Clear(utf8, 0, utf8.Length); }
        }
        catch (AiProtocolException) { throw; }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        { throw AiGuard.Protocol(); }
    }

    // System.Text.Json may replace lone escaped surrogates in GetString. Refuse
    // those lexemes before any decoded property name or string is obtained.
    private static void CheckEscapedScalars(string json)
    {
        bool quoted = false;
        for (int i = 0; i < json.Length; i++)
        {
            char c = json[i];
            if (!quoted) { if (c == '"') quoted = true; continue; }
            if (c == '"') { quoted = false; continue; }
            if (c != '\\') continue;
            if (++i >= json.Length) throw AiGuard.Protocol();
            if (json[i] != 'u') continue;
            int scalar = ReadHex(json, i + 1);
            i += 4;
            if (scalar is >= 0xDC00 and <= 0xDFFF) throw AiGuard.Protocol();
            if (scalar is not (>= 0xD800 and <= 0xDBFF)) continue;
            if (i + 6 >= json.Length || json[i + 1] != '\\' || json[i + 2] != 'u') throw AiGuard.Protocol();
            int low = ReadHex(json, i + 3);
            if (low is not (>= 0xDC00 and <= 0xDFFF)) throw AiGuard.Protocol();
            i += 6;
        }
    }
    private static int ReadHex(string text, int start)
    {
        if (start > text.Length - 4) throw AiGuard.Protocol();
        int value = 0;
        for (int n = 0; n < 4; n++)
        {
            char c = text[start + n];
            int digit = c is >= '0' and <= '9' ? c - '0' : c is >= 'a' and <= 'f' ? c - 'a' + 10 : c is >= 'A' and <= 'F' ? c - 'A' + 10 : -1;
            if (digit < 0) throw AiGuard.Protocol();
            value = (value << 4) | digit;
        }
        return value;
    }
    internal static string? OptionalString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw AiGuard.Protocol();
        return value.GetString();
    }
    internal static string RequiredString(JsonElement parent, string name) => OptionalString(parent, name) ?? throw AiGuard.Protocol();
    internal static long? Count(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long count) || count is < 0 or > 1_000_000_000_000) throw AiGuard.Protocol();
        return count;
    }
    internal static int Index(JsonElement value)
    {
        if (!value.TryGetInt32(out int index) || index is < 0 or >= AiCeilings.Calls) throw AiGuard.Protocol();
        return index;
    }
}
