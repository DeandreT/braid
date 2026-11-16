using System.Buffers.Binary;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Braid.Application;
using Braid.Domain;
using Braid.Identity;

namespace Braid.Tests;

/// <summary>Real protection and handwritten wire/schema vectors; no host, cookie or provider activation.</summary>
public sealed class ServerSessionTicketCodecTests
{
    private static readonly Guid Organization = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
    private static readonly Guid Binding = Guid.Parse("10213243-5465-7687-98a9-bacbdcedfe0f");
    private static readonly PrincipalId Owner = new(Guid.Parse("ffeeddcc-bbaa-9988-7766-554433221100"));
    private static readonly Guid Epoch = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
    private static readonly DateTimeOffset Issued = new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private const string VectorKey = "bs1.AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8";
    private const string VectorDigest = "630dcd2966c4336691125448bbb25b4ff412a49c732db2c8abc1b8581bd710dd";

    [Fact]
    public void KnownHandleHashesTheOriginalBytesAndRejectsAlternateLastBits()
    {
        Assert.True(ServerSessionHandle.TryParse(VectorKey, out var handle), "The canonical handle was refused.");
        Assert.True(string.Equals(VectorDigest, handle.Digest.Value, StringComparison.Ordinal),
            "The handle digest did not match the independent SHA-256 vector.");
        Assert.True(string.Equals(VectorKey, handle.ExportKey(), StringComparison.Ordinal),
            "The handle was rewritten.");
        Assert.True(ServerSessionHandle.TryParse("bs1." + new string('A', 43), out var zero),
            "The zero-byte vector was refused.");
        Assert.True(string.Equals("66687aadf862bd776c8fc18b8e9f8e20089714856ee233b3902a591d0d5f2925",
            zero.Digest.Value, StringComparison.Ordinal), "The zero-byte SHA-256 vector changed.");
        Assert.False(ServerSessionHandle.TryParse("bs1." + new string('A', 42) + "B", out _),
            "Noncanonical unused base64 bits were admitted.");
    }

    [Fact]
    public void HandleParserRejectsWhitespaceAlternateAlphabetsLengthAndPrefixChanges()
    {
        string?[] invalid = [null, "", " " + VectorKey, VectorKey + " ", VectorKey + "=", VectorKey[..^1],
            "BS1." + VectorKey[4..], "bs2." + VectorKey[4..], "bs1." + new string('+', 43),
            "bs1." + new string('/', 43), "bs1." + new string('é', 43), "bs1." + new string('\0', 43),
            "bs1.%41" + new string('A', 40)];
        foreach (var value in invalid)
            Assert.False(ServerSessionHandle.TryParse(value, out _), "A malformed handle was admitted.");
    }

    [Fact]
    public void NewlyAllocatedHandlesRoundTripWithoutDiagnosticOrSerializationExposure()
    {
        var first = ServerSessionHandle.Create();
        var second = ServerSessionHandle.Create();
        var key = first.ExportKey();
        Assert.True(key.Length == 47 && key.All(char.IsAscii), "The random handle wire shape changed.");
        Assert.True(ServerSessionHandle.TryParse(key, out var parsed) && parsed.Digest == first.Digest,
            "The generated handle did not round-trip.");
        Assert.False(string.Equals(key, second.ExportKey(), StringComparison.Ordinal),
            "Independent allocations returned the same handle.");
        Assert.True(string.Equals(first.ToString(), nameof(ServerSessionHandle), StringComparison.Ordinal),
            "Handle diagnostics changed.");
        Assert.True(string.Equals("{}", JsonSerializer.Serialize(first), StringComparison.Ordinal),
            "Handle serialization exposed a field.");
    }

    [Fact]
    public void ProtectedPlaintextMatchesTheIndependent120ByteNetworkOrderVector()
    {
        using var fixture = new SessionCodecTestProfile(Organization);
        var handle = ParseHandle();
        var envelope = new ServerSessionEnvelope(new(Organization, handle.Digest), Binding,
            0x0102030405060708, Owner, Epoch, new DateTimeOffset(12345670, TimeSpan.Zero));
        var ciphertext = fixture.Codec.Protect(envelope, CancellationToken.None);
        var plaintext = fixture.UnprotectRaw(ciphertext);
        var expected = Convert.FromHexString(
            "4253543100010001" + VectorDigest +
            "00112233445566778899aabbccddeeff" + "102132435465768798a9bacbdcedfe0f" +
            "0102030405060708" + "ffeeddccbbaa99887766554433221100" +
            "0123456789abcdef0123456789abcdef" + "000000000012d687");
        try
        {
            Assert.True(plaintext.Length == 120 && plaintext.AsSpan().SequenceEqual(expected),
                "The protected envelope did not match the independent fixed wire vector.");
            Assert.True(ciphertext.Length is >= 1 and <= 16384, "Protection returned an out-of-bounds blob.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(expected);
        }
    }

    [Fact]
    public void DecoderRefusesTrailingShortMagicAndVersionChanges()
    {
        using var fixture = new SessionCodecTestProfile(Organization);
        var originalCiphertext = fixture.Codec.Protect(Envelope(), CancellationToken.None);
        var plaintext = fixture.UnprotectRaw(originalCiphertext);
        try
        {
            var unknownMagic = (byte[])plaintext.Clone();
            unknownMagic[0] ^= 1;
            var unknownCodec = (byte[])plaintext.Clone();
            unknownCodec[5] = 2;
            var unknownPolicy = (byte[])plaintext.Clone();
            unknownPolicy[7] = 2;
            byte[][] invalid = [plaintext[..119], [.. plaintext, 0], unknownMagic, unknownCodec, unknownPolicy];
            foreach (var candidate in invalid)
            {
                try
                {
                    var blob = fixture.ProtectRaw(candidate);
                    try
                    {
                        var current = Current(blob);
                        Failure(ServerSessionIdentityFailureKind.UnsupportedFormat,
                            () => fixture.Codec.Unprotect(ParseHandle(), current, CancellationToken.None));
                    }
                    finally { CryptographicOperations.ZeroMemory(blob); }
                }
                finally { CryptographicOperations.ZeroMemory(candidate); }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(originalCiphertext);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    [Fact]
    public void DecoderRefusesEveryEmptyIdentityAndScalarEndpointOutsideTheFormat()
    {
        using var fixture = new SessionCodecTestProfile(Organization);
        var originalCiphertext = fixture.Codec.Protect(Envelope(), CancellationToken.None);
        var plaintext = fixture.UnprotectRaw(originalCiphertext);
        try
        {
            foreach (var offset in new[] { 40, 56, 80, 96 })
            {
                var candidate = (byte[])plaintext.Clone();
                candidate.AsSpan(offset, 16).Clear();
                Refuse(candidate);
            }
            foreach (var value in new[] { 0L, -1L, long.MinValue })
            {
                var candidate = (byte[])plaintext.Clone();
                BinaryPrimitives.WriteInt64BigEndian(candidate.AsSpan(72), value);
                Refuse(candidate);
            }
            foreach (var value in new[] { 0L, -1L, 315537897600000000L, long.MaxValue })
            {
                var candidate = (byte[])plaintext.Clone();
                BinaryPrimitives.WriteInt64BigEndian(candidate.AsSpan(112), value);
                Refuse(candidate);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(originalCiphertext);
            CryptographicOperations.ZeroMemory(plaintext);
        }

        void Refuse(byte[] candidate)
        {
            byte[]? blob = null;
            try
            {
                blob = fixture.ProtectRaw(candidate);
                var current = Current(blob);
                Failure(ServerSessionIdentityFailureKind.IntegrityFailure,
                    () => fixture.Codec.Unprotect(ParseHandle(), current, CancellationToken.None));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(candidate);
                if (blob is not null)
                    CryptographicOperations.ZeroMemory(blob);
            }
        }
    }

    [Fact]
    public void FreshRowOwnerBindingEpochAndRequestedDigestAreAllChecked()
    {
        using var fixture = new SessionCodecTestProfile(Organization);
        var blob = fixture.Codec.Protect(Envelope(), CancellationToken.None);
        try
        {
            var facts = Facts(blob);
            CurrentServerSession[] invalid =
            [
                Current(blob, principal: new(Guid.Parse("11111111-2222-3333-4444-555555555555"))),
                Observe(new(facts.Key, Guid.NewGuid(), facts.BindingVersion, facts.LoginEpoch, facts.Lifetime, 1, blob)),
                Observe(new(facts.Key, Binding, 8, facts.LoginEpoch, facts.Lifetime, 1, blob)),
                Observe(new(facts.Key, Binding, 7, Guid.NewGuid(), facts.Lifetime, 1, blob)),
                Observe(new(new(Guid.NewGuid(), facts.Key.HandleDigest), Binding, 7, Epoch, facts.Lifetime, 1, blob))
            ];
            foreach (var current in invalid)
                Failure(ServerSessionIdentityFailureKind.IntegrityFailure,
                    () => fixture.Codec.Unprotect(ParseHandle(), current, CancellationToken.None));
            Failure(ServerSessionIdentityFailureKind.IntegrityFailure,
                () => fixture.Codec.Unprotect(ServerSessionHandle.Create(), Current(blob), CancellationToken.None));
        }
        finally { CryptographicOperations.ZeroMemory(blob); }
    }

    [Fact]
    public void DatabaseClippedAbsoluteLifetimeAndLegitimateRenewalRetainExactMicroseconds()
    {
        using var fixture = new SessionCodecTestProfile(Organization);
        var issued = Issued.AddTicks(1234560);
        var envelope = Envelope(issued.AddMinutes(90));
        var blob = fixture.Codec.Protect(envelope, CancellationToken.None);
        try
        {
            var initial = new ServerSessionLifetime(issued, issued.AddMinutes(20), issued.AddMinutes(60));
            var created = Facts(blob, lifetime: initial);
            fixture.Codec.ValidateCreated(ParseHandle(), envelope, blob, created, CancellationToken.None);
            var renewed = Facts(blob, 2, new(issued, issued.AddMinutes(30), issued.AddMinutes(60)));
            var decoded = fixture.Codec.Unprotect(ParseHandle(), Observe(renewed), CancellationToken.None);
            Assert.True(decoded.VerifiedDeadlineUtc == issued.AddMinutes(90),
                "The verified deadline was replaced by a framework or database-clipped value.");
            var changedAbsolute = Facts(blob, 2, new(issued, issued.AddMinutes(30), issued.AddMinutes(59)));
            Failure(ServerSessionIdentityFailureKind.IntegrityFailure,
                () => fixture.Codec.Unprotect(ParseHandle(), Observe(changedAbsolute), CancellationToken.None));
            var shortenedInitialIdle = Facts(blob, 2, new(issued, issued.AddMinutes(19), issued.AddMinutes(60)));
            Failure(ServerSessionIdentityFailureKind.IntegrityFailure,
                () => fixture.Codec.Unprotect(ParseHandle(), Observe(shortenedInitialIdle), CancellationToken.None));
        }
        finally { CryptographicOperations.ZeroMemory(blob); }
    }

    [Fact]
    public void CreationRequiresExactAcknowledgedBlobVersionAndInitialLifetime()
    {
        using var fixture = new SessionCodecTestProfile(Organization);
        var envelope = Envelope();
        var blob = fixture.Codec.Protect(envelope, CancellationToken.None);
        try
        {
            var altered = (byte[])blob.Clone();
            altered[^1] ^= 1;
            try
            {
                Failure(ServerSessionIdentityFailureKind.IntegrityFailure,
                    () => fixture.Codec.ValidateCreated(ParseHandle(), envelope, altered, Facts(blob), CancellationToken.None));
                Failure(ServerSessionIdentityFailureKind.IntegrityFailure,
                    () => fixture.Codec.ValidateCreated(ParseHandle(), envelope, blob, Facts(blob, 2), CancellationToken.None));
                Failure(ServerSessionIdentityFailureKind.IntegrityFailure,
                    () => fixture.Codec.ValidateCreated(ParseHandle(), envelope, blob,
                        Facts(blob, lifetime: new(Issued, Issued.AddMinutes(19), Issued.AddMinutes(60))),
                        CancellationToken.None));
            }
            finally { CryptographicOperations.ZeroMemory(altered); }
        }
        finally { CryptographicOperations.ZeroMemory(blob); }
    }

    [Fact]
    public void EnvelopeAdmitsFiniteMicrosecondEndpointsAndRefusesUntrustedPrimitiveShapes()
    {
        using var fixture = new SessionCodecTestProfile(Organization);
        foreach (var ticks in new[] { 10L, 3155378975999999990L })
        {
            var envelope = Envelope(new DateTimeOffset(ticks, TimeSpan.Zero));
            var blob = fixture.Codec.Protect(envelope, CancellationToken.None);
            var plaintext = fixture.UnprotectRaw(blob);
            try
            {
                Assert.True(BinaryPrimitives.ReadInt64BigEndian(plaintext.AsSpan(112)) == ticks / 10,
                    "A finite microsecond endpoint was changed by the envelope format.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(blob);
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        var key = new ServerSessionKey(Organization, ParseHandle().Digest);
        Action[] invalid =
        [
            () => new ServerSessionEnvelope(null!, Binding, 7, Owner, Epoch, Issued),
            () => new ServerSessionEnvelope(key, Guid.Empty, 7, Owner, Epoch, Issued),
            () => new ServerSessionEnvelope(key, Binding, 0, Owner, Epoch, Issued),
            () => new ServerSessionEnvelope(key, Binding, 7, null!, Epoch, Issued),
            () => new ServerSessionEnvelope(key, Binding, 7, Owner, Guid.Empty, Issued),
            () => new ServerSessionEnvelope(key, Binding, 7, Owner, Epoch, Issued.AddTicks(1)),
            () => new ServerSessionEnvelope(key, Binding, 7, Owner, Epoch, Issued.ToOffset(TimeSpan.FromHours(1))),
            () => new ServerSessionEnvelope(key, Binding, 7, Owner, Epoch, DateTimeOffset.MinValue),
            () => new ServerSessionEnvelope(key, Binding, 7, Owner, Epoch, DateTimeOffset.MaxValue)
        ];
        foreach (var action in invalid)
            Failure(ServerSessionIdentityFailureKind.UntrustedIssuance, action);
        var valid = Envelope();
        Assert.True(string.Equals(valid.ToString(), nameof(ServerSessionEnvelope), StringComparison.Ordinal) &&
            string.Equals(JsonSerializer.Serialize(valid), "{}", StringComparison.Ordinal),
            "Envelope diagnostics exposed a private primitive.");
        var foreign = new ServerSessionEnvelope(new(Guid.NewGuid(), ParseHandle().Digest), Binding, 7, Owner, Epoch, Issued);
        Failure(ServerSessionIdentityFailureKind.UntrustedIssuance,
            () => fixture.Codec.Protect(foreign, CancellationToken.None));
    }

    [Fact]
    public void CorruptionAndForeignApplicationOrPurposeRemainIntegrityFailures()
    {
        using var fixture = new SessionCodecTestProfile(Organization);
        var blob = fixture.Codec.Protect(Envelope(), CancellationToken.None);
        var plaintext = fixture.UnprotectRaw(blob);
        try
        {
            var corrupt = (byte[])blob.Clone();
            corrupt[^1] ^= 1;
            try
            {
                Failure(ServerSessionIdentityFailureKind.IntegrityFailure,
                    () => fixture.Codec.Unprotect(ParseHandle(), Current(corrupt), CancellationToken.None));
            }
            finally { CryptographicOperations.ZeroMemory(corrupt); }
            using var foreign = DurableSessionKeyProfile.Open(
                new(Organization, fixture.Application + ".different", fixture.KeyDirectory), fixture.Certificate, []);
            var foreignCodec = new ServerSessionTicketCodec(foreign);
            Failure(ServerSessionIdentityFailureKind.IntegrityFailure,
                () => foreignCodec.Unprotect(ParseHandle(), Current(blob), CancellationToken.None));
            var wrongPurpose = fixture.ProtectRaw(plaintext, purpose: "Braid.UnrelatedPurpose");
            try
            {
                Failure(ServerSessionIdentityFailureKind.IntegrityFailure,
                    () => fixture.Codec.Unprotect(ParseHandle(), Current(wrongPurpose), CancellationToken.None));
            }
            finally { CryptographicOperations.ZeroMemory(wrongPurpose); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(blob);
        }
    }

    [Fact]
    public void CanonicalTicketsAreIndependentMinimalGraphsAndDoNotCopyProviderFacts()
    {
        var envelope = Envelope();
        var first = ServerSessionCanonicalTicket.CreateIssue(Organization, Owner, Epoch);
        var second = ServerSessionCanonicalTicket.CreateIssue(Organization, Owner, Epoch);
        Assert.True(first.AuthenticationScheme == ServerSessionSchemes.Issue &&
            first.Principal.Identities.Count() == 1 && first.Principal.Claims.Count() == 3,
            "The issue graph included unexpected identity facts.");
        Assert.True(first.Properties.IssuedUtc is null && first.Properties.ExpiresUtc is null &&
            !first.Properties.IsPersistent && first.Properties.AllowRefresh == false &&
            first.Properties.Parameters.Count == 0, "The issue graph carried authoritative provisional dates.");
        first.Principal.Identities.Single().AddClaim(new(ClaimTypes.Role, "provider-role"));
        first.Properties.Items["provider-token"] = "synthetic-token";
        ServerSessionCanonicalTicket.ValidateIssue(second, envelope);
        Failure(ServerSessionIdentityFailureKind.UnsupportedFormat,
            () => ServerSessionCanonicalTicket.ValidateIssue(first, envelope));
        Assert.False(ReferenceEquals(first.Principal, second.Principal) ||
            ReferenceEquals(first.Properties, second.Properties), "Issue graphs shared mutable state.");
    }

    [Fact]
    public void FrameworkProjectionFloorsFractionsAndFaithfullyAllowsEqualSecondEndpoints()
    {
        DateTimeOffset[] originals = [Issued.AddTicks(1234560), Issued.AddTicks(9999990), Issued.AddSeconds(1)];
        DateTimeOffset[] expected = [Issued, Issued, Issued.AddSeconds(1)];
        for (var index = 0; index < originals.Length; index++)
            Assert.True(ServerSessionCanonicalTicket.ToFrameworkTime(originals[index]) == expected[index],
                "The framework time projection changed.");
        Assert.True(ServerSessionCanonicalTicket.ToFrameworkTime(new DateTimeOffset(10, TimeSpan.Zero)) ==
            DateTimeOffset.MinValue, "A valid tiny instant did not project faithfully.");
        var tinyFrameworkProperties = new AuthenticationProperties
        {
            IssuedUtc = ServerSessionCanonicalTicket.ToFrameworkTime(new DateTimeOffset(10, TimeSpan.Zero)),
            ExpiresUtc = ServerSessionCanonicalTicket.ToFrameworkTime(new DateTimeOffset(20, TimeSpan.Zero))
        };
        Assert.True(tinyFrameworkProperties.IssuedUtc == DateTimeOffset.MinValue &&
            tinyFrameworkProperties.ExpiresUtc == DateTimeOffset.MinValue,
            "The actual framework property slots did not retain a faithfully collapsed tiny interval.");

        using var fixture = new SessionCodecTestProfile(Organization);
        var issued = Issued.AddTicks(1234560);
        var deadline = issued.AddTicks(10);
        var envelope = Envelope(deadline);
        var blob = fixture.Codec.Protect(envelope, CancellationToken.None);
        try
        {
            var facts = Facts(blob, lifetime: new(issued, deadline, deadline));
            fixture.Codec.ValidateCreated(ParseHandle(), envelope, blob, facts, CancellationToken.None);
            var current = Observe(facts);
            var decoded = fixture.Codec.Unprotect(ParseHandle(), current, CancellationToken.None);
            var ticket = ServerSessionCanonicalTicket.CreateCurrent(current, decoded);
            Assert.True(ticket.Properties.IssuedUtc == Issued && ticket.Properties.ExpiresUtc == Issued,
                "The same-second framework interval was extended or retained private fractions.");
            Assert.True(facts.Lifetime.IssuedAtUtc == issued && facts.Lifetime.AbsoluteExpiresAtUtc == deadline &&
                decoded.VerifiedDeadlineUtc == deadline, "Framework projection changed authoritative microseconds.");
            ServerSessionCanonicalTicket.ValidateRenew(ticket, current, decoded);
            var issue = ServerSessionCanonicalTicket.CreateIssue(Organization, Owner, Epoch);
            ServerSessionCanonicalTicket.ApplyCreatedTimes(issue, facts);
            Assert.True(issue.Properties.IssuedUtc == Issued && issue.Properties.ExpiresUtc == Issued,
                "The acknowledged issue ticket used a different time projection.");
            ticket.Properties.ExpiresUtc = Issued.AddSeconds(1);
            Failure(ServerSessionIdentityFailureKind.IntegrityFailure,
                () => ServerSessionCanonicalTicket.ValidateRenew(ticket, current, decoded));
        }
        finally { CryptographicOperations.ZeroMemory(blob); }
    }

    [Fact]
    public void FrameworkProjectionRefusesNonUtcSubMicrosecondAndSentinelDatabaseInputs()
    {
        DateTimeOffset[] invalid = [Issued.ToOffset(TimeSpan.FromHours(1)), Issued.AddTicks(1),
            DateTimeOffset.MinValue, DateTimeOffset.MaxValue];
        foreach (var value in invalid)
            Failure(ServerSessionIdentityFailureKind.IntegrityFailure,
                () => ServerSessionCanonicalTicket.ToFrameworkTime(value));
    }

    [Fact]
    public void OnlyRecognizedProvisionalDateSlotsAreAcceptedBeforeCreation()
    {
        var envelope = Envelope();
        var ticket = ServerSessionCanonicalTicket.CreateIssue(Organization, Owner, Epoch);
        ticket.Properties.IssuedUtc = Issued.AddMinutes(-5);
        ticket.Properties.ExpiresUtc = Issued.AddDays(2);
        ServerSessionCanonicalTicket.ValidateIssue(ticket, envelope);
        Assert.True(envelope.VerifiedDeadlineUtc == Issued.AddMinutes(90),
            "Provisional dates changed the trusted deadline.");
        Action<AuthenticationTicket>[] invalid =
        [
            value => value.Properties.Items["private-ticks"] = "synthetic",
            value => value.Properties.Parameters["arbitrary"] = new object(),
            value => value.Properties.StoreTokens([new AuthenticationToken { Name = "access_token", Value = "synthetic" }]),
            value => value.Properties.IsPersistent = true,
            value => value.Properties.AllowRefresh = true,
            value => value.Properties.RedirectUri = "/unexpected",
            value => value.Principal.Identities.Single().AddClaim(new(ClaimTypes.Email, "synthetic@example.invalid")),
            value => value.Principal.Identities.Single().BootstrapContext = "synthetic",
            value => value.Principal.Identities.Single().Actor = new ClaimsIdentity()
        ];
        foreach (var mutate in invalid)
        {
            var candidate = ServerSessionCanonicalTicket.CreateIssue(Organization, Owner, Epoch);
            mutate(candidate);
            Failure(ServerSessionIdentityFailureKind.UnsupportedFormat,
                () => ServerSessionCanonicalTicket.ValidateIssue(candidate, envelope));
        }
        var ordinary = new AuthenticationTicket(ticket.Principal, ticket.Properties, ServerSessionSchemes.Session);
        Failure(ServerSessionIdentityFailureKind.UntrustedIssuance,
            () => ServerSessionCanonicalTicket.ValidateIssue(ordinary, envelope));
        var original = ServerSessionCanonicalTicket.CreateIssue(Organization, Owner, Epoch);
        var refresh = original.Properties.Items.Single();
        var alternateKey = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [refresh.Key.ToUpperInvariant()] = refresh.Value
        };
        var alternateProperties = new AuthenticationProperties(alternateKey);
        var alternate = new AuthenticationTicket(original.Principal, alternateProperties, ServerSessionSchemes.Issue);
        Failure(ServerSessionIdentityFailureKind.UnsupportedFormat,
            () => ServerSessionCanonicalTicket.ValidateIssue(alternate, envelope));
    }

    [Fact]
    public void ClaimIssuerPropertiesDuplicateAndNoncanonicalUuidChangesAreRefused()
    {
        var envelope = Envelope();
        var canonical = ServerSessionCanonicalTicket.CreateIssue(Organization, Owner, Epoch);
        var original = canonical.Principal.Identities.Single();
        Claim[] replacements =
        [
            new(ClaimTypes.NameIdentifier, Owner.Value.ToString("D").ToUpperInvariant(), ClaimValueTypes.String,
                ServerSessionSchemes.Session, ServerSessionSchemes.Session),
            new(ClaimTypes.NameIdentifier, Owner.Value.ToString("D"), ClaimValueTypes.String, "foreign-issuer"),
            new(ClaimTypes.NameIdentifier, Owner.Value.ToString("D"), ClaimValueTypes.Integer,
                ServerSessionSchemes.Session, ServerSessionSchemes.Session)
        ];
        foreach (var replacement in replacements)
        {
            var claims = original.Claims.Where(claim => claim.Type != ClaimTypes.NameIdentifier).Append(replacement);
            var identity = new ClaimsIdentity(claims, ServerSessionSchemes.Session, ClaimTypes.NameIdentifier, ClaimTypes.Role);
            var candidate = new AuthenticationTicket(new ClaimsPrincipal(identity),
                new AuthenticationProperties { IsPersistent = false, AllowRefresh = false }, ServerSessionSchemes.Issue);
            Failure(ServerSessionIdentityFailureKind.UnsupportedFormat,
                () => ServerSessionCanonicalTicket.ValidateIssue(candidate, envelope));
        }
        var withClaimProperty = ServerSessionCanonicalTicket.CreateIssue(Organization, Owner, Epoch);
        withClaimProperty.Principal.Claims.First().Properties["private"] = "synthetic";
        Failure(ServerSessionIdentityFailureKind.UnsupportedFormat,
            () => ServerSessionCanonicalTicket.ValidateIssue(withClaimProperty, envelope));
        var duplicate = ServerSessionCanonicalTicket.CreateIssue(Organization, Owner, Epoch);
        duplicate.Principal.Identities.Single().AddClaim(duplicate.Principal.Claims.First());
        Failure(ServerSessionIdentityFailureKind.UnsupportedFormat,
            () => ServerSessionCanonicalTicket.ValidateIssue(duplicate, envelope));
    }

    [Fact]
    public void PreCanceledCryptoOperationsRemainCancellationWithoutProtectionWork()
    {
        using var fixture = new SessionCodecTestProfile(Organization);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => fixture.Codec.Protect(Envelope(), canceled.Token));
        var blob = fixture.Codec.Protect(Envelope(), CancellationToken.None);
        try
        {
            Assert.Throws<OperationCanceledException>(() => fixture.Codec.Unprotect(ParseHandle(), Current(blob), canceled.Token));
            Assert.Throws<OperationCanceledException>(() => fixture.Codec.ValidateCreated(ParseHandle(), Envelope(),
                blob, Facts(blob), canceled.Token));
        }
        finally { CryptographicOperations.ZeroMemory(blob); }
    }

    private static ServerSessionEnvelope Envelope(DateTimeOffset? deadline = null) =>
        new(new(Organization, ParseHandle().Digest), Binding, 7, Owner, Epoch, deadline ?? Issued.AddMinutes(90));

    private static ServerSessionFacts Facts(byte[] blob, long version = 1, ServerSessionLifetime? lifetime = null) =>
        new(new(Organization, ParseHandle().Digest), Binding, 7, Epoch,
            lifetime ?? new(Issued, Issued.AddMinutes(20), Issued.AddMinutes(60)), version, blob);

    private static CurrentServerSession Current(byte[] blob, PrincipalId? principal = null) => Observe(Facts(blob), principal);

    private static CurrentServerSession Observe(ServerSessionFacts facts, PrincipalId? principal = null) =>
        ServerSessionPolicy.ObserveCurrent(facts.Key, facts.Lifetime.IssuedAtUtc, facts,
            new(facts.BindingId, facts.BindingVersion, principal ?? Owner, true, true))
            ?? throw new InvalidOperationException("The synthetic live observation was refused.");

    private static ServerSessionHandle ParseHandle()
    {
        if (!ServerSessionHandle.TryParse(VectorKey, out var handle))
            throw new InvalidOperationException("The fixed handle vector was refused.");
        return handle;
    }

    private static void Failure(ServerSessionIdentityFailureKind kind, Action action)
    {
        var error = Assert.Throws<ServerSessionIdentityException>(action);
        Assert.True(error.Kind == kind && error.InnerException is null &&
            string.Equals(error.Message, "The server session operation failed.", StringComparison.Ordinal) &&
            string.Equals(error.ToString(), nameof(ServerSessionIdentityException), StringComparison.Ordinal),
            "The closed failure kind or redacted diagnostics changed.");
    }
}

/// <summary>Caller-owned real certificate, filesystem and standard DP fixture; no production test hook.</summary>
internal sealed class SessionCodecTestProfile : IDisposable
{
    private readonly List<ServiceProvider> rawProviders = [];
    private readonly string root;
    private readonly Guid organization;

    internal SessionCodecTestProfile(Guid organization, bool bootstrap = true)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("The durable session fixture requires Linux.");
        this.organization = organization;
        root = Path.Combine(Path.GetTempPath(), "braid-session-codec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        KeyDirectory = Path.Combine(root, "keys");
        Directory.CreateDirectory(KeyDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            Certificate = NewCertificate();
            Application = "Braid.Session.CodecTests." + Guid.NewGuid().ToString("N");
            Options = new(organization, Application, KeyDirectory);
            if (bootstrap)
            {
                var now = DateTimeOffset.UtcNow;
                now = new(now.Ticks - now.Ticks % 10, TimeSpan.Zero);
                Profile = DurableSessionKeyProfile.BootstrapEmpty(Options, Certificate, [], now.AddDays(-1), now.AddDays(30));
                Codec = new(Profile);
            }
        }
        catch
        {
            Profile?.Dispose();
            Certificate?.Dispose();
            Directory.Delete(root, recursive: true);
            throw;
        }
    }

    internal string Root => root;
    internal string KeyDirectory { get; }
    internal string Application { get; }
    internal X509Certificate2 Certificate { get; }
    internal ServerSessionKeyProfileOptions Options { get; }
    internal DurableSessionKeyProfile Profile { get; } = null!;
    internal ServerSessionTicketCodec Codec { get; } = null!;

    internal byte[] ProtectRaw(byte[] plaintext, string purpose = "Braid.ServerSessionTicket") =>
        NewProtector(purpose).Protect(plaintext);
    internal byte[] UnprotectRaw(byte[] ciphertext) => NewProtector("Braid.ServerSessionTicket").Unprotect(ciphertext);

    internal ServiceProvider NewStandardProvider(X509Certificate2 current,
        params X509Certificate2[] overlap)
    {
        var services = new ServiceCollection();
        services.AddDataProtection().SetApplicationName(Application).PersistKeysToFileSystem(new(KeyDirectory))
            .ProtectKeysWithCertificate(current).UnprotectKeysWithAnyCertificate([current, .. overlap]);
        var provider = services.BuildServiceProvider();
        rawProviders.Add(provider);
        return provider;
    }

    internal static X509Certificate2 NewCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Braid Session Test", rsa, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(180));
    }

    public void Dispose()
    {
        Profile?.Dispose();
        foreach (var provider in rawProviders)
            provider.Dispose();
        Certificate.Dispose();
        Directory.Delete(root, recursive: true);
    }

    private IDataProtector NewProtector(string purpose) =>
        NewStandardProvider(Certificate).GetRequiredService<IDataProtectionProvider>().CreateProtector(purpose,
            "codec:1", "policy:1", "configured-organization:" + organization.ToString("D"));
}
