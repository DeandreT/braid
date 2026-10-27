using System.Globalization;
using System.Text;
using System.Text.Json;
using Weft.Domain;

namespace Weft.Application;

public sealed record PreviewDeclaredInput(string? Name, long ByteLength, Sha256Digest Digest);

/// <summary>The decoded canonical request a run was admitted with.</summary>
public sealed record PreviewRequestDescription(
    RevisionScope Revision,
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
    public const string DocumentSchema = "weft.document-preview-request.v1";
    public const int MaximumBytes = 64 * 1024;

    public static string Encode(PreviewRunSubmission submission, PreviewLimits limits)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentNullException.ThrowIfNull(limits);
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            var revision = submission.Revision;
            json.WriteStartObject();
            if (!Enum.IsDefined(submission.Profile)) throw new PreviewRunException("Unknown preview profile.");
            json.WriteString("schema", submission.Profile == PreviewExecutionProfile.Document ? DocumentSchema : Schema);
            json.WriteString("organizationId", revision.OrganizationId.ToString("D"));
            json.WriteString("projectId", revision.ProjectId.ToString("D"));
            json.WriteString("mappingId", revision.MappingId.ToString("D"));
            json.WriteString("revisionId", revision.RevisionId.ToString("D"));
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
                json.WriteNumber("byteLength", input.ByteCount);
                json.WriteString("sha256", input.Digest.Value);
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
            Require(schema is Schema or DocumentSchema);
            var revision = new RevisionScope(Guid.ParseExact(Text(root, "organizationId"), "D"), Guid.ParseExact(Text(root, "projectId"), "D"),
                Guid.ParseExact(Text(root, "mappingId"), "D"), Guid.ParseExact(Text(root, "revisionId"), "D"));
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
                item.GetProperty("byteLength").GetInt64(), Sha256Digest.Parse(Text(item, "sha256")))).ToArray();
            var limits = root.GetProperty("limits");
            var description = new PreviewRequestDescription(revision, selection, parameters, inputs, new PreviewLimits(
                limits.GetProperty("maximumInputBytes").GetInt32(), limits.GetProperty("maximumOutputBytes").GetInt32(),
                limits.GetProperty("maximumArtifacts").GetInt32(), limits.GetProperty("maximumTextBytes").GetInt32()))
            { Profile = schema == DocumentSchema ? PreviewExecutionProfile.Document : PreviewExecutionProfile.LegacyJson };
            Require(inputs.Length > 0 && inputs[0].Name is null && inputs.Skip(1).All(input => input.Name is not null));
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
