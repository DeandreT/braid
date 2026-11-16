using System.Globalization;
using System.Text;
using System.Text.Json;
using Braid.Application;
using Braid.Domain;

namespace Braid.Api.Catalog;

/// <summary>Bounded continuation facts only; a cursor never grants authority or retains a database snapshot.</summary>
public static class CatalogCursorCodec
{
    public const int MaximumDecodedBytes = 512;

    public static string Encode(CatalogProjectCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        return Write("projects", "uuid-ascending", cursor.OrganizationId, null, null,
            cursor.AfterProjectId.ToString("D"));
    }

    public static string Encode(CatalogMappingCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        return Write("mappings", "uuid-ascending", cursor.Project.OrganizationId,
            cursor.Project.ProjectId, null, cursor.AfterMappingId.ToString("D"));
    }

    public static string Encode(CatalogRevisionCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        return Write("revisions", "sequence-descending", cursor.Mapping.OrganizationId,
            cursor.Mapping.ProjectId, cursor.Mapping.MappingId,
            cursor.BeforeSequence.ToString(CultureInfo.InvariantCulture));
    }

    public static CatalogProjectCursor DecodeProjects(string encoded, Guid organizationId)
    {
        using var document = Read(encoded, "projects", "uuid-ascending", false, false);
        var root = document.RootElement;
        if (ReadUuid(root, "organizationId") != organizationId)
            throw InvalidCursor();
        return new(organizationId, ReadUuid(root, "last"));
    }

    public static CatalogMappingCursor DecodeMappings(string encoded, ProjectScope project)
    {
        ArgumentNullException.ThrowIfNull(project);
        using var document = Read(encoded, "mappings", "uuid-ascending", true, false);
        var root = document.RootElement;
        if (ReadUuid(root, "organizationId") != project.OrganizationId ||
            ReadUuid(root, "projectId") != project.ProjectId)
            throw InvalidCursor();
        return new(project, ReadUuid(root, "last"));
    }

    public static CatalogRevisionCursor DecodeRevisions(string encoded, MappingScope mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        using var document = Read(encoded, "revisions", "sequence-descending", true, true);
        var root = document.RootElement;
        if (ReadUuid(root, "organizationId") != mapping.OrganizationId ||
            ReadUuid(root, "projectId") != mapping.ProjectId || ReadUuid(root, "mappingId") != mapping.MappingId)
            throw InvalidCursor();
        var last = ReadString(root, "last");
        if (last.Length > 19 || !long.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out var sequence) ||
            sequence < 1 || last != sequence.ToString(CultureInfo.InvariantCulture))
            throw InvalidCursor();
        return new(mapping, sequence);
    }

    private static string Write(string kind, string order, Guid organizationId, Guid? projectId,
        Guid? mappingId, string last)
    {
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", 1);
            writer.WriteString("kind", kind);
            writer.WriteString("order", order);
            writer.WriteString("organizationId", organizationId.ToString("D"));
            if (projectId is { } project) writer.WriteString("projectId", project.ToString("D"));
            if (mappingId is { } mapping) writer.WriteString("mappingId", mapping.ToString("D"));
            writer.WriteString("last", last);
            writer.WriteEndObject();
        }
        if (bytes.Length > MaximumDecodedBytes)
            throw new InvalidOperationException("The catalog continuation exceeds its wire limit.");
        return EncodeBytes(bytes.ToArray());
    }

    private static JsonDocument Read(string encoded, string kind, string order, bool hasProject, bool hasMapping)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        if (encoded.Length is < 1 or > 683 || encoded.Length % 4 == 1 ||
            encoded.Any(character => !(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or
                >= '0' and <= '9' or '-' or '_')))
            throw InvalidCursor();
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(encoded.Replace('-', '+').Replace('_', '/') +
                new string('=', (4 - encoded.Length % 4) % 4));
        }
        catch (FormatException) { throw InvalidCursor(); }
        if (bytes.Length is < 1 or > MaximumDecodedBytes || EncodeBytes(bytes) != encoded)
            throw InvalidCursor();
        try { _ = new UTF8Encoding(false, true).GetCharCount(bytes); }
        catch (DecoderFallbackException) { throw InvalidCursor(); }
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 2 });
        }
        catch (JsonException) { throw InvalidCursor(); }
        try
        {
            var root = document.RootElement;
            var expected = new HashSet<string>(["v", "kind", "order", "organizationId", "last"], StringComparer.Ordinal);
            if (hasProject) expected.Add("projectId");
            if (hasMapping) expected.Add("mappingId");
            if (root.ValueKind != JsonValueKind.Object)
                throw InvalidCursor();
            foreach (var property in root.EnumerateObject())
                if (!expected.Remove(property.Name)) throw InvalidCursor();
            if (expected.Count != 0 || root.GetProperty("v").GetRawText() != "1" ||
                ReadString(root, "kind") != kind || ReadString(root, "order") != order)
                throw InvalidCursor();
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    private static Guid ReadUuid(JsonElement root, string property)
    {
        var text = ReadString(root, property);
        if (!Guid.TryParseExact(text, "D", out var id) || id == Guid.Empty || text != id.ToString("D"))
            throw InvalidCursor();
        return id;
    }

    private static string ReadString(JsonElement root, string property)
    {
        var element = root.GetProperty(property);
        return element.ValueKind == JsonValueKind.String && element.GetString() is { } value ? value : throw InvalidCursor();
    }

    private static string EncodeBytes(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static ArgumentException InvalidCursor() => new("The catalog cursor is invalid.", "encoded");
}
