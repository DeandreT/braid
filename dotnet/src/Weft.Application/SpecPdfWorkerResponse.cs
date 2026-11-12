using System.Text;
using System.Text.Json;
using Weft.Domain;

namespace Weft.Application;

public enum SpecPdfWorkerStatus { Partial = 1, NeedsOcr = 2, Encrypted = 3, Malformed = 4, LimitExceeded = 5 }

public sealed class SpecPdfWorkerPage
{
    internal SpecPdfWorkerPage(int page, string text, bool missingText)
    { Locator = new(page); Text = text; MissingText = missingText; }
    public SpecEvidenceLocator.PdfPage Locator { get; }
    public string Text { get; }
    public bool MissingText { get; }
    public override string ToString() => nameof(SpecPdfWorkerPage);
}

/// <summary>Validated transport result only. Not published intake evidence or proof of sandbox qualification.</summary>
public sealed class SpecPdfWorkerResponse
{
    public const string Format = "weft.spec-pdf-extraction.v1";
    public const string Extractor = "ferrule-pdf-dddc4dce-text.v1";
    public const int MaximumSourceBytes = 8 * 1024 * 1024;
    public const int MaximumResponseBytes = 8 * 1024 * 1024;
    public const int MaximumTextBytes = 1024 * 1024;
    public const int MaximumPages = 256;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    private SpecPdfWorkerResponse(SpecSourceIdentity source, Sha256Digest sourceDigest, int sourceByteCount,
        SpecPdfWorkerStatus status, IReadOnlyList<SpecPdfWorkerPage> pages, Sha256Digest responseDigest)
    {
        Source = source; SourceDigest = sourceDigest; SourceByteCount = sourceByteCount;
        Status = status; Pages = pages; ResponseDigest = responseDigest;
    }
    public SpecSourceIdentity Source { get; }
    public Sha256Digest SourceDigest { get; }
    public int SourceByteCount { get; }
    public SpecPdfWorkerStatus Status { get; }
    public IReadOnlyList<SpecPdfWorkerPage> Pages { get; }
    /// <summary>Exact stdout digest, not an intake canonical-evidence digest.</summary>
    public Sha256Digest ResponseDigest { get; }
    public override string ToString() => nameof(SpecPdfWorkerResponse);

    /// <summary>Call only after a qualified child has exited successfully. Parent must bound stdout while reading,
    /// bind these expected facts to the complete stdin, discard stderr, and enforce process/resource restrictions.
    /// The runner must independently verify a configured approved executable build digest: protocol/extractor
    /// strings alone do not pin fixes that preserve this protocol version.
    /// This method never launches a process, reads a PDF, accesses a file or enables a capability.</summary>
    public static SpecPdfWorkerResponse Decode(ReadOnlyMemory<byte> stdout, int exitCode, SpecSourceIdentity source,
        Sha256Digest expectedSourceDigest, int expectedSourceByteCount)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(expectedSourceDigest);
        if (exitCode != 0 || stdout.Length is 0 or > MaximumResponseBytes || expectedSourceByteCount is < 0 or > MaximumSourceBytes)
            throw new SpecPdfWorkerProtocolException();
        try
        {
            _ = Utf8.GetCharCount(stdout.Span);
            using var json = JsonDocument.Parse(stdout, new JsonDocumentOptions { MaxDepth = 8, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            var root = json.RootElement;
            Closed(root, "format", "extractor", "sourceSha256", "sourceByteCount", "status", "pages");
            if (Text(root, "format") != Format || Text(root, "extractor") != Extractor || Text(root, "sourceSha256") != expectedSourceDigest.Value ||
                !root.GetProperty("sourceByteCount").TryGetInt32(out var count) || count != expectedSourceByteCount)
                throw new SpecPdfWorkerProtocolException();
            var status = Text(root, "status") switch
            {
                "partial" => SpecPdfWorkerStatus.Partial,
                "needsOcr" => SpecPdfWorkerStatus.NeedsOcr,
                "encrypted" => SpecPdfWorkerStatus.Encrypted,
                "malformed" => SpecPdfWorkerStatus.Malformed,
                "limitExceeded" => SpecPdfWorkerStatus.LimitExceeded,
                _ => throw new SpecPdfWorkerProtocolException()
            };
            var array = root.GetProperty("pages");
            if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > MaximumPages) throw new SpecPdfWorkerProtocolException();
            var pages = new List<SpecPdfWorkerPage>();
            var textBytes = 0; var anyText = false;
            foreach (var page in array.EnumerateArray())
            {
                Closed(page, "page", "text", "missingText");
                if (!page.GetProperty("page").TryGetInt32(out var number) || number != pages.Count + 1) throw new SpecPdfWorkerProtocolException();
                var text = Text(page, "text");
                var missing = page.GetProperty("missingText").GetBoolean();
                if (missing != string.IsNullOrWhiteSpace(text)) throw new SpecPdfWorkerProtocolException();
                textBytes += Utf8.GetByteCount(text);
                if (textBytes > MaximumTextBytes) throw new SpecPdfWorkerProtocolException();
                anyText |= !missing;
                pages.Add(new(number, text, missing));
            }
            if (status is SpecPdfWorkerStatus.Partial or SpecPdfWorkerStatus.NeedsOcr)
            {
                if (pages.Count == 0 || (status == SpecPdfWorkerStatus.Partial) != anyText) throw new SpecPdfWorkerProtocolException();
            }
            else if (pages.Count != 0) throw new SpecPdfWorkerProtocolException();
            return new(source, expectedSourceDigest, count, status, pages.AsReadOnly(), Sha256Digest.FromBytes(stdout.Span));
        }
        catch (JsonException) { throw new SpecPdfWorkerProtocolException(); }
        catch (InvalidOperationException) { throw new SpecPdfWorkerProtocolException(); }
        catch (DecoderFallbackException) { throw new SpecPdfWorkerProtocolException(); }
        catch (EncoderFallbackException) { throw new SpecPdfWorkerProtocolException(); }
    }
    private static string Text(JsonElement element, string name) => element.GetProperty(name).GetString() ?? throw new SpecPdfWorkerProtocolException();
    private static void Closed(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new SpecPdfWorkerProtocolException();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!names.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name)) throw new SpecPdfWorkerProtocolException();
        if (seen.Count != names.Length) throw new SpecPdfWorkerProtocolException();
    }
}

/// <summary>Transport failure, deliberately distinct from a document's malformed outcome. Never includes source/stdout/stderr.</summary>
public sealed class SpecPdfWorkerProtocolException : Exception
{
    public SpecPdfWorkerProtocolException() : base("PDF worker response could not be admitted.") { }
}
