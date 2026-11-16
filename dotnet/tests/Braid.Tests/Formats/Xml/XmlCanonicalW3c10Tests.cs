using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Braid.Application;
using Braid.Application.Regression;
using Braid.Contracts.Operations;
using Braid.Domain;

namespace Braid.Tests;

/// <summary>
/// Official Recommendation examples: one admitted complete-document golden and six original-input refusals.
/// Source definitions for the dormant core only, not general C14N conformance or worker/resource qualification.
/// </summary>
public sealed class XmlCanonicalW3c10Tests
{
    private const string SourceUrl = "https://www.w3.org/TR/2001/REC-xml-c14n-20010315";
    private const int MaximumBytes = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    [Fact]
    public void Published_example_inventory_preserves_source_provenance_and_original_octets()
    {
        using var document = ReadFixture();
        var root = document.RootElement;
        Assert.Equal("braid.xml-c14n10-w3c-examples.v1", Text(root, "schema"));
        var source = root.GetProperty("source");
        Assert.Equal(SourceUrl, Text(source, "url"));
        Assert.Equal("W3C Recommendation 15 March 2001", Text(source, "status"));
        Assert.Equal("John Boyer, PureEdge Solutions Inc.", Text(source, "author"));
        Assert.Equal("09c02ef3bc0f8364b00b16fb06092637c08e8f38cf223d47a5862eacc2956bc0",
            Text(source, "sourceSha256"));
        Assert.Equal("iso-8859-1", Text(source, "sourceEncoding"));
        Assert.Equal("https://www.w3.org/Consortium/Legal/copyright-documents-19990405", Text(source, "licenseUrl"));
        var notice = File.ReadAllText(Path.Combine(FixtureDirectory(), Text(source, "noticeFile")));
        Assert.Contains(SourceUrl, notice, StringComparison.Ordinal);
        Assert.Contains(Text(source, "status"), notice, StringComparison.Ordinal);
        Assert.Contains(Text(source, "copyright"), notice, StringComparison.Ordinal);

        var cases = root.GetProperty("cases").EnumerateArray().ToArray();
        Assert.Equal(new[] { "3.1", "3.2", "3.3", "3.4", "3.5", "3.6", "3.7" },
            cases.Select(item => Text(item, "section")));
        var anchors = new[] { "Example-OutsideDoc", "Example-WhitespaceInContent", "Example-SETags",
            "Example-Chars", "Example-Entities", "Example-UTF8", "Example-DocSubsets" };
        for (var index = 0; index < cases.Length; index++)
        {
            var item = cases[index];
            var section = Text(item, "section");
            Assert.Equal(SourceUrl + "#" + anchors[index], Text(item, "url"));
            Assert.False(string.IsNullOrWhiteSpace(Text(item, "title")));
            var input = Bytes(item.GetProperty("input"));
            Assert.NotEmpty(input);
            var text = StrictUtf8.GetString(input);
            Assert.DoesNotContain("\u00a0", text, StringComparison.Ordinal);
            Assert.Equal(section == "3.2" ? "admit" : "refuse", Text(item, "disposition"));
            Assert.Equal(section == "3.2", item.TryGetProperty("canonical", out var canonical));
            var constraints = item.GetProperty("constraints").EnumerateArray().Select(value => value.GetString()).ToArray();
            if (section == "3.2")
            {
                Assert.Empty(constraints);
                Assert.Equal(input, Bytes(canonical));
                Assert.Equal(169, input.Length);
                Assert.Equal((byte)'>', input[^1]);
                Assert.Equal("d844efc8c46782fec445a5726c7bc6130fe5cdb3e4804f680aef702a158afbba", Hash(input));
            }
            else if (section == "3.6")
            {
                Assert.Equal(new[] { "non-utf8-declaration" }, constraints);
                Assert.StartsWith("<?xml version=\"1.0\" encoding=\"ISO-8859-1\"?>\n", text);
                Assert.EndsWith("<doc>&#169;</doc>", text);
                Assert.DoesNotContain("<!DOCTYPE", text, StringComparison.Ordinal);
            }
            else
            {
                Assert.Contains("dtd", constraints);
                Assert.Contains("<!DOCTYPE doc", text, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Section_3_2_complete_original_produces_exact_published_canonical_bytes()
    {
        using var document = ReadFixture();
        var item = Example(document, "3.2");
        var original = Bytes(item.GetProperty("input"));
        var golden = Bytes(item.GetProperty("canonical"));
        var retained = (byte[])original.Clone();
        Assert.Equal(original, golden); // The Recommendation explicitly states this identity.

        using (var canonical = XmlCanonicalDocument.Open(original, MaximumBytes, MaximumBytes, CancellationToken.None))
        using (var output = new MemoryStream())
        {
            var buffer = new byte[17];
            int count;
            while ((count = canonical.Read(buffer, CancellationToken.None)) != 0)
                output.Write(buffer, 0, count);
            Assert.Equal((long)golden.Length, canonical.Length);
            Assert.Equal(canonical.Length, output.Length);
            Assert.Equal(golden, output.ToArray());
            Assert.Equal(0, canonical.Read(buffer, CancellationToken.None));
        }

        var expected = Output(original);
        var actual = Output(golden);
        var result = Compare(expected, actual);
        Assert.Equal(XmlCanonicalComparison.Policy, result.Policy);
        Assert.Equal(RegressionComparisonOutcome.Passed, result.Outcome);
        Assert.Equal(XmlCanonicalFailure.None, result.NotEvaluatedReason);
        Assert.True(result.ComparisonComplete);
        Assert.Equal(0, result.TotalDifferenceCount);
        Assert.Empty(result.Differences);
        Assert.Null(result.Diagnostic);
        Assert.Equal((long)original.Length, result.ExpectedOriginalBytes);
        Assert.Equal((long)golden.Length, result.ActualOriginalBytes);
        Assert.Equal(new XmlCanonicalTotals(1, golden.Length), result.ExpectedCanonical);
        Assert.Equal(new XmlCanonicalTotals(1, golden.Length), result.ActualCanonical);
        Assert.Equal(retained, original);
        AssertPreserved(expected, retained);
        AssertPreserved(actual, golden);
    }

    [Theory]
    [InlineData("3.1")]
    [InlineData("3.3")]
    [InlineData("3.4")]
    [InlineData("3.5")]
    [InlineData("3.6")]
    [InlineData("3.7")]
    public void Original_out_of_domain_examples_are_refused_even_when_raw_bytes_are_identical(string section)
    {
        using var document = ReadFixture();
        var item = Example(document, section);
        Assert.Equal("refuse", Text(item, "disposition"));
        Assert.False(item.TryGetProperty("canonical", out _));
        var original = Bytes(item.GetProperty("input"));
        var retained = (byte[])original.Clone();
        var safe = Bytes(Example(document, "3.2").GetProperty("input"));

        var error = Assert.Throws<XmlCanonicalizationException>(() =>
        {
            using var canonical = XmlCanonicalDocument.Open(original, MaximumBytes, MaximumBytes, CancellationToken.None);
        });
        Assert.Equal(XmlCanonicalFailure.InvalidDocument, error.Reason);
        Assert.Equal(XmlCanonicalPhase.Safety, error.Phase);
        Assert.Equal("XML canonical comparison refused.", error.Message);
        Assert.Null(error.InnerException);

        // Identical unsafe pair, unsafe baseline, and unsafe actual: no normalization or DTD stripping first.
        var pairs = new[] { (Left: original, Right: original, Side: XmlComparisonSide.Expected),
            (Left: original, Right: safe, Side: XmlComparisonSide.Expected),
            (Left: safe, Right: original, Side: XmlComparisonSide.Actual) };
        foreach (var pair in pairs)
        {
            var expected = Output(pair.Left);
            var actual = Output(pair.Right);
            var result = Compare(expected, actual);
            Assert.Equal(RegressionComparisonOutcome.NotEvaluated, result.Outcome);
            Assert.Equal(XmlCanonicalFailure.InvalidDocument, result.NotEvaluatedReason);
            Assert.False(result.ComparisonComplete);
            Assert.Equal(0, result.TotalDifferenceCount);
            Assert.Empty(result.Differences);
            Assert.Null(result.ExpectedCanonical);
            Assert.Null(result.ActualCanonical);
            Assert.NotNull(result.Diagnostic);
            Assert.Equal(XmlCanonicalPhase.Safety, result.Diagnostic.Phase);
            Assert.Equal(XmlCanonicalFailure.InvalidDocument, result.Diagnostic.Reason);
            Assert.Equal(pair.Side, result.Diagnostic.Side);
            Assert.Equal(RegressionOutputKey.Primary, result.Diagnostic.Key);
            Assert.Equal((long)pair.Left.Length, result.ExpectedOriginalBytes);
            Assert.Equal((long)pair.Right.Length, result.ActualOriginalBytes);
            AssertPreserved(expected, pair.Left);
            AssertPreserved(actual, pair.Right);
        }
        Assert.Equal(retained, original);
    }

    [Fact]
    public void Section_3_7_subset_expression_is_provenance_not_a_complete_document_golden()
    {
        using var document = ReadFixture();
        var item = Example(document, "3.7");
        Assert.Equal(new[] { "dtd", "document-subset" },
            item.GetProperty("constraints").EnumerateArray().Select(value => value.GetString()));
        var expression = StrictUtf8.GetString(Bytes(item.GetProperty("subsetExpression")));
        Assert.Contains("(//. | //@* | //namespace::*)", expression, StringComparison.Ordinal);
        Assert.Contains("count(id(\"E3\")|ancestor-or-self::node())", expression, StringComparison.Ordinal);
        Assert.False(item.TryGetProperty("canonical", out _));
        // Safety rejects its original DTD. This is not a claim that a subset API has been implemented or tested.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Self_authored_whitespace_mutations_are_not_equivalent_to_the_official_document(bool useNonbreakingSpace)
    {
        using var document = ReadFixture();
        var original = Bytes(Example(document, "3.2").GetProperty("input"));
        var text = StrictUtf8.GetString(original);
        var offset = text.IndexOf("<clean>", StringComparison.Ordinal) + "<clean>".Length;
        Assert.True(offset >= "<clean>".Length);
        Assert.Equal(' ', text[offset]);
        // Local negative controls, never stored or described as official W3C input/golden pairs.
        var changed = StrictUtf8.GetBytes(text[..offset] + (useNonbreakingSpace ? "\u00a0" : "") + text[(offset + 1)..]);
        var result = Compare(Output(original), Output(changed));
        Assert.Equal(RegressionComparisonOutcome.Failed, result.Outcome);
        Assert.True(result.ComparisonComplete);
        var difference = Assert.Single(result.Differences);
        Assert.Equal(XmlCanonicalDifferenceKind.CanonicalBytesDiffer, difference.Kind);
        // Removing the first of three identical spaces is first observable at the third space.
        var firstDifference = useNonbreakingSpace ? offset : offset + 2;
        Assert.Equal((long)StrictUtf8.GetByteCount(text[..firstDifference]), difference.CanonicalByteOffset);
        Assert.Null(difference.FirstDifferentByte);
    }

    [Fact]
    public void Official_examples_do_not_make_the_dormant_policy_public_or_worker_qualified()
    {
        Assert.Equal("braid.regression.xml-c14n10-comments.v1", XmlCanonicalComparison.Policy);
        Assert.Equal(XmlCanonicalDocument.AlgorithmUri,
            SourceUrl.Replace("https:", "http:", StringComparison.Ordinal) + "#WithComments");
        var json = RegressionOutputSet.Capture([RegressionOutput.Capture(RegressionOutputKey.Primary, "{}"u8)]);
        var result = new RegressionComparisonService().Compare(json, new(OperationStatusV1.Succeeded, json),
            XmlCanonicalComparison.Policy);
        Assert.Equal(RegressionComparisonOutcome.NotEvaluated, result.Outcome);
        Assert.Equal(RegressionNotEvaluatedReason.UnsupportedPolicy, result.NotEvaluatedReason);
        Assert.DoesNotContain(typeof(RegressionComparisonService).Assembly.GetExportedTypes(),
            type => type.Name.StartsWith("XmlCanonical", StringComparison.Ordinal) ||
                type.Name.StartsWith("XmlComparison", StringComparison.Ordinal));
        // No in-process functional vector establishes termination inside synchronous LoadInput/GetOutput.
    }

    private static XmlComparisonOutput Output(byte[] bytes) =>
        XmlComparisonOutput.Capture(RegressionOutputKey.Primary, bytes, XmlDocumentMetadata.WithoutHints);

    private static XmlCanonicalComparisonResult Compare(XmlComparisonOutput expected, XmlComparisonOutput actual) =>
        XmlCanonicalComparison.Compare(XmlComparisonOutputSet.Capture([expected]), XmlComparisonOutputSet.Capture([actual]),
            OperationStatusV1.Succeeded, new(2 * MaximumBytes, 1), new(MaximumBytes, MaximumBytes, 2 * MaximumBytes));

    private static void AssertPreserved(XmlComparisonOutput output, byte[] original)
    {
        Assert.Equal(original.Length, output.ByteCount);
        Assert.Equal(original, output.CopyBytes());
        Assert.Equal(Sha256Digest.FromBytes(original), output.Digest);
    }

    private static JsonDocument ReadFixture() =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(FixtureDirectory(), "recommendation-examples.json")));

    private static JsonElement Example(JsonDocument document, string section) =>
        Assert.Single(document.RootElement.GetProperty("cases").EnumerateArray(), item => Text(item, "section") == section);

    private static byte[] Bytes(JsonElement fragment)
    {
        var code = Text(fragment, "sourceCodeHtml");
        Assert.Equal(Text(fragment, "sourceCodeSha256"), Hash(StrictUtf8.GetBytes(code)));
        var bytes = Convert.FromBase64String(Text(fragment, "base64"));
        Assert.Equal(Text(fragment, "sha256"), Hash(bytes));
        // The Recommendation uses HTML layout newlines plus explicit <br/> line separators.
        var encoded = code.Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal)
            .Replace("<br/>", "\n", StringComparison.Ordinal).Replace("&nbsp;", " ", StringComparison.Ordinal);
        Assert.DoesNotContain("<", encoded, StringComparison.Ordinal);
        var decoded = WebUtility.HtmlDecode(encoded); // Exactly once: leave the XML's own references intact.
        Assert.All(decoded, character => Assert.InRange((int)character, 0, 127));
        Assert.Equal(bytes, StrictUtf8.GetBytes(decoded));
        if (fragment.TryGetProperty("sourceLiteralComment", out var comment))
        {
            var literal = comment.GetString()!;
            Assert.StartsWith("\n", literal);
            Assert.EndsWith("\n", literal);
            Assert.Equal(bytes, StrictUtf8.GetBytes(literal[1..^1]));
        }
        return bytes;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string Text(JsonElement element, string name) => element.GetProperty(name).GetString()!;
    private static string FixtureDirectory() => Path.Combine(RepositoryRoot(), "dotnet", "tests", "Braid.Tests",
        "Fixtures", "XmlCanonicalW3c10");

    // Match XmlSafetyParityFixtureTests: prefer this source worktree over an unrelated build-output directory.
    private static string RepositoryRoot([CallerFilePath] string sourceFile = "")
    {
        foreach (var root in new[] { Path.GetDirectoryName(sourceFile)!, AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "global.json")) &&
                    File.Exists(Path.Combine(directory.FullName, "contracts", "braid-v1.json")))
                    return directory.FullName;
        throw new InvalidOperationException("The repository sources are required for W3C XML canonical fixtures.");
    }
}
