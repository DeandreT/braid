using Weft.Application;
using Weft.Contracts.Examples;
using Weft.Contracts.Validation;
using Weft.Domain;

namespace Weft.Tests;

public sealed class RevisionReportRegistrationTests
{
    [Fact]
    public void KnownAdmissionDoesNotResolveAnUnusedBackendAndBothSignalsMustAllowExecution()
    {
        var calls = 0;
        var admission = new UnusedBackend { CanExecute = false };
        var backend = new UnusedBackend();
        var profile = new RevisionReportBackendProfile("provider", "engine", "build", "adapter");
        var registration = new RevisionReportRegistration(profile, Format(), new(), () => { calls++; return backend; }, admission);
        Assert.False(registration.CanExecute);
        Assert.Equal(0, calls);
        admission.CanExecute = true;
        Assert.True(registration.CanExecute);
        Assert.Equal(0, calls);
        Assert.Same(backend, registration.GetBackend());
        backend.CanExecute = false;
        Assert.False(registration.CanExecute);
        backend.CanExecute = true;
        admission.CanExecute = false;
        Assert.False(registration.CanExecute);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void RegistryOwnsSelectionsAndHistoricalFormatsWithoutResolvingBackends()
    {
        var calls = 0;
        var profile = new RevisionReportBackendProfile("provider", "engine", "build", "adapter");
        var currentFormat = Format();
        var historicalFormat = Format(algorithm: "old.algorithm.v1");
        var backend = new UnusedBackend();
        var current = new RevisionReportRegistration(profile, currentFormat, new(), () => { calls++; return backend; });
        var validator = new UnusedValidator();
        RevisionReportRegistration[] selections = [current];
        RevisionReportStoredFormatRegistration[] formats = [new(historicalFormat, validator)];
        var registry = new RevisionReportProfileRegistry(selections, formats);
        selections[0] = new(profile, currentFormat, new(), () => throw new InvalidOperationException("Must stay lazy."));
        formats[0] = new(currentFormat, new UnusedValidator());

        Assert.Same(current, registry.FindCurrent("provider", RevisionReportOperation.Projection));
        Assert.Null(registry.FindCurrent("Provider", RevisionReportOperation.Projection));
        Assert.Null(registry.FindCurrent("provider", RevisionReportOperation.Validation));
        Assert.Same(validator, registry.FindStored(Format(algorithm: "old.algorithm.v1")));
        Assert.Null(registry.FindStored(currentFormat));
        Assert.True(current.CanExecute);
        Assert.Equal(0, calls);
        Assert.Same(backend, current.GetBackend());
        Assert.Same(backend, current.GetBackend());
        Assert.Equal(1, calls);
        backend.CanExecute = false;
        Assert.False(current.CanExecute);
        backend.CanExecute = true;
        Assert.True(current.CanExecute);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void RegistryRejectsAmbiguousSelectionsAndFormatsEvenWhenInstancesMatch()
    {
        var profile = new RevisionReportBackendProfile("provider", "engine", "build", "adapter");
        var registration = new RevisionReportRegistration(profile, Format(), new(), () => new UnusedBackend());
        var nextBuild = new RevisionReportRegistration(new("provider", "other-engine", "other-build", "adapter"),
            Format(algorithm: "other.algorithm"), new(), () => new UnusedBackend());
        Assert.Throws<ArgumentException>(() => new RevisionReportProfileRegistry([registration, nextBuild], []));
        var stored = new RevisionReportStoredFormatRegistration(Format(), new UnusedValidator());
        Assert.Throws<ArgumentException>(() => new RevisionReportProfileRegistry([], [stored, stored]));
        Assert.Throws<ArgumentException>(() => new RevisionReportRegistration(profile, Format(backend: "other"), new(), () => new UnusedBackend()));
        var registry = new RevisionReportProfileRegistry([], []);
        Assert.Throws<ArgumentOutOfRangeException>(() => registry.FindCurrent("provider", (RevisionReportOperation)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => Format(operation: (RevisionReportOperation)999));
    }

    [Fact]
    public void FormatHasFullOrdinalValueEqualityAndStrictBoundedIdentityText()
    {
        var same = Format();
        Assert.Equal(same, Format());
        Assert.Single(new HashSet<RevisionReportFormat> { same, Format() });
        Assert.NotEqual(same, Format(codec: "other.codec"));
        Assert.NotEqual(same, Format(schema: "other.schema"));
        Assert.NotEqual(same, Format(algorithm: "other.algorithm"));
        Assert.NotEqual(same, Format(operation: RevisionReportOperation.Validation));
        Assert.NotEqual(same, Format(backend: "Provider"));
        foreach (var identity in new[] { "", " ", "provider\0", new string('x', 129), new string('é', 65), new string((char)0xd800, 1) })
            Assert.Throws<ArgumentException>(() => Format(backend: identity));
        Assert.Equal(new string('é', 64), Format(backend: new string('é', 64)).BackendId);
    }

    [Fact]
    public void StorageCapsReduceKeyIdentityAndRefuseBeforeResolvingTheStore()
    {
        var calls = 0;
        var registration = new RevisionReportStorageRegistration(2, 8, 5, () => { calls++; return new UnusedStore(); });
        var oldPolicy = new RevisionReportPolicy(itemLimit: 7, textByteLimit: 100, responseByteLimit: 1000);
        var reduced = registration.ApplyTo(oldPolicy);
        Assert.Equal(2, reduced.NativeByteLimit); Assert.Equal(5, reduced.StoredObjectByteLimit);
        Assert.Equal(7, reduced.ItemLimit); Assert.Equal(100, reduced.TextByteLimit); Assert.Equal(1000, reduced.ResponseByteLimit);
        Assert.NotEqual(oldPolicy.Fingerprint, reduced.Fingerprint);
        var key = Key(oldPolicy);
        Assert.NotEqual(key.Fingerprint, Key(reduced).Fingerprint);
        Assert.Equal(RevisionReportValidationFailure.ResourceLimit,
            Assert.Throws<RevisionReportValidationException>(() => registration.ValidateNativeSource(key)).Failure);
        Assert.Equal(RevisionReportValidationFailure.ResourceLimit,
            Assert.Throws<RevisionReportValidationException>(() => registration.ValidateDerivedReference(key, Derived(key, 6), oldPolicy)).Failure);
        // Historical derived-only reads load no native bytes and keep the old native key.
        registration.ValidateDerivedReference(key, Derived(key, 5), new(nativeByteLimit: 1));
        Assert.Equal(3, key.Source.NativeObject.ByteCount);
        Assert.Equal(oldPolicy, key.Policy);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void DerivedPreflightUsesStoredActiveAndAdapterBoundsAndExactScopeClass()
    {
        var registration = new RevisionReportStorageRegistration(4, 8, 6, () => throw new InvalidOperationException("No I/O."));
        var key = Key(new(storedObjectByteLimit: 4));
        Assert.Throws<RevisionReportValidationException>(() => registration.ValidateDerivedReference(key, Derived(key, 5), new()));
        Assert.Throws<RevisionReportValidationException>(() => registration.ValidateDerivedReference(key, Derived(key, 4), new(storedObjectByteLimit: 3)));
        registration.ValidateDerivedReference(key, Derived(key, 3), new(storedObjectByteLimit: 3));
        var other = new RevisionScope(key.Scope.OrganizationId, key.Scope.ProjectId, Guid.NewGuid(), key.Scope.RevisionId);
        var foreign = new PrivateObjectReference(PrivateObjectId.Restore(other, Guid.NewGuid(), PrivateObjectClass.DerivedArtifact), key.NativeDigest, 1);
        Assert.Equal(RevisionReportValidationFailure.Integrity,
            Assert.Throws<RevisionReportValidationException>(() => registration.ValidateDerivedReference(key, foreign, new())).Failure);
        Assert.Equal(RevisionReportValidationFailure.Integrity,
            Assert.Throws<RevisionReportValidationException>(() => registration.ValidateDerivedReference(key, key.Source.NativeObject, new())).Failure);
        Assert.Equal(RevisionReportValidationFailure.Integrity,
            Assert.Throws<RevisionReportValidationException>(() => registration.ValidateDerivedReference(key, Derived(key, 0), new())).Failure);
    }

    [Fact]
    public void StorageRegistrationHasClosedPositiveLimitsAndALazySharedInstance()
    {
        var calls = 0;
        var store = new UnusedStore();
        var registration = new RevisionReportStorageRegistration(4, 8, 6, () => { calls++; return store; });
        Assert.Equal(0, calls); Assert.Same(store, registration.GetStore()); Assert.Same(store, registration.GetStore()); Assert.Equal(1, calls);
        foreach (var native in new[] { 0, -1, 4194305 })
            Assert.Throws<ArgumentOutOfRangeException>(() => new RevisionReportStorageRegistration(native, 8, 6, () => store));
        foreach (var derived in new[] { 0, -1, 16777217 })
            Assert.Throws<ArgumentOutOfRangeException>(() => new RevisionReportStorageRegistration(4, derived, 1, () => store));
        foreach (var read in new[] { 0, -1, 9 })
            Assert.Throws<ArgumentOutOfRangeException>(() => new RevisionReportStorageRegistration(4, 8, read, () => store));
        Assert.Throws<ArgumentNullException>(() => new RevisionReportStorageRegistration(4, 8, 6, null!));
    }

    [Fact]
    public void NewModelsHaveGetOnlyStateAndBoundedCategoricalErrors()
    {
        Type[] immutable = [typeof(RevisionReportFormat), typeof(RevisionReportRegistration), typeof(RevisionReportStorageRegistration),
            typeof(RevisionReportStoredFormatRegistration), typeof(RevisionReportReadResult), typeof(RevisionReportEnsureResult),
            typeof(RevisionReportBackendResult), typeof(VerifiedProjectionReport), typeof(VerifiedValidationReport)];
        Assert.All(immutable, type => Assert.All(type.GetProperties(), property => Assert.Null(property.SetMethod)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RevisionReportValidationException((RevisionReportValidationFailure)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RevisionReportServiceException((RevisionReportFailureCategory)999));
        var exception = new RevisionReportValidationException(RevisionReportValidationFailure.Integrity);
        Assert.Equal("The revision report is inconsistent.", exception.Message);
        Assert.Null(exception.InnerException);
    }

    private static RevisionReportFormat Format(string backend = "provider", RevisionReportOperation operation = RevisionReportOperation.Projection,
        string schema = "schema.v1", string algorithm = "algorithm.v1", string codec = "codec.v1") => new(backend, operation, schema, algorithm, codec);

    private static ProjectionCacheKey Key(RevisionReportPolicy policy)
    {
        var scope = new RevisionScope(Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Guid.Parse("33333333-3333-3333-3333-333333333333"), Guid.Parse("44444444-4444-4444-4444-444444444444"));
        var digest = Sha256Digest.FromBytes([1, 2, 3]);
        var reference = new PrivateObjectReference(PrivateObjectId.Restore(scope,
            Guid.Parse("66666666-6666-6666-6666-666666666666"), PrivateObjectClass.NativeArtifact), digest, 3);
        return new(new(scope, reference, "entry.native.json", "weft.dependencies.empty.v1", "provider"),
            new("provider", "engine", "build", "adapter"), "schema.v1", "algorithm.v1", "codec.v1", policy);
    }

    private static PrivateObjectReference Derived(RevisionReportKey key, long bytes) =>
        new(PrivateObjectId.Restore(key.Scope, Guid.NewGuid(), PrivateObjectClass.DerivedArtifact), key.NativeDigest, bytes);

    private sealed class UnusedBackend : IRevisionReportBackend, IExecutionAdmission
    {
        public bool CanExecute { get; set; } = true;
        public Task<RevisionReportBackendResult> DeriveAsync(RevisionReportKey key, NativeArtifact artifact, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("This constructor/registration test executes no backend.");
    }

    private sealed class UnusedValidator : IRevisionReportFormatValidator
    {
        public void Validate(RevisionReportKey key, ProjectionReportV1 report, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("This registration test validates no report.");
        public void Validate(RevisionReportKey key, ValidationReportV1 report, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("This registration test validates no report.");
    }

    private sealed class UnusedStore : IPrivateObjectStore
    {
        public Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(RevisionScope expectedScope, PrivateObjectReference reference, Stream source, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("This pure preflight test performs no I/O.");
        public Task<byte[]> ReadVerifiedAsync(RevisionScope expectedScope, PrivateObjectReference reference, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("This pure preflight test performs no I/O.");
    }
}
