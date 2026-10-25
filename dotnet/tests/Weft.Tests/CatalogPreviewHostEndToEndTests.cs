using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Weft.Api;
using Weft.Api.Catalog;
using Weft.Application;
using Weft.Server;
using Weft.Storage.FileSystem;

namespace Weft.Tests;

/// <summary>
/// The Development host's own preview composition over the actual database, private storage,
/// Wolverine outbox and a separate Weft.Worker process running the Ferrule worker container.
/// </summary>
[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class CatalogPreviewHostEndToEndTests(PostgresFixture fixture)
{
    private static readonly IDataProtectionProvider Keys = new EphemeralDataProtectionProvider();

    [PostgresPreviewJobsTests.PreviewWorkerProcessFact]
    public async Task SubmittedRunsCompleteThroughTheWorkerAndSurviveAWorkerRestart()
    {
        await using var test = await PreviewRunTestSupport.CreateAsync(fixture);
        using var storage = new PrivateStorageTestRoot();
        var registry = new PrivateStorageV2PhysicalRegistry();
        using (var physical = FileSystemV2PrivateObjectStore.Initialize(storage.Options, () => registry))
        using (var input = new MemoryStream(PreviewRunTestSupport.NativeBytes))
            Assert.Equal(PrivateObjectPublishResult.Created,
                await physical.PublishCreateOnlyAsync(PrivateStorageV2Data.Owner(test.Native), test.Revision, test.Native, input));

        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        var origin = new Uri($"http://127.0.0.1:{port}");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development", Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server =>
            server.Listen(IPAddress.Loopback, port, listen => listen.Protocols = HttpProtocols.Http1));
        builder.Services.AddWeftApi();
        builder.Services.AddSingleton(Keys);
        var values = new Dictionary<string, string>
        {
            ["WEFT_CATALOG_ENABLED"] = "true",
            ["WEFT_DEV_IDENTITY_ENABLED"] = "true",
            ["WEFT_DEV_PRINCIPAL_ID"] = test.Alice.PrincipalId.Value.ToString("D"),
            ["WEFT_DEV_ORGANIZATION_ID"] = test.Revision.OrganizationId.ToString("D"),
            ["WEFT_DEV_BROWSER_ORIGIN"] = origin.GetLeftPart(UriPartial.Authority),
            ["WEFT_CATALOG_CONNECTION_STRING"] = test.Case.CatalogConnectionString,
            ["WEFT_PREVIEWS_ENABLED"] = "true",
            ["WEFT_POSTGRES_PREVIEW_WRITER_CONNECTION_STRING"] = test.Case.PreviewWriterConnectionString,
            ["WEFT_POSTGRES_PREVIEW_READER_CONNECTION_STRING"] = test.Case.PreviewReaderConnectionString
        };
        CatalogHostConfiguration.Configure(builder, new IPEndPoint(IPAddress.Loopback, port), values.GetValueOrDefault);
        await using var app = builder.Build();
        app.MapWeftCatalog();
        reservation.Stop();
        await app.StartAsync();
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false, UseCookies = true, CookieContainer = new() })
        {
            BaseAddress = origin,
            Timeout = TimeSpan.FromSeconds(60)
        };
        using var context = await client.GetAsync("/api/v1/catalog/context");
        var token = (await CatalogHttpTestSupport.JsonAsync(context)).GetProperty("requestToken").GetString()!;
        var org = test.Revision.OrganizationId;

        var first = PreviewWorkerProcess.Start(test, storage);
        Guid interrupted;
        try
        {
            var delivered = await SubmitAsync(client, test, token);
            var finished = await UntilAsync(client, org, delivered, run => run.GetProperty("state").GetString() is "completed" or "canceled", first);
            Assert.True(finished.GetProperty("outcome").GetString() == "succeeded", finished.GetProperty("failure").ToString());
            var outputs = finished.GetProperty("outputs").EnumerateArray().ToArray();
            Assert.Equal(["primary-output", "named-output:0"], outputs.Select(output => output.GetProperty("boundaryId").GetString()));
            foreach (var output in outputs)
            {
                using var bytes = await client.GetAsync($"/api/v1/organizations/{org:D}/previews/{delivered:D}/outputs/{output.GetProperty("ordinal").GetInt32()}");
                Assert.Equal(HttpStatusCode.OK, bytes.StatusCode);
                var content = await bytes.Content.ReadAsByteArrayAsync();
                Assert.Equal(output.GetProperty("byteCount").GetInt32(), content.Length);
                Assert.Equal(output.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(content)));
            }

            // Submit while the worker is stopped, then hold its input read. A fast completed
            // run must not race the observer that is arranging an actual interrupted claim.
            first.Kill();
            interrupted = await SubmitAsync(client, test, token);
            await using var blocker = await test.Case.Owner.OpenConnectionAsync();
            await using (var gate = await blocker.BeginTransactionAsync())
            {
                await using (var command = new NpgsqlCommand("LOCK TABLE weft.preview_documents IN ACCESS EXCLUSIVE MODE", blocker, gate))
                    await command.ExecuteNonQueryAsync();
                first = PreviewWorkerProcess.Start(test, storage);
                await UntilClaimedAsync(blocker, gate, org, interrupted, first);
                first.Kill();
            }
        }
        finally { first.Kill(); }

        var second = PreviewWorkerProcess.Start(test, storage);
        try
        {
            var recovered = await UntilAsync(client, org, interrupted,
                run => run.GetProperty("state").GetString() is "completed" or "canceled", second);
            Assert.True(recovered.GetProperty("outcome").GetString() == "succeeded", recovered.GetProperty("failure").ToString());
            Assert.Equal(2, recovered.GetProperty("attempt").GetInt32());
            Assert.Equal(2, recovered.GetProperty("outputs").GetArrayLength());
        }
        finally { second.Kill(); }
        await app.StopAsync();
    }

    private static async Task<Guid> SubmitAsync(HttpClient client, PreviewRunTestSupport test, string token)
    {
        var submission = test.Submission();
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            target = new { kind = "all" },
            parameters = submission.Parameters.Select(parameter => new
            {
                name = parameter.Name,
                kind = parameter.Kind.ToString().ToLowerInvariant(),
                value = parameter.Value
            }),
            inputs = new[] { new { name = (string?)null, content = Convert.ToBase64String(PreviewRunTestSupport.Order) } }
                .Concat(submission.NamedInputs.Select(input => new { name = input.Name, content = Convert.ToBase64String(input.Bytes) }))
        });
        using var request = new HttpRequestMessage(HttpMethod.Post,
            CatalogRevisionReportHttpTestSupport.RevisionPath(test.Revision) + "/previews")
        {
            Content = new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation("X-XSRF-TOKEN", token);
        request.Headers.TryAddWithoutValidation("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        request.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString("D"));
        using var response = await client.SendAsync(request);
        var created = await CatalogHttpTestSupport.JsonAsync(response, HttpStatusCode.Created);
        return Guid.Parse(created.GetProperty("run").GetProperty("runId").GetString()!);
    }

    private static async Task<JsonElement> UntilAsync(HttpClient client, Guid org, Guid run, Func<JsonElement, bool> condition,
        PreviewWorkerProcess worker)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(4);
        while (true)
        {
            using var response = await client.GetAsync($"/api/v1/organizations/{org:D}/previews/{run:D}");
            var value = await CatalogHttpTestSupport.JsonAsync(response);
            if (condition(value))
                return value;
            if (worker.HasExited)
                throw new InvalidOperationException($"The preview worker exited early: {worker.Diagnostics()}");
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"The preview run did not reach the expected state: {worker.Diagnostics()}");
            await Task.Delay(100);
        }
    }

    private static async Task UntilClaimedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid org, Guid run, PreviewWorkerProcess worker)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(4);
        await using var command = new NpgsqlCommand(
            "SELECT state = 2 AND attempt = 1 FROM weft.preview_runs WHERE organization_id=$1 AND run_id=$2", connection, transaction);
        command.Parameters.AddWithValue(org);
        command.Parameters.AddWithValue(run);
        while (await command.ExecuteScalarAsync() is not true)
        {
            if (worker.HasExited)
                throw new InvalidOperationException($"The preview worker exited before claiming the held input: {worker.Diagnostics()}");
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"The preview worker did not claim the held input: {worker.Diagnostics()}");
            await Task.Delay(100);
        }
    }
}
