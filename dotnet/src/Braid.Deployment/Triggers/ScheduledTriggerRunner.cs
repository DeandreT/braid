using System.Text;
using Braid.Application;
using Braid.Domain;

namespace Braid.Deployment.Triggers;

/// <summary>
/// One listed source object version. The identity is a digest of the exact name and version, so it is private and stable; a
/// replaced object is a new version and a new identity, never the old order with new bytes.
/// </summary>
public sealed record TriggerSourceItem
{
    public TriggerSourceItem(string name, string version, long length)
    {
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(version) || length < 0) throw new ArgumentException("Invalid source item.");
        Name = name; Version = version; Length = length;
        Identity = "src:" + Sha256Digest.FromBytes(Encoding.UTF8.GetBytes(name + "\n" + version)).Value;
    }

    public string Name { get; }
    public string Version { get; }
    public long Length { get; }
    public string Identity { get; }
    public override string ToString() => nameof(TriggerSourceItem);
}

/// <summary>A null NextCursor means the full-prefix pass is complete.</summary>
public sealed record TriggerSourcePage(IReadOnlyList<TriggerSourceItem> Items, string? NextCursor);

/// <summary>Changed and Missing mean the listed version is no longer readable; it is never read as the old version.</summary>
public enum TriggerSourceReadStatus { Read, Changed, Missing, TooLarge, DigestMismatch }

public sealed record TriggerSourceRead(TriggerSourceReadStatus Status, byte[]? Content = null)
{
    public override string ToString() => nameof(TriggerSourceRead);
}

/// <summary>An operator-registered listing source. Listing and reads never delete, rename or acknowledge source objects.</summary>
public interface ITriggerSource
{
    TriggerConnectorReference Connector { get; }
    /// <summary>One bounded page of the registered prefix, excluding objects modified after the run's observation cutoff.</summary>
    Task<TriggerSourcePage> ListPageAsync(string? cursor, DateTimeOffset cutoff, CancellationToken token);
    /// <summary>The exact bytes of the listed version under a conditional read.</summary>
    Task<TriggerSourceRead> ReadAsync(TriggerSourceItem item, int maximumBytes, CancellationToken token);
}

/// <summary>Runs one admitted order through the pinned invocation core; production passes InvokeTriggeredAsync.</summary>
public delegate Task<TriggeredInvocationResult> TriggerOrderExecutor(ReleaseTargetReference target, Guid workId, MachineCaller caller, CancellationToken token);

/// <summary>
/// What one tick did, as metadata counts. TimeZoneDataMismatch: the host's tz data is not the reviewed version, so no new occurrence
/// was planned; unfinished runs and admitted orders still continue under their recorded instants and pins.
/// </summary>
public sealed record ScheduledTickResult(int Runs, int Missed, int Admitted, int Executed, bool TimeZoneDataMismatch = false);

/// <summary>
/// One schedule trigger tick: resume an unfinished run whose lease lapsed, record due occurrences (missed, skipped or run) for the
/// current schedule, scan each claimed run's source in a full-prefix pass that persists every page with its cursor, then execute
/// admitted orders, each in its own ledger boundary. Nothing is sent here; deliveries are only enqueued by the ledger.
/// </summary>
public sealed class ScheduledTriggerRunner(ITriggerWorkStore work, ITriggerRunStore runs, ITriggerSource source, TriggerOrderExecutor execute,
    TriggerDeliveryDispatcher deliveries, Func<string?> timeZoneDataVersion, TimeProvider time)
{
    /// <summary>The most orders one tick executes; the rest stay admitted for the next tick.</summary>
    public const int MaximumExecutionsPerTick = 64;
    // One object's orders always fit: a batch holds at most 256 orders decoded from at most 16 MiB.
    private const int MaximumOrdersPerPart = 1024;
    private const long MaximumBytesPerPart = 32L * 1024 * 1024;

    public async Task<ScheduledTickResult> TickAsync(ReleaseTargetReference target, Guid triggerId, MachineCaller caller, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(target); ArgumentNullException.ThrowIfNull(caller);
        var definition = await work.ReadDefinitionAsync(target, triggerId, token).ConfigureAwait(false);
        // Missing explicit choices, another validated caller, another source or an unserved route admit nothing.
        if (definition?.Configuration is not { Kind: TriggerKind.Schedule, Policies: not null, Schedule: { } schedule } configuration ||
            configuration.Caller != caller.Principal || configuration.ApplicationId != caller.ApplicationId || configuration.Source != source.Connector ||
            !configuration.Routes.All(deliveries.Serves))
            return new(0, 0, 0, 0);
        int scanned = 0, missed = 0, admitted = 0;
        var mismatch = timeZoneDataVersion() != schedule.TimeZoneDataVersion;
        var resumed = await runs.ResumeRunAsync(target, triggerId, caller, token).ConfigureAwait(false);
        if (resumed.Claim is { } unfinished) { scanned++; admitted += await ScanAsync(definition, caller, unfinished, token).ConfigureAwait(false); }
        if (!mismatch && await runs.ReadScheduleAsync(target, triggerId, token).ConfigureAwait(false) is { } state && state.Version == definition.Version)
        {
            var pending = new List<ScheduledOccurrence>();
            foreach (var planned in schedule.Plan(state.LatestRecorded, time.GetUtcNow(), schedule.TimeZoneDataVersion))
            {
                if (planned.Disposition == OccurrenceDisposition.Missed) { pending.Add(planned.Occurrence); continue; }
                var recorded = await runs.RecordOccurrencesAsync(definition, caller, pending, planned.Occurrence, token).ConfigureAwait(false);
                if (recorded.Status == TriggerOccurrenceStatus.Refused) { pending.Clear(); break; }
                missed += pending.Count; pending.Clear();
                if (recorded.Claim is { } claim) { scanned++; admitted += await ScanAsync(definition, caller, claim, token).ConfigureAwait(false); }
            }
            if (pending.Count > 0 && (await runs.RecordOccurrencesAsync(definition, caller, pending, null, token).ConfigureAwait(false)).Status != TriggerOccurrenceStatus.Refused)
                missed += pending.Count;
        }
        var executed = 0;
        foreach (var order in await runs.ReadPendingWorkAsync(target, triggerId, caller, MaximumExecutionsPerTick, token).ConfigureAwait(false))
        {
            await execute(target, order, caller, token).ConfigureAwait(false);
            executed++;
        }
        return new(scanned, missed, admitted, executed, mismatch);
    }

    /// <summary>Continues one claimed full-prefix pass from its persisted cursor; returns the orders admitted. A lost claim stops it.</summary>
    private async Task<int> ScanAsync(TriggerDefinition definition, MachineCaller caller, TriggerRunClaim claim, CancellationToken token)
    {
        var configuration = definition.Configuration;
        var policies = configuration.Policies!;
        var cursor = claim.Cursor;
        var admitted = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var page = await source.ListPageAsync(cursor, claim.ScheduledAt, token).ConfigureAwait(false);
            var known = await runs.ReadKnownSourcesAsync(configuration.Target, configuration.TriggerId, caller,
                [.. page.Items.Select(item => item.Identity).Distinct(StringComparer.Ordinal)], token).ConfigureAwait(false);
            var objects = new List<List<TriggerDiscoveredOrder>>();
            int changed = 0;
            List<(string Source, string Reason)> rejected = [];
            foreach (var item in page.Items.Where(item => !known.Contains(item.Identity)).DistinctBy(item => item.Identity))
            {
                var limit = configuration.Envelope == TriggerEnvelope.Batch ? TriggerBatchEnvelope.MaximumEnvelopeBytes : policies.MaximumMessageBytes;
                var read = await source.ReadAsync(item, limit, token).ConfigureAwait(false);
                if (read.Status is TriggerSourceReadStatus.Changed or TriggerSourceReadStatus.Missing) { changed++; continue; }
                var parsed = read.Status != TriggerSourceReadStatus.Read ? null : Orders(configuration, item, read.Content!);
                if (parsed is null) rejected.Add((item.Identity, read.Status switch { TriggerSourceReadStatus.TooLarge => "tooLarge", TriggerSourceReadStatus.DigestMismatch => "digestMismatch", _ => "invalidInput" }));
                else objects.Add(parsed);
            }
            var parts = Parts(objects);
            for (var index = 0; index < parts.Count; index++)
            {
                var last = index == parts.Count - 1;
                var result = await runs.RecordPageAsync(definition, caller, claim, cursor, last ? page.NextCursor : null, last && page.NextCursor is null,
                    last ? new(page.Items.Count, changed, rejected.Count, rejected) : new(0, 0, 0), parts[index], token).ConfigureAwait(false);
                // Stale, fenced, refused, no release or backlog pressure: the pass stays incomplete and resumes after its lease.
                if (result.Status != TriggerPageStatus.Recorded) return admitted;
                admitted += result.Orders.Count(order => order.Status == TriggerAdmissionStatus.Admitted);
            }
            if (page.NextCursor is null) return admitted;
            cursor = page.NextCursor;
        }
    }

    /// <summary>The complete orders of one source object, or null when it is not exactly the configured envelope and input.</summary>
    private static List<TriggerDiscoveredOrder>? Orders(TriggerConfiguration configuration, TriggerSourceItem item, byte[] content)
    {
        try
        {
            if (configuration.Envelope == TriggerEnvelope.SingleOrder)
                return [Order(configuration, new(configuration.SourceBindingId, item.Identity, configuration.Target, configuration.Caller, QueueOrderKind.SingleOrder, null), content)];
            return TriggerBatchEnvelope.Parse(content, configuration.Policies!.MaximumMessageBytes)?.Select(order => Order(configuration,
                new(configuration.SourceBindingId, item.Identity, configuration.Target, configuration.Caller, QueueOrderKind.KeyedChild, order.Key),
                order.CopyContent())).ToList();
        }
        catch (ArgumentException) { return null; }
    }

    internal static TriggerDiscoveredOrder Order(TriggerConfiguration configuration, QueueOrderIdentity identity, byte[] content) => new(identity, content,
        PilotRouteInputs.Request(configuration, content).Digest);

    /// <summary>
    /// Bounded transaction parts that never split one source object's orders, so an object is either wholly admitted or unknown.
    /// An empty page still records its cursor.
    /// </summary>
    private static List<List<TriggerDiscoveredOrder>> Parts(List<List<TriggerDiscoveredOrder>> objects)
    {
        var parts = new List<List<TriggerDiscoveredOrder>> { new() };
        long bytes = 0;
        foreach (var orders in objects)
        {
            var size = orders.Sum(order => (long)order.Length);
            if (parts[^1].Count > 0 && (parts[^1].Count + orders.Count > MaximumOrdersPerPart || bytes + size > MaximumBytesPerPart)) { parts.Add([]); bytes = 0; }
            parts[^1].AddRange(orders); bytes += size;
        }
        return parts;
    }
}
