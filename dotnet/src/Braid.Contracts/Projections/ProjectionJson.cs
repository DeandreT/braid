using System.Text.Json;
using System.Text.Json.Serialization;
using Braid.Contracts.Catalog;

namespace Braid.Contracts.Projections;

public static class ProjectionJson
{
    public static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Configure(options);
        return options;
    }

    public static void Configure(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
        options.NumberHandling = JsonNumberHandling.Strict;
        options.RespectRequiredConstructorParameters = true;
        options.RespectNullableAnnotations = true;
        // Options converters take precedence over attributes. Keep the new closed report
        // enums ahead of any generic enum converter already supplied by composition.
        options.Converters.Insert(0, new CatalogRevisionReportStateConverterV1());
        options.Converters.Insert(1, new CatalogRevisionReportReadOutcomeConverterV1());
        options.Converters.Insert(2, new CatalogRevisionReportFailureConverterV1());
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
    }
}
