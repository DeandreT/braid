using System.Security.Cryptography;
using System.Text;
using Braid.Domain;

namespace Braid.Application;

public enum CatalogSearchKind { Project = 1, Mapping = 2, Revision = 3, Field = 4, Rule = 5 }

/// <summary>Literal text, not a SQL, regular-expression or full-text query language.</summary>
public sealed record CatalogSearchQuery
{
    public const int MaximumBytes = 256;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public CatalogSearchQuery(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Text = text.Trim();
        try
        {
            if (Text.Length == 0 || Utf8.GetByteCount(Text) > MaximumBytes || Text.Any(char.IsControl))
                throw new ArgumentException("Search text must contain between one and 256 UTF-8 bytes without controls.", nameof(text));
        }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException("Search text must be valid Unicode.", nameof(text));
        }
        Fingerprint = Convert.ToHexStringLower(SHA256.HashData(Utf8.GetBytes("braid.catalog-search.v1\n" + Text)));
    }

    public string Text { get; }
    public string Fingerprint { get; }
}

/// <summary>Stable resource identity; a field or rule always belongs to an exact revision.</summary>
public sealed record CatalogSearchKey : IComparable<CatalogSearchKey>
{
    public CatalogSearchKey(CatalogSearchKind kind, ProjectScope project, Guid? mappingId = null,
        Guid? revisionId = null, string? resourceId = null, Guid? projectionDerivationId = null)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        ArgumentNullException.ThrowIfNull(project);
        if (mappingId == Guid.Empty || revisionId == Guid.Empty ||
            (kind == CatalogSearchKind.Project) != (mappingId is null) ||
            (kind is CatalogSearchKind.Project or CatalogSearchKind.Mapping) != (revisionId is null) ||
            (kind is CatalogSearchKind.Field or CatalogSearchKind.Rule) != (resourceId is not null) ||
            (kind is CatalogSearchKind.Field or CatalogSearchKind.Rule) != (projectionDerivationId is not null) ||
            projectionDerivationId == Guid.Empty)
            throw new ArgumentException("Search identities must retain their complete resource ancestry.");
        if (resourceId is not null) CatalogSearchText.Require(resourceId, 2048, allowEmpty: false);
        Kind = kind;
        Project = project;
        MappingId = mappingId;
        RevisionId = revisionId;
        ResourceId = resourceId;
        ProjectionDerivationId = projectionDerivationId;
    }

    public CatalogSearchKind Kind { get; }
    public ProjectScope Project { get; }
    public Guid? MappingId { get; }
    public Guid? RevisionId { get; }
    public string? ResourceId { get; }
    public Guid? ProjectionDerivationId { get; }

    public int CompareTo(CatalogSearchKey? other)
    {
        if (other is null) return 1;
        var order = Kind.CompareTo(other.Kind);
        if (order == 0) order = Id(Project.OrganizationId, other.Project.OrganizationId);
        if (order == 0) order = Id(Project.ProjectId, other.Project.ProjectId);
        if (order == 0) order = Id(MappingId ?? Guid.Empty, other.MappingId ?? Guid.Empty);
        if (order == 0) order = Id(RevisionId ?? Guid.Empty, other.RevisionId ?? Guid.Empty);
        if (order == 0) order = Id(ProjectionDerivationId ?? Guid.Empty, other.ProjectionDerivationId ?? Guid.Empty);
        // Match PostgreSQL's bytewise C ordering, including supplementary Unicode characters.
        if (order == 0) order = Encoding.UTF8.GetBytes(ResourceId ?? "").AsSpan()
            .SequenceCompareTo(Encoding.UTF8.GetBytes(other.ResourceId ?? ""));
        return order;
    }

    private static int Id(Guid left, Guid right) => string.CompareOrdinal(left.ToString("D"), right.ToString("D"));
}

/// <summary>A range in plain text, using UTF-16 offsets for browser string slicing; never markup.</summary>
public sealed record CatalogSearchHighlight(int Start, int Length);

public sealed record CatalogSearchText
{
    public CatalogSearchText(string text, IEnumerable<CatalogSearchHighlight>? highlights = null)
    {
        Require(text, 4096, allowEmpty: true);
        var ranges = (highlights ?? []).Take(65).ToArray();
        if (ranges.Length > 64) throw new ArgumentException("Too many search highlights.", nameof(highlights));
        var end = 0;
        foreach (var range in ranges)
        {
            if (range is null || range.Start < end || range.Length < 1 || range.Start > text.Length ||
                range.Length > text.Length - range.Start || SplitsScalar(text, range.Start) ||
                SplitsScalar(text, range.Start + range.Length))
                throw new ArgumentException("Highlights must be ordered nonoverlapping Unicode ranges.", nameof(highlights));
            end = range.Start + range.Length;
        }
        Text = text;
        Highlights = Array.AsReadOnly(ranges);
    }

    public string Text { get; }
    public IReadOnlyList<CatalogSearchHighlight> Highlights { get; }

    internal static void Require(string text, int maximumBytes, bool allowEmpty)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            if ((!allowEmpty && text.Length == 0) || new UTF8Encoding(false, true).GetByteCount(text) > maximumBytes)
                throw new ArgumentException("Search text exceeds its bounded representation.", nameof(text));
        }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException("Search text must be valid Unicode.", nameof(text));
        }
    }

    private static bool SplitsScalar(string text, int offset) => offset > 0 && offset < text.Length &&
        char.IsHighSurrogate(text[offset - 1]) && char.IsLowSurrogate(text[offset]);
}

public sealed record CatalogSearchHit
{
    public CatalogSearchHit(CatalogSearchKey key, CatalogSearchText title, CatalogSearchText description, Guid? targetRevisionId = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(description);
        if (title.Text.Length == 0) throw new ArgumentException("A search result needs a title.", nameof(title));
        Key = key;
        Title = title;
        Description = description;
        if (targetRevisionId == Guid.Empty || (key.Kind == CatalogSearchKind.Mapping) != (targetRevisionId is not null))
            throw new ArgumentException("A mapping result requires its observed committed head; other resources retain their own exact ancestry.");
        TargetRevisionId = key.RevisionId ?? targetRevisionId;
    }
    public CatalogSearchKey Key { get; }
    public CatalogSearchText Title { get; }
    public CatalogSearchText Description { get; }
    public Guid? TargetRevisionId { get; }
}

/// <summary>A continuation cannot be reused for another query or organization.</summary>
public sealed record CatalogSearchCursor
{
    public CatalogSearchCursor(CatalogSearchQuery query, CatalogSearchKey after)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(after);
        QueryFingerprint = query.Fingerprint;
        After = after;
    }

    public string QueryFingerprint { get; }
    public CatalogSearchKey After { get; }

    public void RequireContext(Guid organizationId, CatalogSearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (After.Project.OrganizationId != organizationId || QueryFingerprint != query.Fingerprint)
            throw new ArgumentException("The search continuation belongs to another query or organization.");
    }
}

public sealed record CatalogSearchPage
{
    public CatalogSearchPage(CatalogOrganizationAdmission admission, CatalogSearchQuery query,
        IEnumerable<CatalogSearchHit> hits, bool hasMore)
    {
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(hits);
        var snapshot = hits.Take(101).ToArray();
        if (snapshot.Length > 100 || hasMore && snapshot.Length == 0)
            throw new ArgumentException("Search pages contain at most 100 hits and cannot have an empty continuation.");
        for (var index = 0; index < snapshot.Length; index++)
        {
            if (snapshot[index] is not { } hit || hit.Key.Project.OrganizationId != admission.OrganizationId ||
                index > 0 && snapshot[index - 1].Key.CompareTo(hit.Key) >= 0)
                throw new ArgumentException("Search pages require distinct ordered resources from the admitted organization.");
        }
        Admission = admission;
        Query = query;
        Hits = Array.AsReadOnly(snapshot);
        NextCursor = hasMore ? new(query, snapshot[^1].Key) : null;
    }

    public CatalogOrganizationAdmission Admission { get; }
    public CatalogSearchQuery Query { get; }
    public IReadOnlyList<CatalogSearchHit> Hits { get; }
    public CatalogSearchCursor? NextCursor { get; }
}

/// <summary>Reads only freshly authorized resources; search never derives a report or reads payload data.</summary>
public interface ICatalogSearch
{
    Task<CatalogSearchPage?> SearchAsync(ActorContext actor, Guid organizationId, CatalogSearchQuery query,
        int limit, CatalogSearchCursor? cursor = null, CancellationToken cancellationToken = default);
}

/// <summary>Missing retained-report indexes are explicit, never partial successful search results.</summary>
public sealed class CatalogSearchIndexPendingException : Exception
{
    public CatalogSearchIndexPendingException() : base("Retained report search indexing is pending or unavailable. Retry the search.") { }
}
