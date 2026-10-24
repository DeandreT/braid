using System.Runtime.Versioning;
using Weft.Ai;
using Weft.Application;
using Weft.Domain;
using Weft.Identity;

namespace Weft.Tests;

/// <summary>The actual audit writer, counted per request; a record can make it unavailable or report a changed identity.</summary>
[SupportedOSPlatform("linux")]
internal sealed class AuthHttpAuditWriter(IApiRequestAuditWriter actual, AuthHttpFixture test) : IApiRequestAuditWriter
{
    public async Task<ApiRequestAuditOutcome> RecordAsync(ApiRequestAudit audit, CancellationToken cancellationToken)
    {
        var record = test.Current; Interlocked.Increment(ref record.Audits);
        if (record.AuditUnavailable) throw new InvalidOperationException("The owned audit fixture is unavailable.");
        if (record.AuditIdentityChanged) return ApiRequestAuditOutcome.IdentityChanged;
        return await actual.RecordAsync(audit, cancellationToken);
    }
}

/// <summary>The chat routes' provider credential is never needed: no case starts a turn.</summary>
internal sealed class AuthHttpNoCredentials : IAiCredentialResolver
{
    public ValueTask<AiCredentialResult> ResolveAsync(string reference, CancellationToken ct) =>
        ValueTask.FromResult<AiCredentialResult>(CredentialUnavailable.Instance);
}

[SupportedOSPlatform("linux")]
internal sealed class AuthHttpIdentityReader(IExternalIdentityReader actual, AuthHttpFixture test) : IExternalIdentityReader, IDisposable
{
    internal bool Disposed;
    public void Dispose() => Disposed = true;
    public Task<ExternalIdentityResolution> ResolveAsync(ExternalIdentityKey key, CancellationToken cancellationToken = default)
    {
        if (Disposed) throw new ServerSessionIdentityException(ServerSessionIdentityFailureKind.Unavailable);
        var record = test.Current; Interlocked.Increment(ref record.Resolves); record.ResolvedKey = key;
        var original = ResolveCoreAsync(key, record, cancellationToken);
        record.BindingGate?.Original.TrySetResult(original);
        return test.Track(original, record, failure: record.BindingUnavailable);
    }
    private async Task<ExternalIdentityResolution> ResolveCoreAsync(ExternalIdentityKey key, AuthHttpRecord record, CancellationToken token)
    {
        var original = actual.ResolveAsync(key, token); _ = test.Track(original, record, failure: record.BindingUnavailable);
        var result = await original;
        record.Order.Enqueue("binding-read");
        if (record.BindingGate is { } gate) await gate.HoldAsync(token);
        if (record.BindingUnavailable) throw new ServerSessionIdentityException(ServerSessionIdentityFailureKind.Unavailable);
        return result;
    }
    public Task<ExternalIdentityResolution> ResolveBindingAsync(Guid bindingId, CancellationToken cancellationToken = default)
    {
        var record = test.Current; Interlocked.Increment(ref record.BindingReads);
        return test.Track(actual.ResolveBindingAsync(bindingId, cancellationToken), record);
    }
}

[SupportedOSPlatform("linux")]
internal sealed class AuthHttpSessionReader(IServerSessionReader actual, AuthHttpFixture test) : IServerSessionReader
{
    public Task<CurrentServerSession?> ObserveAsync(ServerSessionKey key, CancellationToken cancellationToken = default)
    {
        var record = test.Current; int read = Interlocked.Increment(ref record.SessionReads);
        var gate = record.ReaderGateAtRead == 0 || read == record.ReaderGateAtRead ? record.ReaderGate : null;
        var original = ObserveCoreAsync(key, record, gate, cancellationToken);
        record.GatedReaderOperation = original;
        gate?.Original.TrySetResult(original);
        return test.Track(original, record, failure: record.ReaderUnavailable || record.ReaderIntegrity, role: AuthHttpTaskRole.GatedReader);
    }
    private async Task<CurrentServerSession?> ObserveCoreAsync(ServerSessionKey key, AuthHttpRecord record, AuthHttpGate? gate, CancellationToken token)
    {
        var original = actual.ObserveAsync(key, token); record.ActualReaderOperation = original; _ = test.Track(original, record, role: AuthHttpTaskRole.ActualReader);
        var result = await original;
        if (gate is not null) await gate.HoldAsync(token);
        if (record.ReaderUnavailable || record.ReaderIntegrity)
        {
            var kind = record.ReaderIntegrity ? ServerSessionIdentityFailureKind.IntegrityFailure : ServerSessionIdentityFailureKind.Unavailable;
            record.InjectedReaderFailure = kind;
            throw new ServerSessionIdentityException(kind);
        }
        return result;
    }
}

[SupportedOSPlatform("linux")]
internal sealed class AuthHttpSessionWriter(IServerSessionWriter actual, AuthHttpFixture test) : IServerSessionWriter
{
    public Task<ServerSessionWriteResult> CreateOnlyAsync(ServerSessionCreateIntent intent, CancellationToken cancellationToken = default)
    {
        var record = test.Current; Interlocked.Increment(ref record.Creates); record.CreatedKey = intent.Key; record.Order.Enqueue("create-enter");
        var original = CreateCoreAsync(intent, record, cancellationToken);
        record.CreateGate?.Original.TrySetResult(original);
        return test.Track(original, record, failure: record.LoseCreateAck);
    }
    private async Task<ServerSessionWriteResult> CreateCoreAsync(ServerSessionCreateIntent intent, AuthHttpRecord record, CancellationToken token)
    {
        var original = actual.CreateOnlyAsync(intent, token); _ = test.Track(original, record);
        var result = await original; record.CreatedFacts = result.Facts; record.Order.Enqueue("create-committed");
        if (record.CreateGate is { } gate) await gate.HoldAsync(token);
        if (record.StartHeadersAfterCreate) await record.Context!.Response.StartAsync(token);
        if (record.LoseCreateAck) throw new ServerSessionIdentityException(ServerSessionIdentityFailureKind.Unavailable);
        record.Order.Enqueue("create-ack"); return result;
    }
    public Task<ServerSessionWriteResult> RenewAsync(ServerSessionKey key, long expectedVersion, CancellationToken cancellationToken = default)
    {
        var record = test.Current; Interlocked.Increment(ref record.Renews); record.Order.Enqueue("renew");
        return test.Track(actual.RenewAsync(key, expectedVersion, cancellationToken), record);
    }
    public Task<ServerSessionWriteResult> RevokeAsync(ServerSessionKey key, CancellationToken cancellationToken = default)
    {
        var record = test.Current; Interlocked.Increment(ref record.Revokes); record.Order.Enqueue("revoke-enter");
        var original = RevokeCoreAsync(key, record, cancellationToken);
        record.RevokeGate?.Original.TrySetResult(original);
        return test.Track(original, record, failure: record.LoseRevokeAck);
    }
    private async Task<ServerSessionWriteResult> RevokeCoreAsync(ServerSessionKey key, AuthHttpRecord record, CancellationToken token)
    {
        var original = actual.RevokeAsync(key, token); _ = test.Track(original, record);
        var result = await original; record.Order.Enqueue("revoke-committed");
        if (record.RevokeGate is { } gate) await gate.HoldAsync(token);
        if (record.LoseRevokeAck) throw new ServerSessionIdentityException(ServerSessionIdentityFailureKind.Unavailable);
        record.Order.Enqueue("revoke-ack"); return result;
    }
}

[SupportedOSPlatform("linux")]
internal sealed class AuthHttpCatalogReader(ICatalogReader actual, AuthHttpFixture test) : ICatalogReader
{
    private AuthHttpRecord Enter() { var record = test.Current; Interlocked.Increment(ref record.CatalogReads); return record; }
    public Task<CatalogOrganizationAdmission?> GetOrganizationAdmissionAsync(ActorContext actor, Guid organizationId, CancellationToken cancellationToken = default)
    {
        var record = Enter(); var original = OrganizationCoreAsync(actor, organizationId, record, cancellationToken);
        record.CatalogGate?.Original.TrySetResult(original); return test.Track(original, record);
    }
    private async Task<CatalogOrganizationAdmission?> OrganizationCoreAsync(ActorContext actor, Guid organizationId, AuthHttpRecord record, CancellationToken token)
    {
        var original = actual.GetOrganizationAdmissionAsync(actor, organizationId, token); _ = test.Track(original, record);
        var result = await original;
        if (record.CatalogGate is { } gate) await gate.HoldAsync(token);
        return result;
    }
    public Task<CatalogProjectPage?> ListProjectsAsync(ActorContext actor, Guid organizationId, int limit, CatalogProjectCursor? cursor = null, CancellationToken cancellationToken = default)
    { Enter(); return test.Track(actual.ListProjectsAsync(actor, organizationId, limit, cursor, cancellationToken), test.Current); }
    public Task<CatalogProject?> GetProjectAsync(ActorContext actor, ProjectScope scope, CancellationToken cancellationToken = default)
    { Enter(); return test.Track(actual.GetProjectAsync(actor, scope, cancellationToken), test.Current); }
    public Task<CatalogMappingPage?> ListMappingsAsync(ActorContext actor, ProjectScope scope, int limit, CatalogMappingCursor? cursor = null, CancellationToken cancellationToken = default)
    { Enter(); return test.Track(actual.ListMappingsAsync(actor, scope, limit, cursor, cancellationToken), test.Current); }
    public Task<CatalogMapping?> GetMappingAsync(ActorContext actor, MappingScope scope, CancellationToken cancellationToken = default)
    { Enter(); return test.Track(actual.GetMappingAsync(actor, scope, cancellationToken), test.Current); }
    public Task<CatalogMappingMetadata?> GetMappingMetadataAsync(ActorContext actor, MappingScope scope, CancellationToken cancellationToken = default)
    { Enter(); return test.Track(actual.GetMappingMetadataAsync(actor, scope, cancellationToken), test.Current); }
    public Task<CatalogRevisionPage?> ListRevisionsAsync(ActorContext actor, MappingScope scope, int limit, CatalogRevisionCursor? cursor = null, CancellationToken cancellationToken = default)
    { Enter(); return test.Track(actual.ListRevisionsAsync(actor, scope, limit, cursor, cancellationToken), test.Current); }
    public Task<CatalogRevision?> GetRevisionAsync(ActorContext actor, RevisionScope scope, CancellationToken cancellationToken = default)
    { Enter(); return test.Track(actual.GetRevisionAsync(actor, scope, cancellationToken), test.Current); }
}

[SupportedOSPlatform("linux")]
internal sealed class AuthHttpMetadataWriter(IMappingMetadataWriter actual, AuthHttpFixture test) : IMappingMetadataWriter
{
    public Task<MappingMetadataWriteResult> ReplaceAsync(ActorContext actor, MappingScope scope, MappingMetadataVersion expectedVersion, MappingMetadataEdit edit, CancellationToken cancellationToken = default)
    { var record = test.Current; Interlocked.Increment(ref record.MetadataWrites); return test.Track(actual.ReplaceAsync(actor, scope, expectedVersion, edit, cancellationToken), record); }
}
internal sealed class AuthHttpDueClock : TimeProvider
{ public override DateTimeOffset GetUtcNow() => DateTimeOffset.MaxValue.AddDays(-1); }
