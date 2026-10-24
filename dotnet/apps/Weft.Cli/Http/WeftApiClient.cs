using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using Weft.Cli.Authentication;
using Weft.Contracts.Catalog;
using Weft.Contracts.Chat;
using Weft.Contracts.Projections;

namespace Weft.Cli.Http;

/// <summary>
/// Sends documented operations as the signed-in user: bearer authorization only, no cookies and no redirects.
/// Rate-limited and busy responses are retried after the server's <c>Retry-After</c> (bounded), transport failures
/// with backoff, and an expired token once after a forced refresh. Every attempt replays the same request.
/// </summary>
internal sealed class WeftApiClient : IDisposable
{
    internal const int MaximumAttempts = 5;
    internal static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(120);
    private static readonly string UserAgent = "weft-cli/" +
        (typeof(WeftApiClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0");

    private readonly HttpClient _http;
    private readonly Uri _server;
    private readonly IAccessTokenSource _tokens;
    private readonly TimeProvider _time;
    private readonly TextWriter _notices;
    private readonly TimeSpan _requestTimeout;

    public WeftApiClient(Uri server, IAccessTokenSource tokens, HttpMessageHandler? handler, TimeProvider time, TextWriter notices,
        TimeSpan? requestTimeout = null)
    {
        _server = server;
        _tokens = tokens;
        _time = time;
        _notices = notices;
        _requestTimeout = requestTimeout ?? RequestTimeout;
        if (_requestTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        _http = handler is null
            ? new HttpClient(new SocketsHttpHandler
            {
                UseCookies = false,
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None,
                ConnectTimeout = TimeSpan.FromSeconds(30)
            })
            : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    internal static JsonSerializerOptions Json { get; } = CreateJson();

    private static JsonSerializerOptions CreateJson()
    {
        var options = ProjectionJson.CreateOptions();
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    public async Task<T> ReadAsync<T>(ApiRequest request, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(request, cancellationToken);
        await response.RequireSuccessAsync(cancellationToken);
        return await response.ReadJsonAsync<T>(cancellationToken);
    }

    /// <summary>Sends with retries. The caller owns the response and decides how to treat its status.</summary>
    public async Task<ApiResponse> SendAsync(ApiRequest request, CancellationToken cancellationToken, bool streaming = false)
    {
        var refreshed = false;
        var forceRefresh = false;
        for (var attempt = 1; ; attempt++)
        {
            var token = await _tokens.GetAccessTokenAsync(forceRefresh, cancellationToken);
            forceRefresh = false;
            using var message = Build(request, token);
            CancellationTokenSource? timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_requestTimeout);
            HttpResponseMessage? response = null;
            TimeSpan delay;
            string reason;
            try
            {
                response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                var status = (int)response.StatusCode;
                if (status == 401 && !refreshed)
                {
                    refreshed = forceRefresh = true;
                    attempt--;
                    continue;
                }
                if (attempt < MaximumAttempts && await RetryableAsync(response, request.Operation, timeout.Token) is { } advised)
                {
                    delay = advised;
                    reason = status == 429 ? "The server is rate limiting requests" : "The server is busy";
                }
                else
                {
                    // Only a successful SSE body is unbounded. Its headers and any error body retain a deadline.
                    if (streaming && request.Operation.ResponseMediaType == "text/event-stream" &&
                        request.Operation.Success.ContainsKey(status) && response.Content.Headers.ContentType?.MediaType == "text/event-stream")
                        timeout.CancelAfter(Timeout.InfiniteTimeSpan);
                    var owned = new ApiResponse(response, request.Operation, timeout);
                    response = null;
                    timeout = null;
                    return owned;
                }
            }
            catch (Exception error) when (error is HttpRequestException or IOException ||
                error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                if (attempt >= MaximumAttempts)
                    throw new CliException(CliExitCode.Transport, "The Weft server could not be reached.", "transport");
                delay = Backoff(attempt);
                reason = "The Weft server could not be reached";
            }
            finally
            {
                response?.Dispose();
                timeout?.Dispose();
            }
            await WaitAsync(delay, reason, cancellationToken);
        }
    }

    private HttpRequestMessage Build(ApiRequest request, string token)
    {
        var message = new HttpRequestMessage(new HttpMethod(request.Operation.Method), new Uri(_server, request.Target()));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(request.Operation.ResponseMediaType));
        message.Headers.UserAgent.ParseAdd(UserAgent);
        foreach (var (name, value) in request.Headers)
            message.Headers.TryAddWithoutValidation(name, value);
        if (request.Content is { } content)
            message.Content = content();
        else if (request.Operation.Method != "GET")
            // An explicit zero length: Weft refuses unsafe requests whose empty body is not proven.
            message.Content = new ByteArrayContent([]);
        if (message.Content is { } body && request.Operation.RequestMediaType is { } mediaType)
            body.Headers.ContentType = MediaTypeHeaderValue.Parse(mediaType);
        return message;
    }

    /// <summary>A bounded delay for 429 and busy 503 responses, or null when the response is final.</summary>
    private async Task<TimeSpan?> RetryableAsync(HttpResponseMessage response, ApiOperation operation, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        if (status == 429)
            return RetryAfter(response) ?? TimeSpan.FromSeconds(2);
        if (status != 503)
            return null;
        if (RetryAfter(response) is { } advised)
            return advised;
        // Only a declared busy category is retried; other 503 categories describe the outcome itself.
        // Ingestion failures can carry a bounded outcome document instead of the small error union.
        await response.Content.LoadIntoBufferAsync(1 << 20, cancellationToken);
        var code = await ApiResponse.ErrorCodeAsync(response, operation, cancellationToken);
        return code is "ingestionAdmissionBusy" or "projectCreationAdmissionBusy" or "revisionReportAdmissionBusy"
            ? TimeSpan.FromSeconds(2)
            : null;
    }

    internal TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
            return Clamp(delta);
        if (header?.Date is { } date)
            return Clamp(date - _time.GetUtcNow());
        return null;
    }

    private static TimeSpan Clamp(TimeSpan delay) =>
        delay < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : delay > MaximumRetryDelay ? MaximumRetryDelay : delay;

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Min(16, 1 << (attempt - 1)));

    private async Task WaitAsync(TimeSpan delay, string reason, CancellationToken cancellationToken)
    {
        await _notices.WriteLineAsync($"{reason}; retrying in {Math.Ceiling(delay.TotalSeconds).ToString(CultureInfo.InvariantCulture)} s.");
        await Task.Delay(delay, _time, cancellationToken);
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>A response whose failures map to stable exit codes. Response bodies are never printed.</summary>
internal sealed class ApiResponse(HttpResponseMessage response, ApiOperation operation, CancellationTokenSource? lifetime = null) : IDisposable
{
    private int disposed;
    public HttpResponseMessage Message => response;
    public int Status => (int)response.StatusCode;
    public bool IsSuccess => operation.Success.ContainsKey(Status);

    /// <summary>The response owns its request deadline until disposal, including every body read.</summary>
    public async Task<Stream> ReadStreamAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime?.Token ?? CancellationToken.None);
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(linked.Token);
            return new ResponseBodyStream(stream, lifetime?.Token ?? CancellationToken.None, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw BodyTimeout();
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new CliException(CliExitCode.Transport, "The connection to the Weft server failed while opening the response body.", "transport");
        }
    }

    public async Task<byte[]> ReadBytesAsync(int maximumBytes, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime?.Token ?? CancellationToken.None);
        try
        {
            await response.Content.LoadIntoBufferAsync(maximumBytes, linked.Token);
            return await response.Content.ReadAsByteArrayAsync(linked.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw BodyTimeout();
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (linked.IsCancellationRequested)
                throw BodyTimeout();
            throw new CliException(CliExitCode.Transport, "The server's response could not be read within its body limit.", "transport");
        }
    }

    public async Task RequireSuccessAsync(CancellationToken cancellationToken)
    {
        if (!IsSuccess)
            throw await FailureAsync(cancellationToken);
    }

    public async Task<T> ReadJsonAsync<T>(CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentType?.MediaType is not "application/json")
            throw Invalid();
        try
        {
            await using var stream = await ReadStreamAsync(cancellationToken);
            return await JsonSerializer.DeserializeAsync<T>(stream, WeftApiClient.Json, cancellationToken) ?? throw Invalid();
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            throw Invalid();
        }
        catch (Exception error) when (error is HttpRequestException or IOException && !cancellationToken.IsCancellationRequested)
        {
            throw new CliException(CliExitCode.Transport, "The connection to the Weft server failed while reading the response.", "transport");
        }
    }

    /// <summary>Reads a JSON body of a non-success status, for operations whose failures carry an outcome document.</summary>
    public Task<T> ReadOutcomeAsync<T>(CancellationToken cancellationToken) => ReadJsonAsync<T>(cancellationToken);

    public async Task<CliException> FailureAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime?.Token ?? CancellationToken.None);
        try
        {
            var code = await ErrorCodeAsync(response, operation, linked.Token);
            return Failure(Status, code, RetryAfterSeconds(response));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw BodyTimeout();
        }
    }

    internal static CliException Failure(int status, string? code, int? retryAfter = null)
    {
        var exit = status switch
        {
            400 or 413 or 415 or 422 => CliExitCode.InvalidRequest,
            401 => CliExitCode.SignInFailed,
            // A valid token whose identity the server does not admit (for example no enabled binding).
            403 when code == "identityRejected" => CliExitCode.SignInFailed,
            403 => CliExitCode.PermissionDenied,
            404 when code == "unavailable" => CliExitCode.Unavailable,
            404 => CliExitCode.NotFound,
            409 => CliExitCode.Conflict,
            412 or 428 => CliExitCode.PreconditionFailed,
            429 => CliExitCode.RateLimited,
            503 => CliExitCode.Unavailable,
            >= 500 and < 600 => CliExitCode.ServerFailure,
            _ => CliExitCode.Transport
        };
        var category = code ?? exit switch
        {
            CliExitCode.NotFound => "notFound",
            CliExitCode.SignInFailed => "identityRejected",
            CliExitCode.Transport => "unexpectedStatus",
            _ => "unspecified"
        };
        var message = exit switch
        {
            CliExitCode.NotFound => "Not found, or you do not have access to it",
            CliExitCode.SignInFailed => "The server refused the signed-in identity",
            CliExitCode.PermissionDenied => "Permission denied",
            CliExitCode.Conflict => "The request conflicts with the resource's current state",
            CliExitCode.PreconditionFailed => "The resource changed since it was read",
            CliExitCode.InvalidRequest => "The server refused the request as invalid",
            CliExitCode.RateLimited => "The server is rate limiting requests",
            CliExitCode.Unavailable => "The feature is disabled or temporarily unavailable",
            CliExitCode.ServerFailure => "The server failed to process the request",
            _ => "The server returned an unexpected response"
        };
        return new CliException(exit, $"{message} ({category}, HTTP {status.ToString(CultureInfo.InvariantCulture)}).", category)
        {
            RetryAfterSeconds = retryAfter
        };
    }

    /// <summary>The closed error category of a Weft error body, or null. Unknown values are never echoed.</summary>
    internal static async Task<string?> ErrorCodeAsync(HttpResponseMessage response, ApiOperation operation, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentType?.MediaType is not "application/json" || response.Content.Headers.ContentLength is > 4096)
            return null;
        try
        {
            await response.Content.LoadIntoBufferAsync(4096, cancellationToken);
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length > 4096)
                return null;
            if (operation.ErrorBody == typeof(ChatErrorV1))
                return JsonSerializer.Deserialize<ChatErrorV1>(bytes, WeftApiClient.Json) is { } chat ? Name(chat.Code) : null;
            return JsonSerializer.Deserialize<CatalogErrorV1>(bytes, WeftApiClient.Json) is { } catalog ? Name(catalog.Code) : null;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or HttpRequestException or IOException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }

    private static string Name<T>(T code) where T : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(code.ToString());

    private static int? RetryAfterSeconds(HttpResponseMessage response) =>
        response.Headers.RetryAfter?.Delta is { } delta ? (int)Math.Ceiling(delta.TotalSeconds) : null;

    private static CliException Invalid() =>
        new(CliExitCode.Transport, "The server's response was not a valid Weft response.", "invalidResponse");

    internal static CliException BodyTimeout() => new(CliExitCode.Transport, "The Weft server response timed out.", "transport");

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        try { lifetime?.Cancel(); }
        finally
        {
            try { response.Dispose(); }
            finally { lifetime?.Dispose(); }
        }
    }
}
