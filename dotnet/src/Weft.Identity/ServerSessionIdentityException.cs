namespace Weft.Identity;

public enum ServerSessionIdentityFailureKind
{
    ContextRequired = 1,
    UntrustedIssuance = 2,
    Denied = 3,
    HandleCollision = 4,
    VersionExhausted = 5,
    UnsupportedFormat = 6,
    IntegrityFailure = 7,
    ConfigurationFailure = 8,
    Unavailable = 9
}

/// <summary>A closed failure without provider, storage, ticket or key diagnostics.</summary>
public sealed class ServerSessionIdentityException : Exception
{
    internal ServerSessionIdentityException(ServerSessionIdentityFailureKind kind)
        : base("The server session operation failed.") => Kind = kind;

    public ServerSessionIdentityFailureKind Kind { get; }
    public override string ToString() => nameof(ServerSessionIdentityException);
}

internal static class ServerSessionIdentityErrors
{
    internal static ServerSessionIdentityException Create(ServerSessionIdentityFailureKind kind) => new(kind);
}

public static class ServerSessionSchemes
{
    public const string Session = "Weft.Session";
    public const string Issue = "Weft.Session.Issue";
}
