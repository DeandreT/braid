using System.Buffers.Binary;
using System.Text.Json;

namespace Braid.MappingBackends.Workers;

/// <summary>Authoring cannot enter through another served profile when its service flag is disabled.</summary>
public static class AuthoringFrameAdmission
{
    public static bool Allows(DockerWorkerProfile profile, ReadOnlyMemory<byte> frame)
    {
        // Preserve legacy profile parsing: malformed/non-JSON requests still reach their own bounded parser.
        var authoring = profile == DockerWorkerProfile.Authoring;
        if (frame.Length < 4) return !authoring;
        var length = BinaryPrimitives.ReadUInt32BigEndian(frame.Span);
        if (length is 0 or > InspectionPolicy.MaximumHeaderBytes || frame.Length < 4L + length) return !authoring;
        try
        {
            using var header = JsonDocument.Parse(frame.Slice(4, (int)length));
            if (header.RootElement.ValueKind != JsonValueKind.Object) return !authoring;
            var declarations = header.RootElement.EnumerateObject().Where(item => item.NameEquals("protocolVersion")).ToArray();
            var selectsAuthoring = declarations.Any(item => item.Value.ValueKind == JsonValueKind.String
                && item.Value.GetString() == FerruleInspectionBackend.AuthoringProtocolVersion);
            return authoring ? declarations.Length == 1 && selectsAuthoring : !selectsAuthoring;
        }
        catch (JsonException) { return !authoring; }
    }
}
