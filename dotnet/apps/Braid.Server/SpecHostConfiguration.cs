using System.Text;
using Npgsql;
using Braid.Api.Specs;
using Braid.Application;
using Braid.Domain;
using Braid.Persistence.Postgres;
using Braid.Storage.Blob;

namespace Braid.Server;

internal static class SpecHostConfiguration
{
    internal static void Configure(IServiceCollection services, Func<string, string?> read, bool session)
    {
        var enabled = read("BRAID_SPEC_INTAKE_ENABLED") switch
        {
            null or "false" => false,
            "true" => true,
            _ => throw ServerHostOptions.Failure()
        };
        services.AddSingleton(new SpecApiOptions(enabled));
        services.AddSingleton(SpecDocumentIntakeAdmission.Unavailable);
        var documentSettings = enabled ? SpecDocumentHostSettings.Read(read) : null;
        services.AddSingleton(new SpecDocumentHostResources(documentSettings));
        services.AddHostedService(provider => provider.GetRequiredService<SpecDocumentHostResources>());
        if (!enabled) return;
        if (!session) throw ServerHostOptions.Failure();
        var reader = SessionHostResources.ReadProtectedConnection(read, "BRAID_POSTGRES_SPEC_READER_CONNECTION_STRING");
        var writer = SessionHostResources.ReadProtectedConnection(read, "BRAID_POSTGRES_SPEC_WRITER_CONNECTION_STRING");
        if (new NpgsqlConnectionStringBuilder(reader).Username != "braid_spec_reader" ||
            new NpgsqlConnectionStringBuilder(writer).Username != "braid_spec_writer") throw ServerHostOptions.Failure();
        var credential = read("BRAID_SPEC_BLOB_CREDENTIAL") switch
        {
            "managed-identity" => BlobStorageCredentialKind.ManagedIdentity,
            "workload-identity" => BlobStorageCredentialKind.WorkloadIdentity,
            _ => throw ServerHostOptions.Failure()
        };
        var options = new BlobStorageOptions(new Uri(ServerHostOptions.Required(read, "BRAID_SPEC_BLOB_SERVICE_URI")),
            ServerHostOptions.Required(read, "BRAID_SPEC_BLOB_CONTAINER"), credential,
            read("BRAID_SPEC_BLOB_MANAGED_IDENTITY_CLIENT_ID") is { } client ? Guid.ParseExact(client, "D") : null);
        var storeId = Guid.ParseExact(ServerHostOptions.Required(read, "BRAID_SPEC_STORE_ID"), "D");
        // Derived only from the actual admitted server-owned endpoint/container; no independent fingerprint knob.
        var digest = Sha256Digest.FromBytes(Encoding.UTF8.GetBytes("braid.spec-blob-location.v1\n" +
            options.ServiceUri.AbsoluteUri.TrimEnd('/') + "/" + options.ContainerName));
        var binding = new SpecStoreBinding(storeId, SpecStoreKind.Blob, digest);
        services.AddSingleton(binding);
        services.AddKeyedSingleton(SpecDatabaseProfile.Reader, (_, _) => new PostgresSpecBundleDatabase(reader, SpecDatabaseProfile.Reader));
        services.AddKeyedSingleton(SpecDatabaseProfile.Writer, (_, _) => new PostgresSpecBundleDatabase(writer, SpecDatabaseProfile.Writer));
        services.AddSingleton<ISpecBundleServiceFactory>(provider => new Factory(
            provider.GetRequiredKeyedService<PostgresSpecBundleDatabase>(SpecDatabaseProfile.Reader),
            provider.GetRequiredKeyedService<PostgresSpecBundleDatabase>(SpecDatabaseProfile.Writer), options, binding,
            provider.GetRequiredService<SpecDocumentHostResources>()));
    }

    private sealed class Factory(PostgresSpecBundleDatabase reader, PostgresSpecBundleDatabase writer,
        BlobStorageOptions options, SpecStoreBinding binding, SpecDocumentHostResources documents) : ISpecBundleServiceFactory
    {
        public async Task<SpecBundleService> OpenAsync(ActorContext actor, bool write, CancellationToken token)
        {
            var session = (write ? writer : reader).ForActor(actor, binding);
            return (await session.CreateServiceAsync(async (registry, ct) =>
                await BlobSpecObjectStore.OpenAsync(options, binding.StoreId, registry, ct).ConfigureAwait(false), token).ConfigureAwait(false))
                .WithDocumentIntake(documents.Bind(session));
        }
    }
}
