using Microsoft.Extensions.DependencyInjection;
using Braid.Application;

namespace Braid.Api.Catalog;

/// <summary>Default-off preview run routes. The body bound covers base64 inputs and JSON framing.</summary>
public sealed record CatalogPreviewOptions
{
    public const int MaximumBodyBytes = 12 * 1024 * 1024;
    public const string CsvEnabledSetting = "BRAID_PREVIEWS_CSV_ENABLED";

    public CatalogPreviewOptions(bool enabled = false, int bodyByteLimit = MaximumBodyBytes, bool csvEnabled = false, bool xmlEnabled = false, IReadOnlyList<NativeDocumentFormat>? nativeFormats = null)
    {
        if (bodyByteLimit is < 1024 or > MaximumBodyBytes)
            throw new ArgumentOutOfRangeException(nameof(bodyByteLimit), "The preview body limit is outside its admitted range.");
        if (csvEnabled && !enabled) throw new ArgumentException("CSV previews require enabled preview routes.", nameof(csvEnabled));
        if (xmlEnabled && !enabled) throw new ArgumentException("XML previews require enabled preview routes.", nameof(xmlEnabled));
        NativeFormats = nativeFormats?.Where(NativeDocumentFormat.CatalogFormats.Contains).ToArray() ?? [];
        if (NativeFormats.Count != 0 && !enabled) throw new ArgumentException("Native previews require enabled preview routes.");
        Enabled = enabled;
        CsvEnabled = csvEnabled;
        XmlEnabled = xmlEnabled;
        BodyByteLimit = bodyByteLimit;
    }

    public bool Enabled { get; }
    public IReadOnlyList<NativeDocumentFormat> NativeFormats { get; }
    public bool CsvEnabled { get; }
    public bool XmlEnabled { get; }
    public int BodyByteLimit { get; }
}

public static partial class CatalogEndpoints
{
    /// <summary>
    /// Enables preview routes over an already registered Development or session-backed catalog. The
    /// host supplies the <see cref="PreviewRunService"/> composed with its durable submission store.
    /// </summary>
    public static IServiceCollection AddBraidPreviews(this IServiceCollection services, CatalogPreviewOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        if (!services.Any(service => service.ServiceType == typeof(CatalogRequestBoundary)))
            throw new InvalidOperationException("Preview routes require a registered catalog.");
        if (services.Any(service => service.ServiceType == typeof(CatalogPreviewOptions)))
            throw new InvalidOperationException("Preview routes are already registered.");
        if (options.CsvEnabled || options.XmlEnabled || options.NativeFormats.Count != 0) CatalogPreviewSourceAdmission.RequireNativeSources(services);
        services.AddSingleton(options);
        return services;
    }
}
