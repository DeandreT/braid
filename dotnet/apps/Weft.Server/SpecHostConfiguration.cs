using System.Text;
using Npgsql;
using Weft.Api.Specs;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;
using Weft.Storage.Blob;

namespace Weft.Server;

internal static class SpecHostConfiguration
{
    internal static void Configure(IServiceCollection services, Func<string, string?> read, bool session)
    {
        var enabled = read("WEFT_SPEC_INTAKE_ENABLED") switch
        {
            null or "false" => false,
            "true" => true,
            _ => throw ServerHostOptions.Failure()
        };
        services.AddSingleton(new SpecApiOptions(enabled));
        // Public native admission is still closed pending Linux installation qualification and schema 46.
        // No environment flag or portable test result may mint an admitted worker composition.
        services.AddSingleton(SpecDocumentIntakeAdmission.Unavailable);
        if (!enabled) return;
        if (!session) throw ServerHostOptions.Failure();
        var reader = SessionHostResources.ReadProtectedConnection(read, "WEFT_POSTGRES_SPEC_READER_CONNECTION_STRING");
        var writer = SessionHostResources.ReadProtectedConnection(read, "WEFT_POSTGRES_SPEC_WRITER_CONNECTION_STRING");
        if (new NpgsqlConnectionStringBuilder(reader).Username != "weft_spec_reader" ||
            new NpgsqlConnectionStringBuilder(writer).Username != "weft_spec_writer") throw ServerHostOptions.Failure();
        var credential = read("WEFT_SPEC_BLOB_CREDENTIAL") switch
        {
            "managed-identity" => BlobStorageCredentialKind.ManagedIdentity,
            "workload-identity" => BlobStorageCredentialKind.WorkloadIdentity,
            _ => throw ServerHostOptions.Failure()
        };
        var options = new BlobStorageOptions(new Uri(ServerHostOptions.Required(read, "WEFT_SPEC_BLOB_SERVICE_URI")),
            ServerHostOptions.Required(read, "WEFT_SPEC_BLOB_CONTAINER"), credential,
            read("WEFT_SPEC_BLOB_MANAGED_IDENTITY_CLIENT_ID") is { } client ? Guid.ParseExact(client, "D") : null);
        var storeId = Guid.ParseExact(ServerHostOptions.Required(read, "WEFT_SPEC_STORE_ID"), "D");
        // Derived only from the actual admitted server-owned endpoint/container; no independent fingerprint knob.
        var digest = Sha256Digest.FromBytes(Encoding.UTF8.GetBytes("weft.spec-blob-location.v1\n" +
            options.ServiceUri.AbsoluteUri.TrimEnd('/') + "/" + options.ContainerName));
        var binding = new SpecStoreBinding(storeId, SpecStoreKind.Blob, digest);
        services.AddKeyedSingleton(SpecDatabaseProfile.Reader, (_, _) => new PostgresSpecBundleDatabase(reader, SpecDatabaseProfile.Reader));
        services.AddKeyedSingleton(SpecDatabaseProfile.Writer, (_, _) => new PostgresSpecBundleDatabase(writer, SpecDatabaseProfile.Writer));
        services.AddSingleton<ISpecBundleServiceFactory>(provider => new Factory(
            provider.GetRequiredKeyedService<PostgresSpecBundleDatabase>(SpecDatabaseProfile.Reader),
            provider.GetRequiredKeyedService<PostgresSpecBundleDatabase>(SpecDatabaseProfile.Writer), options, binding,
            provider.GetRequiredService<SpecDocumentIntakeAdmission>()));
    }

    private sealed class Factory(PostgresSpecBundleDatabase reader, PostgresSpecBundleDatabase writer,
        BlobStorageOptions options, SpecStoreBinding binding, SpecDocumentIntakeAdmission documents) : ISpecBundleServiceFactory
    {
        public async Task<SpecBundleService> OpenAsync(ActorContext actor, bool write, CancellationToken token) =>
            (await (write ? writer : reader).ForActor(actor, binding).CreateServiceAsync(async (registry, ct) =>
                await BlobSpecObjectStore.OpenAsync(options, binding.StoreId, registry, ct).ConfigureAwait(false), token).ConfigureAwait(false))
                .WithDocumentIntake(documents);
    }
}
