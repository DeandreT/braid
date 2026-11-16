using System.Globalization;
using System.Text.Json;

namespace Braid.Application;

internal sealed partial class RevisionReportEnvelopeCodec
{
    private delegate void RawRule(JsonElement value, CancellationToken cancellationToken);

    // This is an independently declared stored-wire matrix, including defaulted and nullable members.
    private static void Object(JsonElement value, CancellationToken cancellationToken,
        params (string Name, RawRule Rule)[] fields)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (value.ValueKind != JsonValueKind.Object) throw Integrity();
        var count = 0;
        foreach (var property in value.EnumerateObject())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var found = false;
            foreach (var field in fields)
            {
                if (property.Name != field.Name) continue;
                found = true;
                field.Rule(property.Value, cancellationToken);
                break;
            }
            if (!found) throw Integrity();
            count++;
        }
        if (count != fields.Length) throw Integrity();
        foreach (var field in fields)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!value.TryGetProperty(field.Name, out _)) throw Integrity();
        }
    }

    private static RawRule Nullable(RawRule rule) => (value, token) =>
    {
        token.ThrowIfCancellationRequested();
        if (value.ValueKind != JsonValueKind.Null) rule(value, token);
    };

    private static RawRule ArrayOf(RawRule rule) => (value, token) =>
    {
        token.ThrowIfCancellationRequested();
        if (value.ValueKind != JsonValueKind.Array) throw Integrity();
        foreach (var item in value.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            rule(item, token);
        }
    };

    private static void String(JsonElement value, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (value.ValueKind != JsonValueKind.String) throw Integrity();
    }

    private static void Boolean(JsonElement value, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (value.ValueKind is not JsonValueKind.True and not JsonValueKind.False) throw Integrity();
    }

    private static void Integer(JsonElement value, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var integer) ||
            value.GetRawText() != integer.ToString(CultureInfo.InvariantCulture))
            throw Integrity();
    }

    private static RawRule OneOf(params string[] choices) => (value, token) =>
    {
        String(value, token);
        var text = value.GetString();
        if (!choices.Contains(text, StringComparer.Ordinal)) throw Integrity();
    };

    private static string Kind(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("kind", out var kind) ||
            kind.ValueKind != JsonValueKind.String) throw Integrity();
        return kind.GetString() ?? throw Integrity();
    }

    private static void Scope(JsonElement value, CancellationToken token) => Object(value, token,
        ("organizationId", CanonicalId), ("projectId", CanonicalId), ("mappingId", CanonicalId), ("revisionId", CanonicalId));

    private static void ArtifactDigest(JsonElement value, CancellationToken token) => Object(value, token,
        ("algorithm", OneOf("sha256")), ("value", CanonicalDigest));

    private static void BackendIdentity(JsonElement value, CancellationToken token) => Object(value, token,
        ("id", String), ("revision", String), ("buildId", String));

    private static void OperationProvenance(JsonElement value, CancellationToken token) => Object(value, token,
        ("revision", Scope), ("nativeArtifactDigest", ArtifactDigest), ("backend", BackendIdentity),
        ("adapterVersion", String), ("confirmed", Boolean));

    private static void ProjectionProvenance(JsonElement value, CancellationToken token) => Object(value, token,
        ("revision", Scope), ("nativeArtifactDigest", ArtifactDigest), ("backend", BackendIdentity),
        ("adapterVersion", String), ("projectionVersion", String));

    private static RawRule Status => OneOf("succeeded", "invalidMapping", "unsupportedArtifact", "resourceLimit",
        "protocolFailure", "backendUnavailable", "backendFailure", "timedOut", "canceled", "isolationFailure");
    private static RawRule Severity => OneOf("information", "warning", "error");

    private static void OperationDiagnostic(JsonElement value, CancellationToken token) => Object(value, token,
        ("code", String), ("severity", Severity), ("message", String), ("messageTruncated", Boolean),
        ("nativeLocator", Nullable(NativeLocator)));

    private static void NativeLocator(JsonElement value, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        switch (Kind(value))
        {
            case "document": Object(value, token, ("kind", OneOf("document")), ("documentId", String)); break;
            case "jsonPointer": Object(value, token, ("kind", OneOf("jsonPointer")), ("documentId", String), ("pointer", String)); break;
            case "graphNode":
                Object(value, token, ("kind", OneOf("graphNode")), ("documentId", String),
                ("nodeId", String), ("functionId", Nullable(String))); break;
            case "textSpan":
                Object(value, token, ("kind", OneOf("textSpan")), ("documentId", String),
                ("startOffset", String), ("length", String), ("unit", OneOf("utf8Bytes", "utf16CodeUnits", "unicodeScalars"))); break;
            case "opaque":
                Object(value, token, ("kind", OneOf("opaque")), ("documentId", String),
                ("scheme", String), ("value", String)); break;
            default: throw Integrity();
        }
    }

    private static void NodeReference(JsonElement value, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var kind = Kind(value);
        var member = kind switch
        {
            "boundary" => "boundaryId",
            "field" => "fieldId",
            "parameter" => "parameterId",
            "rule" => "ruleId",
            "control" => "controlId",
            _ => throw Integrity()
        };
        Object(value, token, ("kind", OneOf(kind)), (member, String));
    }

    private static void CanonicalId(JsonElement value, CancellationToken token)
    {
        String(value, token);
        var text = value.GetString();
        if (!Guid.TryParseExact(text, "D", out var id) || id == Guid.Empty || text != id.ToString("D")) throw Integrity();
    }

    private static void CanonicalDigest(JsonElement value, CancellationToken token)
    {
        String(value, token);
        var text = value.GetString()!;
        if (text.Length != 64 || text.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw Integrity();
    }
}
