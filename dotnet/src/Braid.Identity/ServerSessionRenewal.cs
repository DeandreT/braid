using Microsoft.AspNetCore.Authentication;

namespace Braid.Identity;

internal enum ServerSessionRenewalKind
{
    AcknowledgedRenewed,
    AcknowledgedUnchanged,
    ObservedConcurrent
}

internal sealed class ServerSessionRenewal
{
    private readonly AuthenticationTicket ticket;

    internal ServerSessionRenewal(ServerSessionRenewalKind kind, AuthenticationTicket ticket)
    {
        Kind = kind;
        this.ticket = ticket.Clone();
    }

    internal ServerSessionRenewalKind Kind { get; }
    internal AuthenticationTicket CopyTicket() => ticket.Clone();
    public override string ToString() => nameof(ServerSessionRenewal);
}
