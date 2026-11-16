using Microsoft.Extensions.DependencyInjection;
using Braid.Api.Creation;
using Braid.Application;
using Braid.Domain;

namespace Braid.Tests;

// Recording portable ports: exercises application/HTTP orchestration, not durable lease qualification.
internal sealed class CreationRuntimeFixture : ISpecBundleServiceFactory, ISpecBundleLifecycle, ISpecBundleInventory, ISpecObjectStore,
    ICreationRuntimeAdmission, IMappingCreationCompiler, IMappingCreationAuthority, IMappingCreationProposalStore,
    IMappingCreationApprovalStore, IMappingCreationPreview, IRevisionIngestionService, IIngestionWorkflowReader, ICreationDiscovery
{
    internal readonly WorkspaceToolFixture Catalog = new(payloads: true);
    internal readonly SpecBundleScope Bundle = new(WorkspaceToolFixture.Project, Guid.NewGuid());
    internal readonly Guid OriginalId = Guid.NewGuid(), ExtractionId = Guid.NewGuid();
    internal readonly SpecCapturedSource Source;
    internal readonly SpecExtractionResult Extraction;
    internal readonly List<(Guid Original, Guid Extraction, SpecCapturedSource Source, SpecExtractionResult Evidence)> Additional = [];
    internal readonly List<(Guid Original, Guid Extraction, SpecCapturedSource Source, CreationEvidenceDocument Evidence)> Documents = [];
    private IEnumerable<(Guid Original, Guid Extraction, SpecCapturedSource Source, CreationEvidenceDocument Evidence)> Entries =>
        new[] { (Original: OriginalId, Extraction: ExtractionId, Source, Evidence: Extraction) }.Concat(Additional)
            .Select(a => (a.Original, a.Extraction, a.Source, CreationEvidenceDocument.From(a.Evidence))).Concat(Documents);
    internal long BundleVersion = 3;
    internal readonly MappingCreationPlan Plan = new([new("a", CreationScalarType.Text, true)], [new("b", CreationScalarType.Text, true)], [new("a", "b")]);
    internal CreationSpecification Specification => new(Bundle, Entries.Select(a => a.Source), Entries.Select(a => a.Evidence));
    internal byte[]? ReplacedEvidence;
    internal SpecExtractionIdentity? StoredIdentityOverride { get; set; }
    internal bool Ready = true, Denied, WrongSnapshot;
    internal bool Stale;
    internal bool LoseRetentionAcknowledgement;
    internal Action? AfterRead, AfterCompile;
    internal Action? AfterRetain;
    internal Action? AfterPreview { get; set; }
    internal int Reads, Compiles, Creates, Resumes, Leases;
    internal MappingCreationState? State;
    internal IngestionReceipt? Receipt;
    internal CreationRuntimeFixture(string? sourceText = null)
    {
        Catalog.Payloads!.Grants.Add(WorkspaceToolFixture.Project);
        Source = new(new(Bundle, Guid.NewGuid(), 1), SpecSourceFormat.Text, System.Text.Encoding.UTF8.GetBytes(sourceText ?? "Map a to b. Private customer evidence."));
        Extraction = SpecSourceExtractor.Extract(Source);
    }
    internal void Register(IServiceCollection services)
    {
        services.AddSingleton(new CreationRuntimeOptions(true));
        foreach (var type in new[] { typeof(ISpecBundleServiceFactory), typeof(ICreationRuntimeAdmission), typeof(IMappingCreationCompiler),
            typeof(IMappingCreationAuthority), typeof(IMappingCreationProposalStore), typeof(IMappingCreationApprovalStore), typeof(IMappingCreationPreview),
            typeof(IRevisionIngestionService), typeof(IIngestionWorkflowReader), typeof(ICreationDiscovery) }) services.AddSingleton(type, this);
        services.AddSingleton<ICatalogReader>(Catalog); services.AddSingleton<ICatalogResourceLocator>(Catalog);
        services.AddSingleton<IRevisionReportReader>(Catalog); services.AddSingleton<IRevisionReportMetadataReader>(Catalog);
        services.AddSingleton<IProjectPayloadPermissionReader>(Catalog.Payloads!); services.AddSingleton<IProjectPayloadGrantPresence>(Catalog.Payloads!);
    }
    private void Admit(SpecBundleScope scope) { if (Denied || scope != Bundle) throw new SpecStorageException(SpecStorageError.NotAdmitted); }
    public Task<IReadOnlyList<CreationSelectionReference>> SelectionsAsync(ActorContext actor, ProjectScope project,
        Guid? afterBundle, Guid? afterObject, int limit, CancellationToken ct)
    {
        Admit(Bundle);
        if (actor != WorkspaceToolFixture.Actor || project != Bundle.Project) throw new SpecStorageException(SpecStorageError.NotAdmitted);
        return Task.FromResult<IReadOnlyList<CreationSelectionReference>>(Items().Where(i => i.Extraction is not null)
            .Where(i => afterBundle is null || string.CompareOrdinal($"{Bundle.BundleId:D}/{i.Reference.Id.ObjectId:D}", $"{afterBundle:D}/{afterObject:D}") > 0)
            .OrderBy(i => i.Reference.Id.ObjectId.ToString("D"), StringComparer.Ordinal).Take(limit).Select(i => new CreationSelectionReference(Bundle, i.Reference.Id.ObjectId)).ToArray());
    }
    public Task<IReadOnlyList<CreationProposalReference>> ProposalsAsync(ActorContext actor, ProjectScope project, Guid? afterProposal, int limit, CancellationToken ct)
    {
        Admit(Bundle);
        if (actor != WorkspaceToolFixture.Actor || project != Bundle.Project) throw new SpecStorageException(SpecStorageError.NotAdmitted);
        return Task.FromResult<IReadOnlyList<CreationProposalReference>>(State is { } s && (afterProposal is null || string.CompareOrdinal(s.Proposal.Id.ToString("D"), afterProposal.Value.ToString("D")) > 0)
            ? [new(project, s.Proposal.Id, s.Proposal.Fingerprint)] : []);
    }
    public Task<bool> VerifyAsync(CancellationToken ct) => Task.FromResult(Ready);
    public Task<SpecBundleService> OpenAsync(ActorContext actor, bool write, CancellationToken ct)
    { if (actor != WorkspaceToolFixture.Actor || write) throw new SpecStorageException(SpecStorageError.NotAdmitted); return Task.FromResult(new SpecBundleService(this, this)); }
    private SpecInventoryItem[] Items() => Entries.SelectMany(a => new SpecInventoryItem[] {
        new(new(new(a.Source.Identity, SpecObjectKind.Original, a.Original), a.Source.Digest, a.Source.ByteCount), a.Source.Format, "published", null),
        new(new(new(a.Source.Identity, SpecObjectKind.Extraction, a.Extraction), a.Evidence.Identity.EvidenceDigest, a.Evidence.CopyEvidenceBytes().Length), a.Source.Format, "published", a.Evidence.Outcome,
            a.Extraction == ExtractionId ? StoredIdentityOverride ?? a.Evidence.Identity : a.Evidence.Identity) }).ToArray();
    public Task<SpecBundleInventory> InventoryAsync(SpecBundleScope scope, CancellationToken ct)
    { Admit(scope); return Task.FromResult(new SpecBundleInventory(new(scope, BundleVersion, false), Items())); }
    public Task<SpecObjectReference> FindAsync(SpecBundleScope scope, Guid id, CancellationToken ct)
    { Admit(scope); return Task.FromResult(Items().Single(i => i.Reference.Id.ObjectId == id).Reference); }
    public Task<byte[]> ReadAsync(SpecBundleScope scope, SpecObjectReference reference, CancellationToken ct)
    {
        Admit(scope); Reads++; AfterRead?.Invoke();
        var selected = Entries
            .Single(a => a.Source.Identity == reference.Id.Source);
        return Task.FromResult(reference.Id.Kind == SpecObjectKind.Original ? selected.Source.CopyBytes() : ReplacedEvidence ?? selected.Evidence.CopyEvidenceBytes());
    }
    public Task<SpecBundleStatus> AuthorizeAsync(SpecBundleScope scope, bool write, CancellationToken ct) { Admit(scope); return Task.FromResult(new SpecBundleStatus(scope, 3, false)); }
    public Task<SpecBundleStatus> CreateAsync(SpecBundleScope s, Guid k, CancellationToken ct) => throw new NotSupportedException();
    public Task<SpecObjectReservation> ReserveAsync(SpecObjectReference r, SpecSourceFormat f, Guid a, long v, SpecExtractionIdentity? e, CancellationToken ct) => throw new NotSupportedException();
    public Task<SpecBundleStatus> TombstoneAsync(SpecBundleScope s, SpecSourceIdentity? i, long v, Guid k, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<SpecRetirementClaim>> RetirementsAsync(SpecBundleScope s, CancellationToken ct) => throw new NotSupportedException();
    public Task<SpecPublishResult> PublishAsync(SpecBundleScope s, SpecPublicationClaim c, Stream b, CancellationToken ct) => throw new NotSupportedException();
    public Task<SpecRetireResult> RetireAsync(SpecBundleScope s, SpecRetirementClaim c, CancellationToken ct) => throw new NotSupportedException();
    public Task<CreationAuthority> CheckAsync(ActorContext actor, SpecBundleSnapshot snapshot, CancellationToken ct) =>
        Task.FromResult(!Denied && actor == WorkspaceToolFixture.Actor && snapshot.Digest == Specification.Snapshot.Digest ? Stale ? CreationAuthority.Stale : CreationAuthority.Current : CreationAuthority.Revoked);
    internal void MakeTerminal()
    {
        var facts = Receipt!.Facts; var start = facts.ReservedAtUtc;
        using var empty = System.Text.Json.JsonDocument.Parse("[]");
        var evidence = IngestionEvidenceDocument.Create(new(IngestionBackendStatus.Succeeded,
            new(facts.FutureRevision, facts.Intent.NativeDigest, facts.BackendProfile, true), true, 0, 0, false), empty.RootElement);
        var attempt = new IngestionAttemptReceipt(1, start, start.AddSeconds(2), IngestionAttemptCompletion.FromValidation(evidence), IngestionAdmissionClassification.EligibleValid);
        var committed = new CommittedRevisionFacts(facts.FutureRevision, 1, facts.NativeObject, facts.BackendProfile, facts.Intent.LogicalDocument,
            facts.Intent.DependencyIdentity, facts.OriginalActor, start.AddSeconds(3), facts.OperationId, 1, IngestionAdmissionClassification.EligibleValid);
        Receipt = new(facts, IngestionPhase.Committed, 1, 1, 1, start.AddSeconds(1), 1, [attempt], committed, start.AddSeconds(3));
    }
    public Task<CreationCompilationResult> CompileAsync(ProjectScope p, Guid id, MappingCreationPlan plan, SpecBundleSnapshot snapshot, CancellationToken ct)
    {
        Compiles++; AfterCompile?.Invoke();
        return Task.FromResult(new CreationCompilationResult(new(p, id, plan.CompilerDigest, WrongSnapshot ? Sha256Digest.FromBytes("other"u8) : snapshot.Digest,
            "portable-fixture", "portable-fixture", "native-fixture"u8, "{}"u8, "{}"u8), null));
    }
    internal async Task<MappingCreationProposal> Generate(bool criticalQuestion = false)
    {
        var proposal = await new MappingCreationGenerationService(this, this).GenerateAsync(WorkspaceToolFixture.Actor, Guid.NewGuid(), Guid.NewGuid(),
            Specification, Plan, new([new(Source.Identity, 0)], [new("a1", "The field is text", true)], criticalQuestion ? [new("Confirm output", true)] : []), new("Mapping", ""));
        State = new(proposal); return proposal;
    }
    public Task<MappingCreationState?> RetainAsync(ActorContext actor, MappingCreationProposal proposal, CancellationToken ct)
    {
        if (Denied || actor.PrincipalId != proposal.Author) return Task.FromResult<MappingCreationState?>(null);
        if (State is not null && State.Proposal.Fingerprint != proposal.Fingerprint) throw new InvalidOperationException();
        State ??= new(proposal);
        AfterRetain?.Invoke();
        if (LoseRetentionAcknowledgement) throw new IOException("Fixture lost acknowledgement");
        return Task.FromResult<MappingCreationState?>(State);
    }
    public Task<MappingCreationState?> ReadAsync(ActorContext actor, ProjectScope p, Guid id, CancellationToken ct) =>
        Task.FromResult(!Denied && State?.Proposal.Author == actor.PrincipalId && State.Proposal.Project == p && State.Proposal.Id == id ? State : null);
    public Task<IMappingCreationApprovalLease?> AcquireAsync(ActorContext actor, ProjectScope p, Guid id, Sha256Digest digest, CreationApprovalDecision? decision, CancellationToken ct)
    {
        Leases++;
        if (Denied || State is null || State.Proposal.Author != actor.PrincipalId || State.Proposal.Fingerprint != digest || decision is null && !State.Approved)
            return Task.FromResult<IMappingCreationApprovalLease?>(null);
        State = State with { Approval = decision ?? State.Approval }; return Task.FromResult<IMappingCreationApprovalLease?>(new Lease(State));
    }
    private sealed class Lease(MappingCreationState state) : IMappingCreationApprovalLease
    { public MappingCreationState State => state; public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    public Task<CreationPreviewEvidence> RunAsync(MappingCreationProposal proposal, ReadOnlyMemory<byte> sample, CancellationToken ct)
    {
        AfterPreview?.Invoke();
        return Task.FromResult(new CreationPreviewEvidence(new(proposal.Project, proposal.Id, proposal.Fingerprint, proposal.Compilation.NativeDigest,
            Sha256Digest.FromBytes(sample.Span)), CreationPreviewOutcome.Succeeded, "{\"b\":\"x\"}"u8, "json", "application/json"));
    }
    public async Task<RevisionIngestionResult> CreateAsync(ActorContext actor, ProjectScope project, Guid key, CreateRevisionRequest request, Stream native, CancellationToken ct = default)
    {
        Creates++; using var bytes = new MemoryStream(); await native.CopyToAsync(bytes, ct);
        var intent = IngestionIntent.ForCreate(request.Metadata, request.BackendId, request.OriginalFilename, Sha256Digest.FromBytes(bytes.ToArray()), bytes.Length);
        var scope = new RevisionScope(project.OrganizationId, project.ProjectId, Guid.NewGuid(), Guid.NewGuid());
        Receipt ??= new(new(project, Guid.NewGuid(), actor.PrincipalId, key, CanonicalFingerprint.Create(actor.PrincipalId, project, intent), intent, scope,
            new(PrivateObjectId.Create(scope, PrivateObjectClass.NativeArtifact), intent.NativeDigest, intent.NativeByteCount),
            new("ferrule", "fixture", "fixture", "fixture"), new(IngestionAdmissionPolicy.InitialIdentity, 4 * 1024 * 1024), DateTimeOffset.UtcNow),
            IngestionPhase.Reserved, 0, 0, null, null, null, []);
        return RevisionIngestionResult.ForReceipt(RevisionIngestionOutcome.InProgress, RevisionIngestionReceipt.FromMetadata(Receipt));
    }
    public Task<RevisionIngestionResult> ResumeAsync(ActorContext actor, ProjectScope p, Guid operationId, CancellationToken ct = default)
    { Assert.Equal(Receipt!.Facts.OperationId, operationId); Resumes++; return Task.FromResult(RevisionIngestionResult.ForReceipt(RevisionIngestionOutcome.InProgress, RevisionIngestionReceipt.FromMetadata(Receipt))); }
    public Task<RevisionIngestionResult> AppendAsync(ActorContext actor, MappingScope p, Guid k, AppendRevisionRequest r, Stream s, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<RevisionIngestionReceipt?> GetOwnReceiptAsync(ActorContext actor, ProjectScope p, Guid id, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IngestionReceipt?> FindOwnReceiptAsync(ActorContext actor, ProjectScope p, IngestionOperationKind kind, MappingScope? target, Guid key, CancellationToken ct = default) => Task.FromResult(Receipt);
    public Task<IngestionAppendTarget?> GetAppendTargetAsync(ActorContext actor, MappingScope target, CancellationToken ct = default) => throw new NotSupportedException();
}
