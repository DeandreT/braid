using Weft.Contracts.Projections;

namespace Weft.Contracts.Operations;

public enum OperationStatusV1
{
    Succeeded,
    InvalidMapping,
    UnsupportedArtifact,
    ResourceLimit,
    ProtocolFailure,
    BackendUnavailable,
    BackendFailure,
    TimedOut,
    Canceled,
    IsolationFailure
}

public sealed record OperationProvenanceV1(
    ScopedRevisionId Revision,
    ArtifactDigest NativeArtifactDigest,
    BackendIdentity Backend,
    string AdapterVersion,
    bool Confirmed);

public sealed record OperationDiagnosticV1(
    string Code,
    DiagnosticSeverity Severity,
    string Message,
    bool MessageTruncated,
    NativeLocator? NativeLocator = null);
