using System.Text;
using Braid.Application;

namespace Braid.Tests;

public sealed class DocumentFormatLimitsTests
{
    private static string Edifact(string reference, string body, int count, bool custom) => custom
        ? $"UNA;|.! $UNB|UNOC;3|S|R|261026;1200|{reference}$UNH|M{reference}|ORDERS;D;96A;UN${body}UNT|{count + 2}|M{reference}$UNZ|1|{reference}$"
        : $"UNB+UNOC:3+S+R+261026:1200+{reference}'UNH+M{reference}+ORDERS:D:96A:UN'{body}UNT+{count + 2}+M{reference}'UNZ+1+{reference}'";

    [Fact]
    public void Mixed_edifact_limits_use_the_envelope_boundary_and_current_release_character()
    {
        var first = Edifact("A", "BGM+220+FIRST?'VALUE'", 1, false);
        var body = string.Concat(Enumerable.Repeat($"BGM|220|{new string('X', 100)}!!VALUE!$LITERAL$", 180));
        var second = Edifact("B", body, 180, true);
        var bytes = Encoding.UTF8.GetBytes(first + "\r\n \t" + second);
        Assert.True(second.Length > 16 * 1024);
        Assert.True(EdiEnvelopeValidation.IsValid(bytes, "application/edifact"));
        Assert.True(DocumentFormatLimits.WithinLimits(bytes, "application/edifact"));
        var crossRole = second.Replace("UNA;", "UNA'", StringComparison.Ordinal).Replace("UNOC;3", "UNOC'3", StringComparison.Ordinal)
            .Replace("ORDERS;D;96A;UN", "ORDERS'D'96A'UN", StringComparison.Ordinal).Replace('$', '?');
        var crossBytes = Encoding.UTF8.GetBytes(first + "\r\n" + crossRole);
        Assert.True(EdiEnvelopeValidation.IsValid(crossBytes, "application/edifact"));
        Assert.True(DocumentFormatLimits.WithinLimits(crossBytes, "application/edifact"));
        var oversized = Edifact("B", "BGM|220|" + new string('X', 16 * 1024) + "$", 1, true);
        Assert.False(DocumentFormatLimits.WithinLimits(Encoding.UTF8.GetBytes(first + oversized), "application/edifact"));
        var mismatch = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("UNT|182|MB", "UNT|999|MB", StringComparison.Ordinal));
        Assert.False(EdiEnvelopeValidation.IsValid(mismatch, "application/edifact"));
        Assert.True(DocumentFormatLimits.WithinLimits(mismatch, "application/edifact"));
    }

    [Fact]
    public void Edifact_resets_to_defaults_after_an_interchange_or_standalone_message()
    {
        var second = Edifact("B", "BGM+220+DEFAULT?'VALUE'", 1, false);
        foreach (var first in new[] { Edifact("A", "BGM|220|FIRST!!VALUE!$LITERAL$", 1, true), "UNA;|.! $UNH|S|ORDERS;D;96A;UN$BGM|220|ONE$UNT|3|S$" })
        {
            var bytes = Encoding.UTF8.GetBytes(first + "\r\n \t" + second);
            Assert.True(EdiEnvelopeValidation.IsValid(bytes, "application/edifact"));
            Assert.True(DocumentFormatLimits.WithinLimits(bytes, "application/edifact"));
        }
    }

    [Fact]
    public void Advice_looking_field_text_cannot_reset_edifact_size_limits()
    {
        const string literal = "BEFOREUNA;|.! $AFTER";
        var bytes = Encoding.UTF8.GetBytes(Edifact("A", $"BGM+220+{literal}'", 1, false));
        Assert.True(EdiEnvelopeValidation.IsValid(bytes, "application/edifact"));
        Assert.True(DocumentFormatLimits.WithinLimits(bytes, "application/edifact"));
        var oversized = Edifact("A", "BGM+220+" + string.Concat(Enumerable.Repeat(literal, 1000)) + "'", 1, false);
        Assert.False(DocumentFormatLimits.WithinLimits(Encoding.UTF8.GetBytes(oversized), "application/edifact"));
        var released = "UNB+UNOC:3+S+R+261026:1200+A'UNH+M+ORDERS:D:96A:UN'BGM+220+" + string.Concat(Enumerable.Repeat("UNZ+1+A?'UNA;|.! $", 1500)) + "'";
        Assert.False(DocumentFormatLimits.WithinLimits(Encoding.UTF8.GetBytes(released), "application/edifact"));
        Assert.False(DocumentFormatLimits.WithinLimits(Encoding.UTF8.GetBytes("UNA:++? $" + new string('X', 16 * 1024) + "$"), "application/edifact"));
    }

    [Fact]
    public void Headerless_unicode_terminators_preserve_the_same_utf8_segment_budget()
    {
        const char terminator = '\u2020';
        bool Within(string text) => DocumentFormatLimits.WithinLimits(Encoding.UTF8.GetBytes(text), "application/edi-x12", terminator);
        Assert.True(Within($"ISA*X X{terminator}ISA*Y Y{terminator}"));
        Assert.False(Within("ISA*" + string.Concat(Enumerable.Repeat("X ", 10_000)) + terminator));
        var atLimit = new string('X', 16 * 1024 - Encoding.UTF8.GetByteCount(terminator.ToString())) + terminator;
        Assert.True(Within(atLimit));
        Assert.False(Within("X" + atLimit));
    }
    [Fact]
    public void Every_interchange_uses_its_own_syntax_for_size_and_count_limits()
    {
        var first = DocumentPreviewTestData.X12[..106];
        var alternate = new string(first.Select(c => c switch { '*' => '|', ':' => '~', '~' => ':', '^' => '!', _ => c }).ToArray());
        bool Within(string text) => DocumentFormatLimits.WithinLimits(Encoding.UTF8.GetBytes(text), "application/edi-x12");
        Assert.True(Within(first + "BEG*OK~" + alternate + string.Concat(Enumerable.Repeat("BEG|A~B!C~D:", 4_000))));
        Assert.False(Within(first + "BEG*OK~" + alternate + "BEG|" + string.Concat(Enumerable.Repeat("A~", 10_000)) + ":"));
        Assert.False(Within(first + string.Concat(Enumerable.Repeat("BEG*OK~", 32_767)) + alternate + "BEG|OK:"));
    }
    [Fact]
    public void Partial_and_malformed_isa_never_override_the_declared_terminator()
    {
        foreach (var terminator in new[] { '~', '!' })
        {
            var partial = Encoding.UTF8.GetBytes("ISA*" + new string('X', 20_000) + terminator);
            Assert.False(DocumentFormatLimits.TryReadX12Segment(partial, out _));
            Assert.False(DocumentFormatLimits.WithinLimits(partial, "application/edi-x12", terminator));
        }
        foreach (var (offset, replacement) in new[] { (6, 'X'), (84, 'A'), (104, '*'), (82, ':'), (7, 'é') })
        {
            var header = DocumentPreviewTestData.X12[..106].ToCharArray();
            header[offset] = replacement;
            header[105] = '!';
            var text = Encoding.UTF8.GetBytes(new string(header) + string.Concat(Enumerable.Repeat("X!", 10_000)) + "~");
            Assert.False(DocumentFormatLimits.TryReadX12Segment(text, out _));
            Assert.False(DocumentFormatLimits.WithinLimits(text, "application/edi-x12", '~'));
            Assert.False(EdiEnvelopeValidation.IsValid(text, "application/edi-x12"));
        }
        Assert.True(DocumentFormatLimits.WithinLimits("ISA*OK~"u8, "application/edi-x12", '~'));
    }

    [Fact]
    public void Only_a_complete_isa_can_override_mapping_syntax()
    {
        foreach (var terminator in new[] { '~', '!', '\n' })
        {
            var header = DocumentPreviewTestData.X12[..106].Replace('~', terminator);
            var valid = Encoding.UTF8.GetBytes(header + string.Concat(Enumerable.Repeat("BEG*OK" + terminator, 4_000)));
            Assert.True(DocumentFormatLimits.TryReadX12Segment(valid, out var actual));
            Assert.Equal(terminator, actual);
            Assert.True(DocumentFormatLimits.WithinLimits(valid, "application/edi-x12", '|'));
            Assert.False(DocumentFormatLimits.WithinLimits(Encoding.UTF8.GetBytes(header + new string('X', 20_000) + terminator), "application/edi-x12", '|'));
        }
    }

    [Theory]
    [InlineData("application/edi-x12", "~", null)]
    [InlineData("application/edi-x12", "!", '!')]
    [InlineData("application/edifact", "'", null)]
    public void Exact_segment_and_document_count_bounds_are_enforced(string media, string terminator, char? configured)
    {
        bool Check(string text) => DocumentFormatLimits.WithinLimits(Encoding.UTF8.GetBytes(text), media, configured);
        Assert.True(Check(new string('x', 16 * 1024 - 1) + terminator));
        Assert.False(Check(new string('x', 16 * 1024) + terminator));
        Assert.True(Check(string.Concat(Enumerable.Repeat("A" + terminator, 32_768))));
        Assert.False(Check(string.Concat(Enumerable.Repeat("A" + terminator, 32_769))));
        Assert.False(Check(new string('x', 2 * 1024 * 1024 + 1)));
        Assert.False(Check(new string('é', 8192) + terminator));
    }

    [Fact]
    public void Json_depth_uses_the_document_profile_not_the_legacy_serializer_limit()
    {
        Assert.True(DocumentFormatLimits.WithinLimits(Encoding.UTF8.GetBytes(new string('[', 64) + "0" + new string(']', 64)), "application/json"));
        Assert.False(DocumentFormatLimits.WithinLimits(Encoding.UTF8.GetBytes(new string('[', 65) + "0" + new string(']', 65)), "application/json"));
        Assert.True(DocumentFormatLimits.WithinLimits("{\"value\":\"[[[\\\"{{{\"}"u8, "application/json"));
    }

    [Fact]
    public void Advice_and_released_terminators_are_not_counted_as_business_segments()
    {
        Assert.True(DocumentFormatLimits.WithinLimits(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("A?'B'", 32_768))), "application/edifact"));
        Assert.True(DocumentFormatLimits.WithinLimits("UNA>|.! ~A!~B~"u8, "application/edifact"));
        var x12 = DocumentPreviewTestData.X12.Replace('*', '|').Replace('~', '!');
        Assert.True(DocumentFormatLimits.WithinLimits(Encoding.UTF8.GetBytes(x12), "application/edi-x12"));
    }
}
