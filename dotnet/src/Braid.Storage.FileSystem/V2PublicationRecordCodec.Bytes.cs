using System.Buffers.Binary;
using Braid.Domain;

namespace Braid.Storage.FileSystem;

internal static partial class V2PublicationRecordCodec
{
    private ref struct RecordWriter
    {
        private readonly byte[] _buffer;
        private int _position;

        public RecordWriter() => _buffer = new byte[MaximumRecordBytes];

        private Span<byte> Next(int count)
        {
            if (count < 0 || count > _buffer.Length - _position)
                throw Unsafe();
            var span = _buffer.AsSpan(_position, count);
            _position += count;
            return span;
        }

        internal void Bytes(ReadOnlySpan<byte> bytes) => bytes.CopyTo(Next(bytes.Length));
        internal void Byte(byte value) => Next(1)[0] = value;
        internal void UInt16(ushort value) => BinaryPrimitives.WriteUInt16BigEndian(Next(2), value);
        internal void UInt32(uint value) => BinaryPrimitives.WriteUInt32BigEndian(Next(4), value);
        internal void UInt64(ulong value) => BinaryPrimitives.WriteUInt64BigEndian(Next(8), value);
        internal void Int64(long value) => BinaryPrimitives.WriteInt64BigEndian(Next(8), value);
        internal void Time(DateTimeOffset value) => Int64(value.Ticks);

        internal void Id(Guid value)
        {
            if (!value.TryWriteBytes(Next(16), bigEndian: true, out var count) || count != 16)
                throw Unsafe();
        }

        internal void Digest(Sha256Digest digest) => Bytes(Convert.FromHexString(digest.Value));
        internal void Identity(PrivatePublicationInodeIdentity value)
        {
            UInt64(value.Inode);
            UInt32(value.DeviceMajor);
            UInt32(value.DeviceMinor);
            UInt64(value.MountId);
        }

        internal byte[] Complete()
        {
            BinaryPrimitives.WriteUInt32BigEndian(_buffer.AsSpan(13, 4), (uint)_position);
            return _buffer.AsSpan(0, _position).ToArray();
        }
    }

    private ref struct RecordReader(ReadOnlySpan<byte> bytes)
    {
        private readonly ReadOnlySpan<byte> _bytes = bytes;
        private int _position;

        internal bool IsAtEnd => _position == _bytes.Length;
        internal ReadOnlySpan<byte> Bytes(int count)
        {
            if (count < 0 || count > _bytes.Length - _position)
                throw Unsafe();
            var value = _bytes.Slice(_position, count);
            _position += count;
            return value;
        }

        internal byte Byte() => Bytes(1)[0];
        internal ushort UInt16() => BinaryPrimitives.ReadUInt16BigEndian(Bytes(2));
        internal uint UInt32() => BinaryPrimitives.ReadUInt32BigEndian(Bytes(4));
        internal ulong UInt64() => BinaryPrimitives.ReadUInt64BigEndian(Bytes(8));
        internal long Int64() => BinaryPrimitives.ReadInt64BigEndian(Bytes(8));
        internal Guid Id() => V2RecordValue.Id(new Guid(Bytes(16), bigEndian: true));
        internal Sha256Digest Digest() => Sha256Digest.Parse(Convert.ToHexStringLower(Bytes(32)));
        internal DateTimeOffset Time() => new(Int64(), TimeSpan.Zero);
        internal PrivatePublicationInodeIdentity Identity() => new(UInt64(), UInt32(), UInt32(), UInt64());
    }
}
