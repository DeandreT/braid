using System.Text;
using System.Xml;

namespace Braid.Application;

/// <summary>
/// Physical XML preflight only, not schema/projection or compiler admission. Scan original bytes before a native
/// payload reader and scan every result before publication. The pinned general native XML reader permits DTDs;
/// this preflight must not be skipped when that reader is integrated. No DOM or external resource is created here.
/// </summary>
public static class XmlDocumentSafety
{
    private const int ChunkSize = 4096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static XmlDocumentStatistics Validate(ReadOnlySpan<byte> bytes, CancellationToken cancellationToken = default,
        int maximumBytes = XmlDocumentPolicy.MaximumBytes)
    {
        CheckByteLimit(maximumBytes);
        cancellationToken.ThrowIfCancellationRequested();
        var nodes = 0;
        var maximumDepth = 0;
        var maximumAttributes = 0;
        var originalBytes = bytes.Length;
        XmlDocumentStatistics Statistics() => new(originalBytes, nodes, maximumDepth, maximumAttributes);
        if (bytes.Length > maximumBytes)
            throw new XmlDocumentException(XmlDocumentFailure.ByteLimit, statistics: Statistics(), observed: bytes.Length, limit: maximumBytes);
        if (bytes.IsEmpty) throw new XmlDocumentException(XmlDocumentFailure.DocumentRequired, statistics: Statistics());
        string text;
        try { text = Decode(bytes, cancellationToken); }
        catch (DecoderFallbackException)
        {
            throw new XmlDocumentException(XmlDocumentFailure.InvalidUtf8, statistics: Statistics());
        }
        using var source = new CancellableTextReader(text, cancellationToken);
        if (text.StartsWith('\ufeff')) source.Read(); // Strip one initial decoded BOM, never the retained bytes.
        using var reader = XmlReader.Create(source, new XmlReaderSettings
        {
            ConformanceLevel = ConformanceLevel.Document,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            CheckCharacters = true,
            ValidationType = ValidationType.None,
            IgnoreWhitespace = false,
            IgnoreComments = false,
            IgnoreProcessingInstructions = false,
            MaxCharactersInDocument = maximumBytes,
            MaxCharactersFromEntities = maximumBytes
        });
        var chunk = new char[ChunkSize];
        nodes = 1; // The document node; declarations and outside-root whitespace do not add nodes.
        var roots = 0;
        var inTextRun = false;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!reader.Read()) break;
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        inTextRun = false;
                        if (reader.Depth == 0) roots++;
                        maximumDepth = Math.Max(maximumDepth, checked(reader.Depth + 1));
                        if (maximumDepth > XmlDocumentPolicy.MaximumDepth)
                            throw Refuse(XmlDocumentFailure.DepthLimit, maximumDepth, XmlDocumentPolicy.MaximumDepth);
                        AddNode();
                        CheckName(reader.Name);
                        maximumAttributes = Math.Max(maximumAttributes, reader.AttributeCount);
                        if (maximumAttributes > XmlDocumentPolicy.MaximumAttributes)
                            throw Refuse(XmlDocumentFailure.AttributeLimit, maximumAttributes, XmlDocumentPolicy.MaximumAttributes);
                        if (reader.MoveToFirstAttribute())
                        {
                            do
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                CheckName(reader.Name);
                                DrainValue();
                            } while (reader.MoveToNextAttribute());
                            reader.MoveToElement();
                        }
                        break;
                    case XmlNodeType.Text:
                    case XmlNodeType.CDATA:
                    case XmlNodeType.Whitespace:
                    case XmlNodeType.SignificantWhitespace:
                        if (reader.Depth > 0)
                        {
                            if (!inTextRun) AddNode();
                            inTextRun = true;
                        }
                        else inTextRun = false;
                        DrainValue();
                        break;
                    case XmlNodeType.Comment:
                    case XmlNodeType.ProcessingInstruction:
                        inTextRun = false;
                        AddNode();
                        if (reader.NodeType == XmlNodeType.ProcessingInstruction) CheckName(reader.Name);
                        DrainValue();
                        break;
                    case XmlNodeType.XmlDeclaration:
                        inTextRun = false;
                        if (!reader.MoveToAttribute("version")) throw Refuse(XmlDocumentFailure.InvalidDeclaration);
                        RequireValue("1.0", false);
                        if (reader.MoveToAttribute("encoding")) RequireValue("UTF-8", true);
                        reader.MoveToElement();
                        break;
                    case XmlNodeType.EndElement:
                        inTextRun = false;
                        break;
                    default:
                        // No DTD, entity-reference, fragment or resolver path is admitted. BCL DTD/entity
                        // syntax exceptions below use the same fixed refusal, never message classification.
                        throw Refuse(XmlDocumentFailure.InvalidXml);
                }
            }
        }
        catch (XmlException error)
        {
            throw new XmlDocumentException(XmlDocumentFailure.InvalidXml,
                new(error.LineNumber, error.LinePosition), Statistics());
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (roots != 1) throw new XmlDocumentException(XmlDocumentFailure.DocumentRequired, statistics: Statistics());
        return Statistics();

        XmlDocumentException Refuse(XmlDocumentFailure reason, long? observed = null, long? limit = null) =>
            new(reason, reader is IXmlLineInfo info && info.HasLineInfo()
                ? new(info.LineNumber, info.LinePosition) : null, Statistics(), observed, limit);

        void AddNode()
        {
            if (++nodes > XmlDocumentPolicy.MaximumNodes)
                throw Refuse(XmlDocumentFailure.NodeLimit, nodes, XmlDocumentPolicy.MaximumNodes);
        }

        void CheckName(string name)
        {
            for (var index = 0; index < name.Length; index++)
            {
                if ((index & (ChunkSize - 1)) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (char.IsSurrogate(name[index])) throw Refuse(XmlDocumentFailure.UnsupportedName);
            }
        }

        void DrainValue()
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.ReadValueChunk(chunk, 0, chunk.Length) == 0) return;
            }
        }

        void RequireValue(string expected, bool ignoreCase)
        {
            var offset = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = reader.ReadValueChunk(chunk, 0, chunk.Length);
                if (count == 0) break;
                for (var index = 0; index < count; index++, offset++)
                    if (offset >= expected.Length || (ignoreCase ? char.ToUpperInvariant(chunk[index]) : chunk[index]) != expected[offset])
                        throw Refuse(XmlDocumentFailure.InvalidDeclaration);
            }
            if (offset != expected.Length) throw Refuse(XmlDocumentFailure.InvalidDeclaration);
        }
    }

    /// <summary>Checks the complete original-byte ledger before parsing any member. No partial set is returned.</summary>
    public static IReadOnlyList<XmlDocumentStatistics> ValidateSet(IReadOnlyList<ReadOnlyMemory<byte>> documents,
        XmlDocumentRole role, CancellationToken cancellationToken = default, int maximumBytes = XmlDocumentPolicy.MaximumBytes)
    {
        ArgumentNullException.ThrowIfNull(documents);
        CheckByteLimit(maximumBytes);
        cancellationToken.ThrowIfCancellationRequested();
        var maximumDocuments = role switch
        {
            XmlDocumentRole.Input => XmlDocumentPolicy.MaximumInputs,
            XmlDocumentRole.Output => XmlDocumentPolicy.MaximumOutputs,
            _ => throw new ArgumentOutOfRangeException(nameof(role))
        };
        if (documents.Count is < 1 || documents.Count > maximumDocuments)
            throw new XmlDocumentException(XmlDocumentFailure.DocumentCountLimit, observed: documents.Count, limit: maximumDocuments);
        long total = 0;
        foreach (var document in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            total = checked(total + document.Length);
            if (total > maximumBytes)
                throw new XmlDocumentException(XmlDocumentFailure.ByteLimit, observed: total, limit: maximumBytes);
        }
        var results = new XmlDocumentStatistics[documents.Count];
        for (var index = 0; index < results.Length; index++)
            results[index] = Validate(documents[index].Span, cancellationToken, maximumBytes);
        return Array.AsReadOnly(results);
    }

    private static string Decode(ReadOnlySpan<byte> bytes, CancellationToken token)
    {
        var decoder = StrictUtf8.GetDecoder();
        var text = new StringBuilder(Math.Min(bytes.Length, ChunkSize));
        Span<char> chunk = stackalloc char[ChunkSize];
        while (!bytes.IsEmpty)
        {
            token.ThrowIfCancellationRequested();
            var length = Math.Min(bytes.Length, ChunkSize);
            decoder.Convert(bytes[..length], chunk, length == bytes.Length, out var usedBytes, out var usedChars, out _);
            text.Append(chunk[..usedChars]);
            bytes = bytes[usedBytes..];
        }
        token.ThrowIfCancellationRequested();
        return text.ToString();
    }

    private static void CheckByteLimit(int maximumBytes)
    {
        if (maximumBytes is <= 0 or > XmlDocumentPolicy.MaximumBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
    }

    // XmlReader can consume a complete comment, name or start tag within Read(). Poll even during
    // its internal refills, rather than waiting for the next public event or value chunk.
    private sealed class CancellableTextReader(string text, CancellationToken token) : StringReader(text)
    {
        public override int Peek()
        {
            token.ThrowIfCancellationRequested();
            return base.Peek();
        }

        public override int Read()
        {
            token.ThrowIfCancellationRequested();
            return base.Read();
        }

        public override int Read(char[] buffer, int index, int count)
        {
            token.ThrowIfCancellationRequested();
            return base.Read(buffer, index, Math.Min(count, ChunkSize));
        }

        public override int Read(Span<char> buffer)
        {
            token.ThrowIfCancellationRequested();
            return base.Read(buffer[..Math.Min(buffer.Length, ChunkSize)]);
        }
    }
}
