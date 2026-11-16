using System.Text;

namespace Braid.Domain;

public enum IngestionOperationKind { Create = 1, Append = 2 }

public enum IngestionPhase
{
    Reserved = 1,
    Staged = 2,
    Ready = 3,
    Committed = 4,
    Rejected = 5,
    HeadConflict = 6,
    AttemptExhausted = 7
}

public enum IngestionAdmissionClassification
{
    EligibleValid = 1,
    EligibleDecodedInvalid = 2,
    RejectedNativeDecode = 3,
    RejectedUnsupported = 4,
    RejectedResourceLimit = 5,
    RetryableFailure = 6,
    ReusedEvidence = 7
}

public abstract record HeadCondition
{
    private protected HeadCondition() { }
}

public sealed record AnyHead : HeadCondition;

public sealed record EqualHead : HeadCondition
{
    public EqualHead(Guid revisionId) => RevisionId = IngestionValue.RequireId(revisionId, nameof(revisionId));
    public Guid RevisionId { get; }
}

public sealed record IngestionCreateMetadata
{
    public IngestionCreateMetadata(string name, string description)
    {
        ArgumentNullException.ThrowIfNull(name);
        var trimmedName = name.Trim();
        ArgumentException.ThrowIfNullOrWhiteSpace(trimmedName, nameof(name));
        IngestionValue.RequireScalarText(trimmedName, 200, nameof(name));
        IngestionValue.RequireScalarText(description, 4000, nameof(description));
        Name = trimmedName;
        Description = description;
    }

    public string Name { get; }
    public string Description { get; }
}

public sealed record IngestionBackendProfile
{
    public IngestionBackendProfile(string backendId, string engineRevision, string backendBuild, string adapterBuild)
    {
        BackendId = IngestionValue.RequireText(backendId, 128, nameof(backendId));
        EngineRevision = IngestionValue.RequireText(engineRevision, 256, nameof(engineRevision));
        BackendBuild = IngestionValue.RequireText(backendBuild, 2048, nameof(backendBuild));
        AdapterBuild = IngestionValue.RequireText(adapterBuild, 256, nameof(adapterBuild));
    }

    public string BackendId { get; }
    public string EngineRevision { get; }
    public string BackendBuild { get; }
    public string AdapterBuild { get; }
}

public sealed record IngestionAdmissionPolicy
{
    public const string InitialIdentity = "braid.native-admission.v1";
    public const long MaximumNativeByteCount = 4 * 1024 * 1024;

    public IngestionAdmissionPolicy(string identity, long effectiveNativeByteLimit)
    {
        Identity = IngestionValue.RequireText(identity, 128, nameof(identity));
        IngestionValue.RequireNativeCount(effectiveNativeByteLimit, nameof(effectiveNativeByteLimit));
        EffectiveNativeByteLimit = effectiveNativeByteLimit;
    }

    public string Identity { get; }
    public long EffectiveNativeByteLimit { get; }
}

/// <summary>Exact immutable upload intent, established before reservation without retaining native bytes.</summary>
public sealed record IngestionIntent
{
    public const string EntryDocument = "entry.native.json";
    public const string EmptyDependencyIdentity = "braid.dependencies.empty.v1";

    private IngestionIntent(IngestionOperationKind kind, MappingScope? target, IngestionCreateMetadata? metadata,
        string backendId, string? originalFilename, HeadCondition headCondition, Sha256Digest nativeDigest,
        long nativeByteCount, InterchangeImportProvenance? importProvenance = null)
    {
        ArgumentNullException.ThrowIfNull(headCondition);
        ArgumentNullException.ThrowIfNull(nativeDigest);
        if (headCondition is not (AnyHead or EqualHead))
            throw new ArgumentException("The head condition must be known.", nameof(headCondition));
        IngestionValue.RequireNativeCount(nativeByteCount, nameof(nativeByteCount));

        Kind = kind;
        Target = target;
        CreateMetadata = metadata;
        BackendId = IngestionValue.RequireText(backendId, 128, nameof(backendId));
        OriginalFilename = RequireFilename(originalFilename);
        HeadCondition = headCondition;
        NativeDigest = nativeDigest;
        NativeByteCount = nativeByteCount;
        if (importProvenance is not null && (importProvenance.NativeDigest != nativeDigest ||
            importProvenance.Engine.BackendId != backendId))
            throw new ArgumentException("Import provenance must bind the exact native digest and backend.", nameof(importProvenance));
        ImportProvenance = importProvenance;
    }

    public IngestionOperationKind Kind { get; }
    public MappingScope? Target { get; }
    public IngestionCreateMetadata? CreateMetadata { get; }
    public string BackendId { get; }
    public string OriginalFilename { get; }
    public HeadCondition HeadCondition { get; }
    public Sha256Digest NativeDigest { get; }
    public long NativeByteCount { get; }
    public InterchangeImportProvenance? ImportProvenance { get; }
    public string LogicalDocument => EntryDocument;
    public string DependencyIdentity => EmptyDependencyIdentity;

    public static IngestionIntent ForCreate(IngestionCreateMetadata metadata, string backendId,
        string? originalFilename, Sha256Digest nativeDigest, long nativeByteCount,
        InterchangeImportProvenance? importProvenance = null)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return new(IngestionOperationKind.Create, null, metadata, backendId, originalFilename, new AnyHead(),
            nativeDigest, nativeByteCount, importProvenance);
    }

    public static IngestionIntent ForAppend(MappingScope target, string backendId, string? originalFilename,
        HeadCondition headCondition, Sha256Digest nativeDigest, long nativeByteCount,
        InterchangeImportProvenance? importProvenance = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new(IngestionOperationKind.Append, target, null, backendId, originalFilename, headCondition,
            nativeDigest, nativeByteCount, importProvenance);
    }

    private static string RequireFilename(string? value)
    {
        value ??= EntryDocument;
        if (value.Length == 0 || value is "." or ".." || value.Any(character =>
                character is '/' or '\\' || char.IsControl(character)))
            throw new ArgumentException("The display filename must be a safe basename.", nameof(value));
        IngestionValue.RequireUtf8(value, 255, nameof(value));
        return value;
    }
}

/// <summary>Server-reserved immutable facts. Restoring them never adopts current deployment configuration.</summary>
public sealed record ReservedIngestionFacts
{
    public ReservedIngestionFacts(ProjectScope project, Guid operationId, PrincipalId originalActor,
        Guid idempotencyKey, CanonicalFingerprint fingerprint, IngestionIntent intent, RevisionScope futureRevision,
        PrivateObjectReference nativeObject, IngestionBackendProfile backendProfile,
        IngestionAdmissionPolicy admissionPolicy, DateTimeOffset reservedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(originalActor);
        ArgumentNullException.ThrowIfNull(fingerprint);
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(futureRevision);
        ArgumentNullException.ThrowIfNull(nativeObject);
        ArgumentNullException.ThrowIfNull(backendProfile);
        ArgumentNullException.ThrowIfNull(admissionPolicy);
        IngestionValue.RequireId(operationId, nameof(operationId));
        IngestionValue.RequireId(idempotencyKey, nameof(idempotencyKey));

        if (futureRevision.OrganizationId != project.OrganizationId || futureRevision.ProjectId != project.ProjectId ||
            (intent.Target is { } target && target.MappingId != futureRevision.MappingId) ||
            nativeObject.Id.Scope != futureRevision || nativeObject.Id.Class != PrivateObjectClass.NativeArtifact ||
            nativeObject.Digest != intent.NativeDigest || nativeObject.ByteCount != intent.NativeByteCount ||
            backendProfile.BackendId != intent.BackendId || admissionPolicy.EffectiveNativeByteLimit < intent.NativeByteCount ||
            (intent.ImportProvenance is { } imported && imported.Engine != backendProfile) ||
            fingerprint != CanonicalFingerprint.Create(originalActor, project, intent))
            throw new ArgumentException("Reserved ingestion facts must agree in scope, identity, and immutable intent.");

        Project = project;
        OperationId = operationId;
        OriginalActor = originalActor;
        IdempotencyKey = idempotencyKey;
        Fingerprint = fingerprint;
        Intent = intent;
        FutureRevision = futureRevision;
        NativeObject = nativeObject;
        BackendProfile = backendProfile;
        AdmissionPolicy = admissionPolicy;
        ReservedAtUtc = reservedAtUtc.ToUniversalTime();
    }

    public ProjectScope Project { get; }
    public Guid OperationId { get; }
    public PrincipalId OriginalActor { get; }
    public Guid IdempotencyKey { get; }
    public CanonicalFingerprint Fingerprint { get; }
    public IngestionIntent Intent { get; }
    public RevisionScope FutureRevision { get; }
    public PrivateObjectReference NativeObject { get; }
    public IngestionBackendProfile BackendProfile { get; }
    public IngestionAdmissionPolicy AdmissionPolicy { get; }
    public DateTimeOffset ReservedAtUtc { get; }
}

public sealed record CommittedRevisionFacts
{
    public CommittedRevisionFacts(RevisionScope scope, long sequence, PrivateObjectReference nativeObject,
        IngestionBackendProfile backendProfile, string logicalDocument, string dependencyIdentity, PrincipalId creator,
        DateTimeOffset committedAtUtc, Guid operationId, long initialAdmissionAttemptGeneration,
        IngestionAdmissionClassification initialAdmission)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(nativeObject);
        ArgumentNullException.ThrowIfNull(backendProfile);
        ArgumentNullException.ThrowIfNull(creator);
        ArgumentOutOfRangeException.ThrowIfLessThan(sequence, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(initialAdmissionAttemptGeneration, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(initialAdmissionAttemptGeneration, 8);
        IngestionValue.RequireId(operationId, nameof(operationId));
        if (initialAdmission is not (IngestionAdmissionClassification.EligibleValid or
            IngestionAdmissionClassification.EligibleDecodedInvalid))
            throw new ArgumentOutOfRangeException(nameof(initialAdmission), "A committed revision requires eligible admission.");
        if (nativeObject.Id.Scope != scope || nativeObject.Id.Class != PrivateObjectClass.NativeArtifact ||
            logicalDocument != IngestionIntent.EntryDocument || dependencyIdentity != IngestionIntent.EmptyDependencyIdentity)
            throw new ArgumentException("Committed revision facts must agree with the native revision identity.");
        IngestionValue.RequireNativeCount(nativeObject.ByteCount, nameof(nativeObject));

        Scope = scope;
        Sequence = sequence;
        NativeObject = nativeObject;
        BackendProfile = backendProfile;
        LogicalDocument = logicalDocument;
        DependencyIdentity = dependencyIdentity;
        Creator = creator;
        CommittedAtUtc = committedAtUtc.ToUniversalTime();
        OperationId = operationId;
        InitialAdmissionAttemptGeneration = initialAdmissionAttemptGeneration;
        InitialAdmission = initialAdmission;
    }

    public RevisionScope Scope { get; }
    public long Sequence { get; }
    public PrivateObjectReference NativeObject { get; }
    public IngestionBackendProfile BackendProfile { get; }
    public string LogicalDocument { get; }
    public string DependencyIdentity { get; }
    public PrincipalId Creator { get; }
    public DateTimeOffset CommittedAtUtc { get; }
    public Guid OperationId { get; }
    public long InitialAdmissionAttemptGeneration { get; }
    public IngestionAdmissionClassification InitialAdmission { get; }
}

internal static class IngestionValue
{
    internal static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static Guid RequireId(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("An ingestion identifier cannot be empty.", parameterName);
        return value;
    }

    internal static void RequireNativeCount(long value, string parameterName)
    {
        if (value is < 1 or > IngestionAdmissionPolicy.MaximumNativeByteCount)
            throw new ArgumentOutOfRangeException(parameterName, "Native byte counts must be within the admission ceiling.");
    }

    internal static string RequireText(string value, int maximumBytes, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        RequireUtf8(value, maximumBytes, parameterName);
        return value;
    }

    internal static void RequireUtf8(string value, int maximumBytes, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Contains('\0'))
            throw new ArgumentException("Persisted ingestion text cannot contain NUL.", parameterName);
        try
        {
            if (StrictUtf8.GetByteCount(value) > maximumBytes)
                throw new ArgumentException("Ingestion text exceeds its UTF8 byte limit.", parameterName);
        }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException("Ingestion text must contain valid Unicode.", parameterName);
        }
    }

    internal static void RequireScalarText(string value, int maximumScalars, string parameterName)
    {
        RequireUtf8(value, 4 * maximumScalars, parameterName);
        var count = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            _ = rune;
            if (++count > maximumScalars)
                throw new ArgumentException("Ingestion text exceeds its character limit.", parameterName);
        }
    }
}
