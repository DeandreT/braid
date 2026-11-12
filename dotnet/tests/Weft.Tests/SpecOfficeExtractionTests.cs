using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Weft.Application;
using Weft.Domain;

namespace Weft.Tests;

public sealed class SpecOfficeExtractionTests
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string Rel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";
    private const string Types = "http://schemas.openxmlformats.org/package/2006/content-types";
    private static readonly SpecSourceIdentity SourceId = new(new(new ProjectScope(Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid()), Guid.NewGuid(), 1);
    private static SpecCapturedSource Source(byte[] bytes, SpecSourceFormat format = SpecSourceFormat.Docx) => new(SourceId, format, bytes);
    internal static Dictionary<string, string> Word(string body = "<w:p><w:r><w:t>café 東京</w:t></w:r></w:p>") => new()
    {
        ["[Content_Types].xml"] = $"<Types xmlns='{Types}'><Default Extension='rels' ContentType='application/vnd.openxmlformats-package.relationships+xml'/><Override PartName='/word/document.xml' ContentType='application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml'/></Types>",
        ["_rels/.rels"] = $"<Relationships xmlns='{Rel}'><Relationship Id='main' Type='{R}officeDocument' Target='word/document.xml'/></Relationships>",
        ["word/document.xml"] = $"<w:document xmlns:w='{W}'><w:body>{body}</w:body></w:document>"
    };
    internal static Dictionary<string, string> Excel(string cells = "<c r='A1' t='s'><v>0</v></c>", string shared = "<si><t>東京</t></si>") => new()
    {
        ["[Content_Types].xml"] = $"<Types xmlns='{Types}'><Default Extension='rels' ContentType='application/vnd.openxmlformats-package.relationships+xml'/><Default Extension='xml' ContentType='application/xml'/><Override PartName='/xl/workbook.xml' ContentType='application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml'/></Types>",
        ["_rels/.rels"] = $"<Relationships xmlns='{Rel}'><Relationship Id='main' Type='{R}officeDocument' Target='xl/workbook.xml'/></Relationships>",
        ["xl/workbook.xml"] = $"<workbook xmlns='{S}' xmlns:r='{R.TrimEnd('/')}'><sheets><sheet name='Données' sheetId='1' r:id='sheet'/></sheets></workbook>",
        ["xl/_rels/workbook.xml.rels"] = $"<Relationships xmlns='{Rel}'><Relationship Id='sheet' Type='{R}worksheet' Target='worksheets/sheet1.xml'/><Relationship Id='strings' Type='{R}sharedStrings' Target='sharedStrings.xml'/></Relationships>",
        ["xl/worksheets/sheet1.xml"] = $"<worksheet xmlns='{S}'><sheetData><row r='1'>{cells}</row></sheetData></worksheet>",
        ["xl/sharedStrings.xml"] = $"<sst xmlns='{S}'>{shared}</sst>"
    };
    internal static byte[] Zip(IEnumerable<KeyValuePair<string, string>> parts, CompressionLevel compression = CompressionLevel.NoCompression)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            foreach (var (name, text) in parts)
            {
                using var entry = zip.CreateEntry(name, compression).Open();
                entry.Write(Encoding.UTF8.GetBytes(text));
            }
        return stream.ToArray();
    }
    private static SpecOfficeExtractionResult Extract(Dictionary<string, string> parts, SpecSourceFormat format = SpecSourceFormat.Docx,
        SpecOfficeExtractionLimits? limits = null) => SpecOfficeParser.Extract(Source(Zip(parts), format), limits);
    private static void Refused(SpecOfficeExtractionResult result, SpecExtractionOutcome outcome, string code)
    {
        Assert.Equal(outcome, result.Outcome); Assert.Equal(code, result.Code); Assert.Empty(result.Chunks);
        Assert.Equal(result.Identity.EvidenceDigest, Sha256Digest.FromBytes(result.CopyEvidenceBytes()));
        using var json = JsonDocument.Parse(result.CopyEvidenceBytes());
        Assert.Equal(2, json.RootElement.GetArrayLength());
        Assert.Throws<ArgumentOutOfRangeException>(() => result.Cite(0));
    }

    [Fact]
    public void Word_cites_real_part_paragraph_and_table_cell_without_pages_or_byte_offsets()
    {
        var parts = Word("<w:p><w:r><w:t>café </w:t><w:t>東京</w:t><w:tab/><w:br/><w:t>end</w:t></w:r></w:p>" +
            "<w:p/><w:tbl><w:tr><w:tc><w:p><w:r><w:t>cell one</w:t></w:r></w:p><w:p/></w:tc><w:tc><w:p><w:r><w:t>二</w:t></w:r></w:p></w:tc></w:tr></w:tbl>");
        var source = Source(Zip(parts));
        var result = SpecOfficeParser.Extract(source);
        Assert.Equal(SpecExtractionOutcome.Partial, result.Outcome);
        Assert.Equal("office-body-text-only", result.Code);
        Assert.Equal(new[] { "café 東京\t\nend", "", "cell one", "", "二" }, result.Chunks.Select(c => c.Text));
        Assert.Equal(new SpecEvidenceLocator.WordParagraph("word/document.xml", 1), result.Chunks[0].Locator);
        Assert.Equal(new SpecEvidenceLocator.WordParagraph("word/document.xml", 2), result.Chunks[1].Locator);
        Assert.Equal(new SpecEvidenceLocator.WordParagraph("word/document.xml", 1, 1, 1, 1), result.Chunks[2].Locator);
        Assert.Equal(new SpecEvidenceLocator.WordParagraph("word/document.xml", 2, 1, 1, 1), result.Chunks[3].Locator);
        Assert.Equal(new SpecEvidenceLocator.WordParagraph("word/document.xml", 1, 1, 1, 2), result.Chunks[4].Locator);
        Assert.Equal(source.Digest, result.Identity.SourceDigest);
        Assert.Equal(3, result.Identity.ExtractorVersion);
        Assert.Equal(result.Identity, result.Cite(4).Extraction);
        Assert.Equal(result.Chunks[4].Locator, result.Cite(4).Locator);
        Assert.Equal(result.Identity.EvidenceDigest, Sha256Digest.FromBytes(result.CopyEvidenceBytes()));
        Assert.Equal(result.CopyEvidenceBytes(), SpecOfficeParser.Extract(source).CopyEvidenceBytes());
        var copy = result.CopyEvidenceBytes(); copy[0] = 0;
        Assert.Equal((byte)'[', result.CopyEvidenceBytes()[0]);
        using var json = JsonDocument.Parse(result.CopyEvidenceBytes());
        Assert.Equal("weft.spec-evidence.v2", json.RootElement[0].GetString());
        Assert.Equal("docx-paragraph", json.RootElement[2][2][0].GetString());
        Assert.Null(json.RootElement[2][2][3].GetString());
        Assert.Equal(1, json.RootElement[4][2][3].GetInt32());
    }

    [Fact]
    public void Workbook_cites_sheet_cells_shared_and_inline_strings_and_literal_formula_cache_states()
    {
        var result = Extract(Excel("<c r='A1' t='s'><v>0</v></c><c r='B1' t='inlineStr'><is><r><t>é</t></r><r><t>二</t></r></is></c>" +
            "<c r='C1'><f>SUM(1,2)</f><v>3</v></c><c r='D1'><f>HYPERLINK(&quot;https://not-fetched.invalid&quot;)</f></c>" +
            "<c r='E1'/><c r='F1' t='b'><v>1</v></c><c r='G1' t='e'><v>#DIV/0!</v></c><c r='H1' t='s'><v>1</v></c>",
            "<si><r><t>東</t></r><r><t>京</t></r></si><si><t></t></si>"), SpecSourceFormat.Xlsx);
        Assert.Equal(SpecExtractionOutcome.Partial, result.Outcome);
        Assert.Equal("office-stored-cells-only", result.Code);
        Assert.Equal(new[] { "東京", "é二", "3", "", "", "1", "#DIV/0!", "" }, result.Chunks.Select(c => c.Text));
        var locators = result.Chunks.Select(c => Assert.IsType<SpecEvidenceLocator.SheetCell>(c.Locator)).ToArray();
        Assert.All(locators, l => { Assert.Equal("xl/worksheets/sheet1.xml", l.Part); Assert.Equal("Données", l.Sheet); });
        Assert.Equal(new[] { "A1", "B1", "C1", "D1", "E1", "F1", "G1", "H1" }, locators.Select(l => l.Cell));
        Assert.Equal(SpecCellValueKind.SharedString, locators[0].ValueKind);
        Assert.Equal(SpecCellValueKind.InlineString, locators[1].ValueKind);
        Assert.True(locators[2].HasCachedValue); Assert.False(locators[3].HasCachedValue);
        Assert.Equal("SUM(1,2)", result.Chunks[2].Formula);
        Assert.Equal("HYPERLINK(\"https://not-fetched.invalid\")", result.Chunks[3].Formula);
        Assert.Null(result.Chunks[4].Formula); Assert.Equal(SpecCellValueKind.Blank, locators[4].ValueKind);
        using var json = JsonDocument.Parse(result.CopyEvidenceBytes());
        Assert.Equal("xlsx-cell", json.RootElement[2][2][0].GetString());
        Assert.True(json.RootElement[4][2][5].GetBoolean());
        Assert.Equal("SUM(1,2)", json.RootElement[4][3].GetString());
    }

    [Fact]
    public void Empty_documents_and_empty_sheets_do_not_invent_content_or_claim_complete_coverage()
    {
        var word = Extract(Word(""));
        Assert.Empty(word.Chunks); Assert.Equal(SpecExtractionOutcome.Partial, word.Outcome);
        var excel = Extract(Excel("", ""), SpecSourceFormat.Xlsx);
        Assert.Empty(excel.Chunks); Assert.Equal(SpecExtractionOutcome.Partial, excel.Outcome);
    }

    [Fact]
    public void Empty_numeric_formula_cache_is_missing_but_empty_string_cache_is_present()
    {
        var result = Extract(Excel("<c r='A1'><f>1+2</f><v/></c><c r='B1' t='str'><f>&quot;&quot;</f><v/></c><c r='C1'><v/></c>"), SpecSourceFormat.Xlsx);
        Assert.Equal(SpecExtractionOutcome.Partial, result.Outcome);
        Assert.All(result.Chunks, c => Assert.Equal("", c.Text));
        Assert.False(Assert.IsType<SpecEvidenceLocator.SheetCell>(result.Chunks[0].Locator).HasCachedValue);
        Assert.True(Assert.IsType<SpecEvidenceLocator.SheetCell>(result.Chunks[1].Locator).HasCachedValue);
        Assert.Null(result.Chunks[2].Formula);
        Assert.Equal(SpecCellValueKind.Blank, Assert.IsType<SpecEvidenceLocator.SheetCell>(result.Chunks[2].Locator).ValueKind);
    }

    [Theory]
    [InlineData("<w:p><w:r><w:drawing/></w:r></w:p>", "unsupported-word-run")]
    [InlineData("<w:p><w:r><w:instrText>INCLUDETEXT</w:instrText></w:r></w:p>", "unsupported-word-run")]
    [InlineData("<w:p><w:ins><w:r><w:t>revision</w:t></w:r></w:ins></w:p>", "unsupported-word-paragraph")]
    [InlineData("<w:altChunk/>", "unsupported-word-content")]
    [InlineData("<w:tbl><w:tr><w:tc><w:tbl/></w:tc></w:tr></w:tbl>", "unsupported-word-cell")]
    public void Unhandled_word_features_refuse_atomically(string body, string code) =>
        Refused(Extract(Word("<w:p><w:r><w:t>earlier</w:t></w:r></w:p>" + body)), SpecExtractionOutcome.Unsupported, code);

    [Theory]
    [InlineData("<c r='A1' t='s'><v>999</v></c>", "invalid-shared-string-index")]
    [InlineData("<c r='A1'/><c r='A1'/>", "invalid-cell-address")]
    [InlineData("<c r='A2'/>", "cell-row-mismatch")]
    [InlineData("<c r='XFE1'/>", "invalid-cell-address")]
    [InlineData("<c r='MWLQKWW1'/>", "invalid-cell-address")]
    [InlineData("<c r='A0'/>", "invalid-cell-address")]
    [InlineData("<c r='A1'><v>NaN</v></c>", "invalid-numeric-cell")]
    [InlineData("<c r='A1' t='b'><v>yes</v></c>", "invalid-boolean-cell")]
    [InlineData("<c r='A1'><v>1</v><v>2</v></c>", "invalid-cell-value")]
    public void Malformed_cell_references_and_values_are_not_guessed(string cells, string code) =>
        Refused(Extract(Excel(cells), SpecSourceFormat.Xlsx), SpecExtractionOutcome.Malformed, code);

    [Theory]
    [InlineData("shared")]
    [InlineData("array")]
    [InlineData("dataTable")]
    public void Formula_features_requiring_expansion_or_evaluation_are_unsupported(string kind) =>
        Refused(Extract(Excel($"<c r='A1'><f t='{kind}' si='0'>A2+1</f><v>3</v></c>"), SpecSourceFormat.Xlsx), SpecExtractionOutcome.Unsupported, "unsupported-formula-kind");

    [Theory]
    [InlineData("https://example.invalid/doc", "External", "external-relationship", SpecExtractionOutcome.Unsupported)]
    [InlineData("file:///private", "External", "external-relationship", SpecExtractionOutcome.Unsupported)]
    [InlineData("../secret.xml", "Internal", "unsafe-package-path", SpecExtractionOutcome.Malformed)]
    [InlineData("%2e%2e/secret.xml", "Internal", "unsafe-package-path", SpecExtractionOutcome.Malformed)]
    [InlineData("/word/document.xml", "Internal", "unsafe-package-path", SpecExtractionOutcome.Malformed)]
    [InlineData("missing.xml", "Internal", "missing-package-part", SpecExtractionOutcome.Malformed)]
    public void Relationships_never_resolve_network_files_or_ambiguous_paths(string target, string mode, string code, SpecExtractionOutcome outcome)
    {
        var parts = Word();
        parts["_rels/.rels"] = $"<Relationships xmlns='{Rel}'><Relationship Id='main' Type='{R}officeDocument' Target='{target}' TargetMode='{mode}'/></Relationships>";
        Refused(Extract(parts), outcome, code);
    }

    [Fact]
    public void Duplicate_relationship_ids_and_missing_sheet_targets_are_malformed()
    {
        var parts = Excel();
        parts["xl/_rels/workbook.xml.rels"] = $"<Relationships xmlns='{Rel}'><Relationship Id='sheet' Type='{R}worksheet' Target='worksheets/sheet1.xml'/><Relationship Id='sheet' Type='{R}worksheet' Target='worksheets/sheet1.xml'/></Relationships>";
        Refused(Extract(parts, SpecSourceFormat.Xlsx), SpecExtractionOutcome.Malformed, "duplicate-relationship");
        parts = Excel(); parts["xl/workbook.xml"] = parts["xl/workbook.xml"].Replace("r:id='sheet'", "r:id='absent'", StringComparison.Ordinal);
        Refused(Extract(parts, SpecSourceFormat.Xlsx), SpecExtractionOutcome.Malformed, "invalid-sheet-relationship");
    }

    [Theory]
    [InlineData("../outside.xml")]
    [InlineData("word/../outside.xml")]
    [InlineData("/absolute.xml")]
    [InlineData("word\\document.xml")]
    [InlineData("word/%64ocument.xml")]
    public void Unsafe_zip_paths_are_rejected_without_filesystem_extraction(string path)
    {
        var parts = Word(); parts[path] = "<x/>";
        Refused(Extract(parts), SpecExtractionOutcome.Malformed, "unsafe-package-path");
    }

    [Fact]
    public void Duplicate_and_case_alias_zip_entries_are_rejected()
    {
        foreach (var name in new[] { "word/document.xml", "WORD/document.xml" })
        {
            var entries = Word().ToList(); entries.Add(new(name, "<x/>"));
            Refused(SpecOfficeParser.Extract(Source(Zip(entries))), SpecExtractionOutcome.Malformed, "duplicate-package-part");
        }
    }

    [Fact]
    public void Macro_legacy_and_encrypted_containers_are_explicitly_unsupported()
    {
        Refused(SpecOfficeParser.Extract(Source([0xd0, 0xcf, 0x11, 0xe0, 1, 2, 3, 4])), SpecExtractionOutcome.Unsupported, "legacy-or-encrypted-office");
        var parts = Word(); parts["word/vbaProject.bin"] = "macro";
        Refused(Extract(parts), SpecExtractionOutcome.Unsupported, "active-office-content");
        parts = Word(); parts["[Content_Types].xml"] = parts["[Content_Types].xml"].Replace("wordprocessingml.document.main", "ms-word.document.macroEnabled.main", StringComparison.Ordinal);
        Refused(Extract(parts), SpecExtractionOutcome.Unsupported, "active-office-content");
        var bytes = Zip(Word()); var central = Central(bytes);
        bytes[central + 8] |= 1;
        Refused(SpecOfficeParser.Extract(Source(bytes)), SpecExtractionOutcome.Unsupported, "encrypted-zip");
    }

    [Theory]
    [InlineData("<!DOCTYPE document [<!ENTITY x SYSTEM 'file:///private'>]>")]
    [InlineData("<!DOCTYPE document [<!ENTITY x SYSTEM 'https://not-fetched.invalid'>]>")]
    [InlineData("<!DOCTYPE document [<!ENTITY x 'expansion'>]>")]
    public void Dtd_and_entities_are_disabled(string dtd)
    {
        var parts = Word(); parts["word/document.xml"] = dtd + parts["word/document.xml"];
        Refused(Extract(parts), SpecExtractionOutcome.Malformed, "invalid-office-xml");
    }

    [Fact]
    public void Truncated_zip_bad_crc_and_malformed_xml_are_atomic()
    {
        var bytes = Zip(Word());
        Refused(SpecOfficeParser.Extract(Source(bytes[..^12])), SpecExtractionOutcome.Malformed, "invalid-zip-directory");
        bytes[Central(bytes) + 16] ^= 1;
        bytes[14] ^= 1;
        Refused(SpecOfficeParser.Extract(Source(bytes)), SpecExtractionOutcome.Malformed, "zip-content-mismatch");
        var parts = Word(); parts["word/document.xml"] = "<broken>";
        Refused(Extract(parts), SpecExtractionOutcome.Malformed, "invalid-office-xml");
    }

    [Fact]
    public void Every_budget_is_enforced_and_part_of_the_profile()
    {
        var parts = Word(); var source = Source(Zip(parts));
        Refused(SpecOfficeParser.Extract(source, new(maximumSourceBytes: 10)), SpecExtractionOutcome.LimitExceeded, "source-bytes");
        Refused(SpecOfficeParser.Extract(source, new(maximumEntries: 2)), SpecExtractionOutcome.LimitExceeded, "zip-entries");
        Refused(SpecOfficeParser.Extract(source, new(maximumEntryBytes: 10)), SpecExtractionOutcome.LimitExceeded, "zip-entry-bytes");
        Refused(SpecOfficeParser.Extract(source, new(maximumExpandedBytes: 10)), SpecExtractionOutcome.LimitExceeded, "zip-expanded-bytes");
        Refused(SpecOfficeParser.Extract(source, new(maximumXmlNodes: 2)), SpecExtractionOutcome.LimitExceeded, "xml-nodes");
        Refused(SpecOfficeParser.Extract(source, new(maximumXmlDepth: 1)), SpecExtractionOutcome.LimitExceeded, "xml-depth");
        Refused(Extract(Word("<w:p/><w:p/>"), limits: new(maximumChunks: 1)), SpecExtractionOutcome.LimitExceeded, "office-chunks");
        Refused(Extract(Word($"<w:p><w:r><w:t>{new string('é', 150)}</w:t></w:r></w:p>"), limits: new(maximumEvidenceBytes: 256)), SpecExtractionOutcome.LimitExceeded, "evidence-bytes");
        Assert.NotEqual(new SpecOfficeExtractionLimits().Digest, new SpecOfficeExtractionLimits(maximumEntries: 2).Digest);
        Assert.NotEqual(new SpecOfficeExtractionLimits().Digest, new SpecOfficeExtractionLimits(maximumCompressionRatio: 99).Digest);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpecOfficeExtractionLimits(maximumExpandedBytes: int.MaxValue));
    }

    [Fact]
    public void Compressed_bombs_and_forged_lengths_are_refused_and_aggregate_budget_is_enforced_during_expansion()
    {
        var parts = Word($"<w:p><w:r><w:t>{new string('x', 20_000)}</w:t></w:r></w:p>");
        var bytes = Zip(parts, CompressionLevel.SmallestSize);
        Refused(SpecOfficeParser.Extract(Source(bytes)), SpecExtractionOutcome.LimitExceeded, "zip-ratio");
        // Falsify the declared length of the last entry, retaining its real compressed stream.
        var directory = Central(bytes);
        while (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(directory, 4)) == 0x02014b50)
        {
            var declared = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(directory + 24, 4));
            if (declared > 10_000)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(directory + 24, 4), 1);
                var local = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(directory + 42, 4));
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(local + 22, 4), 1);
            }
            directory += 46 + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(directory + 28, 2)) +
                BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(directory + 30, 2)) + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(directory + 32, 2));
        }
        Refused(SpecOfficeParser.Extract(Source(bytes)), SpecExtractionOutcome.Malformed, "zip-content-mismatch");
        // Each stored entry individually fits; the last read crosses the aggregate budget.
        var total = parts.Values.Sum(v => Encoding.UTF8.GetByteCount(v));
        Refused(Extract(parts, limits: new(maximumExpandedBytes: total - 1)), SpecExtractionOutcome.LimitExceeded, "zip-expanded-bytes");
    }

    [Fact]
    public void Canonical_serialization_limit_accounts_for_escaping_and_discards_all_chunks()
    {
        // 160 raw UTF-8 bytes become 480 escaped JSON bytes before framing and structural coordinates.
        var result = Extract(Word($"<w:p><w:r><w:t>{new string('é', 80)}</w:t></w:r></w:p>"), limits: new(maximumEvidenceBytes: 256));
        Refused(result, SpecExtractionOutcome.LimitExceeded, "serialized-evidence-bytes");
        Assert.True(result.CopyEvidenceBytes().Length <= 256);
    }

    [Fact]
    public async Task Ordinary_extractor_and_default_worker_remain_fail_closed_and_cancellation_propagates()
    {
        var source = Source(Zip(Word()));
        var ordinary = SpecSourceExtractor.Extract(source);
        Assert.Equal(SpecExtractionOutcome.Unsupported, ordinary.Outcome); Assert.Equal("office-worker-required", ordinary.Code);
        Assert.Empty(ordinary.Chunks);
        Refused(await new UnavailableSpecOfficeExtractionWorker().ExtractAsync(source, new(), default), SpecExtractionOutcome.Unsupported, "office-worker-unavailable");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => SpecOfficeParser.Extract(source, token: cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new UnavailableSpecOfficeExtractionWorker().ExtractAsync(source, new(), cancelled.Token));
        var text = SpecSourceExtractor.Extract(Source("é"u8.ToArray(), SpecSourceFormat.Text));
        Assert.IsType<SpecEvidenceLocator.ByteRange>(text.Chunks[0].Locator);
        Assert.Equal(2, text.Chunks[0].Locator.Length);
        Assert.Contains("weft.spec-evidence.v1", Encoding.UTF8.GetString(text.CopyEvidenceBytes()), StringComparison.Ordinal);
    }

    private static int Central(byte[] bytes) => (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 6, 4));

    [Theory]
    [InlineData("MWLQKWW1", false)]
    [InlineData("XFE1", false)]
    [InlineData("XFD1048576", true)]
    public void Cell_column_validation_cannot_wrap_overflow(string address, bool valid) =>
        Assert.Equal(valid, SpecEvidenceLocator.SheetCell.IsCell(address));

    [Theory]
    [InlineData("d", "2024-02-29")]
    [InlineData("d", "2024-02-29T12:34:56")]
    [InlineData("d", "2024-02-29T12:34:56.1234567Z")]
    [InlineData("d", "2024-02-29T12:34:56+14:00")]
    [InlineData("d", "2024-02-29T12:34:56-05:30")]
    [InlineData("e", "#NULL!")]
    [InlineData("e", "#DIV/0!")]
    [InlineData("e", "#VALUE!")]
    [InlineData("e", "#REF!")]
    [InlineData("e", "#NAME?")]
    [InlineData("e", "#NUM!")]
    [InlineData("e", "#N/A")]
    [InlineData("e", "#GETTING_DATA")]
    public void Admitted_date_and_error_values_and_formula_caches_preserve_exact_lexical_text(string type, string value)
    {
        foreach (var formula in new[] { "", "<f>unexecuted()</f>" })
        {
            var result = Extract(Excel($"<c r='A1' t='{type}'>{formula}<v>{value}</v></c>"), SpecSourceFormat.Xlsx);
            Assert.Equal(SpecExtractionOutcome.Partial, result.Outcome);
            var chunk = Assert.Single(result.Chunks);
            Assert.Equal(value, chunk.Text);
            Assert.Equal(formula.Length == 0 ? null : "unexecuted()", chunk.Formula);
            var locator = Assert.IsType<SpecEvidenceLocator.SheetCell>(chunk.Locator);
            Assert.Equal(type == "d" ? SpecCellValueKind.IsoDate : SpecCellValueKind.Error, locator.ValueKind);
            Assert.Equal(formula.Length != 0, locator.HasCachedValue);
        }
    }

    [Theory]
    [InlineData("d", "not-a-date")]
    [InlineData("d", "2023-02-29")]
    [InlineData("d", "0000-01-01")]
    [InlineData("d", "2024-13-01")]
    [InlineData("d", "2024-01-01T25:00:00")]
    [InlineData("d", "2024-01-01T12:00:00+14:01")]
    [InlineData("d", "2024-01-01T12:00:00+15:00")]
    [InlineData("d", "2024-01-01T12:00:00.12345678")]
    [InlineData("d", " 2024-01-01")]
    [InlineData("e", "ordinarytext")]
    [InlineData("e", "#n/a")]
    [InlineData("e", "#SPILL!")]
    [InlineData("e", " #REF!")]
    public void Invalid_or_unadmitted_date_and_error_lexical_values_refuse_including_caches(string type, string value)
    {
        foreach (var formula in new[] { "", "<f>unexecuted()</f>" })
            Refused(Extract(Excel($"<c r='A1' t='{type}'>{formula}<v>{value}</v></c>"), SpecSourceFormat.Xlsx),
                SpecExtractionOutcome.Malformed, type == "d" ? "invalid-date-cell" : "invalid-error-cell");
    }

    [Theory]
    [InlineData(14)]
    [InlineData(18)]
    [InlineData(22)]
    public void Non_descriptor_local_crc_and_sizes_must_match_the_directory(int field)
    {
        var bytes = Zip(Word()); bytes[field] ^= 1;
        Refused(SpecOfficeParser.Extract(Source(bytes)), SpecExtractionOutcome.Malformed, "conflicting-local-header");
    }

    // Produce real classic ZIP entries with streaming local placeholders and signed/unsigned descriptors.
    private static byte[] DescriptorZip(bool signed)
    {
        var original = Zip(Word());
        using var output = new MemoryStream();
        var records = new List<byte[]>();
        var p = Central(original);
        while (BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(p, 4)) == 0x02014b50)
        {
            var size = 46 + BinaryPrimitives.ReadUInt16LittleEndian(original.AsSpan(p + 28, 2)) +
                BinaryPrimitives.ReadUInt16LittleEndian(original.AsSpan(p + 30, 2)) + BinaryPrimitives.ReadUInt16LittleEndian(original.AsSpan(p + 32, 2));
            var record = original.AsSpan(p, size).ToArray();
            var local = (int)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(42, 4));
            var compressed = (int)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(20, 4));
            var length = 30 + BinaryPrimitives.ReadUInt16LittleEndian(original.AsSpan(local + 26, 2)) +
                BinaryPrimitives.ReadUInt16LittleEndian(original.AsSpan(local + 28, 2)) + compressed;
            var body = original.AsSpan(local, length).ToArray(); body[6] |= 8; body.AsSpan(14, 12).Clear();
            record[8] |= 8;
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(42, 4), (uint)output.Position);
            output.Write(body);
            if (signed) output.Write(new byte[] { 0x50, 0x4b, 0x07, 0x08 });
            output.Write(record.AsSpan(16, 12));
            records.Add(record); p += size;
        }
        var start = output.Position;
        foreach (var record in records) output.Write(record);
        var end = original.AsSpan(original.Length - 22).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(12, 4), (uint)(output.Position - start));
        BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(16, 4), (uint)start);
        output.Write(end);
        return output.ToArray();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Classic_streaming_descriptors_are_validated_and_supported(bool signed)
    {
        var bytes = DescriptorZip(signed);
        Assert.Equal("café 東京", Assert.Single(SpecOfficeParser.Extract(Source(bytes)).Chunks).Text);
        var central = Central(bytes);
        var compressed = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(central + 20, 4));
        var descriptor = 30 + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(26, 2)) +
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(28, 2)) + compressed;
        foreach (var field in new[] { 0, 4, 8 })
        {
            var corrupt = (byte[])bytes.Clone(); corrupt[descriptor + (signed ? 4 : 0) + field] ^= 1;
            Refused(SpecOfficeParser.Extract(Source(corrupt)), SpecExtractionOutcome.Malformed, "invalid-data-descriptor");
        }
        var localConflict = (byte[])bytes.Clone(); localConflict[14] = 1;
        Refused(SpecOfficeParser.Extract(Source(localConflict)), SpecExtractionOutcome.Malformed, "conflicting-local-header");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Descriptors_cannot_overlap_the_next_local_header_or_central_directory(bool last)
    {
        var bytes = DescriptorZip(true); var p = Central(bytes);
        if (last)
        {
            while (true)
            {
                var next = p + 46 + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(p + 28, 2)) +
                    BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(p + 30, 2)) + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(p + 32, 2));
                if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(next, 4)) != 0x02014b50) break;
                p = next;
            }
        }
        // Move the declared descriptor start into its own tail, so it would consume the next record's bytes.
        var compressed = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p + 20, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(p + 20, 4), compressed + 8);
        Refused(SpecOfficeParser.Extract(Source(bytes)), SpecExtractionOutcome.Malformed, "invalid-data-descriptor");
    }

    [Fact]
    public void Pdf_page_locator_has_a_closed_v2_tuple_without_claiming_ocr_or_byte_offsets()
    {
        var page = new SpecEvidenceLocator.PdfPage(2);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) SpecEvidenceLocatorJson.Write(writer, page);
        Assert.Equal("[\"pdf-page\",2]", Encoding.UTF8.GetString(stream.ToArray()));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpecEvidenceLocator.PdfPage(0));
    }

    [Fact]
    public void Compressed_normal_documents_extract_and_deep_xml_is_refused_without_partial_chunks()
    {
        var source = Source(Zip(Word(), CompressionLevel.Optimal));
        Assert.Equal("café 東京", SpecOfficeParser.Extract(source).Chunks.Single().Text);
        var parts = Word(); parts["word/document.xml"] = new string(' ', 10) + string.Concat(Enumerable.Repeat("<x>", 66)) + string.Concat(Enumerable.Repeat("</x>", 66));
        Refused(Extract(parts), SpecExtractionOutcome.LimitExceeded, "xml-depth");
    }

    [Fact]
    public void Package_shape_and_format_are_checked_before_content_claims()
    {
        Refused(Extract(Word(), SpecSourceFormat.Xlsx), SpecExtractionOutcome.Unsupported, "office-format-mismatch");
        var parts = Word(); parts.Remove("word/document.xml");
        Refused(Extract(parts), SpecExtractionOutcome.Malformed, "missing-package-part");
        parts = Word(); parts["word/_rels/document.xml.rels"] = $"<Relationships xmlns='{Rel}'><Relationship Id='header' Type='{R}header' Target='header1.xml'/></Relationships>";
        parts["word/header1.xml"] = $"<w:hdr xmlns:w='{W}'><w:p/></w:hdr>";
        Refused(Extract(parts), SpecExtractionOutcome.Unsupported, "unsupported-relationship");
        parts = Excel(); parts["xl/sharedStrings.xml"] = $"<sst xmlns='{S}'><si><rPh><t>phonetic</t></rPh></si></sst>";
        Refused(Extract(parts, SpecSourceFormat.Xlsx), SpecExtractionOutcome.Unsupported, "unsupported-rich-string");
    }

    [Fact]
    public void Case_sensitive_relationship_targets_do_not_silently_alias_other_parts()
    {
        var parts = Word(); parts["_rels/.rels"] = parts["_rels/.rels"].Replace("word/document.xml", "WORD/document.xml", StringComparison.Ordinal);
        Refused(Extract(parts), SpecExtractionOutcome.Malformed, "missing-package-part");
    }
}
