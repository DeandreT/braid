using System.Globalization;
using System.Text;

namespace Weft.Contracts.Catalog;

internal static class CatalogValueV1
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static string RequireId(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (!Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty || value != id.ToString("D"))
            throw new ArgumentException("Catalog identifiers require canonical nonzero lowercase UUID strings.", parameterName);
        return value;
    }

    internal static string RequirePositiveDecimal(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length is < 1 or > 19 || value[0] is < '1' or > '9' ||
            value.Any(character => character is < '0' or > '9') ||
            !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < 1)
            throw new ArgumentException("Catalog versions and sequences require canonical positive bigint decimal strings.", parameterName);
        return value;
    }

    internal static string RequireText(string value, int maximumBytes, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        RequireUtf8(value, maximumBytes, parameterName);
        return value;
    }

    internal static string RequireName(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        RequireScalars(value, 200, parameterName);
        return value;
    }

    internal static string RequireDescription(string value)
    {
        RequireScalars(value, 4000, nameof(value));
        return value;
    }

    internal static string RequireFilename(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0 || value is "." or ".." || value.Any(character =>
            character is '/' or '\\' || char.IsControl(character)))
            throw new ArgumentException("The catalog display filename must be a safe basename.", nameof(value));
        RequireUtf8(value, 255, nameof(value));
        return value;
    }

    internal static void RequireHistory(DateTimeOffset createdAtUtc, DateTimeOffset updatedAtUtc)
    {
        if (updatedAtUtc < createdAtUtc)
            throw new ArgumentException("A catalog update cannot precede creation.", nameof(updatedAtUtc));
    }

    internal static void RequireAdmission(CatalogAdmissionV1 admission)
    {
        if (!Enum.IsDefined(admission))
            throw new ArgumentOutOfRangeException(nameof(admission), "Catalog admission must be valid or decoded-invalid.");
    }

    internal static IReadOnlyList<T> OwnPage<T>(IEnumerable<T> values, string parameterName) where T : class
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        var snapshot = values.Take(101).ToArray();
        if (snapshot.Length > 100 || snapshot.Any(value => value is null))
            throw new ArgumentException("A catalog page must contain at most 100 nonnull entries.", parameterName);
        return Array.AsReadOnly(snapshot);
    }

    internal static string? RequireCursor(string? value)
    {
        if (value is null)
            return null;
        if (value.Length is < 1 or > 683 || value.Any(character => character is not
            (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_')) || value.Length % 4 == 1)
            throw new ArgumentException("A catalog cursor must be bounded canonical base64url.", nameof(value));
        try
        {
            var encoded = value.Replace('-', '+').Replace('_', '/');
            var decoded = Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '='));
            if (decoded.Length > 512 || Convert.ToBase64String(decoded).TrimEnd('=').Replace('+', '-').Replace('/', '_') != value)
                throw new ArgumentException("A catalog cursor must be bounded canonical base64url.", nameof(value));
        }
        catch (FormatException)
        {
            throw new ArgumentException("A catalog cursor must be bounded canonical base64url.", nameof(value));
        }
        return value;
    }

    private static void RequireScalars(string value, int maximumScalars, string parameterName)
    {
        RequireUtf8(value, 4 * maximumScalars, parameterName);
        var count = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            _ = rune;
            if (++count > maximumScalars)
                throw new ArgumentException("Catalog text exceeds its character limit.", parameterName);
        }
    }

    private static void RequireUtf8(string value, int maximumBytes, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Contains('\0'))
            throw new ArgumentException("Persisted catalog text cannot contain NUL.", parameterName);
        try
        {
            if (StrictUtf8.GetByteCount(value) > maximumBytes)
                throw new ArgumentException("Catalog text exceeds its UTF8 byte limit.", parameterName);
        }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException("Catalog text must contain valid Unicode.", parameterName);
        }
    }
}
