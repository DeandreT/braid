using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Braid.Api.Chat;

internal static partial class ChatRequestParser
{
    internal static readonly TimeSpan EmptyBodyReadTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Proves decoded EOF, including trailers, before a cancellation can act.</summary>
    internal static async Task RequireNoBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        var transfer = request.Headers.TransferEncoding;
        var encoding = request.Headers.ContentEncoding;
        // Kestrel preserves a conflicting Content-Length as X-Content-Length
        // when Transfer-Encoding takes precedence. Refuse that ambiguity too.
        if (request.ContentLength is > 0 || request.Headers.ContentType.Count != 0 ||
            request.Headers.ContainsKey("Trailer") || request.Headers.ContainsKey("X-Content-Length") ||
            transfer.Count != 0 && (transfer.Count != 1 || !string.Equals(transfer[0]?.Trim(' ', '\t'), "chunked", StringComparison.OrdinalIgnoreCase)) ||
            encoding.Count != 0 && (encoding.Count != 1 || !string.Equals(encoding[0]?.Trim(' ', '\t'), "identity", StringComparison.OrdinalIgnoreCase)))
            throw InvalidBody();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(EmptyBodyReadTimeout);
        try
        {
            // Never infer empty from Content-Length: the server's decoder must reach
            // EOF. A one-byte probe also bounds memory and refuses any actual content.
            if (await request.Body.ReadAsync(new byte[1], timeout.Token) != 0) throw InvalidBody();
            timeout.Token.ThrowIfCancellationRequested();
            var trailers = request.HttpContext.Features.Get<IHttpRequestTrailersFeature>();
            if (trailers is not null)
            {
                if (trailers.Available)
                {
                    if (trailers.Trailers.Count != 0) throw InvalidBody();
                }
                // Kestrel does not expose trailers for a message it classified as
                // bodyless. Require that server feature, not a Content-Length guess.
                else if (transfer.Count != 0 || request.HttpContext.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody != false)
                    throw InvalidBody();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw InvalidBody(); }
        catch (Exception error) when (error is BadHttpRequestException or IOException) { throw InvalidBody(); }

        ChatRequestException InvalidBody()
        {
            // An invalid HTTP/1 message is not a reusable pipeline. HTTP/2/3 have
            // independent streams and must not receive the Connection header.
            if (request.Protocol is "HTTP/1.0" or "HTTP/1.1")
                request.HttpContext.Response.Headers.Connection = "close";
            return ChatRequestException.Invalid();
        }
    }
}
