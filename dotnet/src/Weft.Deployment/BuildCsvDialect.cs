using System.Text.Json;
using Weft.Application;

namespace Weft.Deployment;

/// <summary>The canonical CSV boundary metadata used by build intents, replay digests and endpoint receipts.</summary>
public static class BuildCsvDialect
{
    public static void Write(Utf8JsonWriter json, CsvDocumentDialect dialect) => CsvDialectJson.Write(json, dialect);

    public static CsvDocumentDialect? ReadOptional(JsonElement value)
    {
        if (!value.TryGetProperty("csvDialect", out var dialect)) return null;
        return CsvDialectJson.Read(dialect);
    }
}
