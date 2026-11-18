using System.Text;
using System.Text.Json;
using Braid.Domain;

namespace Braid.Deployment.Triggers;

/// <summary>One keyed order of a batch: its stable explicit order key and exact input bytes.</summary>
public sealed class TriggerBatchOrder
{
    private readonly byte[] content;
    internal TriggerBatchOrder(string key, byte[] content) { Key = key; this.content = content; }
    public string Key { get; }
    public int Length => content.Length;
    public byte[] CopyContent() => (byte[])content.Clone();
    public override string ToString() => nameof(TriggerBatchOrder);
}

/// <summary>
/// The explicit batch envelope: <c>{"schema":"braid.trigger-batch.v1","orders":[{"key":"...","content":"base64"}]}</c> with exactly
/// these members. Keys are unique, exact and case-sensitive; each order is bounded by the trigger's message size. The complete
/// bounded manifest is materialized before anything is admitted, and nothing else is ever split.
/// </summary>
public static class TriggerBatchEnvelope
{
    public const string Schema = "braid.trigger-batch.v1";
    public const string MediaType = "application/vnd.braid.trigger-batch+json";
    public const int MaximumOrders = 256;
    public const int MaximumEnvelopeBytes = 16 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>The orders in envelope order, or null when the envelope is not exactly this contract.</summary>
    public static IReadOnlyList<TriggerBatchOrder>? Parse(ReadOnlySpan<byte> envelope, int maximumOrderBytes)
    {
        if (envelope.Length > MaximumEnvelopeBytes) return null;
        try
        {
            using var document = JsonDocument.Parse(envelope.ToArray(), new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !Members(root, "schema", "orders") || root.GetProperty("schema").GetString() != Schema ||
                root.GetProperty("orders") is not { ValueKind: JsonValueKind.Array } orders || orders.GetArrayLength() is < 1 or > MaximumOrders)
                return null;
            var result = new List<TriggerBatchOrder>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var order in orders.EnumerateArray())
            {
                if (order.ValueKind != JsonValueKind.Object || !Members(order, "key", "content") ||
                    order.GetProperty("key") is not { ValueKind: JsonValueKind.String } keyElement ||
                    order.GetProperty("content") is not { ValueKind: JsonValueKind.String } contentElement)
                    return null;
                var key = keyElement.GetString()!;
                if (key.Length == 0 || StrictUtf8.GetByteCount(key) > QueueOrderIdentity.MaximumChildKeyBytes || !keys.Add(key)) return null;
                var content = contentElement.GetBytesFromBase64();
                if (content.Length > maximumOrderBytes) return null;
                result.Add(new(key, content));
            }
            return result;
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException or EncoderFallbackException) { return null; }
    }

    /// <summary>The digest of the complete child manifest: every key and its exact content digest, in envelope order.</summary>
    public static Sha256Digest ManifestDigest(IReadOnlyList<TriggerBatchOrder> orders)
    {
        ArgumentNullException.ThrowIfNull(orders);
        var manifest = new StringBuilder();
        foreach (var order in orders)
            manifest.Append(Convert.ToBase64String(StrictUtf8.GetBytes(order.Key))).Append(' ').Append(Sha256Digest.FromBytes(order.CopyContent()).Value).Append('\n');
        return Sha256Digest.FromBytes(Encoding.ASCII.GetBytes(manifest.ToString()));
    }

    private static bool Members(JsonElement value, params string[] names)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!names.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name)) return false;
        return seen.Count == names.Length;
    }
}
