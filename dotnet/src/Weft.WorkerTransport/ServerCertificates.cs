using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Weft.WorkerTransport;

public static class ServerCertificates
{
    /// <summary>
    /// Admits a pinned listener's current RSA (at least 2048-bit) certificate and its matching private key.
    /// Trust chains are never consulted; the peer pins this exact certificate.
    /// </summary>
    public static X509Certificate2 FromPem(string certificatePem, string keyPem)
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

    /// <summary>Splits one PEM bundle holding exactly one private key and its certificate.</summary>
    public static X509Certificate2 FromPemBundle(string bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var text = bundle.Replace("\r", "", StringComparison.Ordinal);
        var keys = 0;
        var certificates = 0;
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("-----BEGIN ", StringComparison.Ordinal) && line.EndsWith("PRIVATE KEY-----", StringComparison.Ordinal)) keys++;
            if (line == "-----BEGIN CERTIFICATE-----") certificates++;
        }

        if (keys != 1 || certificates != 1) throw Failure();
        return FromPem(text, text);
    }

    private static InvalidOperationException Failure() => new("The listener certificate is invalid.");
}
