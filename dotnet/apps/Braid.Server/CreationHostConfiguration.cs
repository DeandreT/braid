using System.Data.Common;
using Npgsql;
using Braid.Api.Creation;
using Braid.Application;
using Braid.Domain;
using Braid.MappingBackends;
using Braid.MappingBackends.Workers;
using Braid.Persistence.Postgres;

namespace Braid.Server;

internal sealed record CreationFerruleBinding(string BuildIdentity);

/// <summary>Concrete lazy server composition; configuration validates deliveries, admission opens the real ports.</summary>
internal static class CreationHostConfiguration
{
    internal const string ReaderConnection = "BRAID_POSTGRES_CREATION_READER_CONNECTION_STRING";
    internal const string WriterConnection = "BRAID_POSTGRES_CREATION_WRITER_CONNECTION_STRING";
    internal const string ApproverConnection = "BRAID_POSTGRES_CREATION_APPROVER_CONNECTION_STRING";

    internal static void Configure(IServiceCollection services, Func<string, string?> read, bool session)
    {
        var enabled = ServerHostOptions.Flag(read, "BRAID_CREATION_ENABLED");
        services.AddSingleton(new CreationRuntimeOptions(enabled));
        if (!enabled) return;
        if (!session || !services.Any(d => d.ServiceType == typeof(ChatHostDatabases)) ||
            !services.Any(d => d.ServiceType == typeof(ISpecBundleServiceFactory)) ||
            !services.Any(d => d.ServiceType == typeof(SpecStoreBinding)) ||
            !services.Any(d => d.ServiceType == typeof(PostgresSpecBundleDatabase) && Equals(d.ServiceKey, SpecDatabaseProfile.Reader)) ||
            !services.Any(d => d.ServiceType == typeof(CreationFerruleBinding)) ||
            !services.Any(d => d.ServiceType == typeof(IRevisionIngestionService)) ||
            !services.Any(d => d.ServiceType == typeof(IIngestionWorkflowReader))) throw ServerHostOptions.Failure();
        var reader = SessionHostResources.ReadProtectedConnection(read, ReaderConnection);
        var writer = SessionHostResources.ReadProtectedConnection(read, WriterConnection);
        var approver = SessionHostResources.ReadProtectedConnection(read, ApproverConnection);
        var chat = SessionHostResources.ReadProtectedConnection(read, "BRAID_CHAT_READER_CONNECTION_STRING");
        var spec = SessionHostResources.ReadProtectedConnection(read, "BRAID_POSTGRES_SPEC_READER_CONNECTION_STRING");
        AdmitPlacement(reader, writer, approver, chat, spec);
        Register(services, reader, writer, approver);
    }

    // Shares the concrete composition with local contract tests; production reaches this only after protected delivery.
    internal static void Register(IServiceCollection services, string reader, string writer, string approver)
    {
        AdmitConnections(reader, writer, approver);
        services.AddSingleton(_ => new PostgresMappingCreationStore(reader, writer, approver));
        services.AddSingleton<IMappingCreationProposalStore>(p => p.GetRequiredService<PostgresMappingCreationStore>());
        services.AddSingleton<IMappingCreationApprovalStore>(p => p.GetRequiredService<PostgresMappingCreationStore>());
        services.AddSingleton<IMappingCreationAuthority>(p => p.GetRequiredService<PostgresMappingCreationStore>());
        services.AddSingleton<ICreationDiscovery>(p => new PostgresCreationDiscovery(
            p.GetRequiredKeyedService<PostgresSpecBundleDatabase>(SpecDatabaseProfile.Reader), p.GetRequiredService<SpecStoreBinding>()));
        foreach (var profile in new[] { FerruleServiceProtocol.AuthoringProfile, FerruleServiceProtocol.AuthoredPreviewProfile })
        {
            services.AddKeyedSingleton(profile, (p, _) => new FerruleServiceRunner(new(
                p.GetRequiredKeyedService<IFerruleServiceConnector>(nameof(IFerruleServiceConnector)), profile,
                p.GetRequiredService<CreationFerruleBinding>().BuildIdentity, requiredCreationEngineRevision: FerruleInspectionBackend.EngineRevision)));
            services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(p => new InspectionWorkerHostLifetime(
                p.GetRequiredKeyedService<FerruleServiceRunner>(profile).StopAsync));
        }
        services.AddSingleton<IMappingCreationCompiler>(p => new FerruleMappingCreationCompiler(new(
            p.GetRequiredKeyedService<FerruleServiceRunner>(FerruleServiceProtocol.AuthoringProfile))));
        services.AddSingleton<IMappingCreationPreview>(p => new FerruleMappingCreationPreview(new(
            p.GetRequiredKeyedService<FerruleServiceRunner>(FerruleServiceProtocol.AuthoredPreviewProfile))));
        services.AddSingleton<ICreationRuntimeAdmission, CreationHostAdmission>();
    }

    internal static void AdmitConnections(string reader, string writer, string approver)
    {
        try
        {
            var connections = new[] { Profile(reader, "braid_creation_reader"), Profile(writer, "braid_creation_writer"), Profile(approver, "braid_creation_approver") };
            if (connections.Select(c => c.Password).Distinct(StringComparer.Ordinal).Count() != 3 ||
                connections.Any(c => !string.Equals(c.Host, connections[0].Host, StringComparison.OrdinalIgnoreCase) ||
                    c.Port != connections[0].Port || c.Database != connections[0].Database)) throw ServerHostOptions.Failure();
        }
        catch (Exception e) when (SessionHostResources.Nonfatal(e)) { throw ServerHostOptions.Failure(); }
    }

    internal static void AdmitPlacement(string reader, string writer, string approver, string chatReader, string specReader)
    {
        AdmitConnections(reader, writer, approver);
        try
        {
            NpgsqlConnectionStringBuilder[] creation = [new(reader), new(writer), new(approver)];
            foreach (var dependency in new[] { Profile(chatReader, "braid_chat_reader"), Profile(specReader, "braid_spec_reader") })
                if (creation.Any(c => c.Password == dependency.Password || !string.Equals(c.Host, dependency.Host, StringComparison.OrdinalIgnoreCase) ||
                    c.Port != dependency.Port || c.Database != dependency.Database)) throw ServerHostOptions.Failure();
        }
        catch (Exception e) when (SessionHostResources.Nonfatal(e)) { throw ServerHostOptions.Failure(); }
    }

    private static NpgsqlConnectionStringBuilder Profile(string text, string role)
    {
        var c = new NpgsqlConnectionStringBuilder(text); var supplied = new DbConnectionStringBuilder { ConnectionString = text };
        if (c.Username != role || string.IsNullOrWhiteSpace(c.Host) || string.IsNullOrWhiteSpace(c.Database) || string.IsNullOrEmpty(c.Password) ||
            c.Passfile is not null || c.SslCertificate is not null || c.SslKey is not null || c.SslPassword is not null || c.NoResetOnClose || c.Multiplexing ||
            supplied.ContainsKey("Enlist") && c.Enlist) throw ServerHostOptions.Failure();
        return c;
    }
}

internal sealed class CreationHostAdmission(IServiceProvider services,
    PostgresMappingCreationStore creation, ChatHostDatabases chat) : ICreationRuntimeAdmission
{
    public async Task<bool> VerifyAsync(CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await creation.VerifyContractsAsync(deadline.Token);
            await services.GetRequiredKeyedService<PostgresSpecBundleDatabase>(SpecDatabaseProfile.Reader).VerifyContractsAsync(deadline.Token);
            await chat.Reader.VerifyContractsAsync(deadline.Token);
            await chat.Writer.VerifyContractsAsync(deadline.Token);
            await chat.Approver.VerifyContractsAsync(deadline.Token);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (SessionHostResources.Nonfatal(e)) { return false; }
    }
}
