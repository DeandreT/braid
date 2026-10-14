using Weft.Application;
using Weft.Domain;
using Weft.MappingBackends.Workers;

namespace Weft.MappingBackends;

public static class FerruleRevisionReportBackendFactory
{
    public const string ValidationAlgorithm = "weft.ferrule-validation.v1";
    public const string CodecVersion = "weft.revision-report-codec.v1";

    public static RevisionReportBackendProfile CreateProfile(string immutableImageId)
    {
        RequireImage(immutableImageId);
        return new("ferrule", FerruleInspectionBackend.EngineRevision, immutableImageId, FerruleInspectionBackend.AdapterBuild);
    }

    public static RevisionReportProfileRegistry CreateRegistry(RevisionReportBackendProfile trustedProfile,
        RevisionReportPolicy policy, Func<IInspectionWorkerRunner> sharedRunnerFactory)
    {
        RequireProfile(trustedProfile);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(sharedRunnerFactory);
        var runner = new Lazy<IInspectionWorkerRunner>(() => sharedRunnerFactory() ??
            throw new RevisionReportServiceException(RevisionReportFailureCategory.ProfileUnavailable));
        var projection = Format(RevisionReportOperation.Projection);
        var validation = Format(RevisionReportOperation.Validation);
        var validator = new FerruleRevisionReportFormatValidator();
        return new([
            new(trustedProfile, projection, policy, () => new DeferredBackend(runner, trustedProfile)),
            new(trustedProfile, validation, policy, () => new DeferredBackend(runner, trustedProfile))
        ], [new(projection, validator), new(validation, validator)]);
    }

    internal static RevisionReportFormat Format(RevisionReportOperation operation) => operation switch
    {
        RevisionReportOperation.Projection => new("ferrule", operation, "weft.mapping-projection.v1", FerruleInspectionBackend.ProjectionVersion, CodecVersion),
        RevisionReportOperation.Validation => new("ferrule", operation, "weft.mapping-validation.v1", ValidationAlgorithm, CodecVersion),
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

    private sealed class DeferredBackend : IRevisionReportBackend
    {
        private readonly RevisionReportBackendProfile _profile;
        private readonly Lazy<FerruleRevisionReportBackend> _backend;
        internal DeferredBackend(Lazy<IInspectionWorkerRunner> runner, RevisionReportBackendProfile profile)
        {
            _profile = profile;
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
