using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Weft.Application;
using Weft.Deployment;

namespace Weft.MachineEndpoint;

/// <summary>
/// What one call needs: the configured target, the admission bound, the token validator, the invocation service and
/// the clock. <paramref name="Work"/> bounds validation, the body, the ledger and execution; <paramref name="Deadline"/>
/// also bounds the response, so an admitted call holds its slot for at most the deadline. <paramref name="RuntimeAvailable"/>
/// reports whether the compiled runtime still admits work; a closed runner fails readiness until restart.
/// </summary>
internal sealed record MappingEndpointCalls(ReleaseTargetReference Target, int ConcurrencyLimit, IMachineTokenValidator Validator,
    MachineInvocationService Service, TimeProvider Time, TimeSpan Work, TimeSpan Deadline, Func<bool> RuntimeAvailable);

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
    /// <summary>Of the rest: the bound on releasing a canceled call's attempt, whose lease expires if the release is lost.</summary>
    internal static readonly TimeSpan Release = TimeSpan.FromSeconds(1);
    /// <summary>Of the rest: kept for writing the response.</summary>
    internal static readonly TimeSpan ResponseReserve = TimeSpan.FromMilliseconds(500);
    /// <summary>Of the rest: what a canceled execution may spend settling and closing its channel.</summary>
    internal static TimeSpan Settlement(TimeSpan work, TimeSpan deadline) => deadline - work - Release - ResponseReserve;
    private static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = 8 };
    /// <summary>Every status an invocation answers besides 200, each with <see cref="MappingInvocationErrorV1"/>.</summary>
    internal static readonly int[] Refusals = [.. Enum.GetValues<MappingInvocationOutcomeV1>().Select(Status).Distinct().Order()];
    private readonly SemaphoreSlim slots = new(calls.ConcurrencyLimit, calls.ConcurrencyLimit);
    private Dictionary<string, string[]>? mapped;
    private int ready;

    internal void MarkReady() => Volatile.Write(ref ready, 1);
    internal void MarkStopping() => Volatile.Write(ref ready, 0);

    /// <summary>
    /// The routes and their answers, which <c>contracts/weft-endpoint-v1.json</c> describes. Handlers find the routes
    /// among the request's services, so the document-only host maps these same registrations without them.
    /// </summary>
    internal static void Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        // No Accepts metadata: routing would answer a media type before saturation, the token and the request ID.
        var invoke = endpoints.MapPost(InvocationsPath, Handler(static (routes, context) => routes.InvokeAsync(context)))
            .WithName("InvokeRelease").WithTags("Invocations")
            .Produces<MappingInvocationResponseV1>(StatusCodes.Status200OK, MappingEndpointContract.MediaType);
        foreach (var status in Refusals) invoke.Produces<MappingInvocationErrorV1>(status, MappingEndpointContract.MediaType);
        endpoints.MapGet(LivePath, Handler(static (_, context) => Status(context, StatusCodes.Status204NoContent)))
            .WithName("GetLiveness").WithTags("Health").Produces(StatusCodes.Status204NoContent);
        endpoints.MapGet(ReadyPath, Handler(static (routes, context) => routes.ReadyAsync(context)))
            .WithName("GetReadiness").WithTags("Health")
            .Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status503ServiceUnavailable);
    }

    private static Delegate Handler(Func<MappingEndpointRoutes, HttpContext, Task> handle) =>
        (HttpContext context) => handle(context.RequestServices.GetRequiredService<MappingEndpointRoutes>(), context);

    /// <summary>
    /// Runs before routing; every answer is no-store and nosniff. Only a mapped route's exact path and method, without a
    /// query or path base, reaches routing: any other path answers 404, and another method on a mapped path 405 with
    /// <c>Allow</c>. Routing alone would also match case and trailing-slash variants.
    /// </summary>
    internal Task GateAsync(HttpContext context, RequestDelegate next)
    {
        var request = context.Request;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        var routes = LazyInitializer.EnsureInitialized(ref mapped, () => Mapped(context.RequestServices.GetRequiredService<EndpointDataSource>()));
        if (request.QueryString.HasValue || request.PathBase.HasValue || request.Path.Value is not { } path ||
            !routes.TryGetValue(path, out var methods))
            return Status(context, StatusCodes.Status404NotFound);
        if (!methods.Any(method => HttpMethods.Equals(method, request.Method)))
        {
            context.Response.Headers.Allow = string.Join(", ", methods);
            return Status(context, StatusCodes.Status405MethodNotAllowed);
        }
        return next(context);
    }

    /// <summary>Each mapped route's exact path and its methods.</summary>
    private static Dictionary<string, string[]> Mapped(EndpointDataSource source) =>
        source.Endpoints.OfType<RouteEndpoint>()
            .GroupBy(endpoint => endpoint.RoutePattern.RawText ?? throw new InvalidOperationException("A route needs its path."), StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => group.SelectMany(endpoint => endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? []).ToArray(),
                StringComparer.Ordinal);

    internal Task ReadyAsync(HttpContext context) =>
        Status(context, Volatile.Read(ref ready) == 1 && calls.RuntimeAvailable() ? StatusCodes.Status204NoContent : StatusCodes.Status503ServiceUnavailable);

    internal async Task InvokeAsync(HttpContext context)
    {
        using var call = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        call.CancelAfter(calls.Deadline);
        // Saturation refuses before any token, body or database work.
        if (Volatile.Read(ref ready) != 1 || !slots.Wait(0))
        { await Outcome(context, null, MappingInvocationOutcomeV1.Busy, call.Token, retryAfter: 1); return; }
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
            { await Outcome(context, null, MappingInvocationOutcomeV1.Unavailable, call, retryAfter: 5); return; }
            catch (MachineTokenException) { await Unauthorized(context, call); return; }
            // The credential bounds the call: once it expires, nothing is read, claimed, run or delivered for it.
            var remaining = caller.ExpiresAt - calls.Time.GetUtcNow();
            if (remaining <= TimeSpan.Zero) { await Unauthorized(context, call); return; }
            if (remaining < calls.Deadline) credential.CancelAfter(remaining);
            if (headers[RequestIdHeader].Count != 1 || !Guid.TryParseExact(headers[RequestIdHeader][0], "D", out var id) ||
                id == Guid.Empty || headers[RequestIdHeader][0] != id.ToString("D"))
            { await Outcome(context, null, MappingInvocationOutcomeV1.InvalidRequestId, call); return; }
            requestId = id;
            if (context.Request.ContentType is not ("application/json" or "application/json; charset=utf-8") ||
                headers.ContentEncoding.Count != 0)
            { await Outcome(context, id, MappingInvocationOutcomeV1.UnsupportedMediaType, call); return; }
            var (body, refusal) = await ReadBodyAsync(context, bounded.Token);
            if (body is null)
            { await Outcome(context, id, refusal == 413 ? MappingInvocationOutcomeV1.RequestTooLarge : MappingInvocationOutcomeV1.InvalidRequest, call); return; }
            MachineInvocationRequest invocation;
            try { invocation = Parse(body); }
            catch (Exception error) when (error is JsonException or FormatException or ArgumentException or InvalidOperationException)
            { await Outcome(context, id, MappingInvocationOutcomeV1.InvalidRequest, call); return; }
            if (Expired(caller, credential)) { await Unauthorized(context, call); return; }
            MachineInvocationResult result;
            try { result = await calls.Service.InvokeAsync(calls.Target, caller, id, invocation, bounded.Token); }
            catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
            { await Outcome(context, id, MappingInvocationOutcomeV1.Unavailable, call, retryAfter: 2); return; }
            // A recorded outcome is not delivered on an expired credential; a retry with a valid one replays it.
            if (Expired(caller, credential)) { await Unauthorized(context, call); return; }
            await DeliverAsync(context, id, result, delivery.Token);
        }
        catch (OperationCanceledException) when (bounded.IsCancellationRequested && !context.RequestAborted.IsCancellationRequested)
        {
            if (context.Response.HasStarted) { context.Abort(); return; }
            if (caller is not null && Expired(caller, credential)) await Unauthorized(context, call);
            else await Outcome(context, requestId, MappingInvocationOutcomeV1.Deadline, call, retryAfter: 1);
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
            case MachineInvocationStatus.Failed: await Outcome(context, requestId, Name(result.Outcome!.Value), call); return;
            case MachineInvocationStatus.Expired: await Outcome(context, requestId, MappingInvocationOutcomeV1.Expired, call); return;
            case MachineInvocationStatus.Conflict: await Outcome(context, requestId, MappingInvocationOutcomeV1.Conflict, call); return;
            case MachineInvocationStatus.InProgress: await Outcome(context, requestId, MappingInvocationOutcomeV1.InProgress, call, retryAfter: 5); return;
            case MachineInvocationStatus.Refused: await Outcome(context, requestId, MappingInvocationOutcomeV1.Refused, call); return;
            case MachineInvocationStatus.NoRelease: await Outcome(context, requestId, MappingInvocationOutcomeV1.NoRelease, call, retryAfter: 30); return;
            default: await Outcome(context, requestId, result.Outcome is { } outcome ? Name(outcome) : MappingInvocationOutcomeV1.Unavailable, call, retryAfter: 2); return;
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
        Object(root, "inputs", "parameters", "schema");
        var documentProfile = root.TryGetProperty("schema", out var schema);
        if (documentProfile && (schema.ValueKind != JsonValueKind.String || schema.GetString() != "weft.document-invocation-request.v1")) throw new FormatException();
        if (!root.TryGetProperty("inputs", out var inputs) || inputs.ValueKind != JsonValueKind.Array || inputs.GetArrayLength() is < 1 or > PreviewPolicy.MaximumInputDocuments)
            throw new FormatException();
        var documents = new List<PreviewInputDocument>();
        var media = new List<MachineInputMedia>();
        foreach (var input in inputs.EnumerateArray())
        {
            Object(input, documentProfile ? ["name", "content", "mediaType", "csvDialect"] : ["name", "content"]);
            string? name = null;
            if (input.TryGetProperty("name", out var named) && named.ValueKind != JsonValueKind.Null)
                name = named.ValueKind == JsonValueKind.String ? named.GetString() : throw new FormatException();
            if ((documents.Count == 0) != (name is null)) throw new FormatException();
            if (!input.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String) throw new FormatException();
            var bytes = Convert.FromBase64String(content.GetString()!);
            var dialect = documentProfile ? BuildCsvDialect.ReadOptional(input) : null;
            var mediaType = documentProfile ? Text(input, "mediaType") : PreviewPolicy.MediaType;
            if (CsvDocumentPolicy.AdmitsMediaType(mediaType) ? !CsvDocumentPolicy.Admits(dialect, mediaType) : dialect is not null)
                throw new FormatException();
            if (bytes.Length == 0 && dialect is null) throw new FormatException();
            documents.Add(new(name, bytes, dialect));
            if (documentProfile) media.Add(new(name, mediaType, dialect));
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
        return new(documents[0], documents.Skip(1).ToArray(), parameters, documentProfile ? media : null);
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

    /// <summary>A recorded outcome on the wire. A completed outcome is delivered as the response, never as a refusal.</summary>
    private static MappingInvocationOutcomeV1 Name(MachineInvocationOutcome outcome) => outcome switch
    {
        MachineInvocationOutcome.InvalidInput => MappingInvocationOutcomeV1.InvalidInput,
        MachineInvocationOutcome.InvalidParameter => MappingInvocationOutcomeV1.InvalidParameter,
        MachineInvocationOutcome.ExecutionFailed => MappingInvocationOutcomeV1.ExecutionFailed,
        MachineInvocationOutcome.ResourceLimit => MappingInvocationOutcomeV1.ResourceLimit,
        MachineInvocationOutcome.IntegrityFailure => MappingInvocationOutcomeV1.IntegrityFailure,
        MachineInvocationOutcome.Unsupported => MappingInvocationOutcomeV1.Unsupported,
        MachineInvocationOutcome.Interrupted => MappingInvocationOutcomeV1.Interrupted,
        MachineInvocationOutcome.AttemptsExhausted => MappingInvocationOutcomeV1.AttemptsExhausted,
        MachineInvocationOutcome.Busy => MappingInvocationOutcomeV1.Busy,
        MachineInvocationOutcome.ReleaseChanged => MappingInvocationOutcomeV1.ReleaseChanged,
        MachineInvocationOutcome.Refused => MappingInvocationOutcomeV1.Refused,
        _ => MappingInvocationOutcomeV1.Unavailable
    };

    private static Task Unauthorized(HttpContext context, CancellationToken call)
    {
        context.Response.Headers.WWWAuthenticate = "Bearer";
        return Outcome(context, null, MappingInvocationOutcomeV1.Unauthorized, call);
    }

    private static Task Status(HttpContext context, int status)
    {
        context.Response.StatusCode = status;
        context.Response.ContentLength = 0;
        return Task.CompletedTask;
    }

    /// <summary>The one status of each outcome; the OpenAPI document lists every status's outcomes from it.</summary>
    internal static int Status(MappingInvocationOutcomeV1 outcome) => outcome switch
    {
        MappingInvocationOutcomeV1.InvalidRequestId or MappingInvocationOutcomeV1.InvalidRequest => StatusCodes.Status400BadRequest,
        MappingInvocationOutcomeV1.Unauthorized => StatusCodes.Status401Unauthorized,
        MappingInvocationOutcomeV1.Refused => StatusCodes.Status403Forbidden,
        MappingInvocationOutcomeV1.Conflict or MappingInvocationOutcomeV1.InProgress => StatusCodes.Status409Conflict,
        MappingInvocationOutcomeV1.Expired => StatusCodes.Status410Gone,
        MappingInvocationOutcomeV1.RequestTooLarge => StatusCodes.Status413PayloadTooLarge,
        MappingInvocationOutcomeV1.UnsupportedMediaType => StatusCodes.Status415UnsupportedMediaType,
        MappingInvocationOutcomeV1.InvalidInput or MappingInvocationOutcomeV1.InvalidParameter or MappingInvocationOutcomeV1.ExecutionFailed or
            MappingInvocationOutcomeV1.ResourceLimit or MappingInvocationOutcomeV1.Unsupported => StatusCodes.Status422UnprocessableEntity,
        MappingInvocationOutcomeV1.IntegrityFailure or MappingInvocationOutcomeV1.Interrupted or MappingInvocationOutcomeV1.AttemptsExhausted =>
            StatusCodes.Status500InternalServerError,
        MappingInvocationOutcomeV1.Busy or MappingInvocationOutcomeV1.Unavailable or MappingInvocationOutcomeV1.ReleaseChanged or
            MappingInvocationOutcomeV1.Deadline or MappingInvocationOutcomeV1.NoRelease => StatusCodes.Status503ServiceUnavailable,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome))
    };

    private static Task Outcome(HttpContext context, Guid? requestId, MappingInvocationOutcomeV1 outcome, CancellationToken call, int? retryAfter = null)
    {
        context.Response.StatusCode = Status(outcome);
        if (retryAfter is { } seconds) context.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return WriteAsync(context, JsonSerializer.SerializeToUtf8Bytes(new MappingInvocationErrorV1(requestId, outcome), MappingEndpointContract.Json), call);
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
