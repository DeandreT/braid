using System.Globalization;
using System.Text;

namespace Braid.Application.Regression;

/// <summary>Bounded lexical comparison, not a trading-partner schema validator. Envelopes are never skipped leniently.</summary>
internal static class EdiSegmentComparison
{
    private const int MaximumBytes = 2 * 1024 * 1024;
    private const int MaximumSegments = 32_768;
    private const int MaximumSegmentBytes = 16 * 1024;
    private const int MaximumValues = 65_536;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal sealed record Difference(RegressionDifferenceKind Kind, RegressionDocumentLocation Location);
    internal sealed class InvalidDocument(RegressionDocumentLocation location) : Exception("EDI document refused.")
    { internal RegressionDocumentLocation Location { get; } = location; }

    private sealed record Separators(char Element, char Component, char Segment, char? Repetition, char? Release, char? Decimal);
    private sealed record Segment(string Id, string[][][] Elements, Separators Separators, int Interchange, int Number)
    {
        internal string? Value(int element, int component = 1) =>
            element <= Elements.Length && Elements[element - 1].Length > 0 && component <= Elements[element - 1][0].Length
                ? Elements[element - 1][0][component - 1] : null;
        internal RegressionDocumentLocation Location(int element = 0, int repetition = 0, int component = 0) =>
            new(Interchange, Number, Id, element, repetition, component);
    }

    internal static Difference? Compare(RegressionOutput expected, RegressionOutput actual, EdiComparisonOptions options,
        CancellationToken token, out RegressionDocumentDiagnostic? diagnostic)
    {
        diagnostic = null;
        List<Segment> left;
        List<Segment> right;
        try { left = Parse(expected, token); }
        catch (InvalidDocument error) { diagnostic = new(expected.Key, "expected", error.Location); return null; }
        try { right = Parse(actual, token); }
        catch (InvalidDocument error) { diagnostic = new(actual.Key, "actual", error.Location); return null; }
        for (var index = 0; index < Math.Min(left.Count, right.Count); index++)
        {
            token.ThrowIfCancellationRequested();
            var a = left[index];
            var b = right[index];
            if (a.Id != b.Id || a.Interchange != b.Interchange)
                return new(RegressionDifferenceKind.SegmentsDiffer, a.Location());
            if (options.CompareSeparators && a.Separators != b.Separators)
                return new(RegressionDifferenceKind.SeparatorsDiffer, a.Location());
            for (var element = 0; element < Math.Max(a.Elements.Length, b.Elements.Length); element++)
            {
                if (element >= a.Elements.Length || element >= b.Elements.Length)
                    return new(RegressionDifferenceKind.SegmentsDiffer, a.Location(element + 1));
                var aRepeats = a.Elements[element];
                var bRepeats = b.Elements[element];
                for (var repeat = 0; repeat < Math.Max(aRepeats.Length, bRepeats.Length); repeat++)
                {
                    if (repeat >= aRepeats.Length || repeat >= bRepeats.Length)
                        return new(RegressionDifferenceKind.SegmentsDiffer, a.Location(element + 1, repeat + 1));
                    var ac = aRepeats[repeat];
                    var bc = bRepeats[repeat];
                    for (var component = 0; component < Math.Max(ac.Length, bc.Length); component++)
                    {
                        if (component >= ac.Length || component >= bc.Length)
                            return new(RegressionDifferenceKind.SegmentsDiffer, a.Location(element + 1, repeat + 1, component + 1));
                        if (IgnoreElement(a, element + 1, options, expected.MediaType) ||
                            options.IgnoreDates && expected.MediaType == "application/edifact" && a.Id == "DTM" && element == 0 && component == 1) continue;
                        if (ac[component] != bc[component])
                            return new(RegressionDifferenceKind.SegmentsDiffer, a.Location(element + 1, repeat + 1, component + 1));
                    }
                }
            }
        }
        return left.Count == right.Count ? null : new(RegressionDifferenceKind.SegmentsDiffer,
            (left.Count > right.Count ? left[right.Count] : right[left.Count]).Location());
    }

    private static bool IgnoreElement(Segment segment, int element, EdiComparisonOptions options, string media)
    {
        var tag = segment.Id;
        if (!options.CompareSeparators && media == "application/edi-x12" && tag == "ISA" &&
            (element == 16 || element == 11 && segment.Separators.Repetition is not null)) return true;
        if (options.IgnoreControlNumbers && (media == "application/edi-x12"
            ? (tag, element) is ("ISA", 13) or ("GS", 6) or ("ST", 2) or ("SE", 2) or ("GE", 2) or ("IEA", 2)
                or ("TA1", 1) or ("AK1", 2) or ("AK2", 2) or ("IK2", 2)
            : (tag, element) is ("UNB", 5) or ("UNG", 5) or ("UNH", 1) or ("UNT", 2) or ("UNE", 2) or ("UNZ", 2)
                or ("UCI", 1) or ("UCF", 1) or ("UCM", 1))) return true;
        return options.IgnoreDates && (media == "application/edi-x12"
            ? (tag, element) is ("ISA", 9) or ("ISA", 10) or ("GS", 4) or ("GS", 5) or ("BHT", 4) or ("BHT", 5)
                or ("DTM", 2) or ("DTM", 3) or ("G62", 2) or ("G62", 4) or ("TA1", 2) or ("TA1", 3)
            : (tag, element) is ("UNB", 4) or ("UNG", 4));
    }

    internal static bool IsValidEnvelope(ReadOnlySpan<byte> bytes, string mediaType, CancellationToken token)
    {
        try { _ = Parse(bytes, mediaType, token); return true; }
        catch (InvalidDocument) { return false; }
    }

    internal static EdiExecutionValidation ValidateExecution(ReadOnlySpan<byte> bytes, string mediaType, CancellationToken token)
    {
        try { _ = Parse(bytes, mediaType, token); return EdiExecutionValidation.Valid; }
        catch (InvalidDocument) { return EdiExecutionValidation.Invalid; }
    }

    private static List<Segment> Parse(RegressionOutput output, CancellationToken token) => Parse(output.Span, output.MediaType, token);

    private static List<Segment> Parse(ReadOnlySpan<byte> bytes, string mediaType, CancellationToken token)
    {
        var initial = new RegressionDocumentLocation(1, 1, "", 0, 0, 0);
        if (bytes.Length > MaximumBytes) throw new InvalidDocument(initial);
        string text;
        try { text = Utf8.GetString(bytes); }
        catch (DecoderFallbackException) { throw new InvalidDocument(initial); }
        var x12 = mediaType == "application/edi-x12";
        var state = new Envelope(x12);
        var result = new List<Segment>();
        var settings = DefaultEdifact();
        char? advisedRepetition = null;
        var advice = false;
        var offset = 0;
        var values = 0;
        while (offset < text.Length)
        {
            token.ThrowIfCancellationRequested();
            while (offset < text.Length && text[offset] is ' ' or '\r' or '\n' or '\t') offset++;
            if (offset == text.Length) break;
            var location = new RegressionDocumentLocation(Math.Max(1, state.Interchange + (state.Open ? 0 : 1)), result.Count + 1, "", 0, 0, 0);
            if (result.Count >= MaximumSegments) throw new InvalidDocument(location);
            if (x12 && text.AsSpan(offset).StartsWith("ISA", StringComparison.Ordinal))
            {
                if (state.Open || !X12ServiceSyntax.TryRead(text.AsSpan(offset), out var syntax)) throw new InvalidDocument(location);
                settings = new(syntax.Element, syntax.Component, syntax.Segment, syntax.Repetition, null, null);
            }
            else if (!x12 && text.AsSpan(offset).StartsWith("UNA", StringComparison.Ordinal))
            {
                if (state.Open || advice || text.Length - offset < 9) throw new InvalidDocument(location);
                var una = text.AsSpan(offset, 9);
                advisedRepetition = una[7] == ' ' ? null : una[7];
                settings = new(una[4], una[3], una[8], null, una[6], una[5]);
                CheckSeparators(settings with { Repetition = advisedRepetition }, location);
                advice = true;
                offset += 9;
                continue;
            }
            else if (!x12 && !state.Open && !advice) { settings = DefaultEdifact(); advisedRepetition = null; }
            if (!x12 && advice && text.AsSpan(offset).StartsWith("UNH", StringComparison.Ordinal))
                settings = settings with { Repetition = advisedRepetition };
            var segmentStart = offset;
            var valuesBefore = values;
            var segment = ReadSegment(text, ref offset, settings, result.Count + 1, location.Interchange, ref values, token);
            if (!x12 && segment.Id == "UNB")
            {
                var version = segment.Value(1, 2);
                if (version is not ("1" or "2" or "3" or "4") || version != "4" && advisedRepetition is not null)
                    throw new InvalidDocument(segment.Location(1, 1, 2));
                settings = settings with { Repetition = version == "4" ? advisedRepetition ?? '*' : null };
                CheckSeparators(settings, segment.Location());
                // UNB declares whether repetition is active. Parse its own fields with
                // that syntax too, rather than treating a repeated control as text.
                offset = segmentStart;
                values = valuesBefore;
                segment = ReadSegment(text, ref offset, settings, result.Count + 1, location.Interchange, ref values, token);
            }
            else if (!x12 && advice && segment.Id == "UNH")
            {
                settings = settings with { Repetition = advisedRepetition };
                segment = segment with { Separators = settings };
            }
            if (advice && segment.Id is not ("UNB" or "UNH")) throw new InvalidDocument(segment.Location());
            advice = false;
            segment = state.Accept(segment) with { Number = result.Count + 1 };
            result.Add(segment);
        }
        if (state.Open || advice || result.Count == 0) throw new InvalidDocument(result.LastOrDefault()?.Location() ?? initial);
        return result;
    }

    private static Separators DefaultEdifact() => new('+', ':', '\'', null, '?', '.');

    private static void CheckSeparators(Separators value, RegressionDocumentLocation location)
    {
        var chars = new[] { (char?)value.Element, value.Component, value.Segment, value.Repetition, value.Release, value.Decimal }
            .Where(character => character is not null).Select(character => character!.Value).ToArray();
        if (chars.Any(character => (character < '!' || character > '~' || char.IsAsciiLetterOrDigit(character)) &&
                !(character == '\n' && character == value.Segment && value.Release is null)) ||
            chars.Distinct().Count() != chars.Length) throw new InvalidDocument(location);
    }

    private static Segment ReadSegment(string text, ref int offset, Separators settings, int number, int interchange,
        ref int values, CancellationToken token)
    {
        var start = offset;
        while (offset < text.Length && text[offset] != settings.Element && text[offset] != settings.Segment && offset - start < 4) offset++;
        var id = text[start..offset];
        var validTag = id.Length is >= 1 and <= 3 && char.IsAsciiLetterUpper(id[0]) && id.All(character => char.IsAsciiLetterUpper(character) || char.IsAsciiDigit(character));
        var location = new RegressionDocumentLocation(interchange, number, validTag ? id : "", 0, 0, 0);
        if (!validTag || offset == text.Length)
            throw new InvalidDocument(location);
        var elements = new List<string[][]>();
        if (text[offset++] == settings.Segment) return new(id, [], settings, interchange, number);
        var repeats = new List<string[]>();
        var components = new List<string>();
        var value = new StringBuilder();
        var isa = id == "ISA";
        while (offset < text.Length)
        {
            if ((offset & 1023) == 0) token.ThrowIfCancellationRequested();
            if (offset - start > MaximumSegmentBytes) throw new InvalidDocument(location);
            var character = text[offset++];
            if ((character < ' ' || character == '\u007f') && character != settings.Segment) throw new InvalidDocument(location);
            if (character == settings.Release)
            {
                if (offset == text.Length) throw new InvalidDocument(location);
                value.Append(text[offset++]);
                continue;
            }
            if (character == settings.Segment || character == settings.Element || !isa && (character == settings.Component || character == settings.Repetition))
            {
                if (++values > MaximumValues || components.Count >= 128) throw new InvalidDocument(location);
                components.Add(value.ToString()); value.Clear();
                if (character == settings.Component && !isa) continue;
                if (repeats.Count >= 256) throw new InvalidDocument(location);
                repeats.Add(components.ToArray()); components.Clear();
                if (character == settings.Repetition && !isa) continue;
                if (elements.Count >= 256) throw new InvalidDocument(location);
                elements.Add(repeats.ToArray()); repeats.Clear();
                if (character != settings.Segment) continue;
                if (Utf8.GetByteCount(text.AsSpan(start, offset - start)) > MaximumSegmentBytes) throw new InvalidDocument(location);
                return new(id, elements.ToArray(), settings, interchange, number);
            }
            value.Append(character);
        }
        throw new InvalidDocument(location);
    }

    private sealed class Envelope(bool x12)
    {
        private bool interchange;
        private bool group;
        private bool message;
        private int groups;
        private int messages;
        private int groupMessages;
        private int start;
        private string? interchangeControl;
        private string? groupControl;
        private string? messageControl;
        internal int Interchange { get; private set; }
        internal bool Open => interchange || group || message;

        internal Segment Accept(Segment segment)
        {
            var tag = segment.Id;
            void Require(bool condition, int element = 0)
            { if (!condition) throw new InvalidDocument(segment.Location(element)); }
            string Reference(int element)
            {
                Require(element <= segment.Elements.Length && segment.Elements[element - 1].Length == 1 &&
                    segment.Elements[element - 1][0].Length == 1, element);
                var value = segment.Value(element);
                Require(!string.IsNullOrEmpty(value), element);
                return value!;
            }
            void Count(int element, int expected) => Require(int.TryParse(Reference(element), NumberStyles.None,
                CultureInfo.InvariantCulture, out var value) && value == expected, element);
            if (tag == (x12 ? "ISA" : "UNB"))
            {
                Require(!Open && segment.Elements.Length == (x12 ? 16 : segment.Elements.Length));
                interchange = true; Interchange++; groups = messages = 0;
                interchangeControl = Reference(x12 ? 13 : 5);
            }
            else if (tag == (x12 ? "GS" : "UNG"))
            {
                Require(interchange && !group && !message && messages == 0);
                group = true; groups++; groupMessages = 0; groupControl = Reference(x12 ? 6 : 5);
            }
            else if (tag == (x12 ? "ST" : "UNH"))
            {
                Require(!message && (x12 ? group : interchange ? group || groups == 0 : !group));
                if (!interchange) Interchange++;
                message = true; start = segment.Number; messageControl = Reference(x12 ? 2 : 1);
            }
            else if (tag == (x12 ? "SE" : "UNT"))
            {
                Require(message); Count(1, segment.Number - start + 1); Require(Reference(2) == messageControl, 2);
                message = false;
                if (group) groupMessages++; else messages++;
            }
            else if (tag == (x12 ? "GE" : "UNE"))
            {
                Require(interchange && group && !message); Count(1, groupMessages); Require(Reference(2) == groupControl, 2); group = false;
            }
            else if (tag == (x12 ? "IEA" : "UNZ"))
            {
                Require(interchange && !group && !message); Count(1, x12 || groups > 0 ? groups : messages);
                Require(Reference(2) == interchangeControl, 2); interchange = false;
            }
            else Require(message || x12 && interchange && !group && tag == "TA1");
            return segment with { Interchange = Interchange };
        }
    }
}
