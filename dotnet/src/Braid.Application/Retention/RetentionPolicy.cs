using System.Globalization;
using Braid.Contracts.Administration;

namespace Braid.Application.Retention;

public enum RetentionFamily { Preview = 1, RegressionRun, Conversation, InvocationHistory }

/// <summary>An unconfigured family holds its data; there is no default duration.</summary>
public enum RetentionMode { NotConfigured = 1, Retain, Expire }

/// <summary>Closed outcomes; a database failure is never reported as not found.</summary>
public enum RetentionPolicyOutcome { Updated = 1, Unchanged, NotAuthorizedOrNotFound, PreconditionFailed, Conflict, InvalidRequest }

/// <summary>One family's requested policy. Applying to existing data is explicit and only meaningful for expiry.</summary>
public sealed record RetentionFamilyPolicy
{
    public const long MinimumSeconds = 3600;
    public const long MaximumSeconds = 315_360_000;

    public RetentionFamilyPolicy(RetentionFamily family, RetentionMode mode, long? durationSeconds, bool applyToExisting)
    {
        if (!Enum.IsDefined(family) || !Enum.IsDefined(mode) || (mode == RetentionMode.Expire) != (durationSeconds is not null) ||
            durationSeconds is < MinimumSeconds or > MaximumSeconds || applyToExisting && mode != RetentionMode.Expire)
            throw new ArgumentException("Invalid retention policy.");
        Family = family; Mode = mode; DurationSeconds = durationSeconds; ApplyToExisting = applyToExisting;
    }

    public RetentionFamily Family { get; }
    public RetentionMode Mode { get; }
    public long? DurationSeconds { get; }
    public bool ApplyToExisting { get; }

    public static readonly RetentionFamily[] Families = [RetentionFamily.Preview, RetentionFamily.RegressionRun, RetentionFamily.Conversation, RetentionFamily.InvocationHistory];

    public static string FamilyText(RetentionFamily family) => family switch
    {
        RetentionFamily.Preview => "preview",
        RetentionFamily.RegressionRun => "regressionRun",
        RetentionFamily.Conversation => "conversation",
        RetentionFamily.InvocationHistory => "invocationHistory",
        _ => throw new ArgumentOutOfRangeException(nameof(family))
    };
    public static RetentionFamily ParseFamily(string? text) => text switch
    {
        "preview" => RetentionFamily.Preview,
        "regressionRun" => RetentionFamily.RegressionRun,
        "conversation" => RetentionFamily.Conversation,
        "invocationHistory" => RetentionFamily.InvocationHistory,
        _ => throw new ArgumentException("Invalid retention family.")
    };
    public static string ModeText(RetentionMode mode) => mode switch
    {
        RetentionMode.NotConfigured => "notConfigured",
        RetentionMode.Retain => "retain",
        RetentionMode.Expire => "expire",
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };
    public static RetentionMode ParseMode(string? text) => text switch
    {
        "notConfigured" => RetentionMode.NotConfigured,
        "retain" => RetentionMode.Retain,
        "expire" => RetentionMode.Expire,
        _ => throw new ArgumentException("Invalid retention mode.")
    };
    public static long? ParseCount(string? text) => text is null ? null :
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0 &&
        text == value.ToString(CultureInfo.InvariantCulture) ? value : throw new ArgumentException("Invalid retention count.");

    /// <summary>The complete policy in fixed family order, or an error for a missing or repeated family.</summary>
    public static IReadOnlyList<RetentionFamilyPolicy> Complete(IEnumerable<RetentionFamilyPolicy> policies)
    {
        var ordered = policies.OrderBy(p => p.Family).ToArray();
        return ordered.Select(p => p.Family).SequenceEqual(Families) ? ordered : throw new ArgumentException("Every retention family is required once.");
    }

    public static string ETag(Guid organizationId, long version) =>
        $"\"retention-{organizationId:D}-{version.ToString(CultureInfo.InvariantCulture)}\"";

    /// <summary>Only an exact canonical strong tag for this organization is accepted.</summary>
    public static long? ParseETag(Guid organizationId, string? text)
    {
        var prefix = $"\"retention-{organizationId:D}-";
        if (text is null || text.Length > 128 || !text.StartsWith(prefix, StringComparison.Ordinal) || !text.EndsWith('"')) return null;
        return long.TryParse(text.AsSpan(prefix.Length, text.Length - prefix.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var version) &&
            version > 0 && ETag(organizationId, version) == text ? version : null;
    }
}

/// <summary>Fresh organization-administrator authority on every call; writers never retry automatically.</summary>
public interface IRetentionPolicyStore
{
    Task<RetentionPoliciesV1?> ReadRetentionAsync(ActorContext actor, Guid organizationId, CancellationToken token);
    Task<RetentionPolicyOutcome> ReplaceRetentionAsync(ActorContext actor, Guid organizationId, long expectedVersion,
        IReadOnlyList<RetentionFamilyPolicy> policies, CancellationToken token);
}
