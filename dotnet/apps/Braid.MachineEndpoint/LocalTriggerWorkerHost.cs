using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Braid.Storage.FileSystem;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Hosting;
using Braid.Application;
using Braid.Deployment;
using Braid.Deployment.Triggers;
using Braid.Domain;
using Braid.Persistence.Postgres;
using Braid.Storage.Blob;
using Braid.Triggers.Emulators;
using Braid.Triggers.Sftp;

namespace Braid.MachineEndpoint;

/// <summary>
/// The operator-private trigger host file for this endpoint's one target. It names connectors, private-file references and receive
/// bounds only; trigger definitions, routes and policies stay in the database and are re-read for every order.
/// </summary>
internal sealed record TriggerHostPlan(string TokenFile, string? ServiceBusEmulatorConnectionFile,
    IReadOnlyList<TriggerHostPlan.Queue> Queues, IReadOnlyList<TriggerHostPlan.Schedule> Schedules, IReadOnlyList<TriggerHostPlan.Destination> Destinations)
{
    internal const string FileSetting = "BRAID_ENDPOINT_LOCAL_TRIGGER_HOST_FILE";
    internal const string ConnectionSetting = "BRAID_ENDPOINT_LOCAL_TRIGGER_CONNECTION_STRING";
    private const int MaximumFileCharacters = 256 * 1024;
    private const int MaximumEntries = 64;

    internal sealed record Queue(Guid TriggerId, ServiceBusQueueConnector Connector);
    internal sealed record Schedule(Guid TriggerId, TriggerConnectorReference Connector, BlobStorageOptions Storage, string Prefix, int PageSize, TimeSpan Tick);
    internal sealed record Destination(Guid BindingId, string Kind, object Settings);

    /// <summary>Null when this endpoint hosts no triggers. Both settings are present together or not at all.</summary>
    internal static (TriggerHostPlan Plan, string Connection)? Read(Func<string, string?> environment, Func<string, int, string> privateReader)
    {
        var file = environment(FileSetting) is { Length: > 0 } path ? path : null;
        var connection = environment(ConnectionSetting + "_FILE") is { Length: > 0 } secret ? secret : null;
        if (file is null && connection is null) return null;
        if (file is null || connection is null || !Path.IsPathFullyQualified(file) || !Path.IsPathFullyQualified(connection))
            throw new ArgumentException("Trigger hosting requires both its host file and its restricted worker connection file.");
        return (Parse(privateReader(file, MaximumFileCharacters)), MappingEndpointSettings.Connection(connection, "braid_trigger_worker", environment));
    }

    internal static TriggerHostPlan Parse(string json)
    {
        HostFile? value;
        try { value = JsonSerializer.Deserialize<HostFile>(json, Options); }
        catch (JsonException) { throw Invalid(); }
        if (value is not { Version: 1, TokenFile: { } tokenFile } || !Path.IsPathFullyQualified(tokenFile) ||
            value.Queues.Count + value.Schedules.Count is 0 or > MaximumEntries || value.Destinations.Count > MaximumEntries ||
            value.Queues.Count > 0 && value.ServiceBusEmulatorConnectionFile is null ||
            value.ServiceBusEmulatorConnectionFile is { } emulator && !Path.IsPathFullyQualified(emulator))
            throw Invalid();
        var queues = value.Queues.Select(queue => new Queue(Id(queue.TriggerId), new(new(queue.ConnectorId, queue.ConnectorVersion), queue.QueueName,
            queue.PrefetchCount, queue.MaximumConcurrentCalls, TimeSpan.FromSeconds(queue.RetryBackoffSeconds), queue.Subscription))).ToArray();
        var schedules = value.Schedules.Select(schedule => schedule.TickSeconds is < 5 or > 3600 ? throw Invalid()
            : new Schedule(Id(schedule.TriggerId), new(schedule.ConnectorId, schedule.ConnectorVersion), Storage(schedule.ServiceUri, schedule.Container,
                schedule.Credential), schedule.Prefix, schedule.PageSize, TimeSpan.FromSeconds(schedule.TickSeconds))).ToArray();
        var destinations = value.Destinations.Select(item =>
        {
            var connector = new TriggerConnectorReference(item.ConnectorId, item.ConnectorVersion);
            object settings = item.Kind switch
            {
                "https" => new HttpsDestinationConnector(connector, new Uri(item.Endpoint ?? throw Invalid()), hosted: true,
                    item.AcceptedStatuses ?? throw Invalid(), item.RejectedStatuses ?? [], item.SigningKeyFile ?? throw Invalid()),
                "serviceBusTopic" when value.ServiceBusEmulatorConnectionFile is not null =>
                    item.TopicName is { Length: > 0 and <= 260 } topic ? (connector, topic) : throw Invalid(),
                "sftp" => new SftpDestinationConnector(connector, item.Host ?? throw Invalid(), item.Port ?? 22, item.UserName ?? throw Invalid(),
                    item.HostKeySha256 ?? throw Invalid(), item.PasswordFile, item.PrivateKeyFile, item.Root ?? throw Invalid(), item.Prefix ?? throw Invalid(),
                    SftpPublishContract.NoReplaceRename),
                "blob" => (connector, Storage(item.ServiceUri, item.Container, item.Credential), item.Prefix ?? throw Invalid()),
                _ => throw Invalid()
            };
            return new Destination(Id(item.BindingId), item.Kind, settings);
        }).ToArray();
        if (queues.Select(item => item.TriggerId).Concat(schedules.Select(item => item.TriggerId)).Distinct().Count() != queues.Length + schedules.Length ||
            destinations.Select(item => item.BindingId).Distinct().Count() != destinations.Length)
            throw Invalid();
        return new(tokenFile, value.ServiceBusEmulatorConnectionFile, queues, schedules, destinations);
    }

    private static Guid Id(Guid value) => value == Guid.Empty ? throw Invalid() : value;
    private static BlobStorageOptions Storage(string? serviceUri, string? container, string? credential) =>
        new(new Uri(serviceUri ?? throw Invalid()), container ?? throw Invalid(), credential == "emulator" ? BlobStorageCredentialKind.Emulator : throw Invalid());
    private static ArgumentException Invalid() => new("Invalid trigger host file.");

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        MaxDepth = 8
    };

    private sealed record HostFile(int Version, string TokenFile, string? ServiceBusEmulatorConnectionFile,
        IReadOnlyList<QueueEntry> Queues, IReadOnlyList<ScheduleEntry> Schedules, IReadOnlyList<DestinationEntry> Destinations);
    private sealed record QueueEntry(Guid TriggerId, string ConnectorId, long ConnectorVersion, string QueueName, int PrefetchCount, int MaximumConcurrentCalls,
        int RetryBackoffSeconds, string? Subscription = null);
    private sealed record ScheduleEntry(Guid TriggerId, string ConnectorId, long ConnectorVersion, string ServiceUri, string Container, string Credential,
        string Prefix, int PageSize, int TickSeconds);
    private sealed record DestinationEntry(Guid BindingId, string Kind, string ConnectorId, long ConnectorVersion, string? Endpoint = null,
        IReadOnlyList<int>? AcceptedStatuses = null, IReadOnlyList<int>? RejectedStatuses = null, string? SigningKeyFile = null, string? TopicName = null,
        string? Host = null, int? Port = null, string? UserName = null, string? HostKeySha256 = null, string? PasswordFile = null, string? PrivateKeyFile = null,
        string? Root = null, string? Prefix = null, string? ServiceUri = null, string? Container = null, string? Credential = null);
}

/// <summary>
/// Runs this target's triggers inside the endpoint that owns its release invocation path: queue receivers, schedule ticks and the
/// delivery outbox. Every order still passes the database's trigger, caller-grant and release checks; nothing here is trusted.
/// </summary>
internal sealed class TriggerWorkerHost(TriggerHostPlan plan, string connection, MappingEndpointResources resources, Func<string, int, string> privateReader,
    TimeProvider time) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var store = new PostgresTriggerWorkStore(connection);
        await using var bus = plan.ServiceBusEmulatorConnectionFile is { } emulator
            ? ServiceBusQueueTrigger.CreateEmulatorClient(privateReader(emulator, 4096).Trim())
            : null;
        Task<MachineCaller> Caller(CancellationToken token) => resources.Validator.ValidateAsync(privateReader(plan.TokenFile, 16384).TrimEnd('\r', '\n'), token);
        var destinations = plan.Destinations.Select(item => item.Settings switch
        {
            HttpsDestinationConnector https => (IOutputDestination)new HttpsOutputDestination(https, privateReader, time),
            SftpDestinationConnector sftp => new SftpOutputDestination(sftp, privateReader),
            ValueTuple<TriggerConnectorReference, string> topic => new ServiceBusTopicDestination(topic.Item1, bus!, topic.Item2),
            ValueTuple<TriggerConnectorReference, BlobStorageOptions, string> blob => new BlobOutputDestination(blob.Item1, blob.Item2, blob.Item3),
            _ => throw new InvalidOperationException("Unknown trigger destination.")
        }).ToArray();
        TriggerOrderExecutor execute = (target, work, caller, token) => resources.Service.InvokeTriggeredAsync(store, target, work, caller, token);
        var zoneData = HostTimeZoneDataVersion();
        await new TriggerWorkerLoop(resources.Settings.TargetReference, store, bus, plan.Queues.Select(item => (item.TriggerId, item.Connector)).ToArray(),
            plan.Schedules.Select(item => (item.TriggerId, item.Tick, (ITriggerSource)new BlobTriggerSource(item.Connector, item.Storage, item.Prefix, item.PageSize)))
                .ToArray(),
            destinations, plan.Destinations.Select(item => item.BindingId).ToArray(), execute, Caller, () => zoneData, time).RunAsync(stoppingToken);
    }

    /// <summary>The image's IANA data release; a schedule reviewed against another release refuses its tick visibly.</summary>
    internal static string? HostTimeZoneDataVersion() => HostTimeZoneDataVersion("/usr/share/zoneinfo/tzdata.zi");

    /// <summary>The release named in one zic data file's header.</summary>
    internal static string? HostTimeZoneDataVersion(string path)
    {
        try
        {
            using var reader = File.OpenText(path);
            return reader.ReadLine() is { } line && line.StartsWith("# version ", StringComparison.Ordinal) ? line[10..].Trim() : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }
}

/// <summary>
/// The trigger work loop over already built parts: queue receivers start once their definition is readable, schedule ticks run
/// when due, and each destination's outbox is drained a bounded amount per pass. Failures leave durable work retryable.
/// </summary>
internal sealed class TriggerWorkerLoop(ReleaseTargetReference target, PostgresTriggerWorkStore store, ServiceBusClient? bus,
    IReadOnlyList<(Guid TriggerId, ServiceBusQueueConnector Connector)> queues, IReadOnlyList<(Guid TriggerId, TimeSpan Tick, ITriggerSource Source)> schedules,
    IReadOnlyList<IOutputDestination> destinations, IReadOnlyList<Guid> destinationBindings, TriggerOrderExecutor execute,
    Func<CancellationToken, Task<MachineCaller>> callers, Func<string?> timeZoneDataVersion, TimeProvider time)
{
    internal static readonly TimeSpan Idle = TimeSpan.FromSeconds(1);
    private const int MaximumDispatchesPerPass = 16;

    internal async Task RunAsync(CancellationToken stoppingToken)
    {
        var dispatcher = new TriggerDeliveryDispatcher(store, destinations, time);
        var processor = new QueueOrderProcessor(store, execute, dispatcher, store);
        var runners = schedules.Select(item => (item.TriggerId, item.Tick,
            Runner: new ScheduledTriggerRunner(store, store, item.Source, execute, dispatcher, timeZoneDataVersion, time))).ToArray();
        var due = runners.ToDictionary(item => item.TriggerId, _ => time.GetUtcNow());
        var receivers = new Dictionary<Guid, (long Version, ServiceBusQueueTrigger Receiver)>();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                foreach (var queue in queues)
                    await Guarded(async () =>
                    {
                        // A receiver starts only once the trigger's registered definition names this exact connector and has bounds.
                        var current = await store.ReadDefinitionAsync(target, queue.TriggerId, stoppingToken);
                        if (receivers.TryGetValue(queue.TriggerId, out var prior))
                        {
                            if (prior.Version == current?.Version) return;
                            await prior.Receiver.StopAsync(stoppingToken);
                            await prior.Receiver.DisposeAsync();
                            receivers.Remove(queue.TriggerId);
                        }
                        if (current is not { Configuration: { } configuration }) return;
                        var receiver = new ServiceBusQueueTrigger(bus ?? throw new InvalidOperationException("No Service Bus client."), queue.Connector,
                            configuration, async (message, token) => await processor.ProcessAsync(target, queue.TriggerId, await callers(token), message, token, current.Version));
                        try { await receiver.StartAsync(stoppingToken); } catch { await receiver.DisposeAsync(); throw; }
                        receivers[queue.TriggerId] = (current.Version, receiver);
                    });
                foreach (var (triggerId, tick, runner) in runners.Where(entry => due[entry.TriggerId] <= time.GetUtcNow()))
                {
                    due[triggerId] = time.GetUtcNow() + tick;
                    await Guarded(async () => await runner.TickAsync(target, triggerId, await callers(stoppingToken), stoppingToken));
                }
                foreach (var binding in destinationBindings)
                    await Guarded(async () =>
                    {
                        for (var sent = 0; sent < MaximumDispatchesPerPass && await dispatcher.DispatchAsync(target, binding, stoppingToken); sent++) { }
                    });
                await Task.Delay(Idle, time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            foreach (var (_, receiver) in receivers.Values)
            {
                try { await receiver.StopAsync(CancellationToken.None); }
                finally { await receiver.DisposeAsync(); }
            }
        }

        async Task Guarded(Func<Task> work)
        {
            // Infrastructure, token and connector failures leave durable work retryable; details never reach logs.
            try { await work(); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException)) { Console.Error.WriteLine("BraidTriggerWorkRetryable"); }
        }
    }
}
