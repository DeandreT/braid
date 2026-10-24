using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Net.Http.Headers;
using Weft.Cli.Http;
using Weft.Contracts.Catalog;

namespace Weft.Tests.Cli;

internal sealed record CliStubRequest(string OperationId, IReadOnlyDictionary<string, string> Path, IReadOnlyDictionary<string, string> Query,
    IReadOnlyDictionary<string, string> Headers, byte[] Body)
{
    public JsonNode? Json => Body.Length == 0 ? null : JsonNode.Parse(Body);
}

/// <summary>One scripted response: a JSON contract value, raw bytes, or a streamed body.</summary>
internal sealed class CliStubReply
{
    public int Status { get; init; } = 200;
    public object? Json { get; init; }
    public byte[]? Bytes { get; init; }
    public string ContentType { get; init; } = "application/json; charset=utf-8";
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Func<HttpContext, Task>? Stream { get; init; }

    public static CliStubReply Of(int status, object? json, params (string Name, string Value)[] headers)
    {
        var reply = new CliStubReply { Status = status, Json = json };
        foreach (var (name, value) in headers)
            reply.Headers[name] = value;
        return reply;
    }

    public static CliStubReply Error(int status, CatalogErrorCodeV1 code, params (string Name, string Value)[] headers) =>
        Of(status, new CatalogErrorV1(code), headers);
}

/// <summary>
/// An in-process stand-in for the Weft API built from <c>contracts/weft-v1.json</c>. Every request must match a documented
/// operation, carry a valid delegated bearer token from the owned issuer and no cookie, use only declared query parameters
/// and headers with every required one present, and send the declared media type with a body that conforms to its schema.
/// Every scripted JSON response is checked against the documented response schema too. Any deviation is a violation.
/// </summary>
internal sealed class CliApiStub : IAsyncDisposable
{
    private static readonly HashSet<string> TransportHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Authorization", "Accept", "User-Agent", "Content-Type", "Content-Length", "Transfer-Encoding", "Accept-Encoding",
        "Connection", "Expect", "traceparent", "tracestate", "Request-Id", "Correlation-Context", "baggage"
    };
    private readonly CliOpenApi spec = CliOpenApi.Load();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<CliStubReply>> queued = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Func<CliStubRequest, CliStubReply>> responders = new(StringComparer.Ordinal);
    private readonly CliTestIssuer issuer;
    private WebApplication? app;

    private CliApiStub(CliTestIssuer issuer) => this.issuer = issuer;

    public ConcurrentQueue<string> Violations { get; } = new();
    public ConcurrentQueue<CliStubRequest> Requests { get; } = new();
    public HttpMessageHandler Handler { get; private set; } = null!;
    public string OrganizationId { get; } = Guid.NewGuid().ToString("D");

    public static async Task<CliApiStub> StartAsync(CliTestIssuer issuer)
    {
        var stub = new CliApiStub(issuer);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], EnvironmentName = Environments.Production });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        stub.app = builder.Build();
        stub.app.Run(stub.HandleAsync);
        await stub.app.StartAsync();
        stub.Handler = stub.app.GetTestServer().CreateHandler();
        stub.Reply("GetCatalogContext", _ => CliStubReply.Of(200, new CatalogContextV1("bearer", Guid.NewGuid().ToString("D"),
            stub.OrganizationId, "Stub organization", new CatalogFeaturesV1(true, true, true, true, true, true, true, true, false, true),
            new CatalogOrganizationActionsV1(true), requestToken: null, identityContext: null)));
        return stub;
    }

    /// <summary>Answers every request to the operation.</summary>
    public void Reply(string operationId, Func<CliStubRequest, CliStubReply> responder)
    {
        _ = spec.Operation(operationId);
        responders[operationId] = responder;
    }

    /// <summary>Answers the next requests to the operation in order, ahead of any standing responder.</summary>
    public void Enqueue(string operationId, params CliStubReply[] replies)
    {
        _ = spec.Operation(operationId);
        var queue = queued.GetOrAdd(operationId, _ => new ConcurrentQueue<CliStubReply>());
        foreach (var reply in replies)
            queue.Enqueue(reply);
    }

    public IReadOnlyList<CliStubRequest> Calls(string operationId) => Requests.Where(request => request.OperationId == operationId).ToArray();

    public void AssertClean() => Assert.True(Violations.IsEmpty, "Requests deviated from weft-v1.json:\n" + string.Join("\n", Violations));

    private async Task HandleAsync(HttpContext context)
    {
        var request = context.Request;
        var operation = spec.Match(request.Method, request.Path.Value ?? "", out var path);
        if (operation is null)
        {
            Violations.Enqueue($"{request.Method} {request.Path}: no documented operation");
            context.Response.StatusCode = 404;
            return;
        }
        string Violation(string text)
        {
            var message = $"{operation.Id}: {text}";
            Violations.Enqueue(message);
            return message;
        }
        if (request.Headers.ContainsKey(HeaderNames.Cookie))
            Violation("sent a cookie");
        if (!await AuthorizedAsync(request))
        {
            Violation("missing or invalid delegated bearer token");
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new CatalogErrorV1(CatalogErrorCodeV1.IdentityRejected), WeftApiClient.Json);
            return;
        }
        var declared = operation.Parameters.ToArray();
        foreach (var (name, _) in request.Query)
            if (!declared.Any(parameter => parameter.In == "query" && parameter.Name == name))
                Violation($"undeclared query parameter {name}");
        foreach (var (name, _) in request.Headers)
            if (!TransportHeaders.Contains(name) && !declared.Any(parameter => parameter.In == "header" &&
                string.Equals(parameter.Name, name, StringComparison.OrdinalIgnoreCase)))
                Violation($"undeclared header {name}");
        foreach (var (location, name, required) in declared)
            if (required && !ApiOperations.CookieSessionHeaders.Contains(name) &&
                !(location == "query" ? request.Query.ContainsKey(name) : location != "header" || request.Headers.ContainsKey(name)))
                Violation($"missing required {location} parameter {name}");
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, context.RequestAborted);
        var body = buffer.ToArray();
        var mediaTypes = operation.RequestMediaTypes;
        if (mediaTypes.Count == 0)
        {
            if (body.Length != 0 || request.ContentType is not null)
                Violation("sent a body to an operation that takes none");
        }
        else if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var type) || !mediaTypes.Contains(type.MediaType.Value) ||
            type.Parameters.Any(parameter => parameter.Name != "charset" || !string.Equals(parameter.Value.Value, "utf-8", StringComparison.OrdinalIgnoreCase)))
            Violation($"sent media type {request.ContentType ?? "(none)"}");
        else if (type.MediaType.Value!.EndsWith("json", StringComparison.Ordinal))
        {
            JsonNode? node = null;
            try { node = JsonNode.Parse(body); }
            catch (JsonException) { Violation("sent a body that is not JSON"); }
            if (node is not null)
                foreach (var error in spec.Validate(node, operation.RequestSchema(type.MediaType.Value)!))
                    Violation("request body " + error);
        }
        else if (body.Length == 0)
            Violation("sent an empty body");
        var recorded = new CliStubRequest(operation.Id, path, request.Query.ToDictionary(pair => pair.Key, pair => pair.Value.ToString()),
            request.Headers.Where(pair => !TransportHeaders.Contains(pair.Key) || pair.Key == "Content-Type")
                .ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.OrdinalIgnoreCase), body);
        Requests.Enqueue(recorded);
        var reply = queued.TryGetValue(operation.Id, out var queue) && queue.TryDequeue(out var next) ? next
            : responders.TryGetValue(operation.Id, out var responder) ? responder(recorded)
            : CliStubReply.Error(500, CatalogErrorCodeV1.InternalFailure);
        await WriteAsync(context, operation, reply, Violation);
    }

    private async Task WriteAsync(HttpContext context, CliOpenApiOperation operation, CliStubReply reply, Func<string, string> violation)
    {
        var documented = operation.Response(reply.Status);
        if (documented is null && reply.Status is not (429 or 503 or 500))
            violation($"replied with undocumented status {reply.Status}");
        context.Response.StatusCode = reply.Status;
        foreach (var (name, header) in reply.Headers)
            context.Response.Headers[name] = header;
        if (reply.Stream is { } stream)
        {
            await stream(context);
            return;
        }
        if (reply.Json is { } value)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, value.GetType(), WeftApiClient.Json);
            var schema = documented?["content"]?["application/json"]?["schema"];
            if (schema is not null)
                foreach (var error in spec.Validate(JsonNode.Parse(bytes), schema))
                    violation($"scripted {reply.Status} response " + error);
            context.Response.ContentType = reply.ContentType;
            context.Response.ContentLength = bytes.Length;
            await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
        }
        else if (reply.Bytes is { } raw)
        {
            context.Response.ContentType = reply.ContentType;
            context.Response.ContentLength = raw.Length;
            await context.Response.Body.WriteAsync(raw, context.RequestAborted);
        }
    }

    private async Task<bool> AuthorizedAsync(HttpRequest request)
    {
        var values = request.Headers.Authorization;
        if (values.Count != 1 || values[0] is not { } header || !header.StartsWith("Bearer ", StringComparison.Ordinal))
            return false;
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(header["Bearer ".Length..], new TokenValidationParameters
        {
            ValidIssuer = issuer.Issuer,
            ValidAudience = issuer.Audience.ToString("D"),
            IssuerSigningKey = issuer.PublicKey,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ClockSkew = TimeSpan.Zero,
            RequireExpirationTime = true
        });
        return result.IsValid &&
            result.Claims.TryGetValue("scp", out var scope) && ((string)scope).Split(' ').Contains(issuer.ScopeName) &&
            result.Claims.TryGetValue("azp", out var client) && (string)client == issuer.PublicClient.ToString("D") &&
            result.Claims.TryGetValue("oid", out var objectId) && (string)objectId == issuer.ObjectId.ToString("D");
    }

    public async ValueTask DisposeAsync()
    {
        if (app is not null)
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
