using System.Buffers.Binary;
using System.Text.Json;

namespace Weft.MappingBackends.Workers;

/// <summary>The dedicated preview protocol cannot enter through a legacy service profile.</summary>
public static class AuthoredPreviewFrameAdmission
{
    public static bool Allows(DockerWorkerProfile profile, ReadOnlyMemory<byte> frame)
    {
        var selected = profile == DockerWorkerProfile.AuthoredPreview;
        if (frame.Length < 4) return !selected;
        var length = BinaryPrimitives.ReadUInt32BigEndian(frame.Span);
        if (length is 0 or > MappingAuthoredPreviewPolicy.MaximumHeaderBytes || frame.Length < 4L + length) return !selected;
        try
        {
            using var document = JsonDocument.Parse(frame.Slice(4, (int)length));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return !selected;
            var declarations = document.RootElement.EnumerateObject().Where(p => p.NameEquals("protocolVersion")).ToArray();
            var targetsPreview = declarations.Any(p => p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() == FerruleInspectionBackend.AuthoredPreviewProtocolVersion);
            return selected ? declarations.Length == 1 && targetsPreview : !targetsPreview;
        }
        catch (JsonException) { return !selected; }
    }
}
