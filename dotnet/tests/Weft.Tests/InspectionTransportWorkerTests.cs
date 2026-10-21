using System.Net;
using System.Text.Json;
using Weft.Domain;
using Weft.Jobs;
using Weft.MappingBackends;
using Weft.MappingBackends.Workers;

namespace Weft.Tests;

/// <summary>The qualified Ferrule image behind the pinned transport, compared with the same image run directly.</summary>
public sealed class InspectionTransportWorkerTests
{
    private static readonly RevisionScope Scope = new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Guid.Parse("33333333-3333-3333-3333-333333333333"), Guid.Parse("44444444-4444-4444-4444-444444444444"));

    [FerruleWorkerIntegrationTests.WorkerFact]
    public async Task RemoteInspectionValidationAndProjectionEqualTheDirectWorker()
    {
        var image = Environment.GetEnvironmentVariable("WEFT_FERRULE_WORKER_IMAGE")!.ToLowerInvariant();
        using var workerCertificate = InspectionTransportTests.TransportCase.Certificate("CN=Inspection worker");
        using var clientCertificate = InspectionTransportTests.TransportCase.Certificate("CN=Inspection client");
        var hostedRunner = new DockerInspectionWorkerRunner(new("/usr/bin/docker", image, profile: DockerWorkerProfile.Inspection));
        var directRunner = new DockerInspectionWorkerRunner(new("/usr/bin/docker", image));
        var server = new InspectionTransportServer(new(new IPEndPoint(IPAddress.Loopback, 0), workerCertificate,
            InspectionTransportTests.TransportCase.Pin(clientCertificate)), hostedRunner);
        RemoteInspectionWorkerRunner? remote = null;
        await server.StartAsync(CancellationToken.None);
        try
        {
            remote = new RemoteInspectionWorkerRunner(new(new Uri("https://127.0.0.1:" + server.Port),
                InspectionTransportTests.TransportCase.Pin(workerCertificate), clientCertificate, image));
            var hosted = new FerruleInspectionBackend(remote);
            var direct = new FerruleInspectionBackend(directRunner);
            var artifact = Artifact("valid-basic");
            var before = artifact.CopyBytes();
            var validation = await hosted.ValidateAsync(artifact);
            Assert.Equal(MappingOperationStatus.Succeeded, validation.Status);
            Assert.True(validation.Provenance.Confirmed);
            Assert.Equal(image, validation.Provenance.BackendBuild);
            Assert.Equal(Json(await direct.ValidateAsync(artifact)), Json(validation));
            Assert.Equal(Json(await direct.InspectAsync(artifact)), Json(await hosted.InspectAsync(artifact)));
            var projection = await hosted.ProjectAsync(artifact);
            Assert.Equal(MappingOperationStatus.Succeeded, projection.Status);
            Assert.Equal(Json(await direct.ProjectAsync(artifact)), Json(projection));
            Assert.Equal(before, artifact.CopyBytes());

            var invalidArtifact = Artifact("invalid-function");
            var invalid = await hosted.ValidateAsync(invalidArtifact);
            Assert.Equal(MappingOperationStatus.InvalidMapping, invalid.Status);
            Assert.Contains(invalid.Diagnostics, diagnostic => diagnostic.Owner is GraphNodeDiagnosticOwner
            {
                FunctionId: "18446744073709551615",
                NodeId: "7"
            });
            Assert.Equal(Json(await direct.ValidateAsync(invalidArtifact)), Json(invalid));

            // Every container settled: neither runner latched an unconfirmed cleanup.
            await remote.StopAsync();
            await server.StopAsync(CancellationToken.None);
            await hostedRunner.StopAsync();
            await directRunner.StopAsync();
        }
        finally
        {
            // Settle every owner after a failed assertion too, without replacing that failure.
            if (remote is not null) await Settle(remote.StopAsync());
            await Settle(server.StopAsync(CancellationToken.None));
            await Settle(server.DisposeAsync().AsTask());
            await Settle(hostedRunner.DisposeAsync().AsTask());
            await Settle(directRunner.DisposeAsync().AsTask());
        }
    }

    private static async Task Settle(Task task)
    {
        try { await task; }
        catch (InvalidOperationException) { }
    }

    /// <summary>Every public value, including derived owner and locator types and collection items.</summary>
    private static string Json(object? value) => value switch
    {
        null => "null",
        Weft.Contracts.Projections.ProjectionDocumentV1 document =>
            JsonSerializer.Serialize(document, Weft.Contracts.Projections.ProjectionJson.CreateOptions()),
        string or bool or int or long or uint or ulong or Enum or Guid or Sha256Digest => JsonSerializer.Serialize(value.ToString()),
        System.Collections.IEnumerable items => "[" + string.Join(",", items.Cast<object?>().Select(Json)) + "]",
        _ => value.GetType().Name + "{" + string.Join(",", value.GetType().GetProperties()
            .Where(property => property.GetIndexParameters().Length == 0 && property.Name != "EqualityContract")
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .Select(property => property.Name + ":" + Json(property.GetValue(value)))) + "}"
    };

    private static NativeArtifact Artifact(string fixture) => NativeArtifact.Admit(Scope,
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "FerruleFixtures", fixture + ".json")), InspectionPolicy.MaximumNativeBytes);
}
