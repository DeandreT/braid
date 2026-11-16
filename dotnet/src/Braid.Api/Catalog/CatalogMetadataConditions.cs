using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Braid.Application;
using Braid.Contracts.Catalog;

namespace Braid.Api.Catalog;

internal static class CatalogMetadataConditions
{
    private const int MaximumGetCharacters = 4096;
    private const int MaximumGetTagsPerHeader = 16;
    private const int MaximumPatchCharacters = 256;

    internal static string ETag(CatalogMappingMetadata metadata) =>
        $"\"metadata-v1:{metadata.Scope.OrganizationId:D}:{metadata.Scope.ProjectId:D}:{metadata.Scope.MappingId:D}:" +
        metadata.Version.ToString(CultureInfo.InvariantCulture) + "\"";

    internal static int EvaluateGet(IHeaderDictionary headers, string currentTag)
    {
        var ifMatch = headers.IfMatch;
        var ifNoneMatch = headers.IfNoneMatch;
        if (CharacterCount(ifMatch) + CharacterCount(ifNoneMatch) > MaximumGetCharacters)
            throw CatalogMetadataRequestException.Invalid();
        // Both headers are parsed before comparison, including when If-Match will fail.
        var match = ParseOptional(ifMatch);
        var noneMatch = ParseOptional(ifNoneMatch);
        if (match is not null && !match.Wildcard &&
            !match.Tags.Any(tag => !tag.Weak && tag.Quoted == currentTag))
            return StatusCodes.Status412PreconditionFailed;
        if (noneMatch is not null && (noneMatch.Wildcard || noneMatch.Tags.Any(tag => tag.Quoted == currentTag)))
            return StatusCodes.Status304NotModified;
        return StatusCodes.Status200OK;
    }

    internal static bool PatchMatches(IHeaderDictionary headers, string currentTag)
    {
        if (headers.ContainsKey("If-None-Match"))
            throw CatalogMetadataRequestException.Invalid();
        var values = headers.IfMatch;
        if (values.Count == 0)
            throw new CatalogMetadataRequestException(CatalogErrorCodeV1.PreconditionRequired,
                StatusCodes.Status428PreconditionRequired);
        if (values.Count != 1 || values[0] is not { } text || text.Length > MaximumPatchCharacters)
            throw CatalogMetadataRequestException.Invalid();
        var condition = Parse(text, 1, allowWildcard: false);
        return !condition.Tags[0].Weak && condition.Tags[0].Quoted == currentTag;
    }

    private static long CharacterCount(StringValues values)
    {
        long count = values.Count > 0 ? values.Count - 1 : 0;
        foreach (var value in values)
            count += value?.Length ?? 0;
        return count;
    }

    private static Condition? ParseOptional(StringValues values) => values.Count == 0
        ? null : Parse(string.Join(",", values.ToArray()), MaximumGetTagsPerHeader, allowWildcard: true);

    private static Condition Parse(string value, int maximumTags, bool allowWildcard)
    {
        var position = 0;
        SkipOws(value, ref position);
        if (allowWildcard && position < value.Length && value[position] == '*')
        {
            position++;
            SkipOws(value, ref position);
            if (position != value.Length)
                throw CatalogMetadataRequestException.Invalid();
            return new(true, []);
        }
        var tags = new List<EntityTag>();
        while (position < value.Length)
        {
            var weak = position + 1 < value.Length && value[position] == 'W' && value[position + 1] == '/';
            if (weak)
                position += 2;
            if (position >= value.Length || value[position] != '"')
                throw CatalogMetadataRequestException.Invalid();
            var start = position++;
            while (position < value.Length && value[position] != '"')
            {
                var character = value[position++];
                if (character != '!' && character is not (>= '#' and <= '~' or >= '\u0080' and <= '\u00ff'))
                    throw CatalogMetadataRequestException.Invalid();
            }
            if (position >= value.Length)
                throw CatalogMetadataRequestException.Invalid();
            position++;
            tags.Add(new(weak, value[start..position]));
            if (tags.Count > maximumTags)
                throw CatalogMetadataRequestException.Invalid();
            SkipOws(value, ref position);
            if (position == value.Length)
                return new(false, tags);
            if (value[position++] != ',')
                throw CatalogMetadataRequestException.Invalid();
            SkipOws(value, ref position);
            if (position == value.Length)
                throw CatalogMetadataRequestException.Invalid();
        }
        throw CatalogMetadataRequestException.Invalid();
    }

    private static void SkipOws(string value, ref int position)
    {
        while (position < value.Length && value[position] is ' ' or '\t')
            position++;
    }

    private sealed record EntityTag(bool Weak, string Quoted);
    private sealed record Condition(bool Wildcard, IReadOnlyList<EntityTag> Tags);
}

internal sealed class CatalogMetadataRequestException(CatalogErrorCodeV1 code, int statusCode) : Exception
{
    internal CatalogErrorCodeV1 Code { get; } = code;
    internal int StatusCode { get; } = statusCode;
    internal static CatalogMetadataRequestException Invalid() =>
        new(CatalogErrorCodeV1.InvalidRequest, StatusCodes.Status400BadRequest);
}
