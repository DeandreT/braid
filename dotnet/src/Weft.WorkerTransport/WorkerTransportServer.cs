using System.Buffers;
using System.Net;
using System.Security.Authentication;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Weft.Deployment;
using Weft.MappingBackends.Workers;

namespace Weft.WorkerTransport;

/// <summary>
/// Serves <c>weft.inspection-transport.v1</c> and, when enabled, <c>weft.ferrule-service.v1</c> with this host's
/// own runners, one per trusted profile. Only authenticated callers reach a route. Each operation identity is
/// admitted once; a cancellation that arrives before its operation refuses it. A frame is read completely before
/// admission, so an incomplete frame never runs. A refused operation never reaches a runner and is answered with a
/// non-success status; an admitted one is answered only after its runner has settled it.
/// </summary>
public sealed class WorkerTransportServer : IHostedService, IAsyncDisposable
{
    private const int RememberedOperations = 4096;
    private const string LegacyReadyPath = InspectionTransport.ReadyPath;
    private const string LegacyOperationsPath = InspectionTransport.OperationsPath;
    private readonly WorkerTransportServerOptions options;
    private readonly IReadOnlyDictionary<string, IInspectionWorkerRunner> runners;
    private readonly string identity;
    private readonly string replica = Guid.NewGuid().ToString("N");
    private readonly object gate = new();
    private readonly Dictionary<string, CancellationTokenSource> active = new(StringComparer.Ordinal);
    private readonly HashSet<string> seen = new(StringComparer.Ordinal);
    private readonly Queue<string> seenOrder = new();
    private readonly HashSet<string> canceledBeforeArrival = new(StringComparer.Ordinal);
    private readonly Queue<string> canceledOrder = new();
    private readonly SemaphoreSlim slots;
    private readonly SemaphoreSlim ingress;
    private readonly CancellationTokenSource shutdown = new();
    private WebApplication? application;
    private int disposed;
    private bool stopping;

    public WorkerTransportServer(WorkerTransportServerOptions options, IReadOnlyDictionary<string, IInspectionWorkerRunner> runners)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(runners);
        options.Validate();
        if (runners.Count == 0 || runners.Any(runner => FerruleServiceProtocol.Profile(runner.Key) is null || runner.Value is null))
            throw new ArgumentException("Every runner must serve one known profile.", nameof(runners));
        if (!options.ServiceRoutes && (runners.Count != 1 || !runners.ContainsKey(FerruleServiceProtocol.InspectionProfile)))
            throw new ArgumentException("The inspection transport alone serves exactly the inspection profile.", nameof(runners));
        if (runners.ContainsKey(FerruleServiceProtocol.CompileProfile) != options.CompileToolchain is not null)
            throw new ArgumentException("A served compile profile reports exactly one toolchain identity.", nameof(runners));
        if (options.CompileToolchain is { } toolchain) InspectionTransport.RequireImage(toolchain);
        if (runners.ContainsKey(FerruleServiceProtocol.CompiledProfile) != options.CompiledRuntimeIdentity is not null)
            throw new ArgumentException("A served execution profile reports its runtime identity.", nameof(runners));
        if (options.CompiledRuntimeIdentity is { } runtime) InspectionTransport.RequireImage(runtime);
        // One service runs one worker build; every profile reports the same identity.
        var identities = runners.Values.Select(runner => runner.BuildIdentity).Distinct(StringComparer.Ordinal).ToArray();
        if (identities.Length != 1)
            throw new ArgumentException("Every runner of one server must share one build identity.", nameof(runners));
        identity = InspectionTransport.RequireImage(identities[0]);
        this.options = options;
        this.runners = runners;
        slots = new SemaphoreSlim(options.Concurrency, options.Concurrency);
        ingress = new SemaphoreSlim(options.Capacity, options.Capacity);
    }

    /// <summary>The actual bound port, for example of an explicitly selected ephemeral listener.</summary>
    public int Port { get; private set; }

    /// <summary>The actual bound port of the optional probe listener.</summary>
    public int HealthPort { get; private set; }

    // Includes body readers, queued/running operations and replies not yet settled.
    internal int ReservedRequests => options.Capacity - ingress.CurrentCount;

    private int MaximumFrameBytes => runners.Keys.Max(name => FerruleServiceProtocol.Profile(name)!.MaximumRequestBytes);
    private bool Tls => options.Authentication == WorkerTransportAuthentication.PinnedMutualTls;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            EnvironmentName = Environments.Production,
            ContentRootPath = AppContext.BaseDirectory
        });
        // Ambient URLs, ports and logging configuration never select this listener or record its traffic.
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.AddServerHeader = false;
            server.Limits.MaxRequestBodySize = MaximumFrameBytes;
            server.Limits.MaxConcurrentConnections = 4 * options.Capacity + 16;
            // Ready, operation and cancellation are the only streams one operation needs.
            server.Limits.Http2.MaxStreamsPerConnection = 4;
            server.Listen(options.Listen, listener =>
            {
                if (!Tls)
                {
                    listener.Protocols = HttpProtocols.Http1;
                    return;
                }

                listener.Protocols = options.ServiceRoutes ? HttpProtocols.Http1AndHttp2 : HttpProtocols.Http1;
                listener.UseHttps(new HttpsConnectionAdapterOptions
                {
                    ServerCertificate = options.ServerCertificate,
                    ClientCertificateMode = ClientCertificateMode.RequireCertificate,
                    ClientCertificateValidation = (certificate, _, _) =>
                        options.ClientCertificateSha256.Any(pin => InspectionTransport.HasDigest(certificate, pin)),
                    CheckCertificateRevocation = false,
                    SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                });
            });
            if (options.HealthListen is { } health)
                server.Listen(health, listener => listener.Protocols = HttpProtocols.Http1);
        });
        // Route handlers find this instance among the request's services.
        builder.Services.AddSingleton(this);
        var built = builder.Build();
        // The plain probe listener reaches nothing but liveness and readiness.
        built.Use((context, next) => Tls && !context.Request.IsHttps && !IsHealthPath(context.Request.Path)
            ? NotFound(context) : next(context));
        MapRoutes(built, Routes);

        try
        {
            await built.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await built.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        application = built;
        var ports = built.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
            .Select(address => new Uri(address.Replace("://*", "://localhost", StringComparison.Ordinal)))
            .ToArray();
        Port = ports.Single(address => address.Scheme == (Tls ? Uri.UriSchemeHttps : Uri.UriSchemeHttp)).Port;
        if (options.HealthListen is not null)
            HealthPort = ports.Single(address => address.Scheme == Uri.UriSchemeHttp).Port;
    }

    /// <summary>The routes the started listener serves.</summary>
    internal IEnumerable<Endpoint> Endpoints =>
        (application ?? throw new InvalidOperationException("The server has not started.")).Services.GetRequiredService<EndpointDataSource>().Endpoints;

    /// <summary>The routes this configuration serves.</summary>
    internal WorkerTransportRoutes Routes =>
        (options.ServiceRoutes ? WorkerTransportRoutes.Service | WorkerTransportRoutes.Health
            : options.HealthListen is not null ? WorkerTransportRoutes.Health : WorkerTransportRoutes.None) |
        (Tls && runners.ContainsKey(FerruleServiceProtocol.InspectionProfile) ? WorkerTransportRoutes.Inspection : WorkerTransportRoutes.None);

    /// <summary>
    /// Maps the selected routes with their answers. Handlers resolve the serving instance from the request's services,
    /// so a document-only host maps these same registrations without a listener or runners.
    /// </summary>
    public static void MapRoutes(IEndpointRouteBuilder routes, WorkerTransportRoutes selected)
    {
        ArgumentNullException.ThrowIfNull(routes);
        const string Json = "application/json";
        const string Service = "Ferrule service";
        const string Health = "Health";
        const string Inspection = "Inspection transport";
        if (selected.HasFlag(WorkerTransportRoutes.Service))
        {
            routes.MapGet(FerruleServiceProtocol.ReadyPath, Handler(static (server, context) => server.Ready(context, service: true)))
                .WithName("GetServiceReady").WithTags(Service)
                .Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status503ServiceUnavailable);
            routes.MapGet(FerruleServiceProtocol.CapabilitiesPath, Handler(static (server, context) => server.CapabilitiesAsync(context)))
                .WithName("GetCapabilities").WithTags(Service)
                .Produces<FerruleServiceCapabilitiesV1>(StatusCodes.Status200OK, Json);
            Operation(routes.MapPost(FerruleServiceProtocol.OperationsPath + "{profile}/{operation}", Handler(static (server, context) =>
                    server.RunAsync(context, context.Request.RouteValues["profile"] as string, service: true))), "RunOperation", Service)
                .Produces(StatusCodes.Status404NotFound).Produces(StatusCodes.Status501NotImplemented);
            Cancellation(routes.MapPost(FerruleServiceProtocol.OperationsPath + "{profile}/{operation}" + InspectionTransport.CancelSuffix,
                Handler(static (server, context) => server.Cancel(context, context.Request.RouteValues["profile"] as string))), "CancelOperation", Service);
        }

        if (selected.HasFlag(WorkerTransportRoutes.Health))
        {
            routes.MapGet(FerruleServiceProtocol.LivenessPath, Handler(static (_, context) => Live(context)))
                .WithName("GetLiveness").WithTags(Health).Produces(StatusCodes.Status204NoContent);
            routes.MapGet(FerruleServiceProtocol.ReadinessPath, Handler(static (server, context) => server.Readiness(context)))
                .WithName("GetReadiness").WithTags(Health)
                .Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status503ServiceUnavailable);
        }

        if (selected.HasFlag(WorkerTransportRoutes.Inspection))
        {
            routes.MapGet(LegacyReadyPath, Handler(static (server, context) => server.Ready(context, service: false)))
                .WithName("GetInspectionReady").WithTags(Inspection).Produces(StatusCodes.Status204NoContent);
            Operation(routes.MapPost(LegacyOperationsPath + "{operation}", Handler(static (server, context) =>
                server.RunAsync(context, FerruleServiceProtocol.InspectionProfile, service: false))), "RunInspectionOperation", Inspection);
            Cancellation(routes.MapPost(LegacyOperationsPath + "{operation}" + InspectionTransport.CancelSuffix, Handler(static (server, context) =>
                server.Cancel(context, FerruleServiceProtocol.InspectionProfile))), "CancelInspectionOperation", Inspection);
        }

        // No Accepts metadata: routing would answer a media type before the operation's own checks. The service's
        // OpenAPI document describes the frame body.
        static RouteHandlerBuilder Operation(RouteHandlerBuilder route, string name, string tag) => route
            .WithName(name).WithTags(tag)
            .Produces(StatusCodes.Status200OK, contentType: InspectionTransport.MediaType)
            .Produces(StatusCodes.Status400BadRequest).Produces(StatusCodes.Status409Conflict).Produces(StatusCodes.Status503ServiceUnavailable);

        static RouteHandlerBuilder Cancellation(RouteHandlerBuilder route, string name, string tag) => route
            .WithName(name).WithTags(tag).Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status400BadRequest);
    }

    private static Delegate Handler(Func<WorkerTransportServer, HttpContext, Task> handle) =>
        (HttpContext context) =>
        {
            var server = context.RequestServices.GetRequiredService<WorkerTransportServer>();
            if (server.options.ServiceRoutes)
                context.Response.Headers[FerruleServiceProtocol.ReplicaHeader] = server.replica;
            return handle(server, context);
        };

    private static bool IsHealthPath(PathString path) =>
        path == FerruleServiceProtocol.LivenessPath || path == FerruleServiceProtocol.ReadinessPath;

    private static Task NotFound(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return Task.CompletedTask;
    }

    private Task Ready(HttpContext context, bool service)
    {
        // Over pinned TLS, reaching any route proves the handshake admitted an admitted client certificate.
        bool accepting;
        lock (gate) accepting = !stopping;
        if (service && (!accepting || options.Healthy?.Invoke() == false))
        {
            context.Response.Headers[FerruleServiceProtocol.ProtocolHeader] = FerruleServiceProtocol.Version;
            Busy(context);
            return Task.CompletedTask;
        }

        context.Response.StatusCode = StatusCodes.Status204NoContent;
        context.Response.Headers[InspectionTransport.ImageHeader] = identity;
        if (service) context.Response.Headers[FerruleServiceProtocol.ProtocolHeader] = FerruleServiceProtocol.Version;
        if (service && options.CompileToolchain is { } toolchain)
            context.Response.Headers[FerruleServiceProtocol.CompileToolchainHeader] = toolchain;
        if (service && options.CompiledRuntimeIdentity is { } runtime)
            context.Response.Headers[FerruleServiceProtocol.CompiledRuntimeHeader] = runtime;
        return Task.CompletedTask;
    }

    private static Task Live(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status204NoContent;
        return Task.CompletedTask;
    }

    private Task Readiness(HttpContext context)
    {
        bool ready;
        lock (gate) ready = !stopping;
        context.Response.StatusCode = ready && (options.Healthy?.Invoke() ?? true)
            ? StatusCodes.Status204NoContent : StatusCodes.Status503ServiceUnavailable;
        return Task.CompletedTask;
    }

    private async Task CapabilitiesAsync(HttpContext context)
    {
        var buffer = new ArrayBufferWriter<byte>();
        await using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("protocol", FerruleServiceProtocol.Version);
            writer.WriteString("buildIdentity", identity);
            if (options.EngineRevision is { } engine) writer.WriteString("engineRevision", engine);
            writer.WriteNumber("concurrency", options.Concurrency);
            writer.WriteNumber("capacity", options.Capacity);
            writer.WriteNumber("admissionWaitMilliseconds", (long)options.AdmissionWait.TotalMilliseconds);
            writer.WriteStartArray("profiles");
            foreach (var name in runners.Keys.Order(StringComparer.Ordinal))
            {
                var profile = FerruleServiceProtocol.Profile(name)!;
                writer.WriteStartObject();
                writer.WriteString("name", name);
                writer.WriteNumber("maximumRequestBytes", profile.MaximumRequestBytes);
                writer.WriteNumber("maximumResponseBytes", profile.MaximumOutputBytes);
                writer.WriteNumber("wallSeconds", (long)profile.MaximumWallTimeout.TotalSeconds);
                writer.WriteNumber("cpuSeconds", profile.CpuSeconds);
                writer.WriteNumber("memoryBytes", profile.MemoryBytes);
                if (name == FerruleServiceProtocol.CompileProfile)
                {
                    writer.WriteStartObject("toolchain");
                    writer.WriteString("protocol", BuildWorkerProtocol.Protocol);
                    writer.WriteString("profile", BuildWorkerProtocol.Profile);
                    writer.WriteStartArray("supportedProfiles");
                    writer.WriteStringValue(BuildWorkerProtocol.Profile);
                    writer.WriteStringValue(BuildWorkerProtocol.DocumentProfile);
                    writer.WriteStringValue(BuildWorkerProtocol.X12InterchangesProfile);
                    writer.WriteEndArray();
                    writer.WriteString("sdkVersion", BuildWorkerProtocol.SdkVersion);
                    writer.WriteString("runtimeVersion", BuildWorkerProtocol.RuntimeVersion);
                    writer.WriteString("identity", options.CompileToolchain);
                    writer.WriteString("buildIdentity", CompileWorkerIdentity.Combine(identity, options.CompileToolchain!));
                    writer.WriteEndObject();
                }
                if (name == FerruleServiceProtocol.CompiledProfile)
                {
                    writer.WriteStartObject("runtime");
                    writer.WriteString("protocol", CompiledRuntimeProtocol.Protocol);
                    writer.WriteStartArray("supportedProtocols");
                    writer.WriteStringValue(CompiledRuntimeProtocol.Protocol);
                    writer.WriteStringValue(CompiledRuntimeProtocol.DocumentProtocol);
                    writer.WriteEndArray();
                    writer.WriteString("version", BuildWorkerProtocol.RuntimeVersion);
                    writer.WriteString("identity", options.CompiledRuntimeIdentity);
                    writer.WriteNumber("managedHeapBytes", ProcessCompiledRuntimeOptions.Profile.MemoryBytes);
                    writer.WriteNumber("maximumAddressSpaceBytes", ProcessCompiledRuntimeOptions.AddressSpaceBytes);
                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("reserved");
            if (!runners.ContainsKey(FerruleServiceProtocol.CompileProfile))
                writer.WriteStringValue(FerruleServiceProtocol.CompileProfile);
            if (!runners.ContainsKey(FerruleServiceProtocol.CompiledProfile))
                writer.WriteStringValue(FerruleServiceProtocol.CompiledProfile);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength = buffer.WrittenCount;
        context.Response.Headers[FerruleServiceProtocol.ProtocolHeader] = FerruleServiceProtocol.Version;
        await context.Response.Body.WriteAsync(buffer.WrittenMemory, context.RequestAborted).ConfigureAwait(false);
    }

    private async Task RunAsync(HttpContext context, string? profileName, bool service)
    {
        var request = context.Request;
        var operation = request.RouteValues["operation"] as string;
        // Service refusals identify the service, so a client can tell them from an intermediary's answer.
        if (service) context.Response.Headers[FerruleServiceProtocol.ProtocolHeader] = FerruleServiceProtocol.Version;
        if (profileName is null || !runners.TryGetValue(profileName, out var runner))
        {
            context.Response.StatusCode = profileName is FerruleServiceProtocol.CompileProfile or FerruleServiceProtocol.CompiledProfile
                ? StatusCodes.Status501NotImplemented : StatusCodes.Status404NotFound;
            return;
        }

        var profile = FerruleServiceProtocol.Profile(profileName)!;
        if (!InspectionTransport.IsOperationId(operation) || request.ContentType != InspectionTransport.MediaType ||
            request.Headers.ContentEncoding.Count != 0 || request.ContentLength is not { } length ||
            length < 1 || length > profile.MaximumRequestBytes)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var image = request.Headers[InspectionTransport.ImageHeader];
        if (image.Count != 1 || !string.Equals(image[0], identity, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }
        if (profileName == FerruleServiceProtocol.CompileProfile || request.Headers.ContainsKey(FerruleServiceProtocol.CompileToolchainHeader))
        {
            var toolchain = request.Headers[FerruleServiceProtocol.CompileToolchainHeader];
            if (toolchain.Count != 1 || toolchain[0] != options.CompileToolchain)
            {
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                return;
            }
        }
        if (profileName == FerruleServiceProtocol.CompiledProfile || request.Headers.ContainsKey(FerruleServiceProtocol.CompiledRuntimeHeader))
        {
            var runtime = request.Headers[FerruleServiceProtocol.CompiledRuntimeHeader];
            if (profileName != FerruleServiceProtocol.CompiledProfile || runtime.Count != 1 || runtime[0] != options.CompiledRuntimeIdentity)
            {
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                return;
            }
        }

        // Body readers count against capacity before allocating a frame. Cancellation
        // endpoints remain independent so a full replica can still settle its work.
        if (!await ingress.WaitAsync(0, context.RequestAborted).ConfigureAwait(false))
        {
            Busy(context);
            return;
        }
        try
        {
            await ReadAndRunAsync(context, profile, runner, operation!, length, service).ConfigureAwait(false);
        }
        finally
        {
            ingress.Release();
        }
    }

    private async Task ReadAndRunAsync(HttpContext context, DockerWorkerProfile profile,
        IInspectionWorkerRunner runner, string operation, long length, bool service)
    {
        var request = context.Request;
        var compileToolchain = options.CompileToolchain;
        var frame = new byte[length];
        try
        {
            using var bodyDeadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, shutdown.Token);
            bodyDeadline.CancelAfter(profile.MaximumWallTimeout);
            await request.Body.ReadExactlyAsync(frame, bodyDeadline.Token).ConfigureAwait(false);
            if (await request.Body.ReadAsync(new byte[1], bodyDeadline.Token).ConfigureAwait(false) != 0)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or Microsoft.AspNetCore.Http.BadHttpRequestException)
        {
            // An incomplete frame never becomes an operation.
            context.Abort();
            return;
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, shutdown.Token);
        switch (Admit(operation!, cancellation))
        {
            case Admission.Duplicate:
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                return;
            case Admission.Unavailable:
                Busy(context);
                return;
            case Admission.CanceledBeforeArrival:
                await RespondAsync(context, new([], WorkerProcessFailure.Canceled), service, compileToolchain).ConfigureAwait(false);
                return;
        }

        try
        {
            bool admitted;
            try
            {
                admitted = await slots.WaitAsync(options.AdmissionWait, cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await RespondAsync(context, new([], WorkerProcessFailure.Canceled), service, compileToolchain).ConfigureAwait(false);
                return;
            }

            if (!admitted)
            {
                Busy(context);
                return;
            }

            WorkerProcessResult result;
            try
            {
                result = await runner.RunAsync(frame, cancellation.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
            {
                // The runner has already closed its own admission; its settlement is unconfirmed.
                result = new([], WorkerProcessFailure.CleanupFailed);
            }
            finally
            {
                slots.Release();
            }

            await RespondAsync(context, result, service, compileToolchain).ConfigureAwait(false);
        }
        finally
        {
            lock (gate) active.Remove(operation!);
        }
    }

    /// <summary>Nothing ran; the caller may retry with a fresh operation on any instance.</summary>
    private static void Busy(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.RetryAfter = "1";
    }

    private Task Cancel(HttpContext context, string? profileName)
    {
        var operation = context.Request.RouteValues["operation"] as string;
        if (!InspectionTransport.IsOperationId(operation) || profileName is null || !runners.ContainsKey(profileName))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return Task.CompletedTask;
        }

        CancellationTokenSource? source;
        lock (gate)
        {
            if (!active.TryGetValue(operation!, out source) && !seen.Contains(operation!))
                Remember(canceledBeforeArrival, canceledOrder, operation!);
        }

        try
        {
            source?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The operation settled between lookup and cancellation.
        }

        context.Response.StatusCode = StatusCodes.Status204NoContent;
        return Task.CompletedTask;
    }

    private Admission Admit(string operation, CancellationTokenSource cancellation)
    {
        lock (gate)
        {
            if (stopping || options.Healthy?.Invoke() == false) return Admission.Unavailable;
            if (seen.Contains(operation)) return Admission.Duplicate;
            Remember(seen, seenOrder, operation);
            if (canceledBeforeArrival.Remove(operation)) return Admission.CanceledBeforeArrival;
            if (active.Count >= options.Capacity) return Admission.Unavailable;
            active.Add(operation, cancellation);
            return Admission.Accepted;
        }
    }

    private static void Remember(HashSet<string> set, Queue<string> order, string operation)
    {
        if (!set.Add(operation)) return;
        order.Enqueue(operation);
        if (order.Count > RememberedOperations) set.Remove(order.Dequeue());
    }

    private async Task RespondAsync(HttpContext context, WorkerProcessResult result, bool service, string? compileToolchain)
    {
        var body = result.Failure == WorkerProcessFailure.None ? result.Output : [];
        var response = context.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = InspectionTransport.MediaType;
        response.ContentLength = body.Length;
        response.Headers[InspectionTransport.ResultHeader] = InspectionTransport.Encode(result.Failure);
        response.Headers[InspectionTransport.ImageHeader] = identity;
        if (service) response.Headers[FerruleServiceProtocol.ProtocolHeader] = FerruleServiceProtocol.Version;
        if (compileToolchain is not null) response.Headers[FerruleServiceProtocol.CompileToolchainHeader] = compileToolchain;
        if (context.Request.RouteValues["profile"] as string == FerruleServiceProtocol.CompiledProfile && options.CompiledRuntimeIdentity is { } runtime)
            response.Headers[FerruleServiceProtocol.CompiledRuntimeHeader] = runtime;
        try
        {
            await response.Body.WriteAsync(body, context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException)
        {
            // The client left; the operation has settled regardless.
        }
    }

    /// <summary>Refuses new operations, cancels admitted ones through the runners and drains their answers.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        lock (gate) stopping = true;
        await shutdown.CancelAsync().ConfigureAwait(false);
        if (application is not null) await application.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        if (application is not null) await application.DisposeAsync().ConfigureAwait(false);
        shutdown.Dispose();
        slots.Dispose();
        ingress.Dispose();
    }

    private enum Admission
    {
        Accepted,
        Duplicate,
        Unavailable,
        CanceledBeforeArrival
    }
}
