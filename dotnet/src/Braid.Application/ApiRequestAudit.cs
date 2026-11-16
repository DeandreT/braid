using System.Globalization;
using Braid.Domain;

namespace Braid.Application;

/// <summary>
/// One mutating API request admitted with a signed-in user's bearer token, recorded before it runs: the principal,
/// the binding that resolved it, the client application, the operation and its route resource identifiers. It has
/// no token, claim, body or response, and resource values are only canonical UUIDs or positive decimal integers.
/// </summary>
public sealed class ApiRequestAudit
{
    public const int MaximumResources = 8;

    public ApiRequestAudit(Guid auditId, Guid organizationId, PrincipalId principalId, Guid bindingId,
        Guid clientApplicationId, string operation, string method, IEnumerable<KeyValuePair<string, string>> resources)
    {
        ArgumentNullException.ThrowIfNull(principalId);
        ArgumentNullException.ThrowIfNull(resources);
        var items = resources.ToArray();
        if (auditId == Guid.Empty || organizationId == Guid.Empty || principalId.Value == Guid.Empty || bindingId == Guid.Empty ||
            clientApplicationId == Guid.Empty || !IsOperation(operation) || method is not ("POST" or "PUT" or "PATCH" or "DELETE") ||
            items.Length > MaximumResources || items.Select(item => item.Key).Distinct(StringComparer.Ordinal).Count() != items.Length ||
            items.Any(item => !IsResourceName(item.Key) || !IsResourceValue(item.Value)))
            throw new ArgumentException("The API request audit is invalid.");
        AuditId = auditId; OrganizationId = organizationId; PrincipalId = principalId; BindingId = bindingId;
        ClientApplicationId = clientApplicationId; Operation = operation; Method = method; Resources = Array.AsReadOnly(items);
    }

    public Guid AuditId { get; }
    public Guid OrganizationId { get; }
    public PrincipalId PrincipalId { get; }
    public Guid BindingId { get; }
    public Guid ClientApplicationId { get; }
    public string Operation { get; }
    public string Method { get; }
    public IReadOnlyList<KeyValuePair<string, string>> Resources { get; }

    /// <summary>An operation ID: an ASCII letter-or-digit name of at most 128 characters starting with an uppercase letter.</summary>
    public static bool IsOperation(string? value) => value is { Length: > 0 and <= 128 } && value[0] is >= 'A' and <= 'Z' &&
        value.All(char.IsAsciiLetterOrDigit);

    public static bool IsResourceName(string? value) => value is { Length: > 0 and <= 64 } && value[0] is >= 'a' and <= 'z' &&
        value.All(char.IsAsciiLetterOrDigit);

    /// <summary>A canonical lowercase <c>D</c>-format UUID or a canonical positive decimal integer of at most 19 digits.</summary>
    public static bool IsResourceValue(string? value) => value is not null &&
        (Guid.TryParseExact(value, "D", out var id) && value == id.ToString("D") ||
         value.Length is > 0 and <= 19 && value[0] != '0' && value.All(char.IsAsciiDigit) &&
         long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _));

    public override string ToString() => nameof(ApiRequestAudit);
}

public enum ApiRequestAuditOutcome
{
    Recorded = 0,
    /// <summary>The binding is no longer enabled for its principal, or the principal is inactive; the request must not run.</summary>
    IdentityChanged = 1
}

/// <summary>Append-only storage of <see cref="ApiRequestAudit"/>. Failures are exceptions, never a recorded outcome.</summary>
public interface IApiRequestAuditWriter
{
    Task<ApiRequestAuditOutcome> RecordAsync(ApiRequestAudit audit, CancellationToken cancellationToken);
}
