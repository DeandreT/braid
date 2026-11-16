using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;

namespace Braid.Ai;

internal static class AiGuard
{
    internal static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
    internal static ArgumentException Invalid() => new ArgumentException("Invalid AI value.");
    internal static int Bytes(string value, int maximum, bool allowEmpty = true)
    {
        if (value is null || value.Length > maximum || (!allowEmpty && value.Length == 0)) throw Invalid();
        try
        {
            int count = Utf8.GetByteCount(value);
            if (count > maximum) throw Invalid();
            return count;
        }
        catch (EncoderFallbackException) { throw Invalid(); }
    }
    internal static string Text(string value, int maximum, bool allowEmpty = true)
    { Bytes(value, maximum, allowEmpty); return value; }
    internal static string Token(string value, int maximum)
    {
        Bytes(value, maximum, false);
        foreach (char c in value) if (char.IsControl(c)) throw Invalid();
        return value;
    }
    internal static string Identifier(string value)
    {
        if (value is null || value.Length is < 1 or > 64 || !LowerOrDigit(value[0])) throw Invalid();
        foreach (char c in value) if (!LowerOrDigit(c) && c is not '.' and not '_' and not '-') throw Invalid();
        return value;
    }
    internal static string ToolName(string value)
    {
        if (value is null || value.Length is < 1 or > 64 || !ToolInitial(value[0])) throw Invalid();
        foreach (char c in value) if (!ToolInitial(c) && !(c >= '0' && c <= '9')) throw Invalid();
        return value;
    }
    private static bool LowerOrDigit(char c) => c is >= 'a' and <= 'z' or >= '0' and <= '9';
    private static bool ToolInitial(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_';
    internal static ImmutableArray<T> Copy<T>(IEnumerable<T> values, int maximum) where T : class
    {
        if (values is null) throw Invalid();
        try
        {
            var result = ImmutableArray.CreateBuilder<T>();
            foreach (T value in values)
            {
                if (value is null || result.Count >= maximum) throw Invalid();
                result.Add(value);
            }
            return result.ToImmutable();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        { throw Invalid(); }
    }
    internal static AiProtocolException Protocol(AiFailureCode code = AiFailureCode.MalformedResponse) => new AiProtocolException(code);
}
