using Weft.Domain;

namespace Weft.Application;

public enum SpecCaptureOutcome { Captured = 1, LimitExceeded = 2 }

public sealed class SpecCaptureResult
{
    internal SpecCaptureResult(SpecCapturedSource? source) => Source = source;
    public SpecCaptureOutcome Outcome => Source is null ? SpecCaptureOutcome.LimitExceeded : SpecCaptureOutcome.Captured;
    public SpecCapturedSource? Source { get; }
    public override string ToString() => nameof(SpecCaptureResult);
}

/// <summary>Consumes at most limit + one detection byte. Leaves the caller's stream open; no partial source is returned.</summary>
public static class SpecSourceCapture
{
    public static async Task<SpecCaptureResult> ReadAsync(SpecSourceIdentity identity, SpecSourceFormat format,
        Stream source, int maximumBytes = SpecCapturedSource.MaximumBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(source);
        if (!Enum.IsDefined(format)) throw new ArgumentOutOfRangeException(nameof(format));
        if (maximumBytes is < 1 or > SpecCapturedSource.MaximumBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        using var captured = new MemoryStream();
        var buffer = new byte[Math.Min(8192, maximumBytes + 1)];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = await source.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maximumBytes - (int)captured.Length + 1)), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (count > maximumBytes - captured.Length) return new(null);
            captured.Write(buffer, 0, count);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(new SpecCapturedSource(identity, format, captured.GetBuffer().AsSpan(0, (int)captured.Length)));
    }
}
