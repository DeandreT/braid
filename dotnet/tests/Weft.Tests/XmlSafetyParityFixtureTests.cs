using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Weft.Application;

namespace Weft.Tests;

/// <summary>Shared authored physical-safety inputs, not execution or resource-boundary qualification.</summary>
public sealed class XmlSafetyParityFixtureTests
{
    [Fact]
    public void Authored_physical_safety_corpus_matches_csharp_categories()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(RepositoryRoot(),
            "engines", "ferrule-host", "tests", "corpora", "xml-safety-cases.json")));
        var corpus = document.RootElement;
        RequireObject(corpus, "schema", "description", "limits", "cases");
        Assert.Equal("weft.xml-safety-cases.v1", RequiredString(corpus, "schema"));
        Assert.False(string.IsNullOrWhiteSpace(RequiredString(corpus, "description")));
        var limits = corpus.GetProperty("limits");
        RequireObject(limits, "maximumBytes", "maximumDepth", "maximumNodes", "maximumAttributes");
        var maximumBytes = limits.GetProperty("maximumBytes").GetInt32();
        Assert.Equal(XmlDocumentPolicy.MaximumBytes, maximumBytes);
        Assert.Equal(XmlDocumentPolicy.MaximumDepth, limits.GetProperty("maximumDepth").GetInt32());
        Assert.Equal(XmlDocumentPolicy.MaximumNodes, limits.GetProperty("maximumNodes").GetInt32());
        Assert.Equal(XmlDocumentPolicy.MaximumAttributes, limits.GetProperty("maximumAttributes").GetInt32());
        var cases = corpus.GetProperty("cases");
        Assert.Equal(JsonValueKind.Array, cases.ValueKind);
        Assert.InRange(cases.GetArrayLength(), 15, 65);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in cases.EnumerateArray())
        {
            RequireObject(item, "id", "payload", "expectedCSharp", "expectedNative");
            var id = RequiredString(item, "id");
            Assert.Matches("^xml-[a-z0-9]+(?:-[a-z0-9]+)*$", id);
            Assert.True(ids.Add(id), $"Duplicate fixture ID: {id}");
            var expected = RequiredString(item, "expectedCSharp");
            var native = RequiredString(item, "expectedNative");
            Assert.Contains(expected, new[] { "Accept", "InvalidUtf8", "InvalidXml", "InvalidDeclaration", "DocumentRequired", "UnsupportedName" });
            Assert.Contains(native, new[] { "Accept", "InvalidInput", "InvalidEncoding", "InvalidDeclaration", "DtdProhibited", "UnsupportedName" });
            Assert.True((expected == "Accept") == (native == "Accept"),
                $"{id}: runtimes must expect the same accept/refuse decision.");
            var bytes = Decode(item.GetProperty("payload"));
            var actual = "Accept";
            try
            {
                var statistics = XmlDocumentSafety.Validate(bytes, maximumBytes: maximumBytes);
                Assert.Equal(bytes.Length, statistics.OriginalBytes);
            }
            catch (XmlDocumentException error)
            {
                actual = error.Reason.ToString(); // Public category, never parser message matching.
            }
            Assert.True(expected == actual, $"{id}: expected {expected}, got {actual}.");
        }
    }

    private static byte[] Decode(JsonElement payload)
    {
        Assert.Equal(JsonValueKind.Object, payload.ValueKind);
        var encoding = RequiredString(payload, "encoding");
        Assert.Contains(encoding, new[] { "utf8", "bytes" });
        if (encoding == "utf8")
        {
            RequireObject(payload, "encoding", "text");
            // No BOM insertion, newline normalization, XML parsing or replacement decoding.
            return new UTF8Encoding(false, true).GetBytes(RequiredString(payload, "text"));
        }
        RequireObject(payload, "encoding", "bytes");
        var bytes = payload.GetProperty("bytes");
        Assert.Equal(JsonValueKind.Array, bytes.ValueKind);
        return bytes.EnumerateArray().Select(value => value.GetByte()).ToArray();
    }

    private static string RequiredString(JsonElement item, string name)
    {
        var value = item.GetProperty(name);
        Assert.Equal(JsonValueKind.String, value.ValueKind);
        return value.GetString()!;
    }

    private static void RequireObject(JsonElement item, params string[] expected)
    {
        Assert.Equal(JsonValueKind.Object, item.ValueKind);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in item.EnumerateObject())
            Assert.True(names.Add(property.Name), $"Duplicate fixture property: {property.Name}");
        Assert.True(names.SetEquals(expected), "Fixture object has missing or unknown properties.");
    }

    // Same source-backed root-discovery pattern as BearerSecurityContractTests.
    // Prefer this source worktree over a potentially unrelated build-output directory.
    private static string RepositoryRoot([CallerFilePath] string sourceFile = "")
    {
        foreach (var root in new[] { Path.GetDirectoryName(sourceFile)!, AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "global.json")) && File.Exists(Path.Combine(directory.FullName, "contracts", "weft-v1.json")))
                    return directory.FullName;
        throw new InvalidOperationException("The repository sources are required for XML safety fixture parity.");
    }
}
