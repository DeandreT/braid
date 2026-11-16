using Braid.Domain;

namespace Braid.Application;

public interface IIngestionAdmissionBackend
{
    Task<IngestionAttemptCompletion> ValidateAsync(NativeArtifact artifact,
        CancellationToken cancellationToken = default);
}

public interface IIngestionBackendRegistry
{
    IngestionBackendRegistration? GetCurrent(string backendId);
    IngestionBackendRegistration? GetReserved(IngestionBackendProfile exactProfile);
}

/// <summary>Server-owned immutable profile and native ceiling paired with its actual configured adapter.</summary>
public sealed record IngestionBackendRegistration
{
    public IngestionBackendRegistration(IngestionBackendProfile profile, long nativeByteLimit,
        IIngestionAdmissionBackend backend)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(backend);
        RevisionIngestionGuard.RequireNativeLimit(nativeByteLimit);
        Profile = profile;
        NativeByteLimit = nativeByteLimit;
        Backend = backend;
    }

    public IngestionBackendProfile Profile { get; }
    public long NativeByteLimit { get; }
    public IIngestionAdmissionBackend Backend { get; }
}

/// <summary>Supply the native limit from the same trusted options that created the private store.</summary>
public sealed record IngestionPrivateStoreRegistration
{
    public IngestionPrivateStoreRegistration(IPrivateObjectStore store, long nativeByteLimit)
    {
        ArgumentNullException.ThrowIfNull(store);
        RevisionIngestionGuard.RequireNativeLimit(nativeByteLimit);
        Store = store;
        NativeByteLimit = nativeByteLimit;
    }

    public IPrivateObjectStore Store { get; }
    public long NativeByteLimit { get; }
}

public sealed record RevisionIngestionOptions
{
    public RevisionIngestionOptions(long nativeByteLimit = IngestionAdmissionPolicy.MaximumNativeByteCount)
    {
        RevisionIngestionGuard.RequireNativeLimit(nativeByteLimit);
        NativeByteLimit = nativeByteLimit;
    }

    public long NativeByteLimit { get; }
}

/// <summary>Owned exact-profile registrations; retained adapters never automatically replace a current registration.</summary>
public sealed class IngestionBackendRegistry : IIngestionBackendRegistry
{
    private readonly Dictionary<string, IngestionBackendRegistration> _current = new(StringComparer.Ordinal);
    private readonly Dictionary<IngestionBackendProfile, IngestionBackendRegistration> _reserved = [];

    public IngestionBackendRegistry(IEnumerable<IngestionBackendRegistration> currentRegistrations,
        IEnumerable<IngestionBackendRegistration>? retainedRegistrations = null)
    {
        ArgumentNullException.ThrowIfNull(currentRegistrations);
        foreach (var registration in currentRegistrations)
        {
            ArgumentNullException.ThrowIfNull(registration);
            if (!_current.TryAdd(registration.Profile.BackendId, registration))
                throw new ArgumentException("Current backend IDs must be unambiguous.", nameof(currentRegistrations));
            AddReserved(registration, nameof(currentRegistrations));
        }
        if (retainedRegistrations is not null)
        {
            foreach (var registration in retainedRegistrations)
            {
                ArgumentNullException.ThrowIfNull(registration);
                AddReserved(registration, nameof(retainedRegistrations));
            }
        }
    }

    public IngestionBackendRegistration? GetCurrent(string backendId)
    {
        RevisionIngestionGuard.RequireBackendId(backendId);
        return _current.GetValueOrDefault(backendId);
    }

    public IngestionBackendRegistration? GetReserved(IngestionBackendProfile exactProfile)
    {
        ArgumentNullException.ThrowIfNull(exactProfile);
        return _reserved.GetValueOrDefault(exactProfile);
    }

    private void AddReserved(IngestionBackendRegistration registration, string parameterName)
    {
        if (!_reserved.TryAdd(registration.Profile, registration))
            throw new ArgumentException("Exact backend profiles must be unambiguous.", parameterName);
    }
}
