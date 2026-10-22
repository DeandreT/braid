namespace Weft.MappingBackends.Workers;

/// <summary>
/// <c>weft.ferrule-service.v1</c>: the <c>weft.inspection-transport.v1</c> operation model (ready probe, one exact
/// frame per fresh operation identity, cancellation, settled answers) for a stateless, horizontally scaled
/// Ferrule service. A route selects the trusted operation profile; the frame's own header selects the Ferrule
/// operation within it. Every request of one operation, including its cancellation, travels on one channel that
/// reaches one service instance: one HTTP/2 connection for pinned mutual TLS, or one sandbox session.
/// </summary>
public static class FerruleServiceProtocol
{
    public const string Version = "weft.ferrule-service.v1";
    public const string ReadyPath = "/v1/ready";
    public const string CapabilitiesPath = "/v1/capabilities";
    public const string OperationsPath = "/v1/operations/";
    public const string LivenessPath = "/v1/health/live";
    public const string ReadinessPath = "/v1/health/ready";
    public const string ProtocolHeader = "Weft-Service-Protocol";

    /// <summary>Read-only inspection, validation and projection frames (<c>weft.ferrule-host.v1</c>).</summary>
    public const string InspectionProfile = "inspection";

    /// <summary>Bounded JSON sample previews (<c>weft.ferrule-preview.v1</c>).</summary>
    public const string PreviewProfile = "preview";

    /// <summary>Reserved for compilation of deployable mapping libraries; refused until a service implements it.</summary>
    public const string CompileProfile = "compile";

    /// <summary>The fixed limits behind a profile name; the service and its clients derive bounds from the same table.</summary>
    public static DockerWorkerProfile? Profile(string? name) => name switch
    {
        InspectionProfile => DockerWorkerProfile.Inspection,
        PreviewProfile => DockerWorkerProfile.Preview,
        _ => null
    };

    public static string OperationPath(string profile, string operation) => OperationsPath + profile + "/" + operation;
    public static string CancelPath(string profile, string operation) => OperationPath(profile, operation) + InspectionTransport.CancelSuffix;
}
