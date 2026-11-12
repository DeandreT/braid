using System.Text;
using System.Text.Json;
using Weft.Application.Regression;
using Weft.Domain;

namespace Weft.Application;

/// <summary>Saved metadata and physical checks only. Native execution still admits the complete graph.</summary>
public static class XmlPreviewAdmission
{
    public const string Profile = "weft.ferrule-xml-preview.v1";
    public const string EnabledSetting = "WEFT_PREVIEWS_XML_ENABLED";
    public const string RegressionEnabledSetting = "WEFT_REGRESSIONS_XML_ENABLED";

    public static bool Enabled(Func<string, string?> environment, string setting, bool parentEnabled)
    {
        var enabled = environment(setting) switch
        {
            null or "false" => false,
            "true" => true,
            _ => throw new ArgumentException($"{setting} must be true or false.")
        };
        if (enabled && !parentEnabled) throw new ArgumentException($"{setting} requires its parent workflow.");
        return enabled;
    }

    public sealed record Boundary(string Id, string Name, XmlDocumentMetadata Metadata);

    /// <summary>A case's metadata must equal the revision's complete selected declarations. Input bytes are safety-checked here.</summary>
    public static bool MatchesCase(NativeArtifact artifact, RegressionReplay replay, RegressionOutputSet expected)
    {
        var supplied = replay.NamedInputs.Prepend(replay.Primary).ToArray();
        var bound = BindInputs(artifact, supplied);
        if (bound.Where((input, index) => input.XmlMetadata != supplied[index].XmlMetadata).Any()) return false;
        var declared = Outputs(artifact, replay.Target);
        return expected.Outputs.Count == declared.Count && expected.Outputs.All(output => declared.Count(boundary =>
            (output.Key.IsPrimary ? boundary.Id == "primary-output" : boundary.Id != "primary-output" && boundary.Name == output.Key.Name) &&
            boundary.Metadata == output.XmlMetadata) == 1);
    }

    public static IReadOnlyList<Boundary> Outputs(NativeArtifact artifact, PreviewTargetSelection target)
    {
        try { return ReadOutputs(artifact, target); }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException) { throw Invalid(); }
    }

    private static IReadOnlyList<Boundary> ReadOutputs(NativeArtifact artifact, PreviewTargetSelection target)
    {
        var metadata = XmlNativeMetadata.Read(artifact); // All declarations, including unselected outputs, share the ledger.
        using var parsed = JsonDocument.Parse(artifact.CopyBytes(), new JsonDocumentOptions { MaxDepth = 128 });
        var root = Project(parsed.RootElement);
        var result = new List<Boundary> { new("primary-output", root.GetProperty("target").GetProperty("name").GetString()!,
            metadata.Single(item => item.Id == "primary-output").Metadata) };
        if (root.TryGetProperty("extra_targets", out var named))
            foreach (var item in named.EnumerateArray())
            {
                var id = $"named-output:{result.Count - 1}";
                result.Add(new(id, item.GetProperty("name").GetString()!, metadata.Single(value => value.Id == id).Metadata));
            }
        if (result.Any(item => string.IsNullOrEmpty(item.Name) || item.Name.Contains('\0')) ||
            result.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != result.Count) throw Invalid();
        return target.Kind switch
        {
            PreviewTargetKind.All => result.AsReadOnly(),
            PreviewTargetKind.Primary => result.Take(1).ToArray(),
            PreviewTargetKind.Named => result.Skip(1).Where(item => item.Name == target.Name).ToArray() is { Length: 1 } selected ? selected : throw Invalid(),
            _ => throw Invalid()
        };
    }

    public static IReadOnlyList<PreviewInputDocument> BindInputs(NativeArtifact artifact, IReadOnlyList<PreviewInputDocument> inputs)
    {
        try { return ReadInputs(artifact, inputs); }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException) { throw Invalid(); }
    }

    private static IReadOnlyList<PreviewInputDocument> ReadInputs(NativeArtifact artifact, IReadOnlyList<PreviewInputDocument> inputs)
    {
        var metadata = XmlNativeMetadata.Read(artifact);
        using var parsed = JsonDocument.Parse(artifact.CopyBytes(), new JsonDocumentOptions { MaxDepth = 128 });
        var root = Project(parsed.RootElement);
        var names = root.TryGetProperty("extra_sources", out var named)
            ? named.EnumerateArray().Select(item => item.GetProperty("name").GetString()).ToArray() : [];
        if (names.Any(string.IsNullOrEmpty) || names.Distinct(StringComparer.Ordinal).Count() != names.Length) throw Invalid();
        return inputs.Select((input, index) =>
        {
            if ((index == 0) != (input.Name is null) || input.CsvDialect is not null) throw Invalid();
            var ordinal = input.Name is null ? 0 : Array.IndexOf(names, input.Name) + 1;
            if (input.Name is not null && ordinal == 0) throw Invalid();
            var id = ordinal == 0 ? "primary-input" : $"named-input:{ordinal - 1}";
            var saved = metadata.Single(item => item.Id == id).Metadata;
            if (input.XmlMetadata is not null && input.XmlMetadata != saved) throw Invalid();
            XmlDocumentSafety.Validate(input.Bytes);
            return new PreviewInputDocument(input.Name, input.Bytes, xmlMetadata: saved);
        }).ToArray();
    }

    public static void VerifyOutputs(MappingPreviewRequest request, IReadOnlyList<PreviewOutputArtifact> outputs)
    {
        var totalBytes = outputs.Sum(output => (long)output.ByteCount);
        if (outputs.Count > request.Limits.MaximumArtifacts || totalBytes > request.Limits.MaximumOutputBytes)
            throw new XmlDocumentException(XmlDocumentFailure.ByteLimit, observed: totalBytes, limit: request.Limits.MaximumOutputBytes);
        var expected = Outputs(request.Artifact, request.Target);
        if (expected.Count != outputs.Count) throw Invalid();
        for (var index = 0; index < outputs.Count; index++)
        {
            var output = outputs[index];
            var saved = expected[index];
            var name = BoundedName(saved.Name, request.Limits.MaximumTextBytes);
            if (output.BoundaryId != saved.Id || output.TargetName != name || output.TargetNameTruncated != (name != saved.Name) ||
                output.XmlMetadata != saved.Metadata || output.CsvDialect is not null ||
                output.MediaType != XmlDocumentPolicy.MediaType || Sha256Digest.FromBytes(output.Bytes) != output.Digest) throw Invalid();
            XmlDocumentSafety.Validate(output.Bytes);
        }
    }

    public static void VerifyResult(MappingPreviewRequest request, MappingPreviewResult result)
    {
        if (result.Profile != Profile || result.Provenance.Scope != request.Artifact.Scope || result.Provenance.NativeDigest != request.Artifact.Digest ||
            !result.Provenance.Confirmed || result.Provenance.BackendId != "ferrule") throw Invalid();
        VerifyOutputs(request, result.Artifacts);
    }

    private static string BoundedName(string value, int limit)
    {
        var size = 0;
        var result = new StringBuilder();
        foreach (var rune in value.EnumerateRunes())
        {
            if (size + rune.Utf8SequenceLength > limit) break;
            result.Append(rune.ToString()); size += rune.Utf8SequenceLength;
        }
        return result.ToString();
    }

    private static JsonElement Project(JsonElement root) => root.TryGetProperty("__ferrule_file", out _) ? root.GetProperty("document") : root;
    private static XmlDocumentException Invalid() => new(XmlDocumentFailure.InvalidNativeMetadata);
}
