using System.Text;
using Braid.Domain;

namespace Braid.Application;

/// <summary>Versioned bounded facts, not authority. Binary UTF-8 avoids inflating Unicode IDs in query URLs.</summary>
public static class CatalogSearchCursorCodec
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static string Encode(CatalogSearchCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Utf8, leaveOpen: true))
        {
            var key = cursor.After;
            writer.Write((byte)1);
            writer.Write(Convert.FromHexString(cursor.QueryFingerprint));
            writer.Write((byte)key.Kind);
            writer.Write(key.Project.OrganizationId.ToByteArray());
            writer.Write(key.Project.ProjectId.ToByteArray());
            writer.Write((key.MappingId ?? Guid.Empty).ToByteArray());
            writer.Write((key.RevisionId ?? Guid.Empty).ToByteArray());
            writer.Write((key.ProjectionDerivationId ?? Guid.Empty).ToByteArray());
            writer.Write(key.ResourceId ?? "");
        }
        return Base64(buffer.ToArray());
    }

    public static CatalogSearchCursor Decode(string encoded, Guid organizationId, CatalogSearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        ArgumentNullException.ThrowIfNull(query);
        try
        {
            if (encoded.Length is < 1 or > 4096 || encoded.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
                throw Invalid();
            var bytes = Convert.FromBase64String(encoded.Replace('-', '+').Replace('_', '/') + new string('=', (4 - encoded.Length % 4) % 4));
            using var buffer = new MemoryStream(bytes, writable: false);
            using var reader = new BinaryReader(buffer, Utf8);
            if (reader.ReadByte() != 1 || !reader.ReadBytes(32).AsSpan().SequenceEqual(Convert.FromHexString(query.Fingerprint))) throw Invalid();
            var kind = (CatalogSearchKind)reader.ReadByte();
            Guid Id() => new(reader.ReadBytes(16));
            Guid? OptionalId() { var id = Id(); return id == Guid.Empty ? null : id; }
            var project = new ProjectScope(Id(), Id());
            var mapping = OptionalId();
            var revision = OptionalId();
            var derivation = OptionalId();
            var resource = reader.ReadString();
            var cursor = new CatalogSearchCursor(query, new(kind, project, mapping, revision,
                resource.Length == 0 ? null : resource, derivation));
            cursor.RequireContext(organizationId, query);
            if (buffer.Position != buffer.Length || Encode(cursor) != encoded) throw Invalid();
            return cursor;
        }
        catch (Exception error) when (error is IOException or FormatException or ArgumentException)
        {
            throw Invalid();
        }
    }

    private static string Base64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static ArgumentException Invalid() => new("The search continuation is invalid.");
}
