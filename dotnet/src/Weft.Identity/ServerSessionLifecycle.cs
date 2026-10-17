using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Weft.Application;

namespace Weft.Identity;

internal sealed class ServerSessionLifecycle
{
    private readonly ServerSessionTicketCodec codec;
    private readonly IServerSessionReader reader;
    private readonly IServerSessionWriter writer;

    internal ServerSessionLifecycle(ServerSessionTicketCodec codec, IServerSessionReader reader,
        IServerSessionWriter writer)
    {
        this.codec = codec ?? throw Failure(ServerSessionIdentityFailureKind.ConfigurationFailure);
        this.reader = reader ?? throw Failure(ServerSessionIdentityFailureKind.ConfigurationFailure);
        this.writer = writer ?? throw Failure(ServerSessionIdentityFailureKind.ConfigurationFailure);
    }

    internal async Task<ServerSessionFacts> CreateAsync(ServerSessionHandle handle,
        ServerSessionEnvelope envelope, AuthenticationTicket issueTicket, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (envelope.Key != new ServerSessionKey(codec.OrganizationId, handle.Digest))
            throw Integrity();
        ServerSessionCanonicalTicket.ValidateIssue(issueTicket, envelope);
        var protectedTicket = codec.Protect(envelope, ct);
        try
        {
            var intent = new ServerSessionCreateIntent(envelope.Key, envelope.BindingId,
                envelope.BindingVersion, envelope.Owner, envelope.Epoch, envelope.VerifiedDeadlineUtc,
                protectedTicket);
            var result = await WriteAsync(() => writer.CreateOnlyAsync(intent, ct), ct);
            if (result.Outcome != ServerSessionWriteOutcome.Created)
                throw Failure(result.Outcome switch
                {
                    ServerSessionWriteOutcome.Denied => ServerSessionIdentityFailureKind.Denied,
                    ServerSessionWriteOutcome.Conflict => ServerSessionIdentityFailureKind.HandleCollision,
                    _ => ServerSessionIdentityFailureKind.IntegrityFailure
                });
            var facts = result.Facts ?? throw Integrity();
            codec.ValidateCreated(handle, envelope, protectedTicket, facts, ct);
            ct.ThrowIfCancellationRequested();
            return facts;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedTicket);
        }
    }

    internal async Task<AuthenticationTicket?> RetrieveAsync(ServerSessionHandle handle,
        CancellationToken ct)
    {
        var current = await ObserveAsync(handle, ct);
        if (current is null)
            return null;
        var envelope = codec.Unprotect(handle, current, ct);
        var ticket = ServerSessionCanonicalTicket.CreateCurrent(current, envelope);
        ct.ThrowIfCancellationRequested();
        return ticket;
    }

    internal async Task<ServerSessionRenewal> RenewAsync(ServerSessionHandle handle,
        AuthenticationTicket ticket, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (ticket is null || ticket.AuthenticationScheme != ServerSessionSchemes.Session)
            throw Failure(ServerSessionIdentityFailureKind.UnsupportedFormat);
        var current = await ObserveAsync(handle, ct) ?? throw Failure(ServerSessionIdentityFailureKind.Denied);
        var envelope = codec.Unprotect(handle, current, ct);
        ServerSessionCanonicalTicket.ValidateRenew(ticket, current, envelope);
        var before = current.Facts;
        var result = await WriteAsync(() => writer.RenewAsync(before.Key, before.Version, ct), ct);
        ServerSessionRenewal renewal;
        if (result.Outcome == ServerSessionWriteOutcome.Conflict)
        {
            var latest = await ObserveAsync(handle, ct) ?? throw Failure(ServerSessionIdentityFailureKind.Denied);
            var latestEnvelope = codec.Unprotect(handle, latest, ct);
            RequireImmutable(before, latest.Facts);
            if (latest.PrincipalId != current.PrincipalId || latestEnvelope.Owner != envelope.Owner ||
                latestEnvelope.VerifiedDeadlineUtc != envelope.VerifiedDeadlineUtc ||
                latest.Facts.Version <= before.Version ||
                latest.Facts.Lifetime.IdleExpiresAtUtc < before.Lifetime.IdleExpiresAtUtc)
                throw Integrity();
            renewal = new(ServerSessionRenewalKind.ObservedConcurrent,
                ServerSessionCanonicalTicket.CreateCurrent(latest, latestEnvelope));
        }
        else
        {
            if (result.Outcome is not (ServerSessionWriteOutcome.Renewed or ServerSessionWriteOutcome.Unchanged))
                throw Refusal(result.Outcome);
            var after = result.Facts ?? throw Integrity();
            RequireImmutable(before, after);
            if (after.RevokedAtUtc is not null ||
                (result.Outcome == ServerSessionWriteOutcome.Renewed
                    ? before.Version == long.MaxValue || after.Version != before.Version + 1 ||
                      after.Lifetime.IdleExpiresAtUtc <= before.Lifetime.IdleExpiresAtUtc
                    : after.Version != before.Version ||
                      after.Lifetime.IdleExpiresAtUtc != before.Lifetime.IdleExpiresAtUtc))
                throw Integrity();
            // Ticket fields are immutable identity and projected issue/absolute dates only.
            renewal = new(result.Outcome == ServerSessionWriteOutcome.Renewed
                    ? ServerSessionRenewalKind.AcknowledgedRenewed : ServerSessionRenewalKind.AcknowledgedUnchanged,
                ServerSessionCanonicalTicket.CreateCurrent(current, envelope));
        }
        ct.ThrowIfCancellationRequested();
        return renewal;
    }

    internal async Task RevokeAsync(ServerSessionHandle handle, CancellationToken ct)
    {
        var key = new ServerSessionKey(codec.OrganizationId, handle.Digest);
        var result = await WriteAsync(() => writer.RevokeAsync(key, ct), ct);
        if (result.Outcome is not (ServerSessionWriteOutcome.Revoked or ServerSessionWriteOutcome.Unchanged))
            throw Failure(result.Outcome == ServerSessionWriteOutcome.VersionExhausted
                ? ServerSessionIdentityFailureKind.VersionExhausted : ServerSessionIdentityFailureKind.IntegrityFailure);
        if (result.Facts is { } facts)
        {
            if (facts.Key != key || facts.RevokedAtUtc is null)
                throw Integrity();
        }
        else if (result.Outcome != ServerSessionWriteOutcome.Unchanged)
            throw Integrity();
        ct.ThrowIfCancellationRequested();
    }

    private async Task<CurrentServerSession?> ObserveAsync(ServerSessionHandle handle, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var current = await reader.ObserveAsync(new(codec.OrganizationId, handle.Digest), ct);
            ct.ThrowIfCancellationRequested();
            return current;
        }
        catch (OperationCanceledException) { throw; }
        catch (ServerSessionIdentityException) { throw; }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        {
            throw Failure(ServerSessionIdentityFailureKind.Unavailable);
        }
    }

    private static async Task<ServerSessionWriteResult> WriteAsync(
        Func<Task<ServerSessionWriteResult>> operation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ServerSessionWriteResult? result;
        try { result = await operation(); }
        catch (OperationCanceledException) { throw; }
        catch (ServerSessionIdentityException) { throw; }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        {
            throw Failure(ServerSessionIdentityFailureKind.Unavailable);
        }
        ct.ThrowIfCancellationRequested();
        return result ?? throw Integrity();
    }

    private static void RequireImmutable(ServerSessionFacts before, ServerSessionFacts after)
    {
        var first = before.CopyProtectedTicket();
        var second = after.CopyProtectedTicket();
        try
        {
            if (after.Key != before.Key || after.BindingId != before.BindingId ||
                after.BindingVersion != before.BindingVersion || after.LoginEpoch != before.LoginEpoch ||
                after.PolicyVersion != before.PolicyVersion || after.CodecVersion != before.CodecVersion ||
                after.Lifetime.IssuedAtUtc != before.Lifetime.IssuedAtUtc ||
                after.Lifetime.AbsoluteExpiresAtUtc != before.Lifetime.AbsoluteExpiresAtUtc ||
                !first.AsSpan().SequenceEqual(second))
                throw Integrity();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(first);
            CryptographicOperations.ZeroMemory(second);
        }
    }

    private static ServerSessionIdentityException Refusal(ServerSessionWriteOutcome outcome) =>
        Failure(outcome switch
        {
            ServerSessionWriteOutcome.Denied => ServerSessionIdentityFailureKind.Denied,
            ServerSessionWriteOutcome.VersionExhausted => ServerSessionIdentityFailureKind.VersionExhausted,
            _ => ServerSessionIdentityFailureKind.IntegrityFailure
        });

    private static ServerSessionIdentityException Integrity() => Failure(ServerSessionIdentityFailureKind.IntegrityFailure);
    private static ServerSessionIdentityException Failure(ServerSessionIdentityFailureKind kind) =>
        ServerSessionIdentityErrors.Create(kind);
}
