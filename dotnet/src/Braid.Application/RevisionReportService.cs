using Braid.Domain;

namespace Braid.Application;

/// <summary>
/// Composes freshly authorized metadata observations with bounded private object reads and
/// fenced derivation work. Composition shares one instance through both workflow ports.
/// </summary>
public sealed partial class RevisionReportService : IRevisionReportReader, IRevisionReportDeriver
{
    private static readonly TimeSpan OperationLimit = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RecoveryLimit = TimeSpan.FromSeconds(5);
    private readonly IRevisionReportMetadataReader _reader;
    private readonly IRevisionReportMetadataStore _metadata;
    private readonly IRevisionReportProfileRegistry _registry;
    private readonly RevisionReportStorageRegistration _storage;
    private readonly Guid _serverInstanceId;
    private readonly RevisionReportPolicy _readCaps;
    private readonly RevisionReportEnvelopeCodec _codec;
    private readonly SemaphoreSlim _workGate = new(1, 1);
    private readonly SemaphoreSlim _readGate = new(2, 2);

    public RevisionReportService(IRevisionReportMetadataReader metadataReader,
        IRevisionReportMetadataStore metadataStore, IRevisionReportProfileRegistry registry,
        RevisionReportStorageRegistration storage, Guid serverInstanceId,
        RevisionReportPolicy? activeReadCaps = null)
    {
        ArgumentNullException.ThrowIfNull(metadataReader);
        ArgumentNullException.ThrowIfNull(metadataStore);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(storage);
        if (serverInstanceId == Guid.Empty)
            throw new ArgumentException("The report server instance identifier cannot be empty.", nameof(serverInstanceId));
        _reader = metadataReader;
        _metadata = metadataStore;
        _registry = registry;
        _storage = storage;
        _serverInstanceId = serverInstanceId;
        _readCaps = storage.ApplyTo(activeReadCaps ?? new RevisionReportPolicy());
        _codec = new(registry);
    }

    private static CancellationTokenSource Deadline(CancellationToken caller)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(caller);
        deadline.CancelAfter(OperationLimit);
        return deadline;
    }

    private static void Input(ActorContext actor, RevisionScope scope, RevisionReportOperation operation,
        Guid? selector, bool projectionSelector)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(scope);
        if (!Enum.IsDefined(operation)) throw new ArgumentOutOfRangeException(nameof(operation));
        if (selector == Guid.Empty)
            throw new ArgumentException("A report selector cannot be empty.", nameof(selector));
        if (projectionSelector && operation == RevisionReportOperation.Projection && selector is not null)
            throw new ArgumentException("A projection report cannot select another projection.", nameof(selector));
    }

    private static RevisionReportServiceException Inconsistent() =>
        new(RevisionReportFailureCategory.ProtocolFailure);

    private static RevisionReportReadResult HiddenRead() =>
        RevisionReportReadResult.WithoutData(RevisionReportReadOutcome.NotAuthorizedOrNotFound);

    private static RevisionReportEnsureResult HiddenEnsure() =>
        RevisionReportEnsureResult.WithoutData(RevisionReportEnsureOutcome.NotAuthorizedOrNotFound);
}
