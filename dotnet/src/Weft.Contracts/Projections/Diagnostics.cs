namespace Weft.Contracts.Projections;

public enum DiagnosticSeverity
{
    Information,
    Warning,
    Error
}

public sealed record ProjectionDiagnostic(
    string Code,
    DiagnosticSeverity Severity,
    string Message,
    NativeLocator? NativeLocator = null,
    ProjectionNodeReference? ProjectionLocator = null);
