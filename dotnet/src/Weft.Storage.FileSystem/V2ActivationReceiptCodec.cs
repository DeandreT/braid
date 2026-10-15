using System.Buffers.Binary;
using Weft.Application;
using Weft.Domain;

namespace Weft.Storage.FileSystem;

internal sealed record V2ActivationRecord
{
    internal V2ActivationRecord(Guid activationId, Guid rootId, Guid controlNamespaceId,
        LinuxFileSystem.Incarnation incarnation, uint effectiveUserId,
        LinuxFileSystem.Identity rootIdentity, LinuxFileSystem.Identity activationDirectoryIdentity,
        LinuxFileSystem.Identity gateIdentity, LinuxFileSystem.Identity controlRootIdentity,
        LinuxFileSystem.Identity controlNamespaceIdentity, LinuxFileSystem.Identity controlMetaIdentity,
        LinuxFileSystem.Identity originalMarkerIdentity, LinuxFileSystem.Identity activeMarkerIdentity,
        LinuxFileSystem.Identity receiptIdentity, byte[] originalMarker, byte[] activeMarker, byte[] controlMeta)
    {
        if (activationId == Guid.Empty || rootId == Guid.Empty || controlNamespaceId == Guid.Empty ||
            incarnation.BootId == Guid.Empty || incarnation.NamespaceInode == 0 ||
            FileSystemV2PrivateObjectStore.ActivationOriginalRootId(originalMarker) != rootId ||
            !activeMarker.AsSpan().SequenceEqual(FileSystemV2PrivateObjectStore.ActivationMarkerBytes(rootId, controlNamespaceId)) ||
            !controlMeta.AsSpan().SequenceEqual(FileSystemV2PrivateObjectStore.ActivationControlBytes(rootId, controlNamespaceId)))
            throw Unsafe();
        LinuxFileSystem.Identity[] identities = [rootIdentity, activationDirectoryIdentity, gateIdentity,
            controlRootIdentity, controlNamespaceIdentity, controlMetaIdentity, originalMarkerIdentity,
            activeMarkerIdentity, receiptIdentity];
        for (var i = 0; i < identities.Length; i++)
        {
            var id = identities[i];
            if (id.Inode == 0 || id.MountId == 0 || id.DeviceMajor != rootIdentity.DeviceMajor ||
                id.DeviceMinor != rootIdentity.DeviceMinor || id.MountId != rootIdentity.MountId)
                throw Unsafe();
            for (var j = 0; j < i; j++) if (identities[j].Inode == id.Inode) throw Unsafe();
        }
        ActivationId = activationId; RootId = rootId; ControlNamespaceId = controlNamespaceId;
        Incarnation = incarnation; EffectiveUserId = effectiveUserId; RootIdentity = rootIdentity;
        ActivationDirectoryIdentity = activationDirectoryIdentity; GateIdentity = gateIdentity;
        ControlRootIdentity = controlRootIdentity; ControlNamespaceIdentity = controlNamespaceIdentity;
        ControlMetaIdentity = controlMetaIdentity; OriginalMarkerIdentity = originalMarkerIdentity;
        ActiveMarkerIdentity = activeMarkerIdentity; ReceiptIdentity = receiptIdentity;
        OriginalMarker = originalMarker.ToArray(); ActiveMarker = activeMarker.ToArray(); ControlMeta = controlMeta.ToArray();
    }
    internal Guid ActivationId { get; }
    internal Guid RootId { get; }
    internal Guid ControlNamespaceId { get; }
    internal LinuxFileSystem.Incarnation Incarnation { get; }
    internal uint EffectiveUserId { get; }
    internal LinuxFileSystem.Identity RootIdentity { get; }
    internal LinuxFileSystem.Identity ActivationDirectoryIdentity { get; }
    internal LinuxFileSystem.Identity GateIdentity { get; }
    internal LinuxFileSystem.Identity ControlRootIdentity { get; }
    internal LinuxFileSystem.Identity ControlNamespaceIdentity { get; }
    internal LinuxFileSystem.Identity ControlMetaIdentity { get; }
    internal LinuxFileSystem.Identity OriginalMarkerIdentity { get; }
    internal LinuxFileSystem.Identity ActiveMarkerIdentity { get; }
    internal LinuxFileSystem.Identity ReceiptIdentity { get; }
    internal ReadOnlyMemory<byte> OriginalMarker { get; }
    internal ReadOnlyMemory<byte> ActiveMarker { get; }
    internal ReadOnlyMemory<byte> ControlMeta { get; }
    internal PrivateStorageV2ActivationReceipt PublicReceipt() => new(ActivationId, RootId, ControlNamespaceId,
        Sha256Digest.FromBytes(OriginalMarker.Span), Sha256Digest.FromBytes(ActiveMarker.Span));
    private static PrivateObjectStorageException Unsafe() => new(PrivateObjectStorageError.UnsafeStorage);
}

internal static class V2ActivationReceiptCodec
{
    internal const int Size = 628;
    internal static byte[] Encode(V2ActivationRecord value)
    {
        var output = new byte[Size];
        var writer = new Writer(output);
        writer.Bytes("WEFTV2A\n"u8); writer.U16(1); writer.U16(1); writer.U32(Size);
        writer.Id(value.ActivationId); writer.Id(value.RootId); writer.Id(value.ControlNamespaceId);
        writer.Id(value.Incarnation.BootId); writer.U64(value.Incarnation.NamespaceDevice);
        writer.U64(value.Incarnation.NamespaceInode); writer.U32(value.EffectiveUserId); writer.Byte(1);
        writer.Identity(value.RootIdentity); writer.Identity(value.ActivationDirectoryIdentity); writer.Identity(value.GateIdentity);
        writer.Identity(value.ControlRootIdentity); writer.Identity(value.ControlNamespaceIdentity); writer.Identity(value.ControlMetaIdentity);
        writer.Identity(value.OriginalMarkerIdentity); writer.Identity(value.ActiveMarkerIdentity); writer.Identity(value.ReceiptIdentity);
        writer.Payload(value.OriginalMarker.Span); writer.Payload(value.ActiveMarker.Span); writer.Payload(value.ControlMeta.Span);
        if (writer.Offset != Size) throw Unsafe();
        return output;
    }
    internal static V2ActivationRecord Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size) throw Unsafe();
        try
        {
            var r = new Reader(bytes);
            if (!r.Take(8).SequenceEqual("WEFTV2A\n"u8) || r.U16() != 1 || r.U16() != 1 || r.U32() != Size) throw Unsafe();
            var activation = r.Id(); var root = r.Id(); var control = r.Id();
            var incarnation = new LinuxFileSystem.Incarnation(r.Id(), r.U64(), r.U64());
            var user = r.U32(); if (r.Byte() != 1) throw Unsafe();
            var rootIdentity = r.Identity(); var activationIdentity = r.Identity(); var gate = r.Identity();
            var controlRoot = r.Identity(); var controlNamespace = r.Identity(); var meta = r.Identity();
            var oldMarker = r.Identity(); var newMarker = r.Identity(); var self = r.Identity();
            var oldBytes = r.Payload(57); var newBytes = r.Payload(90); var metaBytes = r.Payload(68);
            if (r.Offset != Size) throw Unsafe();
            return new(activation, root, control, incarnation, user, rootIdentity, activationIdentity, gate,
                controlRoot, controlNamespace, meta, oldMarker, newMarker, self, oldBytes, newBytes, metaBytes);
        }
        catch (Exception error) when (error is ArgumentException or OverflowException or FormatException)
        { throw Unsafe(); }
    }
    private ref struct Writer
    {
        private Span<byte> bytes;
        internal int Offset;
        internal Writer(Span<byte> value) { bytes = value; Offset = 0; }
        internal void Bytes(ReadOnlySpan<byte> value) { value.CopyTo(bytes[Offset..]); Offset += value.Length; }
        internal void Byte(byte value) => bytes[Offset++] = value;
        internal void U16(ushort value) { BinaryPrimitives.WriteUInt16BigEndian(bytes[Offset..], value); Offset += 2; }
        internal void U32(uint value) { BinaryPrimitives.WriteUInt32BigEndian(bytes[Offset..], value); Offset += 4; }
        internal void U64(ulong value) { BinaryPrimitives.WriteUInt64BigEndian(bytes[Offset..], value); Offset += 8; }
        internal void Id(Guid value) { value.TryWriteBytes(bytes.Slice(Offset, 16), true, out _); Offset += 16; }
        internal void Identity(LinuxFileSystem.Identity id) { U64(id.Inode); U32(id.DeviceMajor); U32(id.DeviceMinor); U64(id.MountId); }
        internal void Payload(ReadOnlySpan<byte> value) { Bytes(value); Bytes(Convert.FromHexString(Sha256Digest.FromBytes(value).Value)); }
    }
    private ref struct Reader
    {
        private ReadOnlySpan<byte> bytes;
        internal int Offset;
        internal Reader(ReadOnlySpan<byte> value) { bytes = value; Offset = 0; }
        internal ReadOnlySpan<byte> Take(int count) { var result = bytes.Slice(Offset, count); Offset += count; return result; }
        internal byte Byte() => Take(1)[0];
        internal ushort U16() => BinaryPrimitives.ReadUInt16BigEndian(Take(2));
        internal uint U32() => BinaryPrimitives.ReadUInt32BigEndian(Take(4));
        internal ulong U64() => BinaryPrimitives.ReadUInt64BigEndian(Take(8));
        internal Guid Id() => new(Take(16), bigEndian: true);
        internal LinuxFileSystem.Identity Identity() => new(U64(), U32(), U32(), U64());
        internal byte[] Payload(int count)
        {
            var value = Take(count).ToArray();
            if (!Take(32).SequenceEqual(Convert.FromHexString(Sha256Digest.FromBytes(value).Value))) throw Unsafe();
            return value;
        }
    }
    private static PrivateObjectStorageException Unsafe() => new(PrivateObjectStorageError.UnsafeStorage);
}
