using System.Buffers.Binary;
using System.Text;
using Weft.Application;
using Weft.Domain;
using Weft.Storage.FileSystem;

namespace Weft.Tests;

/// <summary>Independent format vectors and authored metadata only; no filesystem or database evidence.</summary>
public sealed class PrivateStorageV2RecordTests
{
    // Fixed bytes transcribed from the frozen wire grammar, not obtained from the codec.
    // Common facts: network-order UUID byte runs00..cf, expected count3, generation7,
    // expiry08da00000000007b, unsigned namespace values, six distinct same-mount inodes.
    private const string NativeCommonHex =
        "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f01" +
        "202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f" +
        "404142434445464748494a4b4c4d4e4f505152535455565758595a5b5c5d5e5f" +
        "606162636465666768696a6b6c6d6e6f707172737475767778797a7b7c7d7e7f" +
        "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f0000000000000003" +
        "01808182838485868788898a8b8c8d8e8f909192939495969798999a9b9c9d9e9f" +
        "a0a1a2a3a4a5a6a7a8a9aaabacadaeaf000000000000000708da00000000007b" +
        "b0b1b2b3b4b5b6b7b8b9babbbcbdbebfc0c1c2c3c4c5c6c7c8c9cacbcccdcecf" +
        "fffffffffffffffffedcba9876543210fedcba98" +
        "000000000000000111223344556677880123456789abcdef" +
        "000000000000000211223344556677880123456789abcdef" +
        "000000000000000311223344556677880123456789abcdef" +
        "000000000000000411223344556677880123456789abcdef" +
        "000000000000000511223344556677880123456789abcdef" +
        "000000000000000611223344556677880123456789abcdef";
    private const string PublicationHex = "d0d1d2d3d4d5d6d7d8d9dadbdcdddedf";
    private const string DataIdentityHex = "000000000000000a11223344556677880123456789abcdef";
    private const string DigestHex = "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f";
    private static readonly DateTimeOffset Expiry = new(0x08da00000000007bL, TimeSpan.Zero);

    [Theory]
    [InlineData(1, 446)]
    [InlineData(2, 526)]
    [InlineData(3, 542)]
    [InlineData(4, 590)]
    public void NativeGoldenImagesPreserveNetworkUuidsUnsignedWidthsAndExactDatabaseTicks(int kind, int byteCount)
    {
        var expected = Golden(kind);
        Assert.Equal(byteCount, expected.Length);
        var facts = NativeRecord(kind);
        Assert.Equal(expected, V2PublicationRecordCodec.Encode(facts));
        var decoded = V2PublicationRecordCodec.Decode(expected);
        Assert.Equal(facts, decoded);
        Assert.Equal(expected, V2PublicationRecordCodec.Encode(decoded));
        Assert.Equal(Guid.ParseExact("00010203-0405-0607-0809-0a0b0c0d0e0f", "D"), decoded.Context.RootId);
        Assert.Equal(ulong.MaxValue, decoded.Context.MountNamespaceDevice);
        Assert.Equal(0xfedcba9876543210UL, decoded.Context.MountNamespaceInode);
        Assert.Equal(0xfedcba98U, decoded.Context.EffectiveUserId);
        Assert.Equal(Expiry, decoded.Context.Owner.LeaseExpiresAtUtc);
        Assert.Equal(9, decoded.Context.Owner.LeaseExpiresAtUtc.Ticks % 10);
        Assert.Equal(7, decoded.Context.Owner.Generation);
        Assert.Equal(0x0123456789abcdefUL, decoded.Context.DataParentIdentity.MountId);
    }

    [Fact]
    public void EveryTruncatedGoldenRecordAndEveryTrailingByteRefusesInsteadOfAcceptingAPrefix()
    {
        foreach (var kind in new[] { 1, 2, 3, 4 })
        {
            var expected = Golden(kind);
            for (var length = 0; length < expected.Length; length++)
                AssertUnsafe(expected.AsSpan(0, length).ToArray());
            AssertUnsafe([.. expected, 0]);
            AssertUnsafe([.. expected, 255]);
        }
        AssertUnsafe(new byte[V2PublicationRecordCodec.MaximumRecordBytes + 1]);
    }

    [Theory]
    [InlineData(0, 0)] // magic
    [InlineData(8, 1)] // unknown kind257
    [InlineData(10, 1)] // unknown version257
    [InlineData(48, 3)] // closed object class
    [InlineData(185, 3)] // closed owner kind
    [InlineData(241, 9)] // native generation exceeds8
    [InlineData(334, 1)] // control directory is on another device
    public void HeaderDiscriminatorsAndObservedIdentityDomainsAreClosed(int offset, byte value)
    {
        var malformed = Golden(1);
        malformed[offset] = value;
        AssertUnsafe(malformed);
    }

    [Theory]
    [InlineData(16, 16)] // RootId
    [InlineData(32, 16)] // ControlNamespaceId
    [InlineData(49, 16)] // organization
    [InlineData(113, 16)] // object
    [InlineData(129, 16)] // TempId
    [InlineData(186, 16)] // original actor
    [InlineData(202, 16)] // instance
    [InlineData(218, 16)] // owner token
    [InlineData(234, 8)] // generation
    [InlineData(250, 16)] // native operation
    [InlineData(266, 16)] // BootId
    [InlineData(290, 8)] // namespace inode
    [InlineData(302, 8)] // root inode
    public void RequiredIdsGenerationsAndInodesCannotBecomeZeroOnDecode(int offset, int length)
    {
        var malformed = Golden(1);
        malformed.AsSpan(offset, length).Clear();
        AssertUnsafe(malformed);
    }

    [Fact]
    public void DeclaredLengthSignedCountsAndRepeatedLocationsCannotDisagreeWithTheWireFacts()
    {
        var badLength = Golden(1);
        BinaryPrimitives.WriteUInt32BigEndian(badLength.AsSpan(12, 4), 445);
        AssertUnsafe(badLength);
        BinaryPrimitives.WriteUInt32BigEndian(badLength.AsSpan(12, 4), 447);
        AssertUnsafe(badLength);
        var negative = Golden(1);
        negative.AsSpan(177, 8).Fill(255);
        AssertUnsafe(negative);
        var emptyNative = Golden(1);
        emptyNative.AsSpan(177, 8).Clear();
        AssertUnsafe(emptyNative);
        var overNative = Golden(1);
        BinaryPrimitives.WriteInt64BigEndian(overNative.AsSpan(177, 8), 4194305);
        AssertUnsafe(overNative);
        var duplicatedLocation = Golden(1);
        duplicatedLocation.AsSpan(302, 8).CopyTo(duplicatedLocation.AsSpan(326, 8));
        AssertUnsafe(duplicatedLocation);
    }

    [Fact]
    public void PreparedWireCannotSubstituteTheVerifiedDigestByteCountOrDataIdentity()
    {
        var digest = Golden(4);
        digest[^40] ^= 1;
        AssertUnsafe(digest);
        var count = Golden(4);
        count[^1] = 4;
        AssertUnsafe(count);
        var parentAlias = Golden(4);
        // Prepared suffix starts446: hash32 + predecessor identity24 + publication16 + time8.
        parentAlias.AsSpan(398, 8).CopyTo(parentAlias.AsSpan(526, 8));
        AssertUnsafe(parentAlias);
    }

    [Fact]
    public void CompleteReportOwnerRetainsCanonicalKeyRepairLineageAndActualCandidate()
    {
        var context = ReportContext();
        var expectedKey = context.Owner.ReportClaim!.Key.CopyCanonicalUtf8();
        var bytes = V2PublicationRecordCodec.Encode(new V2AbsenceRecord(context));
        var decoded = V2PublicationRecordCodec.Decode(bytes).Context;
        Assert.Equal(context, decoded);
        Assert.Equal(expectedKey, decoded.Owner.ReportClaim!.Key.CopyCanonicalUtf8());
        Assert.Equal(context.Reference.Id, decoded.Owner.ReportClaim.CandidateObjectId);
        Assert.Equal(context.Owner.ReportClaim.RepairTargetDerivationId, decoded.Owner.ReportClaim.RepairTargetDerivationId);
        Assert.Equal(context.Owner.ReportClaim.StartedAtUtc, decoded.Owner.ReportClaim.StartedAtUtc);
        Assert.Equal(context.Owner.OriginalActor, decoded.Owner.OriginalActor);
        Assert.Equal(context.Owner.LeaseToken, decoded.Owner.LeaseToken);
    }

    [Fact]
    public void ConstructorLimitEscapedReportIdentityFitsTheFrozenKeyAndRecordBudgetsWithoutFingerprintSubstitution()
    {
        var key = ReportKey(maximumText: true);
        var canonical = key.CopyCanonicalUtf8();
        Assert.InRange(canonical.Length, 22000, V2PublicationRecordCodec.MaximumKeyBytes);
        var decoded = V2ReportKeyCodec.Decode(canonical);
        Assert.Equal(key, decoded);
        Assert.Equal(canonical, decoded.CopyCanonicalUtf8());
        Assert.Equal(key.Profile, decoded.Profile);
        Assert.Equal(key.Source, decoded.Source);
        Assert.Equal(key.Policy, decoded.Policy);
        var context = ReportContext(key);
        var record = V2PublicationRecordCodec.Encode(new V2AbsenceRecord(context));
        Assert.InRange(record.Length, canonical.Length, V2PublicationRecordCodec.MaximumRecordBytes);
        Assert.Equal(context, V2PublicationRecordCodec.Decode(record).Context);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("\n")]
    public void ReportKeyDecoderRequiresExactCanonicalBytesEvenForOtherwiseEquivalentJson(string suffix)
    {
        var canonical = ReportKey().CopyCanonicalUtf8();
        var noncanonical = canonical.Concat(Encoding.UTF8.GetBytes(suffix)).ToArray();
        var refusal = Assert.Throws<PrivateObjectStorageException>(() => V2ReportKeyCodec.Decode(noncanonical));
        Assert.Equal(PrivateObjectStorageError.UnsafeStorage, refusal.Error);
    }

    [Fact]
    public void ReportKeyDecoderRejectsDuplicateUnknownMalformedUtf8AndMismatchedFingerprintFields()
    {
        var canonical = Encoding.UTF8.GetString(ReportKey().CopyCanonicalUtf8());
        foreach (var malformed in new[]
        {
            canonical.Insert(1, "\"unknown\":true,"),
            canonical.Insert(1, "\"operation\":\"projection\","),
            canonical.Replace("weft.revision-report-key.v1", "weft.revision-report-key.v2", StringComparison.Ordinal),
            canonical.Replace("\"policyFingerprint\":\"", "\"policyFingerprint\":\"0", StringComparison.Ordinal)
        })
        {
            var refusal = Assert.Throws<PrivateObjectStorageException>(() => V2ReportKeyCodec.Decode(Encoding.UTF8.GetBytes(malformed)));
            Assert.Equal(PrivateObjectStorageError.UnsafeStorage, refusal.Error);
        }
        var badUtf8 = ReportKey().CopyCanonicalUtf8();
        badUtf8[2] = 255;
        var utf8Refusal = Assert.Throws<PrivateObjectStorageException>(() => V2ReportKeyCodec.Decode(badUtf8));
        Assert.Equal(PrivateObjectStorageError.UnsafeStorage, utf8Refusal.Error);
        var oversized = Assert.Throws<PrivateObjectStorageException>(() =>
            V2ReportKeyCodec.Decode(new byte[V2PublicationRecordCodec.MaximumKeyBytes + 1]));
        Assert.Equal(PrivateObjectStorageError.UnsafeStorage, oversized.Error);
    }

    [Fact]
    public void TypedContextRefusesForeignOriginalActorCandidateAndAliasedObservedLocations()
    {
        var native = NativeContext();
        Assert.Throws<ArgumentException>(() => PrivatePublicationOwner.ForIngestion(
            new ActorContext(new PrincipalId(Guid.NewGuid())), native.Owner.IngestionClaim!));
        var report = ReportContext();
        var substituted = new PrivateObjectReference(
            PrivateObjectId.Create(report.Reference.Id.Scope, PrivateObjectClass.DerivedArtifact),
            report.Reference.Digest, report.Reference.ByteCount);
        Assert.Throws<ArgumentException>(() => CopyContext(report, substituted));
        var foreign = new PrivateObjectReference(PrivateObjectId.Restore(
            new RevisionScope(Guid.NewGuid(), native.Reference.Id.Scope.ProjectId,
                native.Reference.Id.Scope.MappingId, native.Reference.Id.Scope.RevisionId),
            native.Reference.Id.ObjectId, PrivateObjectClass.NativeArtifact), native.Reference.Digest, 3);
        Assert.Throws<ArgumentException>(() => CopyContext(native, foreign));
        Assert.Throws<ArgumentException>(() => new V2PublicationContext(native.RootId, native.ControlNamespaceId,
            native.Reference, native.TempId, native.Owner, native.BootId, native.MountNamespaceDevice,
            native.MountNamespaceInode, native.EffectiveUserId, native.RootIdentity, native.ControlIdentity,
            native.ObjectDirectoryIdentity, native.TempDirectoryIdentity, native.DataParentIdentity, native.DataParentIdentity));
        Assert.Throws<ArgumentException>(() => new V2PreparedRecord(native, Sha256Digest.Parse(new string('f', 64)),
            Identity(9), Id("d0d1d2d3d4d5d6d7d8d9dadbdcdddedf"), Expiry.AddTicks(-1), Identity(10),
            Sha256Digest.FromBytes("different"u8), native.Reference.ByteCount));
    }

    private static byte[] Golden(int kind) => Convert.FromHexString(kind switch
    {
        1 => "574546545632520a00010001000001be" + NativeCommonHex,
        2 => "574546545632520a000200010000020e" + NativeCommonHex +
            "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd" +
            "000000000000000711223344556677880123456789abcdef" + PublicationHex + "08da000000000079",
        3 => "574546545632520a000300010000021e" + NativeCommonHex +
            "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee" +
            "000000000000000811223344556677880123456789abcdef" + PublicationHex + DataIdentityHex,
        4 => "574546545632520a000400010000024e" + NativeCommonHex +
            "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff" +
            "000000000000000911223344556677880123456789abcdef" + PublicationHex + "08da00000000007a" +
            DataIdentityHex + DigestHex + "0000000000000003",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    });

    private static V2PublicationRecord NativeRecord(int kind)
    {
        var context = NativeContext();
        var publication = Id(PublicationHex);
        return kind switch
        {
            1 => new V2AbsenceRecord(context),
            2 => new V2ReservationRecord(context, Sha256Digest.Parse(new string('d', 64)), Identity(7), publication, Expiry.AddTicks(-2)),
            3 => new V2BoundRecord(context, Sha256Digest.Parse(new string('e', 64)), Identity(8), publication, Identity(10)),
            4 => new V2PreparedRecord(context, Sha256Digest.Parse(new string('f', 64)), Identity(9), publication,
                Expiry.AddTicks(-1), Identity(10), context.Reference.Digest, 3),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    private static V2PublicationContext NativeContext()
    {
        var scope = new RevisionScope(Id("202122232425262728292a2b2c2d2e2f"), Id("303132333435363738393a3b3c3d3e3f"),
            Id("404142434445464748494a4b4c4d4e4f"), Id("505152535455565758595a5b5c5d5e5f"));
        var reference = new PrivateObjectReference(PrivateObjectId.Restore(scope,
            Id("606162636465666768696a6b6c6d6e6f"), PrivateObjectClass.NativeArtifact), Sha256Digest.Parse(DigestHex), 3);
        var actor = new ActorContext(new PrincipalId(Id("808182838485868788898a8b8c8d8e8f")));
        var claim = new IngestionClaim(new ProjectScope(scope.OrganizationId, scope.ProjectId),
            Id("b0b1b2b3b4b5b6b7b8b9babbbcbdbebf"), actor.PrincipalId,
            Id("909192939495969798999a9b9c9d9e9f"), Id("a0a1a2a3a4a5a6a7a8a9aaabacadaeaf"), 7, Expiry);
        return Context(reference, PrivatePublicationOwner.ForIngestion(actor, claim));
    }

    private static V2PublicationContext ReportContext(RevisionReportKey? key = null)
    {
        key ??= ReportKey();
        var actor = new ActorContext(new PrincipalId(Id("808182838485868788898a8b8c8d8e8f")));
        var candidate = PrivateObjectId.Restore(key.Scope, Id("606162636465666768696a6b6c6d6e6f"), PrivateObjectClass.DerivedArtifact);
        var claim = new RevisionReportClaim(Id("d0d1d2d3d4d5d6d7d8d9dadbdcdddedf"),
            Id("e0e1e2e3e4e5e6e7e8e9eaebecedeeef"), key, actor.PrincipalId,
            Id("909192939495969798999a9b9c9d9e9f"), Id("a0a1a2a3a4a5a6a7a8a9aaabacadaeaf"), 7,
            Expiry.AddSeconds(-60), Expiry, candidate, Id("f0f1f2f3f4f5f6f7f8f9fafbfcfdfeff"));
        return Context(new PrivateObjectReference(candidate, Sha256Digest.Parse(DigestHex), 3),
            PrivatePublicationOwner.ForReport(actor, claim));
    }

    private static RevisionReportKey ReportKey(bool maximumText = false)
    {
        var native = NativeContext().Reference;
        string Text(int count, string ordinary) => maximumText ? new string('\u0001', count) : ordinary;
        var backend = Text(128, "test-backend");
        var source = new RevisionReportSource(native.Id.Scope, native, Text(256, "entry.native.json"),
            Text(256, "weft.dependencies.empty.v1"), backend);
        return new ProjectionCacheKey(source, new RevisionReportBackendProfile(backend, Text(256, "test-engine"),
            Text(2048, "test-worker"), Text(256, "test-adapter")), Text(128, "test.projection.v1"),
            Text(128, "test.algorithm.v1"), Text(128, "test.codec.v1"), new RevisionReportPolicy());
    }

    private static V2PublicationContext CopyContext(V2PublicationContext context, PrivateObjectReference reference) =>
        new(context.RootId, context.ControlNamespaceId, reference, context.TempId, context.Owner, context.BootId,
            context.MountNamespaceDevice, context.MountNamespaceInode, context.EffectiveUserId,
            context.RootIdentity, context.ControlIdentity, context.ObjectDirectoryIdentity, context.TempDirectoryIdentity,
            context.DataParentIdentity, context.TempGateIdentity);

    private static V2PublicationContext Context(PrivateObjectReference reference, PrivatePublicationOwner owner) =>
        new(Id("000102030405060708090a0b0c0d0e0f"), Id("101112131415161718191a1b1c1d1e1f"), reference,
            Id("707172737475767778797a7b7c7d7e7f"), owner, Id("c0c1c2c3c4c5c6c7c8c9cacbcccdcecf"),
            ulong.MaxValue, 0xfedcba9876543210UL, 0xfedcba98U,
            Identity(1), Identity(2), Identity(3), Identity(4), Identity(5), Identity(6));

    private static PrivatePublicationInodeIdentity Identity(ulong inode) => new(inode, 0x11223344U, 0x55667788U, 0x0123456789abcdefUL);
    private static Guid Id(string networkHex) => Guid.ParseExact(networkHex, "N");
    private static void AssertUnsafe(byte[] bytes)
    {
        var refusal = Assert.Throws<PrivateObjectStorageException>(() => V2PublicationRecordCodec.Decode(bytes));
        Assert.Equal(PrivateObjectStorageError.UnsafeStorage, refusal.Error);
    }
}
