namespace Weft.Application;

/// <summary>
/// One machine principal from a configured identity provider: a tenant and that tenant's
/// immutable object ID. It carries no role; access needs a current invocation grant.
/// </summary>
public sealed record MachinePrincipal
{
    public const string Entra = "entra";
    private static readonly Guid ConsumerTenant = new("9188040d-6c67-4c5b-b112-36a304b66dad");

    public MachinePrincipal(string provider, Guid tenantId, Guid objectId)
    {
        if (provider != Entra || tenantId == Guid.Empty || tenantId == ConsumerTenant || objectId == Guid.Empty)
            throw new ArgumentException("A machine principal requires a supported provider, organizational tenant and object ID.");
        Provider = provider; TenantId = tenantId; ObjectId = objectId;
    }

    public string Provider { get; }
    public Guid TenantId { get; }
    public Guid ObjectId { get; }
}

/// <summary>A validated machine caller: the principal, the client application it used and the token expiry.</summary>
public sealed record MachineCaller
{
    public MachineCaller(MachinePrincipal principal, Guid applicationId, DateTimeOffset expiresAt)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (applicationId == Guid.Empty) throw new ArgumentException("A client application ID is required.", nameof(applicationId));
        Principal = principal; ApplicationId = applicationId; ExpiresAt = expiresAt.ToUniversalTime();
    }

    public MachinePrincipal Principal { get; }
    public Guid ApplicationId { get; }
    public DateTimeOffset ExpiresAt { get; }
}

public enum MachineTokenFailure { Denied, Unavailable }

/// <summary>A closed refusal. It never carries token text, claims or provider diagnostics.</summary>
public sealed class MachineTokenException : Exception
{
    public MachineTokenException(MachineTokenFailure failure) : base("The machine credential was not accepted.")
    {
        if (!Enum.IsDefined(failure)) throw new ArgumentOutOfRangeException(nameof(failure));
        Failure = failure;
    }

    public MachineTokenFailure Failure { get; }
}

/// <summary>Provider-neutral bearer-token validation for machine callers. Adapters fix their issuer by configuration.</summary>
public interface IMachineTokenValidator
{
    /// <summary>Returns the caller, or throws <see cref="MachineTokenException"/>: Denied for any invalid token, Unavailable when keys cannot be obtained.</summary>
    Task<MachineCaller> ValidateAsync(string bearerToken, CancellationToken cancellationToken);
}
