using Weft.Storage.Blob;
using Weft.Storage.Hosting;

namespace Weft.Tests;

/// <summary>Pure parsing of the explicit private storage selection; no storage is contacted.</summary>
public sealed class PrivateStorageSettingsTests
{
    private const string Endpoint = "https://weftaccount.blob.core.windows.net";

    [Theory]
    [InlineData(null)]
    [InlineData("filesystem")]
    public void AnAbsentOrExplicitFileSystemKindSelectsTheRoot(string? kind)
    {
        var settings = PrivateStorageSettings.Read(Values(("WEFT_PRIVATE_STORAGE_KIND", kind),
            ("WEFT_PRIVATE_STORAGE_ROOT", "/var/lib/weft/private")));
        Assert.Equal(PrivateStorageKind.FileSystem, settings.Kind);
        Assert.Equal("/var/lib/weft/private", settings.RootPath);
        Assert.Null(settings.Blob);
        var bound = settings.Bind("/srv/weft");
        Assert.Equal(PrivateStorageKind.FileSystem, bound.Kind);
        Assert.Equal(4194304L, bound.NativeByteLimit);
        Assert.Equal(16777216L, bound.DerivedByteLimit);
        Assert.Throws<ArgumentException>(() => settings.Bind("/var/lib/weft"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Filesystem")]
    [InlineData("blob")]
    [InlineData("azure-blob ")]
    [InlineData("azure_blob")]
    public void UnknownKindsAreRefused(string kind) => Assert.Throws<ArgumentException>(() =>
        PrivateStorageSettings.Read(Values(("WEFT_PRIVATE_STORAGE_KIND", kind), ("WEFT_PRIVATE_STORAGE_ROOT", "/var/lib/weft/private"))));

    [Theory]
    [InlineData("WEFT_PRIVATE_STORAGE_BLOB_ENDPOINT", Endpoint)]
    [InlineData("WEFT_PRIVATE_STORAGE_BLOB_CONTAINER", "weft-private")]
    [InlineData("WEFT_PRIVATE_STORAGE_BLOB_CREDENTIAL", "managed-identity")]
    [InlineData("WEFT_PRIVATE_STORAGE_BLOB_CLIENT_ID", "11111111-2222-3333-4444-555555555555")]
    public void FileSystemStorageRefusesBlobSettings(string name, string value) => Assert.Throws<ArgumentException>(() =>
        PrivateStorageSettings.Read(Values(("WEFT_PRIVATE_STORAGE_ROOT", "/var/lib/weft/private"), (name, value))));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AMissingRootIsAMissingSetting(string? root) => Assert.Throws<InvalidOperationException>(() =>
        PrivateStorageSettings.Read(Values(("WEFT_PRIVATE_STORAGE_ROOT", root))));

    [Theory]
    [InlineData("relative/root")]
    [InlineData("/tmp/alias/../store")]
    [InlineData("/var/lib/weft/")]
    public void AMalformedRootIsRefused(string root) => Assert.Throws<ArgumentException>(() =>
        PrivateStorageSettings.Read(Values(("WEFT_PRIVATE_STORAGE_ROOT", root))));

    [Fact]
    public void AzureBlobSelectsManagedIdentityWithAnOptionalClientId()
    {
        var settings = PrivateStorageSettings.Read(Blob(("WEFT_PRIVATE_STORAGE_BLOB_CLIENT_ID", "11111111-2222-3333-4444-555555555555")));
        Assert.Equal(PrivateStorageKind.AzureBlob, settings.Kind);
        Assert.Null(settings.RootPath);
        var blob = Assert.IsType<BlobStorageOptions>(settings.Blob);
        Assert.Equal(new Uri(Endpoint), blob.ServiceUri);
        Assert.Equal("weft-private", blob.ContainerName);
        Assert.Equal(BlobStorageCredentialKind.ManagedIdentity, blob.Credential);
        Assert.Equal(Guid.Parse("11111111-2222-3333-4444-555555555555"), blob.ManagedIdentityClientId);
        var bound = settings.Bind("/srv/weft");
        Assert.Equal(PrivateStorageKind.AzureBlob, bound.Kind);
        Assert.Same(blob, bound.Blob);
        Assert.Null(PrivateStorageSettings.Read(Blob()).Blob!.ManagedIdentityClientId);
        Assert.Equal(BlobStorageCredentialKind.WorkloadIdentity, PrivateStorageSettings.Read(Blob(
            ("WEFT_PRIVATE_STORAGE_BLOB_CREDENTIAL", "workload-identity"))).Blob!.Credential);
        Assert.Equal(BlobStorageCredentialKind.Emulator, PrivateStorageSettings.Read(Blob(
            ("WEFT_PRIVATE_STORAGE_BLOB_CREDENTIAL", "emulator"),
            ("WEFT_PRIVATE_STORAGE_BLOB_ENDPOINT", "http://127.0.0.1:10000/devstoreaccount1"))).Blob!.Credential);
    }

    [Theory]
    [InlineData("WEFT_PRIVATE_STORAGE_BLOB_ENDPOINT")]
    [InlineData("WEFT_PRIVATE_STORAGE_BLOB_CONTAINER")]
    [InlineData("WEFT_PRIVATE_STORAGE_BLOB_CREDENTIAL")]
    public void MissingBlobSettingsAreMissingSettings(string name) =>
        Assert.Throws<InvalidOperationException>(() => PrivateStorageSettings.Read(Blob((name, null))));

    [Theory]
    [InlineData("WEFT_PRIVATE_STORAGE_ROOT", "/var/lib/weft/private")]
    [InlineData("WEFT_PRIVATE_STORAGE_BLOB_CREDENTIAL", "account-key")]
    [InlineData("WEFT_PRIVATE_STORAGE_BLOB_CREDENTIAL", "Managed-Identity")]
    [InlineData("WEFT_PRIVATE_STORAGE_BLOB_CLIENT_ID", "11111111-2222-3333-4444-55555555555A")]
    [InlineData("WEFT_PRIVATE_STORAGE_BLOB_CLIENT_ID", "{11111111-2222-3333-4444-555555555555}")]
    [InlineData("WEFT_PRIVATE_STORAGE_BLOB_CLIENT_ID", "00000000-0000-0000-0000-000000000000")]
    [InlineData("WEFT_PRIVATE_STORAGE_BLOB_ENDPOINT", "weftaccount.blob.core.windows.net")]
    [InlineData("WEFT_PRIVATE_STORAGE_BLOB_ENDPOINT", "http://weftaccount.blob.core.windows.net")]
    [InlineData("WEFT_PRIVATE_STORAGE_BLOB_ENDPOINT", Endpoint + "/?sv=2025-01-05&sig=redacted")]
    [InlineData("WEFT_PRIVATE_STORAGE_BLOB_CONTAINER", "Weft-Private")]
    public void MalformedOrConflictingBlobSettingsAreRefused(string name, string value) =>
        Assert.ThrowsAny<ArgumentException>(() => PrivateStorageSettings.Read(Blob((name, value))));

    [Fact]
    public void AClientIdIsOnlyForAManagedIdentityAndBlobMustBeSelectedExplicitly()
    {
        Assert.Throws<ArgumentException>(() => PrivateStorageSettings.Read(Blob(
            ("WEFT_PRIVATE_STORAGE_BLOB_CREDENTIAL", "workload-identity"),
            ("WEFT_PRIVATE_STORAGE_BLOB_CLIENT_ID", "11111111-2222-3333-4444-555555555555"))));
        Assert.Throws<ArgumentException>(() => PrivateStorageSettings.ReadBlob(Blob(("WEFT_PRIVATE_STORAGE_KIND", null))));
        Assert.Equal(PrivateStorageKind.AzureBlob, PrivateStorageSettings.ReadBlob(Blob()).Kind);
    }

    private static Func<string, string?> Blob(params (string Name, string? Value)[] overrides)
    {
        var values = new Dictionary<string, string?>
        {
            ["WEFT_PRIVATE_STORAGE_KIND"] = "azure-blob",
            ["WEFT_PRIVATE_STORAGE_BLOB_ENDPOINT"] = Endpoint,
            ["WEFT_PRIVATE_STORAGE_BLOB_CONTAINER"] = "weft-private",
            ["WEFT_PRIVATE_STORAGE_BLOB_CREDENTIAL"] = "managed-identity"
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
