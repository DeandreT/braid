using System.Data;
using Npgsql;
using NpgsqlTypes;
using Braid.Application;

namespace Braid.Persistence.Postgres;

/// <summary>Restricted durable metadata transitions; no provider, ticket decoding or parent mutation.</summary>
public sealed class PostgresServerSessionWriter : IServerSessionWriter
{
    private readonly PostgresServerSessionDatabase _database;

    public PostgresServerSessionWriter(PostgresServerSessionDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (database.Profile != ServerSessionDatabaseProfile.Writer)
            throw new ArgumentException("Session mutation requires the separate writer profile.", nameof(database));
        _database = database;
    }

    public Task<ServerSessionWriteResult> CreateOnlyAsync(ServerSessionCreateIntent intent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        return ExecuteAsync(intent.Key, async (connection, transaction) =>
        {
            // Preparation precedes this operation, so undo retains its trusted local scope/limits.
            const string savepoint = "braid_session_create_admission";
            await transaction.SaveAsync(savepoint, cancellationToken);
            await using var command = new NpgsqlCommand($"""
                WITH clock AS MATERIALIZED ({PostgresServerSessionFacts.Clock}), current_source AS MATERIALIZED (
                    SELECT clock.observed_at,{PostgresServerSessionFacts.ValidInstant("clock.observed_at")} AS clock_valid,
                        EXISTS(SELECT 1 FROM ONLY braid.organizations organization WHERE organization.id=@organizationId) AS organization_exists,
                        {PostgresServerSessionFacts.IdentityProjection}
                    FROM clock
                    LEFT JOIN ONLY braid.external_identity_bindings binding ON binding.binding_id=@bindingId
                    LEFT JOIN ONLY braid.principals principal ON principal.id=binding.principal_id
                ), admission AS MATERIALIZED (
                    SELECT current_source.*,CASE
                        WHEN NOT clock_valid THEN 'invalid'
                        WHEN identity_binding_id IS NOT NULL AND
                            (identity_binding_id<>@bindingId OR identity_owner_id<>@expectedOwner
                             OR identity_principal_id IS NULL OR identity_principal_id<>identity_owner_id
                             OR identity_binding_version IS NULL OR identity_binding_version<1) THEN 'invalid'
                        WHEN NOT organization_exists OR identity_binding_id IS NULL
                            OR identity_enabled IS NOT TRUE OR identity_active IS NOT TRUE
                            OR identity_binding_version<>@bindingVersion THEN 'denied'
                        WHEN @verifiedDeadline<=observed_at THEN 'denied'
                        WHEN observed_at>{PostgresServerSessionFacts.MaximumBeforeAbsoluteAddition} THEN 'overflow'
                        ELSE 'admitted' END AS phase
                    FROM current_source
                ), absolute_bounds AS MATERIALIZED (
                    SELECT admission.*,CASE WHEN phase='admitted'
                        THEN LEAST(observed_at+INTERVAL '60 minutes',@verifiedDeadline) END AS absolute_bound
                    FROM admission
                ), bounds AS MATERIALIZED (
                    SELECT absolute_bounds.*,CASE WHEN phase='admitted'
                        THEN LEAST(observed_at+INTERVAL '20 minutes',absolute_bound) END AS idle_bound
                    FROM absolute_bounds
                ), inserted AS (
                    INSERT INTO braid.server_sessions AS session_row
                        (handle_digest,organization_id,binding_id,binding_version,login_epoch,issued_at,
                         idle_expires_at,absolute_expires_at,version,revoked_at,policy_version,codec_version,protected_ticket)
                    SELECT @digest,@organizationId,@bindingId,@bindingVersion,@epoch,observed_at,
                        idle_bound,absolute_bound,1,NULL,@policyVersion,@codecVersion,@ticket
                    FROM bounds WHERE phase='admitted'
                    ON CONFLICT(handle_digest) DO NOTHING
                    RETURNING {PostgresServerSessionFacts.ColumnProjection("session_row")}
                )
                SELECT bounds.observed_at,bounds.clock_valid,bounds.organization_exists,bounds.phase,
                    {PostgresServerSessionFacts.ProjectedIdentity},
                    {PostgresServerSessionFacts.Projection("inserted", "new_")}
                FROM bounds LEFT JOIN inserted ON true
                """, connection, transaction);
            PostgresServerSessionFacts.AddKey(command, intent.Key);
            PostgresServerSessionFacts.Add(command, "bindingId", NpgsqlDbType.Uuid, intent.BindingId);
            PostgresServerSessionFacts.Add(command, "bindingVersion", NpgsqlDbType.Bigint, intent.BindingVersion);
            PostgresServerSessionFacts.Add(command, "expectedOwner", NpgsqlDbType.Uuid, intent.ExpectedPrincipalId.Value);
            PostgresServerSessionFacts.Add(command, "epoch", NpgsqlDbType.Uuid, intent.LoginEpoch);
            PostgresServerSessionFacts.Add(command, "verifiedDeadline", NpgsqlDbType.TimestampTz, intent.VerifiedLoginDeadlineUtc);
            PostgresServerSessionFacts.Add(command, "policyVersion", NpgsqlDbType.Integer, intent.PolicyVersion);
            PostgresServerSessionFacts.Add(command, "codecVersion", NpgsqlDbType.Integer, intent.CodecVersion);
            PostgresServerSessionFacts.Add(command, "ticket", NpgsqlDbType.Bytea, intent.CopyProtectedTicket());
            ServerSessionWriteOutcome outcome;
            ServerSessionFacts? created;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                await RequireRowAsync(reader, cancellationToken);
                var sample = PostgresServerSessionFacts.ReadClock(reader);
                // Retain the actual projected owner even when INSERT eligibility is false. A mismatched
                // binding/owner is an integrity error, never a WHERE-filtered ordinary denial.
                var identity = PostgresServerSessionFacts.ReadIdentity(reader, null, intent.ExpectedPrincipalId);
                created = PostgresServerSessionFacts.ReadFacts(reader, "new_", intent.Key);
                var organizationExists = PostgresServerSessionFacts.Read(() => reader.GetBoolean(reader.GetOrdinal("organization_exists")));
                outcome = PostgresServerSessionFacts.Read(() => ServerSessionPolicy.ClassifyCreate(intent,
                    sample, organizationExists, created is null, identity));
                var phase = PostgresServerSessionFacts.Read(() => reader.GetString(reader.GetOrdinal("phase")));
                if (outcome == ServerSessionWriteOutcome.Created)
                {
                    if (phase != "admitted" || created is null) throw PostgresServerSessionFacts.InvalidFacts();
                    var lifetime = PostgresServerSessionFacts.Read(() => ServerSessionPolicy.InitialLifetime(sample, intent.VerifiedLoginDeadlineUtc));
                    if (created.BindingId != intent.BindingId || created.BindingVersion != intent.BindingVersion ||
                        created.LoginEpoch != intent.LoginEpoch || created.Lifetime != lifetime || created.Version != 1 ||
                        created.RevokedAtUtc is not null || created.PolicyVersion != intent.PolicyVersion ||
                        created.CodecVersion != intent.CodecVersion || !created.CopyProtectedTicket().AsSpan().SequenceEqual(intent.CopyProtectedTicket()))
                        throw PostgresServerSessionFacts.InvalidFacts();
                }
                else if (created is not null || outcome is not (ServerSessionWriteOutcome.Denied or ServerSessionWriteOutcome.Conflict) ||
                    phase != (outcome == ServerSessionWriteOutcome.Denied ? "denied" : "admitted"))
                    throw PostgresServerSessionFacts.InvalidFacts();
                await RequireEndAsync(reader, cancellationToken);
            }

            if (outcome == ServerSessionWriteOutcome.Created)
            {
                if (created is null) throw PostgresServerSessionFacts.InvalidFacts();
                // A rolled-back global arbiter can let INSERT finish from its earlier snapshot.
                // Every inserted candidate therefore needs this new statement before acknowledgement.
                bool admitted;
                await using (var observation = new NpgsqlCommand($"""
                    WITH clock AS MATERIALIZED ({PostgresServerSessionFacts.Clock})
                    SELECT clock.observed_at,{PostgresServerSessionFacts.ValidInstant("clock.observed_at")} AS clock_valid,
                        {PostgresServerSessionFacts.Projection("session_row", "stored_")},
                        {PostgresServerSessionFacts.IdentityProjection}
                    FROM clock
                    LEFT JOIN ONLY braid.server_sessions session_row
                        ON session_row.organization_id=@organizationId AND session_row.handle_digest=@digest
                    LEFT JOIN ONLY braid.external_identity_bindings binding ON binding.binding_id=session_row.binding_id
                    LEFT JOIN ONLY braid.principals principal ON principal.id=binding.principal_id
                    """, connection, transaction))
                {
                    PostgresServerSessionFacts.AddKey(observation, intent.Key);
                    await using var reader = await observation.ExecuteReaderAsync(cancellationToken);
                    await RequireRowAsync(reader, cancellationToken);
                    var sample = PostgresServerSessionFacts.ReadClock(reader);
                    var stored = PostgresServerSessionFacts.ReadFacts(reader, "stored_", intent.Key)
                        ?? throw PostgresServerSessionFacts.InvalidFacts();
                    RequireImmutable(created, stored);
                    if (stored.Lifetime != created.Lifetime || stored.Version != created.Version ||
                        stored.RevokedAtUtc != created.RevokedAtUtc)
                        throw PostgresServerSessionFacts.InvalidFacts();
                    // Preserve wrong-owner/reference failures ahead of ordinary current denial.
                    var identity = PostgresServerSessionFacts.ReadIdentity(reader,
                        intent.BindingId, intent.ExpectedPrincipalId);
                    admitted = ServerSessionPolicy.ObserveCurrent(intent.Key, sample, stored, identity) is not null;
                    await RequireEndAsync(reader, cancellationToken);
                }

                if (!admitted)
                {
                    // Transaction undo removes only this uncommitted insertion; no DELETE right.
                    // Any failure/cancellation here escapes, never an acknowledged Denied result.
                    await transaction.RollbackAsync(savepoint, cancellationToken);
                    outcome = ServerSessionWriteOutcome.Denied;
                    created = null;
                }
            }
            // Keep the original lifetime anchor. This is a fresh admission point, not a parent
            // fence through COMMIT; ExecuteAsync acknowledges every pending outcome only afterward.
            await transaction.ReleaseAsync(savepoint, cancellationToken);
            return new PendingWrite(outcome, created);
        }, cancellationToken);
    }

    public Task<ServerSessionWriteResult> RenewAsync(ServerSessionKey key, long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedVersion, 1);
        return ExecuteAsync(key, async (connection, transaction) =>
        {
            var locked = await LockSessionAsync(connection, transaction, key, cancellationToken);
            await using var command = new NpgsqlCommand($"""
                WITH clock AS MATERIALIZED ({PostgresServerSessionFacts.Clock}), current_source AS MATERIALIZED (
                    SELECT clock.observed_at,{PostgresServerSessionFacts.ValidInstant("clock.observed_at")} AS clock_valid,
                        {PostgresServerSessionFacts.ColumnProjection("session_row")},
                        {PostgresServerSessionFacts.IdentityProjection}
                    FROM clock
                    LEFT JOIN ONLY braid.server_sessions session_row ON @locked
                        AND session_row.organization_id=@organizationId AND session_row.handle_digest=@digest
                    LEFT JOIN ONLY braid.external_identity_bindings binding ON binding.binding_id=session_row.binding_id
                    LEFT JOIN ONLY braid.principals principal ON principal.id=binding.principal_id
                ), admission AS MATERIALIZED (
                    SELECT current_source.*,CASE
                        WHEN NOT clock_valid THEN 'invalid'
                        WHEN handle_digest IS NULL THEN 'denied'
                        WHEN NOT COALESCE({PostgresServerSessionFacts.ValidShape("current_source")},false) THEN 'invalid'
                        WHEN identity_binding_id IS NULL OR identity_binding_id<>binding_id
                            OR identity_owner_id IS NULL OR identity_principal_id IS NULL
                            OR identity_owner_id<>identity_principal_id
                            OR identity_binding_version IS NULL OR identity_binding_version<1 THEN 'invalid'
                        WHEN identity_enabled IS NOT TRUE OR identity_active IS NOT TRUE
                            OR identity_binding_version<>binding_version THEN 'denied'
                        WHEN revoked_at IS NOT NULL OR issued_at>observed_at
                            OR observed_at>=idle_expires_at OR observed_at>=absolute_expires_at THEN 'denied'
                        WHEN version<>@expectedVersion THEN 'conflict'
                        WHEN observed_at>{PostgresServerSessionFacts.MaximumBeforeIdleAddition} THEN 'overflow'
                        ELSE 'eligible' END AS admission_phase
                    FROM current_source
                ), proposed AS MATERIALIZED (
                    SELECT admission.*,CASE WHEN admission_phase='eligible'
                        THEN LEAST(GREATEST(idle_expires_at,observed_at+INTERVAL '20 minutes'),absolute_expires_at)
                        END AS proposed_idle FROM admission
                ), decision AS MATERIALIZED (
                    SELECT proposed.*,CASE WHEN admission_phase<>'eligible' THEN admission_phase
                        WHEN proposed_idle=idle_expires_at THEN 'unchanged'
                        WHEN version=9223372036854775807 THEN 'exhausted'
                        ELSE 'renewed' END AS phase FROM proposed
                ), updated AS (
                    UPDATE ONLY braid.server_sessions session_row
                    SET idle_expires_at=decision.proposed_idle,version=session_row.version+1
                    FROM decision WHERE decision.phase='renewed'
                        AND session_row.organization_id=@organizationId AND session_row.handle_digest=@digest
                        AND session_row.version=@expectedVersion AND session_row.revoked_at IS NULL
                    RETURNING {PostgresServerSessionFacts.ColumnProjection("session_row")}
                )
                SELECT decision.observed_at,decision.clock_valid,decision.phase,
                    {PostgresServerSessionFacts.ProjectedIdentity},
                    {PostgresServerSessionFacts.Projection("decision", "old_")},
                    {PostgresServerSessionFacts.Projection("updated", "new_")}
                FROM decision LEFT JOIN updated ON true
                """, connection, transaction);
            PostgresServerSessionFacts.AddKey(command, key);
            PostgresServerSessionFacts.Add(command, "locked", NpgsqlDbType.Boolean, locked);
            PostgresServerSessionFacts.Add(command, "expectedVersion", NpgsqlDbType.Bigint, expectedVersion);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await RequireRowAsync(reader, cancellationToken);
            var sample = PostgresServerSessionFacts.ReadClock(reader);
            var previous = PostgresServerSessionFacts.ReadFacts(reader, "old_", key);
            var identity = PostgresServerSessionFacts.ReadIdentity(reader, previous?.BindingId);
            var current = PostgresServerSessionFacts.ReadFacts(reader, "new_", key);
            var outcome = PostgresServerSessionFacts.Read(() =>
                ServerSessionPolicy.ClassifyRenew(key, expectedVersion, sample, previous, identity));
            RequirePhase(reader, outcome);
            if (outcome == ServerSessionWriteOutcome.Renewed)
            {
                if (previous is null || current is null) throw PostgresServerSessionFacts.InvalidFacts();
                RequireImmutable(previous, current);
                if (current.Version != previous.Version + 1 || current.RevokedAtUtc is not null ||
                    current.Lifetime.IdleExpiresAtUtc != ServerSessionPolicy.RenewalIdleExpiresAt(previous, sample))
                    throw PostgresServerSessionFacts.InvalidFacts();
            }
            else if (current is not null) throw PostgresServerSessionFacts.InvalidFacts();
            await RequireEndAsync(reader, cancellationToken);
            return new PendingWrite(outcome, outcome == ServerSessionWriteOutcome.Unchanged ? previous : current);
        }, cancellationToken);
    }

    public Task<ServerSessionWriteResult> RevokeAsync(ServerSessionKey key,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        return ExecuteAsync(key, async (connection, transaction) =>
        {
            var locked = await LockSessionAsync(connection, transaction, key, cancellationToken);
            await using var command = new NpgsqlCommand($"""
                WITH clock AS MATERIALIZED ({PostgresServerSessionFacts.Clock}), current_source AS MATERIALIZED (
                    SELECT clock.observed_at,{PostgresServerSessionFacts.ValidInstant("clock.observed_at")} AS clock_valid,
                        {PostgresServerSessionFacts.ColumnProjection("session_row")}
                    FROM clock LEFT JOIN ONLY braid.server_sessions session_row ON @locked
                        AND session_row.organization_id=@organizationId AND session_row.handle_digest=@digest
                ), decision AS MATERIALIZED (
                    SELECT current_source.*,CASE
                        WHEN NOT clock_valid THEN 'invalid'
                        WHEN handle_digest IS NULL THEN 'unchanged'
                        WHEN NOT COALESCE({PostgresServerSessionFacts.ValidShape("current_source")},false) THEN 'invalid'
                        WHEN revoked_at IS NOT NULL THEN 'unchanged'
                        WHEN observed_at<issued_at THEN 'invalid'
                        WHEN version=9223372036854775807 THEN 'exhausted'
                        ELSE 'revoked' END AS phase FROM current_source
                ), updated AS (
                    UPDATE ONLY braid.server_sessions session_row
                    SET revoked_at=decision.observed_at,version=session_row.version+1
                    FROM decision WHERE decision.phase='revoked'
                        AND session_row.organization_id=@organizationId AND session_row.handle_digest=@digest
                        AND session_row.version=decision.version AND session_row.revoked_at IS NULL
                    RETURNING {PostgresServerSessionFacts.ColumnProjection("session_row")}
                )
                SELECT decision.observed_at,decision.clock_valid,decision.phase,
                    {PostgresServerSessionFacts.Projection("decision", "old_")},
                    {PostgresServerSessionFacts.Projection("updated", "new_")}
                FROM decision LEFT JOIN updated ON true
                """, connection, transaction);
            PostgresServerSessionFacts.AddKey(command, key);
            PostgresServerSessionFacts.Add(command, "locked", NpgsqlDbType.Boolean, locked);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await RequireRowAsync(reader, cancellationToken);
            var sample = PostgresServerSessionFacts.ReadClock(reader);
            var previous = PostgresServerSessionFacts.ReadFacts(reader, "old_", key);
            var current = PostgresServerSessionFacts.ReadFacts(reader, "new_", key);
            var outcome = ServerSessionPolicy.ClassifyRevoke(key, sample, previous);
            RequirePhase(reader, outcome);
            if (outcome == ServerSessionWriteOutcome.Revoked)
            {
                if (previous is null || current is null) throw PostgresServerSessionFacts.InvalidFacts();
                RequireImmutable(previous, current);
                if (current.Version != previous.Version + 1 || current.RevokedAtUtc != sample ||
                    current.Lifetime.IdleExpiresAtUtc != previous.Lifetime.IdleExpiresAtUtc)
                    throw PostgresServerSessionFacts.InvalidFacts();
            }
            else if (current is not null) throw PostgresServerSessionFacts.InvalidFacts();
            await RequireEndAsync(reader, cancellationToken);
            return new PendingWrite(outcome, outcome == ServerSessionWriteOutcome.Unchanged ? previous : current);
        }, cancellationToken);
    }

    private async Task<ServerSessionWriteResult> ExecuteAsync(ServerSessionKey key,
        Func<NpgsqlConnection, NpgsqlTransaction, Task<PendingWrite>> operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var connection = await _database.DataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            await PostgresServerSessionFacts.PrepareAsync(connection, transaction, key.OrganizationId,
                _database.Profile, cancellationToken);
            var pending = await operation(connection, transaction);
            // No outcome or facts escape a lost/failed acknowledgement, including no-op/refusal.
            await transaction.CommitAsync(cancellationToken);
            return pending.Acknowledge();
        }
        catch (NpgsqlException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException(cancellationToken); }
        catch (NpgsqlException)
        { throw new InvalidOperationException("Server session mutation failed."); }
    }

    private static async Task<bool> LockSessionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        ServerSessionKey key, CancellationToken cancellationToken)
    {
        // No binding/principal lock or forbidden parent UPDATE privilege. This statement chooses
        // no policy clock; after any wait a new statement observes current state and identity.
        await using var command = new NpgsqlCommand("""
            SELECT session_row.handle_digest,session_row.organization_id FROM ONLY braid.server_sessions session_row
            WHERE session_row.organization_id=@organizationId AND session_row.handle_digest=@digest
            FOR UPDATE OF session_row
            """, connection, transaction);
        PostgresServerSessionFacts.AddKey(command, key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return false;
        PostgresServerSessionFacts.Read(() =>
        {
            var digest = reader.GetFieldValue<byte[]>(0);
            if (digest.Length != 32 || reader.GetGuid(1) != key.OrganizationId ||
                !string.Equals(Convert.ToHexStringLower(digest), key.HandleDigest.Value, StringComparison.Ordinal))
                throw PostgresServerSessionFacts.InvalidFacts();
            return true;
        });
        await RequireEndAsync(reader, cancellationToken);
        return true;
    }

    private static void RequireImmutable(ServerSessionFacts previous, ServerSessionFacts current)
    {
        if (previous.Key != current.Key || previous.BindingId != current.BindingId || previous.BindingVersion != current.BindingVersion ||
            previous.LoginEpoch != current.LoginEpoch || previous.Lifetime.IssuedAtUtc != current.Lifetime.IssuedAtUtc ||
            previous.Lifetime.AbsoluteExpiresAtUtc != current.Lifetime.AbsoluteExpiresAtUtc ||
            previous.PolicyVersion != current.PolicyVersion || previous.CodecVersion != current.CodecVersion ||
            !previous.CopyProtectedTicket().AsSpan().SequenceEqual(current.CopyProtectedTicket()))
            throw PostgresServerSessionFacts.InvalidFacts();
    }

    private static void RequirePhase(NpgsqlDataReader reader, ServerSessionWriteOutcome outcome)
    {
        var expected = outcome switch
        {
            ServerSessionWriteOutcome.Renewed => "renewed",
            ServerSessionWriteOutcome.Revoked => "revoked",
            ServerSessionWriteOutcome.Unchanged => "unchanged",
            ServerSessionWriteOutcome.Conflict => "conflict",
            ServerSessionWriteOutcome.Denied => "denied",
            ServerSessionWriteOutcome.VersionExhausted => "exhausted",
            _ => throw PostgresServerSessionFacts.InvalidFacts()
        };
        if (PostgresServerSessionFacts.Read(() => reader.GetString(reader.GetOrdinal("phase"))) != expected)
            throw PostgresServerSessionFacts.InvalidFacts();
    }

    private static async Task RequireRowAsync(NpgsqlDataReader reader, CancellationToken cancellationToken)
    {
        if (!await reader.ReadAsync(cancellationToken)) throw PostgresServerSessionFacts.InvalidFacts();
    }

    private static async Task RequireEndAsync(NpgsqlDataReader reader, CancellationToken cancellationToken)
    {
        if (await reader.ReadAsync(cancellationToken)) throw PostgresServerSessionFacts.InvalidFacts();
    }

    private sealed record PendingWrite(ServerSessionWriteOutcome Outcome, ServerSessionFacts? Facts)
    {
        internal ServerSessionWriteResult Acknowledge() => Facts is null
            ? ServerSessionWriteResult.WithoutFacts(Outcome)
            : ServerSessionWriteResult.ForAcknowledgedFacts(Outcome, Facts);
        public override string ToString() => nameof(PendingWrite);
    }
}
