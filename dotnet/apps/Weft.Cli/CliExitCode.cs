namespace Weft.Cli;

/// <summary>Stable process exit codes. Scripts may rely on these values; never renumber them.</summary>
internal enum CliExitCode
{
    Success = 0,
    /// <summary>An unexpected client failure.</summary>
    Failure = 1,
    /// <summary>Invalid arguments, profile settings or local input.</summary>
    Usage = 2,
    /// <summary>No usable sign-in: run <c>weft login</c>.</summary>
    SignInRequired = 3,
    /// <summary>Sign-in failed, or the server refused the signed-in identity.</summary>
    SignInFailed = 4,
    PermissionDenied = 5,
    NotFound = 6,
    Conflict = 7,
    PreconditionFailed = 8,
    /// <summary>The server refused the request as invalid, too large or of an unsupported type.</summary>
    InvalidRequest = 9,
    /// <summary>Rate limited or busy after the client's bounded retries.</summary>
    RateLimited = 10,
    /// <summary>The feature is disabled or the server is temporarily unavailable.</summary>
    Unavailable = 11,
    ServerFailure = 12,
    /// <summary>The server could not be reached, or its response was not a valid Weft response.</summary>
    Transport = 13,
    /// <summary>No operating-system protected token store is available, so nothing is cached.</summary>
    ProtectedStoreUnavailable = 14,
    /// <summary>A waited-for operation finished without success (a failed run, turn or ingestion).</summary>
    OperationFailed = 15,
    Canceled = 130
}

/// <summary>A categorized failure with a fixed, safe message. Never carries tokens or response bodies.</summary>
internal sealed class CliException : Exception
{
    public CliException(CliExitCode code, string message, string? category = null) : base(message)
    {
        Code = code;
        Category = category;
    }

    public CliExitCode Code { get; }

    /// <summary>The closed server or client category (for example <c>notFound</c>), when there is one.</summary>
    public string? Category { get; }

    /// <summary>Seconds the server asked the client to wait, for rate-limit failures.</summary>
    public int? RetryAfterSeconds { get; init; }

    internal static CliException Usage(string message) => new(CliExitCode.Usage, message, "usage");
}
