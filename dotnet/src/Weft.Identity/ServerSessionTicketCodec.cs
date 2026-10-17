using System.Buffers.Binary;
using System.Security.Cryptography;
using Weft.Application;
using Weft.Domain;

namespace Weft.Identity;

internal sealed class ServerSessionTicketCodec
{
    private const int EnvelopeLength = 120;
    private const long MaximumDeadlineMicroseconds = 315537897599999999;
    private readonly DurableSessionKeyProfile profile;

    internal ServerSessionTicketCodec(DurableSessionKeyProfile profile)
    {
        this.profile = profile ?? throw ServerSessionIdentityErrors.Create(
            ServerSessionIdentityFailureKind.ConfigurationFailure);
    }

    internal Guid OrganizationId => profile.OrganizationId;

    internal byte[] Protect(ServerSessionEnvelope envelope, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (envelope is null || envelope.Key.OrganizationId != OrganizationId)
            throw ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.UntrustedIssuance);
        var plaintext = new byte[EnvelopeLength];
        byte[]? ciphertext = null;
        byte[]? digest = null;
        try
        {
            "WST1"u8.CopyTo(plaintext);
            BinaryPrimitives.WriteUInt16BigEndian(plaintext.AsSpan(4), ServerSessionPolicy.CodecVersion);
            BinaryPrimitives.WriteUInt16BigEndian(plaintext.AsSpan(6), ServerSessionPolicy.PolicyVersion);
            digest = Convert.FromHexString(envelope.Key.HandleDigest.Value);
            digest.CopyTo(plaintext, 8);
            WriteGuid(plaintext.AsSpan(40, 16), envelope.Key.OrganizationId);
            WriteGuid(plaintext.AsSpan(56, 16), envelope.BindingId);
            BinaryPrimitives.WriteInt64BigEndian(plaintext.AsSpan(72), envelope.BindingVersion);
            WriteGuid(plaintext.AsSpan(80, 16), envelope.Owner.Value);
            WriteGuid(plaintext.AsSpan(96, 16), envelope.Epoch);
            BinaryPrimitives.WriteInt64BigEndian(plaintext.AsSpan(112), envelope.VerifiedDeadlineUtc.Ticks / 10);
            cancellationToken.ThrowIfCancellationRequested();
            ciphertext = profile.Protect(plaintext);
            cancellationToken.ThrowIfCancellationRequested();
            if (ciphertext.Length is < 1 or > ServerSessionPolicy.MaximumProtectedTicketBytes)
                throw ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.Unavailable);
            var result = ciphertext;
            ciphertext = null;
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (digest is not null)
                CryptographicOperations.ZeroMemory(digest);
            if (ciphertext is not null)
                CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    internal ServerSessionEnvelope Unprotect(ServerSessionHandle handle, CurrentServerSession current,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (handle is null || current is null || current.Facts.Key.OrganizationId != OrganizationId ||
            current.Facts.Key.HandleDigest != handle.Digest || current.Facts.RevokedAtUtc is not null ||
            current.Facts.ProtectedTicketLength is < 1 or > ServerSessionPolicy.MaximumProtectedTicketBytes)
            throw IntegrityFailure();
        byte[]? ciphertext = null;
        byte[]? plaintext = null;
        try
        {
            ciphertext = current.Facts.CopyProtectedTicket();
            cancellationToken.ThrowIfCancellationRequested();
            plaintext = profile.Unprotect(ciphertext);
            cancellationToken.ThrowIfCancellationRequested();
            var envelope = Decode(plaintext);
            RequireIdentity(handle, envelope, current.Facts, current.PrincipalId);
            var initial = InitialLifetime(current.Facts, envelope);
            if (initial.AbsoluteExpiresAtUtc != current.Facts.Lifetime.AbsoluteExpiresAtUtc ||
                initial.IdleExpiresAtUtc > current.Facts.Lifetime.IdleExpiresAtUtc)
                throw IntegrityFailure();
            cancellationToken.ThrowIfCancellationRequested();
            return envelope;
        }
        finally
        {
            if (ciphertext is not null)
                CryptographicOperations.ZeroMemory(ciphertext);
            if (plaintext is not null)
                CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    internal void ValidateCreated(ServerSessionHandle handle, ServerSessionEnvelope envelope,
        ReadOnlySpan<byte> protectedTicket, ServerSessionFacts created, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (handle is null || envelope is null || created is null || created.Version != 1 ||
            created.RevokedAtUtc is not null || protectedTicket.IsEmpty ||
            protectedTicket.Length > ServerSessionPolicy.MaximumProtectedTicketBytes)
            throw IntegrityFailure();
        RequireIdentity(handle, envelope, created, envelope.Owner);
        var persisted = created.CopyProtectedTicket();
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(protectedTicket, persisted) ||
                created.Lifetime != InitialLifetime(created, envelope))
                throw IntegrityFailure();
        }
        finally { CryptographicOperations.ZeroMemory(persisted); }
        cancellationToken.ThrowIfCancellationRequested();
    }

    public override string ToString() => nameof(ServerSessionTicketCodec);

    private ServerSessionEnvelope Decode(ReadOnlySpan<byte> plaintext)
    {
        if (plaintext.Length != EnvelopeLength || !plaintext[..4].SequenceEqual("WST1"u8) ||
            BinaryPrimitives.ReadUInt16BigEndian(plaintext[4..]) != ServerSessionPolicy.CodecVersion ||
            BinaryPrimitives.ReadUInt16BigEndian(plaintext[6..]) != ServerSessionPolicy.PolicyVersion)
            throw ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.UnsupportedFormat);
        var organization = new Guid(plaintext.Slice(40, 16), bigEndian: true);
        var binding = new Guid(plaintext.Slice(56, 16), bigEndian: true);
        var bindingVersion = BinaryPrimitives.ReadInt64BigEndian(plaintext[72..]);
        var owner = new Guid(plaintext.Slice(80, 16), bigEndian: true);
        var epoch = new Guid(plaintext.Slice(96, 16), bigEndian: true);
        var microseconds = BinaryPrimitives.ReadInt64BigEndian(plaintext[112..]);
        if (organization == Guid.Empty || binding == Guid.Empty || bindingVersion < 1 || owner == Guid.Empty ||
            epoch == Guid.Empty || microseconds is < 1 or > MaximumDeadlineMicroseconds)
            throw IntegrityFailure();
        var key = new ServerSessionKey(organization, Sha256Digest.Parse(Convert.ToHexStringLower(plaintext.Slice(8, 32))));
        return new(key, binding, bindingVersion, new PrincipalId(owner), epoch,
            new DateTimeOffset(microseconds * 10, TimeSpan.Zero));
    }

    private void RequireIdentity(ServerSessionHandle handle, ServerSessionEnvelope envelope, ServerSessionFacts facts,
        PrincipalId owner)
    {
        if (envelope.Key.OrganizationId != OrganizationId || envelope.Key.HandleDigest != handle.Digest ||
            envelope.Key != facts.Key || envelope.BindingId != facts.BindingId ||
            envelope.BindingVersion != facts.BindingVersion || envelope.Owner != owner ||
            envelope.Epoch != facts.LoginEpoch || facts.PolicyVersion != ServerSessionPolicy.PolicyVersion ||
            facts.CodecVersion != ServerSessionPolicy.CodecVersion || facts.RevokedAtUtc is not null)
            throw IntegrityFailure();
    }

    private static ServerSessionLifetime InitialLifetime(ServerSessionFacts facts, ServerSessionEnvelope envelope)
    {
        try
        {
            return ServerSessionPolicy.InitialLifetime(facts.Lifetime.IssuedAtUtc, envelope.VerifiedDeadlineUtc)
                ?? throw IntegrityFailure();
        }
        catch (ArgumentException) { throw IntegrityFailure(); }
    }

    private static void WriteGuid(Span<byte> output, Guid value)
    {
        if (!value.TryWriteBytes(output, bigEndian: true, out var written) || written != 16)
            throw IntegrityFailure();
    }

    private static ServerSessionIdentityException IntegrityFailure() =>
        ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.IntegrityFailure);
}
