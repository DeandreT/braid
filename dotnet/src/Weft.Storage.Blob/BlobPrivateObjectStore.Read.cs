using Weft.Application;
using Weft.Domain;

namespace Weft.Storage.Blob;

public sealed partial class BlobPrivateObjectStore
{
    /// <summary>
    /// One consistent download of the exact final name. Readers take no gate: a retirement either
    /// precedes the read (Missing) or follows it, and a download never mixes two versions.
    /// </summary>
    public async Task<byte[]> ReadVerifiedAsync(RevisionScope expectedScope, PrivateObjectReference reference,
        CancellationToken cancellationToken = default)
    {
        ValidateReference(expectedScope, reference);
        using var operation = await AcquireOperationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (bytes, _) = await ReadFinalAsync(reference, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return bytes;
        }
        catch (Exception error) { throw Normalize(error, cancellationToken); }
    }
}
