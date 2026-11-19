using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi;
using Braid.Application;

namespace Braid.MachineEndpoint;

/// <summary>
/// The OpenAPI description of the machine-facing contract. The build writes it to <c>contracts/braid-endpoint-v1.json</c>
/// from <see cref="MappingEndpointRoutes.Map"/> and the wire types; this class adds what route metadata cannot carry:
/// headers, the request body, each status's outcomes and the client-credentials scheme.
/// </summary>
internal static class MappingEndpointOpenApi
{
    internal const string DocumentName = "v1";
    internal const string MachineToken = "machineToken";
    internal const string Scope = "{audience}/.default";

    /// <summary>
    /// True only inside build-time document generation, whose tool is then the entry assembly. No setting, argument or
    /// environment value selects the document-only host.
    /// </summary>
    internal static bool Generating => Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider";

    /// <summary>
    /// The document-only host: the endpoint's route registrations and the OpenAPI document. It has no listener,
    /// settings, credentials, database or runtime, so it answers nothing.
    /// </summary>
    internal static IHost BuildDocumentHost() => new HostBuilder()
        .UseContentRoot(AppContext.BaseDirectory)
        .ConfigureHostConfiguration(configuration =>
            configuration.AddInMemoryCollection([new(HostDefaults.ApplicationKey, "braid-mapping-endpoint")]))
        .ConfigureWebHost(web => web
            .ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddSingleton<IServer, NoListener>();
                services.ConfigureHttpJsonOptions(options => MappingEndpointContract.Configure(options.SerializerOptions));
                services.AddOpenApi(DocumentName, options =>
                {
                    options.AddSchemaTransformer((schema, context, _) =>
                    {
                        if (context.JsonTypeInfo.Type == typeof(MappingDocumentInvocationInputV1) ||
                            context.JsonTypeInfo.Type == typeof(MappingDocumentInvocationOutputV1))
                            schema.Properties!["mediaType"] = Text("application/json", "application/edi-x12", "application/edifact", "text/csv", "text/tab-separated-values", "application/xml", "text/plain", "application/x-protobuf", "application/xbrl+xml");
                        if (context.JsonTypeInfo.Type == typeof(CsvDocumentDialect))
                        {
                            schema.Properties!["schema"] = Text(CsvDocumentPolicy.DialectSchema);
                            schema.Description = "Resolved saved CSV settings. Delimiter and quote are single ASCII characters; disabled quoting requires null quote. A tab delimiter identifies TSV. All fields are required.";
                            schema.Required = new HashSet<string>(["schema", "delimiter", "quote", "quoteDisabled", "hasHeaderRow", "utf8Bom", "preserveEmptyStrings"]);
                        }
                        return Task.CompletedTask;
                    });
                    options.AddDocumentTransformer((document, _, _) =>
                    {
                        Describe(document);
                        return Task.CompletedTask;
                    });
                    options.AddOperationTransformer(DescribeAsync);
                });
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseEndpoints(MappingEndpointRoutes.Map);
            }))
        .Build();

    private static void Describe(OpenApiDocument document)
    {
        document.Info = new OpenApiInfo
        {
            Title = "Braid mapping endpoint",
            Version = DocumentName,
            Description = "The machine-facing HTTP host of one registered release target: machine callers send mapping inputs and " +
                "receive the outputs of the target's current release. There is no browser session, cookie, redirect or default " +
                "authentication scheme. Every answer carries `Cache-Control: no-store` and `X-Content-Type-Options: nosniff`. " +
                "Any other path, any query string and any path base answer `404`; another method on a mapped path answers " +
                "`405` with `Allow`."
        };
        document.Servers = [];
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[MachineToken] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.OAuth2,
            Description = "A Microsoft Entra v2 app-only access token from a client-credentials application or managed identity, " +
                "sent as `Authorization: Bearer <token>`. `{tenant}` is the endpoint's fixed organizational tenant " +
                "(`BRAID_ENDPOINT_ENTRA_TENANT_ID`) and `{audience}` the application (client) ID of the endpoint's own app " +
                "registration (`BRAID_ENDPOINT_ENTRA_AUDIENCE`), which must accept v2 access tokens. The token must be RS256-signed " +
                "by the tenant, carry exactly that audience and no `scp` claim; the caller is its `oid`, and an active " +
                "invocation grant must bind that principal to the target.",
            Flows = new OpenApiOAuthFlows
            {
                ClientCredentials = new OpenApiOAuthFlow
                {
                    TokenUrl = new Uri("https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token"),
                    Scopes = new Dictionary<string, string> { [Scope] = "Call the endpoint as the client application." }
                }
            }
        };
        var schemas = document.Components.Schemas ?? throw new InvalidOperationException("The endpoint document has no schemas.");
        if (!schemas.TryGetValue(nameof(MappingInvocationResponseV1), out var completed) || completed is not OpenApiSchema { Properties: { } response })
            throw new InvalidOperationException("The completed response schema is missing.");
        response["schema"] = Text(MappingEndpointContract.ResponseSchema);
        Set(nameof(MappingDocumentInvocationRequestV1), "schema", Text(MappingEndpointContract.DocumentRequestSchema));
        Set(nameof(MappingDocumentInvocationResponseV1), "schema", Text(MappingEndpointContract.DocumentResponseSchema));
        OmittedWhenNull(document, typeof(MappingInvocationErrorV1));

        void Set(string name, string property, OpenApiSchema value)
        {
            if (!schemas.TryGetValue(name, out var found) || found is not OpenApiSchema { Properties: { } properties })
                throw new InvalidOperationException("The document schema " + name + " is missing.");
            properties[property] = value;
        }
    }

    private static async Task DescribeAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        var document = context.Document ?? throw new InvalidOperationException("Operations require their document.");
        switch (operation.OperationId)
        {
            case "InvokeRelease":
                operation.Summary = "Invoke the target's current release.";
                operation.Description = "Runs every target of the current release's qualified artifact with the server's current UTC clock, " +
                    "as the release's publisher. The endpoint admits at most its configured concurrency (1 to 4) without a queue, " +
                    "and a saturated or stopping endpoint answers `503` before it validates the token or reads the body. A call " +
                    "has a 90-second deadline that covers validation, the body, the ledger, one execution and the response; the " +
                    "token's expiry also bounds it. The ledger claims each request ID once, so a retry with the same ID and body " +
                    "returns the recorded outcome instead of running again.";
                operation.Security = [new() { [new OpenApiSecuritySchemeReference(MachineToken, document)] = [Scope] }];
                operation.Parameters =
                [
                    new OpenApiParameter
                    {
                        Name = MappingEndpointRoutes.RequestIdHeader,
                        In = ParameterLocation.Header,
                        Required = true,
                        Description = "A caller-chosen canonical lowercase UUID, not the nil UUID, sent once. Retrying with the same ID " +
                            "and body returns the original outcome; another body or client application under it conflicts.",
                        Schema = RequestId()
                    }
                ];
                var request = await context.GetOrCreateSchemaAsync(typeof(MappingInvocationRequestV1), parameterDescription: null, cancellationToken);
                document.AddComponent(nameof(MappingInvocationRequestV1), request);
                var documentRequest = await context.GetOrCreateSchemaAsync(typeof(MappingDocumentInvocationRequestV1), parameterDescription: null, cancellationToken);
                document.AddComponent(nameof(MappingDocumentInvocationRequestV1), documentRequest);
                var documentResponse = await context.GetOrCreateSchemaAsync(typeof(MappingDocumentInvocationResponseV1), parameterDescription: null, cancellationToken);
                document.AddComponent(nameof(MappingDocumentInvocationResponseV1), documentResponse);
                foreach (var type in new[] { typeof(MappingDocumentInvocationInputV1), typeof(MappingDocumentInvocationOutputV1) })
                    document.AddComponent(type.Name, await context.GetOrCreateSchemaAsync(type, parameterDescription: null, cancellationToken));
                operation.RequestBody = new OpenApiRequestBody
                {
                    Required = true,
                    Description = "At most " + MappingEndpointRoutes.MaximumBodyBytes + " bytes as `application/json` " +
                        "(optionally `; charset=utf-8`) without a content encoding. Inputs total at most 1 MiB, outputs at most 1 MiB, " +
                        "and the preview bounds on counts, names and parameters apply. Legacy bodies omit schema and media. " +
                        "Document bodies require braid.document-invocation-request.v1 and a mediaType per input matching the qualified boundary; no filename inference. " +
                        "CSV/TSV requires matching csvDialect metadata and a primary-only CSV build. An explicit empty base64 string is a valid empty CSV document; missing content is not. JSON/EDI inputs remain nonempty.",
                    Content = new Dictionary<string, OpenApiMediaType>
                    {
                        [MappingEndpointContract.MediaType] = new()
                        {
                            Schema = new OpenApiSchema
                            {
                                OneOf = [
                            new OpenApiSchemaReference(nameof(MappingInvocationRequestV1), document),
                            new OpenApiSchemaReference(nameof(MappingDocumentInvocationRequestV1), document)]
                            }
                        }
                    }
                };
                Invocation(operation, document);
                ((OpenApiResponse)operation.Responses!["200"]).Content![MappingEndpointContract.MediaType].Schema = new OpenApiSchema
                {
                    OneOf = [
                    new OpenApiSchemaReference(nameof(MappingInvocationResponseV1), document),
                    new OpenApiSchemaReference(nameof(MappingDocumentInvocationResponseV1), document)]
                };
                break;
            case "GetLiveness":
                operation.Summary = "Liveness probe.";
                Respond(operation, "204", "The process serves requests.");
                break;
            case "GetReadiness":
                operation.Summary = "Readiness probe.";
                Respond(operation, "204", "Started, and the compiled runtime admits work.");
                Respond(operation, "503", "Not yet started, stopping, or the compiled runtime has closed.");
                break;
            default:
                // A new route needs its description here before the document can be written.
                throw new InvalidOperationException("The operation " + operation.OperationId + " is not described.");
        }
    }

    /// <summary>The invocation's answers; each refusal lists exactly the outcomes <see cref="MappingEndpointRoutes.Status"/> gives it.</summary>
    private static void Invocation(OpenApiOperation operation, OpenApiDocument document)
    {
        Respond(operation, "200", "Completed. A retry with the same request ID and body returns these exact bytes.",
            RequestIdHeader(), Replayed());
        foreach (var status in MappingEndpointRoutes.Refusals)
        {
            var outcomes = Enum.GetValues<MappingInvocationOutcomeV1>().Where(outcome => MappingEndpointRoutes.Status(outcome) == status)
                .Select(outcome => JsonNamingPolicy.CamelCase.ConvertName(outcome.ToString())).ToArray();
            var (description, headers) = status switch
            {
                400 => ("A malformed request ID (`invalidRequestId`, without `requestId`) or body (`invalidRequest`). Nothing is recorded.",
                    Array.Empty<(string, IOpenApiHeader)>()),
                401 => ("A missing, refused or expired bearer token, also once it expires during the call. Nothing is claimed or " +
                    "delivered for it; a retry with a valid token replays a recorded outcome.", [Authenticate()]),
                403 => ("No active grant, a disabled target, or a release the publisher can no longer run.", [RequestIdHeader(), Replayed(required: false)]),
                409 => ("`conflict`: another body or client application under this request ID. `inProgress`: an attempt for this ID " +
                    "is running; retry after `Retry-After`.", [RequestIdHeader(), RetryAfter(required: false)]),
                410 => ("The call completed, but its retained response has expired.", [RequestIdHeader(), Replayed()]),
                413 => ("The body exceeds " + MappingEndpointRoutes.MaximumBodyBytes + " bytes. Nothing is recorded.", []),
                415 => ("The body is not JSON, or has a content encoding. Nothing is recorded.", []),
                422 => ("A recorded mapping outcome; the same request returns it again.", [RequestIdHeader(), Replayed()]),
                500 => ("A recorded failure; use a new request ID.", [RequestIdHeader(), Replayed()]),
                503 => ("Nothing was recorded as final; retry the same request after `Retry-After`. `Braid-Request-Id` is present once " +
                    "the call reached the ledger.", [RetryAfter(required: true), RequestIdHeader(required: false)]),
                _ => throw new InvalidOperationException("The invocation status " + status + " is not described.")
            };
            Respond(operation, status.ToString(System.Globalization.CultureInfo.InvariantCulture), description, headers);
            ((OpenApiResponse)operation.Responses![status.ToString(System.Globalization.CultureInfo.InvariantCulture)]).Content =
                new Dictionary<string, OpenApiMediaType>
                {
                    [MappingEndpointContract.MediaType] = new()
                    {
                        Schema = new OpenApiSchema
                        {
                            AllOf = [new OpenApiSchemaReference(nameof(MappingInvocationErrorV1), document)],
                            Properties = new Dictionary<string, IOpenApiSchema> { ["outcome"] = Text(outcomes) }
                        }
                    }
                };
        }
    }

    private static void Respond(OpenApiOperation operation, string status, string description, params (string Name, IOpenApiHeader Header)[] headers)
    {
        // The route's own response metadata decides which statuses exist; a description cannot add one.
        if (operation.Responses?.TryGetValue(status, out var found) != true || found is not OpenApiResponse response)
            throw new InvalidOperationException("The operation " + operation.OperationId + " has no " + status + " response.");
        response.Description = description;
        response.Headers = new Dictionary<string, IOpenApiHeader>(StringComparer.Ordinal)
        {
            ["Cache-Control"] = new OpenApiHeader { Required = true, Schema = Text("no-store") },
            ["X-Content-Type-Options"] = new OpenApiHeader { Required = true, Schema = Text("nosniff") }
        };
        foreach (var (name, header) in headers) response.Headers[name] = header;
    }

    private static (string, IOpenApiHeader) RequestIdHeader(bool required = true) => (MappingEndpointRoutes.RequestIdHeader, new OpenApiHeader
    {
        Required = required,
        Description = "The admitted request ID.",
        Schema = RequestId()
    });

    private static (string, IOpenApiHeader) Replayed(bool required = true) => (MappingEndpointRoutes.ReplayedHeader, new OpenApiHeader
    {
        Required = required,
        Description = "Whether this answer replays an outcome recorded by an earlier call" + (required ? "." : "; present for a recorded refusal."),
        Schema = Text("true", "false")
    });

    private static (string, IOpenApiHeader) RetryAfter(bool required) => ("Retry-After", new OpenApiHeader
    {
        Required = required,
        Description = "Seconds to wait before retrying" + (required ? "." : "; present for `inProgress`."),
        Schema = new OpenApiSchema { Type = JsonSchemaType.String, Pattern = "^[1-9][0-9]*$" }
    });

    private static (string, IOpenApiHeader) Authenticate() => ("WWW-Authenticate", new OpenApiHeader
    {
        Required = true,
        Schema = Text("Bearer")
    });

    private static OpenApiSchema RequestId() => new()
    {
        Type = JsonSchemaType.String,
        Format = "uuid",
        Pattern = "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$"
    };

    /// <summary>A property the endpoint omits when it has no value is optional and never null.</summary>
    private static void OmittedWhenNull(OpenApiDocument document, params Type[] types)
    {
        foreach (var type in types)
        {
            if (document.Components?.Schemas?.TryGetValue(type.Name, out var found) != true || found is not OpenApiSchema { Properties: { } properties } schema)
                throw new InvalidOperationException("The schema " + type.Name + " is missing.");
            foreach (var property in type.GetProperties())
            {
                if (property.GetCustomAttribute<JsonIgnoreAttribute>() is not { Condition: JsonIgnoreCondition.WhenWritingNull }) continue;
                var name = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
                schema.Required?.Remove(name);
                if (properties[name] is not OpenApiSchema { Type: { } kind } value)
                    throw new InvalidOperationException("The property " + type.Name + "." + name + " has an unexpected schema.");
                value.Type = kind & ~JsonSchemaType.Null;
            }
        }
    }

    private static OpenApiSchema Text(params string[] values) => new()
    {
        Type = JsonSchemaType.String,
        Enum = values.Select(value => (JsonNode)JsonValue.Create(value)!).ToList()
    };

    /// <summary>A server that never listens: the document host answers no request.</summary>
    private sealed class NoListener : IServer
    {
        public IFeatureCollection Features { get; } = new FeatureCollection();
        public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken) where TContext : notnull =>
            Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Dispose() { }
    }
}
