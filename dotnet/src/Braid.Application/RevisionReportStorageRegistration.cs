using Braid.Domain;

namespace Braid.Application;

/// <summary>Trusted adapter caps and lazy shared store; composition binds caps to the actual adapter options.</summary>
public sealed class RevisionReportStorageRegistration
{
    private readonly Lazy<IPrivateObjectStore> _store;

    public RevisionReportStorageRegistration(int nativeByteLimit, int derivedByteLimit, int derivedReadByteLimit,
        Func<IPrivateObjectStore> storeFactory)
    {
        if (nativeByteLimit is < 1 or > RevisionReportPolicy.MaximumNativeByteLimit)
            throw new ArgumentOutOfRangeException(nameof(nativeByteLimit));
        if (derivedByteLimit is < 1 or > RevisionReportPolicy.MaximumStoredObjectByteLimit)
            throw new ArgumentOutOfRangeException(nameof(derivedByteLimit));
        if (derivedReadByteLimit < 1 || derivedReadByteLimit > derivedByteLimit)
            throw new ArgumentOutOfRangeException(nameof(derivedReadByteLimit));
        ArgumentNullException.ThrowIfNull(storeFactory);
        NativeByteLimit = nativeByteLimit;
        DerivedByteLimit = derivedByteLimit;
        DerivedReadByteLimit = derivedReadByteLimit;
        _store = new(() => storeFactory() ?? throw new RevisionReportServiceException(RevisionReportFailureCategory.StorageIo));
    }

    public int NativeByteLimit { get; }
    public int DerivedByteLimit { get; }
    public int DerivedReadByteLimit { get; }
    public IPrivateObjectStore GetStore() => _store.Value;

    public RevisionReportPolicy ApplyTo(RevisionReportPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return new(Math.Min(policy.NativeByteLimit, NativeByteLimit), policy.WorkerResponseByteLimit, policy.ItemLimit,
            policy.ResponseItemLimit, policy.JsonDepthLimit, policy.DetailDepthLimit, policy.DiagnosticLimit,
            policy.ValidationIssueLimit, policy.TextByteLimit, policy.HeaderByteLimit,
            Math.Min(policy.StoredObjectByteLimit, DerivedReadByteLimit), policy.ResponseByteLimit);
    }

    public void ValidateNativeSource(RevisionReportKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Source.NativeObject.ByteCount > Math.Min(key.Policy.NativeByteLimit, NativeByteLimit))
            throw new RevisionReportValidationException(RevisionReportValidationFailure.ResourceLimit);
    }

    public void ValidateDerivedReference(RevisionReportKey key, PrivateObjectReference reference, RevisionReportPolicy activeReadCaps)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(activeReadCaps);
        if (reference.Id.Scope != key.Scope || reference.Id.Class != PrivateObjectClass.DerivedArtifact || reference.ByteCount < 1)
            throw new RevisionReportValidationException(RevisionReportValidationFailure.Integrity);
        var maximum = Math.Min(Math.Min(key.Policy.StoredObjectByteLimit, activeReadCaps.StoredObjectByteLimit), DerivedReadByteLimit);
        if (reference.ByteCount > maximum)
            throw new RevisionReportValidationException(RevisionReportValidationFailure.ResourceLimit);
    }
}
