using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using Braid.As2;
using Braid.Deployment.Triggers;
using Braid.Persistence.Postgres;
using Braid.Storage.FileSystem;
using Braid.Triggers.As2;
using Braid.Triggers.Sftp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Braid.MachineEndpoint;

/// <summary>One endpoint serves one enrolled target. Operator files register connectors; the database decides which work they may process.</summary>
internal sealed class SftpRouteWorker(MappingEndpointResources host, SftpRouteWorker.Plan plan, string connection) : BackgroundService
{
    internal sealed record Plan(Guid TriggerId, string TokenFile, string Inbox, string Host, int Port, string User, string HostKey, string SshKeyFile,
        string Source, long SourceVersion, Guid BindingId, string Destination, long DestinationVersion, Uri Endpoint, string Sender, string Recipient,
        string SigningCertFile, string SigningKeyFile, string EncryptionCertFile, string ReceiptCertFile, string SigningSha256, string EncryptionSha256,
        string ReceiptSha256, int IntervalSeconds);

    internal static void Register(IServiceCollection services, Func<string, string?> environment)
    {
        var path = environment("BRAID_ENDPOINT_SFTP_ROUTE_FILE");
        var login = environment("BRAID_ENDPOINT_TRIGGER_WORKER_CONNECTION_STRING_FILE");
        if (path is null && login is null) return;
        if (path is null || login is null) throw new ArgumentException("Both route and worker login files are required.");
        var p = JsonSerializer.Deserialize<Plan>(ProtectedHostFiles.ReadText(path, 16384), new JsonSerializerOptions(JsonSerializerDefaults.Web)
            { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true });
        if (p is null || p.TriggerId == Guid.Empty || p.BindingId == Guid.Empty || p.IntervalSeconds is < 5 or > 3600 ||
            !p.Endpoint.IsAbsoluteUri || p.Endpoint.Scheme != Uri.UriSchemeHttps || p.Endpoint.Fragment.Length + p.Endpoint.UserInfo.Length + p.Endpoint.Query.Length != 0)
            throw new ArgumentException("Invalid SFTP route registration.");
        var connection = MappingEndpointSettings.Connection(login, "braid_trigger_worker", environment);
        services.AddHostedService(sp => new SftpRouteWorker(sp.GetRequiredService<MappingEndpointResources>(), p, connection));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        static string Read(string path, int limit) => ProtectedHostFiles.ReadText(path, limit);
        using var signing = X509Certificate2.CreateFromPem(Read(plan.SigningCertFile, 65536), Read(plan.SigningKeyFile, 65536));
        using var encryption = X509Certificate2.CreateFromPem(Read(plan.EncryptionCertFile, 65536));
        using var receipt = X509Certificate2.CreateFromPem(Read(plan.ReceiptCertFile, 65536));
        static void Pin(X509Certificate2 cert, string digest)
        {
            if (!string.Equals(cert.GetCertHashString(HashAlgorithmName.SHA256), digest, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Partner certificate does not match the binding pin.");
        }
        Pin(signing, plan.SigningSha256); Pin(encryption, plan.EncryptionSha256); Pin(receipt, plan.ReceiptSha256);
        using var client = As2Connector.CreateTransportClient();
        await using var fences = NpgsqlDataSource.Create(connection);
        await using var store = new PostgresTriggerWorkStore(connection);
        var connector = new TriggerConnectorReference(plan.Destination, plan.DestinationVersion);
        var partner = new As2Partner(As2OutputDestination.BindingVersion(connector, plan.BindingId), plan.Endpoint, plan.Sender, plan.Recipient, signing, encryption, receipt);
        var dispatcher = new TriggerDeliveryDispatcher(store, [new As2OutputDestination(connector, plan.BindingId, partner, client, new PostgresAs2ClaimFenceStore(fences))], TimeProvider.System);
        var source = new SftpIntakeSource(new(plan.Source, plan.SourceVersion), plan.Inbox, plan.Host, plan.Port, plan.User, plan.HostKey, plan.SshKeyFile, Read);
        var runner = new ScheduledTriggerRunner(store, store, source, (target, id, caller, token) => host.Service.InvokeTriggeredAsync(store, target, id, caller, token),
            dispatcher, TriggerTimeZoneData.CurrentVersion, TimeProvider.System);
        var target = host.Settings.TargetReference;
        do
        {
            using var tick = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            tick.CancelAfter(TimeSpan.FromMinutes(2));
            try
            {
                var caller = await host.Validator.ValidateAsync(ProtectedHostFiles.ReadText(plan.TokenFile, 16384, true), tick.Token);
                var remaining = caller.ExpiresAt - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero) throw new InvalidOperationException("Route caller expired.");
                if (remaining < TimeSpan.FromMinutes(2)) tick.CancelAfter(remaining);
                await runner.TickAsync(target, plan.TriggerId, caller, tick.Token);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested && e is not (OutOfMemoryException or StackOverflowException)) { Console.Error.WriteLine("BraidTriggerIntakeDeferred"); }
            // Backlog does not depend on a new inbox page. Every send rechecks live grant and route in PostgreSQL.
            for (var i = 0; i < 16 && !stoppingToken.IsCancellationRequested; i++)
            {
                try { if (!await dispatcher.DispatchAsync(target, plan.BindingId, stoppingToken)) break; }
                catch (Exception e) when (!stoppingToken.IsCancellationRequested && e is not (OutOfMemoryException or StackOverflowException)) { Console.Error.WriteLine("BraidTriggerDeliveryDeferred"); break; }
            }
            await Task.Delay(TimeSpan.FromSeconds(plan.IntervalSeconds), stoppingToken);
        } while (!stoppingToken.IsCancellationRequested);
    }
}
