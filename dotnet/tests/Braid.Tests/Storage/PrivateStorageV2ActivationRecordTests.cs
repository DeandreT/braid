using System.Security.Cryptography;
using System.Text;
using Braid.Application;
using Braid.Storage.FileSystem;

namespace Braid.Tests;

/// <summary>Pure fixed-format vectors; authored identities do not establish actual filesystem ownership or activation.</summary>
public sealed class PrivateStorageV2ActivationRecordTests
{
    private const string OriginalMarker = "braid-private-storage-v1\n102132435465768798a9bacbdcedfe0f\n";
    private const string ActiveMarker = "braid-private-storage-v2\n102132435465768798a9bacbdcedfe0f\nffeeddccbbaa99887766554433221100\n";
    private const string ControlHex =
        "42524149445632430a0001102132435465768798a9bacbdcedfe0fffeeddccbbaa998877665544332211000001" +
        "000000000040000000000000010000000001000000008000";

    // Independently transcribed big-endian grammar: 102-byte header, nine24-byte
    // identities, then58/91/69-byte payloads each followed by its literal SHA256.
    // No product encoder, marker builder or format constant generated these bytes.
    private const string GoldenHex =
        "42524149445632410a000100010000027800112233445566778899aabbccddeeff102132435465768798a9bacbdcedfe0f" +
        "ffeeddccbbaa99887766554433221100123456789abcdef0123456789abcdef0fedcba9876543210f0e1d2c3b4a59687fedcba9801" +
        "fffffffffffffff089abcdeffedcba98fedcba9876543210" +
        "fffffffffffffff189abcdeffedcba98fedcba9876543210" +
        "fffffffffffffff289abcdeffedcba98fedcba9876543210" +
        "fffffffffffffff389abcdeffedcba98fedcba9876543210" +
        "fffffffffffffff489abcdeffedcba98fedcba9876543210" +
        "fffffffffffffff589abcdeffedcba98fedcba9876543210" +
        "fffffffffffffff689abcdeffedcba98fedcba9876543210" +
        "fffffffffffffff789abcdeffedcba98fedcba9876543210" +
        "fffffffffffffff889abcdeffedcba98fedcba9876543210" +
        "62726169642d707269766174652d73746f726167652d76310a31303231333234333534363537363837393861396261636264636564666530660a" +
        "f29c40b5dbfb87697d206365f82d7565682e98c25b007b037a9a3ff3c07051e1" +
        "62726169642d707269766174652d73746f726167652d76320a31303231333234333534363537363837393861396261636264636564666530660a" +
        "66666565646463636262616139393838373736363535343433333232313130300a" +
        "20b913809a790bfe029b90775de67ff47da02a45b0c72fee85c44516372f52bd" +
        ControlHex +
        "0bd40c65aa310a468b45196c7f2e531aba6fbf272a2d5c8f87acabe8e376fa13";

    [Fact]
    public void Fixed632ByteReceiptPreservesNetworkUuidsUnsignedIdentitiesAndIndependentMarkerHashes()
    {
        var expected = Golden();
        Assert.Equal(632, expected.Length);
        Assert.Equal("0ddea5d5248243bfb5d445195b03d78a3512e72aa6bbd5604aab579e1a971ba9",
            Convert.ToHexStringLower(SHA256.HashData(expected)));
        Assert.Equal(expected, V2ActivationReceiptCodec.Encode(Facts()));
        var decoded = V2ActivationReceiptCodec.Decode(expected);
        Assert.Equal(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"), decoded.ActivationId);
        Assert.Equal(Guid.Parse("10213243-5465-7687-98a9-bacbdcedfe0f"), decoded.RootId);
        Assert.Equal(Guid.Parse("ffeeddcc-bbaa-9988-7766-554433221100"), decoded.ControlNamespaceId);
        Assert.Equal(Guid.Parse("12345678-9abc-def0-1234-56789abcdef0"), decoded.Incarnation.BootId);
        Assert.Equal(0xfedcba9876543210UL, decoded.Incarnation.NamespaceDevice);
        Assert.Equal(0xf0e1d2c3b4a59687UL, decoded.Incarnation.NamespaceInode);
        Assert.Equal(0xfedcba98U, decoded.EffectiveUserId);
        LinuxFileSystem.Identity[] identities = [decoded.RootIdentity, decoded.ActivationDirectoryIdentity,
            decoded.GateIdentity, decoded.ControlRootIdentity, decoded.ControlNamespaceIdentity,
            decoded.ControlMetaIdentity, decoded.OriginalMarkerIdentity, decoded.ActiveMarkerIdentity, decoded.ReceiptIdentity];
        for (var index = 0; index < identities.Length; index++) Assert.Equal(Identity(index), identities[index]);
        Assert.Equal(Encoding.ASCII.GetBytes(OriginalMarker), decoded.OriginalMarker.ToArray());
        Assert.Equal(Encoding.ASCII.GetBytes(ActiveMarker), decoded.ActiveMarker.ToArray());
        Assert.Equal(Convert.FromHexString(ControlHex), decoded.ControlMeta.ToArray());
        var receipt = decoded.PublicReceipt();
        Assert.Equal(decoded.ActivationId, receipt.ActivationId);
        Assert.Equal(decoded.RootId, receipt.RootId);
        Assert.Equal(decoded.ControlNamespaceId, receipt.ControlNamespaceId);
        Assert.Equal("f29c40b5dbfb87697d206365f82d7565682e98c25b007b037a9a3ff3c07051e1", receipt.OriginalMarkerDigest.Value);
        Assert.Equal("20b913809a790bfe029b90775de67ff47da02a45b0c72fee85c44516372f52bd", receipt.ActiveMarkerDigest.Value);
        Assert.Equal(expected, V2ActivationReceiptCodec.Encode(decoded));
    }

    [Fact]
    public void EveryTruncatedReceiptAndAnyTrailingByteRefuseInsteadOfAcceptingAPrefix()
    {
        var bytes = Golden();
        for (var length = 0; length < 632; length++) AssertUnsafe(bytes.AsSpan(0, length).ToArray());
        AssertUnsafe([.. bytes, 0]);
        AssertUnsafe([.. bytes, 255]);
    }

    [Theory]
    [InlineData(0, 0)] // magic
    [InlineData(10, 2)] // version
    [InlineData(12, 0)] // missing Linux profile
    [InlineData(12, 2)] // unknown profile
    [InlineData(16, 119)] // declared631
    [InlineData(16, 121)] // declared633
    [InlineData(101, 0)] // acknowledgement is absent
    [InlineData(101, 2)] // acknowledgement is a closed true byte
    public void HeaderLengthVersionProfileAndOfflineAcknowledgementAreClosed(int offset, byte value)
    {
        var bytes = Golden();
        bytes[offset] = value;
        AssertUnsafe(bytes);
    }

    [Fact]
    public void RequiredIdentityFactsCannotBeZeroAliasedOrMovedOffTheCapturedMount()
    {
        foreach (var offset in new[] { 17, 33, 49, 65 })
        {
            var bytes = Golden();
            bytes.AsSpan(offset, 16).Clear();
            AssertUnsafe(bytes);
        }
        var namespaceInode = Golden();
        namespaceInode.AsSpan(89, 8).Clear();
        AssertUnsafe(namespaceInode);
        for (var index = 0; index < 9; index++)
        {
            var inodeOffset = 102 + index * 24;
            var noInode = Golden();
            noInode.AsSpan(inodeOffset, 8).Clear();
            AssertUnsafe(noInode);
            var noMount = Golden();
            noMount.AsSpan(inodeOffset + 16, 8).Clear();
            AssertUnsafe(noMount);
            if (index == 0) continue;
            var alias = Golden();
            alias.AsSpan(102, 8).CopyTo(alias.AsSpan(inodeOffset, 8));
            AssertUnsafe(alias);
        }
        var aliasedMarkers = Golden();
        aliasedMarkers.AsSpan(246, 8).CopyTo(aliasedMarkers.AsSpan(270, 8));
        AssertUnsafe(aliasedMarkers);
        // The ninth identity is the receipt itself. Validating only the eight
        // referenced paths would permit these independent self-identity defects.
        foreach (var offset in new[] { 302, 306, 310 })
        {
            var selfOnAnotherDeviceOrMount = Golden();
            selfOnAnotherDeviceOrMount[offset] ^= 1;
            AssertUnsafe(selfOnAnotherDeviceOrMount);
        }
    }

    [Theory]
    [InlineData(318, 318, 58, false)] // original marker bytes
    [InlineData(376, 318, 58, false)] // original marker hash
    [InlineData(408, 408, 91, false)] // active marker bytes
    [InlineData(499, 408, 91, false)] // active marker hash
    [InlineData(531, 531, 69, false)] // control metadata bytes
    [InlineData(600, 531, 69, false)] // control metadata hash
    [InlineData(318, 318, 58, true)] // original marker grammar, with a truthful replacement hash
    [InlineData(343, 318, 58, true)] // original marker root disagrees with header
    [InlineData(408, 408, 91, true)] // active marker grammar
    [InlineData(433, 408, 91, true)] // active marker root
    [InlineData(497, 408, 91, true)] // active marker control namespace, another valid UUID
    [InlineData(541, 531, 69, true)] // control format version
    [InlineData(542, 531, 69, true)] // control root
    [InlineData(558, 531, 69, true)] // control namespace
    [InlineData(575, 531, 69, true)] // control Linux profile
    [InlineData(583, 531, 69, true)] // control native byte ceiling
    public void MarkerAndControlIntegrityRequiresBothHashesAndExactRepeatedGrammar(
        int changedOffset, int payloadStart, int payloadLength, bool repairHash)
    {
        var bytes = Golden();
        bytes[changedOffset] ^= 1;
        if (repairHash)
            SHA256.HashData(bytes.AsSpan(payloadStart, payloadLength)).CopyTo(bytes.AsSpan(payloadStart + payloadLength, 32));
        AssertUnsafe(bytes);
    }

    [Fact]
    public void RecordConstructionAndDecodingOwnTheirAcceptedMarkerAndControlBytes()
    {
        var original = Encoding.ASCII.GetBytes(OriginalMarker);
        var active = Encoding.ASCII.GetBytes(ActiveMarker);
        var control = Convert.FromHexString(ControlHex);
        var record = Facts(original, active, control);
        Array.Clear(original);
        Array.Clear(active);
        Array.Clear(control);
        Assert.Equal(Golden(), V2ActivationReceiptCodec.Encode(record));
        var input = Golden();
        var decoded = V2ActivationReceiptCodec.Decode(input);
        Array.Clear(input);
        Assert.Equal(Golden(), V2ActivationReceiptCodec.Encode(decoded));
    }

    private static V2ActivationRecord Facts(byte[]? original = null, byte[]? active = null, byte[]? control = null) =>
        new(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"), Guid.Parse("10213243-5465-7687-98a9-bacbdcedfe0f"),
            Guid.Parse("ffeeddcc-bbaa-9988-7766-554433221100"),
            new(Guid.Parse("12345678-9abc-def0-1234-56789abcdef0"), 0xfedcba9876543210UL, 0xf0e1d2c3b4a59687UL),
            0xfedcba98U, Identity(0), Identity(1), Identity(2), Identity(3), Identity(4), Identity(5),
            Identity(6), Identity(7), Identity(8), original ?? Encoding.ASCII.GetBytes(OriginalMarker),
            active ?? Encoding.ASCII.GetBytes(ActiveMarker), control ?? Convert.FromHexString(ControlHex));

    private static LinuxFileSystem.Identity Identity(int index) =>
        new(0xfffffffffffffff0UL + (ulong)index, 0x89abcdefU, 0xfedcba98U, 0xfedcba9876543210UL);
    private static byte[] Golden() => Convert.FromHexString(GoldenHex);
    private static void AssertUnsafe(byte[] bytes) =>
        Assert.Equal(PrivateObjectStorageError.UnsafeStorage,
            Assert.Throws<PrivateObjectStorageException>(() => V2ActivationReceiptCodec.Decode(bytes)).Error);
}
