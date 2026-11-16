using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Braid.Application;
using Braid.Contracts.Projections;
using Braid.Contracts.Validation;
using Braid.Domain;

namespace Braid.Tests;

/// <summary>
/// Independently controlled synthetic ports. Claims, clocks, completions and acknowledgement faults
/// here are synthetic and do not qualify PostgreSQL, filesystem, process or native-worker behavior.
/// Cached wire bytes use the manually authored codec test oracle, never the product envelope encoder.
/// </summary>
internal sealed class RevisionReportServiceTestPorts : IRevisionReportMetadataReader,
    IRevisionReportMetadataStore, IRevisionReportProfileRegistry, IRevisionReportFormatValidator,
    IRevisionReportBackend, IPrivateObjectStore
{
    internal static readonly RevisionScope Scope = new(
        Guid.Parse("1059fe00-0000-4000-8000-000000000001"), Guid.Parse("1059fe00-0000-4000-8000-000000000002"),
        Guid.Parse("1059fe00-0000-4000-8000-000000000003"), Guid.Parse("1059fe00-0000-4000-8000-000000000004"));
    internal static readonly ActorContext Actor = new(new(Guid.Parse("1059fe00-0000-4000-8000-000000000005")));
    internal static readonly Guid Instance = Guid.Parse("1059fe00-0000-4000-8000-000000000006");
    internal static readonly Guid CacheId = Guid.Parse("1059fe00-0000-4000-8000-000000000007");
    internal static readonly Guid DerivationId = Guid.Parse("1059fe00-0000-4000-8000-000000000008");
    internal static readonly Guid CandidateId = Guid.Parse("1059fe00-0000-4000-8000-000000000009");
    internal static readonly DateTimeOffset Start = new(2026, 10, 14, 21, 0, 0, TimeSpan.Zero);
    internal static readonly RevisionReportBackendProfile Profile = new("neutral", "synthetic-v1", "synthetic-build", "synthetic-adapter");

    private readonly Dictionary<RevisionReportOperation, RevisionReportRegistration> registrations = [];
    private readonly Dictionary<Guid, AuthorizedRevisionReportAttempt> retained = [];
    private readonly Dictionary<Guid, byte[]> objects = [];
    internal RevisionReportServiceTestPorts(RevisionReportPolicy? policy = null, int nativeCap = 4_194_304,
        int derivedCap = 16_777_216, int? derivedReadCap = null, int sourceBytes = 3)
    {
        NativeBytes = Enumerable.Range(1, sourceBytes).Select(value => unchecked((byte)value)).ToArray();
        Source = new(Scope, new(PrivateObjectId.Restore(Scope,
            Guid.Parse("1059fe00-0000-4000-8000-00000000000a"), PrivateObjectClass.NativeArtifact),
            Digest(NativeBytes), NativeBytes.LongLength), "a/../logical~1.json", "synthetic-dependencies", Profile.BackendId);
        objects.Add(Source.NativeObject.Id.ObjectId, NativeBytes);
        foreach (var operation in new[] { RevisionReportOperation.Projection, RevisionReportOperation.Validation })
        {
            registrations.Add(operation, new(Profile, Format(operation), policy ?? new(), () =>
            {
                BackendFactoryCalls++;
                return this;
            }));
        }
        Storage = new(nativeCap, derivedCap, derivedReadCap ?? derivedCap, () =>
        {
            StorageFactoryCalls++;
            return this;
        });
    }

    internal RevisionReportSource Source { get; }
    internal byte[] NativeBytes { get; }
    internal RevisionReportStorageRegistration Storage { get; }
    internal bool Authorized { get; set; } = true;
    internal bool CurrentProfileAvailable { get; set; } = true;
    internal bool StoredFormatAvailable { get; set; } = true;
    internal int SourceCalls { get; private set; }
    internal int CurrentCalls { get; private set; }
    internal int RetainedCalls { get; private set; }
    internal int ClaimCalls { get; private set; }
    internal int FinishCalls { get; private set; }
    internal int RepairCalls { get; private set; }
    internal int BackendFactoryCalls { get; private set; }
    internal int BackendCalls { get; private set; }
    internal int StorageFactoryCalls { get; private set; }
    internal int NativeReads { get; private set; }
    internal int DerivedReads { get; private set; }
    internal int PublishCalls { get; private set; }
    internal int CurrentProfileLookups { get; private set; }
    internal Guid? LastRetainedId { get; private set; }
    internal RevisionReportKey? LastCurrentKey { get; private set; }
    internal RevisionReportClaim? Claimed { get; private set; }
    internal RevisionReportCompletion? FinishedCompletion { get; private set; }
    internal bool FinishTokenWasCanceled { get; private set; }
    internal RevisionReportReadSnapshot? Snapshot { get; set; }
    internal Func<int, AuthorizedRevisionReportSource?>? OnSource { get; set; }
    internal Func<RevisionReportKey, int, RevisionReportReadSnapshot?>? OnCurrent { get; set; }
    internal Func<Guid, AuthorizedRevisionReportAttempt?>? OnRetained { get; set; }
    internal Func<RevisionReportClaim, RevisionReportCompletion, RevisionReportStoreResult>? OnFinish { get; set; }
    internal Func<RevisionReportKey, NativeArtifact, CancellationToken, Task<RevisionReportBackendResult>>? OnBackend { get; set; }
    internal Func<PrivateObjectReference, byte[]>? OnRead { get; set; }
    internal Func<PrivateObjectReference, PrivateObjectPublishResult>? OnPublish { get; set; }

    internal RevisionReportService Service(RevisionReportPolicy? activeReadCaps = null) =>
        new(this, this, this, Storage, Instance, activeReadCaps);

    internal RevisionReportKey Key(RevisionReportOperation operation = RevisionReportOperation.Projection,
        RevisionReportPolicy? policy = null, RevisionReportBackendProfile? profile = null) => operation switch
        {
            RevisionReportOperation.Projection => new ProjectionCacheKey(Source, profile ?? Profile,
                "braid.mapping-projection.v1", "synthetic.project.v1", "braid.revision-report-codec.v1", policy ?? new()),
            RevisionReportOperation.Validation => new ValidationCacheKey(Source, profile ?? Profile,
                "braid.mapping-validation.v1", "synthetic.validate.v1", "braid.revision-report-codec.v1", policy ?? new()),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

    internal AuthorizedRevisionReportSource Authority(RevisionReportKey key, DateTimeOffset? observed = null) =>
        new(key.Source, Actor.PrincipalId, observed ?? Start.AddSeconds(3));

    internal RevisionReportClaim Claim(RevisionReportKey key, Guid? id = null, long generation = 1,
        Guid? candidateId = null, DateTimeOffset? start = null, Guid? repairId = null) =>
        new(CacheId, id ?? DerivationId, key, Actor.PrincipalId, Instance,
            Guid.Parse("1059fe00-0000-4000-8000-00000000000b"), generation, start ?? Start,
            (start ?? Start).AddSeconds(60), PrivateObjectId.Restore(Scope, candidateId ?? CandidateId,
                PrivateObjectClass.DerivedArtifact), repairId);

    internal RevisionReportReadSnapshot Pending(RevisionReportKey key) =>
        new(Authority(key), key, CacheId, 1, null,
            new(DerivationId, 1, Start, Start.AddSeconds(60), null, null));

    internal RevisionReportReadSnapshot RetryDelayed(RevisionReportKey key) =>
        new(Authority(key, Start.AddSeconds(10)), key, CacheId, 1, null,
            new(DerivationId, 1, Start, Start.AddSeconds(60), Start.AddSeconds(2),
                RevisionReportCompletion.ForFailure(RevisionReportFailureCategory.BackendUnavailable)));

    internal RevisionReportResult Cache(RevisionReportKey key, string? reportJson = null,
        RevisionReportObservation? observation = null, Guid? id = null, Guid? candidateId = null)
    {
        var claim = Claim(key, id, candidateId: candidateId);
        reportJson ??= key is ProjectionCacheKey projection
            ? RevisionReportCodecTestSupport.ProjectionJson(projection)
            : RevisionReportCodecTestSupport.ValidationJson((ValidationCacheKey)key);
        var bytes = Encoding.UTF8.GetBytes(RevisionReportCodecTestSupport.Envelope(claim, reportJson));
        var result = Metadata(claim, new(claim.CandidateObjectId, Digest(bytes), bytes.LongLength),
            observation ?? Observation(key));
        objects[claim.CandidateObjectId.ObjectId] = bytes;
        Retain(claim, RevisionReportCompletion.ForUsable(key, result.Observation, result.DerivedObject), result);
        Snapshot = new(Authority(key), key, CacheId, claim.Generation, result,
            retained[claim.DerivationId].Attempt);
        return result;
    }

    internal AuthorizedRevisionReportAttempt Retain(RevisionReportClaim claim, RevisionReportCompletion completion,
        RevisionReportResult? result = null)
    {
        var attempt = new RevisionReportAttemptSummary(claim.DerivationId, claim.Generation, claim.StartedAtUtc,
            claim.LeaseExpiresAtUtc, claim.StartedAtUtc.AddSeconds(2), completion, claim.RepairTargetDerivationId);
        var value = new AuthorizedRevisionReportAttempt(Authority(claim.Key), claim.Key, claim.CacheEntryId, attempt, result);
        retained[claim.DerivationId] = value;
        return value;
    }

    internal static RevisionReportResult Metadata(RevisionReportClaim claim, PrivateObjectReference reference,
        RevisionReportObservation observation) => new(claim.DerivationId, claim.CacheEntryId, claim.Key,
            claim.Generation, claim.OriginalActor, claim.StartedAtUtc, claim.StartedAtUtc.AddSeconds(2), reference, observation);

    internal static RevisionReportObservation Observation(RevisionReportKey key, int items = 0, bool valid = true,
        int issues = 0) => new(key.Operation,
            valid ? RevisionReportBackendStatus.Succeeded : RevisionReportBackendStatus.InvalidMapping,
            new(key.Scope, key.NativeDigest, key.Profile, true),
            key.Operation == RevisionReportOperation.Projection ? new(RevisionReportCoverage.Complete, items, 0, 0, 0) : null,
            key.Operation == RevisionReportOperation.Validation ? new(valid, issues, issues, false, issues) : null);

    internal static ProjectionReportV1 Projection(ProjectionCacheKey key, string parameters = "[]") =>
        JsonSerializer.Deserialize<ProjectionReportV1>(RevisionReportCodecTestSupport.ProjectionJson(key, parameters),
            ProjectionJson.CreateOptions())!;

    internal static ValidationReportV1 Validation(ValidationCacheKey key, string diagnostics, int issues = 1) =>
        JsonSerializer.Deserialize<ValidationReportV1>($$"""
            {"status":"invalidMapping","provenance":{{RevisionReportCodecTestSupport.OperationProvenanceJson(key)}},"isValid":false,"totalIssueCount":{{issues}},"emittedIssueCount":{{issues}},"truncated":false,"diagnostics":{{diagnostics}}}
            """, ProjectionJson.CreateOptions())!;

    internal byte[] ObjectBytes(RevisionReportResult result) => objects[result.DerivedObject.Id.ObjectId];

    public Task<AuthorizedRevisionReportSource?> GetSourceAsync(ActorContext actor, RevisionScope scope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SourceCalls++;
        return Task.FromResult(OnSource is not null ? OnSource(SourceCalls) :
            Authorized ? new AuthorizedRevisionReportSource(Source, actor.PrincipalId, Start.AddSeconds(3)) : null);
    }

    public Task<RevisionReportReadSnapshot?> GetCurrentAsync(ActorContext actor, RevisionReportKey key,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CurrentCalls++;
        LastCurrentKey = key;
        if (!Authorized) return Task.FromResult<RevisionReportReadSnapshot?>(null);
        return Task.FromResult(OnCurrent is not null ? OnCurrent(key, CurrentCalls) :
            Snapshot ?? new RevisionReportReadSnapshot(Authority(key), key, null, 0, null, null));
    }

    public Task<AuthorizedRevisionReportAttempt?> GetRetainedAsync(ActorContext actor, RevisionScope scope,
        RevisionReportOperation operation, Guid derivationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RetainedCalls++;
        LastRetainedId = derivationId;
        return Task.FromResult(!Authorized ? null : OnRetained is not null ? OnRetained(derivationId) :
            retained.GetValueOrDefault(derivationId));
    }

    public Task<RevisionReportStoreResult> TryClaimAsync(ActorContext actor, RevisionReportKey key,
        Guid serverInstanceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ClaimCalls++;
        var claim = Claim(key);
        var snapshot = Pending(key);
        Claimed = claim;
        Snapshot = snapshot;
        // An accepted synthetic claim is visible to exact retained lookup while still OPEN,
        // as the real metadata port is. Cancellation cleanup must prove this exact open intent
        // before finishing it; absence/refusal overrides deliberately remain absent/refused.
        retained[claim.DerivationId] = new(Authority(key), key, claim.CacheEntryId,
            snapshot.CurrentAttempt!, null);
        return Task.FromResult(RevisionReportStoreResult.ForClaim(snapshot, claim));
    }

    public Task<RevisionReportStoreResult> FinishAsync(ActorContext actor, RevisionReportClaim claim,
        RevisionReportCompletion completion, CancellationToken cancellationToken = default)
    {
        FinishTokenWasCanceled = cancellationToken.IsCancellationRequested;
        cancellationToken.ThrowIfCancellationRequested();
        FinishCalls++;
        FinishedCompletion = completion;
        var result = completion.IsUsable ? Metadata(claim, completion.DerivedObject!, completion.Observation!) : null;
        var exact = Retain(claim, completion, result);
        var snapshot = new RevisionReportReadSnapshot(Authority(claim.Key), claim.Key, claim.CacheEntryId,
            claim.Generation, result, exact.Attempt);
        Snapshot = snapshot;
        return Task.FromResult(OnFinish is not null ? OnFinish(claim, completion) :
            RevisionReportStoreResult.ForSnapshot(RevisionReportStoreOutcome.Applied, snapshot));
    }

    public Task<RevisionReportStoreResult> RepairAsync(ActorContext actor, RevisionReportRepairTarget target,
        Guid serverInstanceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RepairCalls++;
        throw new InvalidOperationException("These synthetic tests never authorize a repair write.");
    }

    public RevisionReportRegistration? FindCurrent(string sourceBackendId, RevisionReportOperation operation)
    {
        CurrentProfileLookups++;
        return CurrentProfileAvailable && sourceBackendId == Source.SourceBackendId ? registrations[operation] : null;
    }

    public IRevisionReportFormatValidator? FindStored(RevisionReportFormat format) => StoredFormatAvailable ? this : null;
    public void Validate(RevisionReportKey key, ProjectionReportV1 report, CancellationToken cancellationToken = default) =>
        cancellationToken.ThrowIfCancellationRequested();
    public void Validate(RevisionReportKey key, ValidationReportV1 report, CancellationToken cancellationToken = default) =>
        cancellationToken.ThrowIfCancellationRequested();

    public Task<RevisionReportBackendResult> DeriveAsync(RevisionReportKey key, NativeArtifact artifact,
        CancellationToken cancellationToken = default)
    {
        BackendCalls++;
        Assert.Equal(key.Scope, artifact.Scope);
        Assert.Equal(key.NativeDigest, artifact.Digest);
        Assert.Equal(NativeBytes, artifact.CopyBytes());
        if (OnBackend is not null) return OnBackend(key, artifact, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(RevisionReportBackendResult.ForProjection((ProjectionCacheKey)key,
            Projection((ProjectionCacheKey)key), Observation(key), cancellationToken));
    }

    public async Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(RevisionScope expectedScope,
        PrivateObjectReference reference, Stream source, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Assert.Equal(expectedScope, reference.Id.Scope);
        Assert.Equal(PrivateObjectClass.DerivedArtifact, reference.Id.Class);
        PublishCalls++;
        using var copy = new MemoryStream();
        await source.CopyToAsync(copy, cancellationToken);
        var bytes = copy.ToArray();
        Assert.Equal(reference.ByteCount, bytes.LongLength);
        Assert.Equal(reference.Digest, Digest(bytes));
        var outcome = OnPublish?.Invoke(reference) ?? PrivateObjectPublishResult.Created;
        if (outcome == PrivateObjectPublishResult.Created) objects.Add(reference.Id.ObjectId, bytes);
        return outcome;
    }

    public Task<byte[]> ReadVerifiedAsync(RevisionScope expectedScope, PrivateObjectReference reference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Assert.Equal(expectedScope, reference.Id.Scope);
        if (reference.Id.Class == PrivateObjectClass.NativeArtifact) NativeReads++;
        else DerivedReads++;
        // Deliberately expose a mutable port-owned alias: the service must own accepted bytes/payloads.
        return Task.FromResult(OnRead?.Invoke(reference) ?? objects[reference.Id.ObjectId]);
    }

    private static RevisionReportFormat Format(RevisionReportOperation operation) => new(Profile.BackendId, operation,
        operation == RevisionReportOperation.Projection ? "braid.mapping-projection.v1" : "braid.mapping-validation.v1",
        operation == RevisionReportOperation.Projection ? "synthetic.project.v1" : "synthetic.validate.v1",
        "braid.revision-report-codec.v1");

    private static Sha256Digest Digest(byte[] bytes) => Sha256Digest.Parse(Convert.ToHexStringLower(SHA256.HashData(bytes)));
}
