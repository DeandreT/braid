using Microsoft.Extensions.DependencyInjection;
using Braid.Deployment;

namespace Braid.Api.Catalog;

public sealed record CatalogBuildOptions
{
    public CatalogBuildOptions(bool enabled = false, BuildBackendIdentity? backend = null, BuildRuntimeProfile? profile = null,
        BuildBackendIdentity? documentBackend = null, BuildRuntimeProfile? documentProfile = null, BuildRuntimeProfile? interchangeProfile = null,
        BuildBackendIdentity? csvBackend = null, BuildRuntimeProfile? csvProfile = null)
    {
        if (enabled && (backend is null || profile is null)) throw new ArgumentException("Build API requires an explicit trusted runtime selection.");
        Enabled = enabled; Backend = backend; Profile = profile;
        if ((documentBackend is null) != (documentProfile is null)) throw new ArgumentException("Document builds require an explicit backend and profile pair.");
        if (documentBackend is not null && (backend is null || documentProfile!.Id != BuildWorkerProtocol.DocumentProfile ||
            documentBackend.BackendId != backend.BackendId || documentBackend.EngineRevision != backend.EngineRevision || documentBackend.EngineBuild != backend.EngineBuild))
            throw new ArgumentException("Document builds must pin the same installed engine and a separate document profile.");
        DocumentBackend = documentBackend; DocumentProfile = documentProfile;
        if (interchangeProfile is not null && (documentBackend is null || interchangeProfile.Id != BuildWorkerProtocol.X12InterchangesProfile))
            throw new ArgumentException("X12 interchange builds require the explicit document backend and interchange profile.");
        InterchangeProfile = interchangeProfile;
        if ((csvBackend is null) != (csvProfile is null) || csvProfile is not null &&
            (csvProfile.Id != BuildWorkerProtocol.CsvProfile || csvProfile.Version != 1 || csvProfile.Runtime != "dotnet" ||
             csvProfile.RuntimeVersion != BuildWorkerProtocol.RuntimeVersion || backend is null || csvBackend!.BackendId != backend.BackendId ||
             csvBackend.EngineRevision != backend.EngineRevision || csvBackend.EngineBuild != backend.EngineBuild))
            throw new ArgumentException("CSV builds require the explicitly configured compiler and runtime profile pair.");
        CsvBackend = csvBackend; CsvProfile = csvProfile;
    }
    public bool Enabled { get; }
    public BuildBackendIdentity? Backend { get; }
    public BuildRuntimeProfile? Profile { get; }
    public BuildBackendIdentity? DocumentBackend { get; }
    public BuildRuntimeProfile? DocumentProfile { get; }
    public BuildRuntimeProfile? InterchangeProfile { get; }
    public BuildBackendIdentity? CsvBackend { get; }
    public BuildRuntimeProfile? CsvProfile { get; }
}

public static class CatalogBuildRegistration
{
    public static IServiceCollection AddBraidBuildApi(this IServiceCollection services, CatalogBuildOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(options ?? new CatalogBuildOptions());
        return services;
    }
}
