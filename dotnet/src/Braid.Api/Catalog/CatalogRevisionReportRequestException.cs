using Braid.Contracts.Catalog;

namespace Braid.Api.Catalog;

internal sealed class CatalogRevisionReportRequestException(CatalogErrorCodeV1 code, int statusCode) : Exception
{
    internal CatalogErrorCodeV1 Code { get; } = code;
    internal int StatusCode { get; } = statusCode;
}

/// <summary>Report-only deferred dependency failure, without private configuration details.</summary>
internal sealed class CatalogRevisionReportUnavailableException : Exception { }
