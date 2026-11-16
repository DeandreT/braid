using System.Text.Json;

namespace Braid.Application;

internal sealed partial class RevisionReportEnvelopeCodec
{
    private static void ValidationReport(JsonElement value, CancellationToken token) => Object(value, token,
        ("status", Status), ("provenance", Nullable(OperationProvenance)), ("isValid", Nullable(Boolean)),
        ("totalIssueCount", Integer), ("emittedIssueCount", Integer), ("truncated", Boolean),
        ("diagnostics", ArrayOf(ValidationDiagnostic)));

    private static void ValidationDiagnostic(JsonElement value, CancellationToken token)
    {
        Object(value, token, ("code", String), ("severity", Severity), ("message", String), ("messageTruncated", Boolean),
            ("nativeLocator", Nullable(NativeLocator)), ("owner", Nullable(NativeOwner)), ("location", Nullable(String)),
            ("locationTruncated", Boolean), ("projectionLocator", Nullable(NodeReference)));
        // The stored report is native-only. Links are rebuilt after a separate verified projection selection.
        if (value.GetProperty("projectionLocator").ValueKind != JsonValueKind.Null) throw Integrity();
    }

    private static void NativeOwner(JsonElement value, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var kind = Kind(value);
        switch (kind)
        {
            case "graphNode": Object(value, token, ("kind", OneOf(kind)), ("functionId", Nullable(String)), ("nodeId", String)); break;
            case "userFunction": Object(value, token, ("kind", OneOf(kind)), ("functionId", String)); break;
            case "endpoint": Object(value, token, ("kind", OneOf(kind)), ("endpoint", Endpoint)); break;
            case "schemaNode": Object(value, token, ("kind", OneOf(kind)), ("endpoint", Endpoint), ("path", ArrayOf(SchemaStep))); break;
            case "scope": Object(value, token, ("kind", OneOf(kind)), ("target", Endpoint), ("path", ArrayOf(ScopeStep))); break;
            case "failureRule": Object(value, token, ("kind", OneOf(kind)), ("index", String)); break;
            default: throw Integrity();
        }
    }

    private static void Endpoint(JsonElement value, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var kind = Kind(value);
        switch (kind)
        {
            case "source":
            case "target": Object(value, token, ("kind", OneOf(kind))); break;
            case "namedSource":
            case "namedTarget": Object(value, token, ("kind", OneOf(kind)), ("index", String), ("name", String)); break;
            default: throw Integrity();
        }
    }

    private static void SchemaStep(JsonElement value, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var kind = Kind(value);
        switch (kind)
        {
            case "child":
            case "contains":
            case "dependentSchema": Object(value, token, ("kind", OneOf(kind)), ("index", String)); break;
            case "dynamicField": Object(value, token, ("kind", OneOf(kind))); break;
            default: throw Integrity();
        }
    }

    private static void ScopeStep(JsonElement value, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var kind = Kind(value);
        if (kind is not ("child" or "dynamicChild" or "segment")) throw Integrity();
        Object(value, token, ("kind", OneOf(kind)), ("index", String));
    }
}
