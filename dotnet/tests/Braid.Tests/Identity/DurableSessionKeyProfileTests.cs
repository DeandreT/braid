using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using Braid.Identity;

namespace Braid.Tests;

/// <summary>Actual standard Data Protection, certificate and private filesystem behavior.</summary>
public sealed class DurableSessionKeyProfileTests
{
    private static readonly Guid Organization = Guid.Parse("e8932546-a5df-4ada-9d38-99ed3c0b6720");
    private static ReadOnlySpan<byte> Proof => "synthetic-session-profile-proof"u8;

    [Fact]
    public void ExistingEmptyOrMissingDirectoryCannotSilentlyInitializeARing()
    {
        using var fixture = new SessionCodecTestProfile(Organization, bootstrap: false);
        var before = Snapshot(fixture.KeyDirectory);
        ConfigurationFailure(() => DurableSessionKeyProfile.Open(fixture.Options, fixture.Certificate, []));
        AssertSnapshot(before, fixture.KeyDirectory);
        var missing = Path.Combine(fixture.Root, "missing");
        ConfigurationFailure(() => DurableSessionKeyProfile.Open(
            new(Organization, fixture.Application, missing), fixture.Certificate, []));
        Assert.False(Directory.Exists(missing), "Normal startup created a missing directory.");
        AssertSnapshot(before, fixture.KeyDirectory);
    }

    [Fact]
    public void ExplicitBootstrapCreatesCertificateProtectedPrivateFilesAndRefusesReplay()
    {
        using var fixture = new SessionCodecTestProfile(Organization);
        var files = Directory.GetFiles(fixture.KeyDirectory);
        Assert.True(files.Length > 0, "Explicit bootstrap did not persist a ring.");
        foreach (var path in files)
        {
            Assert.True(PrivateFileMode(path) == (UnixFileMode.UserRead | UnixFileMode.UserWrite),
                "A standard persisted key file was not private0600.");
            var document = XDocument.Load(path);
            Assert.True(document.Descendants(XName.Get("EncryptedData", "http://www.w3.org/2001/04/xmlenc#")).Any(),
                "A bootstrap key file was not certificate encrypted by standard Data Protection.");
            Assert.False(document.Descendants().Any(element => element.Name.LocalName == "masterKey" &&
                !element.Ancestors().Any(parent => parent.Name.LocalName == "EncryptedData")),
                "A plaintext master-key element survived in the persisted file.");
        }
        var before = Snapshot(fixture.KeyDirectory);
        var now = MicrosecondNow();
        ConfigurationFailure(() => DurableSessionKeyProfile.BootstrapEmpty(fixture.Options, fixture.Certificate,
            [], now.AddDays(-1), now.AddDays(30)));
        AssertSnapshot(before, fixture.KeyDirectory);
    }

    [Fact]
    public void InvalidBootstrapTimesLeaveTheExplicitEmptyDirectoryUntouched()
    {
        using var fixture = new SessionCodecTestProfile(Organization, bootstrap: false);
        var now = MicrosecondNow();
        var before = Snapshot(fixture.KeyDirectory);
        (DateTimeOffset Start, DateTimeOffset End)[] invalid =
        [
            (now, now), (now, now.AddTicks(-10)),
            (now.ToOffset(TimeSpan.FromHours(1)), now.AddDays(1)),
            (DateTimeOffset.MinValue, now.AddDays(1)), (now, DateTimeOffset.MaxValue)
        ];
        foreach (var vector in invalid)
        {
            ConfigurationFailure(() => DurableSessionKeyProfile.BootstrapEmpty(fixture.Options,
                fixture.Certificate, [], vector.Start, vector.End));
            AssertSnapshot(before, fixture.KeyDirectory);
        }
    }

    [Fact]
    public void FreshProfilesRecoverPersistedCiphertextWithoutSharedProviderState()
    {
        using var fixture = new SessionCodecTestProfile(Organization);
        var plaintext = Proof.ToArray();
        var ciphertext = fixture.Profile.Protect(plaintext);
        fixture.Profile.Dispose();
        try
        {
            using var first = DurableSessionKeyProfile.Open(fixture.Options, fixture.Certificate, []);
            var recovered = first.Unprotect(ciphertext);
            try
            {
                Assert.True(recovered.AsSpan().SequenceEqual(Proof),
                    "A fresh independently composed profile did not recover the persisted protected value.");
            }
            finally { CryptographicOperations.ZeroMemory(recovered); }
            first.Dispose();
            using var second = DurableSessionKeyProfile.Open(fixture.Options, fixture.Certificate, []);
            recovered = second.Unprotect(ciphertext);
            try
            {
                Assert.True(recovered.AsSpan().SequenceEqual(Proof),
                    "A second fresh profile depended on a disposed provider's key cache.");
            }
            finally { CryptographicOperations.ZeroMemory(recovered); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    [Fact]
    public void WrongDecryptCertificateFailsBeforeRotationAndPreservesTheFileClosure()
    {
        using var fixture = new SessionCodecTestProfile(Organization);
        using var wrong = SessionCodecTestProfile.NewCertificate();
        fixture.Profile.Dispose();
        var before = Snapshot(fixture.KeyDirectory);
        ConfigurationFailure(() => DurableSessionKeyProfile.Open(fixture.Options, wrong, []));
        AssertSnapshot(before, fixture.KeyDirectory);
        using var recovered = DurableSessionKeyProfile.Open(fixture.Options, fixture.Certificate, []);
        AssertSnapshot(before, fixture.KeyDirectory);
    }

    [Fact]
    public void AGoodCurrentKeyDoesNotMaskUnreadableRetainedExpiredOrFutureKeys()
    {
        foreach (var future in new[] { false, true })
        {
            using var fixture = new SessionCodecTestProfile(Organization, bootstrap: false);
            using var current = SessionCodecTestProfile.NewCertificate();
            var now = MicrosecondNow();
            var historical = fixture.NewStandardProvider(fixture.Certificate).GetRequiredService<IKeyManager>();
            historical.CreateNewKey(future ? now.AddDays(10) : now.AddDays(-90),
                future ? now.AddDays(100) : now.AddDays(-30));
            var active = fixture.NewStandardProvider(current, fixture.Certificate).GetRequiredService<IKeyManager>();
            active.CreateNewKey(now.AddDays(-1), now.AddDays(30));
            var before = Snapshot(fixture.KeyDirectory);
            Assert.True(before.Length == 2, "The retained-key admission fixture did not persist two distinct keys.");
            ConfigurationFailure(() => DurableSessionKeyProfile.Open(fixture.Options, current, []));
            AssertSnapshot(before, fixture.KeyDirectory);
            using var admitted = DurableSessionKeyProfile.Open(fixture.Options, current, [fixture.Certificate]);
            var proof = Proof.ToArray();
            var protectedProof = admitted.Protect(proof);
            var recovered = admitted.Unprotect(protectedProof);
            try
            {
                Assert.True(recovered.AsSpan().SequenceEqual(Proof),
                    "Explicit retained decrypt material did not admit the complete ring.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(proof);
                CryptographicOperations.ZeroMemory(protectedProof);
                CryptographicOperations.ZeroMemory(recovered);
            }
        }
    }

    [Fact]
    public void RotationRetainsOldCiphertextWithExplicitCertificateOverlapAndFreshProviders()
    {
        using var fixture = new SessionCodecTestProfile(Organization);
        using var current = SessionCodecTestProfile.NewCertificate();
        var proof = Proof.ToArray();
        var oldCiphertext = fixture.Profile.Protect(proof);
        fixture.Profile.Dispose();
        var now = MicrosecondNow();
        var manager = fixture.NewStandardProvider(current, fixture.Certificate).GetRequiredService<IKeyManager>();
        manager.CreateNewKey(now.AddHours(-1), now.AddDays(60));
        byte[]? newCiphertext = null;
        try
        {
            using (var rotated = DurableSessionKeyProfile.Open(fixture.Options, current, [fixture.Certificate]))
            {
                var oldPlaintext = rotated.Unprotect(oldCiphertext);
                try { Assert.True(oldPlaintext.AsSpan().SequenceEqual(Proof), "Rotation lost a live old payload."); }
                finally { CryptographicOperations.ZeroMemory(oldPlaintext); }
                newCiphertext = rotated.Protect(proof);
            }
            using var restarted = DurableSessionKeyProfile.Open(fixture.Options, current, [fixture.Certificate]);
            foreach (var ciphertext in new[] { oldCiphertext, newCiphertext ??
                throw new InvalidOperationException("The rotation fixture did not produce a protected value.") })
            {
                var recovered = restarted.Unprotect(ciphertext);
                try { Assert.True(recovered.AsSpan().SequenceEqual(Proof), "Fresh rotation recovery lost a payload."); }
                finally { CryptographicOperations.ZeroMemory(recovered); }
            }
            var before = Snapshot(fixture.KeyDirectory);
            ConfigurationFailure(() => DurableSessionKeyProfile.Open(fixture.Options, current, []));
            AssertSnapshot(before, fixture.KeyDirectory);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(proof);
            CryptographicOperations.ZeroMemory(oldCiphertext);
            if (newCiphertext is not null)
                CryptographicOperations.ZeroMemory(newCiphertext);
        }
    }

    [Fact]
    public void MissingAndCorruptPersistedMaterialCannotBeReplacedDuringNormalStartup()
    {
        using var fixture = new SessionCodecTestProfile(Organization);
        fixture.Profile.Dispose();
        var key = Directory.GetFiles(fixture.KeyDirectory).Single();
        var original = File.ReadAllBytes(key);
        try
        {
            File.WriteAllText(key, "<malformed-key");
            var corrupt = Snapshot(fixture.KeyDirectory);
            ConfigurationFailure(() => DurableSessionKeyProfile.Open(fixture.Options, fixture.Certificate, []));
            AssertSnapshot(corrupt, fixture.KeyDirectory);
            File.Delete(key);
            var missing = Snapshot(fixture.KeyDirectory);
            ConfigurationFailure(() => DurableSessionKeyProfile.Open(fixture.Options, fixture.Certificate, []));
            AssertSnapshot(missing, fixture.KeyDirectory);
            File.WriteAllBytes(key, original);
            SetMode(key, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            using var recovered = DurableSessionKeyProfile.Open(fixture.Options, fixture.Certificate, []);
        }
        finally { CryptographicOperations.ZeroMemory(original); }
    }

    [Fact]
    public void CurrentAndOverlapCertificatesMustExposeCallerOwnedPrivateKeys()
    {
        using var fixture = new SessionCodecTestProfile(Organization);
        using var publicOnly = X509CertificateLoader.LoadCertificate(fixture.Certificate.Export(X509ContentType.Cert));
        var before = Snapshot(fixture.KeyDirectory);
        ConfigurationFailure(() => DurableSessionKeyProfile.Open(fixture.Options, publicOnly, []));
        ConfigurationFailure(() => DurableSessionKeyProfile.Open(fixture.Options, fixture.Certificate, [publicOnly]));
        ConfigurationFailure(() => DurableSessionKeyProfile.Open(fixture.Options, fixture.Certificate, [null!]));
        AssertSnapshot(before, fixture.KeyDirectory);
    }

    [Fact]
    public void DisposingAProfileDoesNotDisposeItsBorrowedCertificate()
    {
        using var fixture = new SessionCodecTestProfile(Organization);
        fixture.Profile.Dispose();
        fixture.Profile.Dispose();
        using var privateKey = fixture.Certificate.GetRSAPrivateKey();
        Assert.NotNull(privateKey);
        var signature = privateKey.SignData(Proof, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        try
        {
            Assert.True(privateKey.VerifyData(Proof, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
                "Disposing the profile disposed its caller-owned certificate key.");
        }
        finally { CryptographicOperations.ZeroMemory(signature); }
        Failure(ServerSessionIdentityFailureKind.Unavailable, () => fixture.Profile.Protect(Proof.ToArray()));
    }

    [Fact]
    public void ExactApplicationAndOrganizationPurposesSeparateOtherwiseSharedKeyMaterial()
    {
        using var fixture = new SessionCodecTestProfile(Organization);
        var proof = Proof.ToArray();
        var ciphertext = fixture.Profile.Protect(proof);
        try
        {
            var alternatives = new[]
            {
                new ServerSessionKeyProfileOptions(Organization, fixture.Application.ToLowerInvariant(), fixture.KeyDirectory),
                new ServerSessionKeyProfileOptions(Organization, fixture.Application + " ", fixture.KeyDirectory),
                new ServerSessionKeyProfileOptions(Guid.NewGuid(), fixture.Application, fixture.KeyDirectory)
            };
            foreach (var options in alternatives)
            {
                using var foreign = DurableSessionKeyProfile.Open(options, fixture.Certificate, []);
                Failure(ServerSessionIdentityFailureKind.IntegrityFailure, () => foreign.Unprotect(ciphertext));
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(proof);
            CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    [Fact]
    public void RevokedOnlyRingIsNotAnAdmittedReplacementForExistingMaterial()
    {
        using var fixture = new SessionCodecTestProfile(Organization);
        fixture.Profile.Dispose();
        var manager = fixture.NewStandardProvider(fixture.Certificate).GetRequiredService<IKeyManager>();
        var keys = manager.GetAllKeys();
        foreach (var key in keys)
            manager.RevokeKey(key.KeyId, reason: "synthetic test revocation");
        var before = Snapshot(fixture.KeyDirectory);
        ConfigurationFailure(() => DurableSessionKeyProfile.Open(fixture.Options, fixture.Certificate, []));
        AssertSnapshot(before, fixture.KeyDirectory);
    }

    [Fact]
    public void PrivateRootAndChildModesAreRequiredAndNeverRepaired()
    {
        using var fixture = new SessionCodecTestProfile(Organization);
        fixture.Profile.Dispose();
        var rootMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        var key = Directory.GetFiles(fixture.KeyDirectory).Single();
        try
        {
            SetMode(fixture.KeyDirectory, rootMode | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
            var before = Snapshot(fixture.KeyDirectory);
            ConfigurationFailure(() => DurableSessionKeyProfile.Open(fixture.Options, fixture.Certificate, []));
            AssertSnapshot(before, fixture.KeyDirectory);
            Assert.True(PrivateFileMode(fixture.KeyDirectory) ==
                (rootMode | UnixFileMode.GroupRead | UnixFileMode.GroupExecute), "Startup repaired the root mode.");
            SetMode(fixture.KeyDirectory, rootMode);
            SetMode(key, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
            before = Snapshot(fixture.KeyDirectory);
            ConfigurationFailure(() => DurableSessionKeyProfile.Open(fixture.Options, fixture.Certificate, []));
            AssertSnapshot(before, fixture.KeyDirectory);
            Assert.True(PrivateFileMode(key) ==
                (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead), "Startup repaired a child mode.");
        }
        finally
        {
            SetMode(fixture.KeyDirectory, rootMode);
            SetMode(key, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void RootAncestorAndChildSymlinksAndChildDirectoriesAreRefused()
    {
        using var fixture = new SessionCodecTestProfile(Organization);
        var rootAlias = Path.Combine(fixture.Root, "root-alias");
        Directory.CreateSymbolicLink(rootAlias, fixture.KeyDirectory);
        ConfigurationFailure(() => DurableSessionKeyProfile.Open(
            new(Organization, fixture.Application, rootAlias), fixture.Certificate, []));
        Directory.Delete(rootAlias);

        var ancestorAlias = Path.Combine(fixture.Root, "ancestor-alias");
        Directory.CreateSymbolicLink(ancestorAlias, fixture.Root);
        ConfigurationFailure(() => DurableSessionKeyProfile.Open(
            new(Organization, fixture.Application, Path.Combine(ancestorAlias, "keys")), fixture.Certificate, []));
        Directory.Delete(ancestorAlias);

        var child = Path.Combine(fixture.KeyDirectory, "unexpected.xml");
        File.CreateSymbolicLink(child, Directory.GetFiles(fixture.KeyDirectory).Single());
        ConfigurationFailure(() => DurableSessionKeyProfile.Open(fixture.Options, fixture.Certificate, []));
        File.Delete(child);
        CreatePrivateDirectory(child);
        ConfigurationFailure(() => DurableSessionKeyProfile.Open(fixture.Options, fixture.Certificate, []));
        Directory.Delete(child);
    }

    [Fact]
    public void WarmProfileRevalidatesItsRootIdentityAndPrivateFileClosure()
    {
        using var fixture = new SessionCodecTestProfile(Organization);
        var proof = Proof.ToArray();
        var ciphertext = fixture.Profile.Protect(proof);
        var moved = Path.Combine(fixture.Root, "moved-keys");
        try
        {
            Directory.Move(fixture.KeyDirectory, moved);
            CreatePrivateDirectory(fixture.KeyDirectory);
            Failure(ServerSessionIdentityFailureKind.Unavailable, () => fixture.Profile.Protect(proof));
            Failure(ServerSessionIdentityFailureKind.Unavailable, () => fixture.Profile.Unprotect(ciphertext));
            Assert.False(Directory.EnumerateFileSystemEntries(fixture.KeyDirectory).Any(),
                "A refused warm profile initialized the replacement directory.");
            Directory.Delete(fixture.KeyDirectory);
            Directory.Move(moved, fixture.KeyDirectory);
            var key = Directory.GetFiles(fixture.KeyDirectory).Single();
            try
            {
                SetMode(key, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
                Failure(ServerSessionIdentityFailureKind.Unavailable, () => fixture.Profile.Unprotect(ciphertext));
            }
            finally { SetMode(key, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            var recovered = fixture.Profile.Unprotect(ciphertext);
            try { Assert.True(recovered.AsSpan().SequenceEqual(Proof), "Restored exact private material was not usable."); }
            finally { CryptographicOperations.ZeroMemory(recovered); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(proof);
            CryptographicOperations.ZeroMemory(ciphertext);
            if (Directory.Exists(moved))
            {
                if (Directory.Exists(fixture.KeyDirectory))
                    Directory.Delete(fixture.KeyDirectory);
                Directory.Move(moved, fixture.KeyDirectory);
            }
        }
    }

    [Fact]
    public void OptionsPreserveLiteralNamesAndRefuseInvalidScalarUnicodePathsAndBounds()
    {
        using var fixture = new SessionCodecTestProfile(Organization, bootstrap: false);
        var exact = new ServerSessionKeyProfileOptions(Organization, " Realm é ", fixture.KeyDirectory);
        Assert.True(string.Equals(exact.ApplicationName, " Realm é ", StringComparison.Ordinal),
            "The configured realm was normalized.");
        _ = new ServerSessionKeyProfileOptions(Organization, new string('a', 128), fixture.KeyDirectory);
        _ = new ServerSessionKeyProfileOptions(Organization, new string('é', 64), fixture.KeyDirectory);
        string[] invalidNames = ["", "bad\0name", "bad\nname", "bad\u0085name", "\ud800", "\udc00",
            new string('a', 129), new string('é', 65)];
        foreach (var name in invalidNames)
            ConfigurationFailure(() => new ServerSessionKeyProfileOptions(Organization, name, fixture.KeyDirectory));
        string[] invalidPaths = ["", "relative/keys", fixture.KeyDirectory + "/", fixture.KeyDirectory + "/../keys",
            fixture.KeyDirectory + "\0"];
        foreach (var path in invalidPaths)
            ConfigurationFailure(() => new ServerSessionKeyProfileOptions(Organization, "realm", path));
        ConfigurationFailure(() => new ServerSessionKeyProfileOptions(Guid.Empty, "realm", fixture.KeyDirectory));
        Assert.True(string.Equals(exact.ToString(), nameof(ServerSessionKeyProfileOptions), StringComparison.Ordinal) &&
            string.Equals(JsonSerializer.Serialize(exact), "{}", StringComparison.Ordinal),
            "Profile options exposed private configuration in default diagnostics.");
    }

    private static DateTimeOffset MicrosecondNow()
    {
        var now = DateTimeOffset.UtcNow;
        return new(now.Ticks - now.Ticks % 10, TimeSpan.Zero);
    }

    private static (string Name, string Digest, UnixFileMode Mode)[] Snapshot(string directory) =>
        Directory.GetFiles(directory).Order(StringComparer.Ordinal).Select(path =>
        {
            var bytes = File.ReadAllBytes(path);
            try { return (Path.GetFileName(path), Convert.ToHexString(SHA256.HashData(bytes)), PrivateFileMode(path)); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }).ToArray();

    private static void AssertSnapshot((string Name, string Digest, UnixFileMode Mode)[] before, string directory) =>
        Assert.True(before.AsSpan().SequenceEqual(Snapshot(directory)),
            "A refused key-profile operation changed the persisted file closure.");

    private static UnixFileMode PrivateFileMode(string path)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("The private mode fixture requires Linux.");
        return File.GetUnixFileMode(path);
    }

    private static void SetMode(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("The private mode fixture requires Linux.");
        File.SetUnixFileMode(path, mode);
    }

    private static void CreatePrivateDirectory(string path)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("The private directory fixture requires Linux.");
        Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void ConfigurationFailure(Action action) => Failure(ServerSessionIdentityFailureKind.ConfigurationFailure, action);

    private static void Failure(ServerSessionIdentityFailureKind expected, Action action)
    {
        var error = Assert.Throws<ServerSessionIdentityException>(action);
        Assert.True(error.Kind == expected && error.InnerException is null &&
            string.Equals(error.Message, "The server session operation failed.", StringComparison.Ordinal) &&
            string.Equals(error.ToString(), nameof(ServerSessionIdentityException), StringComparison.Ordinal),
            "The key-profile closed failure or redacted diagnostics changed.");
    }
}
