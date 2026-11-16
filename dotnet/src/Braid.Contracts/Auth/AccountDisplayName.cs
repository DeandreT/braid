using System.Buffers;
using System.Globalization;
using System.Text;

namespace Braid.Contracts.Auth;

/// <summary>Untrusted presentation text only; never an account key or authority.</summary>
public static class AccountDisplayName
{
    public const int MaximumScalars = 200;

    public static bool IsValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumScalars * 2) return false;
        var remaining = value.AsSpan();
        var count = 0;
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out var rune, out var consumed) != OperationStatus.Done ||
                ++count > MaximumScalars || Rune.GetUnicodeCategory(rune) == UnicodeCategory.Control ||
                rune.Value is 0x061c or 0x200e or 0x200f or >= 0x202a and <= 0x202e or >= 0x2066 and <= 0x2069)
                return false;
            remaining = remaining[consumed..];
        }
        return true;
    }

    public static string? Optional(string? value) => IsValid(value) ? value : null;
}
