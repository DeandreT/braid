using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Braid.Identity;

// Explicit owner bootstrap of one existing empty key-ring directory. The server only opens
// existing rings; this command never creates, repairs or adopts a directory or key file.
if (args.Length != 7 || args[0] != "bootstrap-empty")
{
    Console.Error.WriteLine("Usage: Braid.SessionKeyTool bootstrap-empty ORGANIZATION_ID APPLICATION_NAME KEY_DIRECTORY CERTIFICATE_FILE PRIVATE_KEY_FILE LIFETIME_DAYS");
    return 2;
}

if (!Guid.TryParseExact(args[1], "D", out var organization) || organization == Guid.Empty ||
    args[1] != organization.ToString("D") || !int.TryParse(args[6], NumberStyles.None, CultureInfo.InvariantCulture, out var days) ||
    days is < 7 or > 365 || args[6] != days.ToString(CultureInfo.InvariantCulture) ||
    !Path.IsPathFullyQualified(args[4]) || !Path.IsPathFullyQualified(args[5]))
{
    Console.Error.WriteLine("InvalidConfiguration");
    return 2;
}

X509Certificate2? certificate = null;
try
{
    var options = new ServerSessionKeyProfileOptions(organization, args[2], args[3]);
    certificate = X509Certificate2.CreateFromPemFile(args[4], args[5]);
    // The host admits the same current RSA certificate before opening the ring.
    var now = DateTime.UtcNow;
    using (var key = certificate.GetRSAPrivateKey())
    {
        if (!certificate.HasPrivateKey || key is null || key.KeySize < 2048 ||
            now < certificate.NotBefore.ToUniversalTime() || now >= certificate.NotAfter.ToUniversalTime())
        {
            Console.Error.WriteLine("CertificateRefused");
            return 1;
        }
    }
    var activation = DateTimeOffset.UtcNow;
    using (DurableSessionKeyProfile.BootstrapEmpty(options, certificate, [], activation, activation.AddDays(days))) { }
    Console.WriteLine("Bootstrapped");
    return 0;
}
catch (ServerSessionIdentityException)
{
    // Closed categories only: no path, certificate or key-ring detail is printed.
    Console.Error.WriteLine("ConfigurationFailure");
    return 1;
}
catch (Exception error) when (error is CryptographicException or IOException or UnauthorizedAccessException or ArgumentException)
{
    Console.Error.WriteLine("CertificateRefused");
    return 1;
}
finally
{
    certificate?.Dispose();
}
