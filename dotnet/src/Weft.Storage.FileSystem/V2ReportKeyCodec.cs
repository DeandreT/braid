using System.Text;
using System.Text.Json;
using Weft.Application;
using Weft.Domain;

namespace Weft.Storage.FileSystem;

// Deliberately local to private seals. No report-envelope decoder or public API is widened.
internal static class V2ReportKeyCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static byte[] Encode(RevisionReportKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var bytes = key.CopyCanonicalUtf8();
        if (bytes.Length is < 1 or > V2PublicationRecordCodec.MaximumKeyBytes)
            throw Unsafe();
        return bytes;
    }

    internal static RevisionReportKey Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 1 or > V2PublicationRecordCodec.MaximumKeyBytes)
            throw Unsafe();
        try
        {
            _ = StrictUtf8.GetCharCount(bytes);
            using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8
            });
            var value = document.RootElement;
            if (Text(value, "schemaVersion") != "weft.revision-report-key.v1")
                throw Unsafe();
            var s = value.GetProperty("scope");
            var scope = new RevisionScope(Id(s, "organizationId"), Id(s, "projectId"),
                Id(s, "mappingId"), Id(s, "revisionId"));
            var native = value.GetProperty("nativeObject");
            if (Text(native, "class") != "native")
                throw Unsafe();
            var reference = new PrivateObjectReference(
                PrivateObjectId.Restore(scope, Id(native, "objectId"), PrivateObjectClass.NativeArtifact),
                Sha256Digest.Parse(Text(native, "digest")), native.GetProperty("byteCount").GetInt64());
            var source = new RevisionReportSource(scope, reference, Text(value, "logicalDocument"),
                Text(value, "dependencyIdentity"), Text(value, "sourceBackendId"));
            var p = value.GetProperty("profile");
            var profile = new RevisionReportBackendProfile(Text(p, "backendId"), Text(p, "backendRevision"),
                Text(p, "backendBuild"), Text(p, "adapterBuild"));
            var limits = value.GetProperty("policy");
            if (Text(limits, "schemaVersion") != "weft.revision-report-policy.v1")
                throw Unsafe();
            var policy = new RevisionReportPolicy(Int(limits, "nativeByteLimit"), Int(limits, "workerResponseByteLimit"),
                Int(limits, "itemLimit"), Int(limits, "responseItemLimit"), Int(limits, "jsonDepthLimit"),
                Int(limits, "detailDepthLimit"), Int(limits, "diagnosticLimit"), Int(limits, "validationIssueLimit"),
                Int(limits, "textByteLimit"), Int(limits, "headerByteLimit"), Int(limits, "storedObjectByteLimit"),
                Int(limits, "responseByteLimit"));
            if (Text(value, "policyFingerprint") != policy.Fingerprint.Value)
                throw Unsafe();
            RevisionReportKey key = Text(value, "operation") switch
            {
                "projection" => new ProjectionCacheKey(source, profile, Text(value, "reportSchemaVersion"),
                    Text(value, "algorithmVersion"), Text(value, "codecVersion"), policy),
                "validation" => new ValidationCacheKey(source, profile, Text(value, "reportSchemaVersion"),
                    Text(value, "algorithmVersion"), Text(value, "codecVersion"), policy),
                _ => throw Unsafe()
            };
            // Exact equality also rejects duplicate/unknown/escaped-alias members, reordered fields,
            // absent or computed-only schema fields, alternate numbers/UUIDs and alternate escaping.
            if (!bytes.SequenceEqual(key.CopyCanonicalUtf8()))
                throw Unsafe();
            return key;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
            FormatException or KeyNotFoundException or JsonException or OverflowException)
        {
            throw Unsafe();
        }
    }

    private static string Text(JsonElement value, string name) => value.GetProperty(name).GetString() ?? throw Unsafe();
    private static Guid Id(JsonElement value, string name) => Guid.ParseExact(Text(value, name), "D");
    private static int Int(JsonElement value, string name) => value.GetProperty(name).GetInt32();
    private static PrivateObjectStorageException Unsafe() => new(PrivateObjectStorageError.UnsafeStorage);
}
