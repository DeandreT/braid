using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Weft.Contracts.Editing;
using Weft.Domain;

namespace Weft.Application;

/// <summary>Owns a bounded closed-schema operation log, not native mapping JSON.</summary>
public sealed class MappingEditScript
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        AllowOutOfOrderMetadataProperties = true,
        MaxDepth = 64,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private readonly byte[] _bytes;
    private MappingEditScript(byte[] bytes, int count) { _bytes = bytes; Count = count; Digest = Sha256Digest.FromBytes(bytes); }
    public int Count { get; }
    public Sha256Digest Digest { get; }
    public byte[] CopyUtf8() => (byte[])_bytes.Clone();
    public EditScriptV1 CopyDocument() => JsonSerializer.Deserialize<EditScriptV1>(_bytes, Options)!;
    public static MappingEditScript Empty { get; } = Parse("{\"format\":\"ferrule.edit.v1\",\"operations\":[]}"u8);

    public static MappingEditScript Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 1 or > MappingEditPolicy.MaximumOperationsBytes)
            throw new ArgumentException("The operation log must be at most 1 MiB.", nameof(bytes));
        try
        {
            // System.Text.Json otherwise accepts duplicate members. Check these
            // before typed deserialization so no discarded intent can survive admission.
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 64 });
            var objects = new Stack<HashSet<string>?>();
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject: objects.Push(new(StringComparer.Ordinal)); break;
                    case JsonTokenType.StartArray: objects.Push(null); break;
                    case JsonTokenType.EndObject or JsonTokenType.EndArray: objects.Pop(); break;
                    case JsonTokenType.PropertyName:
                        if (!objects.Peek()!.Add(reader.GetString()!)) throw new JsonException("Duplicate member.");
                        break;
                }
            }
            var script = JsonSerializer.Deserialize<EditScriptV1>(bytes, Options) ?? throw new JsonException();
            if (script.Format != "ferrule.edit.v1" || script.Operations is null || script.Operations.Count > MappingEditPolicy.MaximumOperations
                || script.Operations.Any(operation => operation is null)) throw new JsonException("Unknown format or operation bound.");
            var canonical = JsonSerializer.SerializeToUtf8Bytes(script, Options);
            if (canonical.Length > MappingEditPolicy.MaximumOperationsBytes) throw new JsonException("Canonical operation log exceeds 1 MiB.");
            using var document = JsonDocument.Parse(canonical);
            Validate(document.RootElement);
            return new(canonical, script.Operations.Count);
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or InvalidOperationException or OverflowException)
        {
            throw new ArgumentException("The log must match the closed ferrule.edit.v1 schema.", nameof(bytes), error);
        }
    }

    private static void Validate(JsonElement value, string? member = null)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject()) Validate(property.Value, property.Name);
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Null) throw new JsonException("Array elements cannot be null.");
                Validate(item, member);
            }
        }
        else if (member is "id" or "node" or "input" or "index" or "children" or "args" or "nodes" or "replacements" or "replacement" or "item"
            || member is "from" or "to" && value.ValueKind == JsonValueKind.String)
        {
            if (value.ValueKind == JsonValueKind.Null) return;
            var text = value.GetString();
            if (text is null || !uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                || id.ToString(CultureInfo.InvariantCulture) != text) throw new JsonException("Identities must be canonical u32 decimal strings.");
        }
    }
}
