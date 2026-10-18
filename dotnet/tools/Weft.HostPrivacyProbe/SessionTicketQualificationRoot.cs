using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using Weft.Identity;

namespace Weft.Tests;

/// <summary>Only the bounded key-root methods consumed by the source-linked116 fixture support; no oracle tests.</summary>
internal sealed class SessionTicketQualificationRoot : IDisposable
{
    internal string Root { get; }
    internal string Keys { get; }
    internal X509Certificate2 Certificate { get; }
    internal SessionTicketQualificationRoot()
    {
        string parent = Environment.GetEnvironmentVariable("WEFT_HOST_PROBE_CHILD_ROOT") ?? throw new EntraProtocolFixtureFailure("The owned probe root was not supplied.");
        EntraProtocolFixturePolicy.Check(Path.IsPathFullyQualified(parent) && new DirectoryInfo(parent).LinkTarget is null
            && File.GetUnixFileMode(parent) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute),
            "The owned probe parent is not private and direct.");
        Root = Path.Combine(parent, "privacy-keys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Keys = Path.Combine(Root, "keys"); Directory.CreateDirectory(Keys, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Weft owned privacy key fixture", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        Certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(3));
    }
    internal ServerSessionKeyProfileOptions Options(Guid organization) => new(organization, "Weft owned privacy116 component", Keys);
    internal void AssertEncryptedRing() => EntraProtocolFixturePolicy.Check(Directory.GetFiles(Keys).Length != 0
        && Directory.GetFiles(Keys).All(path => XDocument.Load(path).Descendants().Any(value => value.Name.LocalName == "encryptedSecret")),
        "The owned component profile did not persist standard certificate-protected keys.");
    public void Dispose() { Certificate.Dispose(); Directory.Delete(Root, recursive: true); }
}
