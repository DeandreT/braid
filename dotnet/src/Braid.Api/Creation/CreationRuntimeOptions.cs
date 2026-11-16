using Microsoft.Extensions.DependencyInjection;
using Braid.Application;

namespace Braid.Api.Creation;

public sealed record CreationRuntimeOptions(bool Enabled = false);

/// <summary>Host qualification of durable creation fencing, Specification disclosure storage, compiler and preview profiles.
/// Implement only after the concrete ports have passed their contract admission. No default implementation.</summary>
public interface ICreationRuntimeAdmission
{
    Task<bool> VerifyAsync(CancellationToken ct);
}

public static class CreationRuntime
{
    public static bool Configured(IServiceProvider services) => services.GetService<CreationRuntimeOptions>()?.Enabled == true &&
        services.GetService<ICreationRuntimeAdmission>() is not null && services.GetService<ISpecBundleServiceFactory>() is not null &&
        services.GetService<ICreationDiscovery>() is not null &&
        services.GetService<IMappingCreationCompiler>() is not null && services.GetService<IMappingCreationPreview>() is not null &&
        services.GetService<IMappingCreationAuthority>() is not null && services.GetService<IMappingCreationProposalStore>() is not null &&
        services.GetService<IMappingCreationApprovalStore>() is not null && services.GetService<IRevisionIngestionService>() is not null &&
        services.GetService<IIngestionWorkflowReader>() is not null;

    public static async Task<bool> AvailableAsync(IServiceProvider services, CancellationToken ct) => Configured(services) &&
        await services.GetRequiredService<ICreationRuntimeAdmission>().VerifyAsync(ct).ConfigureAwait(false);

    public static MappingCreationApprovalService Approvals(IServiceProvider services) => new(
        services.GetRequiredService<IMappingCreationProposalStore>(), services.GetRequiredService<IMappingCreationApprovalStore>(),
        services.GetRequiredService<IRevisionIngestionService>(), services.GetRequiredService<IIngestionWorkflowReader>());
}
