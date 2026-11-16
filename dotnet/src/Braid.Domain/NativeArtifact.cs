namespace Braid.Domain;

public sealed class NativeArtifact
{
    private readonly byte[] _nativeBytes;

    private NativeArtifact(RevisionScope scope, byte[] nativeBytes)
    {
        Scope = scope;
        _nativeBytes = nativeBytes;
        Digest = Sha256Digest.FromBytes(nativeBytes);
    }

    public RevisionScope Scope { get; }
    public Sha256Digest Digest { get; }
    public int ByteCount => _nativeBytes.Length;

    public static NativeArtifact Admit(RevisionScope scope, ReadOnlySpan<byte> nativeBytes, int maximumByteCount)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumByteCount);
        if (nativeBytes.IsEmpty)
            throw new ArgumentException("A native artifact cannot be empty.", nameof(nativeBytes));
        if (nativeBytes.Length > maximumByteCount)
            throw new ArgumentException("The native artifact exceeds the maximum byte count.", nameof(nativeBytes));

        return new NativeArtifact(scope, nativeBytes.ToArray());
    }

    public byte[] CopyBytes() => (byte[])_nativeBytes.Clone();
}
