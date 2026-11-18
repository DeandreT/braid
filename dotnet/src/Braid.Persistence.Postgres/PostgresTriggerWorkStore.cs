using System.Data;
using Npgsql;
using NpgsqlTypes;
using Braid.Application;
using Braid.Application.Regression;
using Braid.Deployment;
using Braid.Deployment.Triggers;
using Braid.Domain;

namespace Braid.Persistence.Postgres;

/// <summary>
/// Order admission, the pinned ledger and the delivery outbox through the restricted trigger-worker login.
/// It has no table access; every rule is enforced by the owner functions and their guards.
/// </summary>
public sealed class PostgresTriggerWorkStore(string connectionString) : ITriggerWorkStore, ITriggeredInvocationLedger, ITriggerDeliveryStore, ITriggerRunStore,
    IAsyncDisposable
{
    private readonly NpgsqlDataSource source = NpgsqlDataSource.Create(PostgresConfiguration.Normalize(connectionString));

    public async Task<TriggerDefinition?> ReadDefinitionAsync(ReleaseTargetReference target, Guid triggerId, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        await using var tx = await BeginAsync(target.Mapping.OrganizationId, false, token);
        await using var command = tx.CreateCommand("SELECT * FROM braid.read_trigger_definition(@org,@project,@mapping,@environment,@version,@target,@trigger)");
        Scope(command, target);
        command.Parameters.AddWithValue("trigger", triggerId);
        TriggerDefinition? result = null;
        await using (var rows = await command.ExecuteReaderAsync(token))
        {
            if (await rows.ReadAsync(token))
            {
                var configuration = TriggerConfiguration.Parse(rows.GetFieldValue<byte[]>(1));
                if (configuration.TriggerId != triggerId || configuration.Target != target) throw Invalid();
                result = new(configuration, rows.GetInt64(0));
            }
            if (await rows.ReadAsync(token)) throw Invalid();
        }
        await tx.CommitAsync(token);
        return result;
    }

    public async Task<TriggerAdmissionResult> AdmitAsync(TriggerDefinition definition, QueueOrderIdentity identity, MachineCaller caller,
        ReadOnlyMemory<byte> source, Sha256Digest requestDigest, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(definition); ArgumentNullException.ThrowIfNull(identity); ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(requestDigest);
        var configuration = definition.Configuration;
        if (identity.Target != configuration.Target || identity.SourceBindingId != configuration.SourceBindingId || identity.Principal != caller.Principal ||
            source.Length > MachineInvocationPolicy.MaximumRetainedBytes)
            throw new ArgumentException("The order does not belong to this trigger's source, target and validated caller.");
        var target = configuration.Target;
        await using var tx = await BeginAsync(target.Mapping.OrganizationId, true, token);
        await using var command = tx.CreateCommand("SELECT * FROM braid.admit_trigger_work(@org,@project,@mapping,@environment,@version,@target,@trigger,@configuration,@issuer,@tenant,@principal,@application,@source,@key,@digest,@body)");
        Scope(command, target);
        command.Parameters.AddWithValue("trigger", configuration.TriggerId);
        command.Parameters.AddWithValue("configuration", definition.Version);
        Caller(command, caller);
        command.Parameters.AddWithValue("source", identity.MessageId);
        command.Parameters.AddWithValue("key", identity.ChildKey ?? "");
        command.Parameters.AddWithValue("digest", requestDigest.Value);
        command.Parameters.AddWithValue("body", NpgsqlDbType.Bytea, source.ToArray());
        TriggerAdmissionResult result;
        await using (var rows = await command.ExecuteReaderAsync(token))
        {
            if (!await rows.ReadAsync(token)) throw Invalid();
            var status = rows.GetInt32(0);
            Guid? work = rows.IsDBNull(1) ? null : rows.GetGuid(1);
            if (status is < 0 or > 5 || (status is 0 or 1) != (work is not null)) throw Invalid();
            result = new((TriggerAdmissionStatus)status, work);
            if (await rows.ReadAsync(token)) throw Invalid();
        }
        await tx.CommitAsync(token);
        return result;
    }

    public async Task<TriggerWorkClaim> ClaimPinnedAsync(ReleaseTargetReference target, Guid workId, MachineCaller caller, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(target); ArgumentNullException.ThrowIfNull(caller);
        await using var tx = await BeginAsync(target.Mapping.OrganizationId, true, token);
        await using var command = tx.CreateCommand("SELECT * FROM braid.claim_trigger_work(@org,@work,@issuer,@tenant,@principal,@application)");
        command.Parameters.AddWithValue("org", target.Mapping.OrganizationId);
        command.Parameters.AddWithValue("work", workId);
        Caller(command, caller);
        TriggerWorkClaim result;
        await using (var rows = await command.ExecuteReaderAsync(token))
        {
            if (!await rows.ReadAsync(token)) throw Invalid();
            result = rows.GetInt32(0) switch
            {
                0 => Claimed(rows, target),
                1 => new(TriggerClaimStatus.Recorded, outcome: PostgresInvocationLedger.Outcome(rows.GetString(17))),
                2 => new(TriggerClaimStatus.InProgress),
                3 => new(TriggerClaimStatus.Refused),
                4 => new(TriggerClaimStatus.Fenced),
                5 => new(TriggerClaimStatus.ReleaseSuperseded),
                _ => throw Invalid()
            };
            if (await rows.ReadAsync(token)) throw Invalid();
        }
        await tx.CommitAsync(token);
        return result;
    }

    private static TriggerWorkClaim Claimed(NpgsqlDataReader rows, ReleaseTargetReference target)
    {
        var configuration = TriggerConfiguration.Parse(rows.GetFieldValue<byte[]>(5));
        if (configuration.Target != target || rows.IsDBNull(6)) throw Invalid();
        var pin = new TriggerWorkPin(new MachineInvocationProvenance(rows.GetGuid(7), rows.GetGuid(8), rows.GetGuid(9), Sha256Digest.Parse(rows.GetString(10)),
            Sha256Digest.Parse(rows.GetString(11)), rows.GetGuid(12), rows.GetString(13)), new PrincipalId(rows.GetGuid(14)), rows.GetInt64(15), rows.GetGuid(16));
        return new(TriggerClaimStatus.Claimed, rows.GetInt32(1), rows.GetGuid(2), pin, rows.GetGuid(3), Sha256Digest.Parse(rows.GetString(4)), configuration,
            rows.GetFieldValue<byte[]>(6));
    }

    public async Task<TriggerFinishStatus> FinishAndEnqueueAsync(ReleaseTargetReference target, Guid workId, Guid claimId, Guid applicationId,
        MachineInvocationOutcome outcome, MachineInvocationProvenance? provenance, string? clock, byte[]? response, RegressionOutputSet? outputs,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (workId == Guid.Empty || claimId == Guid.Empty || applicationId == Guid.Empty || !Enum.IsDefined(outcome) ||
            outcome is MachineInvocationOutcome.Interrupted or MachineInvocationOutcome.AttemptsExhausted ||
            (outcome == MachineInvocationOutcome.Completed) != (response is not null) || (response is null) != (outputs is null) ||
            outcome == MachineInvocationOutcome.Completed && (provenance is null || clock is null || outputs!.Outputs.Count == 0) ||
            response?.Length is < 1 or > MachineInvocationPolicy.MaximumRetainedBytes)
            throw new ArgumentException("Invalid trigger invocation outcome.");
        await using var tx = await BeginAsync(target.Mapping.OrganizationId, true, token);
        await using var command = tx.CreateCommand("SELECT braid.finish_trigger_work(@org,@work,@claim,@application,@finding,@release,@qualification,@build,@intent,@artifact,@revision,@image,@clock,@body,@names,@media,@bodies)");
        command.Parameters.AddWithValue("org", target.Mapping.OrganizationId);
        command.Parameters.AddWithValue("work", workId);
        command.Parameters.AddWithValue("claim", claimId);
        command.Parameters.AddWithValue("application", applicationId);
        command.Parameters.AddWithValue("finding", PostgresInvocationLedger.Name(outcome));
        command.Parameters.AddWithValue("release", NpgsqlDbType.Uuid, (object?)provenance?.ReleaseId ?? DBNull.Value);
        command.Parameters.AddWithValue("qualification", NpgsqlDbType.Uuid, (object?)provenance?.QualificationId ?? DBNull.Value);
        command.Parameters.AddWithValue("build", NpgsqlDbType.Uuid, (object?)provenance?.BuildId ?? DBNull.Value);
        command.Parameters.AddWithValue("intent", NpgsqlDbType.Text, (object?)provenance?.BuildIntentDigest.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("artifact", NpgsqlDbType.Text, (object?)provenance?.ArtifactDigest.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("revision", NpgsqlDbType.Uuid, (object?)provenance?.RevisionId ?? DBNull.Value);
        command.Parameters.AddWithValue("image", NpgsqlDbType.Text, (object?)provenance?.RuntimeImage ?? DBNull.Value);
        command.Parameters.AddWithValue("clock", NpgsqlDbType.Text, (object?)clock ?? DBNull.Value);
        command.Parameters.AddWithValue("body", NpgsqlDbType.Bytea, (object?)response ?? DBNull.Value);
        command.Parameters.AddWithValue("names", NpgsqlDbType.Array | NpgsqlDbType.Text,
            (object?)outputs?.Outputs.Select(output => output.Key.IsPrimary ? "" : output.Key.Name).ToArray() ?? DBNull.Value);
        command.Parameters.AddWithValue("media", NpgsqlDbType.Array | NpgsqlDbType.Text, (object?)outputs?.Outputs.Select(output => output.MediaType).ToArray() ?? DBNull.Value);
        command.Parameters.AddWithValue("bodies", NpgsqlDbType.Array | NpgsqlDbType.Bytea, (object?)outputs?.Outputs.Select(output => output.CopyBytes()).ToArray() ?? DBNull.Value);
        var result = await command.ExecuteScalarAsync(token) switch
        {
            0 => TriggerFinishStatus.Recorded,
            1 => TriggerFinishStatus.Fenced,
            2 => TriggerFinishStatus.Superseded,
            4 => TriggerFinishStatus.ReleaseSuperseded,
            _ => throw Invalid()
        };
        await tx.CommitAsync(token);
        return result;
    }

    public async Task<TriggerDeliveryClaim> ClaimAsync(ReleaseTargetReference target, Guid destinationBindingId, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        await using var tx = await BeginAsync(target.Mapping.OrganizationId, true, token);
        await using var command = tx.CreateCommand("SELECT * FROM braid.claim_trigger_delivery(@org,@project,@mapping,@environment,@version,@target,@destination)");
        Scope(command, target);
        command.Parameters.AddWithValue("destination", destinationBindingId);
        TriggerDeliveryClaim result;
        await using (var rows = await command.ExecuteReaderAsync(token))
        {
            if (!await rows.ReadAsync(token)) throw Invalid();
            result = rows.GetInt32(0) switch
            {
                0 => Delivery(rows, destinationBindingId),
                1 => new(TriggerDeliveryClaimStatus.None),
                2 => new(TriggerDeliveryClaimStatus.Settled),
                _ => throw Invalid()
            };
            if (await rows.ReadAsync(token)) throw Invalid();
        }
        await tx.CommitAsync(token);
        return result;
    }

    private static TriggerDeliveryClaim Delivery(NpgsqlDataReader rows, Guid destination)
    {
        var content = rows.GetFieldValue<byte[]>(9);
        var connector = new TriggerConnectorReference(rows.GetString(11), rows.GetInt64(12));
        var output = rows.GetString(13);
        var route = new TriggerRoute(output.Length == 0 ? null : output, destination, rows.GetString(10) switch
        {
            "blob" => TriggerDestinationKind.Blob,
            "sftp" => TriggerDestinationKind.Sftp,
            "https" => TriggerDestinationKind.Https,
            "as2" => TriggerDestinationKind.As2,
            "serviceBusTopic" => TriggerDestinationKind.ServiceBusTopic,
            _ => throw Invalid()
        }, connector);
        // The frozen delivery re-verifies the stored digest and length over the exact bytes before any send.
        var delivery = new FrozenTriggerDelivery(rows.GetGuid(1), rows.GetGuid(5), destination, connector.Version, rows.GetFieldValue<DateTimeOffset>(6),
            content, Sha256Digest.Parse(rows.GetString(8)), content.Length, rows.GetString(7));
        return new(TriggerDeliveryClaimStatus.Claimed, rows.GetGuid(2), rows.GetInt32(3), rows.GetFieldValue<DateTimeOffset>(4), delivery, route);
    }

    public async Task<TriggerObservationStatus> RecordObservationAsync(ReleaseTargetReference target, Guid deliveryId, Guid claimId, DeliveryObservationKind kind,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        var observation = kind switch
        {
            DeliveryObservationKind.Accepted => "accepted",
            DeliveryObservationKind.ProvenKnownNotAccepted => "knownNotAccepted",
            DeliveryObservationKind.Unknown => "unknown",
            DeliveryObservationKind.PermanentFailure => "permanentFailure",
            _ => throw new ArgumentException("Only an adapter observation of a send can be recorded.", nameof(kind))
        };
        await using var tx = await BeginAsync(target.Mapping.OrganizationId, true, token);
        await using var command = tx.CreateCommand("SELECT braid.observe_trigger_delivery(@org,@delivery,@claim,@observation)");
        command.Parameters.AddWithValue("org", target.Mapping.OrganizationId);
        command.Parameters.AddWithValue("delivery", deliveryId);
        command.Parameters.AddWithValue("claim", claimId);
        command.Parameters.AddWithValue("observation", observation);
        var result = await command.ExecuteScalarAsync(token) switch
        {
            0 => TriggerObservationStatus.Recorded,
            1 => TriggerObservationStatus.Late,
            _ => throw Invalid()
        };
        await tx.CommitAsync(token);
        return result;
    }

    public async Task<TriggerScheduleState?> ReadScheduleAsync(ReleaseTargetReference target, Guid triggerId, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        await using var tx = await BeginAsync(target.Mapping.OrganizationId, false, token);
        await using var command = tx.CreateCommand("SELECT * FROM braid.read_trigger_schedule(@org,@project,@mapping,@environment,@version,@target,@trigger)");
        Scope(command, target);
        command.Parameters.AddWithValue("trigger", triggerId);
        TriggerScheduleState? result = null;
        await using (var rows = await command.ExecuteReaderAsync(token))
        {
            if (await rows.ReadAsync(token)) result = new(rows.GetInt64(0), rows.IsDBNull(2) ? null : rows.GetFieldValue<DateTimeOffset>(2));
            if (await rows.ReadAsync(token)) throw Invalid();
        }
        await tx.CommitAsync(token);
        return result;
    }

    public async Task<TriggerRunResumption> ResumeRunAsync(ReleaseTargetReference target, Guid triggerId, MachineCaller caller, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(target); ArgumentNullException.ThrowIfNull(caller);
        await using var tx = await BeginAsync(target.Mapping.OrganizationId, true, token);
        await using var command = tx.CreateCommand("SELECT * FROM braid.claim_trigger_run(@org,@project,@mapping,@environment,@version,@target,@trigger,@issuer,@tenant,@principal,@application)");
        Scope(command, target);
        command.Parameters.AddWithValue("trigger", triggerId);
        Caller(command, caller);
        TriggerRunResumption result;
        await using (var rows = await command.ExecuteReaderAsync(token))
        {
            if (!await rows.ReadAsync(token)) throw Invalid();
            result = rows.GetInt32(0) switch
            {
                0 => new(TriggerRunResumeStatus.Claimed, new(rows.GetGuid(1), rows.GetGuid(2), rows.IsDBNull(3) ? null : rows.GetString(3), rows.GetFieldValue<DateTimeOffset>(4))),
                1 => new(TriggerRunResumeStatus.None),
                2 => new(TriggerRunResumeStatus.InProgress),
                3 => new(TriggerRunResumeStatus.Refused),
                4 => new(TriggerRunResumeStatus.Fenced),
                _ => throw Invalid()
            };
            if (await rows.ReadAsync(token)) throw Invalid();
        }
        await tx.CommitAsync(token);
        return result;
    }

    public async Task<TriggerOccurrenceResult> RecordOccurrencesAsync(TriggerDefinition definition, MachineCaller caller, IReadOnlyList<ScheduledOccurrence> missed,
        ScheduledOccurrence? run, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(definition); ArgumentNullException.ThrowIfNull(caller); ArgumentNullException.ThrowIfNull(missed);
        var target = definition.Configuration.Target;
        await using var tx = await BeginAsync(target.Mapping.OrganizationId, true, token);
        await using var command = tx.CreateCommand("SELECT * FROM braid.record_trigger_occurrences(@org,@project,@mapping,@environment,@version,@target,@trigger,@configuration,@issuer,@tenant,@principal,@application,@missed,@offsets,@run,@offset)");
        Scope(command, target);
        command.Parameters.AddWithValue("trigger", definition.Configuration.TriggerId);
        command.Parameters.AddWithValue("configuration", definition.Version);
        Caller(command, caller);
        command.Parameters.AddWithValue("missed", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz, missed.Select(value => value.ScheduledAt).ToArray());
        command.Parameters.AddWithValue("offsets", NpgsqlDbType.Array | NpgsqlDbType.Integer, missed.Select(value => Seconds(value.Offset)).ToArray());
        command.Parameters.AddWithValue("run", NpgsqlDbType.TimestampTz, (object?)run?.ScheduledAt ?? DBNull.Value);
        command.Parameters.AddWithValue("offset", NpgsqlDbType.Integer, run is null ? DBNull.Value : Seconds(run.Offset));
        TriggerOccurrenceResult result;
        await using (var rows = await command.ExecuteReaderAsync(token))
        {
            if (!await rows.ReadAsync(token)) throw Invalid();
            result = rows.GetInt32(0) switch
            {
                0 => new(TriggerOccurrenceStatus.Claimed, new(rows.GetGuid(1), rows.GetGuid(2), null, run!.ScheduledAt)),
                1 => new(TriggerOccurrenceStatus.Recorded),
                2 => new(TriggerOccurrenceStatus.InProgress),
                3 => new(TriggerOccurrenceStatus.Refused),
                _ => throw Invalid()
            };
            if (await rows.ReadAsync(token)) throw Invalid();
        }
        await tx.CommitAsync(token);
        return result;
    }

    public async Task<IReadOnlySet<string>> ReadKnownSourcesAsync(ReleaseTargetReference target, Guid triggerId, MachineCaller caller, IReadOnlyList<string> sources,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(target); ArgumentNullException.ThrowIfNull(caller); ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count > 1024) throw new ArgumentException("At most 1024 source identities are checked at once.");
        await using var tx = await BeginAsync(target.Mapping.OrganizationId, false, token);
        await using var command = tx.CreateCommand("SELECT source FROM braid.read_trigger_sources(@org,@target,@trigger,@issuer,@tenant,@principal,@sources)");
        command.Parameters.AddWithValue("org", target.Mapping.OrganizationId);
        command.Parameters.AddWithValue("target", target.TargetId);
        command.Parameters.AddWithValue("trigger", triggerId);
        command.Parameters.AddWithValue("issuer", caller.Principal.Provider);
        command.Parameters.AddWithValue("tenant", caller.Principal.TenantId);
        command.Parameters.AddWithValue("principal", caller.Principal.ObjectId);
        command.Parameters.AddWithValue("sources", NpgsqlDbType.Array | NpgsqlDbType.Text, sources.ToArray());
        var result = new HashSet<string>(StringComparer.Ordinal);
        await using (var rows = await command.ExecuteReaderAsync(token))
            while (await rows.ReadAsync(token)) result.Add(rows.GetString(0));
        await tx.CommitAsync(token);
        return result;
    }

    public async Task<TriggerPageResult> RecordPageAsync(TriggerDefinition definition, MachineCaller caller, TriggerRunClaim claim, string? cursorBefore,
        string? cursorAfter, bool complete, TriggerScanCounts counts, IReadOnlyList<TriggerDiscoveredOrder> orders, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(definition); ArgumentNullException.ThrowIfNull(caller); ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(counts); ArgumentNullException.ThrowIfNull(orders);
        var configuration = definition.Configuration;
        if (orders.Any(order => order.Identity.Target != configuration.Target || order.Identity.SourceBindingId != configuration.SourceBindingId ||
            order.Identity.Principal != caller.Principal))
            throw new ArgumentException("An order does not belong to this trigger's source, target and validated caller.");
        var target = configuration.Target;
        await using var tx = await BeginAsync(target.Mapping.OrganizationId, true, token);
        await using var command = tx.CreateCommand("SELECT * FROM braid.record_trigger_quarantine_page(@org,@project,@mapping,@environment,@version,@target,@trigger,@run,@claim,@before,@after,@complete,@discovered,@changed,@rejected,@issuer,@tenant,@principal,@application,@sources,@keys,@digests,@bodies,@invalid,@reasons)");
        Scope(command, target);
        command.Parameters.AddWithValue("trigger", configuration.TriggerId);
        command.Parameters.AddWithValue("run", claim.RunId);
        command.Parameters.AddWithValue("claim", claim.ClaimId);
        command.Parameters.AddWithValue("before", NpgsqlDbType.Text, (object?)cursorBefore ?? DBNull.Value);
        command.Parameters.AddWithValue("after", NpgsqlDbType.Text, (object?)cursorAfter ?? DBNull.Value);
        command.Parameters.AddWithValue("complete", complete);
        command.Parameters.AddWithValue("discovered", counts.Discovered);
        command.Parameters.AddWithValue("changed", counts.Changed);
        command.Parameters.AddWithValue("rejected", counts.Rejected);
        command.Parameters.AddWithValue("invalid", NpgsqlDbType.Array | NpgsqlDbType.Text, counts.Quarantine?.Select(item => item.Source).ToArray() ?? []);
        command.Parameters.AddWithValue("reasons", NpgsqlDbType.Array | NpgsqlDbType.Text, counts.Quarantine?.Select(item => item.Reason).ToArray() ?? []);
        Caller(command, caller);
        Orders(command, orders, sources: true);
        TriggerPageResult result;
        await using (var rows = await command.ExecuteReaderAsync(token))
        {
            if (!await rows.ReadAsync(token)) throw Invalid();
            var status = rows.GetInt32(0);
            if (status is < 0 or > 6 or 2) throw Invalid();
            IReadOnlyList<TriggerAdmissionResult> admitted = [];
            if (status == 0)
            {
                var works = rows.GetFieldValue<Guid?[]>(1); var outcomes = rows.GetFieldValue<int[]>(2);
                if (works.Length != orders.Count || outcomes.Length != orders.Count ||
                    outcomes.Zip(works).Any(pair => pair.First is < 0 or > 2 || (pair.First != 2) != (pair.Second is not null))) throw Invalid();
                admitted = [.. outcomes.Zip(works, (outcome, work) => new TriggerAdmissionResult((TriggerAdmissionStatus)outcome, work))];
            }
            result = new(status switch
            {
                0 => TriggerPageStatus.Recorded,
                1 => TriggerPageStatus.Stale,
                3 => TriggerPageStatus.Refused,
                4 => TriggerPageStatus.NoRelease,
                5 => TriggerPageStatus.CapacityExceeded,
                _ => TriggerPageStatus.Fenced
            }, admitted);
            if (await rows.ReadAsync(token)) throw Invalid();
        }
        await tx.CommitAsync(token);
        return result;
    }

    public async Task<IReadOnlyList<Guid>> ReadPendingWorkAsync(ReleaseTargetReference target, Guid triggerId, MachineCaller caller, int maximum,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(target); ArgumentNullException.ThrowIfNull(caller);
        await using var tx = await BeginAsync(target.Mapping.OrganizationId, false, token);
        await using var command = tx.CreateCommand("SELECT work FROM braid.read_trigger_pending_work(@org,@target,@trigger,@issuer,@tenant,@principal,@maximum)");
        command.Parameters.AddWithValue("org", target.Mapping.OrganizationId);
        command.Parameters.AddWithValue("target", target.TargetId);
        command.Parameters.AddWithValue("trigger", triggerId);
        command.Parameters.AddWithValue("issuer", caller.Principal.Provider);
        command.Parameters.AddWithValue("tenant", caller.Principal.TenantId);
        command.Parameters.AddWithValue("principal", caller.Principal.ObjectId);
        command.Parameters.AddWithValue("maximum", maximum);
        var result = new List<Guid>();
        await using (var rows = await command.ExecuteReaderAsync(token))
            while (await rows.ReadAsync(token)) result.Add(rows.GetGuid(0));
        await tx.CommitAsync(token);
        return result;
    }

    public async Task<TriggerBatchAdmission> AdmitBatchAsync(TriggerDefinition definition, string messageId, MachineCaller caller, Sha256Digest manifestDigest,
        IReadOnlyList<TriggerDiscoveredOrder> orders, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(definition); ArgumentNullException.ThrowIfNull(messageId); ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(manifestDigest); ArgumentNullException.ThrowIfNull(orders);
        var configuration = definition.Configuration;
        if (orders.Count == 0 || orders.Any(order => order.Identity.Target != configuration.Target || order.Identity.SourceBindingId != configuration.SourceBindingId ||
            order.Identity.Principal != caller.Principal || order.Identity.MessageId != messageId || order.Identity.Kind != QueueOrderKind.KeyedChild))
            throw new ArgumentException("A batch order does not belong to this message, trigger and validated caller.");
        var target = configuration.Target;
        await using var tx = await BeginAsync(target.Mapping.OrganizationId, true, token);
        await using var command = tx.CreateCommand("SELECT * FROM braid.admit_trigger_batch(@org,@project,@mapping,@environment,@version,@target,@trigger,@configuration,@issuer,@tenant,@principal,@application,@source,@manifest,@keys,@digests,@bodies)");
        Scope(command, target);
        command.Parameters.AddWithValue("trigger", configuration.TriggerId);
        command.Parameters.AddWithValue("configuration", definition.Version);
        Caller(command, caller);
        command.Parameters.AddWithValue("source", messageId);
        command.Parameters.AddWithValue("manifest", manifestDigest.Value);
        Orders(command, orders, sources: false);
        TriggerBatchAdmission result;
        await using (var rows = await command.ExecuteReaderAsync(token))
        {
            if (!await rows.ReadAsync(token)) throw Invalid();
            var status = rows.GetInt32(0);
            if (status is < 0 or > 5) throw Invalid();
            Guid? run = rows.IsDBNull(1) ? null : rows.GetGuid(1);
            var works = rows.IsDBNull(2) ? null : rows.GetFieldValue<Guid[]>(2);
            if ((status is 0 or 1) != (run is not null) || (status is 0 or 1) != (works?.Length == orders.Count)) throw Invalid();
            result = new((TriggerAdmissionStatus)status, run, works);
            if (await rows.ReadAsync(token)) throw Invalid();
        }
        await tx.CommitAsync(token);
        return result;
    }

    private static void Orders(NpgsqlCommand command, IReadOnlyList<TriggerDiscoveredOrder> orders, bool sources)
    {
        if (sources) command.Parameters.AddWithValue("sources", NpgsqlDbType.Array | NpgsqlDbType.Text, orders.Select(order => order.Identity.MessageId).ToArray());
        command.Parameters.AddWithValue("keys", NpgsqlDbType.Array | NpgsqlDbType.Text, orders.Select(order => order.Identity.ChildKey ?? "").ToArray());
        command.Parameters.AddWithValue("digests", NpgsqlDbType.Array | NpgsqlDbType.Text, orders.Select(order => order.RequestDigest.Value).ToArray());
        command.Parameters.AddWithValue("bodies", NpgsqlDbType.Array | NpgsqlDbType.Bytea, orders.Select(order => order.CopyContent()).ToArray());
    }

    private static int Seconds(TimeSpan offset) => checked((int)(offset.Ticks / TimeSpan.TicksPerSecond));

    private async Task<PostgresTransaction> BeginAsync(Guid org, bool write, CancellationToken token)
    {
        var connection = await source.OpenConnectionAsync(token);
        NpgsqlTransaction? transaction = null;
        try
        {
            transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
            await using (var settings = new NpgsqlCommand("SET LOCAL search_path=pg_catalog; SET LOCAL TimeZone='UTC'; SET LOCAL DateStyle='ISO, MDY'; SET LOCAL statement_timeout='15s'; SET LOCAL lock_timeout='5s'", connection, transaction))
                await settings.ExecuteNonQueryAsync(token);
            if (!write) await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction)) await readOnly.ExecuteNonQueryAsync(token);
            await TriggerRoleGuard.ValidateWorkerAsync(connection, transaction, token);
            var result = new PostgresTransaction(connection, transaction, org);
            await result.BindOrganizationAsync(org, token);
            return result;
        }
        catch { if (transaction is not null) await transaction.DisposeAsync(); await connection.DisposeAsync(); throw; }
    }

    private static void Caller(NpgsqlCommand command, MachineCaller caller)
    {
        command.Parameters.AddWithValue("issuer", caller.Principal.Provider);
        command.Parameters.AddWithValue("tenant", caller.Principal.TenantId);
        command.Parameters.AddWithValue("principal", caller.Principal.ObjectId);
        command.Parameters.AddWithValue("application", caller.ApplicationId);
    }

    internal static void Scope(NpgsqlCommand command, ReleaseTargetReference target) => PostgresInvocationGrantStore.Scope(command, target);
    private static InvalidOperationException Invalid() => new("Trigger work persistence refused.");
    public ValueTask DisposeAsync() => source.DisposeAsync();
}
