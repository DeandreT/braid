using System.Text.Json;
using System.Text.RegularExpressions;
using Braid.Application;
using Braid.Domain;

namespace Braid.Deployment.Triggers;

/// <summary>A Service Bus queue, or an explicit schedule that lists a registered source in bounded full-prefix passes.</summary>
public enum TriggerKind { ServiceBusQueue, Schedule }

/// <summary>
/// One message or file is one order, or it is an explicit <see cref="TriggerBatchEnvelope"/> of keyed orders. Payloads are never
/// split heuristically, and the envelope of a source binding never changes.
/// </summary>
public enum TriggerEnvelope { SingleOrder, Batch }

/// <summary>Blob and SFTP are create-only by deterministic key; HTTPS and topic ambiguity always needs manual resolution.</summary>
public enum TriggerDestinationKind { Blob, Sftp, Https, ServiceBusTopic, As2 }

/// <summary>An operator-registered connector ID and version. It names private configuration, never an endpoint or secret.</summary>
public sealed partial record TriggerConnectorReference
{
    public TriggerConnectorReference(string connectorId, long version)
    {
        if (connectorId is null || !ConnectorPattern().IsMatch(connectorId) || version <= 0)
            throw new ArgumentException("Invalid trigger connector reference.");
        ConnectorId = connectorId; Version = version;
    }
    public string ConnectorId { get; }
    public long Version { get; }
    [GeneratedRegex("^[a-z0-9][a-z0-9.-]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex ConnectorPattern();
}

/// <summary>One explicit output route. A null output name is the primary output.</summary>
public sealed record TriggerRoute
{
    public TriggerRoute(string? outputName, Guid destinationBindingId, TriggerDestinationKind kind, TriggerConnectorReference connector)
    {
        ArgumentNullException.ThrowIfNull(connector);
        if (destinationBindingId == Guid.Empty || !Enum.IsDefined(kind) ||
            outputName is not null && (outputName.Length is 0 or > 512 || outputName.Any(char.IsControl)))
            throw new ArgumentException("Invalid trigger output route.");
        OutputName = outputName; DestinationBindingId = destinationBindingId; Kind = kind; Connector = connector;
    }
    public string? OutputName { get; }
    public Guid DestinationBindingId { get; }
    public TriggerDestinationKind Kind { get; }
    public TriggerConnectorReference Connector { get; }
    /// <summary>Create-only conditional writes make a repeat with the same key and bytes safe.</summary>
    public bool CreateOnly => Kind is TriggerDestinationKind.Blob or TriggerDestinationKind.Sftp;
}

/// <summary>Required explicit operating bounds. Nothing here has a default; a configuration without them cannot be enabled.</summary>
public sealed record TriggerPolicies
{
    public static readonly TimeSpan MaximumLockRenewal = TimeSpan.FromSeconds(90);

    public TriggerPolicies(DeliveryRetryPolicy retry, TimeSpan lockRenewalCeiling, int maximumMessageBytes, TimeSpan payloadRetention,
        int maximumPendingWork, long maximumPendingBytes)
    {
        ArgumentNullException.ThrowIfNull(retry);
        if (lockRenewalCeiling <= TimeSpan.Zero || lockRenewalCeiling > MaximumLockRenewal || !WholeMilliseconds(lockRenewalCeiling) ||
            maximumMessageBytes is < 1 or > MachineInvocationPolicy.MaximumRetainedBytes ||
            !WholeSeconds(payloadRetention) || payloadRetention < retry.MaximumElapsed || payloadRetention > TimeSpan.FromDays(3650) ||
            maximumPendingWork is < 1 or > 1_000_000 || maximumPendingBytes < maximumMessageBytes || maximumPendingBytes > 1L << 40 ||
            !WholeMilliseconds(retry.AttemptWindow) || !WholeMilliseconds(retry.MaximumElapsed) || retry.RetryDelays.Any(delay => !WholeMilliseconds(delay)))
            throw new ArgumentException("Invalid trigger operating bounds.");
        Retry = retry; LockRenewalCeiling = lockRenewalCeiling; MaximumMessageBytes = maximumMessageBytes; PayloadRetention = payloadRetention;
        MaximumPendingWork = maximumPendingWork; MaximumPendingBytes = maximumPendingBytes;
    }

    public DeliveryRetryPolicy Retry { get; }
    public TimeSpan LockRenewalCeiling { get; }
    public int MaximumMessageBytes { get; }
    /// <summary>Payloads must outlive the whole delivery lifetime; pressure blocks admission rather than expiring needed bytes.</summary>
    public TimeSpan PayloadRetention { get; }
    public int MaximumPendingWork { get; }
    public long MaximumPendingBytes { get; }
    private static bool WholeMilliseconds(TimeSpan value) => value.Ticks % TimeSpan.TicksPerMillisecond == 0;
    private static bool WholeSeconds(TimeSpan value) => value > TimeSpan.Zero && value.Ticks % TimeSpan.TicksPerSecond == 0;
}

/// <summary>
/// One immutable trigger configuration version. It holds connector references and non-secret choices only.
/// Policies may be absent while drafting, but enablement refuses until they are explicit.
/// </summary>
public sealed class TriggerConfiguration
{
    public const string Schema = "braid.trigger-configuration.v1";
    public const int MaximumParameters = 64;
    public const int MaximumRoutes = 64;
    private readonly byte[] canonical;

    /// <param name="schedule">Required for, and only for, a schedule trigger.</param>
    public TriggerConfiguration(Guid triggerId, ReleaseTargetReference target, TriggerKind kind, Guid sourceBindingId, TriggerConnectorReference source,
        MachinePrincipal caller, Guid applicationId, string inputMediaType, IReadOnlyList<PreviewParameter> parameters, IReadOnlyList<TriggerRoute> routes,
        TriggerPolicies? policies, TriggerEnvelope envelope = TriggerEnvelope.SingleOrder, TriggerSchedule? schedule = null)
    {
        ArgumentNullException.ThrowIfNull(target); ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(parameters); ArgumentNullException.ThrowIfNull(routes);
        // CSV and FlexText inputs need explicit dialect or settings bindings, which this contract does not carry yet.
        if (triggerId == Guid.Empty || sourceBindingId == Guid.Empty || applicationId == Guid.Empty || !Enum.IsDefined(kind) ||
            !Enum.IsDefined(envelope) || (kind == TriggerKind.Schedule) != (schedule is not null) ||
            inputMediaType is null || !DocumentPreviewPolicy.AdmitsMediaType(inputMediaType) ||
            parameters.Count > MaximumParameters || parameters.Any(value => value is null || value.Name is not { Length: > 0 and <= 256 } ||
                !Enum.IsDefined(value.Kind) || value.Value is null || value.Value.Length > 4096) ||
            parameters.Select(value => value.Name).Distinct(StringComparer.Ordinal).Count() != parameters.Count ||
            routes.Count is 0 or > MaximumRoutes || routes.Any(value => value is null) ||
            routes.Select(value => (value.OutputName, value.DestinationBindingId)).Distinct().Count() != routes.Count)
            throw new ArgumentException("Invalid trigger configuration.");
        TriggerId = triggerId; Target = target; Kind = kind; SourceBindingId = sourceBindingId; Source = source; Caller = caller;
        ApplicationId = applicationId; InputMediaType = inputMediaType;
        Parameters = Array.AsReadOnly(parameters.OrderBy(value => value.Name, StringComparer.Ordinal).ToArray());
        Routes = Array.AsReadOnly(routes.OrderBy(value => value.OutputName is null ? 0 : 1).ThenBy(value => value.OutputName, StringComparer.Ordinal)
            .ThenBy(value => value.DestinationBindingId).ToArray());
        Policies = policies; Envelope = envelope; Schedule = schedule;
        canonical = Write();
    }

    public Guid TriggerId { get; }
    public ReleaseTargetReference Target { get; }
    public TriggerKind Kind { get; }
    /// <summary>The idempotency scope of the source. It survives configuration edits and is never derived from broker data.</summary>
    public Guid SourceBindingId { get; }
    public TriggerConnectorReference Source { get; }
    public MachinePrincipal Caller { get; }
    public Guid ApplicationId { get; }
    public string InputMediaType { get; }
    public IReadOnlyList<PreviewParameter> Parameters { get; }
    public IReadOnlyList<TriggerRoute> Routes { get; }
    public TriggerPolicies? Policies { get; }
    public TriggerEnvelope Envelope { get; }
    public TriggerSchedule? Schedule { get; }
    public bool Complete => Policies is not null;
    public byte[] CopyCanonicalBytes() => (byte[])canonical.Clone();
    public Sha256Digest Digest => Sha256Digest.FromBytes(canonical);

    /// <summary>True when every produced output has a route and every route names a produced output.</summary>
    public bool RoutesCover(IEnumerable<string?> outputNames)
    {
        ArgumentNullException.ThrowIfNull(outputNames);
        var produced = outputNames.ToArray();
        return produced.Distinct().Count() == produced.Length &&
            produced.ToHashSet().SetEquals(Routes.Select(value => value.OutputName));
    }

    private byte[] Write()
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteString("schema", Schema);
            json.WriteString("triggerId", TriggerId.ToString("D"));
            json.WriteString("organizationId", Target.Mapping.OrganizationId.ToString("D"));
            json.WriteString("projectId", Target.Mapping.ProjectId.ToString("D"));
            json.WriteString("mappingId", Target.Mapping.MappingId.ToString("D"));
            json.WriteString("environmentId", Target.Environment.EnvironmentId.ToString("D"));
            json.WriteNumber("environmentVersion", Target.Environment.Version);
            json.WriteString("targetId", Target.TargetId.ToString("D"));
            json.WriteString("kind", KindName(Kind));
            json.WriteString("sourceBindingId", SourceBindingId.ToString("D"));
            json.WriteString("sourceConnectorId", Source.ConnectorId);
            json.WriteNumber("sourceConnectorVersion", Source.Version);
            json.WriteString("callerProvider", Caller.Provider);
            json.WriteString("callerTenantId", Caller.TenantId.ToString("D"));
            json.WriteString("callerObjectId", Caller.ObjectId.ToString("D"));
            json.WriteString("applicationId", ApplicationId.ToString("D"));
            json.WriteString("envelope", Envelope == TriggerEnvelope.Batch ? "batch" : "singleOrder");
            json.WriteString("inputMediaType", InputMediaType);
            json.WriteStartArray("parameters");
            foreach (var parameter in Parameters)
            {
                json.WriteStartObject();
                json.WriteString("name", parameter.Name);
                json.WriteString("kind", parameter.Kind.ToString().ToLowerInvariant());
                json.WriteString("value", parameter.Value);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteStartArray("routes");
            foreach (var route in Routes)
            {
                json.WriteStartObject();
                json.WriteString("outputName", route.OutputName);
                json.WriteString("destinationBindingId", route.DestinationBindingId.ToString("D"));
                json.WriteString("kind", DestinationName(route.Kind));
                json.WriteString("connectorId", route.Connector.ConnectorId);
                json.WriteNumber("connectorVersion", route.Connector.Version);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            if (Policies is not { } policies) json.WriteNull("policies");
            else
            {
                json.WriteStartObject("policies");
                json.WriteNumber("totalSends", policies.Retry.TotalSends);
                json.WriteStartArray("retryDelaysMs");
                foreach (var delay in policies.Retry.RetryDelays) json.WriteNumberValue((long)delay.TotalMilliseconds);
                json.WriteEndArray();
                json.WriteNumber("attemptWindowMs", (long)policies.Retry.AttemptWindow.TotalMilliseconds);
                json.WriteNumber("maximumElapsedMs", (long)policies.Retry.MaximumElapsed.TotalMilliseconds);
                json.WriteNumber("lockRenewalMs", (long)policies.LockRenewalCeiling.TotalMilliseconds);
                json.WriteNumber("maximumMessageBytes", policies.MaximumMessageBytes);
                json.WriteNumber("payloadRetentionSeconds", (long)policies.PayloadRetention.TotalSeconds);
                json.WriteNumber("maximumPendingWork", policies.MaximumPendingWork);
                json.WriteNumber("maximumPendingBytes", policies.MaximumPendingBytes);
                json.WriteEndObject();
            }
            if (Schedule is { } schedule)
            {
                json.WriteStartObject("schedule");
                json.WriteString("grammar", CronExpression.Grammar);
                json.WriteString("cron", schedule.Cron.Text);
                json.WriteString("timeZone", schedule.TimeZoneId);
                json.WriteString("timeZoneDataVersion", schedule.TimeZoneDataVersion);
                json.WriteString("effectiveFrom", schedule.EffectiveFrom.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture));
                json.WriteString("gap", "skip");
                json.WriteString("fold", schedule.Fold switch { ScheduleFold.First => "first", ScheduleFold.Second => "second", _ => "both" });
                json.WriteString("overlap", "skipIfRunning");
                json.WriteString("catchUp", schedule.CatchUp == ScheduleCatchUp.None ? "none" : "bounded");
                json.WriteNumber("lookbackSeconds", (long)schedule.Lookback.TotalSeconds);
                if (schedule.MaximumCatchUp is { } maximum) json.WriteNumber("maximumCatchUp", maximum);
                else json.WriteNull("maximumCatchUp");
                json.WriteEndObject();
            }
            json.WriteEndObject();
        }
        return stream.ToArray();
    }

    /// <summary>Restores stored bytes, refusing anything that is not exactly this contract's canonical form.</summary>
    public static TriggerConfiguration Parse(ReadOnlySpan<byte> bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes.ToArray());
            var root = document.RootElement;
            if (root.GetProperty("schema").GetString() != Schema) throw new FormatException();
            var envelope = root.GetProperty("envelope").GetString() switch
            {
                "singleOrder" => TriggerEnvelope.SingleOrder,
                "batch" => TriggerEnvelope.Batch,
                _ => throw new FormatException()
            };
            var kind = ParseKind(root.GetProperty("kind").GetString());
            TriggerSchedule? schedule = null;
            if (kind == TriggerKind.Schedule)
            {
                var s = root.GetProperty("schedule");
                if (s.GetProperty("grammar").GetString() != CronExpression.Grammar || s.GetProperty("gap").GetString() != "skip" ||
                    s.GetProperty("overlap").GetString() != "skipIfRunning")
                    throw new FormatException();
                schedule = new(new(s.GetProperty("cron").GetString()!), s.GetProperty("timeZone").GetString()!, s.GetProperty("timeZoneDataVersion").GetString()!,
                    DateTimeOffset.ParseExact(s.GetProperty("effectiveFrom").GetString()!, "yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal),
                    ScheduleGap.Skip, s.GetProperty("fold").GetString() switch { "first" => ScheduleFold.First, "second" => ScheduleFold.Second, "both" => ScheduleFold.Both, _ => throw new FormatException() },
                    ScheduleOverlap.SkipIfRunning, s.GetProperty("catchUp").GetString() switch { "none" => ScheduleCatchUp.None, "bounded" => ScheduleCatchUp.Bounded, _ => throw new FormatException() },
                    TimeSpan.FromSeconds(s.GetProperty("lookbackSeconds").GetInt64()),
                    s.GetProperty("maximumCatchUp").ValueKind == JsonValueKind.Null ? null : s.GetProperty("maximumCatchUp").GetInt32());
            }
            var org = root.GetProperty("organizationId").GetGuid(); var project = root.GetProperty("projectId").GetGuid();
            var target = new ReleaseTargetReference(new(org, project, root.GetProperty("mappingId").GetGuid()),
                new(new(org, project), root.GetProperty("environmentId").GetGuid(), root.GetProperty("environmentVersion").GetInt64()),
                root.GetProperty("targetId").GetGuid());
            var parameters = root.GetProperty("parameters").EnumerateArray().Select(value => new PreviewParameter(value.GetProperty("name").GetString()!,
                Enum.Parse<PreviewParameterKind>(value.GetProperty("kind").GetString()!, ignoreCase: true), value.GetProperty("value").GetString()!)).ToArray();
            var routes = root.GetProperty("routes").EnumerateArray().Select(value => new TriggerRoute(value.GetProperty("outputName").GetString(),
                value.GetProperty("destinationBindingId").GetGuid(), Destination(value.GetProperty("kind").GetString()),
                new(value.GetProperty("connectorId").GetString()!, value.GetProperty("connectorVersion").GetInt64()))).ToArray();
            TriggerPolicies? policies = null;
            if (root.GetProperty("policies") is { ValueKind: JsonValueKind.Object } p)
            {
                var delays = p.GetProperty("retryDelaysMs").EnumerateArray().Select(value => TimeSpan.FromMilliseconds(value.GetInt64())).ToArray();
                policies = new(new DeliveryRetryPolicy(p.GetProperty("totalSends").GetInt32(), delays, TimeSpan.FromMilliseconds(p.GetProperty("attemptWindowMs").GetInt64()),
                        TimeSpan.FromMilliseconds(p.GetProperty("maximumElapsedMs").GetInt64())),
                    TimeSpan.FromMilliseconds(p.GetProperty("lockRenewalMs").GetInt64()), p.GetProperty("maximumMessageBytes").GetInt32(),
                    TimeSpan.FromSeconds(p.GetProperty("payloadRetentionSeconds").GetInt64()), p.GetProperty("maximumPendingWork").GetInt32(),
                    p.GetProperty("maximumPendingBytes").GetInt64());
            }
            var result = new TriggerConfiguration(root.GetProperty("triggerId").GetGuid(), target, kind,
                root.GetProperty("sourceBindingId").GetGuid(), new(root.GetProperty("sourceConnectorId").GetString()!, root.GetProperty("sourceConnectorVersion").GetInt64()),
                new(root.GetProperty("callerProvider").GetString()!, root.GetProperty("callerTenantId").GetGuid(), root.GetProperty("callerObjectId").GetGuid()),
                root.GetProperty("applicationId").GetGuid(), root.GetProperty("inputMediaType").GetString()!, parameters, routes, policies, envelope, schedule);
            if (!result.canonical.AsSpan().SequenceEqual(bytes)) throw new FormatException();
            return result;
        }
        catch (Exception error) when (error is JsonException or FormatException or KeyNotFoundException or InvalidOperationException or ArgumentException or OverflowException)
        {
            throw new InvalidOperationException("Stored trigger configuration is not canonical.", error);
        }
    }

    internal static string KindName(TriggerKind kind) => kind switch
    {
        TriggerKind.ServiceBusQueue => "serviceBusQueue",
        TriggerKind.Schedule => "schedule",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
    private static TriggerKind ParseKind(string? value) => value switch
    {
        "serviceBusQueue" => TriggerKind.ServiceBusQueue,
        "schedule" => TriggerKind.Schedule,
        _ => throw new FormatException()
    };
    internal static string DestinationName(TriggerDestinationKind kind) => kind switch
    {
        TriggerDestinationKind.Blob => "blob",
        TriggerDestinationKind.Sftp => "sftp",
        TriggerDestinationKind.Https => "https",
        TriggerDestinationKind.ServiceBusTopic => "serviceBusTopic",
        TriggerDestinationKind.As2 => "as2",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
    private static TriggerDestinationKind Destination(string? value) => value switch
    {
        "blob" => TriggerDestinationKind.Blob,
        "sftp" => TriggerDestinationKind.Sftp,
        "https" => TriggerDestinationKind.Https,
        "serviceBusTopic" => TriggerDestinationKind.ServiceBusTopic,
        "as2" => TriggerDestinationKind.As2,
        _ => throw new FormatException()
    };

    public override string ToString() => nameof(TriggerConfiguration);
}
