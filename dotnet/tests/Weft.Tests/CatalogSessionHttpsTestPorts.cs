using System.Collections.Concurrent;
using System.Runtime.Versioning;
using Microsoft.AspNetCore.Http;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

internal sealed class CatalogSessionRequestRecord
{
    internal readonly TaskCompletionSource Ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly ConcurrentQueue<string> Order = new();
    internal HttpContext? Context;
    internal ActorContext? Actor;
    internal CatalogSessionBodyGate? BodyGate;
    internal bool Entered, Canceled, UnexpectedFailure, SeedEtag;
    internal int Id;
    internal int BodyReads, BodyBytes, CatalogReads, CapabilityReads, MetadataWrites, CreatorCalls;
    internal int DeferredResolutions, DeferredCalls, DownstreamPaths, SessionReads, SessionRenews;
    public override string ToString() => nameof(CatalogSessionRequestRecord);
}

internal sealed class CatalogSessionBodyGate
{
    internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public override string ToString() => nameof(CatalogSessionBodyGate);
}

internal sealed class CatalogSessionObserveGate
{
    internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource<Task<CurrentServerSession?>> Observation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int Canceled;
    public override string ToString() => nameof(CatalogSessionObserveGate);
}

internal sealed class CatalogSessionCountingBody(Stream inner, CatalogSessionRequestRecord record) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override async ValueTask<int> ReadAsync(Memory<byte> bytes, CancellationToken token = default)
    {
        Interlocked.Increment(ref record.BodyReads); record.Order.Enqueue("body");
        if (record.BodyGate is { } gate)
        { gate.Entered.TrySetResult(); await gate.Release.Task.WaitAsync(token); }
        int count = await inner.ReadAsync(bytes, token);
        Interlocked.Add(ref record.BodyBytes, count); return count;
    }
    public override Task<int> ReadAsync(byte[] bytes, int offset, int count, CancellationToken token) => ReadAsync(bytes.AsMemory(offset, count), token).AsTask();
    public override int Read(byte[] bytes, int offset, int count)
    { Interlocked.Increment(ref record.BodyReads); record.Order.Enqueue("body"); throw new InvalidOperationException("The owned catalog fixture requires asynchronous body reads."); }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] bytes, int offset, int count) => throw new NotSupportedException();
}

[SupportedOSPlatform("linux")]
internal sealed class CatalogSessionCatalogObserver(ICatalogReader actual, CatalogSessionHttpsFixture test) : ICatalogReader
{
    private CatalogSessionRequestRecord Enter(ActorContext actor)
    {
        var record = test.Current; Interlocked.Increment(ref record.CatalogReads);
        record.Actor = new(new PrincipalId(actor.PrincipalId.Value)); record.Order.Enqueue("catalog"); return record;
    }
    public async Task<CatalogOrganizationAdmission?> GetOrganizationAdmissionAsync(ActorContext actor, Guid organizationId, CancellationToken cancellationToken = default)
    {
        var record = Enter(actor);
        var result = await actual.GetOrganizationAdmissionAsync(actor, organizationId, cancellationToken);
        record.Order.Enqueue("organization-completed");
        if (result is not null && test.BreakTokenProfileAfterOrganization)
        {
            // Dispose only this fixture-owned admitted profile after the real organization read.
            // The later private AF token operation must fail operationally, even if User is restored.
            test.Profile.Dispose(); record.Order.Enqueue("token-profile-disposed");
        }
        return result;
    }
    public Task<CatalogProjectPage?> ListProjectsAsync(ActorContext actor, Guid organizationId, int limit, CatalogProjectCursor? cursor = null, CancellationToken cancellationToken = default)
    { Enter(actor); return actual.ListProjectsAsync(actor, organizationId, limit, cursor, cancellationToken); }
    public Task<CatalogProject?> GetProjectAsync(ActorContext actor, ProjectScope scope, CancellationToken cancellationToken = default)
    { Enter(actor); return actual.GetProjectAsync(actor, scope, cancellationToken); }
    public Task<CatalogMappingPage?> ListMappingsAsync(ActorContext actor, ProjectScope scope, int limit, CatalogMappingCursor? cursor = null, CancellationToken cancellationToken = default)
    { Enter(actor); return actual.ListMappingsAsync(actor, scope, limit, cursor, cancellationToken); }
    public async Task<CatalogMapping?> GetMappingAsync(ActorContext actor, MappingScope scope, CancellationToken cancellationToken = default)
    {
        var record = Enter(actor); var result = await actual.GetMappingAsync(actor, scope, cancellationToken);
        record.Order.Enqueue("mapping-completed"); return result;
    }
    public Task<CatalogMappingMetadata?> GetMappingMetadataAsync(ActorContext actor, MappingScope scope, CancellationToken cancellationToken = default)
    { Enter(actor); return actual.GetMappingMetadataAsync(actor, scope, cancellationToken); }
    public Task<CatalogRevisionPage?> ListRevisionsAsync(ActorContext actor, MappingScope scope, int limit, CatalogRevisionCursor? cursor = null, CancellationToken cancellationToken = default)
    { Enter(actor); return actual.ListRevisionsAsync(actor, scope, limit, cursor, cancellationToken); }
    public Task<CatalogRevision?> GetRevisionAsync(ActorContext actor, RevisionScope scope, CancellationToken cancellationToken = default)
    { Enter(actor); return actual.GetRevisionAsync(actor, scope, cancellationToken); }
}

[SupportedOSPlatform("linux")]
internal sealed class CatalogSessionMetadataObserver(IMappingMetadataWriter actual, CatalogSessionHttpsFixture test) : IMappingMetadataWriter
{
    public Task<MappingMetadataWriteResult> ReplaceAsync(ActorContext actor, MappingScope scope, MappingMetadataVersion expectedVersion, MappingMetadataEdit edit, CancellationToken cancellationToken = default)
    {
        var record = test.Current; Interlocked.Increment(ref record.MetadataWrites); record.Order.Enqueue("metadata-writer");
        return actual.ReplaceAsync(actor, scope, expectedVersion, edit, cancellationToken);
    }
}

[SupportedOSPlatform("linux")]
internal sealed class CatalogSessionCreationObserver(PostgresProjectCreator actual, CatalogSessionHttpsFixture test) : IOrganizationProjectCreationReader, IProjectCreator
{
    public Task<OrganizationProjectCreationAdmission?> GetAdmissionAsync(ActorContext actor, Guid organizationId, CancellationToken cancellationToken = default)
    {
        var record = test.Current; Interlocked.Increment(ref record.CapabilityReads); record.Order.Enqueue("capability");
        return actual.GetAdmissionAsync(actor, organizationId, cancellationToken);
    }
    public Task<ProjectCreationResult> CreateAsync(ActorContext actor, Guid organizationId, Guid idempotencyKey, ProjectCreationMetadata metadata, CancellationToken cancellationToken = default)
    {
        var record = test.Current; Interlocked.Increment(ref record.CreatorCalls); record.Order.Enqueue("project-writer");
        return actual.CreateAsync(actor, organizationId, idempotencyKey, metadata, cancellationToken);
    }
}

[SupportedOSPlatform("linux")]
internal sealed class CatalogSessionReaderObserver(IServerSessionReader actual, CatalogSessionHttpsFixture test) : IServerSessionReader
{
    public Task<CurrentServerSession?> ObserveAsync(ServerSessionKey key, CancellationToken cancellationToken = default)
    {
        var gate = test.ObserveGate;
        Task<CurrentServerSession?> original = ObserveCoreAsync(key, gate, cancellationToken);
        gate?.Observation.TrySetResult(original);
        return original;
    }
    private async Task<CurrentServerSession?> ObserveCoreAsync(ServerSessionKey key, CatalogSessionObserveGate? gate,
        CancellationToken cancellationToken)
    {
        var record = test.Current; Interlocked.Increment(ref record.SessionReads);
        var result = await actual.ObserveAsync(key, cancellationToken);
        if (gate is not null)
        {
            gate.Entered.TrySetResult();
            try { await gate.Release.Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { Interlocked.Exchange(ref gate.Canceled, 1); record.Canceled = true; throw; }
        }
        return result;
    }
}

[SupportedOSPlatform("linux")]
internal sealed class CatalogSessionWriterObserver(IServerSessionWriter actual, CatalogSessionHttpsFixture test) : IServerSessionWriter
{
    internal ServerSessionKey? LastKey { get; private set; }
    public Task<ServerSessionWriteResult> CreateOnlyAsync(ServerSessionCreateIntent intent, CancellationToken cancellationToken = default)
    { LastKey = intent.Key; return actual.CreateOnlyAsync(intent, cancellationToken); }
    public Task<ServerSessionWriteResult> RenewAsync(ServerSessionKey key, long expectedVersion, CancellationToken cancellationToken = default)
    {
        var record = test.Current; Interlocked.Increment(ref record.SessionRenews);
        record.Order.Enqueue("session-renew"); return actual.RenewAsync(key, expectedVersion, cancellationToken);
    }
    public Task<ServerSessionWriteResult> RevokeAsync(ServerSessionKey key, CancellationToken cancellationToken = default) => actual.RevokeAsync(key, cancellationToken);
}

/// <summary>Deferred downstream-path witnesses, never actual worker/storage execution.</summary>
[SupportedOSPlatform("linux")]
internal sealed class CatalogSessionForbiddenIngestion(CatalogSessionHttpsFixture test) : IRevisionIngestionService
{
    private Task<T> Witness<T>(string method, T response)
    {
        var record = test.Current; Interlocked.Increment(ref record.DeferredCalls); Interlocked.Increment(ref record.DownstreamPaths); record.Order.Enqueue(method);
        return test.AllowDeferredWitness ? Task.FromResult(response) : Task.FromException<T>(new InvalidOperationException("A refused session reached an ingestion downstream path."));
    }
    public Task<RevisionIngestionResult> CreateAsync(ActorContext actor, ProjectScope scope, Guid key, CreateRevisionRequest request, Stream source, CancellationToken cancellationToken = default) => Witness("native-create", RevisionIngestionResult.WithoutReceipt(RevisionIngestionOutcome.NotAuthorizedOrNotFound));
    public Task<RevisionIngestionResult> AppendAsync(ActorContext actor, MappingScope scope, Guid key, AppendRevisionRequest request, Stream source, CancellationToken cancellationToken = default) => Witness("native-append", RevisionIngestionResult.WithoutReceipt(RevisionIngestionOutcome.NotAuthorizedOrNotFound));
    public Task<RevisionIngestionResult> ResumeAsync(ActorContext actor, ProjectScope scope, Guid operation, CancellationToken cancellationToken = default) => Witness("native-resume", RevisionIngestionResult.WithoutReceipt(RevisionIngestionOutcome.NotAuthorizedOrNotFound));
    public Task<RevisionIngestionReceipt?> GetOwnReceiptAsync(ActorContext actor, ProjectScope scope, Guid operation, CancellationToken cancellationToken = default)
    {
        // Only the exact captured own receipt is positive; arbitrary request IDs still have no receipt.
        var captured = test.Data.Original;
        RevisionIngestionReceipt? receipt = actor.PrincipalId == test.Data.Actor.PrincipalId
            && scope == captured.Facts.Project && operation == captured.Facts.OperationId
            ? RevisionIngestionReceipt.FromMetadata(captured) : null;
        return Witness("native-receipt", receipt);
    }
}
[SupportedOSPlatform("linux")]
internal sealed class CatalogSessionForbiddenReports(CatalogSessionHttpsFixture test) : IRevisionReportReader, IRevisionReportDeriver
{
    private Task<T> Witness<T>(string method, T response)
    {
        var record = test.Current; Interlocked.Increment(ref record.DeferredCalls); Interlocked.Increment(ref record.DownstreamPaths); record.Order.Enqueue(method);
        return test.AllowDeferredWitness ? Task.FromResult(response) : Task.FromException<T>(new InvalidOperationException("A refused session reached a report downstream path."));
    }
    public Task<RevisionReportReadResult> GetCurrentAsync(ActorContext actor, RevisionScope scope, RevisionReportOperation operation, Guid? projectionDerivationId = null, CancellationToken cancellationToken = default) => Witness("report-current", RevisionReportReadResult.WithoutData(RevisionReportReadOutcome.ProfileUnavailable));
    public Task<RevisionReportReadResult> GetRetainedAsync(ActorContext actor, RevisionScope scope, RevisionReportOperation operation, Guid derivationId, Guid? projectionDerivationId = null, CancellationToken cancellationToken = default) => Witness("report-retained", RevisionReportReadResult.WithoutData(RevisionReportReadOutcome.ProfileUnavailable));
    public Task<RevisionReportEnsureResult> EnsureAsync(ActorContext actor, RevisionScope scope, RevisionReportOperation operation, Guid? repairDerivationId = null, CancellationToken cancellationToken = default) => Witness("report-ensure", RevisionReportEnsureResult.WithoutData(RevisionReportEnsureOutcome.ProfileUnavailable));
}

internal sealed class CatalogSessionDueClock : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.MaxValue.AddDays(-1);
}
