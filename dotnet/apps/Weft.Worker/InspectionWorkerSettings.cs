using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Weft.Jobs;
using Weft.MappingBackends.Workers;
using Weft.Storage.FileSystem;

namespace Weft.Worker;

/// <summary>
/// Default-off hosted inspection service. Enabled, it requires one literal private listener, the worker's own
/// current RSA certificate and key as protected files, and the pin of the single admitted client certificate.
/// </summary>
internal static class InspectionWorkerSettings
{
    internal static (InspectionTransportServerOptions Transport, DockerInspectionWorkerOptions Runner)? Read(
        Func<string, string?> environment, string workerImage, string dockerExecutable)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var enabled = environment("WEFT_INSPECTION_WORKER_ENABLED") switch
        {
            null or "false" => false,
            "true" => true,
            _ => throw Failure()
        };
        if (!enabled) return null;
        string Required(string name) => environment(name) is { Length: > 0 } value ? value : throw Failure();
        var listen = Required("WEFT_INSPECTION_LISTEN_ADDR");
        if (!IPEndPoint.TryParse(listen, out var endpoint) || endpoint.Port == 0 || listen != endpoint.ToString())
            throw Failure();
        var pin = Required("WEFT_INSPECTION_CLIENT_CERTIFICATE_SHA256");
        if (!InspectionTransport.IsCertificateDigest(pin)) throw Failure();
        var runner = new DockerInspectionWorkerOptions(dockerExecutable, InspectionTransport.RequireImage(workerImage),
            profile: DockerWorkerProfile.Inspection);
        var certificate = Certificate(ProtectedHostFiles.ReadText(Required("WEFT_INSPECTION_SERVER_CERTIFICATE_FILE"), 65_536),
            ProtectedHostFiles.ReadText(Required("WEFT_INSPECTION_SERVER_PRIVATE_KEY_FILE"), 65_536));
        return (new InspectionTransportServerOptions(endpoint, certificate, pin), runner);
    }

    private static X509Certificate2 Certificate(string certificatePem, string keyPem)
    {
        var certificate = X509Certificate2.CreateFromPem(certificatePem, keyPem);
        try
        {
            var now = DateTime.UtcNow;
            using var key = certificate.GetRSAPrivateKey();
            if (!certificate.HasPrivateKey || key is null || key.KeySize < 2048 ||
                now < certificate.NotBefore.ToUniversalTime() || now >= certificate.NotAfter.ToUniversalTime())
                throw Failure();
            // The key must actually belong to the certificate the client pins.
            var probe = RandomNumberGenerator.GetBytes(32);
            using var publicKey = certificate.GetRSAPublicKey();
            if (publicKey is null || !publicKey.VerifyData(probe, key.SignData(probe, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                throw Failure();
            return certificate;
        }
        catch
        {
            certificate.Dispose();
            throw;
        }
    }

    private static InvalidOperationException Failure() => new("The inspection worker configuration is invalid.");
}
