namespace Braid.BrowserQualification;

internal static class QualificationNetwork
{
    internal static readonly string[] ResponseCases = ["location-host", "location-ip", "location-port", "location-http", "location-file", "location-document", "location-duplicate", "refresh"];
    internal static readonly string[] Cases = [.. ResponseCases, "popup", "frame", "worker", "audio", "paint", "disabled", "preflight", "missing", "duplicate", "mismatch", "stale", "detach", "seal", "teardown", "proxy"];
}
internal sealed partial record QualificationHostInput { public string? NetworkCase { get; init; } }
internal sealed partial record QualificationReply
{
    public int NetworkRequests { get; init; }
    public int NetworkPreflights { get; init; }
    public int NetworkAborts { get; init; }
    public int NetworkActive { get; init; }
    public int ProofPending { get; init; }
    public int ProofUsed { get; init; }
    public bool NetworkArmed { get; init; }
    public bool AdmissionSealed { get; init; }
    public int ProxyRefused { get; init; }
    public int ProxyApproved { get; init; }
}
