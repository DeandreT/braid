using Microsoft.AspNetCore.Authentication;

namespace Weft.Identity;

/// <summary>One trusted request's issuance scope; never a provider verification or grant.</summary>
public sealed class TrustedSessionIssuance : IDisposable
{
    private Func<AuthenticationTicket>? copyTicket;
    private Action? dispose;

    internal TrustedSessionIssuance(Func<AuthenticationTicket> copyTicket, Action dispose)
    {
        this.copyTicket = copyTicket;
        this.dispose = dispose;
    }

    public AuthenticationTicket CopySignInTicket()
    {
        var copy = Volatile.Read(ref copyTicket) ?? throw ServerSessionIdentityErrors.Create(
            ServerSessionIdentityFailureKind.UntrustedIssuance);
        return copy();
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref copyTicket, null);
        Interlocked.Exchange(ref dispose, null)?.Invoke();
    }

    public override string ToString() => nameof(TrustedSessionIssuance);
}
