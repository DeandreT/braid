using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Braid.Application;
using Braid.Domain;

namespace Braid.Identity;

internal static class ServerSessionCanonicalTicket
{
    private const string OrganizationClaim = "braid:organization_id";
    private const string EpochClaim = "braid:login_epoch";

    internal static AuthenticationTicket CreateIssue(Guid organizationId, PrincipalId owner, Guid epoch)
    {
        if (organizationId == Guid.Empty || owner is null || epoch == Guid.Empty)
            throw ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.UntrustedIssuance);
        return Create(organizationId, owner, epoch, ServerSessionSchemes.Issue, NewProperties());
    }

    internal static AuthenticationTicket CreateCurrent(CurrentServerSession current, ServerSessionEnvelope envelope)
    {
        RequireCurrentIdentity(current, envelope);
        var properties = NewProperties();
        properties.IssuedUtc = ToFrameworkTime(current.Facts.Lifetime.IssuedAtUtc);
        properties.ExpiresUtc = ToFrameworkTime(current.Facts.Lifetime.AbsoluteExpiresAtUtc);
        return Create(envelope.Key.OrganizationId, envelope.Owner, envelope.Epoch, ServerSessionSchemes.Session,
            properties);
    }

    internal static void ValidateIssue(AuthenticationTicket ticket, ServerSessionEnvelope envelope)
    {
        if (ticket is null || envelope is null ||
            !string.Equals(ticket.AuthenticationScheme, ServerSessionSchemes.Issue, StringComparison.Ordinal))
            throw ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.UntrustedIssuance);
        ValidateShape(ticket, envelope, ServerSessionIdentityFailureKind.UnsupportedFormat);
        ValidateProperties(ticket.Properties, expectedIssue: null, expectedExpiry: null, provisional: true);
    }

    internal static void ValidateRenew(AuthenticationTicket ticket, CurrentServerSession current,
        ServerSessionEnvelope envelope)
    {
        RequireCurrentIdentity(current, envelope);
        if (ticket is null ||
            !string.Equals(ticket.AuthenticationScheme, ServerSessionSchemes.Session, StringComparison.Ordinal))
            throw ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.UnsupportedFormat);
        ValidateShape(ticket, envelope, ServerSessionIdentityFailureKind.UnsupportedFormat);
        ValidateProperties(ticket.Properties, ToFrameworkTime(current.Facts.Lifetime.IssuedAtUtc),
            ToFrameworkTime(current.Facts.Lifetime.AbsoluteExpiresAtUtc), provisional: false);
    }

    internal static void ApplyCreatedTimes(AuthenticationTicket ticket, ServerSessionFacts created)
    {
        if (ticket is null || created is null)
            throw ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.IntegrityFailure);
        // No hidden precision carrier: only the framework's two existing date slots change.
        ticket.Properties.IssuedUtc = ToFrameworkTime(created.Lifetime.IssuedAtUtc);
        ticket.Properties.ExpiresUtc = ToFrameworkTime(created.Lifetime.AbsoluteExpiresAtUtc);
    }

    internal static DateTimeOffset ToFrameworkTime(DateTimeOffset databaseUtc)
    {
        if (!ServerSessionIdentityValidation.IsDatabaseInstant(databaseUtc))
            throw ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.IntegrityFailure);
        return new(databaseUtc.Ticks - databaseUtc.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
    }

    private static AuthenticationTicket Create(Guid organizationId, PrincipalId owner, Guid epoch, string scheme,
        AuthenticationProperties properties)
    {
        var identity = new ClaimsIdentity(
            [Claim(ClaimTypes.NameIdentifier, owner.Value), Claim(OrganizationClaim, organizationId),
                Claim(EpochClaim, epoch)],
            ServerSessionSchemes.Session, ClaimTypes.NameIdentifier, ClaimTypes.Role);
        return new(new ClaimsPrincipal(identity), properties, scheme);
    }

    private static Claim Claim(string type, Guid value) =>
        new(type, value.ToString("D"), ClaimValueTypes.String, ServerSessionSchemes.Session,
            ServerSessionSchemes.Session);

    private static AuthenticationProperties NewProperties() => new() { IsPersistent = false, AllowRefresh = false };

    private static void ValidateShape(AuthenticationTicket ticket, ServerSessionEnvelope envelope,
        ServerSessionIdentityFailureKind mismatch)
    {
        if (ticket.Principal.GetType() != typeof(ClaimsPrincipal))
            throw ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.UnsupportedFormat);
        var identities = ticket.Principal.Identities.Take(2).ToArray();
        if (identities.Length != 1 || identities[0].GetType() != typeof(ClaimsIdentity))
            throw ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.UnsupportedFormat);
        var identity = identities[0];
        if (!string.Equals(identity.AuthenticationType, ServerSessionSchemes.Session, StringComparison.Ordinal) ||
            !string.Equals(identity.NameClaimType, ClaimTypes.NameIdentifier, StringComparison.Ordinal) ||
            !string.Equals(identity.RoleClaimType, ClaimTypes.Role, StringComparison.Ordinal) ||
            identity.Actor is not null || identity.BootstrapContext is not null || identity.Label is not null)
            throw ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.UnsupportedFormat);
        var claims = identity.Claims.Take(4).ToArray();
        if (claims.Length != 3)
            throw ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.UnsupportedFormat);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var claim in claims)
        {
            if (claim.GetType() != typeof(Claim) || claim.Properties.Count != 0 ||
                !ReferenceEquals(claim.Subject, identity) ||
                !string.Equals(claim.ValueType, ClaimValueTypes.String, StringComparison.Ordinal) ||
                !string.Equals(claim.Issuer, ServerSessionSchemes.Session, StringComparison.Ordinal) ||
                !string.Equals(claim.OriginalIssuer, ServerSessionSchemes.Session, StringComparison.Ordinal) ||
                !seen.Add(claim.Type))
                throw ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.UnsupportedFormat);
            var expected = claim.Type switch
            {
                ClaimTypes.NameIdentifier => envelope.Owner.Value,
                OrganizationClaim => envelope.Key.OrganizationId,
                EpochClaim => envelope.Epoch,
                _ => throw ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.UnsupportedFormat)
            };
            if (!string.Equals(claim.Value, expected.ToString("D"), StringComparison.Ordinal))
                throw ServerSessionIdentityErrors.Create(mismatch);
        }
    }

    private static void ValidateProperties(AuthenticationProperties properties, DateTimeOffset? expectedIssue,
        DateTimeOffset? expectedExpiry, bool provisional)
    {
        if (properties.GetType() != typeof(AuthenticationProperties) || properties.Parameters.Count != 0 ||
            properties.IsPersistent || properties.AllowRefresh != false)
            throw ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.UnsupportedFormat);
        var canonical = NewProperties();
        canonical.IssuedUtc = provisional ? properties.IssuedUtc : expectedIssue;
        canonical.ExpiresUtc = provisional ? properties.ExpiresUtc : expectedExpiry;
        if (properties.Items.Count != canonical.Items.Count ||
            properties.Items.Any(pair => !canonical.Items.Any(expected =>
                string.Equals(expected.Key, pair.Key, StringComparison.Ordinal) &&
                string.Equals(expected.Value, pair.Value, StringComparison.Ordinal))))
            throw ServerSessionIdentityErrors.Create(provisional ? ServerSessionIdentityFailureKind.UnsupportedFormat
                : ServerSessionIdentityFailureKind.IntegrityFailure);
    }

    private static void RequireCurrentIdentity(CurrentServerSession current, ServerSessionEnvelope envelope)
    {
        if (current is null || envelope is null || current.Facts.Key != envelope.Key ||
            current.Facts.BindingId != envelope.BindingId || current.Facts.BindingVersion != envelope.BindingVersion ||
            current.Facts.LoginEpoch != envelope.Epoch || current.PrincipalId != envelope.Owner)
            throw ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.IntegrityFailure);
    }
}
