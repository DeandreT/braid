using Weft.Application;
using Weft.Contracts.Projections;
using Weft.Domain;

namespace Weft.Api;

/// <summary>Nonpersisted example navigation forwards only explicitly emitted native evidence.</summary>
internal sealed class NativeProjectionLinks(ProjectionDocumentV1 document, RevisionReportPolicy limits,
    CancellationToken cancellationToken = default)
{
    private readonly ProjectionOriginLinks _links = ProjectionOriginLinks.Create(document, limits, cancellationToken);

    public ProjectionNodeReference? Find(NativeLocator? native, CancellationToken cancellationToken = default)
        => _links.Find(native, cancellationToken);
}

internal sealed class NativeExampleLimitException : Exception { }
