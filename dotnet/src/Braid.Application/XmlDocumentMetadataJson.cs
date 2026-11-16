using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Braid.Application;

/// <summary>
/// Closed canonical metadata codec. Every occurrence reserves its property name, colon and one separator byte.
/// The complete operation shares one ledger; repeated metadata is never deduplicated.
/// </summary>
public static class XmlDocumentMetadataJson
{
    private const int PropertyOverhead = 15; // UTF-8 length of "\"xmlMetadata\":" plus a reserved separator.
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static int MeasureOperation(IReadOnlyList<XmlDocumentMetadata> inputs,
        IReadOnlyList<XmlDocumentMetadata> outputs, int maximumEncodedBytes = XmlDocumentPolicy.MaximumMetadataBytes)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(outputs);
        CheckBudget(maximumEncodedBytes);
        if (inputs.Count is < 1 or > XmlDocumentPolicy.MaximumInputs ||
            outputs.Count is < 1 or > XmlDocumentPolicy.MaximumOutputs)
            throw new XmlDocumentException(XmlDocumentFailure.DocumentCountLimit);
        using var counter = new CappedStream(maximumEncodedBytes);
        foreach (var input in inputs) Count(counter, input, XmlDocumentRole.Input);
        foreach (var output in outputs) Count(counter, output, XmlDocumentRole.Output);
        return counter.Count;
    }

    public static int MeasureProperty(XmlDocumentMetadata value, XmlDocumentRole role,
        int maximumEncodedBytes = XmlDocumentPolicy.MaximumMetadataBytes)
    {
        CheckBudget(maximumEncodedBytes);
        using var counter = new CappedStream(maximumEncodedBytes);
        Count(counter, value, role);
        return counter.Count;
    }

    public static string Encode(XmlDocumentMetadata value, XmlDocumentRole role)
        => StrictUtf8.GetString(EncodeBytes(value, role));

    /// <summary>Uses canonical raw JSON even when the enclosing writer has a different escaping policy.</summary>
    public static void Write(Utf8JsonWriter json, XmlDocumentMetadata value, XmlDocumentRole role)
    {
        ArgumentNullException.ThrowIfNull(json);
        json.WriteRawValue(EncodeBytes(value, role), skipInputValidation: true);
    }

    public static XmlDocumentMetadata Decode(string value, XmlDocumentRole role)
    {
        ArgumentNullException.ThrowIfNull(value);
        try
        {
            if (value.Length > XmlDocumentPolicy.MaximumMetadataBytes - PropertyOverhead ||
                StrictUtf8.GetByteCount(value) > XmlDocumentPolicy.MaximumMetadataBytes - PropertyOverhead)
                throw new XmlDocumentException(XmlDocumentFailure.MetadataTooLarge);
            using var document = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 8 });
            return Read(document.RootElement, role);
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or EncoderFallbackException)
        {
            throw Invalid();
        }
    }

    public static XmlDocumentMetadata Read(JsonElement value, XmlDocumentRole role)
    {
        try
        {
            RequireFields(value, "schema", "safetyProfile", "schemaHints");
            var hints = value.GetProperty("schemaHints");
            var metadata = new XmlDocumentMetadata(Text(value.GetProperty("schema")), Text(value.GetProperty("safetyProfile")),
                hints.ValueKind == JsonValueKind.Null ? null : ReadHints(hints));
            MeasureProperty(metadata, role);
            return metadata;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            throw Invalid();
        }
    }

    private static XmlSchemaHints ReadHints(JsonElement value)
    {
        RequireFields(value, "noNamespaceLocation", "locations");
        var noNamespace = value.GetProperty("noNamespaceLocation");
        var locations = value.GetProperty("locations");
        if (locations.ValueKind != JsonValueKind.Array || locations.GetArrayLength() > XmlDocumentPolicy.MaximumSchemaHintPairs)
            throw Invalid();
        var pairs = new List<XmlSchemaLocation>(locations.GetArrayLength());
        foreach (var pair in locations.EnumerateArray())
        {
            RequireFields(pair, "namespace", "location");
            pairs.Add(new(Text(pair.GetProperty("namespace")), Text(pair.GetProperty("location"))));
        }
        return new(noNamespace.ValueKind == JsonValueKind.Null ? null : Text(noNamespace), pairs);
    }

    private static string Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString()! : throw Invalid();

    private static void RequireFields(JsonElement value, params string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid();
        var remaining = new HashSet<string>(fields, StringComparer.Ordinal);
        foreach (var field in value.EnumerateObject())
            if (!remaining.Remove(field.Name)) throw Invalid();
        if (remaining.Count != 0) throw Invalid();
    }

    private static byte[] EncodeBytes(XmlDocumentMetadata value, XmlDocumentRole role)
    {
        MeasureProperty(value, role);
        using var buffer = new MemoryStream();
        using (var bounded = new CappedStream(XmlDocumentPolicy.MaximumMetadataBytes - PropertyOverhead, buffer))
            WriteCanonical(bounded, value);
        return buffer.ToArray();
    }

    private static void Count(CappedStream counter, XmlDocumentMetadata value, XmlDocumentRole role)
    {
        if (!XmlDocumentPolicy.Admits(value, role)) throw Invalid();
        counter.Charge(PropertyOverhead);
        WriteCanonical(counter, value);
    }

    private static void WriteCanonical(Stream destination, XmlDocumentMetadata value)
    {
        using var json = new Utf8JsonWriter(destination, new JsonWriterOptions { Encoder = JavaScriptEncoder.Default });
        json.WriteStartObject();
        json.WriteString("schema", value.Schema);
        json.WriteString("safetyProfile", value.SafetyProfile);
        json.WritePropertyName("schemaHints");
        if (value.SchemaHints is not { } hints) json.WriteNullValue();
        else
        {
            json.WriteStartObject();
            json.WriteString("noNamespaceLocation", hints.NoNamespaceLocation);
            json.Flush();
            json.WriteStartArray("locations");
            foreach (var pair in hints.Locations)
            {
                json.WriteStartObject();
                json.WriteString("namespace", pair.Namespace);
                json.Flush();
                json.WriteString("location", pair.Location);
                json.WriteEndObject();
                json.Flush();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        json.WriteEndObject();
        json.Flush();
    }

    private static void CheckBudget(int maximumEncodedBytes)
    {
        if (maximumEncodedBytes is <= 0 or > XmlDocumentPolicy.MaximumMetadataBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumEncodedBytes));
    }

    private static XmlDocumentException Invalid() => new(XmlDocumentFailure.InvalidMetadata);

    /// <summary>Counting keeps no payload; an optional destination is capped by the identical write path.</summary>
    private sealed class CappedStream(int maximumBytes, Stream? destination = null) : Stream
    {
        public int Count { get; private set; }
        public void Charge(int bytes)
        {
            if (bytes < 0 || bytes > maximumBytes - Count)
                throw new XmlDocumentException(XmlDocumentFailure.MetadataTooLarge, observed: (long)maximumBytes + 1, limit: maximumBytes);
            Count = checked(Count + bytes);
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Charge(buffer.Length);
            destination?.Write(buffer);
        }
        public override void Flush() => destination?.Flush();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Count;
        public override long Position { get => Count; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
