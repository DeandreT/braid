using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Weft.Domain;

namespace Weft.Application;

// Worker-side implementation only. Not public, not DI registered, and never reached by SpecSourceExtractor.
internal static partial class SpecOfficeParser
{
    private sealed class Refusal(SpecExtractionOutcome outcome, string code) : Exception
    { internal SpecExtractionOutcome Outcome { get; } = outcome; internal string Code { get; } = code; }
    [DoesNotReturn] private static void Limit(string code) => throw new Refusal(SpecExtractionOutcome.LimitExceeded, code);
    [DoesNotReturn] private static void Bad(string code) => throw new Refusal(SpecExtractionOutcome.Malformed, code);
    [DoesNotReturn] private static void Unsupported(string code) => throw new Refusal(SpecExtractionOutcome.Unsupported, code);

    internal static SpecOfficeExtractionResult Extract(SpecCapturedSource source, SpecOfficeExtractionLimits? limits = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        limits ??= new(); token.ThrowIfCancellationRequested();
        SpecOfficeExtractionResult Failure(SpecExtractionOutcome outcome, string code) => new(source, limits, outcome, code, []);
        try
        {
            if (source.Format is not (SpecSourceFormat.Docx or SpecSourceFormat.Xlsx)) Unsupported("unsupported-office-format");
            if (source.ByteCount > limits.MaximumSourceBytes) Limit("source-bytes");
            var bytes = source.CopyBytes();
            if (bytes.AsSpan().StartsWith(new byte[] { 0xd0, 0xcf, 0x11, 0xe0 })) Unsupported("legacy-or-encrypted-office");
            var package = new Package(bytes, limits, token);
            var chunks = source.Format == SpecSourceFormat.Docx ? package.Word() : package.Excel();
            token.ThrowIfCancellationRequested();
            // Narrow structural extraction is never a claim of complete document/rendered content coverage.
            return new(source, limits, SpecExtractionOutcome.Partial,
                source.Format == SpecSourceFormat.Docx ? "office-body-text-only" : "office-stored-cells-only", chunks);
        }
        catch (Refusal e) { return Failure(e.Outcome, e.Code); }
        catch (InvalidDataException) { return Failure(SpecExtractionOutcome.Malformed, "invalid-office-zip"); }
        catch (XmlException) { return Failure(SpecExtractionOutcome.Malformed, "invalid-office-xml"); }
        catch (DecoderFallbackException) { return Failure(SpecExtractionOutcome.Malformed, "invalid-package-name"); }
    }

    private sealed partial class Package
    {
        private readonly SpecOfficeExtractionLimits limits;
        private readonly CancellationToken token;
        private readonly Dictionary<string, XDocument> parts = new(StringComparer.Ordinal);
        private readonly List<SpecOfficeEvidenceChunk> chunks = [];
        private int evidenceBytes;
        private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";
        private static readonly XNamespace Content = "http://schemas.openxmlformats.org/package/2006/content-types";
        private const string OfficeRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";
        private readonly Dictionary<string, Dictionary<string, (string Type, string Part)>> relationships = new(StringComparer.Ordinal);

        internal Package(byte[] bytes, SpecOfficeExtractionLimits limits, CancellationToken token)
        {
            this.limits = limits; this.token = token;
            var metadata = CheckZip(bytes);
            using var stream = new MemoryStream(bytes, false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, false, Encoding.UTF8);
            if (archive.Entries.Count != metadata.Count) Bad("zip-directory-mismatch");
            var total = 0; var nodes = 0;
            foreach (var entry in archive.Entries)
            {
                token.ThrowIfCancellationRequested();
                if (!metadata.TryGetValue(entry.FullName, out var meta)) Bad("zip-directory-mismatch");
                using var expanded = new MemoryStream();
                using (var input = entry.Open())
                {
                    var buffer = new byte[8192];
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        // At most one detection byte past any effective budget, even for a dishonest directory.
                        var remaining = Math.Min(limits.MaximumEntryBytes - expanded.Length, limits.MaximumExpandedBytes - total);
                        remaining = Math.Min(remaining, Math.Max(1L, meta.Compressed) * limits.MaximumCompressionRatio - expanded.Length);
                        var count = input.Read(buffer, 0, (int)Math.Min(buffer.Length, Math.Max(1, remaining + 1)));
                        if (count == 0) break;
                        if (expanded.Length + count > limits.MaximumEntryBytes) Limit("zip-entry-bytes");
                        if (total + count > limits.MaximumExpandedBytes) Limit("zip-expanded-bytes");
                        if (expanded.Length + count > Math.Max(1L, meta.Compressed) * limits.MaximumCompressionRatio) Limit("zip-ratio");
                        expanded.Write(buffer, 0, count); total += count;
                    }
                }
                var content = expanded.ToArray();
                if (content.Length != meta.Expanded || Crc(content) != meta.Crc) Bad("zip-content-mismatch");
                if (!entry.FullName.EndsWith(".xml", StringComparison.Ordinal) && !entry.FullName.EndsWith(".rels", StringComparison.Ordinal))
                    Unsupported("unsupported-package-part");
                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = limits.MaximumEntryBytes,
                    MaxCharactersFromEntities = 0,
                    IgnoreComments = false
                };
                using (var xml = XmlReader.Create(new MemoryStream(content, false), settings))
                    while (xml.Read())
                    {
                        token.ThrowIfCancellationRequested();
                        if (xml.Depth > limits.MaximumXmlDepth) Limit("xml-depth");
                        nodes += 1 + xml.AttributeCount;
                        if (nodes > limits.MaximumXmlNodes) Limit("xml-nodes");
                    }
                using var reader = XmlReader.Create(new MemoryStream(content, false), settings);
                parts.Add(entry.FullName, XDocument.Load(reader, LoadOptions.PreserveWhitespace));
            }
            ReadRelationships();
            CheckContentTypes();
        }

        private sealed record ZipMeta(uint Compressed, uint Expanded, uint Crc);
        private Dictionary<string, ZipMeta> CheckZip(byte[] bytes)
        {
            // Classic single-disk ZIP only; ZIP64 and encrypted entries have no admitted implementation.
            var end = -1;
            for (var i = bytes.Length - 22; i >= Math.Max(0, bytes.Length - 65557); i--)
                if (U32(bytes, i) == 0x06054b50 && i + 22 + U16(bytes, i + 20) == bytes.Length) { end = i; break; }
            if (end < 0) Bad("invalid-zip-directory");
            var count = U16(bytes, end + 10);
            if (count == ushort.MaxValue || U32(bytes, end + 12) == uint.MaxValue || U32(bytes, end + 16) == uint.MaxValue) Unsupported("zip64-unsupported");
            if (U16(bytes, end + 4) != 0 || U16(bytes, end + 6) != 0 || U16(bytes, end + 8) != count) Unsupported("multi-disk-zip");
            if (count > limits.MaximumEntries) Limit("zip-entries");
            var central = (long)U32(bytes, end + 16);
            if (central + U32(bytes, end + 12) != end) Bad("invalid-zip-directory");
            var result = new Dictionary<string, ZipMeta>(StringComparer.OrdinalIgnoreCase);
            var ranges = new List<(long Start, long End)>();
            for (var n = 0; n < count; n++)
            {
                token.ThrowIfCancellationRequested();
                if (central < 0 || central + 46 > end) Bad("invalid-zip-directory");
                var p = (int)central;
                if (U32(bytes, p) != 0x02014b50) Bad("invalid-zip-directory");
                var flags = U16(bytes, p + 8); var method = U16(bytes, p + 10);
                if ((flags & 0x41) != 0) Unsupported("encrypted-zip");
                if (method is not (0 or 8)) Unsupported("zip-compression-unsupported");
                var compressed = U32(bytes, p + 20); var expanded = U32(bytes, p + 24);
                if (compressed == uint.MaxValue || expanded == uint.MaxValue) Unsupported("zip64-unsupported");
                if (expanded > limits.MaximumEntryBytes) Limit("zip-entry-bytes");
                if (expanded > Math.Max(1L, compressed) * limits.MaximumCompressionRatio) Limit("zip-ratio");
                var length = U16(bytes, p + 28); var extra = U16(bytes, p + 30); var comment = U16(bytes, p + 32);
                central += 46 + length + extra + comment;
                if (central > end || U16(bytes, p + 34) != 0) Bad("invalid-zip-directory");
                var name = new UTF8Encoding(false, true).GetString(bytes, p + 46, length);
                CheckPart(name);
                if (name.Contains("vba", StringComparison.OrdinalIgnoreCase) || name.Contains("macro", StringComparison.OrdinalIgnoreCase) || name.Contains("activeX", StringComparison.OrdinalIgnoreCase))
                    Unsupported("active-office-content");
                if (!result.TryAdd(name, new(compressed, expanded, U32(bytes, p + 16)))) Bad("duplicate-package-part");
                var local = (long)U32(bytes, p + 42);
                if (local + 30 > U32(bytes, end + 16)) Bad("invalid-local-header");
                var l = (int)local;
                if (U32(bytes, l) != 0x04034b50 || U16(bytes, l + 6) != flags || U16(bytes, l + 8) != method) Bad("invalid-local-header");
                var localName = U16(bytes, l + 26); var localExtra = U16(bytes, l + 28);
                var finish = local + 30 + localName + localExtra + compressed;
                if (finish > U32(bytes, end + 16) || localName != length || !bytes.AsSpan(l + 30, localName).SequenceEqual(bytes.AsSpan(p + 46, length))) Bad("invalid-local-header");
                var crc = U32(bytes, p + 16);
                if ((flags & 8) == 0)
                {
                    if (U32(bytes, l + 14) != crc || U32(bytes, l + 18) != compressed || U32(bytes, l + 22) != expanded)
                        Bad("conflicting-local-header");
                }
                else
                {
                    // Streaming writers may leave zero placeholders or repeat the final facts in the local header.
                    if ((U32(bytes, l + 14) != 0 && U32(bytes, l + 14) != crc) ||
                        (U32(bytes, l + 18) != 0 && U32(bytes, l + 18) != compressed) ||
                        (U32(bytes, l + 22) != 0 && U32(bytes, l + 22) != expanded)) Bad("conflicting-local-header");
                    // Classic descriptors may include a signature. Refuse ambiguous CRC/signature encodings rather than guessing.
                    var directoryStart = U32(bytes, end + 16);
                    if (finish + 12 > directoryStart) Bad("invalid-data-descriptor");
                    var descriptor = (int)finish;
                    var signed = U32(bytes, descriptor) == 0x08074b50;
                    if (signed) descriptor += 4;
                    finish += signed ? 16 : 12;
                    if (finish > directoryStart || U32(bytes, descriptor) != crc || U32(bytes, descriptor + 4) != compressed || U32(bytes, descriptor + 8) != expanded)
                        Bad("invalid-data-descriptor");
                }
                if (ranges.Any(r => local < r.End && finish > r.Start)) Bad("overlapping-zip-entries");
                ranges.Add((local, finish));
            }
            if (central != end) Bad("invalid-zip-directory");
            return result;
        }
        private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
        private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
        private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(i =>
        { var value = (uint)i; for (var bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xedb88320 ^ (value >> 1) : value >> 1; return value; }).ToArray();
        private static uint Crc(byte[] content)
        { var value = uint.MaxValue; foreach (var b in content) value = CrcTable[(value ^ b) & 255] ^ (value >> 8); return ~value; }
        private static void CheckPart(string name)
        {
            if (name.Length is 0 or > 240 || name.Split('/').Any(p => p.Length == 0 || p is "." or "..") ||
                name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '/' or '_' or '-' or '.' or '[' or ']'))) Bad("unsafe-package-path");
        }
        private XDocument Part(string name) => parts.TryGetValue(name, out var doc) ? doc : throw new Refusal(SpecExtractionOutcome.Malformed, "missing-package-part");

        private void ReadRelationships()
        {
            foreach (var (name, doc) in parts.Where(p => p.Key.EndsWith(".rels", StringComparison.Ordinal)))
            {
                if (doc.Root?.Name != Rel + "Relationships") Bad("invalid-relationships");
                var source = "";
                if (name != "_rels/.rels")
                {
                    var marker = name.LastIndexOf("/_rels/", StringComparison.Ordinal);
                    if (marker < 0) Bad("invalid-relationships");
                    source = name[..marker] + "/" + name[(marker + 7)..^5];
                    _ = Part(source);
                }
                var map = new Dictionary<string, (string Type, string Part)>(StringComparer.Ordinal);
                foreach (var item in doc.Root!.Elements())
                {
                    if (item.Name != Rel + "Relationship") Bad("invalid-relationships");
                    var id = (string?)item.Attribute("Id"); var type = (string?)item.Attribute("Type"); var target = (string?)item.Attribute("Target");
                    if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(type) || string.IsNullOrEmpty(target)) Bad("invalid-relationships");
                    if (item.Attribute("TargetMode") is { } mode && mode.Value != "Internal") Unsupported("external-relationship");
                    // Deliberately narrow canonical relative paths; no URI decoding, traversal or network resolution.
                    CheckPart(target!);
                    var slash = source.LastIndexOf('/');
                    var resolved = (slash < 0 ? "" : source[..(slash + 1)]) + target;
                    CheckPart(resolved); _ = Part(resolved);
                    if (!map.TryAdd(id!, (type!, resolved))) Bad("duplicate-relationship");
                    var kind = type!.StartsWith(OfficeRel, StringComparison.Ordinal) ? type[OfficeRel.Length..] : type;
                    if (kind is not ("officeDocument" or "worksheet" or "sharedStrings" or "styles" or "theme" or "settings" or "fontTable" or "webSettings" or "numbering" or "extended-properties") &&
                        kind != "http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties") Unsupported("unsupported-relationship");
                }
                relationships.Add(source, map);
            }
        }
        private void CheckContentTypes()
        {
            var doc = Part("[Content_Types].xml");
            if (doc.Root?.Name != Content + "Types") Bad("invalid-content-types");
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in doc.Root!.Elements())
            {
                if (item.Name != Content + "Default" && item.Name != Content + "Override") Bad("invalid-content-types");
                var type = (string?)item.Attribute("ContentType");
                var key = (string?)item.Attribute(item.Name.LocalName == "Default" ? "Extension" : "PartName");
                if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(key) || !keys.Add(item.Name.LocalName + key)) Bad("invalid-content-types");
                if (type!.Contains("macro", StringComparison.OrdinalIgnoreCase) || type.Contains("vba", StringComparison.OrdinalIgnoreCase) || type.Contains("activeX", StringComparison.OrdinalIgnoreCase)) Unsupported("active-office-content");
                if (item.Name.LocalName == "Override")
                { if (!key!.StartsWith('/')) Bad("invalid-content-types"); CheckPart(key![1..]); _ = Part(key[1..]); }
            }
        }
        private string MainPart(string expectedType)
        {
            if (!relationships.TryGetValue("", out var root)) Bad("missing-office-relationship");
            var main = root!.Values.Where(r => r.Type == OfficeRel + "officeDocument").ToArray();
            if (main.Length != 1) Bad("invalid-office-relationship");
            var part = main[0].Part;
            var declared = Part("[Content_Types].xml").Root!.Elements(Content + "Override")
                .SingleOrDefault(e => (string?)e.Attribute("PartName") == "/" + part);
            if ((string?)declared?.Attribute("ContentType") != expectedType) Unsupported("office-format-mismatch");
            return part;
        }
        private void Add(string text, SpecEvidenceLocator locator, string? formula = null)
        {
            token.ThrowIfCancellationRequested();
            if (chunks.Count >= limits.MaximumChunks) Limit("office-chunks");
            evidenceBytes += Encoding.UTF8.GetByteCount(text) + (formula is null ? 0 : Encoding.UTF8.GetByteCount(formula));
            if (evidenceBytes > limits.MaximumEvidenceBytes) Limit("evidence-bytes");
            chunks.Add(new(chunks.Count, text, locator, formula));
        }
    }
}
