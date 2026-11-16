using System.Text;

namespace Braid.Application;

/// <summary>Document-profile publication limits, matching ferrule-host's document_limits module.</summary>
public static class DocumentFormatLimits
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static bool TryReadX12Segment(ReadOnlySpan<byte> bytes, out char segment)
    {
        segment = default;
        try
        {
            if (!X12ServiceSyntax.TryRead(Utf8.GetString(bytes).TrimStart(), out var syntax)) return false;
            segment = syntax.Segment;
            return true;
        }
        catch (DecoderFallbackException) { return false; }
    }

    public static bool WithinLimits(ReadOnlySpan<byte> bytes, string mediaType, char? configuredSegment = null)
    {
        if (mediaType == "application/json")
        {
            var quoted = false;
            var escaped = false;
            var depth = 0;
            foreach (var value in bytes)
            {
                if (escaped) { escaped = false; continue; }
                if (quoted && value == '\\') { escaped = true; continue; }
                if (value == '"') { quoted = !quoted; continue; }
                if (quoted) continue;
                if (value is (byte)'[' or (byte)'{') { if (++depth > 64) return false; }
                else if (value is (byte)']' or (byte)'}') depth = Math.Max(0, depth - 1);
            }
            return true;
        }
        if (mediaType is not ("application/edi-x12" or "application/edifact"))
            throw new ArgumentException("An explicit document media type is required.", nameof(mediaType));
        if (bytes.Length > 2 * 1024 * 1024) return false;
        string text;
        try { text = Utf8.GetString(bytes).TrimStart(); }
        catch (DecoderFallbackException) { return true; } // The codec owns encoding failures.
        if (mediaType == "application/edi-x12") return X12WithinLimits(text, configuredSegment ?? '~');
        return EdifactWithinLimits(bytes[(bytes.Length - Utf8.GetByteCount(text))..]);
    }

    // Only envelope framing is tracked here. The existing validator still owns control
    // values, references, schema and malformed-input diagnostics.
    private enum EdifactFrame { Outside, Interchange, Group, Message, GroupMessage, Standalone, Invalid }
    private enum EdifactTag { Other, UNB, UNG, UNH, UNT, UNE, UNZ }

    private static EdifactFrame Advance(EdifactFrame frame, EdifactTag tag) => (frame, tag) switch
    {
        (EdifactFrame.Outside, EdifactTag.UNB) => EdifactFrame.Interchange,
        (EdifactFrame.Outside, EdifactTag.UNH) => EdifactFrame.Standalone,
        (EdifactFrame.Interchange, EdifactTag.UNG) => EdifactFrame.Group,
        (EdifactFrame.Interchange, EdifactTag.UNH) => EdifactFrame.Message,
        (EdifactFrame.Interchange, EdifactTag.UNZ) => EdifactFrame.Outside,
        (EdifactFrame.Group, EdifactTag.UNH) => EdifactFrame.GroupMessage,
        (EdifactFrame.Group, EdifactTag.UNE) => EdifactFrame.Interchange,
        (EdifactFrame.Message, EdifactTag.UNT) => EdifactFrame.Interchange,
        (EdifactFrame.GroupMessage, EdifactTag.UNT) => EdifactFrame.Group,
        (EdifactFrame.Standalone, EdifactTag.UNT) => EdifactFrame.Outside,
        (_, not EdifactTag.Other) => EdifactFrame.Invalid,
        (EdifactFrame.Message or EdifactFrame.GroupMessage or EdifactFrame.Standalone, _) => frame,
        _ => EdifactFrame.Invalid
    };

    private static EdifactTag Tag(ReadOnlySpan<byte> bytes)
    {
        if (bytes.SequenceEqual("UNB"u8)) return EdifactTag.UNB;
        if (bytes.SequenceEqual("UNG"u8)) return EdifactTag.UNG;
        if (bytes.SequenceEqual("UNH"u8)) return EdifactTag.UNH;
        if (bytes.SequenceEqual("UNT"u8)) return EdifactTag.UNT;
        if (bytes.SequenceEqual("UNE"u8)) return EdifactTag.UNE;
        if (bytes.SequenceEqual("UNZ"u8)) return EdifactTag.UNZ;
        return EdifactTag.Other;
    }

    private static bool HasAdvice(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 9 || !bytes.StartsWith("UNA"u8)) return false;
        for (var i = 3; i < 9; i++)
        {
            if (i == 7 && bytes[i] == ' ') continue;
            if (bytes[i] is < 33 or > 126 || char.IsAsciiLetterOrDigit((char)bytes[i])) return false;
            for (var j = 3; j < i; j++) if (bytes[j] == bytes[i]) return false;
        }
        return true;
    }

    private static bool EdifactWithinLimits(ReadOnlySpan<byte> bytes)
    {
        var frame = EdifactFrame.Outside;
        var advice = false;
        var offset = 0;
        byte element = (byte)'+';
        byte terminator = (byte)'\'';
        byte release = (byte)'?';
        var segments = 0;
        while (offset < bytes.Length)
        {
            while (offset < bytes.Length && bytes[offset] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') offset++;
            if (offset == bytes.Length) break;
            if (frame == EdifactFrame.Outside && !advice)
            {
                element = (byte)'+'; terminator = (byte)'\''; release = (byte)'?';
                if (HasAdvice(bytes[offset..]))
                {
                    element = bytes[offset + 4]; release = bytes[offset + 6]; terminator = bytes[offset + 8];
                    offset += 9; // Advice is atomic even when it includes the previous terminator.
                    advice = true;
                    if (++segments > 32_768) return false;
                    continue;
                }
            }
            var start = offset;
            var released = false;
            var ended = false;
            while (offset < bytes.Length)
            {
                var value = bytes[offset++];
                if (offset - start > 16 * 1024) return false;
                if (released) released = false;
                else if (value == release) released = true;
                else if (value == terminator) { ended = true; break; }
            }
            if (ended)
            {
                if (++segments > 32_768) return false;
                var segment = bytes[start..offset];
                frame = Advance(frame, Tag(segment[..segment.IndexOfAny(element, terminator)]));
                advice = false;
            }
        }
        return true;
    }

    private static bool X12WithinLimits(string text, char terminator)
    {
        var offset = 0;
        var segments = 0;
        while (offset < text.Length)
        {
            while (offset < text.Length && text[offset] is ' ' or '\t' or '\r' or '\n') offset++;
            if (offset == text.Length) break;
            if (X12ServiceSyntax.TryRead(text.AsSpan(offset), out var syntax))
            {
                terminator = syntax.Segment;
                offset += 106;
            }
            else
            {
                var end = text.IndexOf(terminator, offset);
                var length = end < 0 ? text.Length - offset : end - offset + 1;
                if (Utf8.GetByteCount(text.AsSpan(offset, length)) > 16 * 1024) return false;
                offset += length;
            }
            if (++segments > 32_768) return false;
        }
        return true;
    }
}
