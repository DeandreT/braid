using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Braid.Contracts.Administration;

namespace Braid.Application;

public enum AiUsageGrouping { Organization = 1, Project, Principal, ModelProfile }

public sealed class AiUsageQuery
{
    public AiUsageQuery(Guid organizationId, DateTimeOffset since, DateTimeOffset until,
        AiUsageGrouping grouping = AiUsageGrouping.Organization, Guid? projectId = null,
        Guid? principalId = null, string? profileId = null, int? profileVersion = null, int limit = 50, string? cursor = null)
    {
        if (organizationId == Guid.Empty || projectId == Guid.Empty || principalId == Guid.Empty ||
            since.Offset != TimeSpan.Zero || until.Offset != TimeSpan.Zero || since >= until ||
            !Enum.IsDefined(grouping) || limit is < 1 or > 100 ||
            profileVersion is < 1 || profileVersion is not null && profileId is null ||
            profileId is not null && (profileId.Length is < 1 or > 64 ||
                !char.IsAsciiLetterLower(profileId[0]) && !char.IsAsciiDigit(profileId[0]) ||
                !profileId.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '_' or '-')))
            throw new ArgumentException("Invalid AI usage query.");
        OrganizationId = organizationId; ProjectId = projectId; PrincipalId = principalId;
        ProfileId = profileId; ProfileVersion = profileVersion; Grouping = grouping; Limit = limit;
        Since = new(since.UtcTicks - since.UtcTicks % 10, TimeSpan.Zero);
        Until = new(until.UtcTicks - until.UtcTicks % 10, TimeSpan.Zero);
        if (Since >= Until) throw new ArgumentException("Invalid AI usage interval.");
        fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        { organizationId, projectId, principalId, profileId, profileVersion, grouping, since = Since, until = Until }))));
        if (cursor is not null) Position = Decode(cursor);
    }

    public Guid OrganizationId { get; }
    public Guid? ProjectId { get; }
    public Guid? PrincipalId { get; }
    public string? ProfileId { get; }
    public int? ProfileVersion { get; }
    public AiUsageGrouping Grouping { get; }
    public DateTimeOffset Since { get; }
    public DateTimeOffset Until { get; }
    public int Limit { get; }
    public string? Position { get; }
    private readonly string fingerprint;

    public string GroupKey(AiUsageGroupV1 group)
    {
        if (group.OrganizationId != OrganizationId) throw new ArgumentException("Usage group scope differs.");
        var key = Grouping switch
        {
            AiUsageGrouping.Organization => OrganizationId.ToString("D"),
            AiUsageGrouping.Project => group.ProjectId?.ToString("D") ?? "-",
            AiUsageGrouping.Principal when group.PrincipalId is { } principal && principal != Guid.Empty => principal.ToString("D"),
            AiUsageGrouping.ModelProfile when group.ProfileId is { } profile && group.ProfileVersion is > 0 => profile + ":" + group.ProfileVersion.Value.ToString(CultureInfo.InvariantCulture),
            _ => throw new ArgumentException("Invalid usage group identity.")
        };
        if (!ValidGroupKey(key)) throw new ArgumentException("Invalid usage group identity.");
        return key;
    }

    public string Cursor(string groupKey)
    {
        if (!ValidGroupKey(groupKey)) throw new ArgumentException("Invalid usage group key.");
        return Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new CursorValue(1, fingerprint, groupKey)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private string Decode(string cursor)
    {
        if (cursor.Length is < 1 or > 1024 || cursor.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new ArgumentException("Invalid usage cursor.");
        try
        {
            var raw = cursor.Replace('-', '+').Replace('_', '/');
            var bytes = Convert.FromBase64String(raw.PadRight((raw.Length + 3) / 4 * 4, '='));
            if (Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') != cursor) throw new ArgumentException("Invalid usage cursor.");
            var value = JsonSerializer.Deserialize<CursorValue>(bytes, new JsonSerializerOptions
            {
                AllowDuplicateProperties = false,
                RespectRequiredConstructorParameters = true,
                UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
                MaxDepth = 2
            });
            if (value is null || value.Version != 1 || value.Fingerprint != fingerprint || !ValidGroupKey(value.Key))
                throw new ArgumentException("Invalid usage cursor.");
            return value.Key;
        }
        catch (Exception error) when (error is FormatException or JsonException) { throw new ArgumentException("Invalid usage cursor."); }
    }

    private bool ValidGroupKey(string key)
    {
        if (key is null || key.Length is < 1 or > 128) return false;
        if (Grouping == AiUsageGrouping.Organization) return key == OrganizationId.ToString("D");
        if (Grouping == AiUsageGrouping.Project && key == "-") return true;
        if (Grouping is AiUsageGrouping.Project or AiUsageGrouping.Principal)
            return Guid.TryParseExact(key, "D", out var id) && id != Guid.Empty && key == id.ToString("D");
        var split = key.Split(':');
        return split.Length == 2 && split[0].Length is >= 1 and <= 64 &&
            (char.IsAsciiLetterLower(split[0][0]) || char.IsAsciiDigit(split[0][0])) &&
            split[0].All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '_' or '-') &&
            int.TryParse(split[1], NumberStyles.None, CultureInfo.InvariantCulture, out var version) && version > 0 &&
            split[1] == version.ToString(CultureInfo.InvariantCulture);
    }

    private sealed record CursorValue(int Version, string Fingerprint, string Key);
}
