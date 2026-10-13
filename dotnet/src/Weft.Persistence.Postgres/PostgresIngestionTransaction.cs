using Npgsql;

namespace Weft.Persistence.Postgres;

/// <summary>Internal, separately admitted organization write scope; never a client connection route.</summary>
internal sealed class PostgresIngestionTransaction : IAsyncDisposable
{
    private readonly PostgresTransaction _scope;

    internal PostgresIngestionTransaction(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid organizationId)
        => _scope = new PostgresTransaction(connection, transaction, organizationId);

    internal Guid OrganizationId => _scope.OrganizationId;
    internal NpgsqlConnection Connection => _scope.Connection;
    internal NpgsqlTransaction Transaction => _scope.Transaction;
    internal NpgsqlCommand CreateCommand(string sql) => _scope.CreateCommand(sql);
    internal Task BindOrganizationAsync(Guid organizationId, CancellationToken cancellationToken = default)
        => _scope.BindOrganizationAsync(organizationId, cancellationToken);
    internal Task RequireOrganizationAsync(Guid organizationId, CancellationToken cancellationToken = default)
        => _scope.RequireOrganizationAsync(organizationId, cancellationToken);
    internal Task CommitAsync(CancellationToken cancellationToken = default) => _scope.CommitAsync(cancellationToken);
    internal Task RollbackAsync(CancellationToken cancellationToken = default) => _scope.RollbackAsync(cancellationToken);
    public ValueTask DisposeAsync() => _scope.DisposeAsync();
}
