using System.Text.Json;
using Braid.Application;
using Braid.Domain;

namespace Braid.Deployment;

/// <summary>Strict canonical persistence; alternate spellings and unknown/duplicate properties are refused.</summary>
public static class BuildIntentCodec
{
    public static BuildIntent Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 1 or > 1024 * 1024) throw Invalid();
        try
        {
            using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (Text(root, "schema") != BuildIntent.SchemaVersion) throw Invalid();
            var revision = new RevisionScope(Id(root, "organizationId"), Id(root, "projectId"), Id(root, "mappingId"), Id(root, "revisionId"));
            var values = root.GetProperty("boundaries");
            if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() is < 1 or > 128) throw Invalid();
            var boundaries = values.EnumerateArray().Select(value => new BuildBoundary(Text(value, "kind") switch
            {
                "primaryInput" => BuildBoundaryKind.PrimaryInput,
                "namedInput" => BuildBoundaryKind.NamedInput,
                "primaryOutput" => BuildBoundaryKind.PrimaryOutput,
                "namedOutput" => BuildBoundaryKind.NamedOutput,
                _ => throw Invalid()
            }, value.GetProperty("name").ValueKind == JsonValueKind.Null ? null : Text(value, "name"), Text(value, "format"), BuildCsvDialect.ReadOptional(value),
                value.TryGetProperty("xmlMetadata", out var xml) ? XmlDocumentMetadataJson.Read(xml, Text(value, "kind") is "primaryInput" or "namedInput" ? XmlDocumentRole.Input : XmlDocumentRole.Output) : null,
                value.TryGetProperty("documentSettings", out var settings) ? NativeDocumentSettingsJson.Read(settings) : null)).ToArray();
            var intent = new BuildIntent(Id(root, "buildId"), revision, Sha256Digest.Parse(Text(root, "nativeDigest")),
                new(Text(root, "backendId"), Text(root, "engineRevision"), Text(root, "engineBuild"), Text(root, "adapterVersion")),
                new(Text(root, "profileId"), root.GetProperty("profileVersion").GetInt32(), Text(root, "runtime"),
                    Text(root, "runtimeVersion"), Text(root, "artifactFormat"), root.GetProperty("maximumArtifactBytes").GetInt64()),
                boundaries, new(new(revision.OrganizationId, revision.ProjectId), Id(root, "environmentId"), root.GetProperty("environmentVersion").GetInt64()),
                Id(root, "approvalId"), Id(root, "regressionEvidenceId"));
            if (!bytes.SequenceEqual(intent.CopyCanonicalBytes())) throw Invalid();
            return intent;
        }
        catch (Exception failure) when (failure is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException or OverflowException)
        { throw Invalid(); }
    }

    private static string Text(JsonElement value, string name) => value.GetProperty(name).GetString() ?? throw Invalid();
    private static Guid Id(JsonElement value, string name) => Guid.ParseExact(Text(value, name), "D");
    private static BuildContractException Invalid() => new(BuildContractFailure.InvalidInput);
}
