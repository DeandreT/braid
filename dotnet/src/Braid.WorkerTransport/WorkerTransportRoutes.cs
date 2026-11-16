namespace Braid.WorkerTransport;

/// <summary>The route sets of the worker transport; a server maps those its configuration serves.</summary>
[Flags]
public enum WorkerTransportRoutes
{
    None = 0,

    /// <summary><c>braid.ferrule-service.v1</c>: ready, capabilities, operations and their cancellation.</summary>
    Service = 1,

    /// <summary>Liveness and readiness probes.</summary>
    Health = 2,

    /// <summary><c>braid.inspection-transport.v1</c>'s own ready, operation and cancellation routes, over pinned TLS only.</summary>
    Inspection = 4
}
