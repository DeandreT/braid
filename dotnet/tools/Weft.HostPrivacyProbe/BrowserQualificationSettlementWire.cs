namespace Weft.BrowserQualification;

internal sealed partial record QualificationReply
{
    public bool ClosedObservation { get; init; }
    public int AdmissionRefusalsTotal { get; init; }
}

[System.Runtime.Versioning.SupportedOSPlatform("linux")]
internal static class QualificationBackstopPolicy
{
    // forbidden counts the proxy refusals that targeted the proxy vector's own destination.
    internal static void Validate(string? name, int host, int proxy, int forbidden = 0)
    {
        QualificationWire.Require((name is null || QualificationNetwork.Cases.Contains(name)) && host >= 0 && proxy >= 0 && forbidden >= 0);
        int expected = name is "preflight" or "missing" or "duplicate" or "mismatch" or "stale" or "detach" or "seal" or "teardown" or "audio" or "paint" ? 1 : 0;
        // A response fault may be resent by Chromium once with its consumed proof.
        QualificationWire.Require(host == expected || (name is "audio" or "paint") && host == 0
            || QualificationNetwork.ResponseCases.Contains(name) && host == 1);
        // Chromium also preconnects speculatively to that navigation target; nothing else may be refused.
        QualificationWire.Require(proxy == forbidden && (name == "proxy" ? forbidden is >= 1 and <= 4 : forbidden == 0));
    }
}
