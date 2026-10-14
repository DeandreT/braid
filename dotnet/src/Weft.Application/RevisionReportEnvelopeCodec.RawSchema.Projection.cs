using System.Text.Json;

namespace Weft.Application;

internal sealed partial class RevisionReportEnvelopeCodec
{
    private static void ProjectionReport(JsonElement value, CancellationToken token) => Object(value, token,
        ("status", Status), ("provenance", Nullable(OperationProvenance)),
        ("document", Nullable(ProjectionDocument)), ("diagnostics", ArrayOf(OperationDiagnostic)));

    private static void ProjectionDocument(JsonElement value, CancellationToken token)
    {
        Object(value, token, ("provenance", ProjectionProvenance), ("coverage", Coverage),
            ("boundaries", ArrayOf(Boundary)), ("fields", ArrayOf(Field)), ("parameters", ArrayOf(Parameter)),
            ("rules", ArrayOf(Rule)), ("controls", ArrayOf(Control)), ("relationships", ArrayOf(Relationship)),
            ("diagnostics", ArrayOf(ProjectionDiagnostic)), ("capabilities", ArrayOf(Capability)), ("schemaVersion", String));
        if (value.GetProperty("schemaVersion").GetString() != "weft.mapping-projection.v1") throw UnsupportedFormat();
    }

    private static void Coverage(JsonElement value, CancellationToken token) => Object(value, token,
        ("status", OneOf("complete", "partial", "unavailable")), ("gaps", ArrayOf(CoverageGap)));
    private static void CoverageGap(JsonElement value, CancellationToken token) => Object(value, token,
        ("code", String), ("message", String), ("nativeLocator", NativeLocator), ("projectionLocator", Nullable(NodeReference)));

    private static void Boundary(JsonElement value, CancellationToken token) => Object(value, token,
        ("id", String), ("name", String), ("role", OneOf("primaryInput", "namedInput", "primaryOutput", "namedOutput")),
        ("format", String), ("shape", OneOf("unknown", "scalar", "object", "rows", "documentSet")),
        ("rootFieldId", Nullable(String)), ("nativeLocator", NativeLocator));

    private static void DataType(JsonElement value, CancellationToken token) => Object(value, token,
        ("kind", OneOf("unknown", "scalar", "scalarUnion", "object", "array")),
        ("scalarTypes", ArrayOf(OneOf("string", "integer", "decimal", "binaryFloat", "boolean"))), ("nativeName", Nullable(String)));
    private static void Cardinality(JsonElement value, CancellationToken token) => Object(value, token,
        ("kind", OneOf("unknown", "exactlyOne", "zeroOrOne", "zeroOrMore", "oneOrMore", "bounded")),
        ("minimum", Nullable(String)), ("maximum", Nullable(String)));
    private static RawRule Requirement => OneOf("unknown", "optional", "required");
    private static void Field(JsonElement value, CancellationToken token) => Object(value, token,
        ("id", String), ("boundaryId", String), ("path", ArrayOf(Path)), ("name", String), ("dataType", DataType),
        ("cardinality", Cardinality), ("requirement", Requirement), ("nullability", OneOf("unknown", "nonNullable", "nullable")),
        ("nativeLocator", NativeLocator));
    private static void Parameter(JsonElement value, CancellationToken token) => Object(value, token,
        ("id", String), ("name", String), ("dataType", DataType), ("requirement", Requirement),
        ("defaultValue", Nullable(Literal)), ("nativeLocator", NativeLocator));

    private static void Path(JsonElement value, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var kind = Kind(value);
        switch (kind)
        {
            case "field":
            case "attribute": Object(value, token, ("kind", OneOf(kind)), ("name", String), ("namespaceUri", Nullable(String))); break;
            case "index": Object(value, token, ("kind", OneOf(kind)), ("index", String)); break;
            case "anyItem": Object(value, token, ("kind", OneOf(kind))); break;
            default: throw Integrity();
        }
    }

    private static void Literal(JsonElement value, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        switch (Kind(value))
        {
            case "string": Object(value, token, ("kind", OneOf("string")), ("value", String)); break;
            case "boolean": Object(value, token, ("kind", OneOf("boolean")), ("value", Boolean)); break;
            case "exactNumber":
                Object(value, token, ("kind", OneOf("exactNumber")),
                ("flavor", OneOf("integer", "decimal", "binaryFloat")), ("value", String)); break;
            case "null": Object(value, token, ("kind", OneOf("null")), ("nullKind", OneOf("absent", "explicitNull", "xmlNil"))); break;
            default: throw Integrity();
        }
    }

    private static void Rule(JsonElement value, CancellationToken token) => Object(value, token,
        ("id", String), ("outputFieldId", String), ("summary", String), ("dependencies", ArrayOf(Dependency)),
        ("details", ArrayOf(Detail)), ("nativeLocator", NativeLocator));
    private static void Control(JsonElement value, CancellationToken token) => Object(value, token,
        ("id", String), ("kind", OneOf("unknown", "iteration", "filter", "join", "grouping", "sorting", "window",
            "failure", "ordering", "dynamicBoundary", "construction")), ("summary", String),
        ("appliesTo", ArrayOf(NodeReference)), ("dependencies", ArrayOf(Dependency)), ("details", ArrayOf(Detail)),
        ("nativeLocator", NativeLocator));

    private static void Dependency(JsonElement value, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var kind = Kind(value);
        var role = OneOf("value", "condition", "default");
        switch (kind)
        {
            case "field": Object(value, token, ("kind", OneOf(kind)), ("fieldId", String), ("role", role)); break;
            case "parameter": Object(value, token, ("kind", OneOf(kind)), ("parameterId", String), ("role", role)); break;
            case "constant": Object(value, token, ("kind", OneOf(kind)), ("value", Literal), ("role", role)); break;
            case "node": Object(value, token, ("kind", OneOf(kind)), ("node", NodeReference), ("role", role)); break;
            default: throw Integrity();
        }
    }

    private static void Detail(JsonElement value, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var kind = Kind(value);
        switch (kind)
        {
            case "group": Object(value, token, ("kind", OneOf(kind)), ("label", String), ("children", ArrayOf(Detail))); break;
            case "expression": Object(value, token, ("kind", OneOf(kind)), ("operation", String), ("arguments", ArrayOf(Detail))); break;
            case "literal": Object(value, token, ("kind", OneOf(kind)), ("value", Literal)); break;
            case "reference": Object(value, token, ("kind", OneOf(kind)), ("reference", NodeReference)); break;
            case "unsupported":
                Object(value, token, ("kind", OneOf(kind)), ("construct", String),
                ("description", String), ("nativeLocator", NativeLocator)); break;
            default: throw Integrity();
        }
    }

    private static void Relationship(JsonElement value, CancellationToken token) => Object(value, token,
        ("id", String), ("kind", OneOf("readsFrom", "writesTo", "governedBy", "dependsOn", "produces", "boundaryMember")),
        ("from", NodeReference), ("to", NodeReference));
    private static void ProjectionDiagnostic(JsonElement value, CancellationToken token) => Object(value, token,
        ("code", String), ("severity", Severity), ("message", String), ("nativeLocator", Nullable(NativeLocator)),
        ("projectionLocator", Nullable(NodeReference)));
    private static void Capability(JsonElement value, CancellationToken token) => Object(value, token,
        ("operation", OneOf("inspect", "project", "validate", "preview", "build")),
        ("status", OneOf("supported", "unsupported", "conditional", "notQualified")), ("profile", String),
        ("boundaryIds", ArrayOf(String)), ("conditions", ArrayOf(CapabilityCondition)));
    private static void CapabilityCondition(JsonElement value, CancellationToken token) => Object(value, token,
        ("code", String), ("description", String));
}
