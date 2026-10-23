using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Weft.Application;
using Weft.Deployment;

namespace Weft.MachineEndpoint;

/// <summary>
/// What one call needs: the configured target, the admission bound, the token validator, the invocation service and
/// the clock. <paramref name="Work"/> bounds validation, the body, the ledger and execution; <paramref name="Deadline"/>
/// also bounds the response, so an admitted call holds its slot for at most the deadline.
/// </summary>
internal sealed record MappingEndpointCalls(ReleaseTargetReference Target, int ConcurrencyLimit, IMachineTokenValidator Validator,
    MachineInvocationService Service, TimeProvider Time, TimeSpan Work, TimeSpan Deadline);

/// <summary>
/// The machine-facing HTTP contract: <c>POST /v1/invocations</c> for the configured target, and health probes.
/// There is no browser session, cookie, redirect or default authentication scheme.
/// </summary>
internal sealed class MappingEndpointRoutes(MappingEndpointCalls calls)
{
    internal const string InvocationsPath = "/v1/invocations";
    internal const string LivePath = "/v1/health/live";
    internal const string ReadyPath = "/v1/health/ready";
    internal const string RequestIdHeader = "Weft-Request-Id";
    internal const string ReplayedHeader = "Weft-Invocation-Replayed";
    internal const int MaximumBodyBytes = 2 * 1024 * 1024;
    /// <summary>The whole call: admission, token validation, one compiled execution, the ledger writes and the response.</summary>
    internal static readonly TimeSpan Deadline = TimeSpan.FromSeconds(90);
    /// <summary>Work stops here, leaving the rest of the deadline for the response.</summary>
    internal static readonly TimeSpan Work = TimeSpan.FromSeconds(85);
    private static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = 8 };
    private readonly SemaphoreSlim slots = new(calls.ConcurrencyLimit, calls.ConcurrencyLimit);
    private int ready;

    internal void MarkReady() => Volatile.Write(ref ready, 1);
    internal void MarkStopping() => Volatile.Write(ref ready, 0);

    internal async Task HandleAsync(HttpContext context)
    {
        var request = context.Request;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        if (request.QueryString.HasValue || request.PathBase.HasValue) { await Status(context, 404); return; }
        switch (request.Path.Value)
        {
            case LivePath when HttpMethods.IsGet(request.Method):
                await Status(context, 204); return;
            case ReadyPath when HttpMethods.IsGet(request.Method):
                await Status(context, Volatile.Read(ref ready) == 1 ? 204 : 503); return;
            case InvocationsPath when HttpMethods.IsPost(request.Method):
                break;
            case InvocationsPath or LivePath or ReadyPath:
                context.Response.Headers.Allow = request.Path.Value == InvocationsPath ? "POST" : "GET";
                await Status(context, 405); return;
            default:
                await Status(context, 404); return;
        }
        using var call = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        call.CancelAfter(calls.Deadline);
        // Saturation refuses before any token, body or database work.
        if (Volatile.Read(ref ready) != 1 || !slots.Wait(0)) { await Outcome(context, 503, null, "busy", call.Token, retryAfter: 1); return; }
        try { await InvokeAsync(context, call.Token); }
        finally { slots.Release(); }
    }

    private async Task InvokeAsync(HttpContext context, CancellationToken call)
    {
        using var work = CancellationTokenSource.CreateLinkedTokenSource(call);
        work.CancelAfter(calls.Work);
        // The credential's own bound: once it fires, the call ends as unauthorized whatever the clock reads.
        using var credential = new CancellationTokenSource();
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(work.Token, credential.Token);
        using var delivery = CancellationTokenSource.CreateLinkedTokenSource(call, credential.Token);
        var headers = context.Request.Headers;
        MachineCaller? caller = null;
        Guid? requestId = null;
        try
        {
            if (headers.Authorization.Count != 1 || headers.Authorization[0] is not { Length: > 7 } authorization ||
                !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            { await Unauthorized(context, call); return; }
            try { caller = await calls.Validator.ValidateAsync(authorization[7..], work.Token); }
            catch (MachineTokenException error) when (error.Failure == MachineTokenFailure.Unavailable)
            { await Outcome(context, 503, null, "unavailable", call, retryAfter: 5); return; }
            catch (MachineTokenException) { await Unauthorized(context, call); return; }
            // The credential bounds the call: once it expires, nothing is read, claimed, run or delivered for it.
            var remaining = caller.ExpiresAt - calls.Time.GetUtcNow();
            if (remaining <= TimeSpan.Zero) { await Unauthorized(context, call); return; }
            if (remaining < calls.Deadline) credential.CancelAfter(remaining);
            if (headers[RequestIdHeader].Count != 1 || !Guid.TryParseExact(headers[RequestIdHeader][0], "D", out var id) ||
                id == Guid.Empty || headers[RequestIdHeader][0] != id.ToString("D"))
            { await Outcome(context, 400, null, "invalidRequestId", call); return; }
            requestId = id;
            if (context.Request.ContentType is not ("application/json" or "application/json; charset=utf-8") ||
                headers.ContentEncoding.Count != 0)
            { await Outcome(context, 415, id, "unsupportedMediaType", call); return; }
            var (body, refusal) = await ReadBodyAsync(context, bounded.Token);
            if (body is null) { await Outcome(context, refusal, id, refusal == 413 ? "requestTooLarge" : "invalidRequest", call); return; }
            MachineInvocationRequest invocation;
            try { invocation = Parse(body); }
            catch (Exception error) when (error is JsonException or FormatException or ArgumentException or InvalidOperationException)
            { await Outcome(context, 400, id, "invalidRequest", call); return; }
            if (Expired(caller, credential)) { await Unauthorized(context, call); return; }
            MachineInvocationResult result;
            try { result = await calls.Service.InvokeAsync(calls.Target, caller, id, invocation, bounded.Token); }
            catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
            { await Outcome(context, 503, id, "unavailable", call, retryAfter: 2); return; }
            // A recorded outcome is not delivered on an expired credential; a retry with a valid one replays it.
            if (Expired(caller, credential)) { await Unauthorized(context, call); return; }
            await DeliverAsync(context, id, result, delivery.Token);
        }
        catch (OperationCanceledException) when (bounded.IsCancellationRequested && !context.RequestAborted.IsCancellationRequested)
        {
            if (context.Response.HasStarted) { context.Abort(); return; }
            if (caller is not null && Expired(caller, credential)) await Unauthorized(context, call);
            else await Outcome(context, 503, requestId, "deadline", call, retryAfter: 1);
        }
    }

    private bool Expired(MachineCaller caller, CancellationTokenSource credential) =>
        credential.IsCancellationRequested || caller.ExpiresAt <= calls.Time.GetUtcNow();

    private static async Task DeliverAsync(HttpContext context, Guid requestId, MachineInvocationResult result, CancellationToken call)
    {
        context.Response.Headers[RequestIdHeader] = requestId.ToString("D");
        if (result.Status is MachineInvocationStatus.Completed or MachineInvocationStatus.Failed or MachineInvocationStatus.Expired)
            context.Response.Headers[ReplayedHeader] = result.Replayed ? "true" : "false";
        switch (result.Status)
        {
            case MachineInvocationStatus.Completed:
                context.Response.StatusCode = 200;
                await WriteAsync(context, result.Response!, call);
                return;
            case MachineInvocationStatus.Failed:
                await Outcome(context, result.Outcome switch
                {
                    MachineInvocationOutcome.Refused => 403,
                    MachineInvocationOutcome.IntegrityFailure or MachineInvocationOutcome.Interrupted or MachineInvocationOutcome.AttemptsExhausted => 500,
                    _ => 422
                }, requestId, Name(result.Outcome!.Value), call);
                return;
            case MachineInvocationStatus.Expired: await Outcome(context, 410, requestId, "expired", call); return;
            case MachineInvocationStatus.Conflict: await Outcome(context, 409, requestId, "conflict", call); return;
            case MachineInvocationStatus.InProgress: await Outcome(context, 409, requestId, "inProgress", call, retryAfter: 5); return;
            case MachineInvocationStatus.Refused: await Outcome(context, 403, requestId, "refused", call); return;
            case MachineInvocationStatus.NoRelease: await Outcome(context, 503, requestId, "noRelease", call, retryAfter: 30); return;
            default: await Outcome(context, 503, requestId, result.Outcome is { } outcome ? Name(outcome) : "unavailable", call, retryAfter: 2); return;
        }
    }

    private static async Task<(byte[]? Body, int Refusal)> ReadBodyAsync(HttpContext context, CancellationToken token)
    {
        if (context.Request.ContentLength > MaximumBodyBytes) return (null, 413);
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = MaximumBodyBytes;
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        try
        {
            while ((read = await context.Request.Body.ReadAsync(chunk, token)) > 0)
            {
                if (buffer.Length + read > MaximumBodyBytes) return (null, 413);
                buffer.Write(chunk, 0, read);
            }
        }
        catch (BadHttpRequestException error) { return (null, error.StatusCode == 413 ? 413 : 400); }
        return (buffer.ToArray(), 0);
    }

    /// <summary>
    /// <c>{"inputs":[{"content":base64},{"name":..,"content":base64}...],"parameters":[{"name":..,"kind":..,"value":..}...]}</c>.
    /// The first input is the unnamed primary document; inputs are base64 so the exact bytes are kept.
    /// </summary>
    internal static MachineInvocationRequest Parse(byte[] body)
    {
        using var document = JsonDocument.Parse(body, JsonOptions);
        var root = document.RootElement;
        Object(root, "inputs", "parameters");
        if (!root.TryGetProperty("inputs", out var inputs) || inputs.ValueKind != JsonValueKind.Array || inputs.GetArrayLength() is < 1 or > PreviewPolicy.MaximumInputDocuments)
            throw new FormatException();
        var documents = new List<PreviewInputDocument>();
        foreach (var input in inputs.EnumerateArray())
        {
            Object(input, "name", "content");
            string? name = null;
            if (input.TryGetProperty("name", out var named) && named.ValueKind != JsonValueKind.Null)
                name = named.ValueKind == JsonValueKind.String ? named.GetString() : throw new FormatException();
            if ((documents.Count == 0) != (name is null)) throw new FormatException();
            if (!input.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String) throw new FormatException();
            var bytes = Convert.FromBase64String(content.GetString()!);
            if (bytes.Length == 0) throw new FormatException();
            documents.Add(new(name, bytes));
        }
        var parameters = new List<PreviewParameter>();
        if (root.TryGetProperty("parameters", out var values))
        {
            if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() > PreviewPolicy.MaximumParameters) throw new FormatException();
            foreach (var value in values.EnumerateArray())
            {
                Object(value, "name", "kind", "value");
                parameters.Add(new(Text(value, "name"), Text(value, "kind") switch
                {
                    "string" => PreviewParameterKind.String,
                    "integer" => PreviewParameterKind.Integer,
                    "float" => PreviewParameterKind.Float,
                    "boolean" => PreviewParameterKind.Boolean,
                    _ => throw new FormatException()
                }, Text(value, "value")));
            }
        }
        return new(documents[0], documents.Skip(1).ToArray(), parameters);
    }

    private static void Object(JsonElement element, params string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new FormatException();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!seen.Add(property.Name) || !allowed.Contains(property.Name, StringComparer.Ordinal)) throw new FormatException();
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new FormatException();

    private static string Name(MachineInvocationOutcome outcome) => outcome switch
    {
        MachineInvocationOutcome.InvalidInput => "invalidInput",
        MachineInvocationOutcome.InvalidParameter => "invalidParameter",
        MachineInvocationOutcome.ExecutionFailed => "executionFailed",
        MachineInvocationOutcome.ResourceLimit => "resourceLimit",
        MachineInvocationOutcome.IntegrityFailure => "integrityFailure",
        MachineInvocationOutcome.Unsupported => "unsupported",
        MachineInvocationOutcome.Interrupted => "interrupted",
        MachineInvocationOutcome.AttemptsExhausted => "attemptsExhausted",
        MachineInvocationOutcome.Busy => "busy",
        MachineInvocationOutcome.ReleaseChanged => "releaseChanged",
        MachineInvocationOutcome.Refused => "refused",
        MachineInvocationOutcome.Completed => "completed",
        _ => "unavailable"
    };

    private static Task Unauthorized(HttpContext context, CancellationToken call)
    {
        context.Response.Headers.WWWAuthenticate = "Bearer";
        return Outcome(context, 401, null, "unauthorized", call);
    }

    private static Task Status(HttpContext context, int status)
    {
        context.Response.StatusCode = status;
        context.Response.ContentLength = 0;
        return Task.CompletedTask;
    }

    private static Task Outcome(HttpContext context, int status, Guid? requestId, string outcome, CancellationToken call, int? retryAfter = null)
    {
        context.Response.StatusCode = status;
        if (retryAfter is { } seconds) context.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            if (requestId is { } id) json.WriteString("requestId", id.ToString("D"));
            json.WriteString("outcome", outcome);
            json.WriteEndObject();
        }
        return WriteAsync(context, stream.ToArray(), call);
    }

    /// <summary>The response stays inside the call deadline; a client that does not take it in time loses the connection.</summary>
    private static async Task WriteAsync(HttpContext context, byte[] body, CancellationToken call)
    {
        context.Response.ContentType = "application/json";
        context.Response.ContentLength = body.Length;
        try
        {
            await context.Response.Body.WriteAsync(body, call);
            await context.Response.Body.FlushAsync(call);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException) { context.Abort(); }
    }
}
