using Braid.Contracts.Projections;
using Braid.Contracts.Validation;
using Braid.Domain;

namespace Braid.Application;

public interface IRevisionReportFormatValidator
{
    void Validate(RevisionReportKey key, ProjectionReportV1 report, CancellationToken cancellationToken = default);
    void Validate(RevisionReportKey key, ValidationReportV1 report, CancellationToken cancellationToken = default);
}

public interface IRevisionReportProfileRegistry
{
    RevisionReportRegistration? FindCurrent(string sourceBackendId, RevisionReportOperation operation);
    IRevisionReportFormatValidator? FindStored(RevisionReportFormat format);
}

/// <summary>Exact stored format identity; supported historical formats need no live backend.</summary>
public sealed record RevisionReportFormat
{
    public RevisionReportFormat(string backendId, RevisionReportOperation operation, string schemaVersion,
        string algorithmVersion, string codecVersion)
    {
        BackendId = RevisionReportWorkflowValue.Text(backendId, nameof(backendId));
        ReportMetadataValue.Known(operation, nameof(operation));
        Operation = operation;
        SchemaVersion = RevisionReportWorkflowValue.Text(schemaVersion, nameof(schemaVersion));
        AlgorithmVersion = RevisionReportWorkflowValue.Text(algorithmVersion, nameof(algorithmVersion));
        CodecVersion = RevisionReportWorkflowValue.Text(codecVersion, nameof(codecVersion));
    }

    public string BackendId { get; }
    public RevisionReportOperation Operation { get; }
    public string SchemaVersion { get; }
    public string AlgorithmVersion { get; }
    public string CodecVersion { get; }

    public static RevisionReportFormat FromKey(RevisionReportKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return new(key.BackendId, key.Operation, key.SchemaVersion, key.AlgorithmVersion, key.CodecVersion);
    }
}

/// <summary>Trusted immutable selection and policy; constructing it resolves no backend.</summary>
public sealed class RevisionReportRegistration : IExecutionAdmission
{
    private readonly Lazy<IRevisionReportBackend> _backend;
    private readonly IExecutionAdmission? _admission;

    public RevisionReportRegistration(RevisionReportBackendProfile profile, RevisionReportFormat format,
        RevisionReportPolicy policy, Func<IRevisionReportBackend> backendFactory, IExecutionAdmission? admission = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(backendFactory);
        if (profile.BackendId != format.BackendId)
            throw new ArgumentException("The registered profile and format backend must agree.", nameof(format));
        Profile = profile;
        Format = format;
        Policy = policy;
        _admission = admission;
        _backend = new(() => backendFactory() ?? throw new RevisionReportServiceException(RevisionReportFailureCategory.ProfileUnavailable));
    }

    public RevisionReportBackendProfile Profile { get; }
    public RevisionReportFormat Format { get; }
    public RevisionReportPolicy Policy { get; }
    // Asking about known local admission must not resolve a backend before authorized native verification.
    public bool CanExecute => _admission is not { CanExecute: false } &&
        (!_backend.IsValueCreated || _backend.Value is not IExecutionAdmission { CanExecute: false });
    public IRevisionReportBackend GetBackend() => _backend.Value;
}

public sealed record RevisionReportStoredFormatRegistration
{
    public RevisionReportStoredFormatRegistration(RevisionReportFormat format, IRevisionReportFormatValidator validator)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(validator);
        Format = format;
        Validator = validator;
    }

    public RevisionReportFormat Format { get; }
    public IRevisionReportFormatValidator Validator { get; }
}

/// <summary>Owned bounded registrations. Duplicate current selections or stored formats are refused.</summary>
public sealed class RevisionReportProfileRegistry : IRevisionReportProfileRegistry
{
    public const int MaximumRegistrations = 128;
    private readonly Dictionary<(string BackendId, RevisionReportOperation Operation), RevisionReportRegistration> _current = [];
    private readonly Dictionary<RevisionReportFormat, IRevisionReportFormatValidator> _stored = [];

    public RevisionReportProfileRegistry(IEnumerable<RevisionReportRegistration> current,
        IEnumerable<RevisionReportStoredFormatRegistration> stored)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(stored);
        foreach (var registration in current)
        {
            ArgumentNullException.ThrowIfNull(registration);
            if (_current.Count >= MaximumRegistrations)
                throw new ArgumentException("There are too many report registrations.", nameof(current));
            if (!_current.TryAdd((registration.Format.BackendId, registration.Format.Operation), registration))
                throw new ArgumentException("A current report selection must be unambiguous.", nameof(current));
        }
        foreach (var registration in stored)
        {
            ArgumentNullException.ThrowIfNull(registration);
            if (_stored.Count >= MaximumRegistrations)
                throw new ArgumentException("There are too many stored report formats.", nameof(stored));
            if (!_stored.TryAdd(registration.Format, registration.Validator))
                throw new ArgumentException("A stored report format must be unambiguous.", nameof(stored));
        }
    }

    public RevisionReportRegistration? FindCurrent(string sourceBackendId, RevisionReportOperation operation)
    {
        RevisionReportWorkflowValue.Text(sourceBackendId, nameof(sourceBackendId));
        ReportMetadataValue.Known(operation, nameof(operation));
        return _current.GetValueOrDefault((sourceBackendId, operation));
    }

    public IRevisionReportFormatValidator? FindStored(RevisionReportFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        return _stored.GetValueOrDefault(format);
    }
}
