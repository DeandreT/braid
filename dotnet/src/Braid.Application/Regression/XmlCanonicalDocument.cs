using System.Security.Cryptography.Xml;
using System.Text;
using System.Xml;

namespace Braid.Application.Regression;

/// <summary>
/// Complete-document inclusive C14N 1.0 with comments. Only a future qualified, terminable worker may host this
/// core. LoadInput and GetOutput are synchronous and eagerly allocate; cancellation polls cannot interrupt them.
/// </summary>
internal sealed class XmlCanonicalDocument : IDisposable
{
    public const string AlgorithmUri = "http://www.w3.org/TR/2001/REC-xml-c14n-20010315#WithComments";
    private const int ChunkSize = 4096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Stream stream;
    private long consumed;
    private bool endChecked;

    private XmlCanonicalDocument(Stream stream, long length)
    {
        this.stream = stream;
        Length = length;
    }

    public long Length { get; }

    public static XmlCanonicalDocument Open(ReadOnlySpan<byte> original, int maximumOriginalBytes,
        long maximumCanonicalBytes, CancellationToken token)
    {
        if (maximumOriginalBytes is <= 0 or > XmlDocumentPolicy.MaximumBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumOriginalBytes));
        if (maximumCanonicalBytes < 0 || maximumCanonicalBytes == long.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(maximumCanonicalBytes));
        token.ThrowIfCancellationRequested();
        var phase = XmlCanonicalPhase.Safety;
        try
        {
            try { XmlDocumentSafety.Validate(original, token, maximumOriginalBytes); }
            catch (XmlDocumentException error)
            {
                throw new XmlCanonicalizationException(error.IsResourceLimit ? XmlCanonicalFailure.ResourceLimit :
                    XmlCanonicalFailure.InvalidDocument, XmlCanonicalPhase.Safety, error.Observed, error.Limit);
            }

            // Strict decoding has already completed successfully in the physical scan. Preserve the decoded view,
            // removing only its initial BOM for both subsequent readers, never from the captured original bytes.
            token.ThrowIfCancellationRequested();
            var text = StrictUtf8.GetString(original);
            token.ThrowIfCancellationRequested();
            var offset = text.StartsWith('\ufeff') ? 1 : 0;
            phase = XmlCanonicalPhase.NamespaceDomain;
            CheckNamespaceDomain(text, offset, maximumOriginalBytes, token);
            phase = XmlCanonicalPhase.Canonicalization;
            var document = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
            using (var source = new CancellableTextReader(text, offset, token))
            using (var reader = XmlReader.Create(source, Settings(maximumOriginalBytes)))
                document.Load(reader);
            token.ThrowIfCancellationRequested();

            var transform = new XmlDsigC14NWithCommentsTransform { Resolver = null };
            if (!StringComparer.Ordinal.Equals(transform.Algorithm, AlgorithmUri))
                throw Failure(XmlCanonicalPhase.Canonicalization);
            token.ThrowIfCancellationRequested();
            transform.LoadInput(document);
            token.ThrowIfCancellationRequested();
            var result = transform.GetOutput(typeof(Stream)) as Stream;
            if (result is null) throw Failure(XmlCanonicalPhase.Canonicalization);
            try
            {
                token.ThrowIfCancellationRequested();
                if (!result.CanRead || !result.CanSeek) throw Failure(XmlCanonicalPhase.Canonicalization);
                var length = result.Length;
                if (length < 0) throw Failure(XmlCanonicalPhase.Canonicalization);
                if (length > maximumCanonicalBytes)
                    throw new XmlCanonicalizationException(XmlCanonicalFailure.ResourceLimit,
                        XmlCanonicalPhase.Canonicalization, length, maximumCanonicalBytes);
                result.Position = 0;
                return new(result, length);
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }
        catch (OutOfMemoryException)
        {
            throw new XmlCanonicalizationException(XmlCanonicalFailure.ResourceLimit, phase);
        }
        catch (Exception error) when (error is not (OperationCanceledException or XmlCanonicalizationException))
        {
            // The package is a failure boundary. Do not retain its exception, message or payload-bearing data.
            throw Failure(phase);
        }
    }

    /// <summary>Read bounded chunks only; verify the returned stream agrees with its admitted complete length.</summary>
    public int Read(Span<byte> buffer, CancellationToken token)
    {
        if (buffer.IsEmpty) throw new ArgumentException("A read buffer is required.", nameof(buffer));
        token.ThrowIfCancellationRequested();
        try
        {
            var wanted = (int)Math.Min(Math.Min(buffer.Length, ChunkSize), Length - consumed);
            var read = 0;
            while (read < wanted)
            {
                token.ThrowIfCancellationRequested();
                var count = stream.Read(buffer.Slice(read, wanted - read));
                if (count == 0) throw Failure(XmlCanonicalPhase.CanonicalRead);
                read = checked(read + count);
                consumed = checked(consumed + count);
            }
            if (consumed == Length && !endChecked)
            {
                token.ThrowIfCancellationRequested();
                if (stream.ReadByte() != -1) throw Failure(XmlCanonicalPhase.CanonicalRead);
                endChecked = true;
            }
            token.ThrowIfCancellationRequested();
            return read;
        }
        catch (OutOfMemoryException)
        {
            throw new XmlCanonicalizationException(XmlCanonicalFailure.ResourceLimit, XmlCanonicalPhase.CanonicalRead);
        }
        catch (Exception error) when (error is not (OperationCanceledException or XmlCanonicalizationException))
        {
            throw Failure(XmlCanonicalPhase.CanonicalRead);
        }
    }

    private static XmlReaderSettings Settings(int maximumBytes) => new()
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
    };

    private static void CheckNamespaceDomain(string text, int offset, int maximumBytes, CancellationToken token)
    {
        using var source = new CancellableTextReader(text, offset, token);
        using var reader = XmlReader.Create(source, Settings(maximumBytes));
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!reader.Read()) break;
            if (reader.NodeType != XmlNodeType.Element || !reader.MoveToFirstAttribute()) continue;
            do
            {
                token.ThrowIfCancellationRequested();
                if (reader.NamespaceURI != "http://www.w3.org/2000/xmlns/") continue;
                var value = reader.Value;
                for (var index = 0; index < value.Length; index++)
                {
                    if ((index & (ChunkSize - 1)) == 0) token.ThrowIfCancellationRequested();
                    if (char.IsSurrogate(value[index])) throw UnsupportedDomain();
                }
                // An absolute URI has an ASCII scheme followed by ':'. Inspect lexically, without resolving,
                // normalizing, dereferencing, or using platform-dependent relative-path URI interpretation.
                if (value.Length == 0) continue; // The safe reader permits only a legal default namespace reset.
                if (!AsciiLetter(value[0])) throw UnsupportedDomain();
                var colon = 1;
                while (colon < value.Length && value[colon] != ':')
                {
                    if ((colon & (ChunkSize - 1)) == 0) token.ThrowIfCancellationRequested();
                    var character = value[colon++];
                    if (!AsciiLetter(character) && character is not (>= '0' and <= '9' or '+' or '-' or '.'))
                        throw UnsupportedDomain();
                }
                if (colon == value.Length) throw UnsupportedDomain();
            } while (reader.MoveToNextAttribute());
            reader.MoveToElement();
        }
        token.ThrowIfCancellationRequested();
    }

    private static bool AsciiLetter(char value) => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
    private static XmlCanonicalizationException UnsupportedDomain() =>
        new(XmlCanonicalFailure.UnsupportedDomain, XmlCanonicalPhase.NamespaceDomain);
    private static XmlCanonicalizationException Failure(XmlCanonicalPhase phase) =>
        new(XmlCanonicalFailure.CanonicalizerFailure, phase);

    // Bound refills and check cancellation during DOM parsing as well as between public reader events.
    private sealed class CancellableTextReader : StringReader
    {
        private readonly CancellationToken token;

        public CancellableTextReader(string text, int offset, CancellationToken token) : base(text)
        {
            this.token = token;
            if (offset != 0) base.Read();
        }

        public override int Peek() { token.ThrowIfCancellationRequested(); return base.Peek(); }
        public override int Read() { token.ThrowIfCancellationRequested(); return base.Read(); }
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

    public void Dispose() => stream.Dispose();
    public override string ToString() => nameof(XmlCanonicalDocument);
}
