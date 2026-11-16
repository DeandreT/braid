using System.Globalization;
using Microsoft.AspNetCore.Http;

namespace Braid.Api.Catalog;

internal sealed class CatalogRevisionReportPreparedResponse : IResult
{
    private readonly byte[] _bytes;
    private readonly string? _location;
    private readonly int? _retryAfter;

    private CatalogRevisionReportPreparedResponse(int statusCode, string? location, int? retryAfter, byte[] owned)
    {
        StatusCode = statusCode;
        _location = location;
        _retryAfter = retryAfter;
        _bytes = owned;
    }

    internal int StatusCode { get; }
    internal int EncodedByteCount => _bytes.Length;

    internal static CatalogRevisionReportPreparedResponse Create(int statusCode, string? location, int? retryAfter,
        ReadOnlyMemory<byte> bytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (bytes.Length is < 1 or > 16777216)
            throw new InvalidOperationException("The prepared report bytes exceed the fixed response ceiling.");
        var owned = new byte[bytes.Length];
        for (var offset = 0; offset < owned.Length; offset += 4096)
        {
            token.ThrowIfCancellationRequested();
            var count = Math.Min(4096, owned.Length - offset);
            bytes.Span.Slice(offset, count).CopyTo(owned.AsSpan(offset, count));
        }
        token.ThrowIfCancellationRequested();
        return new(statusCode, location, retryAfter, owned);
    }

    public async Task ExecuteAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var token = context.RequestAborted;
        try
        {
            token.ThrowIfCancellationRequested();
            context.Response.StatusCode = StatusCode;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength = _bytes.Length;
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.Remove("ETag");
            context.Response.Headers.Remove("Location");
            context.Response.Headers.Remove("Retry-After");
            if (_location is not null) context.Response.Headers.Location = _location;
            if (_retryAfter is { } seconds) context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
            token.ThrowIfCancellationRequested();
            await context.Response.Body.WriteAsync(_bytes.AsMemory(), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            context.Abort();
            throw;
        }
    }
}
