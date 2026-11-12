using System.Globalization;
using System.Text;
using System.Text.Json;
using Weft.Domain;

namespace Weft.Application;

public sealed record PreviewDeclaredInput(string? Name, long ByteLength, Sha256Digest Digest)
{
    public CsvDocumentDialect? CsvDialect { get; init; }
}

/// <summary>The decoded canonical request a run was admitted with.</summary>
public sealed record PreviewRequestDescription(
    PreviewSubject Subject,
    PreviewTargetSelection Target,
    IReadOnlyList<PreviewParameter> Parameters,
    IReadOnlyList<PreviewDeclaredInput> Inputs,
    PreviewLimits Limits)
{
    public PreviewExecutionProfile Profile { get; init; } = PreviewExecutionProfile.LegacyJson;
}

/// <summary>
/// Canonical UTF-8 JSON for a preview request. Its SHA-256 is the request fingerprint, so a
/// resubmission with the same key replays only when every declared value is identical.
/// Input bytes are represented by length and digest; the execution clock is not part of it.
/// </summary>
public static class PreviewRequestDocument
{
    public const string Schema = "weft.preview-request.v1";
    public const string DraftSchema = "weft.draft-preview-request.v1";
    public const string DocumentSchema = "weft.document-preview-request.v1";
    public const string DraftDocumentSchema = "weft.draft-document-preview-request.v1";
    public const string CsvSchema = "weft.csv-preview-request.v1";
    public const int MaximumBytes = 64 * 1024;

    public static string Encode(PreviewRunSubmission submission, PreviewLimits limits)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentNullException.ThrowIfNull(limits);
        if (submission.Profile == PreviewExecutionProfile.CsvDocument && (submission.Subject is not RevisionPreviewSubject || submission.Target.Kind == PreviewTargetKind.Named ||
            submission.NamedInputs.Count != 0 || submission.Primary.Name is not null || submission.Primary.CsvDialect is null))
            throw new PreviewRunException("CSV requests require a present primary input and its saved native dialect.");
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            var revision = submission.Subject.ExecutionScope;
            json.WriteStartObject();
            if (!Enum.IsDefined(submission.Profile)) throw new PreviewRunException("Unknown preview profile.");
            var document = submission.Profile == PreviewExecutionProfile.Document;
            json.WriteString("schema", submission.Subject is DraftPreviewSubject
                ? document ? DraftDocumentSchema : DraftSchema
                : document ? DocumentSchema : submission.Profile == PreviewExecutionProfile.CsvDocument ? CsvSchema : Schema);
            json.WriteString("organizationId", revision.OrganizationId.ToString("D"));
            json.WriteString("projectId", revision.ProjectId.ToString("D"));
            json.WriteString("mappingId", revision.MappingId.ToString("D"));
            if (submission.Subject is DraftPreviewSubject draft)
            {
                json.WriteString("baseRevisionId", revision.RevisionId.ToString("D"));
                json.WriteString("draftId", draft.DraftId.ToString("D"));
                json.WriteString("draftVersion", draft.Version.ToString(CultureInfo.InvariantCulture));
                json.WriteString("nativeDigest", draft.NativeDigest.Value);
                json.WriteNumber("nativeByteCount", draft.NativeByteCount);
            }
            else json.WriteString("revisionId", revision.RevisionId.ToString("D"));
            json.WriteStartObject("target");
            json.WriteString("kind", submission.Target.Kind switch
            {
                PreviewTargetKind.All => "all",
                PreviewTargetKind.Primary => "primary",
                _ => "named"
            });
            if (submission.Target.Kind == PreviewTargetKind.Named)
                json.WriteString("name", submission.Target.Name);
            json.WriteEndObject();
            json.WriteStartArray("parameters");
            foreach (var parameter in submission.Parameters)
            {
                json.WriteStartObject();
                json.WriteString("name", parameter.Name);
                json.WriteString("kind", Kind(parameter.Kind));
                json.WriteString("value", parameter.Value);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteStartArray("inputs");
            foreach (var input in submission.NamedInputs.Prepend(submission.Primary))
            {
                json.WriteStartObject();
                if (input.Name is null)
                    json.WriteNull("name");
                else
                    json.WriteString("name", input.Name);
                if (submission.Profile == PreviewExecutionProfile.CsvDocument) json.WriteBoolean("present", true);
                json.WriteNumber("byteLength", input.ByteCount);
                json.WriteString("sha256", input.Digest.Value);
                if (submission.Profile == PreviewExecutionProfile.CsvDocument)
                {
                    json.WritePropertyName("csvDialect");
                    CsvDialectJson.Write(json, input.CsvDialect ?? throw new PreviewRunException("CSV input requires its saved native dialect."));
                }
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteStartObject("limits");
            json.WriteNumber("maximumInputBytes", limits.MaximumInputBytes);
            json.WriteNumber("maximumOutputBytes", limits.MaximumOutputBytes);
            json.WriteNumber("maximumArtifacts", limits.MaximumArtifacts);
            json.WriteNumber("maximumTextBytes", limits.MaximumTextBytes);
            json.WriteEndObject();
            json.WriteEndObject();
        }
        if (buffer.Length > MaximumBytes)
            throw new PreviewRunException("The preview request exceeds its canonical size limit.");
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static string Fingerprint(string document) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(document)));

    public static PreviewRequestDescription Decode(string document)
    {
        ArgumentNullException.ThrowIfNull(document);
        try
        {
            using var parsed = JsonDocument.Parse(document, new JsonDocumentOptions { MaxDepth = 8 });
            var root = parsed.RootElement;
            var schema = Text(root, "schema");
            Require(schema is Schema or DraftSchema or DocumentSchema or DraftDocumentSchema or CsvSchema);
            var isDraft = schema is DraftSchema or DraftDocumentSchema;
            var revision = new RevisionScope(Guid.ParseExact(Text(root, "organizationId"), "D"), Guid.ParseExact(Text(root, "projectId"), "D"),
                Guid.ParseExact(Text(root, "mappingId"), "D"), Guid.ParseExact(Text(root, isDraft ? "baseRevisionId" : "revisionId"), "D"));
            PreviewSubject subject = !isDraft ? new RevisionPreviewSubject(revision) : new DraftPreviewSubject(revision,
                Guid.ParseExact(Text(root, "draftId"), "D"), long.Parse(Text(root, "draftVersion"), NumberStyles.None, CultureInfo.InvariantCulture),
                Sha256Digest.Parse(Text(root, "nativeDigest")), root.GetProperty("nativeByteCount").GetInt32());
            var target = root.GetProperty("target");
            var selection = Text(target, "kind") switch
            {
                "all" => PreviewTargetSelection.All,
                "primary" => PreviewTargetSelection.Primary,
                "named" => PreviewTargetSelection.Named(Text(target, "name")),
                _ => throw new PreviewRunException("The stored preview target is unknown.")
            };
            var parameters = root.GetProperty("parameters").EnumerateArray().Select(item => new PreviewParameter(
                Text(item, "name"), Text(item, "kind") switch
                {
                    "string" => PreviewParameterKind.String,
                    "integer" => PreviewParameterKind.Integer,
                    "float" => PreviewParameterKind.Float,
                    "boolean" => PreviewParameterKind.Boolean,
                    _ => throw new PreviewRunException("The stored preview parameter kind is unknown.")
                }, Text(item, "value"))).ToArray();
            var inputs = root.GetProperty("inputs").EnumerateArray().Select(item => new PreviewDeclaredInput(
                item.GetProperty("name").ValueKind == JsonValueKind.Null ? null : Text(item, "name"),
                item.GetProperty("byteLength").GetInt64(), Sha256Digest.Parse(Text(item, "sha256")))
            { CsvDialect = schema == CsvSchema ? CsvDialectJson.Read(item.GetProperty("csvDialect")) : null }).ToArray();
            if (schema == CsvSchema)
            {
                Require(root.EnumerateObject().Select(property => property.Name).SequenceEqual(new[]
                    { "schema", "organizationId", "projectId", "mappingId", "revisionId", "target", "parameters", "inputs", "limits" }));
                Require(root.GetProperty("inputs").EnumerateArray().All(item => item.GetProperty("present").GetBoolean()
                    && item.EnumerateObject().Select(property => property.Name).SequenceEqual(new[] { "name", "present", "byteLength", "sha256", "csvDialect" }))
                    && inputs.Length == 1 && selection.Kind != PreviewTargetKind.Named);
                Require(target.EnumerateObject().Select(property => property.Name).SequenceEqual(new[] { "kind" })
                    && root.GetProperty("parameters").EnumerateArray().All(item => item.EnumerateObject().Select(property => property.Name)
                        .SequenceEqual(new[] { "name", "kind", "value" }))
                    && root.GetProperty("limits").EnumerateObject().Select(property => property.Name).SequenceEqual(new[]
                        { "maximumInputBytes", "maximumOutputBytes", "maximumArtifacts", "maximumTextBytes" }));
                var input = root.GetProperty("inputs")[0];
                Require(input.GetProperty("csvDialect").GetRawText() == CsvDialectJson.Encode(inputs[0].CsvDialect!)
                    && Text(input, "sha256") == inputs[0].Digest.Value
                    && input.GetProperty("byteLength").GetRawText() == inputs[0].ByteLength.ToString(CultureInfo.InvariantCulture)
                    && Text(root, "organizationId") == revision.OrganizationId.ToString("D")
                    && Text(root, "projectId") == revision.ProjectId.ToString("D")
                    && Text(root, "mappingId") == revision.MappingId.ToString("D")
                    && Text(root, "revisionId") == revision.RevisionId.ToString("D"));
                using var canonical = new MemoryStream();
                using (var writer = new Utf8JsonWriter(canonical)) root.WriteTo(writer);
                Require(Encoding.UTF8.GetString(canonical.ToArray()) == document);
            }
            var limits = root.GetProperty("limits");
            var description = new PreviewRequestDescription(subject, selection, parameters, inputs, new PreviewLimits(
                limits.GetProperty("maximumInputBytes").GetInt32(), limits.GetProperty("maximumOutputBytes").GetInt32(),
                limits.GetProperty("maximumArtifacts").GetInt32(), limits.GetProperty("maximumTextBytes").GetInt32()))
            {
                Profile = schema switch
                {
                    DocumentSchema or DraftDocumentSchema => PreviewExecutionProfile.Document,
                    CsvSchema => PreviewExecutionProfile.CsvDocument,
                    _ => PreviewExecutionProfile.LegacyJson
                }
            };
            Require(inputs.Length > 0 && inputs[0].Name is null && inputs.Skip(1).All(input => input.Name is not null));
            Require(inputs.All(input => input.ByteLength >= (schema == CsvSchema ? 0 : 1)));
            return description;
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException
            or FormatException or ArgumentException)
        {
            throw new PreviewRunException("The stored preview request is not a valid canonical document.");
        }
    }

    internal static string Kind(PreviewParameterKind kind) => kind switch
    {
        PreviewParameterKind.String => "string",
        PreviewParameterKind.Integer => "integer",
        PreviewParameterKind.Float => "float",
        PreviewParameterKind.Boolean => "boolean",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    /// <summary>The worker clock form: UTC with seven fractional digits.</summary>
    public static string Clock(DateTimeOffset instant) =>
        instant.ToUniversalTime().ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fffffff'Z'", CultureInfo.InvariantCulture);

    private static string Text(JsonElement element, string name) =>
        element.GetProperty(name).GetString() ?? throw new PreviewRunException("A stored preview text value is missing.");

    private static void Require(bool condition)
    {
        if (!condition) throw new PreviewRunException("The stored preview request is not a valid canonical document.");
    }
}
