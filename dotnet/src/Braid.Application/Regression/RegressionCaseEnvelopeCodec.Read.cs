using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using Braid.Domain;

namespace Braid.Application.Regression;

public sealed partial class RegressionCaseEnvelopeCodec
{
    private static RegressionCaseVersion ReadHeader(JsonElement root, byte[] bytes, ref int offset, CancellationToken token)
    {
        var documents = Text(root, "schema") == DocumentSchema;
        var csv = Text(root, "schema") == CsvSchema;
        var native = NativeDocumentFormat.All.FirstOrDefault(f => f.CaseSchema == Text(root, "schema"));
        var xml = Text(root, "schema") == XmlSchema;
        string[] properties = ["schema", "caseId", "version", "owner", "name", "revision", "nativeDigest", "runtime", "target",
            "currentDateTime", "comparisonPolicy", "limits", "parameters", "inputs", "expected"];
        Shape(root, documents ? [.. properties, "comparisonOptions"] : properties);
        if (native is null && Text(root, "schema") is not (Schema or DocumentSchema or CsvSchema or XmlSchema)) throw Integrity();
        var caseId = Id(root, "caseId");
        var version = Integer(root, "version");
        var owner = new PrincipalId(Id(root, "owner"));
        var name = Text(root, "name");
        var scope = root.GetProperty("revision");
        Shape(scope, "organizationId", "projectId", "mappingId", "revisionId");
        var revision = new RevisionScope(Id(scope, "organizationId"), Id(scope, "projectId"),
            Id(scope, "mappingId"), Id(scope, "revisionId"));
        var nativeDigest = Digest(root, "nativeDigest");
        var runtime = root.GetProperty("runtime");
        Shape(runtime, "backendId", "engineRevision", "backendBuild", "adapterBuild", "profile");
        var identity = new RegressionRuntimeIdentity(Text(runtime, "backendId"), Text(runtime, "engineRevision"),
            Text(runtime, "backendBuild"), Text(runtime, "adapterBuild"), Text(runtime, "profile"));
        if (NativeDocumentFormat.FromProfile(identity.Profile) != native) throw Integrity();
        if (documents != (identity.Profile == DocumentPreviewPolicy.Profile)) throw Integrity();
        if (csv != (identity.Profile == CsvDocumentPolicy.Profile)) throw Integrity();
        if (xml != (identity.Profile == XmlPreviewAdmission.Profile)) throw Integrity();
        EdiComparisonOptions? comparisonOptions = null;
        if (documents && root.GetProperty("comparisonOptions").ValueKind != JsonValueKind.Null)
        {
            var options = root.GetProperty("comparisonOptions");
            Shape(options, "compareSeparators", "ignoreControlNumbers", "ignoreDates");
            comparisonOptions = new(options.GetProperty("compareSeparators").GetBoolean(), options.GetProperty("ignoreControlNumbers").GetBoolean(), options.GetProperty("ignoreDates").GetBoolean());
        }
        var targetElement = root.GetProperty("target");
        Shape(targetElement, "kind", "name");
        var targetName = NullableText(targetElement, "name");
        var target = Text(targetElement, "kind") switch
        {
            "all" when targetName is null => PreviewTargetSelection.All,
            "primary" when targetName is null => PreviewTargetSelection.Primary,
            "named" when targetName is not null => PreviewTargetSelection.Named(targetName),
            _ => throw Integrity()
        };
        var limits = root.GetProperty("limits");
        Shape(limits, "maximumInputBytes", "maximumOutputBytes", "maximumArtifacts", "maximumTextBytes");
        var selectedLimits = new PreviewLimits(checked((int)Integer(limits, "maximumInputBytes")),
            checked((int)Integer(limits, "maximumOutputBytes")), checked((int)Integer(limits, "maximumArtifacts")),
            checked((int)Integer(limits, "maximumTextBytes")));
        var parameters = BoundedArray(root, "parameters", 0, PreviewPolicy.MaximumParameters);
        var parameterList = new PreviewParameter[parameters.GetArrayLength()];
        var index = 0;
        foreach (var parameter in parameters.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            Shape(parameter, "name", "kind", "value");
            var kind = Text(parameter, "kind") switch
            {
                "string" => PreviewParameterKind.String,
                "integer" => PreviewParameterKind.Integer,
                "float" => PreviewParameterKind.Float,
                "boolean" => PreviewParameterKind.Boolean,
                _ => throw Integrity()
            };
            parameterList[index++] = new(Text(parameter, "name"), kind, Text(parameter, "value"));
        }
        var inputs = BoundedArray(root, "inputs", 1, PreviewPolicy.MaximumInputDocuments);
        var inputList = new PreviewInputDocument[inputs.GetArrayLength()];
        index = 0;
        var remaining = PreviewPolicy.MaximumInputBytes;
        foreach (var input in inputs.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            var frame = ReadFrame(input, bytes, ref offset, ref remaining, token, csv: csv, present: csv || native is not null, native: native, xml: xml ? XmlDocumentRole.Input : null);
            if (index == 0 != (frame.Name is null) || !csv && (native is null || !NativeDocumentPolicy.AllowsEmpty(native.Execution)) && frame.Bytes.Length == 0) throw Integrity();
            inputList[index++] = new(frame.Name, frame.Bytes, frame.CsvDialect, frame.XmlMetadata, frame.DocumentSettings);
        }
        var outputs = BoundedArray(root, "expected", 0, PreviewPolicy.MaximumArtifacts);
        var outputList = new RegressionOutput[outputs.GetArrayLength()];
        index = 0;
        remaining = PreviewPolicy.MaximumOutputBytes;
        foreach (var output in outputs.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            var frame = ReadFrame(output, bytes, ref offset, ref remaining, token, documents || csv || xml || native is not null, csv, native: native, xml: xml ? XmlDocumentRole.Output : null);
            outputList[index++] = RegressionOutput.Capture(frame.Name is null ? RegressionOutputKey.Primary :
                RegressionOutputKey.Named(frame.Name), frame.Bytes, token, frame.MediaType, frame.CsvDialect, frame.XmlMetadata, frame.DocumentSettings);
        }
        var replay = new RegressionReplay(target, Text(root, "currentDateTime"), selectedLimits,
            inputList[0], inputList.Skip(1).ToArray(), parameterList, token, native?.Execution ?? (xml ? PreviewExecutionProfile.XmlDocument :
            csv ? PreviewExecutionProfile.CsvDocument : PreviewExecutionProfile.LegacyJson));
        return new(caseId, version, owner, name, revision, nativeDigest, identity, replay,
            Text(root, "comparisonPolicy"), RegressionOutputSet.Capture(outputList, token), comparisonOptions);
    }

    private static (string? Name, byte[] Bytes, string MediaType, CsvDocumentDialect? CsvDialect, XmlDocumentMetadata? XmlMetadata, NativeDocumentSettings? DocumentSettings) ReadFrame(
        JsonElement descriptor, byte[] envelope, ref int offset, ref int remaining, CancellationToken token, bool documents = false, bool csv = false, bool present = false,
        XmlDocumentRole? xml = null, NativeDocumentFormat? native = null)
    {
        List<string> fields = ["name", "byteCount", "sha256"];
        if (documents) fields.Add("mediaType");
        if (csv) fields.Add("csvDialect");
        if (native is not null) fields.Add("documentSettings");
        if (present) fields.Add("present");
        if (xml is not null) fields.Add("xmlMetadata");
        Shape(descriptor, fields.ToArray());
        if (present && !descriptor.GetProperty("present").GetBoolean()) throw Integrity();
        var name = NullableText(descriptor, "name");
        if (name is not null) RegressionCaseValues.Name(name);
        var count = Integer(descriptor, "byteCount");
        if (count < 0 || count > remaining || offset > envelope.Length - 4) throw Integrity();
        var length = BinaryPrimitives.ReadInt32BigEndian(envelope.AsSpan(offset, 4));
        offset += 4;
        if (length != count || length > envelope.Length - offset) throw Integrity();
        var bytes = envelope.AsSpan(offset, length);
        if (Sha256Digest.FromBytes(bytes) != Digest(descriptor, "sha256")) throw Integrity();
        token.ThrowIfCancellationRequested();
        offset += length;
        remaining -= length;
        return (name, bytes.ToArray(), documents ? Text(descriptor, "mediaType") : PreviewPolicy.MediaType,
            csv ? CsvDialectJson.Read(descriptor.GetProperty("csvDialect")) : null,
            xml is { } role ? XmlDocumentMetadataJson.Read(descriptor.GetProperty("xmlMetadata"), role) : null, native is null ? null : NativeDocumentSettingsJson.Read(descriptor.GetProperty("documentSettings")));
    }

    private static void Shape(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Integrity();
        var remaining = new HashSet<string>(names, StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!remaining.Remove(property.Name)) throw Integrity();
        if (remaining.Count != 0) throw Integrity();
    }

    private static JsonElement BoundedArray(JsonElement root, string property, int minimum, int maximum)
    {
        var value = root.GetProperty(property);
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() < minimum || value.GetArrayLength() > maximum)
            throw Integrity();
        return value;
    }

    private static string Text(JsonElement root, string property)
    {
        var value = root.GetProperty(property);
        return value.ValueKind == JsonValueKind.String ? value.GetString()! : throw Integrity();
    }

    private static string? NullableText(JsonElement root, string property) =>
        root.GetProperty(property).ValueKind == JsonValueKind.Null ? null : Text(root, property);

    private static long Integer(JsonElement root, string property)
    {
        var value = root.GetProperty(property);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var result) ||
            value.GetRawText() != result.ToString(CultureInfo.InvariantCulture)) throw Integrity();
        return result;
    }

    private static Guid Id(JsonElement root, string property)
    {
        var text = Text(root, property);
        if (!Guid.TryParseExact(text, "D", out var id) || id == Guid.Empty || text != id.ToString("D")) throw Integrity();
        return id;
    }

    private static Sha256Digest Digest(JsonElement root, string property)
    {
        var text = Text(root, property);
        var digest = Sha256Digest.Parse(text);
        if (text != digest.Value) throw Integrity();
        return digest;
    }
}
