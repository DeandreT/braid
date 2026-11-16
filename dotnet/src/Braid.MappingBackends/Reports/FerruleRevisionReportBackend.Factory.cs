using Braid.Application;
using Braid.Domain;
using Braid.MappingBackends.Workers;

namespace Braid.MappingBackends;

public static class FerruleRevisionReportBackendFactory
{
    public const string ValidationAlgorithm = "braid.ferrule-validation.v1";
    public const string CodecVersion = "braid.revision-report-codec.v1";

    public static RevisionReportBackendProfile CreateProfile(string immutableImageId)
    {
        RequireImage(immutableImageId);
        return new("ferrule", FerruleInspectionBackend.EngineRevision, immutableImageId, FerruleInspectionBackend.AdapterBuild);
    }

    public static RevisionReportProfileRegistry CreateRegistry(RevisionReportBackendProfile trustedProfile,
        RevisionReportPolicy policy, Func<IInspectionWorkerRunner> sharedRunnerFactory, IExecutionAdmission? knownAdmission = null)
    {
        RequireProfile(trustedProfile);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(sharedRunnerFactory);
        var runner = new Lazy<IInspectionWorkerRunner>(() => sharedRunnerFactory() ??
            throw new RevisionReportServiceException(RevisionReportFailureCategory.ProfileUnavailable));
        var admission = new DeferredAdmission(runner, knownAdmission);
        var projection = Format(RevisionReportOperation.Projection);
        var validation = Format(RevisionReportOperation.Validation);
        var validator = new FerruleRevisionReportFormatValidator();
        return new([
            new(trustedProfile, projection, policy, () => new DeferredBackend(runner, trustedProfile, admission), admission),
            new(trustedProfile, validation, policy, () => new DeferredBackend(runner, trustedProfile, admission), admission)
        ], [new(projection, validator), new(validation, validator)]);
    }

    internal static RevisionReportFormat Format(RevisionReportOperation operation) => operation switch
    {
        RevisionReportOperation.Projection => new("ferrule", operation, "braid.mapping-projection.v1", FerruleInspectionBackend.ProjectionVersion, CodecVersion),
        RevisionReportOperation.Validation => new("ferrule", operation, "braid.mapping-validation.v1", ValidationAlgorithm, CodecVersion),
        _ => throw new RevisionReportValidationException(RevisionReportValidationFailure.Integrity)
    };

    internal static void RequireProfile(RevisionReportBackendProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        RequireImage(profile.BackendBuild);
        if (profile.BackendId != "ferrule" || profile.BackendRevision != FerruleInspectionBackend.EngineRevision ||
            profile.AdapterBuild != FerruleInspectionBackend.AdapterBuild)
            throw new ArgumentException("The current Ferrule profile must use the configured adapter identity.", nameof(profile));
    }

    private static void RequireImage(string image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Length != 71 || !image.StartsWith("sha256:", StringComparison.Ordinal))
            throw new ArgumentException("A trusted immutable image identity is required.", nameof(image));
        for (var index = 7; index < image.Length; index++)
            if (image[index] is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
                throw new ArgumentException("A trusted immutable image identity is required.", nameof(image));
    }

    // The registrations share this signal even before either outer backend is resolved.
    // A host can also expose its already-created runner without invoking the runner factory.
    private sealed class DeferredAdmission(Lazy<IInspectionWorkerRunner> runner, IExecutionAdmission? knownAdmission) : IExecutionAdmission
    {
        public bool CanExecute => knownAdmission is not { CanExecute: false } &&
            (!runner.IsValueCreated || runner.Value is not IExecutionAdmission { CanExecute: false });
    }

    private sealed class DeferredBackend : IRevisionReportBackend, IExecutionAdmission
    {
        private readonly RevisionReportBackendProfile _profile;
        private readonly Lazy<FerruleRevisionReportBackend> _backend;
        private readonly IExecutionAdmission _admission;
        public bool CanExecute => _admission.CanExecute;
        internal DeferredBackend(Lazy<IInspectionWorkerRunner> runner, RevisionReportBackendProfile profile, IExecutionAdmission admission)
        {
            _profile = profile;
            _admission = admission;
            _backend = new(() => new(runner.Value, profile));
        }
        public Task<RevisionReportBackendResult> DeriveAsync(RevisionReportKey key, NativeArtifact artifact,
            CancellationToken cancellationToken = default)
        {
            FerruleRevisionReportBackend.ValidateInput(key, artifact, _profile, cancellationToken);
            if (artifact.ByteCount > key.Policy.NativeByteLimit)
                return Task.FromResult(RevisionReportBackendResult.ForFailure(RevisionReportFailureCategory.ResourceLimit, cancellationToken: cancellationToken));
            return _backend.Value.DeriveAsync(key, artifact, cancellationToken);
        }
    }
}
