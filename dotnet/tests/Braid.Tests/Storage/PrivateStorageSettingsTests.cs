using Braid.Storage.Blob;
using Braid.Storage.Hosting;

namespace Braid.Tests;

/// <summary>Pure parsing of the explicit private storage selection; no storage is contacted.</summary>
public sealed class PrivateStorageSettingsTests
{
    private const string Endpoint = "https://braidaccount.blob.core.windows.net";

    [Theory]
    [InlineData(null)]
    [InlineData("filesystem")]
    public void AnAbsentOrExplicitFileSystemKindSelectsTheRoot(string? kind)
    {
        var settings = PrivateStorageSettings.Read(Values(("BRAID_PRIVATE_STORAGE_KIND", kind),
            ("BRAID_PRIVATE_STORAGE_ROOT", "/var/lib/braid/private")));
        Assert.Equal(PrivateStorageKind.FileSystem, settings.Kind);
        Assert.Equal("/var/lib/braid/private", settings.RootPath);
        Assert.Null(settings.Blob);
        var bound = settings.Bind("/srv/braid");
        Assert.Equal(PrivateStorageKind.FileSystem, bound.Kind);
        Assert.Equal(4194304L, bound.NativeByteLimit);
        Assert.Equal(16777216L, bound.DerivedByteLimit);
        Assert.Throws<ArgumentException>(() => settings.Bind("/var/lib/braid"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Filesystem")]
    [InlineData("blob")]
    [InlineData("azure-blob ")]
    [InlineData("azure_blob")]
    public void UnknownKindsAreRefused(string kind) => Assert.Throws<ArgumentException>(() =>
        PrivateStorageSettings.Read(Values(("BRAID_PRIVATE_STORAGE_KIND", kind), ("BRAID_PRIVATE_STORAGE_ROOT", "/var/lib/braid/private"))));

    [Theory]
    [InlineData("BRAID_PRIVATE_STORAGE_BLOB_ENDPOINT", Endpoint)]
    [InlineData("BRAID_PRIVATE_STORAGE_BLOB_CONTAINER", "braid-private")]
    [InlineData("BRAID_PRIVATE_STORAGE_BLOB_CREDENTIAL", "managed-identity")]
    [InlineData("BRAID_PRIVATE_STORAGE_BLOB_CLIENT_ID", "11111111-2222-3333-4444-555555555555")]
    public void FileSystemStorageRefusesBlobSettings(string name, string value) => Assert.Throws<ArgumentException>(() =>
        PrivateStorageSettings.Read(Values(("BRAID_PRIVATE_STORAGE_ROOT", "/var/lib/braid/private"), (name, value))));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AMissingRootIsAMissingSetting(string? root) => Assert.Throws<InvalidOperationException>(() =>
        PrivateStorageSettings.Read(Values(("BRAID_PRIVATE_STORAGE_ROOT", root))));

    [Theory]
    [InlineData("relative/root")]
    [InlineData("/tmp/alias/../store")]
    [InlineData("/var/lib/braid/")]
    public void AMalformedRootIsRefused(string root) => Assert.Throws<ArgumentException>(() =>
        PrivateStorageSettings.Read(Values(("BRAID_PRIVATE_STORAGE_ROOT", root))));

    [Fact]
    public void AzureBlobSelectsManagedIdentityWithAnOptionalClientId()
    {
        var settings = PrivateStorageSettings.Read(Blob(("BRAID_PRIVATE_STORAGE_BLOB_CLIENT_ID", "11111111-2222-3333-4444-555555555555")));
        Assert.Equal(PrivateStorageKind.AzureBlob, settings.Kind);
        Assert.Null(settings.RootPath);
        var blob = Assert.IsType<BlobStorageOptions>(settings.Blob);
        Assert.Equal(new Uri(Endpoint), blob.ServiceUri);
        Assert.Equal("braid-private", blob.ContainerName);
        Assert.Equal(BlobStorageCredentialKind.ManagedIdentity, blob.Credential);
        Assert.Equal(Guid.Parse("11111111-2222-3333-4444-555555555555"), blob.ManagedIdentityClientId);
        var bound = settings.Bind("/srv/braid");
        Assert.Equal(PrivateStorageKind.AzureBlob, bound.Kind);
        Assert.Same(blob, bound.Blob);
        Assert.Null(PrivateStorageSettings.Read(Blob()).Blob!.ManagedIdentityClientId);
        Assert.Equal(BlobStorageCredentialKind.WorkloadIdentity, PrivateStorageSettings.Read(Blob(
            ("BRAID_PRIVATE_STORAGE_BLOB_CREDENTIAL", "workload-identity"))).Blob!.Credential);
        Assert.Equal(BlobStorageCredentialKind.Emulator, PrivateStorageSettings.Read(Blob(
            ("BRAID_PRIVATE_STORAGE_BLOB_CREDENTIAL", "emulator"),
            ("BRAID_PRIVATE_STORAGE_BLOB_ENDPOINT", "http://127.0.0.1:10000/devstoreaccount1"))).Blob!.Credential);
    }

    [Theory]
    [InlineData("BRAID_PRIVATE_STORAGE_BLOB_ENDPOINT")]
    [InlineData("BRAID_PRIVATE_STORAGE_BLOB_CONTAINER")]
    [InlineData("BRAID_PRIVATE_STORAGE_BLOB_CREDENTIAL")]
    public void MissingBlobSettingsAreMissingSettings(string name) =>
        Assert.Throws<InvalidOperationException>(() => PrivateStorageSettings.Read(Blob((name, null))));

    [Theory]
    [InlineData("BRAID_PRIVATE_STORAGE_ROOT", "/var/lib/braid/private")]
    [InlineData("BRAID_PRIVATE_STORAGE_BLOB_CREDENTIAL", "account-key")]
    [InlineData("BRAID_PRIVATE_STORAGE_BLOB_CREDENTIAL", "Managed-Identity")]
    [InlineData("BRAID_PRIVATE_STORAGE_BLOB_CLIENT_ID", "11111111-2222-3333-4444-55555555555A")]
    [InlineData("BRAID_PRIVATE_STORAGE_BLOB_CLIENT_ID", "{11111111-2222-3333-4444-555555555555}")]
    [InlineData("BRAID_PRIVATE_STORAGE_BLOB_CLIENT_ID", "00000000-0000-0000-0000-000000000000")]
    [InlineData("BRAID_PRIVATE_STORAGE_BLOB_ENDPOINT", "braidaccount.blob.core.windows.net")]
    [InlineData("BRAID_PRIVATE_STORAGE_BLOB_ENDPOINT", "http://braidaccount.blob.core.windows.net")]
    [InlineData("BRAID_PRIVATE_STORAGE_BLOB_ENDPOINT", Endpoint + "/?sv=2025-01-05&sig=redacted")]
    [InlineData("BRAID_PRIVATE_STORAGE_BLOB_CONTAINER", "Braid-Private")]
    public void MalformedOrConflictingBlobSettingsAreRefused(string name, string value) =>
        Assert.ThrowsAny<ArgumentException>(() => PrivateStorageSettings.Read(Blob((name, value))));

    [Fact]
    public void AClientIdIsOnlyForAManagedIdentityAndBlobMustBeSelectedExplicitly()
    {
        Assert.Throws<ArgumentException>(() => PrivateStorageSettings.Read(Blob(
            ("BRAID_PRIVATE_STORAGE_BLOB_CREDENTIAL", "workload-identity"),
            ("BRAID_PRIVATE_STORAGE_BLOB_CLIENT_ID", "11111111-2222-3333-4444-555555555555"))));
        Assert.Throws<ArgumentException>(() => PrivateStorageSettings.ReadBlob(Blob(("BRAID_PRIVATE_STORAGE_KIND", null))));
        Assert.Equal(PrivateStorageKind.AzureBlob, PrivateStorageSettings.ReadBlob(Blob()).Kind);
    }

    private static Func<string, string?> Blob(params (string Name, string? Value)[] overrides)
    {
        var values = new Dictionary<string, string?>
        {
            ["BRAID_PRIVATE_STORAGE_KIND"] = "azure-blob",
            ["BRAID_PRIVATE_STORAGE_BLOB_ENDPOINT"] = Endpoint,
            ["BRAID_PRIVATE_STORAGE_BLOB_CONTAINER"] = "braid-private",
            ["BRAID_PRIVATE_STORAGE_BLOB_CREDENTIAL"] = "managed-identity"
        };
        foreach (var (name, value) in overrides) values[name] = value;
        return name => values.GetValueOrDefault(name);
    }

    private static Func<string, string?> Values(params (string Name, string? Value)[] values)
    {
        var map = values.ToDictionary(value => value.Name, value => value.Value);
        return name => map.GetValueOrDefault(name);
    }
}
