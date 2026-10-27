namespace Weft.Application;

/// <summary>Complete fixed-width ISA syntax, shared by envelope admission and document bounds.</summary>
internal readonly record struct X12ServiceSyntax(char Element, char Component, char Segment, char? Repetition)
{
    internal static bool TryRead(ReadOnlySpan<char> text, out X12ServiceSyntax syntax)
    {
        syntax = default;
        if (text.Length < 106 || !text.StartsWith("ISA", StringComparison.Ordinal)) return false;
        var isa = text[..106];
        var element = isa[3];
        ReadOnlySpan<int> positions = [3, 6, 17, 20, 31, 34, 50, 53, 69, 76, 81, 83, 89, 99, 101, 103];
        for (var index = 0; index < 105; index++)
            if (isa[index] is < ' ' or > '~' || (isa[index] == element) != positions.Contains(index) || isa[index] == isa[105]) return false;
        for (var index = 84; index < 89; index++)
            if (!char.IsAsciiDigit(isa[index])) return false;
        var repetition = isa[84..89].CompareTo("00501", StringComparison.Ordinal) >= 0 ? (char?)isa[82] : null;
        static bool Punctuation(char value) => value is >= '!' and <= '~' && !char.IsAsciiLetterOrDigit(value);
        if (!Punctuation(element) || !Punctuation(isa[104]) || !(Punctuation(isa[105]) || isa[105] == '\n') ||
            element == isa[104] || element == isa[105] || isa[104] == isa[105] ||
            repetition is { } repeat && (!Punctuation(repeat) || repeat == element || repeat == isa[104] || repeat == isa[105])) return false;
        syntax = new(element, isa[104], isa[105], repetition);
        return true;
    }
}
