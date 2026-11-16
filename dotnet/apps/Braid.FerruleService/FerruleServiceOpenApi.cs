using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi;
using Braid.MappingBackends.Workers;
using Braid.WorkerTransport;

namespace Braid.FerruleService;

/// <summary>
/// The OpenAPI description of <c>braid.ferrule-service.v1</c>. The build writes it to
/// <c>contracts/ferrule-service-v1.json</c> from the transport's own route registrations and their response metadata;
/// this class adds what metadata cannot carry: headers, bodies, limits and the mutual-TLS scheme.
/// </summary>
internal static class FerruleServiceOpenApi
{
    internal const string DocumentName = "v1";
    internal const string ClientCertificate = "pinnedClientCertificate";

    /// <summary>Every route a service with pinned mutual TLS and a probe listener serves.</summary>
    internal const WorkerTransportRoutes Served = WorkerTransportRoutes.Service | WorkerTransportRoutes.Health | WorkerTransportRoutes.Inspection;

    /// <summary>Every profile name the service routes, served or reserved.</summary>
    internal static readonly string[] Profiles =
    [
        FerruleServiceProtocol.EditProfile, FerruleServiceProtocol.InspectionProfile, FerruleServiceProtocol.PreviewProfile, FerruleServiceProtocol.CompileProfile,
        FerruleServiceProtocol.CompiledProfile, FerruleServiceProtocol.InterchangeProfile, FerruleServiceProtocol.AuthoringProfile,
        FerruleServiceProtocol.AuthoredPreviewProfile
    ];

    /// <summary>
    /// True only inside build-time document generation, whose tool is then the entry assembly. No setting, argument or
    /// environment value selects the document-only host.
    /// </summary>
    internal static bool Generating => Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider";

    /// <summary>
    /// The document-only host: every route the service can serve, mapped by <see cref="WorkerTransportServer.MapRoutes"/>,
    /// and the OpenAPI document. It has no listener, settings, certificate or runner, so it answers nothing.
    /// </summary>
    internal static IHost BuildDocumentHost() => new HostBuilder()
        .UseContentRoot(AppContext.BaseDirectory)
        .ConfigureHostConfiguration(configuration =>
            configuration.AddInMemoryCollection([new(HostDefaults.ApplicationKey, "braid-ferrule-service")]))
        .ConfigureWebHost(web => web
            .ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddSingleton<IServer, NoListener>();
                // The server writes numbers as JSON numbers only.
                services.ConfigureHttpJsonOptions(options => options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict);
                services.AddOpenApi(DocumentName, options =>
                {
                    options.AddDocumentTransformer((document, _, _) =>
                    {
                        Describe(document);
                        return Task.CompletedTask;
                    });
                    options.AddOperationTransformer((operation, context, _) =>
                    {
                        Describe(operation, context.Document ?? throw new InvalidOperationException("Operations require their document."));
                        return Task.CompletedTask;
                    });
                });
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseEndpoints(routes => WorkerTransportServer.MapRoutes(routes, Served));
            }))
        .Build();

    private static void Describe(OpenApiDocument document)
    {
        document.Info = new OpenApiInfo
        {
            Title = "Braid Ferrule service",
            Version = DocumentName,
            Description = "`" + FerruleServiceProtocol.Version + "`: stateless Ferrule operations for horizontally scaled replicas. " +
                "A route selects a trusted profile; the frame's own header selects the Ferrule operation within it. Over pinned " +
                "mutual TLS the service also serves the `braid.inspection-transport.v1` routes, so an existing inspection client " +
                "works unchanged. A separate plain probe listener, when configured, serves only the two health routes and answers " +
                "`404` for every other path. Any other path answers `404`, and another method on a mapped path `405` with `Allow`."
        };
        document.Servers = [];
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[ClientCertificate] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.MutualTLS,
            Description = "Pinned mutual TLS: the TLS handshake completes only for a client certificate whose DER SHA-256 digest " +
                "the service admits (`BRAID_FERRULE_SERVICE_CLIENT_CERTIFICATE_SHA256`), and the client pins the service " +
                "certificate the same way. Trust stores, names and revocation are not used. A platform-sandbox deployment " +
                "(`BRAID_FERRULE_SERVICE_AUTHENTICATION=platformSandbox`) instead listens on plain HTTP behind a platform front " +
                "end that authenticates every caller and is the only route to the listener; it serves the service and probe " +
                "routes only."
        };
        document.Security = [Requirement(document)];
        OmittedWhenNull(document, typeof(FerruleServiceCapabilitiesV1), typeof(FerruleServiceProfileV1));
    }

    private static void Describe(OpenApiOperation operation, OpenApiDocument document)
    {
        switch (operation.OperationId)
        {
            case "GetServiceReady":
                operation.Summary = "Confirm the service and its identities before sending a frame.";
                operation.Description = "Over pinned TLS, reaching this route proves that the service admitted the client certificate.";
                Respond(operation, "204", "Ready.", ServiceHeaders(), Image(), Toolchain(), Runtime());
                Respond(operation, "503", "The replica is stopping or its cleanup is unconfirmed. Send to another replica after `Retry-After`.", ServiceHeaders(), RetryAfter());
                break;
            case "GetCapabilities":
                operation.Summary = "Read the protocol, identities, replica limits and served profiles.";
                Respond(operation, "200", "The capability document.", ServiceHeaders());
                break;
            case "RunOperation":
                operation.Summary = "Run one exact frame under a fresh operation identity.";
                operation.Description = Operation(service: true);
                operation.Parameters = [Profile(), OperationId(), WorkerImage(), CompileToolchain(service: true), CompiledRuntime()];
                operation.RequestBody = Frame();
                Respond(operation, "200", Settled(), ServiceHeaders(), Result(), Image(), Toolchain(), Runtime());
                Output(operation);
                Respond(operation, "400", "Malformed operation identity, media type, content encoding or length. Nothing ran.", ServiceHeaders());
                Respond(operation, "404", "Unknown profile. Nothing ran.", ServiceHeaders());
                Respond(operation, "409", "Another native, toolchain or runtime identity, or a repeated operation identity. Nothing ran.", ServiceHeaders());
                Respond(operation, "501", "A reserved profile: `compile` or `execute` while disabled. Nothing ran.", ServiceHeaders());
                Respond(operation, "503", Busy(), ServiceHeaders(), RetryAfter());
                break;
            case "CancelOperation":
                operation.Summary = "Cancel an operation on the channel that carries it.";
                operation.Description = Cancellation();
                operation.Parameters = [Profile(), OperationId()];
                Respond(operation, "204", "Canceled, or recorded so that the operation is refused when it arrives.");
                Respond(operation, "400", "Malformed operation identity, or a profile this service does not serve.");
                break;
            case "GetLiveness":
                operation.Summary = "Platform liveness probe.";
                operation.Security = Probe(document);
                Respond(operation, "204", "The process serves requests.");
                break;
            case "GetReadiness":
                operation.Summary = "Platform readiness probe.";
                operation.Security = Probe(document);
                Respond(operation, "204", "Ready.");
                Respond(operation, "503", "Stopping, or a cleanup is unconfirmed.");
                break;
            case "GetInspectionReady":
                operation.Summary = "Confirm the client certificate and the native identity (`braid.inspection-transport.v1`).";
                Respond(operation, "204", "Ready.", Image());
                break;
            case "RunInspectionOperation":
                operation.Summary = "Run one exact inspection frame (`braid.inspection-transport.v1`).";
                operation.Description = Operation(service: false);
                operation.Parameters = [OperationId(), WorkerImage(), CompileToolchain(service: false), CompiledRuntime()];
                operation.RequestBody = Frame();
                Respond(operation, "200", Settled(), Result(), Image(), Toolchain());
                Output(operation);
                Respond(operation, "400", "Malformed operation identity, media type, content encoding or length. Nothing ran.");
                Respond(operation, "409", "Another native or toolchain identity, a runtime identity, or a repeated operation identity. Nothing ran.");
                Respond(operation, "503", Busy(), RetryAfter());
                break;
            case "CancelInspectionOperation":
                operation.Summary = "Cancel an inspection operation (`braid.inspection-transport.v1`).";
                operation.Description = Cancellation();
                operation.Parameters = [OperationId()];
                Respond(operation, "204", "Canceled, or recorded so that the operation is refused when it arrives.");
                Respond(operation, "400", "Malformed operation identity.");
                break;
            default:
                // A new route needs its description here before the document can be written.
                throw new InvalidOperationException("The operation " + operation.OperationId + " is not described.");
        }
        // Every mapped service response, including legacy inspection and plain health responses, identifies this
        // process for diagnostics. It is not an affinity token or evidence that an uncertain operation settled.
        foreach (var response in operation.Responses!.Values.Cast<OpenApiResponse>())
        {
            response.Headers ??= new Dictionary<string, IOpenApiHeader>();
            response.Headers[FerruleServiceProtocol.ReplicaHeader] = new OpenApiHeader
            {
                Required = true,
                Description = "Random per-process replica identity for diagnostics only; never proof of operation settlement or routing affinity.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.String, Pattern = "^[0-9a-f]{32}$" }
            };
        }
    }

    private static string Operation(bool service) =>
        "The service reads the complete frame before admission, so an incomplete frame never runs and its connection is " +
        "aborted. An admitted operation answers `200` only after its child has settled; any other status means nothing ran. " +
        "An operation identity is admitted once per replica. Send ready, the operation and its cancellation as streams of " +
        "one HTTP/2 connection (pinned mutual TLS) or within one sandbox session, so that all reach the same replica." +
        (service ? "\n\n" + Limits() : "\n\nThe `inspection` profile's limits apply:\n\n" + Limits(FerruleServiceProtocol.InspectionProfile));

    private static string Cancellation() =>
        "Cancels the admitted operation through its runner, which then answers it `200` with `Braid-Worker-Result: canceled`. " +
        "A cancellation that arrives first is remembered, and the operation is answered `canceled` without running.";

    private static string Settled() =>
        "The operation settled. With `Braid-Worker-Result: none` the body is the exact worker output; otherwise it is empty.";

    private static string Busy() =>
        "The replica is full or stopping, or the operation was not admitted within the admission wait. Nothing ran; send a " +
        "fresh operation identity to any replica after `Retry-After`.";

    /// <summary>The fixed limits behind each profile name, from the same table the service and its clients use.</summary>
    private static string Limits(params string[] names)
    {
        var table = new StringBuilder("| Profile | Request bytes | Response bytes | Wall seconds | CPU seconds | Memory bytes |\n");
        table.Append("| --- | --- | --- | --- | --- | --- |\n");
        foreach (var name in names.Length == 0 ? Profiles : names)
        {
            var profile = FerruleServiceProtocol.Profile(name)!;
            table.Append(CultureInfo.InvariantCulture,
                $"| `{name}` | {profile.MaximumRequestBytes} | {profile.MaximumOutputBytes} | {(long)profile.MaximumWallTimeout.TotalSeconds} | {profile.CpuSeconds} | {profile.MemoryBytes} |\n");
        }
        return table.ToString().TrimEnd('\n');
    }

    private static OpenApiRequestBody Frame() => new()
    {
        Required = true,
        Description = "One exact frame with a known length (`Content-Length` of at least one byte and at most the profile's " +
            "request bytes) and no content encoding.",
        Content = new Dictionary<string, OpenApiMediaType>
        {
            [InspectionTransport.MediaType] = new() { Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" } }
        }
    };

    private static void Output(OpenApiOperation operation) =>
        ((OpenApiResponse)operation.Responses!["200"]).Content = new Dictionary<string, OpenApiMediaType>
        {
            [InspectionTransport.MediaType] = new() { Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" } }
        };

    private static OpenApiParameter Profile() => new()
    {
        Name = "profile",
        In = ParameterLocation.Path,
        Required = true,
        Description = "`edit`, `inspection`, `preview` and `interchange` are always served. `compile` and `execute` are served only when enabled and " +
            "otherwise answer `501`; another name answers `404` (`400` for a cancellation).",
        Schema = Text(Profiles)
    };

    private static OpenApiParameter OperationId() => new()
    {
        Name = "operation",
        In = ParameterLocation.Path,
        Required = true,
        Description = "A fresh 128-bit operation identity.",
        Schema = new OpenApiSchema { Type = JsonSchemaType.String, Pattern = "^[0-9a-f]{32}$" }
    };

    private static OpenApiParameter WorkerImage() => new()
    {
        Name = InspectionTransport.ImageHeader,
        In = ParameterLocation.Header,
        Required = true,
        Description = "The service's native identity, as `GET /v1/ready` reports it; another value answers `409`.",
        Schema = Digest()
    };

    private static OpenApiParameter CompileToolchain(bool service) => new()
    {
        Name = FerruleServiceProtocol.CompileToolchainHeader,
        In = ParameterLocation.Header,
        Required = false,
        Description = (service ? "Required for the `compile` profile. " : "") +
            "When present it must equal the service's compile toolchain identity; another value answers `409`.",
        Schema = Digest()
    };

    private static OpenApiParameter CompiledRuntime() => new()
    {
        Name = FerruleServiceProtocol.CompiledRuntimeHeader,
        In = ParameterLocation.Header,
        Required = false,
        Description = "Required for the `execute` profile and equal to the service's runtime identity. Any other value, or the " +
            "header on another profile, answers `409`.",
        Schema = Digest()
    };

    private static (string Name, IOpenApiHeader Header) Result() => (InspectionTransport.ResultHeader, new OpenApiHeader
    {
        Required = true,
        Description = "How the child settled. Only `none` carries output.",
        Schema = Text(Enum.GetValues<WorkerProcessFailure>().Select(InspectionTransport.Encode).ToArray())
    });

    private static (string Name, IOpenApiHeader Header) Image() => (InspectionTransport.ImageHeader, new OpenApiHeader
    {
        Required = true,
        Description = "The service's native identity.",
        Schema = Digest()
    });

    private static (string Name, IOpenApiHeader Header) Toolchain() => (FerruleServiceProtocol.CompileToolchainHeader, new OpenApiHeader
    {
        Description = "The compile toolchain identity, when the service compiles.",
        Schema = Digest()
    });

    private static (string Name, IOpenApiHeader Header) Runtime() => (FerruleServiceProtocol.CompiledRuntimeHeader, new OpenApiHeader
    {
        Description = "The compiled runtime identity: on ready when execution is enabled, and on `execute` answers.",
        Schema = Digest()
    });

    private static (string Name, IOpenApiHeader Header) RetryAfter() => ("Retry-After", new OpenApiHeader
    {
        Required = true,
        Description = "Seconds to wait before retrying.",
        Schema = Text("1")
    });

    private static (string Name, IOpenApiHeader Header)[] ServiceHeaders() =>
    [
        (FerruleServiceProtocol.ProtocolHeader, new OpenApiHeader
        {
            Required = true,
            Description = "Identifies the service, so a client can tell its answers from an intermediary's.",
            Schema = Text(FerruleServiceProtocol.Version)
        })
    ];

    private static void Respond(OpenApiOperation operation, string status, string description, params (string Name, IOpenApiHeader Header)[] headers) =>
        Respond(operation, status, description, [], headers);

    private static void Respond(OpenApiOperation operation, string status, string description, (string Name, IOpenApiHeader Header)[] shared,
        params (string Name, IOpenApiHeader Header)[] headers)
    {
        // The route's own response metadata decides which statuses exist; a description cannot add one.
        if (operation.Responses?.TryGetValue(status, out var found) != true || found is not OpenApiResponse response)
            throw new InvalidOperationException("The operation " + operation.OperationId + " has no " + status + " response.");
        response.Description = description;
        if (shared.Length + headers.Length == 0) return;
        response.Headers = shared.Concat(headers).ToDictionary(header => header.Name, header => header.Header, StringComparer.Ordinal);
    }

    /// <summary>A property the server omits when it has no value is optional and never null.</summary>
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
                properties[name] = properties[name] switch
                {
                    OpenApiSchema { OneOf: [OpenApiSchema { Type: JsonSchemaType.Null }, var value] } => value,
                    OpenApiSchema { Type: { } kind } value => Strip(value, kind),
                    var other => throw new InvalidOperationException("The property " + type.Name + "." + name + " has an unexpected schema: " + other)
                };
            }
        }

        static OpenApiSchema Strip(OpenApiSchema schema, JsonSchemaType kind)
        {
            schema.Type = kind & ~JsonSchemaType.Null;
            return schema;
        }
    }

    private static OpenApiSecurityRequirement Requirement(OpenApiDocument document) =>
        new() { [new OpenApiSecuritySchemeReference(ClientCertificate, document)] = [] };

    /// <summary>Probes are also served on the plain probe listener, which authenticates nobody.</summary>
    private static List<OpenApiSecurityRequirement> Probe(OpenApiDocument document) => [Requirement(document), new()];

    private static OpenApiSchema Digest() => new() { Type = JsonSchemaType.String, Pattern = "^sha256:[0-9a-f]{64}$" };

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
