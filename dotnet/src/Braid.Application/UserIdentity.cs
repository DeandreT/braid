namespace Braid.Application;

/// <summary>
/// A validated signed-in user calling through a client application: the user's external identity key, the
/// client application that obtained the delegated token, and the token expiry. It carries no role or grant;
/// access needs an enabled binding of that key and the current organization and project authority.
/// </summary>
public sealed record UserCaller
{
    public UserCaller(ExternalIdentityKey identity, Guid clientApplicationId, DateTimeOffset expiresAt)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (clientApplicationId == Guid.Empty) throw new ArgumentException("A client application ID is required.", nameof(clientApplicationId));
        Identity = identity; ClientApplicationId = clientApplicationId; ExpiresAt = expiresAt.ToUniversalTime();
    }

    public ExternalIdentityKey Identity { get; }
    public Guid ClientApplicationId { get; }
    public DateTimeOffset ExpiresAt { get; }
    public override string ToString() => nameof(UserCaller);
}

public enum UserTokenFailure { Denied, Unavailable }

/// <summary>A closed refusal. It never carries token text, claims or provider diagnostics.</summary>
public sealed class UserTokenException : Exception
{
    public UserTokenException(UserTokenFailure failure) : base("The user credential was not accepted.")
    {
        if (!Enum.IsDefined(failure)) throw new ArgumentOutOfRangeException(nameof(failure));
        Failure = failure;
    }

    public UserTokenFailure Failure { get; }
}

/// <summary>Provider-neutral validation of delegated user bearer tokens. Adapters fix their issuer by configuration.</summary>
public interface IUserTokenValidator
{
    /// <summary>Returns the caller, or throws <see cref="UserTokenException"/>: Denied for any invalid token, Unavailable when keys cannot be obtained.</summary>
    Task<UserCaller> ValidateAsync(string bearerToken, CancellationToken cancellationToken);
}
