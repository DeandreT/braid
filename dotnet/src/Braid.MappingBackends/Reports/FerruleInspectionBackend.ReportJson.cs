using System.Text.Json;
using Braid.Domain;

namespace Braid.MappingBackends;

public sealed partial class FerruleInspectionBackend
{
    private static void PreflightReportJson(ReadOnlySpan<byte> json, RevisionReportPolicy limits, CancellationToken token)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = InspectionPolicy.MaximumJsonDepth + 1 });
        var objects = new Stack<HashSet<string>>();
        var values = 0;
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (reader.TokenType is not JsonTokenType.PropertyName and not JsonTokenType.EndObject and not JsonTokenType.EndArray)
            {
                var depth = reader.CurrentDepth + (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray ? 1 : 0);
                if (++values > limits.ResponseItemLimit || depth > limits.JsonDepthLimit) throw new ProjectionLimitException();
            }
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject: objects.Push(new(StringComparer.Ordinal)); break;
                case JsonTokenType.EndObject: objects.Pop(); break;
                case JsonTokenType.PropertyName:
                    Require(reader.ValueSpan.Length <= 128);
                    var properties = objects.Peek();
                    Require(properties.Count < 32 && properties.Add(reader.GetString()!));
                    break;
                case JsonTokenType.String:
                    // Reply context identifiers have independent fixed bounds; report/user text uses its effective cap.
                    // This hard raw ceiling prevents an oversized escaped token from allocating before typed checks.
                    if (reader.ValueSpan.Length > InspectionPolicy.MaximumTextBytes * 6) throw new ProjectionLimitException();
                    break;
            }
        }
        token.ThrowIfCancellationRequested();
    }
}
