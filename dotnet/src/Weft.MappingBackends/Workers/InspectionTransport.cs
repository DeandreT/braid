using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Weft.MappingBackends.Workers;

/// <summary>
/// <c>weft.inspection-transport.v1</c>: one exact inspection frame per operation from a hosted API to a separate
/// inspection worker, over TLS where each side admits only the other's pinned certificate. The worker executes
/// the frame with its own Docker inspection profile; this transport adds no mapping semantics.
/// </summary>
public static class InspectionTransport
{
    /// <summary>
    /// Confirms mutual authentication and the worker image on the operation's connection before any frame byte is
    /// sent. TLS 1.3 completes a client's handshake before the worker has judged its certificate.
    /// </summary>
    public const string ReadyPath = "/v1/inspection/ready";
    public const string OperationsPath = "/v1/inspection/operations/";
    public const string CancelSuffix = "/cancel";
    public const string ImageHeader = "Weft-Worker-Image";
    public const string ResultHeader = "Weft-Worker-Result";
    public const string MediaType = "application/octet-stream";

    /// <summary>Queued and running operations one worker accepts; more are refused before execution.</summary>
    public const int MaximumOperations = 8;

    /// <summary>An operation not admitted to the worker's single runner within this wait is refused unexecuted.</summary>
    public static readonly TimeSpan AdmissionWait = TimeSpan.FromSeconds(20);

    public static int MaximumFrameBytes => DockerWorkerProfile.Inspection.MaximumRequestBytes;
    public static int MaximumOutputBytes => DockerWorkerProfile.Inspection.MaximumOutputBytes;

    public static string Encode(WorkerProcessFailure failure) => failure switch
    {
        WorkerProcessFailure.None => "none",
        WorkerProcessFailure.Canceled => "canceled",
        WorkerProcessFailure.TimedOut => "timedOut",
        WorkerProcessFailure.StartFailed => "startFailed",
        WorkerProcessFailure.WorkerFailed => "workerFailed",
        WorkerProcessFailure.OutputLimitExceeded => "outputLimitExceeded",
        WorkerProcessFailure.CleanupFailed => "cleanupFailed",
        WorkerProcessFailure.SandboxUnavailable => "sandboxUnavailable",
        _ => throw new ArgumentOutOfRangeException(nameof(failure))
    };

    public static bool TryDecode(string? value, out WorkerProcessFailure failure)
    {
        foreach (var candidate in Enum.GetValues<WorkerProcessFailure>())
        {
            if (string.Equals(Encode(candidate), value, StringComparison.Ordinal))
            {
                failure = candidate;
                return true;
            }
        }

        failure = WorkerProcessFailure.WorkerFailed;
        return false;
    }

    /// <summary>A fresh client-chosen operation identity: 32 lowercase hexadecimal characters.</summary>
    public static bool IsOperationId(string? value) => value is { Length: 32 } && LowercaseHexadecimal(value);

    /// <summary>The pinned SHA-256 of a peer certificate's DER encoding: 64 lowercase hexadecimal characters.</summary>
    public static bool IsCertificateDigest(string? value) => value is { Length: 64 } && LowercaseHexadecimal(value);

    public static string CertificateDigest(X509Certificate certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return Convert.ToHexStringLower(SHA256.HashData(certificate.GetRawCertData()));
    }

    /// <summary>Compares the presented certificate with the configured pin in fixed time; trust stores and names are not used.</summary>
    public static bool HasDigest(X509Certificate? certificate, string digest)
    {
        if (certificate is null || !IsCertificateDigest(digest))
            return false;
        Span<byte> actual = stackalloc byte[32];
        SHA256.HashData(certificate.GetRawCertData(), actual);
        return CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(digest));
    }

    /// <summary>The exact trusted image identity both sides must agree on.</summary>
    public static string RequireImage(string imageId)
    {
        ArgumentNullException.ThrowIfNull(imageId);
        if (imageId.Length != 71 || !imageId.StartsWith("sha256:", StringComparison.Ordinal) || !LowercaseHexadecimal(imageId.AsSpan(7)))
            throw new ArgumentException("The worker image must be an immutable lowercase sha256 image ID.", nameof(imageId));
        return imageId;
    }

    private static bool LowercaseHexadecimal(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
                return false;
        }

        return true;
    }
}
