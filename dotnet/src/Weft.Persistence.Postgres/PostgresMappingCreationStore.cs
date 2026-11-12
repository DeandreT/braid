using System.Data;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Weft.Application;
using Weft.Domain;

namespace Weft.Persistence.Postgres;

public enum CreationDatabaseProfile { Reader, Writer, Approver }

/// <summary>Separately admitted pools; no runtime registration. Owns durable decision and execution fence transactions.</summary>
public sealed class PostgresMappingCreationStore : IMappingCreationProposalStore, IMappingCreationApprovalStore, IMappingCreationAuthority, IAsyncDisposable
{
    private readonly NpgsqlDataSource reader, writer, approver;
    public PostgresMappingCreationStore(string readerConnection, string writerConnection, string approverConnection)
    {
        reader = NpgsqlDataSource.Create(PostgresConfiguration.Normalize(readerConnection));
        writer = NpgsqlDataSource.Create(PostgresConfiguration.Normalize(writerConnection));
        approver = NpgsqlDataSource.Create(PostgresConfiguration.Normalize(approverConnection));
    }

    private sealed class Session(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
    {
        internal NpgsqlConnection Connection => connection;
        internal NpgsqlTransaction Transaction => transaction;
        internal NpgsqlCommand Command(string sql, params object[] args)
        {
            var command = new NpgsqlCommand(sql, connection, transaction);
            foreach (var arg in args)
                command.Parameters.Add(arg is byte[] bytes ? new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = bytes } : new NpgsqlParameter { Value = arg });
            return command;
        }
        public async ValueTask DisposeAsync()
        {
            try { await transaction.DisposeAsync().ConfigureAwait(false); }
            finally { await connection.DisposeAsync().ConfigureAwait(false); }
        }
    }

    private async Task<Session> Open(CreationDatabaseProfile profile, Guid organization, CancellationToken ct)
    {
        var connection = await (profile switch { CreationDatabaseProfile.Reader => reader, CreationDatabaseProfile.Writer => writer, _ => approver }).OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
            var session = new Session(connection, transaction);
            // Lease may span bounded native work. Database publication guards remain authoritative if this connection dies.
            await using var setup = session.Command("SET LOCAL search_path=pg_catalog; SET LOCAL statement_timeout='15s'; SET LOCAL lock_timeout='5s'; SET LOCAL idle_in_transaction_session_timeout='300s'");
            await setup.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await using var scope = session.Command("SELECT set_config('weft.organization_id',$1,true)", organization.ToString("D"));
            await scope.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await CreationSchemaAdmission.ValidateAsync(connection, transaction, profile, ct).ConfigureAwait(false);
            return session;
        }
        catch { await connection.DisposeAsync().ConfigureAwait(false); throw; }
    }

    internal static string Selection(SpecBundleSnapshot snapshot, CreationSpecification? specification = null) => JsonSerializer.Serialize(
        snapshot.Extractions.Select(e => new
        {
            source = e.Source.SourceId,
            version = e.Source.Version,
            sourceDigest = e.SourceDigest.Value,
            sourceCount = specification?.Sources.Single(s => s.Identity == e.Source).ByteCount,
            format = (int)e.Format,
            outcome = (int)e.Outcome,
            extractor = e.ExtractorVersion,
            profile = e.ProfileDigest.Value,
            evidence = e.EvidenceDigest.Value,
            evidenceCount = specification?.Extractions.Single(x => x.Identity == e).CopyEvidenceBytes().Length
        }));

    public async Task<MappingCreationState?> RetainAsync(ActorContext actor, MappingCreationProposal proposal, CancellationToken ct)
    {
        if (actor.PrincipalId != proposal.Author) throw new ArgumentException("Proposal author mismatch.");
        var p = proposal; var project = p.Project;
        try
        {
            await using var session = await Open(CreationDatabaseProfile.Writer, project.OrganizationId, ct).ConfigureAwait(false);
            await using var command = session.Command("SELECT weft.creation_retain($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18::jsonb)",
                actor.PrincipalId.Value, project.OrganizationId, project.ProjectId, p.Id, p.RequestKey, p.Specification.Snapshot.Bundle.BundleId,
                p.Fingerprint.Value, p.Specification.Snapshot.Digest.Value, MappingCreationStorageCodec.Encode(p), p.Compilation.NativeDigest.Value,
                p.Compilation.CopyNative().Length, p.Request.BackendId, p.Request.OriginalFilename, p.Request.Metadata.Name, p.Request.Metadata.Description,
                p.Notes.Assumptions.Where(a => a.Critical).Select(a => a.Id).Order(StringComparer.Ordinal).ToArray(), p.Notes.HasCriticalQuestions, Selection(p.Specification.Snapshot, p.Specification));
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await session.Transaction.CommitAsync(ct).ConfigureAwait(false);
            return await ReadAsync(actor, project, p.Id, ct).ConfigureAwait(false);
        }
        catch (PostgresException e) when (e.SqlState == "42501") { return null; }
        catch (PostgresException e) { throw Failure(e); }
    }

    private static async Task<MappingCreationState?> Read(Session session, ActorContext actor, ProjectScope project, Guid id, CancellationToken ct)
    {
        await using var command = session.Command("SELECT * FROM weft.creation_read($1,$2,$3,$4)", actor.PrincipalId.Value, project.OrganizationId, project.ProjectId, id);
        await using var rows = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await rows.ReadAsync(ct).ConfigureAwait(false)) return null;
        var proposal = MappingCreationStorageCodec.Decode(rows.GetFieldValue<byte[]>(0));
        if (proposal.Author != actor.PrincipalId || proposal.Project != project || proposal.Id != id || proposal.Fingerprint.Value != rows.GetString(1))
            throw new InvalidOperationException("Substituted creation record.");
        var decision = rows.IsDBNull(2) ? null : new CreationApprovalDecision(proposal.Fingerprint, rows.GetFieldValue<string[]>(2));
        if (decision is not null && (proposal.Notes.HasCriticalQuestions || !decision.Matches(proposal))) throw new InvalidOperationException("Invalid retained decision.");
        return new(proposal, decision);
    }

    public async Task<MappingCreationState?> ReadAsync(ActorContext actor, ProjectScope project, Guid proposalId, CancellationToken ct)
    {
        try
        {
            await using var session = await Open(CreationDatabaseProfile.Reader, project.OrganizationId, ct).ConfigureAwait(false);
            var result = await Read(session, actor, project, proposalId, ct).ConfigureAwait(false);
            await session.Transaction.CommitAsync(ct).ConfigureAwait(false);
            return result;
        }
        catch (PostgresException e) when (e.SqlState == "42501") { return null; }
        catch (PostgresException e) { throw Failure(e); }
    }

    public async Task<IMappingCreationApprovalLease?> AcquireAsync(ActorContext actor, ProjectScope project, Guid proposalId,
        Sha256Digest fingerprint, CreationApprovalDecision? decision, CancellationToken ct)
    {
        Session? fence = null;
        try
        {
            if (decision is not null)
            {
                if (decision.Proposal != fingerprint) throw new ArgumentException("Decision fingerprint mismatch.");
                await using var durable = await Open(CreationDatabaseProfile.Approver, project.OrganizationId, ct).ConfigureAwait(false);
                await using var approve = durable.Command("SELECT weft.creation_approve($1,$2,$3,$4,$5,$6)", actor.PrincipalId.Value,
                    project.OrganizationId, project.ProjectId, proposalId, fingerprint.Value, decision.AcceptedCriticalAssumptions.ToArray());
                await approve.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                await durable.Transaction.CommitAsync(ct).ConfigureAwait(false);
            }
            fence = await Open(CreationDatabaseProfile.Approver, project.OrganizationId, ct).ConfigureAwait(false);
            await using var acquire = fence.Command("SELECT weft.creation_fence($1,$2,$3,$4,$5)", actor.PrincipalId.Value, project.OrganizationId, project.ProjectId, proposalId, fingerprint.Value);
            await acquire.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            var state = await Read(fence, actor, project, proposalId, ct).ConfigureAwait(false);
            if (state is null || !state.Approved || state.Proposal.Fingerprint != fingerprint) throw new InvalidOperationException("Invalid creation fence.");
            var lease = new Lease(fence, state); fence = null; return lease;
        }
        catch (PostgresException e) when (e.SqlState == "42501") { return null; }
        catch (PostgresException e) { throw Failure(e); }
        finally { if (fence is not null) await fence.DisposeAsync().ConfigureAwait(false); }
    }

    private sealed class Lease(Session session, MappingCreationState state) : IMappingCreationApprovalLease
    {
        public MappingCreationState State => state;
        public ValueTask DisposeAsync() => session.DisposeAsync();
    }

    public async Task<CreationAuthority> CheckAsync(ActorContext actor, SpecBundleSnapshot snapshot, CancellationToken ct)
    {
        if (snapshot.Extractions.Any(e => e.Outcome != SpecExtractionOutcome.Complete &&
            !(e.Outcome == SpecExtractionOutcome.Partial && (((int)e.Format is 4 or 5 && e.ExtractorVersion == 3) || ((int)e.Format == 6 && e.ExtractorVersion == 4))))) return CreationAuthority.Stale;
        try
        {
            await using var session = await Open(CreationDatabaseProfile.Writer, snapshot.Bundle.Project.OrganizationId, ct).ConfigureAwait(false);
            await using var command = session.Command("SELECT weft.creation_select($1,$2,$3,$4,$5::jsonb)", actor.PrincipalId.Value,
                snapshot.Bundle.Project.OrganizationId, snapshot.Bundle.Project.ProjectId, snapshot.Bundle.BundleId, Selection(snapshot));
            await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            await session.Transaction.CommitAsync(ct).ConfigureAwait(false);
            return CreationAuthority.Current;
        }
        catch (PostgresException e) when (e.SqlState == "42501") { return CreationAuthority.Stale; }
        catch (PostgresException e) { throw Failure(e); }
    }

    private static InvalidOperationException Failure(PostgresException error) => new($"Creation persistence refused ({error.SqlState}).");
    public async ValueTask DisposeAsync()
    {
        await reader.DisposeAsync().ConfigureAwait(false); await writer.DisposeAsync().ConfigureAwait(false); await approver.DisposeAsync().ConfigureAwait(false);
    }
}
