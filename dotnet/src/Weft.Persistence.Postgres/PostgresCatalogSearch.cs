using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Weft.Application;
using Weft.Contracts.Projections;
using Weft.Domain;

namespace Weft.Persistence.Postgres;

/// <summary>Shared HTTP/chat query service. Cold reports are indexed from verified retained reports, never native inputs.</summary>
public sealed class PostgresCatalogSearch(PostgresCatalogReader reader,
    Func<IRevisionReportReader?>? reports = null, Func<PostgresRevisionDerivationDatabase?>? indexDatabase = null) : ICatalogSearch
{
    private readonly SemaphoreSlim _backfillGate = new(1, 1);
    public async Task<CatalogSearchPage?> SearchAsync(ActorContext actor, Guid organizationId,
        CatalogSearchQuery query, int limit, CatalogSearchCursor? cursor = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(query);
        if (organizationId == Guid.Empty) throw new ArgumentException("An organization is required.", nameof(organizationId));
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        cursor?.RequireContext(organizationId, query);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            // Bounded read-through backfill. The final search statement checks completeness and
            // fresh grants again, including publications and revocations racing this batch.
            await BackfillAsync(actor, organizationId, 4, deadline.Token);
            return await reader.SearchIndexAsync(actor, organizationId, query, limit, cursor, deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CatalogSearchIndexPendingException();
        }
        catch (RevisionReportServiceException) { throw new CatalogSearchIndexPendingException(); }
    }

    /// <summary>One bounded operator batch; repeat until zero to prime an existing organization's reports.</summary>
    public async Task<int> BackfillAsync(ActorContext actor, Guid organizationId, int batchSize = 100,
        CancellationToken cancellationToken = default)
    {
        if (batchSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(batchSize));
        var pending = await reader.PendingSearchReportsAsync(actor, organizationId, batchSize, cancellationToken);
        if (pending.Count == 0) return 0;
        if (!_backfillGate.Wait(0)) throw new CatalogSearchIndexPendingException();
        try
        {
            var reportReader = reports?.Invoke() ?? throw new CatalogSearchIndexPendingException();
            var database = indexDatabase?.Invoke() ?? throw new CatalogSearchIndexPendingException();
            foreach (var item in pending)
            {
                var retained = await reportReader.GetRetainedAsync(actor, item.Scope, RevisionReportOperation.Projection,
                    item.DerivationId, cancellationToken: cancellationToken);
                if (retained.Report is not VerifiedProjectionReport verified || verified.Metadata.Key.Scope != item.Scope ||
                    verified.Metadata.DerivationId != item.DerivationId)
                    throw new CatalogSearchIndexPendingException();
                try { await IndexAsync(database, actor, verified, cancellationToken); }
                catch (NpgsqlException) when (!cancellationToken.IsCancellationRequested) { throw new CatalogSearchIndexPendingException(); }
                catch (NpgsqlException) { throw new OperationCanceledException(cancellationToken); }
            }
            return pending.Count;
        }
        finally { _backfillGate.Release(); }
    }

    private static async Task IndexAsync(PostgresRevisionDerivationDatabase database, ActorContext actor,
        VerifiedProjectionReport report, CancellationToken token)
    {
        var document = report.Report.Document ?? throw new CatalogSearchIndexPendingException();
        var boundaries = document.Boundaries.ToDictionary(item => item.Id, item => item.Name, StringComparer.Ordinal);
        string Path(ProjectionField field) => boundaries[field.BoundaryId] + ":/" + string.Join('/', field.Path.Select(segment => segment switch
        {
            FieldPathSegment part => part.NamespaceUri is null ? part.Name : "{" + part.NamespaceUri + "}" + part.Name,
            AttributePathSegment part => "@" + (part.NamespaceUri is null ? part.Name : "{" + part.NamespaceUri + "}" + part.Name),
            IndexPathSegment part => "[" + part.Index + "]",
            AnyItemPathSegment => "[*]",
            _ => throw new CatalogSearchIndexPendingException()
        }));
        var paths = document.Fields.ToDictionary(field => field.Id, Path, StringComparer.Ordinal);
        var terms = new List<Term>();
        long textBytes = 0;
        void Append(int kind, string id, string title, string path, NativeLocator locator)
        {
            token.ThrowIfCancellationRequested();
            var native = NativePath(locator);
            textBytes += Encoding.UTF8.GetByteCount(id) + Encoding.UTF8.GetByteCount(title) + Encoding.UTF8.GetByteCount(path) + Encoding.UTF8.GetByteCount(native) + 1L;
            if (textBytes > 16777216) throw new CatalogSearchIndexPendingException();
            terms.Add(new(kind, id, title, path + "\n" + native));
        }
        foreach (var field in document.Fields)
            Append(4, field.Id, string.IsNullOrEmpty(field.Name) ? field.Id : field.Name, paths[field.Id], field.NativeLocator);
        foreach (var rule in document.Rules)
            Append(5, rule.Id, string.IsNullOrEmpty(rule.Summary) ? rule.Id : rule.Summary, paths[rule.OutputFieldId], rule.NativeLocator);
        using var payload = new IndexBuffer();
        JsonSerializer.Serialize(payload, terms, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var metadata = report.Metadata;
        var scope = metadata.Key.Scope;
        await using var transaction = await database.BeginTransactionAsync(scope.OrganizationId, token);
        await transaction.RequireOrganizationAsync(scope.OrganizationId, token);
        await using var command = transaction.CreateCommand("SELECT weft.index_catalog_report($1,$2,$3,$4,$5,$6,$7,$8)");
        command.Parameters.AddWithValue(actor.PrincipalId.Value);
        command.Parameters.AddWithValue(scope.OrganizationId);
        command.Parameters.AddWithValue(scope.ProjectId);
        command.Parameters.AddWithValue(scope.MappingId);
        command.Parameters.AddWithValue(scope.RevisionId);
        command.Parameters.AddWithValue(metadata.DerivationId);
        command.Parameters.AddWithValue(metadata.DerivedObject.Digest.Value);
        command.Parameters.AddWithValue(NpgsqlDbType.Jsonb, Encoding.UTF8.GetString(payload.GetBuffer(), 0, checked((int)payload.Length)));
        if (await command.ExecuteScalarAsync(token) is not true) throw new CatalogSearchIndexPendingException();
        await transaction.CommitAsync(token);
    }

    private sealed record Term(int Kind, string Id, string Title, string Path);

    private static string NativePath(NativeLocator locator) => locator.DocumentId + "#" + (locator switch
    {
        JsonPointerNativeLocator pointer => pointer.Pointer,
        GraphNodeNativeLocator graph => "function/" + graph.FunctionId + "/node/" + graph.NodeId,
        TextSpanNativeLocator span => span.Unit + "/" + span.StartOffset + "/" + span.Length,
        OpaqueNativeLocator opaque => opaque.Scheme + "/" + opaque.Value,
        DocumentNativeLocator => "",
        _ => throw new CatalogSearchIndexPendingException()
    });

    private sealed class IndexBuffer : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            Require(count);
            base.Write(buffer, offset, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Require(buffer.Length);
            var owned = buffer.ToArray();
            base.Write(owned, 0, owned.Length);
        }
        private void Require(int count) { if (count > 16777216 - Length) throw new CatalogSearchIndexPendingException(); }
    }
}
