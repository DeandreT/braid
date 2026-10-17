using System.Text.Json.Serialization;

namespace Weft.Identity;

public sealed class EntraClientOptions
{
    public EntraClientOptions(Guid tenantId, Guid clientId, string publicOrigin)
    {
        if (tenantId == Guid.Empty || tenantId == new Guid("9188040d-6c67-4c5b-b112-36a304b66dad") ||
            clientId == Guid.Empty || publicOrigin is null)
            throw EntraErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure);
        TenantId = tenantId;
        ClientId = clientId;
        // Reuse qualified112's canonical HTTPS root-origin admission, not a new parser.
        PublicOrigin = new ServerSessionBrowserOptions(publicOrigin).ExpectedOrigin;
    }

    [JsonIgnore] public Guid TenantId { get; }
    [JsonIgnore] public Guid ClientId { get; }
    [JsonIgnore] public string PublicOrigin { get; }
    public override string ToString() => nameof(EntraClientOptions);
}

internal static class EntraErrors
{
    internal static ServerSessionIdentityException Create(ServerSessionIdentityFailureKind kind) =>
        ServerSessionIdentityErrors.Create(kind);

    internal static bool Recoverable(Exception error) => error is not
        (OutOfMemoryException or StackOverflowException or AccessViolationException);
}

internal static class EntraFailureScope
{
    internal static readonly AsyncLocal<EntraFailureSignal?> Current = new();
    internal static void Report(ServerSessionIdentityFailureKind kind) => Current.Value?.Refuse(kind);
}

// The manager may capture this primitive-only signal for background retrieval. It owns
// no HttpContext/profile/certificate/transport or provider diagnostic.
internal sealed class EntraFailureSignal
{
    private int kind;
    internal ServerSessionIdentityFailureKind? Kind => Volatile.Read(ref kind) is var value && value != 0
        ? (ServerSessionIdentityFailureKind)value : null;
    internal void Refuse(ServerSessionIdentityFailureKind failure)
    {
        while (true)
        {
            var previous = Volatile.Read(ref kind);
            if (previous != 0 && failure is not
                (ServerSessionIdentityFailureKind.Unavailable or ServerSessionIdentityFailureKind.ConfigurationFailure)) return;
            if (Interlocked.CompareExchange(ref kind, (int)failure, previous) == previous) return;
        }
    }
}
