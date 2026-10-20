using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;

namespace Weft.Ai.Wire;

internal abstract class AiDecoded
{
    public sealed override string ToString() => nameof(AiDecoded);
}
internal sealed class AiDecodedText : AiDecoded
{
    internal AiDecodedText(string text) { Text = text; }
    internal string Text { get; }
}
internal sealed class AiDecodedUsage : AiDecoded
{
    internal AiDecodedUsage(AiTokenUsage usage) { Usage = usage; }
    internal AiTokenUsage Usage { get; }
}
internal sealed class AiDecodedEnd : AiDecoded
{
    internal AiDecodedEnd(AiFinishReason reason, ImmutableArray<AiToolIntent> calls, AiTokenUsage usage, bool observed)
    { Reason = reason; Calls = calls; Usage = usage; UsageObserved = observed; }
    internal AiFinishReason Reason { get; }
    internal ImmutableArray<AiToolIntent> Calls { get; }
    internal AiTokenUsage Usage { get; }
    internal bool UsageObserved { get; }
}
internal static class AiTextPieces
{
    internal static IEnumerable<string> Split(string text)
    {
        int start = 0, offset = 0, bytes = 0;
        while (offset < text.Length)
        {
            Rune rune = Rune.GetRuneAt(text, offset);
            if (bytes + rune.Utf8SequenceLength > AiCeilings.DeltaBytes)
            { yield return text.Substring(start, offset - start); start = offset; bytes = 0; }
            bytes += rune.Utf8SequenceLength; offset += rune.Utf16SequenceLength;
        }
        if (offset > start) yield return text.Substring(start, offset - start);
    }
}
