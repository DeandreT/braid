namespace Weft.BrowserQualification;

// Both assemblies compile this wire shape; it is not a shipped API.
internal sealed partial record QualificationRequest
{
    public required TransportAdmission? Admission { get; init; }
    public required string? CallbackCase { get; init; }
}
internal sealed partial record QualificationReply
{
    public int ProductDeliveries { get; init; }
    public int AdmissionRefusals { get; init; }
    public bool ProofStripped { get; init; }
}
internal sealed partial record QualificationHostInput
{
    public required int Generation { get; init; }
}
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
internal sealed record TransportAdmission
{
    public required string Proof { get; init; }
    public required int Generation { get; init; }
    public required string RawTarget { get; init; }
    public required string Method { get; init; }
    public required string Destination { get; init; }
    public required string Mode { get; init; }
    public required string Site { get; init; }
    public required string? Origin { get; init; }
    // Only named negative-case auth header changes; values are not authentication authority.
    public required string? Negative { get; init; }
    internal void Validate()
    {
        QualificationWire.Require(Proof.Length == 64 && Proof.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
            && Generation > 0 && RawTarget.Length is > 0 and <= 24576 && RawTarget[0] == '/' && !RawTarget.StartsWith("//", StringComparison.Ordinal)
            && !RawTarget.Any(c => c < 33 || c > 126 || c == '#' || c == '\\') && Method is "GET" or "POST"
            && Destination is "document" or "script" or "style" or "image" or "font" or "empty"
            && Mode is "navigate" or "cors" or "no-cors" or "same-origin"
            && Site is "none" or "same-origin" or "cross-site"
            && Negative is null or "missingAction" or "missingEpoch" or "crossPurpose" or "staleEpoch");
    }
}
