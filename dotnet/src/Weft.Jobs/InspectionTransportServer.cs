using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Weft.MappingBackends.Workers;

namespace Weft.Jobs;

/// <summary>Trusted worker configuration: one private listener and the single pinned client certificate.</summary>
public sealed class InspectionTransportServerOptions
{
    public InspectionTransportServerOptions(IPEndPoint listen, X509Certificate2 serverCertificate, string clientCertificateSha256)
    {
        ArgumentNullException.ThrowIfNull(listen);
        ArgumentNullException.ThrowIfNull(serverCertificate);
        if (!serverCertificate.HasPrivateKey)
            throw new ArgumentException("The inspection worker certificate requires its private key.", nameof(serverCertificate));
        if (!InspectionTransport.IsCertificateDigest(clientCertificateSha256))
            throw new ArgumentException("The inspection client pin must be a lowercase SHA-256 digest.", nameof(clientCertificateSha256));
        Listen = listen;
        ServerCertificate = serverCertificate;
        ClientCertificateSha256 = clientCertificateSha256;
    }

    public IPEndPoint Listen { get; }
    /// <summary>Borrowed; its owner disposes it after the server has stopped.</summary>
    public X509Certificate2 ServerCertificate { get; }
    public string ClientCertificateSha256 { get; }
    internal TimeSpan AdmissionWait { get; init; } = InspectionTransport.AdmissionWait;
}

/// <summary>
/// Serves <c>weft.inspection-transport.v1</c> with this worker's own inspection runner. Only the pinned client
/// completes the TLS handshake. Each operation identity is admitted once; a cancellation that arrives before its
/// operation refuses it. A refused operation never reaches the runner, and an admitted one answers only after the
/// runner has settled it.
/// </summary>
public sealed class InspectionTransportServer(InspectionTransportServerOptions options, IInspectionWorkerRunner runner)
    : IHostedService, IAsyncDisposable
{
    private const int RememberedOperations = 4096;
    private readonly object gate = new();
    private readonly Dictionary<string, CancellationTokenSource> active = new(StringComparer.Ordinal);
    private readonly HashSet<string> seen = new(StringComparer.Ordinal);
    private readonly Queue<string> seenOrder = new();
    private readonly HashSet<string> canceledBeforeArrival = new(StringComparer.Ordinal);
    private readonly Queue<string> canceledOrder = new();
    private readonly SemaphoreSlim slot = new(1, 1);
    private readonly CancellationTokenSource shutdown = new();
    private WebApplication? application;
    private int disposed;
    private bool stopping;

    /// <summary>The actual bound port, for an explicitly selected ephemeral test listener.</summary>
    internal int Port { get; private set; }

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
            server.Limits.MaxRequestBodySize = InspectionTransport.MaximumFrameBytes;
            server.Limits.MaxConcurrentConnections = 4 * InspectionTransport.MaximumOperations;
            server.Listen(options.Listen, listener =>
            {
                listener.Protocols = HttpProtocols.Http1;
                listener.UseHttps(new HttpsConnectionAdapterOptions
                {
                    ServerCertificate = options.ServerCertificate,
                    ClientCertificateMode = ClientCertificateMode.RequireCertificate,
                    ClientCertificateValidation = (certificate, _, _) =>
                        InspectionTransport.HasDigest(certificate, options.ClientCertificateSha256),
                    CheckCertificateRevocation = false,
                    SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                });
            });
        });
        var built = builder.Build();
        built.MapGet(InspectionTransport.ReadyPath, (RequestDelegate)Ready);
        built.MapPost(InspectionTransport.OperationsPath + "{operation}", (RequestDelegate)RunAsync);
        built.MapPost(InspectionTransport.OperationsPath + "{operation}" + InspectionTransport.CancelSuffix, (RequestDelegate)Cancel);
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
        var address = built.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        Port = new Uri(address.Replace("://*", "://localhost", StringComparison.Ordinal)).Port;
    }

    private Task Ready(HttpContext context)
    {
        // Reaching any route proves the TLS handshake admitted the pinned client certificate.
        context.Response.StatusCode = StatusCodes.Status204NoContent;
        context.Response.Headers[InspectionTransport.ImageHeader] = runner.BuildIdentity;
        return Task.CompletedTask;
    }

    private async Task RunAsync(HttpContext context)
    {
        var request = context.Request;
        var operation = request.RouteValues["operation"] as string;
        if (!InspectionTransport.IsOperationId(operation) || request.ContentType != InspectionTransport.MediaType ||
            request.Headers.ContentEncoding.Count != 0 || request.ContentLength is not { } length ||
            length < 1 || length > InspectionTransport.MaximumFrameBytes)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var image = request.Headers[InspectionTransport.ImageHeader];
        if (image.Count != 1 || !string.Equals(image[0], runner.BuildIdentity, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }

        var frame = new byte[length];
        try
        {
            await request.Body.ReadExactlyAsync(frame, context.RequestAborted).ConfigureAwait(false);
            if (await request.Body.ReadAsync(new byte[1], context.RequestAborted).ConfigureAwait(false) != 0)
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
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            case Admission.CanceledBeforeArrival:
                await RespondAsync(context, new([], WorkerProcessFailure.Canceled)).ConfigureAwait(false);
                return;
        }

        try
        {
            bool admitted;
            try
            {
                admitted = await slot.WaitAsync(options.AdmissionWait, cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await RespondAsync(context, new([], WorkerProcessFailure.Canceled)).ConfigureAwait(false);
                return;
            }

            if (!admitted)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
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
                slot.Release();
            }

            await RespondAsync(context, result).ConfigureAwait(false);
        }
        finally
        {
            lock (gate) active.Remove(operation!);
        }
    }

    private Task Cancel(HttpContext context)
    {
        var operation = context.Request.RouteValues["operation"] as string;
        if (!InspectionTransport.IsOperationId(operation))
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
            if (stopping) return Admission.Unavailable;
            if (seen.Contains(operation)) return Admission.Duplicate;
            Remember(seen, seenOrder, operation);
            if (canceledBeforeArrival.Remove(operation)) return Admission.CanceledBeforeArrival;
            if (active.Count >= InspectionTransport.MaximumOperations) return Admission.Unavailable;
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

    private async Task RespondAsync(HttpContext context, WorkerProcessResult result)
    {
        var body = result.Failure == WorkerProcessFailure.None ? result.Output : [];
        var response = context.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = InspectionTransport.MediaType;
        response.ContentLength = body.Length;
        response.Headers[InspectionTransport.ResultHeader] = InspectionTransport.Encode(result.Failure);
        response.Headers[InspectionTransport.ImageHeader] = runner.BuildIdentity;
        try
        {
            await response.Body.WriteAsync(body, context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException)
        {
            // The client left; the operation has settled regardless.
        }
    }

    /// <summary>Refuses new operations, cancels admitted ones through the runner and drains their answers.</summary>
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
        slot.Dispose();
    }

    private enum Admission
    {
        Accepted,
        Duplicate,
        Unavailable,
        CanceledBeforeArrival
    }
}
