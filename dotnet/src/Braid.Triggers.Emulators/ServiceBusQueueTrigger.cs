using System.Text.RegularExpressions;
using Azure.Messaging.ServiceBus;
using Braid.Deployment.Triggers;

namespace Braid.Triggers.Emulators;

/// <summary>
/// Operator-private settings for one queue source connector. Every bound is explicit; nothing here has a default.
/// RetryBackoff is how long a message that must be retried is held before it is abandoned back to the broker.
/// </summary>
public sealed partial record ServiceBusQueueConnector
{
    public const int MaximumPrefetch = 64;
    public static readonly TimeSpan MaximumRetryBackoff = TimeSpan.FromSeconds(60);

    public ServiceBusQueueConnector(TriggerConnectorReference connector, string queueName, int prefetchCount, int maximumConcurrentCalls, TimeSpan retryBackoff, string? subscription = null)
    {
        ArgumentNullException.ThrowIfNull(connector);
        if (queueName is null || !QueuePattern().IsMatch(queueName) || subscription is not null && (subscription.Length > 50 || !QueuePattern().IsMatch(subscription)) || prefetchCount is < 0 or > MaximumPrefetch || maximumConcurrentCalls is < 1 or > 4 ||
            retryBackoff <= TimeSpan.Zero || retryBackoff > MaximumRetryBackoff)
            throw new ArgumentException("Invalid Service Bus queue connector.");
        Connector = connector; QueueName = queueName; Subscription = subscription; PrefetchCount = prefetchCount; MaximumConcurrentCalls = maximumConcurrentCalls; RetryBackoff = retryBackoff;
    }

    public TriggerConnectorReference Connector { get; }
    public string QueueName { get; }
    public string? Subscription { get; }
    public int PrefetchCount { get; }
    /// <summary>Within the runtime's limit of four concurrent executions.</summary>
    public int MaximumConcurrentCalls { get; }
    public TimeSpan RetryBackoff { get; }

    [GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9._-]{0,258}[A-Za-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex QueuePattern();
}

/// <summary>
/// PeekLock receiver for one enabled queue trigger. Locks renew only up to the trigger's explicit ceiling, settlement is never
/// automatic, and a failed or uncertain settlement is left to the next delivery, which recovers the durable work.
/// </summary>
public sealed class ServiceBusQueueTrigger : IAsyncDisposable
{
    private readonly ServiceBusProcessor processor;
    private readonly ServiceBusQueueConnector connector;
    private readonly Func<QueueMessage, CancellationToken, Task<QueueOrderDecision>> process;

    public ServiceBusQueueTrigger(ServiceBusClient client, ServiceBusQueueConnector connector, TriggerConfiguration configuration,
        Func<QueueMessage, CancellationToken, Task<QueueOrderDecision>> process)
    {
        ArgumentNullException.ThrowIfNull(client); ArgumentNullException.ThrowIfNull(connector); ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(process);
        if (configuration.Kind != TriggerKind.ServiceBusQueue || configuration.Source != connector.Connector)
            throw new ArgumentException("The connector is not this trigger's registered source.");
        // Missing explicit policies block the receiver as well as enablement.
        var policies = configuration.Policies ?? throw new ArgumentException("The trigger has no explicit operating bounds.");
        this.connector = connector; this.process = process;
        var options = new ServiceBusProcessorOptions
        {
            ReceiveMode = ServiceBusReceiveMode.PeekLock,
            AutoCompleteMessages = false,
            PrefetchCount = connector.PrefetchCount,
            MaxConcurrentCalls = connector.MaximumConcurrentCalls,
            MaxAutoLockRenewalDuration = policies.LockRenewalCeiling
        };
        processor = connector.Subscription is { } subscription ? client.CreateProcessor(connector.QueueName, subscription, options) : client.CreateProcessor(connector.QueueName, options);
        processor.ProcessMessageAsync += OnMessageAsync;
        processor.ProcessErrorAsync += _ => Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken token) => processor.StartProcessingAsync(token);
    public Task StopAsync(CancellationToken token) => processor.StopProcessingAsync(token);

    private async Task OnMessageAsync(ProcessMessageEventArgs args)
    {
        var message = args.Message;
        QueueOrderDecision decision;
        try { decision = await process(new(message.MessageId, message.ContentType, message.Body.ToMemory()), args.CancellationToken).ConfigureAwait(false); }
        // Infrastructure and authentication failures are retried for this message only; they never become a mapping outcome.
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException)) { decision = new(QueueSettlement.Abandon); }
        try
        {
            switch (decision.Settlement)
            {
                case QueueSettlement.Complete:
                    await args.CompleteMessageAsync(message, args.CancellationToken).ConfigureAwait(false);
                    break;
                case QueueSettlement.DeadLetter:
                    await args.DeadLetterMessageAsync(message, decision.DeadLetterReason!, decision.WorkId?.ToString("D"), args.CancellationToken).ConfigureAwait(false);
                    break;
                default:
                    try { await Task.Delay(connector.RetryBackoff, args.CancellationToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                    await args.AbandonMessageAsync(message, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                    break;
            }
        }
        // Settlement unknown: the lock lapses and the next delivery consults the durable work instead of running again.
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException)) { }
    }

    /// <summary>The local emulator only: a loopback development-emulator connection string from a private file.</summary>
    public static ServiceBusClient CreateEmulatorClient(string connectionString)
    {
        ServiceBusConnectionStringProperties properties;
        try { properties = ServiceBusConnectionStringProperties.Parse(connectionString); }
        catch (Exception error) when (error is FormatException or ArgumentException) { throw new ArgumentException("Invalid emulator connection string."); }
        if (!properties.Endpoint.IsLoopback ||
            !connectionString.Contains("UseDevelopmentEmulator=true", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The emulator connection string must name a loopback development emulator.");
        return new(connectionString, Options());
    }

    private static ServiceBusClientOptions Options() => new()
    {
        TransportType = ServiceBusTransportType.AmqpTcp,
        RetryOptions = new ServiceBusRetryOptions { Mode = ServiceBusRetryMode.Fixed, MaxRetries = 0, Delay = TimeSpan.FromSeconds(1), TryTimeout = TimeSpan.FromSeconds(30) }
    };

    public async ValueTask DisposeAsync() => await processor.DisposeAsync().ConfigureAwait(false);
    public override string ToString() => nameof(ServiceBusQueueTrigger);
}
