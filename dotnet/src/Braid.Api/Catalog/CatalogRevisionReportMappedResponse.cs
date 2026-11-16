using Braid.Application;
using Braid.Contracts.Catalog;

namespace Braid.Api.Catalog;

internal readonly record struct CatalogRevisionReportResponseLimits(int ByteLimit, int ValueLimit, int ContainerDepthLimit);

internal sealed class CatalogRevisionReportMappedResponse
{
    private CatalogRevisionReportMappedResponse(int statusCode, string? location, int? retryAfterSeconds,
        CatalogProjectionReportV1? projection, CatalogValidationReportV1? validation, CatalogErrorV1? error,
        AuthorizedRevisionReportSource? source, CatalogRevisionReportResponseLimits? limits)
    {
        if ((projection is null ? 0 : 1) + (validation is null ? 0 : 1) + (error is null ? 0 : 1) != 1 ||
            error is null && (source is null || limits is null))
            throw new InvalidOperationException("The report response branch is inconsistent.");
        StatusCode = statusCode;
        Location = location;
        RetryAfterSeconds = retryAfterSeconds;
        Projection = projection;
        Validation = validation;
        Error = error;
        Source = source;
        Limits = limits;
    }

    internal int StatusCode { get; }
    internal string? Location { get; }
    internal int? RetryAfterSeconds { get; }
    internal CatalogProjectionReportV1? Projection { get; }
    internal CatalogValidationReportV1? Validation { get; }
    internal CatalogErrorV1? Error { get; }
    internal AuthorizedRevisionReportSource? Source { get; }
    internal CatalogRevisionReportResponseLimits? Limits { get; }

    internal static CatalogRevisionReportMappedResponse ForProjection(int statusCode, string? location, int? retryAfter,
        CatalogProjectionReportV1 projection, AuthorizedRevisionReportSource source, CatalogRevisionReportResponseLimits limits) =>
        new(statusCode, location, retryAfter, projection, null, null, source, limits);

    internal static CatalogRevisionReportMappedResponse ForValidation(int statusCode, string? location, int? retryAfter,
        CatalogValidationReportV1 validation, AuthorizedRevisionReportSource source, CatalogRevisionReportResponseLimits limits) =>
        new(statusCode, location, retryAfter, null, validation, null, source, limits);

    internal static CatalogRevisionReportMappedResponse ForError(int statusCode, CatalogErrorCodeV1 code,
        AuthorizedRevisionReportSource? source = null) => new(statusCode, null, null, null, null, new(code), source, null);

    internal CatalogRevisionReportMappedResponse WithoutPayloadForResourceLimit(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Projection is { } projection)
            return ForProjection(StatusCode, Location, RetryAfterSeconds,
                new(projection.SchemaVersion, projection.Revision, projection.Operation, projection.State,
                    CatalogRevisionReportReadOutcomeV1.ResourceLimit, projection.Status, projection.Result,
                    projection.CurrentAttempt, projection.NextAttemptAtUtc, projection.Failure, null), Source!, Limits!.Value);
        if (Validation is { } validation)
            return ForValidation(StatusCode, Location, RetryAfterSeconds,
                new(validation.SchemaVersion, validation.Revision, validation.Operation, validation.State,
                    CatalogRevisionReportReadOutcomeV1.ResourceLimit, validation.Status, validation.Result,
                    validation.CurrentAttempt, validation.NextAttemptAtUtc, validation.Failure, null,
                    validation.ProjectionDerivationId), Source!, Limits!.Value);
        throw new InvalidOperationException("Only a report response can lose its payload for admission.");
    }
}
