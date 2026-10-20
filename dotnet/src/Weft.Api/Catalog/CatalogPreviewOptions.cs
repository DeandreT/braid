using Microsoft.Extensions.DependencyInjection;
using Weft.Application;

namespace Weft.Api.Catalog;

/// <summary>Default-off preview run routes. The body bound covers base64 inputs and JSON framing.</summary>
public sealed record CatalogPreviewOptions
{
    public const int MaximumBodyBytes = 12 * 1024 * 1024;

    public CatalogPreviewOptions(bool enabled = false, int bodyByteLimit = MaximumBodyBytes)
    {
        if (bodyByteLimit is < 1024 or > MaximumBodyBytes)
            throw new ArgumentOutOfRangeException(nameof(bodyByteLimit), "The preview body limit is outside its admitted range.");
        Enabled = enabled;
        BodyByteLimit = bodyByteLimit;
    }

    public bool Enabled { get; }
    public int BodyByteLimit { get; }
}

public static partial class CatalogEndpoints
{
    /// <summary>
    /// Enables preview routes over an already registered catalog. The host supplies the
    /// <see cref="PreviewRunService"/> composed with its durable submission store.
    /// </summary>
    public static IServiceCollection AddWeftPreviews(this IServiceCollection services, CatalogPreviewOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        if (!services.Any(service => service.ServiceType == typeof(CatalogApiOptions)))
            throw new InvalidOperationException("Preview routes require a registered catalog.");
        if (services.Any(service => service.ServiceType == typeof(CatalogPreviewOptions)))
            throw new InvalidOperationException("Preview routes are already registered.");
        services.AddSingleton(options);
        return services;
    }
}
