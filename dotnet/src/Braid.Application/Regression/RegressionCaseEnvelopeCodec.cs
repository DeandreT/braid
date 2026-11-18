using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Braid.Domain;

namespace Braid.Application.Regression;

/// <summary>Bounded private storage bytes, not a public response or authorization capability.</summary>
public sealed class RegressionCaseEnvelope
{
    private readonly byte[] bytes;

    internal RegressionCaseEnvelope(RegressionCaseVersion version, byte[] bytes)
    {
        this.bytes = bytes;
        Reference = new(version.CaseId, version.Version, version.Owner, version.Revision,
            version.NativeDigest, Sha256Digest.FromBytes(bytes), bytes.Length);
    }

    public RegressionCaseReference Reference { get; }
    public byte[] CopyBytes() => (byte[])bytes.Clone();
    public override string ToString() => nameof(RegressionCaseEnvelope);
}

/// <summary>Strict length-prefixed header and exact raw frames. The digest covers the whole envelope.</summary>
public sealed partial class RegressionCaseEnvelopeCodec
{
    public const string Schema = "braid.regression-case.v1";
    public const string DocumentSchema = "braid.document-regression-case.v1";
    public const string CsvSchema = "braid.csv-regression-case.v1";
    public const string XmlSchema = "braid.xml-regression-case.v1";
    public const int MaximumHeaderBytes = 256 * 1024;
    public const int MaximumEnvelopeBytes = 4 + MaximumHeaderBytes +
        4 * (PreviewPolicy.MaximumInputDocuments + PreviewPolicy.MaximumArtifacts) +
        PreviewPolicy.MaximumInputBytes + PreviewPolicy.MaximumOutputBytes;

    public RegressionCaseEnvelope Encode(RegressionCaseVersion version, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(version);
        using var stream = new MemoryStream();
        // Header space is bounded independently from private frame bytes.
        stream.Write(new byte[4]);
        using (var json = new Utf8JsonWriter(stream)) WriteHeader(json, version, cancellationToken);
        var headerLength = checked((int)stream.Length - 4);
        if (headerLength > MaximumHeaderBytes) throw new RegressionCaseException(RegressionCaseFailure.ResourceLimit);
        Append(stream, version.Replay.Primary.Bytes, cancellationToken);
        foreach (var input in version.Replay.NamedInputs) Append(stream, input.Bytes, cancellationToken);
        foreach (var output in version.Expected.Outputs) Append(stream, output.Span, cancellationToken);
        if (stream.Length > MaximumEnvelopeBytes) throw new RegressionCaseException(RegressionCaseFailure.ResourceLimit);
        var bytes = stream.ToArray();
        BinaryPrimitives.WriteInt32BigEndian(bytes, headerLength);
        cancellationToken.ThrowIfCancellationRequested();
        return new(version, bytes);
    }

    public RegressionCaseVersion Decode(RegressionCaseReference expected, ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(expected);
        if (bytes.Length > MaximumEnvelopeBytes) throw new RegressionCaseException(RegressionCaseFailure.ResourceLimit);
        if (bytes.Length < 4 || bytes.Length != expected.ByteCount) throw Integrity();
        // Own the entire bounded input before verification, so concurrent caller mutation cannot bypass its digest.
        var owned = bytes.ToArray();
        if (Sha256Digest.FromBytes(owned) != expected.ContentDigest) throw Integrity();
        try
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(owned);
            if (length < 1 || length > MaximumHeaderBytes || length > owned.Length - 4) throw Integrity();
            _ = new UTF8Encoding(false, true).GetCharCount(owned.AsSpan(4, length));
            using var document = JsonDocument.Parse(owned.AsMemory(4, length), new JsonDocumentOptions { MaxDepth = 12 });
            var offset = 4 + length;
            var result = ReadHeader(document.RootElement, owned, ref offset, cancellationToken);
            if (offset != owned.Length || result.CaseId != expected.CaseId || result.Version != expected.Version ||
                result.Owner != expected.Owner || result.Revision != expected.Revision || result.NativeDigest != expected.NativeDigest)
                throw Integrity();
            if ((result.Runtime.Profile is CsvDocumentPolicy.Profile or XmlPreviewAdmission.Profile || NativeDocumentFormat.FromProfile(result.Runtime.Profile) is not null) &&
                !Encode(result, cancellationToken).CopyBytes().AsSpan().SequenceEqual(owned))
                throw Integrity();
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or OverflowException or
            RegressionComparisonException or PreviewRunException or XmlDocumentException || error is RegressionCaseException { Failure: not RegressionCaseFailure.StorageIntegrity })
        {
            // Never attach parser exceptions: they can contain private names, parameter values or payload text.
            throw Integrity();
        }
    }

    private static void WriteHeader(Utf8JsonWriter json, RegressionCaseVersion value, CancellationToken token)
    {
        var replay = value.Replay;
        json.WriteStartObject();
        var documents = value.Runtime.Profile == DocumentPreviewPolicy.Profile;
        var csv = value.Runtime.Profile == CsvDocumentPolicy.Profile;
        var xml = value.Runtime.Profile == XmlPreviewAdmission.Profile;
        var native = NativeDocumentFormat.FromProfile(value.Runtime.Profile);
        json.WriteString("schema", native?.CaseSchema ?? (xml ? XmlSchema : csv ? CsvSchema : documents ? DocumentSchema : Schema));
        json.WriteString("caseId", value.CaseId.ToString("D"));
        json.WriteNumber("version", value.Version);
        json.WriteString("owner", value.Owner.Value.ToString("D"));
        json.WriteString("name", value.Name);
        json.WriteStartObject("revision");
        json.WriteString("organizationId", value.Revision.OrganizationId.ToString("D"));
        json.WriteString("projectId", value.Revision.ProjectId.ToString("D"));
        json.WriteString("mappingId", value.Revision.MappingId.ToString("D"));
        json.WriteString("revisionId", value.Revision.RevisionId.ToString("D"));
        json.WriteEndObject();
        json.WriteString("nativeDigest", value.NativeDigest.Value);
        json.WriteStartObject("runtime");
        json.WriteString("backendId", value.Runtime.BackendId);
        json.WriteString("engineRevision", value.Runtime.EngineRevision);
        json.WriteString("backendBuild", value.Runtime.BackendBuild);
        json.WriteString("adapterBuild", value.Runtime.AdapterBuild);
        json.WriteString("profile", value.Runtime.Profile);
        json.WriteEndObject();
        json.WriteStartObject("target");
        json.WriteString("kind", replay.Target.Kind switch
        {
            PreviewTargetKind.All => "all",
            PreviewTargetKind.Primary => "primary",
            PreviewTargetKind.Named => "named",
            _ => throw new RegressionCaseException(RegressionCaseFailure.InvalidInput)
        });
        json.WriteString("name", replay.Target.Name);
        json.WriteEndObject();
        json.WriteString("currentDateTime", replay.CurrentDateTime);
        json.WriteString("comparisonPolicy", value.ComparisonPolicy);
        if (documents)
        {
            json.WritePropertyName("comparisonOptions");
            if (value.EdiOptions is not { } options) json.WriteNullValue();
            else
            {
                json.WriteStartObject();
                json.WriteBoolean("compareSeparators", options.CompareSeparators);
                json.WriteBoolean("ignoreControlNumbers", options.IgnoreControlNumbers);
                json.WriteBoolean("ignoreDates", options.IgnoreDates);
                json.WriteEndObject();
            }
        }
        json.WriteStartObject("limits");
        json.WriteNumber("maximumInputBytes", replay.Limits.MaximumInputBytes);
        json.WriteNumber("maximumOutputBytes", replay.Limits.MaximumOutputBytes);
        json.WriteNumber("maximumArtifacts", replay.Limits.MaximumArtifacts);
        json.WriteNumber("maximumTextBytes", replay.Limits.MaximumTextBytes);
        json.WriteEndObject();
        json.WriteStartArray("parameters");
        foreach (var parameter in replay.Parameters)
        {
            token.ThrowIfCancellationRequested();
            json.WriteStartObject();
            json.WriteString("name", parameter.Name);
            json.WriteString("kind", parameter.Kind switch
            {
                PreviewParameterKind.String => "string",
                PreviewParameterKind.Integer => "integer",
                PreviewParameterKind.Float => "float",
                PreviewParameterKind.Boolean => "boolean",
                _ => throw new RegressionCaseException(RegressionCaseFailure.InvalidInput)
            });
            json.WriteString("value", parameter.Value);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteStartArray("inputs");
        WriteFrameHeader(json, null, replay.Primary.ByteCount, replay.Primary.Digest, csvDialect: csv ? replay.Primary.CsvDialect : null, present: csv || native is not null, documentSettings: replay.Primary.DocumentSettings,
            xmlMetadata: replay.Primary.XmlMetadata, xmlRole: XmlDocumentRole.Input);
        foreach (var input in replay.NamedInputs)
        {
            token.ThrowIfCancellationRequested();
            WriteFrameHeader(json, input.Name, input.ByteCount, input.Digest, xmlMetadata: input.XmlMetadata, xmlRole: XmlDocumentRole.Input);
        }
        json.WriteEndArray();
        json.WriteStartArray("expected");
        foreach (var output in value.Expected.Outputs)
        {
            token.ThrowIfCancellationRequested();
            WriteFrameHeader(json, output.Key.Name, output.ByteCount, output.Digest, documents || csv || xml || native is not null ? output.MediaType : null, output.CsvDialect,
                documentSettings: output.DocumentSettings, xmlMetadata: output.XmlMetadata, xmlRole: XmlDocumentRole.Output);
        }
        json.WriteEndArray();
        json.WriteEndObject();
    }

    private static void WriteFrameHeader(Utf8JsonWriter json, string? name, int length, Sha256Digest digest, string? mediaType = null,
        CsvDocumentDialect? csvDialect = null, bool present = false,
        XmlDocumentMetadata? xmlMetadata = null, XmlDocumentRole xmlRole = XmlDocumentRole.Input, NativeDocumentSettings? documentSettings = null)
    {
        json.WriteStartObject();
        json.WriteString("name", name);
        if (present) json.WriteBoolean("present", true);
        json.WriteNumber("byteCount", length);
        json.WriteString("sha256", digest.Value);
        if (mediaType is not null) json.WriteString("mediaType", mediaType);
        if (csvDialect is not null) { json.WritePropertyName("csvDialect"); CsvDialectJson.Write(json, csvDialect); }
        if (documentSettings is not null) { json.WritePropertyName("documentSettings"); NativeDocumentSettingsJson.Write(json, documentSettings); }
        if (xmlMetadata is not null) { json.WritePropertyName("xmlMetadata"); XmlDocumentMetadataJson.Write(json, xmlMetadata, xmlRole); }
        json.WriteEndObject();
    }

    private static void Append(Stream stream, ReadOnlySpan<byte> frame, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, frame.Length);
        stream.Write(length);
        const int chunk = 64 * 1024;
        for (var offset = 0; offset < frame.Length; offset += chunk)
        {
            token.ThrowIfCancellationRequested();
            stream.Write(frame.Slice(offset, Math.Min(chunk, frame.Length - offset)));
        }
    }

    private static RegressionCaseException Integrity() => new(RegressionCaseFailure.StorageIntegrity);
    public override string ToString() => nameof(RegressionCaseEnvelopeCodec);
}
